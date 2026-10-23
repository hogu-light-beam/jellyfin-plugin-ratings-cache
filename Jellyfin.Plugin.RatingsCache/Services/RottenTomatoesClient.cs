using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.RatingsCache.Services;

/// <summary>Lien Rotten Tomatoes (via Wikidata) et badges Certified Fresh / Verified Hot (page RT).</summary>
public static class RottenTomatoesClient
{
    private static readonly Regex ImdbId = new(@"^tt\d{5,12}$", RegexOptions.Compiled);
    private static readonly Regex Scorecard = new(@"<script\s+id=""media-scorecard-json""[^>]*>([\s\S]*?)</script>", RegexOptions.Compiled);

    public static bool IsValidImdb(string? id) => id != null && ImdbId.IsMatch(id);

    public static async Task<(string? Url, bool Cert, bool Ver)> GetAsync(string imdbId, string wikidataUrl, CancellationToken ct)
    {
        if (!IsValidImdb(imdbId))
        {
            return (null, false, false);
        }

        var sparql = $"SELECT ?rtid WHERE {{ ?item wdt:P345 \"{imdbId}\" . ?item wdt:P1258 ?rtid . }} LIMIT 1";
        var json = await MdbListClient.Http
            .GetStringAsync($"{wikidataUrl}?format=json&query={Uri.EscapeDataString(sparql)}", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var bindings = doc.RootElement.GetProperty("results").GetProperty("bindings");
        if (bindings.GetArrayLength() == 0)
        {
            return (null, false, false);
        }

        var rid = bindings[0].GetProperty("rtid").GetProperty("value").GetString();
        if (string.IsNullOrEmpty(rid))
        {
            return (null, false, false);
        }

        var url = rid.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? rid : "https://www.rottentomatoes.com/" + rid;
        bool cert = false, ver = false;
        try
        {
            var html = await MdbListClient.Http.GetStringAsync(url, ct).ConfigureAwait(false);
            var m = Scorecard.Match(html);
            if (m.Success)
            {
                var raw = m.Groups[1].Value;
                (cert, ver) = ParseScorecard(raw);
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException || ex is JsonException)
        {
            // la page RT peut bloquer : on garde au moins le lien
        }

        return (url, cert, ver);
    }

    /// <summary>Lit le JSON "media-scorecard-json" de la page RT. Public pour les tests.</summary>
    public static (bool Cert, bool Ver) ParseScorecard(string raw)
    {
        var cert = false;
        using (var doc = JsonDocument.Parse(raw))
        {
            if (doc.RootElement.TryGetProperty("criticsScore", out var cs) &&
                cs.ValueKind == JsonValueKind.Object &&
                cs.TryGetProperty("certified", out var c) && c.ValueKind == JsonValueKind.True)
            {
                cert = true;
            }
        }

        var ver = raw.Contains("POSITIVE\",\"certified\":true", StringComparison.Ordinal);
        return (cert, ver);
    }
}
