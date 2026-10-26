using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.RatingsCache.Services;

/// <summary>Accès à la configuration courante du plugin.</summary>
public static class Cfg
{
    private static readonly PluginConfiguration Default = new();

    public static PluginConfiguration Current => Plugin.Instance?.Configuration ?? Default;
}

public sealed class Payload
{
    public string Version { get; init; } = string.Empty;

    public byte[] Raw { get; init; } = Array.Empty<byte>();

    public byte[] Gzip { get; init; } = Array.Empty<byte>();

    public int Items { get; init; }
}

/// <summary>Construit le JSON servi aux navigateurs : réglages d'affichage + notes de toute la bibliothèque.</summary>
public sealed class PayloadService
{
    private static readonly JsonSerializerOptions CamelOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ItemOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RatingsStore _store;
    private readonly object _lock = new();
    private Payload? _cache;
    private int _cacheRevision = -1;
    private string _cacheCfg = string.Empty;

    public PayloadService(RatingsStore store)
    {
        _store = store;
    }

    public static string BuildCfgJson(PluginConfiguration c)
    {
        var detail = new
        {
            enabled = c.DetailEnabled,
            show = new { imdb = c.ShowImdb, rt = c.ShowRottenTomatoes, aud = c.ShowAudience, tmdb = c.ShowTmdb, lb = c.ShowLetterboxd },
            order = PluginConfiguration.ParseSources(c.DetailOrder),
            cert = c.ShowCertification,
            countries = c.ShowCountries,
            languages = c.ShowLanguages,
            links = c.ReorderExternalLinks,
            seasonEpisodes = c.ShowSeasonEpisodeRatings,
            episodePage = c.ShowEpisodePageRating,
            logoSize = PluginConfiguration.Clamp(c.DetailLogoSize, PluginConfiguration.DetailLogoMin, PluginConfiguration.DetailLogoMax),
            position = PluginConfiguration.Pick(c.DetailPosition, "afterOfficialRating", "afterOfficialRating", "afterRuntime", "end")
        };
        var cards = new
        {
            enabled = c.CardsEnabled,
            movies = c.CardsMovies,
            series = c.CardsSeries,
            episodes = c.CardsEpisodes,
            line = PluginConfiguration.Pick(c.CardsLine, "secondary", "secondary", "first"),
            sources = PluginConfiguration.ParseSources(c.CardsSources),
            max = PluginConfiguration.Clamp(c.CardsMaxRatings, PluginConfiguration.CardsMaxRatingsMin, PluginConfiguration.CardsMaxRatingsMax),
            sep = PluginConfiguration.SeparatorChar(c.CardsSeparator),
            logo = c.CardsShowLogo,
            logoSize = PluginConfiguration.Clamp(c.CardsLogoSize, PluginConfiguration.CardsLogoMin, PluginConfiguration.CardsLogoMax),
            position = PluginConfiguration.Pick(c.CardsPosition, "before", "before", "after")
        };
        var mediaBar = new
        {
            enabled = c.MediaBarEnabled,
            sources = PluginConfiguration.ParseSources(c.MediaBarSources),
            max = PluginConfiguration.Clamp(c.MediaBarMaxRatings, PluginConfiguration.MediaBarMaxRatingsMin, PluginConfiguration.MediaBarMaxRatingsMax),
            logo = c.MediaBarShowLogo,
            logoSize = PluginConfiguration.Clamp(c.MediaBarLogoSize, PluginConfiguration.MediaBarLogoMin, PluginConfiguration.MediaBarLogoMax),
            position = PluginConfiguration.Pick(c.MediaBarPosition, "start", "start", "afterDate", "afterAge", "end"),
            hideStar = c.MediaBarHideStar
        };
        var useTmdb = c.ReviewsUseTmdb && !string.IsNullOrWhiteSpace(c.TmdbApiKey);
        var useTrakt = c.ReviewsUseTrakt && !string.IsNullOrWhiteSpace(c.TraktClientId);
        var reviews = new
        {
            enabled = c.ReviewsEnabled && (useTmdb || useTrakt),
            perSource = PluginConfiguration.Clamp(c.ReviewsPerSource, PluginConfiguration.ReviewsPerSourceMin, PluginConfiguration.ReviewsPerSourceMax),
            totalMax = PluginConfiguration.Clamp(c.ReviewsTotalMax, PluginConfiguration.ReviewsTotalLow, PluginConfiguration.ReviewsTotalHigh),
            grouping = PluginConfiguration.Pick(c.ReviewsGrouping, "mixed", "mixed", "bySource"),
            sort = PluginConfiguration.Pick(c.ReviewsSort, "likes", "likes", "recent", "rating"),
            maxChars = PluginConfiguration.Clamp(c.ReviewsMaxChars, PluginConfiguration.ReviewsCharsMin, PluginConfiguration.ReviewsCharsMax),
            author = c.ReviewsShowAuthor,
            date = c.ReviewsShowDate,
            rating = c.ReviewsShowRating,
            source = c.ReviewsShowSource,
            likes = c.ReviewsShowLikes,
            hideSpoilers = c.ReviewsHideSpoilers,
            collapsed = c.ReviewsCollapsed,
            position = PluginConfiguration.Pick(c.ReviewsPosition, "top", "top", "bottom")
        };
        return JsonSerializer.Serialize(new { detail, cards, mediaBar, reviews, hideStar = c.HideDefaultStar }, CamelOptions);
    }

    public Payload Get()
    {
        var cfgJson = BuildCfgJson(Cfg.Current);
        lock (_lock)
        {
            var (items, ratings, rev) = _store.Snapshot();
            if (_cache != null && _cacheRevision == rev && _cacheCfg == cfgJson)
            {
                return _cache;
            }

            var m = new SortedDictionary<string, PayloadItem>(StringComparer.Ordinal);
            foreach (var kv in items)
            {
                var it = kv.Value;
                var p = new PayloadItem { T = it.Type, Tm = it.Tmdb, Im = it.Imdb };
                if (!string.IsNullOrEmpty(it.Tmdb) && ratings.TryGetValue(it.Type + "_" + it.Tmdb, out var rr))
                {
                    var d = rr.Data;
                    p.R = d.R;
                    p.Age = d.Age;
                    p.C = d.C;
                    p.L = d.L;
                    p.RtUrl = d.RtUrl;
                    p.Cert = d.Cert;
                    p.Ver = d.Ver;
                }

                m[kv.Key] = p;
            }

            var mJson = JsonSerializer.Serialize(m, ItemOptions);
            var ver = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(cfgJson + mJson))).Substring(0, 12).ToLowerInvariant();
            var raw = Encoding.UTF8.GetBytes("{\"v\":\"" + ver + "\",\"cfg\":" + cfgJson + ",\"m\":" + mJson + "}");
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
            {
                gz.Write(raw, 0, raw.Length);
            }

            _cache = new Payload { Version = ver, Raw = raw, Gzip = ms.ToArray(), Items = m.Count };
            _cacheRevision = rev;
            _cacheCfg = cfgJson;
            return _cache;
        }
    }
}
