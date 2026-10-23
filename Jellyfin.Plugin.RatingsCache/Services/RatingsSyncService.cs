using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.RatingsCache.Services;

/// <summary>Tâche de fond : relit la bibliothèque, récupère et rafraîchit les notes MDBList dans le budget du jour.</summary>
public sealed class RatingsSyncService : BackgroundService
{
    private const int MaxRuntimeMinutes = 100;

    private readonly ILibraryManager _library;
    private readonly RatingsStore _store;
    private readonly PluginLog _log;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _run = new(1, 1);

    public RatingsSyncService(ILibraryManager library, RatingsStore store, PluginLog log)
    {
        _library = library;
        _store = store;
        _log = log;
    }

    public bool Running { get; private set; }

    public string Progress { get; private set; } = string.Empty;

    public DateTimeOffset? NextRun { get; private set; }

    public string MdbListUrl { get; } = Environment.GetEnvironmentVariable("JRC_MDBLIST_URL") ?? "https://api.mdblist.com";

    public string WikidataUrl { get; } = Environment.GetEnvironmentVariable("JRC_WIKIDATA_URL") ?? "https://query.wikidata.org/sparql";

    /// <summary>Déclenche un passage immédiat (bouton "Synchroniser maintenant").</summary>
    public void SyncNow()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Info("Service démarré (premier passage dans 20 s).");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("Passage en erreur", ex);
            }

            var wait = TimeSpan.FromMinutes(PluginConfiguration.Clamp(Cfg.Current.SyncIntervalMinutes, 5, 1440));
            NextRun = DateTimeOffset.UtcNow + wait;
            try
            {
                await _wake.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        if (!await _run.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _log.Info("Un passage est déjà en cours.");
            return;
        }

        try
        {
            Running = true;
            var sw = Stopwatch.StartNew();
            var cfg = Cfg.Current;
            RefreshItems();
            if (cfg.SyncEnabled)
            {
                await RefreshRatingsAsync(cfg, ct).ConfigureAwait(false);
            }
            else
            {
                _log.Info("Synchronisation des notes désactivée dans les réglages.");
            }

            _store.MarkSync(sw.ElapsedMilliseconds);
            _store.Save();
        }
        finally
        {
            Running = false;
            Progress = string.Empty;
            _run.Release();
        }
    }

    private static string? ProviderId(Dictionary<string, string>? ids, string name)
    {
        if (ids == null)
        {
            return null;
        }

        foreach (var kv in ids)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
            {
                return kv.Value.Trim();
            }
        }

        return null;
    }

    private void RefreshItems()
    {
        var result = new Dictionary<string, ItemRec>();
        foreach (var (kind, type) in new[] { (BaseItemKind.Movie, "movie"), (BaseItemKind.Series, "show") })
        {
            var items = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { kind },
                Recursive = true,
                IsVirtualItem = false
            });
            foreach (var it in items)
            {
                var imdb = ProviderId(it.ProviderIds, "Imdb");
                result[it.Id.ToString("N")] = new ItemRec
                {
                    Type = type,
                    Tmdb = ProviderId(it.ProviderIds, "Tmdb"),
                    Imdb = RottenTomatoesClient.IsValidImdb(imdb) ? imdb : null
                };
            }
        }

        if (result.Count == 0 && _store.Counts().Items > 0)
        {
            _log.Warn("La bibliothèque a renvoyé 0 élément : cache précédent conservé.");
            return;
        }

        _store.SetItems(result);
        _store.Save();
        var withTmdb = result.Values.Count(i => !string.IsNullOrEmpty(i.Tmdb));
        _log.Info($"Bibliothèque : {result.Count} éléments ({withTmdb} avec identifiant TMDB).");
    }

    private async Task RefreshRatingsAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.MdbListApiKey))
        {
            _log.Warn("Clé API MDBList absente : renseigne-la dans les réglages du plugin.");
            return;
        }

        var todo = _store.Pending(PluginConfiguration.Clamp(cfg.TtlDays, 1, 365), PluginConfiguration.Clamp(cfg.EmptyRetryDays, 1, 60));
        if (todo.Count == 0)
        {
            _log.Info("Notes à jour.");
            return;
        }

        var budget = PluginConfiguration.Clamp(cfg.DailyBudget, 1, 100000);
        _log.Info($"{todo.Count} notes à récupérer ou rafraîchir (budget restant aujourd'hui : {Math.Max(0, budget - _store.BudgetUsed)}).");
        var deadline = DateTime.UtcNow.AddMinutes(MaxRuntimeMinutes);
        var delay = PluginConfiguration.Clamp(cfg.CallDelayMs, 0, 10000);
        int done = 0, errors = 0;

        foreach (var p in todo)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
            {
                _log.Info("Durée maximale du passage atteinte, la suite au prochain passage.");
                break;
            }

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < _store.BlockedUntil)
            {
                _log.Warn("Circuit breaker actif (limite MDBList atteinte) : reprise plus tard.");
                break;
            }

            if (_store.BudgetUsed >= budget)
            {
                _log.Info($"Budget quotidien atteint ({budget}), reprise demain (UTC).");
                break;
            }

            Progress = $"{done}/{todo.Count}";
            _store.IncBudget();
            MdbResult res;
            try
            {
                res = await MdbListClient.FetchAsync(p.Type, p.Tmdb, cfg.MdbListApiKey.Trim(), MdbListUrl, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException || ex is System.Text.Json.JsonException)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                errors++;
                _log.Warn($"MDBList {p.Key} : {ex.Message}");
                if (errors >= 5)
                {
                    _log.Error("5 erreurs consécutives, arrêt du passage.");
                    break;
                }

                await Task.Delay(2000, ct).ConfigureAwait(false);
                continue;
            }

            if (res.Status == MdbStatus.Blocked)
            {
                _store.BlockedUntil = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + res.RetryAfterSeconds;
                _log.Warn($"MDBList : limite atteinte (429), pause de {MdbListClient.Minutes(res.RetryAfterSeconds)}.");
                break;
            }

            if (res.Status == MdbStatus.InvalidKey)
            {
                _log.Error("MDBList refuse la clé API (401/403). Vérifie la clé dans les réglages.");
                break;
            }

            errors = 0;
            var data = res.Status == MdbStatus.Ok ? res.Data : new RatingData();
            if (cfg.EnableRottenTomatoesBadges && !data.IsEmpty && RottenTomatoesClient.IsValidImdb(p.Imdb))
            {
                try
                {
                    var (url, cert, ver) = await RottenTomatoesClient.GetAsync(p.Imdb!, WikidataUrl, ct).ConfigureAwait(false);
                    data.RtUrl = url;
                    data.Cert = cert ? 1 : null;
                    data.Ver = ver ? 1 : null;
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException || ex is System.Text.Json.JsonException || ex is KeyNotFoundException || ex is InvalidOperationException)
                {
                    if (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    // Rotten Tomatoes / Wikidata indisponibles : les notes MDBList restent valables
                }
            }

            _store.SetRating(p.Key, data);
            done++;
            if (done % 25 == 0)
            {
                _store.Save();
            }

            if (delay > 0)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        _store.Save();
        _log.Info($"{done} notes enregistrées.");
    }

    /// <summary>Teste une clé MDBList avec un film connu (The Matrix, TMDB 603).</summary>
    public async Task<(bool Ok, string Message)> TestKeyAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return (false, "Aucune clé renseignée.");
        }

        try
        {
            var r = await MdbListClient.FetchAsync("movie", "603", key.Trim(), MdbListUrl, ct).ConfigureAwait(false);
            return r.Status switch
            {
                MdbStatus.Ok => (true, "Clé valide : MDBList répond correctement."),
                MdbStatus.InvalidKey => (false, "Clé refusée par MDBList (401/403)."),
                MdbStatus.Blocked => (false, "Clé valide mais limite quotidienne atteinte (429)."),
                _ => (false, "MDBList n'a pas trouvé le titre de test.")
            };
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException || ex is System.Text.Json.JsonException)
        {
            return (false, "Erreur réseau : " + ex.Message);
        }
    }
}
