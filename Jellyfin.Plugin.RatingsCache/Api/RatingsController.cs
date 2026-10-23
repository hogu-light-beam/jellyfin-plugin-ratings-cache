using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
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

/// <summary>
/// Points d'entrée du plugin.
/// client.js : public (aucun secret) ; ratings : utilisateurs connectés ; le reste : administrateurs.
/// </summary>
[ApiController]
[Route("JellyfinRatingsCache")]
public class RatingsController : ControllerBase
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly PayloadService _payload;
    private readonly RatingsStore _store;
    private readonly RatingsSyncService _sync;
    private readonly PluginLog _log;
    private readonly InjectionState _injection;

    public RatingsController(PayloadService payload, RatingsStore store, RatingsSyncService sync, PluginLog log, InjectionState injection)
    {
        _payload = payload;
        _store = store;
        _sync = sync;
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

    [HttpGet("status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetStatus()
    {
        var cfg = Cfg.Current;
        var counts = _store.Counts();
        var p = _payload.Get();
        var toFetch = string.IsNullOrWhiteSpace(cfg.MdbListApiKey)
            ? 0
            : _store.Pending(PluginConfiguration.Clamp(cfg.TtlDays, 1, 365), PluginConfiguration.Clamp(cfg.EmptyRetryDays, 1, 60)).Count;
        var blocked = _store.BlockedUntil;
        var status = new
        {
            version = Plugin.Instance?.Version?.ToString(),
            keySet = !string.IsNullOrWhiteSpace(cfg.MdbListApiKey),
            running = _sync.Running,
            progress = _sync.Progress,
            items = counts.Items,
            itemsWithTmdb = counts.ItemsWithTmdb,
            ratingsCached = counts.RatingsCached,
            ratingsWithData = counts.RatingsWithData,
            withLetterboxd = counts.WithLetterboxd,
            withImdb = counts.WithImdb,
            toFetch,
            budgetUsed = _store.BudgetUsed,
            budgetMax = cfg.DailyBudget,
            blockedUntil = blocked > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? blocked : (long?)null,
            lastSync = _store.LastSync > 0 ? _store.LastSync : (long?)null,
            lastSyncMs = _store.LastSyncMs,
            nextRun = _sync.NextRun?.ToUnixTimeSeconds(),
            payloadBytes = p.Raw.Length,
            payloadGzipBytes = p.Gzip.Length,
            payloadItems = p.Items,
            cacheFileBytes = _store.FileBytes,
            injections = _injection.Count,
            lastInjection = _injection.Last?.ToUnixTimeSeconds()
        };
        return Content(JsonSerializer.Serialize(status, Camel), "application/json; charset=utf-8");
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
        _sync.SyncNow();
        return Content("{\"ok\":true}", "application/json");
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
        _log.Warn("Cache des notes vidé depuis le tableau de bord (les notes seront récupérées à nouveau).");
        _sync.SyncNow();
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
        return Content(JsonSerializer.Serialize(new { ok, message }, Camel), "application/json; charset=utf-8");
    }
}
