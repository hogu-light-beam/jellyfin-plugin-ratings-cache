using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.RatingsCache.Services;

public enum MdbStatus
{
    Ok,
    NotFound,
    Blocked,
    InvalidKey
}

public sealed class MdbResult
{
    public MdbStatus Status { get; set; }

    public RatingData Data { get; set; } = new();

    /// <summary>Durée de pause demandée par MDBList (secondes) en cas de limite atteinte.</summary>
    public int RetryAfterSeconds { get; set; } = 3600;
}

/// <summary>Quota réel du compte MDBList (partagé par toutes les clés et applications du compte).</summary>
public sealed class UserInfo
{
    public int Limit { get; set; }

    public int Count { get; set; }
}

/// <summary>Client MDBList (https://api.mdblist.com).</summary>
public static class MdbListClient
{
    internal static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("jellyfin-ratings-cache/1.0 (Jellyfin plugin)");
        return c;
    }

    public static async Task<MdbResult> FetchAsync(string type, string tmdbId, string apiKey, string baseUrl, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/tmdb/{type}/{Uri.EscapeDataString(tmdbId)}?apikey={Uri.EscapeDataString(apiKey)}";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new MdbResult { Status = MdbStatus.Blocked, RetryAfterSeconds = ReadRetryAfter(body) };
        }

        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
        {
            return new MdbResult { Status = MdbStatus.InvalidKey };
        }

        if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.BadRequest)
        {
            return new MdbResult { Status = MdbStatus.NotFound };
        }

        resp.EnsureSuccessStatusCode();
        return Parse(body);
    }

    /// <summary>Lit la limite quotidienne et le nombre de requêtes déjà utilisées sur le compte (GET /user).</summary>
    public static async Task<UserInfo?> GetUserAsync(string apiKey, string baseUrl, CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"{baseUrl.TrimEnd('/')}/user?apikey={Uri.EscapeDataString(apiKey)}", ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
        {
            return null;
        }

        resp.EnsureSuccessStatusCode();
        return ParseUser(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Public pour les tests.</summary>
    public static UserInfo? ParseUser(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("api_requests", out var lim) || !lim.TryGetInt32(out var limit) ||
            !root.TryGetProperty("api_requests_count", out var cnt) || !cnt.TryGetInt32(out var count))
        {
            return null;
        }

        return new UserInfo { Limit = limit, Count = count };
    }

    private static int ReadRetryAfter(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("retry_after", out var ra) &&
                ra.ValueKind == JsonValueKind.Number && ra.TryGetInt32(out var n) && n > 0)
            {
                return n;
            }
        }
        catch (JsonException)
        {
            // corps non JSON
        }

        return 3600;
    }

    /// <summary>Transforme la réponse MDBList en données compactes. Public pour les tests.</summary>
    public static MdbResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new MdbResult { Status = MdbStatus.NotFound };
        }

        var msg = string.Join(' ', new[] { "message", "error" }
            .Select(k => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : string.Empty));
        if (System.Text.RegularExpressions.Regex.IsMatch(msg ?? string.Empty, "rate.?limit|too.?many", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return new MdbResult { Status = MdbStatus.Blocked, RetryAfterSeconds = ReadRetryAfter(json) };
        }

        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(err.GetString()))
        {
            return new MdbResult { Status = MdbStatus.NotFound };
        }

        var data = new RatingData();
        if (root.TryGetProperty("ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Array)
        {
            var r = new Dictionary<string, double>();
            foreach (var x in ratings.EnumerateArray())
            {
                if (!x.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var source = x.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String
                    ? (s.GetString() ?? string.Empty).ToLowerInvariant().Replace(' ', '_')
                    : string.Empty;
                var value = v.GetDouble();
                if (source == "imdb")
                {
                    r["imdb"] = value;
                }
                else if (source == "tomatoes")
                {
                    r["rt"] = value;
                }
                else if (source.Contains("popcorn", StringComparison.Ordinal))
                {
                    r["aud"] = value;
                }
                else if (source == "tmdb")
                {
                    r["tmdb"] = value;
                }
                else if (source.Contains("letterboxd", StringComparison.Ordinal))
                {
                    r["lb"] = value;
                }
            }

            if (r.Count > 0)
            {
                data.R = r;
            }
        }

        if (root.TryGetProperty("age_rating", out var age) && age.ValueKind == JsonValueKind.Number && age.TryGetDouble(out var a) && a >= 0)
        {
            data.Age = (int)a;
        }

        var countries = Codes(First(root, "production_countries", "production_country"));
        if (countries.Count == 0 && root.TryGetProperty("country", out var cty) && cty.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(cty.GetString()))
        {
            countries.Add(cty.GetString()!);
        }

        countries = countries.GroupBy(c => c.ToUpperInvariant()).Select(g => g.First()).ToList();
        if (countries.Count > 0)
        {
            data.C = countries;
        }

        var langs = Codes(First(root, "spoken_languages", "languages", "spoken_language", "language"));
        langs = langs.GroupBy(c => c.ToLowerInvariant()).Select(g => g.First()).ToList();
        if (langs.Count > 0)
        {
            data.L = langs;
        }

        return new MdbResult { Status = MdbStatus.Ok, Data = data };
    }

    private static JsonElement? First(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var v) &&
                (v.ValueKind == JsonValueKind.String || (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)))
            {
                return v;
            }
        }

        return null;
    }

    private static List<string> Codes(JsonElement? field)
    {
        var list = new List<string>();
        if (field is not { } f)
        {
            return list;
        }

        if (f.ValueKind == JsonValueKind.String)
        {
            if (!string.IsNullOrEmpty(f.GetString()))
            {
                list.Add(f.GetString()!);
            }

            return list;
        }

        foreach (var x in f.EnumerateArray())
        {
            if (x.ValueKind == JsonValueKind.String)
            {
                if (!string.IsNullOrEmpty(x.GetString()))
                {
                    list.Add(x.GetString()!);
                }
            }
            else if (x.ValueKind == JsonValueKind.Object)
            {
                foreach (var k in new[] { "iso_3166_1", "iso_639_1", "code", "name" })
                {
                    if (x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()))
                    {
                        list.Add(v.GetString()!);
                        break;
                    }
                }
            }
        }

        return list;
    }

    /// <summary>Formate une durée en secondes de façon lisible pour le journal.</summary>
    public static string Minutes(int seconds) => (seconds / 60).ToString(CultureInfo.InvariantCulture) + " min";
}
