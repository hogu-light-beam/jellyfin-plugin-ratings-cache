using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.RatingsCache.Services;

/// <summary>Film ou série de la bibliothèque (type, identifiants externes).</summary>
public sealed class ItemRec
{
    /// <summary>movie | show</summary>
    public string Type { get; set; } = "movie";

    public string? Tmdb { get; set; }

    public string? Imdb { get; set; }
}

/// <summary>Données de notation d'un titre. Les noms courts sont ceux lus par le client JavaScript.</summary>
public sealed class RatingData
{
    /// <summary>Notes : imdb, rt, aud, tmdb, lb.</summary>
    [JsonPropertyName("r")]
    public Dictionary<string, double>? R { get; set; }

    [JsonPropertyName("age")]
    public int? Age { get; set; }

    [JsonPropertyName("c")]
    public List<string>? C { get; set; }

    [JsonPropertyName("l")]
    public List<string>? L { get; set; }

    [JsonPropertyName("rt_url")]
    public string? RtUrl { get; set; }

    [JsonPropertyName("cert")]
    public int? Cert { get; set; }

    [JsonPropertyName("ver")]
    public int? Ver { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        (R == null || R.Count == 0) && Age == null && (C == null || C.Count == 0) && (L == null || L.Count == 0);
}

public sealed class RatingRec
{
    public RatingData Data { get; set; } = new();

    /// <summary>Horodatage Unix (secondes) de la dernière récupération.</summary>
    public long Fetched { get; set; }
}

/// <summary>Ligne du fichier servi au client : identifiants + données de notation (aplatis).</summary>
public sealed class PayloadItem
{
    [JsonPropertyName("t")]
    public string T { get; set; } = "movie";

    [JsonPropertyName("tm")]
    public string? Tm { get; set; }

    [JsonPropertyName("im")]
    public string? Im { get; set; }

    [JsonPropertyName("r")]
    public Dictionary<string, double>? R { get; set; }

    [JsonPropertyName("age")]
    public int? Age { get; set; }

    [JsonPropertyName("c")]
    public List<string>? C { get; set; }

    [JsonPropertyName("l")]
    public List<string>? L { get; set; }

    [JsonPropertyName("rt_url")]
    public string? RtUrl { get; set; }

    [JsonPropertyName("cert")]
    public int? Cert { get; set; }

    [JsonPropertyName("ver")]
    public int? Ver { get; set; }
}

public sealed class PendingItem
{
    public string Key { get; set; } = string.Empty;

    public string Type { get; set; } = "movie";

    public string Tmdb { get; set; } = string.Empty;

    public string? Imdb { get; set; }
}

public sealed class StoreCounts
{
    public int Items { get; set; }

    public int ItemsWithTmdb { get; set; }

    public int RatingsCached { get; set; }

    public int RatingsWithData { get; set; }

    public int WithLetterboxd { get; set; }

    public int WithImdb { get; set; }
}
