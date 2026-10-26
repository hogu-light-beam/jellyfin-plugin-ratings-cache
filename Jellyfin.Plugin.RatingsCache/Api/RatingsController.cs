using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RatingsCache.Services;
using Jellyfin.Plugin.RatingsCache.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.RatingsCache.Api;

public sealed class TestKeyRequest
{
    public string? Key { get; set; }
}

public sealed class TestReviewKeysRequest
{
    public string? TmdbKey { get; set; }

    public string? TraktId { get; set; }
}

/// <summary>
/// Points d'entrée du plugin.
/// client.js : public (aucun secret) ; ratings et reviews : utilisateurs connectés ; le reste : administrateurs.
/// </summary>
[ApiController]
[Route("JellyfinRatingsCache")]
public class RatingsController : ControllerBase
{
    private static readonly JsonSerializerOptions Camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex HexId = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

    private readonly PayloadService _payload;
    private readonly RatingsStore _store;
    private readonly RatingsSyncService _sync;
    private readonly ReviewsService _reviews;
    private readonly PluginLog _log;
    private readonly InjectionState _injection;

    public RatingsController(PayloadService payload, RatingsStore store, RatingsSyncService sync, ReviewsService reviews, PluginLog log, InjectionState injection)
    {
        _payload = payload;
        _store = store;
        _sync = sync;
        _reviews = reviews;
        _log = log;
        _injection = injection;
    }

    private static string ReadResource(string name)
    {
        var asm = typeof(RatingsController).Assembly;
        var full = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        if (full == null)
        {
            return "console.error('[JRC] ressource introuvable : " + name + "');";
        }

        using var s = asm.GetManifestResourceStream(full)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private ContentResult Json(object value) => Content(JsonSerializer.Serialize(value, Camel), "application/json; charset=utf-8");

    /// <summary>Script client (injecté dans index.html).</summary>
    [HttpGet("client.js")]
    [AllowAnonymous]
    public ActionResult GetClient()
    {
        var etag = "\"" + (Plugin.Instance?.Version?.ToString() ?? "0") + "\"";
        Response.Headers["ETag"] = etag;
        Response.Headers["Cache-Control"] = "no-cache";
        if (Request.Headers["If-None-Match"].ToString() == etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Content(ReadResource("client.js"), "application/javascript; charset=utf-8");
    }

    /// <summary>Notes de toute la bibliothèque + réglages d'affichage (gzip, ETag).</summary>
    [HttpGet("ratings")]
    [Authorize]
    public ActionResult GetRatings()
    {
        var p = _payload.Get();
        var etag = "\"" + p.Version + "\"";
        Response.Headers["ETag"] = etag;
        Response.Headers["X-Data-Version"] = p.Version;
        Response.Headers["Cache-Control"] = "private, no-cache";
        if (Request.Headers["If-None-Match"].ToString().Replace("W/", string.Empty) == etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        if (Request.Headers["Accept-Encoding"].ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            Response.Headers["Content-Encoding"] = "gzip";
            return File(p.Gzip, "application/json; charset=utf-8");
        }

        return File(p.Raw, "application/json; charset=utf-8");
    }

    /// <summary>Critiques d'un film ou d'une série (TMDb, Trakt), à la demande puis en cache.</summary>
    [HttpGet("reviews/{id}")]
    [Authorize]
    public async Task<ActionResult> GetReviews(string id, CancellationToken ct)
    {
        var key = (id ?? string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        if (!HexId.IsMatch(key))
        {
            return BadRequest();
        }

        var r = await _reviews.GetAsync(key, ct).ConfigureAwait(false);
        Response.Headers["Cache-Control"] = "private, max-age=120";
        return Json(new { enabled = r.Enabled, sources = r.Sources, errors = r.Errors, items = r.Items });
    }

    [HttpGet("status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetStatus()
    {
        var cfg = Cfg.Current;
        var ttl = PluginConfiguration.Clamp(cfg.TtlDays, PluginConfiguration.TtlMin, PluginConfiguration.TtlMax);
        var empty = PluginConfiguration.Clamp(cfg.EmptyRetryDays, PluginConfiguration.EmptyRetryMin, PluginConfiguration.EmptyRetryMax);
        var c = _store.Counts(ttl, empty);
        var p = _payload.Get();
        var reset = PluginConfiguration.ResetMinutes(cfg.QuotaResetUtc);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var resume = _store.ResumeAt;
        var status = new
        {
            version = Plugin.Instance?.Version?.ToString(),
            running = _sync.Running,
            progress = _sync.Progress,
            nextRun = _sync.NextRun?.ToUnixTimeSeconds(),
            nextRunReason = _sync.NextRunReason,
            lastSync = _store.LastSync > 0 ? _store.LastSync : (long?)null,
            lastSyncMs = _store.LastSyncMs,
            library = new { movies = c.Movies, shows = c.Shows, items = c.Items, withTmdb = c.ItemsWithTmdb, withoutTmdb = c.Items - c.ItemsWithTmdb, unique = c.UniqueKeys },
            processing = new { processed = c.Processed, withData = c.WithData, empty = c.Empty, missing = c.Missing, stale = c.Stale, remaining = c.Remaining, cachedTotal = c.RatingsCached },
            coverage = new { perSource = c.PerSource, overlap = c.Overlap, allFive = c.AllFive, age = c.WithAge, countries = c.WithCountries, languages = c.WithLanguages, rtUrl = c.WithRtUrl, certified = c.Certified, verified = c.Verified },
            quota = new
            {
                keySet = !string.IsNullOrWhiteSpace(cfg.MdbListApiKey),
                useAccount = cfg.UseAccountQuota,
                accountLimit = _store.AccountLimit > 0 ? _store.AccountLimit : (int?)null,
                accountUsed = _store.AccountChecked > 0 ? _store.AccountUsed : (int?)null,
                accountChecked = _store.AccountChecked > 0 ? _store.AccountChecked : (long?)null,
                pluginUsed = _store.BudgetUsed(reset),
                pluginMax = PluginConfiguration.Clamp(cfg.DailyBudget, PluginConfiguration.BudgetMin, PluginConfiguration.BudgetMax),
                resetUtc = PluginConfiguration.NormalizeHourMinute(cfg.QuotaResetUtc),
                nextReset = RatingsSyncService.NextReset(cfg),
                lastLimitAt = _store.LastLimitAt > 0 ? _store.LastLimitAt : (long?)null,
                lastLimitReason = string.IsNullOrEmpty(_store.LastLimitReason) ? null : _store.LastLimitReason,
                resumeAt = resume > now ? resume : (long?)null
            },
            runs = _store.Runs().OrderByDescending(r => r.At).ToList(),
            files = new
            {
                payloadBytes = p.Raw.Length,
                payloadGzipBytes = p.Gzip.Length,
                payloadItems = p.Items,
                cacheFileBytes = _store.FileBytes,
                reviewsCount = _reviews.Count,
                reviewsFileBytes = _reviews.FileBytes
            },
            injection = new
            {
                count = _injection.Count,
                last = _injection.Last?.ToUnixTimeSeconds(),
                avgMs = Math.Round(_injection.AvgMs, 3),
                maxMs = Math.Round(_injection.MaxMs, 3)
            },
            reviews = new
            {
                enabled = cfg.ReviewsEnabled,
                tmdbKey = !string.IsNullOrWhiteSpace(cfg.TmdbApiKey),
                traktKey = !string.IsNullOrWhiteSpace(cfg.TraktClientId)
            }
        };
        return Json(status);
    }

    [HttpGet("logs")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetLogs([FromQuery] int lines = 200)
    {
        return Content(string.Join('\n', _log.Tail(lines)), "text/plain; charset=utf-8");
    }

    [HttpPost("sync")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult SyncNow()
    {
        _log.Info("Synchronisation demandée depuis le tableau de bord.");
        _store.ClearLimit();
        _sync.SyncNow();
        return Content("{\"ok\":true}", "application/json");
    }

    /// <summary>Relit le compteur réel du compte MDBList et reprend si le quota est revenu.</summary>
    [HttpPost("quota-check")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> QuotaCheck(CancellationToken ct)
    {
        var (ok, message) = await _sync.RefreshQuotaAsync(ct).ConfigureAwait(false);
        return Json(new { ok, message });
    }

    [HttpPost("rebuild")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult Rebuild()
    {
        _store.Save();
        var p = _payload.Get();
        _log.Info($"Fichier des notes reconstruit : {p.Items} éléments, {p.Raw.Length} octets.");
        return Content("{\"ok\":true}", "application/json");
    }

    [HttpPost("clear-cache")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult ClearCache()
    {
        _store.ClearRatings();
        _store.ClearLimit();
        _log.Warn("Cache des notes vidé depuis le tableau de bord (les notes seront récupérées à nouveau).");
        _sync.SyncNow();
        return Content("{\"ok\":true}", "application/json");
    }

    [HttpPost("clear-reviews")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult ClearReviews()
    {
        _reviews.Clear();
        _log.Warn("Cache des critiques vidé depuis le tableau de bord.");
        return Content("{\"ok\":true}", "application/json");
    }

    [HttpPost("clear-logs")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult ClearLogs()
    {
        _log.Clear();
        return Content("{\"ok\":true}", "application/json");
    }

    [HttpPost("test-key")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> TestKey([FromBody] TestKeyRequest? body, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(body?.Key) ? Cfg.Current.MdbListApiKey : body!.Key!;
        var (ok, message) = await _sync.TestKeyAsync(key, ct).ConfigureAwait(false);
        _log.Info("Test de la clé MDBList : " + message);
        return Json(new { ok, message });
    }

    [HttpPost("test-reviews-keys")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> TestReviewKeys([FromBody] TestReviewKeysRequest? body, CancellationToken ct)
    {
        var cfg = Cfg.Current;
        var tmdb = string.IsNullOrWhiteSpace(body?.TmdbKey) ? cfg.TmdbApiKey : body!.TmdbKey!;
        var trakt = string.IsNullOrWhiteSpace(body?.TraktId) ? cfg.TraktClientId : body!.TraktId!;
        var result = await _reviews.TestKeysAsync(tmdb, trakt, ct).ConfigureAwait(false);
        foreach (var kv in result)
        {
            _log.Info($"Test de la clé {kv.Key} : {kv.Value}");
        }

        return Json(result);
    }
}
