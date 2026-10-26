using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.RatingsCache.Services;

/// <summary>Cache persistant (cache.json) : bibliothèque, notes, budget quotidien, limites atteintes, historique des passages.</summary>
public sealed class RatingsStore
{
    private const int MaxRuns = 12;

    private readonly object _lock = new();
    private readonly string _file;
    private readonly PluginLog _log;
    private Dictionary<string, ItemRec> _items = new();
    private Dictionary<string, RatingRec> _ratings = new();
    private string _budgetDay = string.Empty;
    private int _budgetCount;
    private long _lastSync;
    private long _lastSyncMs;
    private long _lastLimitAt;
    private string _lastLimitReason = string.Empty;
    private long _resumeAt;
    private int _accountLimit;
    private int _accountUsed;
    private long _accountChecked;
    private List<RunInfo> _runs = new();

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

        public long LastSync { get; set; }

        public long LastSyncMs { get; set; }

        public long LastLimitAt { get; set; }

        public string LastLimitReason { get; set; } = string.Empty;

        public long ResumeAt { get; set; }

        public int AccountLimit { get; set; }

        public int AccountUsed { get; set; }

        public long AccountChecked { get; set; }

        public List<RunInfo> Runs { get; set; } = new();
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
            _lastSync = c.LastSync;
            _lastSyncMs = c.LastSyncMs;
            _lastLimitAt = c.LastLimitAt;
            _lastLimitReason = c.LastLimitReason ?? string.Empty;
            _resumeAt = c.ResumeAt;
            _accountLimit = c.AccountLimit;
            _accountUsed = c.AccountUsed;
            _accountChecked = c.AccountChecked;
            _runs = c.Runs ?? new();
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
                    LastSync = _lastSync,
                    LastSyncMs = _lastSyncMs,
                    LastLimitAt = _lastLimitAt,
                    LastLimitReason = _lastLimitReason,
                    ResumeAt = _resumeAt,
                    AccountLimit = _accountLimit,
                    AccountUsed = _accountUsed,
                    AccountChecked = _accountChecked,
                    Runs = _runs
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

    public bool TryGetItem(string id, out ItemRec item)
    {
        lock (_lock)
        {
            var found = _items.TryGetValue(id, out var it);
            item = it ?? new ItemRec();
            return found;
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

    // ---------------------------------------------------------------- budget (jour du quota = jour qui commence à l'heure de remise à zéro)
    private static string QuotaDay(int resetMinutes) => DateTime.UtcNow.AddMinutes(-resetMinutes).ToString("yyyy-MM-dd");

    public int BudgetUsed(int resetMinutes)
    {
        lock (_lock)
        {
            return _budgetDay == QuotaDay(resetMinutes) ? _budgetCount : 0;
        }
    }

    public void IncBudget(int resetMinutes)
    {
        lock (_lock)
        {
            var day = QuotaDay(resetMinutes);
            if (_budgetDay != day)
            {
                _budgetDay = day;
                _budgetCount = 0;
            }

            _budgetCount++;
        }
    }

    // ---------------------------------------------------------------- limite atteinte / reprise planifiée
    public long LastLimitAt => _lastLimitAt;

    public string LastLimitReason => _lastLimitReason;

    /// <summary>Instant (secondes Unix) de la reprise automatique prévue, 0 si aucune limite n'est active.</summary>
    public long ResumeAt => _resumeAt;

    public void SetLimit(string reason, long resumeAt)
    {
        lock (_lock)
        {
            _lastLimitAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _lastLimitReason = reason;
            _resumeAt = resumeAt;
        }
    }

    public void SetResumeAt(long resumeAt)
    {
        lock (_lock)
        {
            _resumeAt = resumeAt;
        }
    }

    public void ClearLimit()
    {
        lock (_lock)
        {
            _resumeAt = 0;
        }
    }

    // ---------------------------------------------------------------- quota réel du compte MDBList
    public int AccountLimit => _accountLimit;

    public int AccountUsed => _accountUsed;

    public long AccountChecked => _accountChecked;

    public void SetAccount(int limit, int used)
    {
        lock (_lock)
        {
            _accountLimit = limit;
            _accountUsed = used;
            _accountChecked = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    // ---------------------------------------------------------------- historique des passages
    public void AddRun(RunInfo run)
    {
        lock (_lock)
        {
            _runs.Add(run);
            while (_runs.Count > MaxRuns)
            {
                _runs.RemoveAt(0);
            }
        }
    }

    public List<RunInfo> Runs()
    {
        lock (_lock)
        {
            return _runs.ToList();
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

    private static bool Has(RatingData d, string key) => d.R != null && d.R.ContainsKey(key);

    /// <summary>Vue détaillée : bibliothèque, traitement, couverture par source, chevauchements.</summary>
    public StoreCounts Counts(int ttlDays, int emptyRetryDays)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string[] order = { "imdb", "rt", "aud", "tmdb", "lb" };
        lock (_lock)
        {
            var c = new StoreCounts
            {
                Movies = _items.Values.Count(i => i.Type == "movie"),
                Shows = _items.Values.Count(i => i.Type == "show"),
                Items = _items.Count,
                ItemsWithTmdb = _items.Values.Count(i => !string.IsNullOrEmpty(i.Tmdb)),
                RatingsCached = _ratings.Count
            };
            var overlap = new int[5][];
            for (var i = 0; i < 5; i++)
            {
                overlap[i] = new int[5];
            }

            var seen = new HashSet<string>();
            foreach (var it in _items.Values)
            {
                if (string.IsNullOrEmpty(it.Tmdb) || !seen.Add(it.Type + "_" + it.Tmdb))
                {
                    continue;
                }

                c.UniqueKeys++;
                if (!_ratings.TryGetValue(it.Type + "_" + it.Tmdb, out var rec))
                {
                    c.Missing++;
                    continue;
                }

                c.Processed++;
                var d = rec.Data;
                var ttl = (d.IsEmpty ? emptyRetryDays : ttlDays) * 86400L;
                if (now - rec.Fetched > ttl)
                {
                    c.Stale++;
                }

                if (d.IsEmpty)
                {
                    c.Empty++;
                    continue;
                }

                c.WithData++;
                var has = order.Select(k => Has(d, k)).ToArray();
                for (var i = 0; i < 5; i++)
                {
                    if (!has[i])
                    {
                        continue;
                    }

                    for (var j = 0; j < 5; j++)
                    {
                        if (has[j])
                        {
                            overlap[i][j]++;
                        }
                    }
                }

                if (has.All(x => x))
                {
                    c.AllFive++;
                }

                if (d.Age != null)
                {
                    c.WithAge++;
                }

                if (d.C != null && d.C.Count > 0)
                {
                    c.WithCountries++;
                }

                if (d.L != null && d.L.Count > 0)
                {
                    c.WithLanguages++;
                }

                if (!string.IsNullOrEmpty(d.RtUrl))
                {
                    c.WithRtUrl++;
                }

                if (d.Cert == 1)
                {
                    c.Certified++;
                }

                if (d.Ver == 1)
                {
                    c.Verified++;
                }
            }

            c.Overlap = overlap;
            c.PerSource = Enumerable.Range(0, 5).Select(i => overlap[i][i]).ToArray();
            return c;
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
