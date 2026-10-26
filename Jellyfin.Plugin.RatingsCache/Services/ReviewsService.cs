using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.RatingsCache.Services;

public sealed class ReviewsResult
{
    public bool Enabled { get; set; }

    public List<string> Sources { get; set; } = new();

    public List<Review> Items { get; set; } = new();

    public Dictionary<string, string> Errors { get; set; } = new();
}

/// <summary>
/// Critiques TMDb et Trakt (API officielles), récupérées à la demande à la première consultation d'un titre,
/// puis conservées en cache. Rien n'est téléchargé pour les titres que personne n'ouvre.
/// </summary>
public sealed class ReviewsService : IDisposable
{
    private const int FetchPerSource = 20;
    private const int StoredChars = 1500;
    private const int MaxEntries = 4000;
    private const int FailureRetrySeconds = 300;
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex MdLink = new(@"\[([^\]]+)\]\((?:https?://)[^)]+\)", RegexOptions.Compiled);
    private static readonly Regex MdMarks = new(@"(\*\*|__|~~|`)", RegexOptions.Compiled);
    private static readonly Regex MdHeading = new(@"(?m)^\s{0,3}#{1,6}\s*", RegexOptions.Compiled);
    private static readonly Regex Blank = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex HttpUrl = new(@"^https?://[^\s]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly RatingsStore _store;
    private readonly PluginLog _log;
    private readonly string _file;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly SemaphoreSlim _outbound = new(4, 4);
    private readonly Timer _saver;
    private Dictionary<string, ReviewRec> _cache = new();
    private bool _dirty;

    public ReviewsService(IApplicationPaths paths, RatingsStore store, PluginLog log)
    {
        _store = store;
        _log = log;
        var dir = StorePaths.Dir(paths);
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "reviews.json");
        _saver = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        Load();
    }

    public string TmdbUrl { get; } = Environment.GetEnvironmentVariable("JRC_TMDB_URL") ?? "https://api.themoviedb.org/3";

    public string TraktUrl { get; } = Environment.GetEnvironmentVariable("JRC_TRAKT_URL") ?? "https://api.trakt.tv";

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _cache.Count(kv => !kv.Value.Failed);
            }
        }
    }

    public long FileBytes => File.Exists(_file) ? new FileInfo(_file).Length : 0;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("jellyfin-ratings-cache/1.1 (Jellyfin plugin)");
        return c;
    }

    private sealed class ReviewsFile
    {
        public Dictionary<string, ReviewRec> Entries { get; set; } = new();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                _cache = JsonSerializer.Deserialize<ReviewsFile>(File.ReadAllText(_file))?.Entries ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException)
        {
            _log.Warn("Cache des critiques illisible, on repart d'un cache vide : " + ex.Message);
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            try
            {
                if (_cache.Count > MaxEntries)
                {
                    foreach (var k in _cache.OrderBy(kv => kv.Value.Fetched).Take(_cache.Count - (MaxEntries * 9 / 10)).Select(kv => kv.Key).ToList())
                    {
                        _cache.Remove(k);
                    }
                }

                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new ReviewsFile { Entries = _cache }));
                File.Move(tmp, _file, true);
                _dirty = false;
            }
            catch (IOException ex)
            {
                _log.Error("Écriture du cache des critiques impossible", ex);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache = new();
            _dirty = true;
        }

        Save();
    }

    public void Dispose()
    {
        _saver.Dispose();
        Save();
    }

    // ---------------------------------------------------------------- nettoyage du texte (jamais de HTML côté navigateur)
    public static string CleanText(string? raw, int max = StoredChars)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var t = WebUtility.HtmlDecode(raw).Replace("\r\n", "\n").Replace('\r', '\n');
        t = Tags.Replace(t, string.Empty);
        t = MdLink.Replace(t, "$1");
        t = MdMarks.Replace(t, string.Empty);
        t = MdHeading.Replace(t, string.Empty);
        t = Spaces.Replace(t, " ");
        t = Blank.Replace(t, "\n\n").Trim();
        if (t.Length > max)
        {
            var cut = t.LastIndexOf(' ', max - 1, Math.Min(80, max - 1));
            t = t.Substring(0, cut > max / 2 ? cut : max).TrimEnd() + "\u2026";
        }

        return t;
    }

    private static string? SafeUrl(string? u) => u != null && HttpUrl.IsMatch(u) && u.Length < 500 ? u : null;

    // ---------------------------------------------------------------- analyse des réponses (publiques pour les tests)
    public static List<Review> ParseTmdb(string json)
    {
        var list = new List<Review>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var r in results.EnumerateArray())
        {
            var text = CleanText(r.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null);
            if (text.Length == 0)
            {
                continue;
            }

            string author = string.Empty;
            double? rating = null;
            if (r.TryGetProperty("author_details", out var ad) && ad.ValueKind == JsonValueKind.Object)
            {
                if (ad.TryGetProperty("rating", out var rt) && rt.ValueKind == JsonValueKind.Number)
                {
                    rating = rt.GetDouble();
                }

                if (ad.TryGetProperty("username", out var un) && un.ValueKind == JsonValueKind.String)
                {
                    author = un.GetString() ?? string.Empty;
                }
            }

            if (r.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(a.GetString()))
            {
                author = a.GetString()!;
            }

            list.Add(new Review
            {
                S = "tmdb",
                A = CleanText(author, 60),
                R = rating,
                D = r.TryGetProperty("created_at", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                T = text,
                U = SafeUrl(r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null)
            });
            if (list.Count >= FetchPerSource)
            {
                break;
            }
        }

        return list;
    }

    public static List<Review> ParseTrakt(string json)
    {
        var list = new List<Review>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var text = CleanText(r.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null);
            if (text.Length == 0)
            {
                continue;
            }

            var author = string.Empty;
            if (r.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            {
                if (user.TryGetProperty("username", out var un) && un.ValueKind == JsonValueKind.String)
                {
                    author = un.GetString() ?? string.Empty;
                }
            }

            double? rating = r.TryGetProperty("user_rating", out var ur) && ur.ValueKind == JsonValueKind.Number ? ur.GetDouble() : null;
            var id = r.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.Number ? idp.GetInt64() : 0;
            var spoiler = r.TryGetProperty("spoiler", out var sp) && sp.ValueKind == JsonValueKind.True;
            list.Add(new Review
            {
                S = "trakt",
                A = CleanText(author, 60),
                R = rating,
                D = r.TryGetProperty("created_at", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                T = text,
                L = r.TryGetProperty("likes", out var lk) && lk.ValueKind == JsonValueKind.Number ? lk.GetInt32() : 0,
                U = id > 0 ? "https://trakt.tv/comments/" + id : null,
                Sp = spoiler ? true : null
            });
            if (list.Count >= FetchPerSource)
            {
                break;
            }
        }

        return list;
    }

    // ---------------------------------------------------------------- accès réseau
    private static void AddTmdbAuth(HttpRequestMessage req, string key)
    {
        if (key.StartsWith("eyJ", StringComparison.Ordinal))
        {
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        }
        else
        {
            req.RequestUri = new Uri(req.RequestUri + (req.RequestUri!.Query.Length > 0 ? "&" : "?") + "api_key=" + Uri.EscapeDataString(key));
        }
    }

    private async Task<string> GetAsync(HttpRequestMessage req, CancellationToken ct)
    {
        await _outbound.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new HttpRequestException("clé refusée (" + (int)resp.StatusCode + ")");
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return "[]";
            }

            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _outbound.Release();
        }
    }

    private async Task<List<Review>> FetchTmdbAsync(string type, string tmdb, string key, CancellationToken ct)
    {
        var kind = type == "show" ? "tv" : "movie";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{TmdbUrl.TrimEnd('/')}/{kind}/{Uri.EscapeDataString(tmdb)}/reviews?page=1");
        AddTmdbAuth(req, key);
        var body = await GetAsync(req, ct).ConfigureAwait(false);
        return body == "[]" ? new List<Review>() : ParseTmdb(body);
    }

    private async Task<List<Review>> FetchTraktAsync(string type, string imdb, string clientId, CancellationToken ct)
    {
        var kind = type == "show" ? "shows" : "movies";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{TraktUrl.TrimEnd('/')}/{kind}/{Uri.EscapeDataString(imdb)}/comments/likes?limit={FetchPerSource}");
        req.Headers.TryAddWithoutValidation("trakt-api-version", "2");
        req.Headers.TryAddWithoutValidation("trakt-api-key", clientId);
        var body = await GetAsync(req, ct).ConfigureAwait(false);
        return ParseTrakt(body);
    }

    /// <summary>Teste les clés TMDb / Trakt avec un film connu (The Matrix).</summary>
    public async Task<Dictionary<string, string>> TestKeysAsync(string tmdbKey, string traktId, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(tmdbKey))
        {
            try
            {
                var n = (await FetchTmdbAsync("movie", "603", PluginConfiguration.CleanApiKey(tmdbKey, PluginConfiguration.TokenMaxLength), ct).ConfigureAwait(false)).Count;
                result["tmdb"] = $"OK : clé valide ({n} critiques trouvées pour le titre de test).";
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException)
            {
                result["tmdb"] = "Échec : " + ex.Message;
            }
        }

        if (!string.IsNullOrWhiteSpace(traktId))
        {
            try
            {
                var n = (await FetchTraktAsync("movie", "tt0133093", PluginConfiguration.CleanApiKey(traktId), ct).ConfigureAwait(false)).Count;
                result["trakt"] = $"OK : Client ID valide ({n} commentaires trouvés pour le titre de test).";
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException)
            {
                result["trakt"] = "Échec : " + ex.Message;
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- lecture avec cache
    private async Task<(List<Review> Items, string? Error)> SourceAsync(string source, string cacheKey, bool wantFresh, Func<Task<List<Review>>> fetch, PluginConfiguration cfg, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ttl = PluginConfiguration.Clamp(cfg.ReviewsCacheDays, PluginConfiguration.ReviewsCacheMin, PluginConfiguration.ReviewsCacheMax) * 86400L;
        var key = source + ":" + cacheKey;
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var rec))
                {
                    var limit = rec.Failed ? FailureRetrySeconds : (rec.Items.Count == 0 ? Math.Min(ttl, 86400L) : ttl);
                    if (now - rec.Fetched < limit)
                    {
                        return (rec.Items, rec.Failed ? "indisponible (nouvel essai dans quelques minutes)" : null);
                    }
                }
            }

            try
            {
                var items = await fetch().ConfigureAwait(false);
                lock (_lock)
                {
                    _cache[key] = new ReviewRec { Items = items, Fetched = now };
                    _dirty = true;
                }

                _saver.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
                return (items, null);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                _log.Warn($"Critiques {source} {cacheKey} : {ex.Message}");
                lock (_lock)
                {
                    _cache[key] = new ReviewRec { Failed = true, Fetched = now };
                    _dirty = true;
                }

                return (new List<Review>(), ex.Message);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ReviewsResult> GetAsync(string jellyfinId, CancellationToken ct)
    {
        var cfg = Cfg.Current;
        var tmdbKey = PluginConfiguration.CleanApiKey(cfg.TmdbApiKey, PluginConfiguration.TokenMaxLength);
        var traktId = PluginConfiguration.CleanApiKey(cfg.TraktClientId);
        var useTmdb = cfg.ReviewsUseTmdb && tmdbKey.Length > 0;
        var useTrakt = cfg.ReviewsUseTrakt && traktId.Length > 0;
        var result = new ReviewsResult { Enabled = cfg.ReviewsEnabled && (useTmdb || useTrakt) };
        if (!result.Enabled || !_store.TryGetItem(jellyfinId, out var item))
        {
            return result;
        }

        var tasks = new List<(string Source, Task<(List<Review> Items, string? Error)> Task)>();
        if (useTmdb && !string.IsNullOrEmpty(item.Tmdb))
        {
            tasks.Add(("tmdb", SourceAsync("tmdb", item.Type + "_" + item.Tmdb, false, () => FetchTmdbAsync(item.Type, item.Tmdb!, tmdbKey, ct), cfg, ct)));
        }

        if (useTrakt && !string.IsNullOrEmpty(item.Imdb))
        {
            tasks.Add(("trakt", SourceAsync("trakt", item.Type + "_" + item.Imdb, false, () => FetchTraktAsync(item.Type, item.Imdb!, traktId, ct), cfg, ct)));
        }

        foreach (var (source, task) in tasks)
        {
            var (items, error) = await task.ConfigureAwait(false);
            result.Sources.Add(source);
            result.Items.AddRange(items);
            if (error != null)
            {
                result.Errors[source] = error;
            }
        }

        return result;
    }
}
