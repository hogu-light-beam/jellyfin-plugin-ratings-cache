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

/// <summary>
/// Tâche de fond : relit la bibliothèque, récupère et rafraîchit les notes MDBList.
/// Quand la limite quotidienne est atteinte, la reprise est planifiée à l'heure exacte de remise à zéro du quota.
/// </summary>
public sealed class RatingsSyncService : BackgroundService
{
    private const int MaxRuntimeMinutes = 100;
    private const int WindowRequests = 800;               // MDBList : 1000 lectures / 5 minutes, on garde une marge
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _library;
    private readonly RatingsStore _store;
    private readonly PluginLog _log;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly Timer _debounce;
    private readonly int _debounceSeconds = EnvInt("JRC_DEBOUNCE_SECONDS", 90);
    private readonly int _startDelaySeconds = EnvInt("JRC_START_DELAY_SECONDS", 20);
    private readonly Queue<DateTime> _recent = new();
    private long _loggedLimitFor;

    public RatingsSyncService(ILibraryManager library, RatingsStore store, PluginLog log)
    {
        _library = library;
        _store = store;
        _log = log;
        _debounce = new Timer(_ => SyncNow(), null, Timeout.Infinite, Timeout.Infinite);
        _library.ItemAdded += OnLibraryChanged;
        _library.ItemUpdated += OnLibraryChanged;
    }

    /// <summary>Film ou série ajouté / mis à jour : nouveau passage peu après la dernière modification (évite les rafales d'un scan).</summary>
    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        var kind = e.Item?.GetType().Name;
        if (kind == "Movie" || kind == "Series")
        {
            _debounce.Change(TimeSpan.FromSeconds(_debounceSeconds), Timeout.InfiniteTimeSpan);
        }
    }

    public override void Dispose()
    {
        _library.ItemAdded -= OnLibraryChanged;
        _library.ItemUpdated -= OnLibraryChanged;
        _debounce.Dispose();
        base.Dispose();
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v >= 0 ? v : fallback;

    public bool Running { get; private set; }

    public string Progress { get; private set; } = string.Empty;

    public DateTimeOffset? NextRun { get; private set; }

    /// <summary>interval (fréquence normale) | limit (reprise planifiée après une limite).</summary>
    public string NextRunReason { get; private set; } = "interval";

    public string MdbListUrl { get; } = Environment.GetEnvironmentVariable("JRC_MDBLIST_URL") ?? "https://api.mdblist.com";

    public string WikidataUrl { get; } = Environment.GetEnvironmentVariable("JRC_WIKIDATA_URL") ?? "https://query.wikidata.org/sparql";

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string Local(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Prochaine remise à zéro du quota (secondes Unix, +20 s de marge).</summary>
    public static long NextReset(PluginConfiguration cfg)
    {
        var now = DateTime.UtcNow;
        var t = now.Date.AddMinutes(PluginConfiguration.ResetMinutes(cfg.QuotaResetUtc));
        if (t <= now)
        {
            t = t.AddDays(1);
        }

        return new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeSeconds() + 20;
    }

    private static int ResetMinutes(PluginConfiguration cfg) => PluginConfiguration.ResetMinutes(cfg.QuotaResetUtc);

    /// <summary>Déclenche un passage immédiat (bouton "Synchroniser maintenant", nouveau film...).</summary>
    public void SyncNow()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Info($"Service démarré (premier passage dans {_startDelaySeconds} s).");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_startDelaySeconds), stoppingToken).ConfigureAwait(false);
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

            // Prochain passage : à la seconde près après une limite, sinon à la fréquence réglée
            var now = UnixNow();
            var resume = _store.ResumeAt;
            TimeSpan wait;
            if (resume > now)
            {
                wait = TimeSpan.FromSeconds(resume - now + 1);
                NextRunReason = "limit";
            }
            else
            {
                wait = TimeSpan.FromMinutes(PluginConfiguration.Clamp(Cfg.Current.SyncIntervalMinutes, PluginConfiguration.IntervalMin, PluginConfiguration.IntervalMax));
                NextRunReason = "interval";
            }

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
            var run = new RunInfo { At = UnixNow(), Stop = "nothing" };
            RefreshItems();
            if (cfg.SyncEnabled)
            {
                await RefreshRatingsAsync(cfg, run, ct).ConfigureAwait(false);
            }
            else
            {
                run.Stop = "disabled";
                _log.Info("Synchronisation des notes désactivée dans les réglages.");
            }

            run.Ms = sw.ElapsedMilliseconds;
            if (run.Stop != "waiting")
            {
                _store.AddRun(run);
            }

            _store.MarkSync(run.Ms);
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

        var cfg = Cfg.Current;
        if (result.Count == 0 && _store.Counts(cfg.TtlDays, cfg.EmptyRetryDays).Items > 0)
        {
            _log.Warn("La bibliothèque a renvoyé 0 élément : cache précédent conservé.");
            return;
        }

        _store.SetItems(result);
        _store.Save();
        var withTmdb = result.Values.Count(i => !string.IsNullOrEmpty(i.Tmdb));
        _log.Info($"Bibliothèque : {result.Count} éléments ({withTmdb} avec identifiant TMDB).");
    }

    /// <summary>Lit le compteur réel du compte MDBList. Null si la lecture échoue (on se rabat alors sur le compteur local).</summary>
    private async Task<UserInfo?> CheckAccountAsync(string key, CancellationToken ct)
    {
        try
        {
            var u = await MdbListClient.GetUserAsync(key, MdbListUrl, ct).ConfigureAwait(false);
            if (u != null)
            {
                _store.SetAccount(u.Limit, u.Count);
            }

            return u;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException || ex is System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            _log.Warn("Lecture du quota du compte MDBList impossible : " + ex.Message);
            return null;
        }
    }

    /// <summary>Bouton « Vérifier le quota maintenant » : relit le compteur du compte et reprend si le quota est remis à zéro.</summary>
    public async Task<(bool Ok, string Message)> RefreshQuotaAsync(CancellationToken ct)
    {
        var cfg = Cfg.Current;
        var key = PluginConfiguration.CleanApiKey(cfg.MdbListApiKey);
        if (key.Length == 0)
        {
            return (false, "Aucune clé MDBList renseignée.");
        }

        var u = await CheckAccountAsync(key, ct).ConfigureAwait(false);
        if (u == null)
        {
            return (false, "MDBList n'a pas renvoyé le quota du compte.");
        }

        var cap = PluginConfiguration.Clamp(cfg.DailyBudget, PluginConfiguration.BudgetMin, PluginConfiguration.BudgetMax);
        _log.Info($"Quota du compte MDBList : {u.Count} / {u.Limit} requêtes utilisées.");
        if (_store.ResumeAt > 0 && u.Count < cap)
        {
            _log.Info("Le quota est disponible : reprise immédiate.");
            _store.ClearLimit();
            SyncNow();
        }

        return (true, $"{u.Count} / {u.Limit} requêtes utilisées sur le compte MDBList.");
    }

    private void SetLimit(string reason, long resumeAt, RunInfo run, string why)
    {
        _store.SetLimit(reason, resumeAt);
        run.Stop = reason;
        if (_loggedLimitFor != resumeAt)
        {
            _loggedLimitFor = resumeAt;
            _log.Warn($"Limite atteinte ({why}). Reprise automatique prévue le {Local(resumeAt)} (heure du serveur).");
        }
    }

    private async Task WindowGuardAsync(CancellationToken ct)
    {
        while (true)
        {
            var now = DateTime.UtcNow;
            while (_recent.Count > 0 && now - _recent.Peek() > Window)
            {
                _recent.Dequeue();
            }

            if (_recent.Count < WindowRequests)
            {
                _recent.Enqueue(now);
                return;
            }

            await Task.Delay(_recent.Peek() + Window - now + TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }

    private async Task RefreshRatingsAsync(PluginConfiguration cfg, RunInfo run, CancellationToken ct)
    {
        var key = PluginConfiguration.CleanApiKey(cfg.MdbListApiKey);
        if (key.Length == 0)
        {
            run.Stop = "nokey";
            _log.Warn("Clé API MDBList absente : renseigne-la dans les réglages du plugin.");
            return;
        }

        var ttl = PluginConfiguration.Clamp(cfg.TtlDays, PluginConfiguration.TtlMin, PluginConfiguration.TtlMax);
        var emptyRetry = PluginConfiguration.Clamp(cfg.EmptyRetryDays, PluginConfiguration.EmptyRetryMin, PluginConfiguration.EmptyRetryMax);
        var todo = _store.Pending(ttl, emptyRetry);
        if (todo.Count == 0)
        {
            _store.ClearLimit();
            run.Stop = "nothing";
            _log.Info("Notes à jour.");
            return;
        }

        var now = UnixNow();
        var cap = PluginConfiguration.Clamp(cfg.DailyBudget, PluginConfiguration.BudgetMin, PluginConfiguration.BudgetMax);
        var reset = ResetMinutes(cfg);

        // Budget relevé dans les réglages (ou compteur local redescendu) : la limite en attente n'a plus lieu d'être
        if (_store.ResumeAt > now && _store.LastLimitReason == "budget" && _store.BudgetUsed(reset) < cap)
        {
            _store.ClearLimit();
        }

        if (_store.ResumeAt > now)
        {
            run.Stop = "waiting";
            if (_loggedLimitFor != _store.ResumeAt)
            {
                _loggedLimitFor = _store.ResumeAt;
                _log.Info($"{todo.Count} notes en attente : reprise automatique prévue le {Local(_store.ResumeAt)}.");
            }

            return;
        }

        int? accountUsed = null;
        if (cfg.UseAccountQuota)
        {
            var u = await CheckAccountAsync(key, ct).ConfigureAwait(false);
            if (u != null)
            {
                accountUsed = u.Count;
            }
        }

        run.Remaining = todo.Count;
        if (accountUsed >= cap)
        {
            // Le compte MDBList (toutes applications confondues) a déjà atteint le budget : on attend la remise à zéro
            var resume = _store.ResumeAt > 0 && _store.ResumeAt <= now ? now + 600 : NextReset(cfg);
            SetLimit("account", resume, run, $"le compte MDBList a déjà utilisé {accountUsed} requêtes sur le budget de {cap}");
            return;
        }

        if (accountUsed == null && _store.BudgetUsed(reset) >= cap)
        {
            SetLimit("budget", NextReset(cfg), run, $"budget de {cap} requêtes atteint");
            return;
        }

        _store.ClearLimit();
        _loggedLimitFor = 0;
        _log.Info($"{todo.Count} notes à récupérer ou rafraîchir (compteur du compte : {(accountUsed?.ToString() ?? "inconnu")} / {cap}, plugin : {_store.BudgetUsed(reset)}).");
        var deadline = DateTime.UtcNow.AddMinutes(MaxRuntimeMinutes);
        var delay = PluginConfiguration.Clamp(cfg.CallDelayMs, PluginConfiguration.DelayMin, PluginConfiguration.DelayMax);
        var sinceCheck = 0;
        int done = 0, errors = 0;
        run.Stop = "done";

        foreach (var p in todo)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
            {
                run.Stop = "time";
                _log.Info("Durée maximale du passage atteinte, la suite au prochain passage.");
                break;
            }

            var used = Math.Max(_store.BudgetUsed(reset), accountUsed.HasValue ? accountUsed.Value + sinceCheck : 0);
            if (used >= cap)
            {
                SetLimit(accountUsed.HasValue ? "account" : "budget", NextReset(cfg), run, $"budget de {cap} requêtes atteint");
                break;
            }

            Progress = $"{done}/{todo.Count}";
            await WindowGuardAsync(ct).ConfigureAwait(false);
            _store.IncBudget(reset);
            sinceCheck++;
            MdbResult res;
            try
            {
                res = await MdbListClient.FetchAsync(p.Type, p.Tmdb, key, MdbListUrl, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException || ex is System.Text.Json.JsonException)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                errors++;
                run.Errors++;
                _log.Warn($"MDBList {p.Key} : {ex.Message}");
                if (errors >= 5)
                {
                    run.Stop = "error";
                    _log.Error("5 erreurs consécutives, arrêt du passage.");
                    break;
                }

                await Task.Delay(2000, ct).ConfigureAwait(false);
                continue;
            }

            if (res.Status == MdbStatus.Blocked)
            {
                var resume = res.RetryAfterSeconds < 3600 ? UnixNow() + res.RetryAfterSeconds + 5 : NextReset(cfg);
                SetLimit("429", resume, run, "MDBList a répondu 429");
                break;
            }

            if (res.Status == MdbStatus.InvalidKey)
            {
                run.Stop = "key";
                _log.Error("MDBList refuse la clé API (401/403). Vérifie la clé dans les réglages.");
                break;
            }

            errors = 0;
            var data = res.Status == MdbStatus.Ok ? res.Data : new RatingData();
            if (data.IsEmpty)
            {
                run.Empty++;
            }

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
            run.Fetched = done;
            if (done % 25 == 0)
            {
                _store.Save();
            }

            if (delay > 0)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        run.Fetched = done;
        run.Remaining = _store.Pending(ttl, emptyRetry).Count;
        if (run.Remaining == 0)
        {
            _store.ClearLimit();
        }

        _store.Save();
        _log.Info($"{done} notes enregistrées, {run.Remaining} restantes.");
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
            var r = await MdbListClient.FetchAsync("movie", "603", PluginConfiguration.CleanApiKey(key), MdbListUrl, ct).ConfigureAwait(false);
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
