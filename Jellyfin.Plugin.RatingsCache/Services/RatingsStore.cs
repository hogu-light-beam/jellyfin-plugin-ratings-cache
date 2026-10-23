using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.RatingsCache.Services;

/// <summary>Cache persistant (cache.json) : bibliothèque, notes, budget MDBList quotidien, circuit breaker.</summary>
public sealed class RatingsStore
{
    private readonly object _lock = new();
    private readonly string _file;
    private readonly PluginLog _log;
    private Dictionary<string, ItemRec> _items = new();
    private Dictionary<string, RatingRec> _ratings = new();
    private string _budgetDay = string.Empty;
    private int _budgetCount;
    private long _blockedUntil;
    private long _lastSync;
    private long _lastSyncMs;

    public RatingsStore(IApplicationPaths paths, PluginLog log)
    {
        _log = log;
        var dir = StorePaths.Dir(paths);
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "cache.json");
        Load();
    }

    /// <summary>Incrémenté à chaque modification : invalide le fichier servi au client.</summary>
    public int Revision { get; private set; }

    public string FilePath => _file;

    public long FileBytes => File.Exists(_file) ? new FileInfo(_file).Length : 0;

    private sealed class CacheFile
    {
        public Dictionary<string, ItemRec> Items { get; set; } = new();

        public Dictionary<string, RatingRec> Ratings { get; set; } = new();

        public string BudgetDay { get; set; } = string.Empty;

        public int BudgetCount { get; set; }

        public long BlockedUntil { get; set; }

        public long LastSync { get; set; }

        public long LastSyncMs { get; set; }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file))
            {
                return;
            }

            var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_file));
            if (c == null)
            {
                return;
            }

            _items = c.Items ?? new();
            _ratings = c.Ratings ?? new();
            _budgetDay = c.BudgetDay;
            _budgetCount = c.BudgetCount;
            _blockedUntil = c.BlockedUntil;
            _lastSync = c.LastSync;
            _lastSyncMs = c.LastSyncMs;
            Revision++;
            _log.Info($"Cache chargé : {_items.Count} éléments, {_ratings.Count} notes en cache.");
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException)
        {
            _log.Error("Cache illisible, on repart d'un cache vide", ex);
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var c = new CacheFile
                {
                    Items = _items,
                    Ratings = _ratings,
                    BudgetDay = _budgetDay,
                    BudgetCount = _budgetCount,
                    BlockedUntil = _blockedUntil,
                    LastSync = _lastSync,
                    LastSyncMs = _lastSyncMs
                };
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(c));
                File.Move(tmp, _file, true);
            }
            catch (IOException ex)
            {
                _log.Error("Écriture du cache impossible", ex);
            }
        }
    }

    public void SetItems(Dictionary<string, ItemRec> items)
    {
        lock (_lock)
        {
            _items = items;
            Revision++;
        }
    }

    public void SetRating(string key, RatingData data)
    {
        lock (_lock)
        {
            _ratings[key] = new RatingRec { Data = data, Fetched = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            Revision++;
        }
    }

    public void ClearRatings()
    {
        lock (_lock)
        {
            _ratings = new();
            Revision++;
        }

        Save();
    }

    public void MarkSync(long ms)
    {
        lock (_lock)
        {
            _lastSync = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _lastSyncMs = ms;
        }
    }

    public long LastSync => _lastSync;

    public long LastSyncMs => _lastSyncMs;

    // ---------------------------------------------------------------- budget & circuit breaker
    private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd");

    public int BudgetUsed
    {
        get
        {
            lock (_lock)
            {
                return _budgetDay == Today() ? _budgetCount : 0;
            }
        }
    }

    public void IncBudget()
    {
        lock (_lock)
        {
            if (_budgetDay != Today())
            {
                _budgetDay = Today();
                _budgetCount = 0;
            }

            _budgetCount++;
        }
    }

    public long BlockedUntil
    {
        get => _blockedUntil;
        set
        {
            lock (_lock)
            {
                _blockedUntil = value;
            }
        }
    }

    // ---------------------------------------------------------------- lecture
    /// <summary>Copie cohérente pour construire le fichier client.</summary>
    public (Dictionary<string, ItemRec> Items, Dictionary<string, RatingRec> Ratings, int Revision) Snapshot()
    {
        lock (_lock)
        {
            return (new Dictionary<string, ItemRec>(_items), new Dictionary<string, RatingRec>(_ratings), Revision);
        }
    }

    public StoreCounts Counts()
    {
        lock (_lock)
        {
            var withData = _ratings.Values.Where(r => !r.Data.IsEmpty).ToList();
            return new StoreCounts
            {
                Items = _items.Count,
                ItemsWithTmdb = _items.Values.Count(i => !string.IsNullOrEmpty(i.Tmdb)),
                RatingsCached = _ratings.Count,
                RatingsWithData = withData.Count,
                WithLetterboxd = withData.Count(r => r.Data.R != null && r.Data.R.ContainsKey("lb")),
                WithImdb = withData.Count(r => r.Data.R != null && r.Data.R.ContainsKey("imdb"))
            };
        }
    }

    /// <summary>Titres à (re)récupérer : d'abord les manquants, puis les plus anciens périmés.</summary>
    public List<PendingItem> Pending(int ttlDays, int emptyRetryDays)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            var seen = new HashSet<string>();
            var missing = new List<PendingItem>();
            var stale = new List<(long Fetched, PendingItem Item)>();
            foreach (var it in _items.Values)
            {
                if (string.IsNullOrEmpty(it.Tmdb))
                {
                    continue;
                }

                var key = it.Type + "_" + it.Tmdb;
                if (!seen.Add(key))
                {
                    continue;
                }

                var p = new PendingItem { Key = key, Type = it.Type, Tmdb = it.Tmdb, Imdb = it.Imdb };
                if (!_ratings.TryGetValue(key, out var rec))
                {
                    missing.Add(p);
                    continue;
                }

                var ttl = (rec.Data.IsEmpty ? emptyRetryDays : ttlDays) * 86400L;
                if (now - rec.Fetched > ttl)
                {
                    stale.Add((rec.Fetched, p));
                }
            }

            return missing.Concat(stale.OrderBy(s => s.Fetched).Select(s => s.Item)).ToList();
        }
    }
}
