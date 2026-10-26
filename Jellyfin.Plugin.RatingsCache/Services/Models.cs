using System;
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

/// <summary>Résumé d'un passage du plugin (affiché dans l'historique).</summary>
public sealed class RunInfo
{
    /// <summary>Début du passage (secondes Unix).</summary>
    public long At { get; set; }

    public long Ms { get; set; }

    public int Fetched { get; set; }

    public int Empty { get; set; }

    public int Errors { get; set; }

    /// <summary>Titres restant à traiter à la fin du passage.</summary>
    public int Remaining { get; set; }

    /// <summary>done | budget | account | 429 | time | error | key | nokey | disabled | nothing</summary>
    public string Stop { get; set; } = "done";
}

/// <summary>Critique (TMDb ou Trakt). Noms courts : ceux lus par le client JavaScript.</summary>
public sealed class Review
{
    [JsonPropertyName("s")]
    public string S { get; set; } = string.Empty;

    [JsonPropertyName("a")]
    public string A { get; set; } = string.Empty;

    [JsonPropertyName("r")]
    public double? R { get; set; }

    [JsonPropertyName("d")]
    public string? D { get; set; }

    [JsonPropertyName("t")]
    public string T { get; set; } = string.Empty;

    [JsonPropertyName("l")]
    public int L { get; set; }

    [JsonPropertyName("u")]
    public string? U { get; set; }

    [JsonPropertyName("sp")]
    public bool? Sp { get; set; }
}

public sealed class ReviewRec
{
    public List<Review> Items { get; set; } = new();

    public long Fetched { get; set; }

    public bool Failed { get; set; }
}

public sealed class StoreCounts
{
    public int Movies { get; set; }

    public int Shows { get; set; }

    public int Items { get; set; }

    public int ItemsWithTmdb { get; set; }

    /// <summary>Titres distincts (deux éditions d'un même film partagent une seule note).</summary>
    public int UniqueKeys { get; set; }

    public int Processed { get; set; }

    public int WithData { get; set; }

    public int Empty { get; set; }

    public int Missing { get; set; }

    public int Stale { get; set; }

    /// <summary>Notes en cache au total, y compris celles de titres retirés de la bibliothèque.</summary>
    public int RatingsCached { get; set; }

    public int WithAge { get; set; }

    public int WithCountries { get; set; }

    public int WithLanguages { get; set; }

    public int WithRtUrl { get; set; }

    public int Certified { get; set; }

    public int Verified { get; set; }

    /// <summary>Titres ayant les cinq notes.</summary>
    public int AllFive { get; set; }

    /// <summary>Compteurs par source, dans l'ordre imdb, rt, aud, tmdb, lb.</summary>
    public int[] PerSource { get; set; } = new int[5];

    /// <summary>Matrice 5x5 : titres ayant à la fois la source i et la source j (la diagonale = le total de la source).</summary>
    public int[][] Overlap { get; set; } = Array.Empty<int[]>();

    public int Remaining => Missing + Stale;
}
