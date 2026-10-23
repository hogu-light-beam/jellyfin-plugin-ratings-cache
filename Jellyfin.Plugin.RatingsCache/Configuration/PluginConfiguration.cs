using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RatingsCache;

/// <summary>Réglages du plugin (page "Ratings Cache" du tableau de bord).</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Clés des notes : imdb, rt (Rotten Tomatoes critiques), aud (Popcorn), tmdb, lb (Letterboxd).</summary>
    public static readonly string[] AllSources = { "imdb", "rt", "aud", "tmdb", "lb" };

    // ---------------------------------------------------------------- Source & cache
    public string MdbListApiKey { get; set; } = string.Empty;

    public bool SyncEnabled { get; set; } = true;

    /// <summary>Durée de validité d'une note avant rafraîchissement (jours).</summary>
    public int TtlDays { get; set; } = 14;

    /// <summary>Nouvel essai quand MDBList ne connaissait pas le titre (jours).</summary>
    public int EmptyRetryDays { get; set; } = 3;

    /// <summary>Fréquence de relecture de la bibliothèque et de rafraîchissement (minutes).</summary>
    public int SyncIntervalMinutes { get; set; } = 30;

    /// <summary>Requêtes MDBList maximum par jour (UTC). L'offre gratuite autorise 1000.</summary>
    public int DailyBudget { get; set; } = 900;

    /// <summary>Pause entre deux appels MDBList (millisecondes).</summary>
    public int CallDelayMs { get; set; } = 400;

    /// <summary>Badges Certified Fresh / Verified Hot et lien Rotten Tomatoes (Wikidata + page RT).</summary>
    public bool EnableRottenTomatoesBadges { get; set; } = true;

    // ---------------------------------------------------------------- Page de détail
    public bool DetailEnabled { get; set; } = true;

    public bool ShowImdb { get; set; } = true;

    public bool ShowRottenTomatoes { get; set; } = true;

    public bool ShowAudience { get; set; } = true;

    public bool ShowTmdb { get; set; } = true;

    public bool ShowLetterboxd { get; set; } = true;

    /// <summary>Ordre d'affichage, séparé par des virgules (imdb,rt,aud,tmdb,lb).</summary>
    public string DetailOrder { get; set; } = "imdb,rt,aud,tmdb,lb";

    public bool ShowCertification { get; set; } = true;

    public bool ShowCountries { get; set; } = true;

    public bool ShowLanguages { get; set; } = true;

    public bool ReorderExternalLinks { get; set; } = true;

    /// <summary>Note TMDb de chaque épisode sur la page d'une saison.</summary>
    public bool ShowSeasonEpisodeRatings { get; set; } = true;

    /// <summary>Note TMDb sur la page d'un épisode.</summary>
    public bool ShowEpisodePageRating { get; set; } = true;

    /// <summary>Taille des logos en % de la hauteur du texte (100 = 1 ligne).</summary>
    public int DetailLogoSize { get; set; } = 150;

    /// <summary>afterOfficialRating | afterRuntime | end</summary>
    public string DetailPosition { get; set; } = "afterOfficialRating";

    // ---------------------------------------------------------------- Cartes
    public bool CardsEnabled { get; set; } = true;

    public bool CardsMovies { get; set; } = true;

    public bool CardsSeries { get; set; } = true;

    /// <summary>Sur les cartes d'épisodes, affiche la note de la série.</summary>
    public bool CardsEpisodes { get; set; } = false;

    /// <summary>secondary (2e ligne : année) | first (1re ligne : titre)</summary>
    public string CardsLine { get; set; } = "secondary";

    /// <summary>Ordre de priorité des notes affichées sur les cartes (lb,imdb,tmdb,rt,aud).</summary>
    public string CardsSources { get; set; } = "lb,imdb,tmdb,rt,aud";

    /// <summary>Nombre de notes affichées par carte (1 à 3).</summary>
    public int CardsMaxRatings { get; set; } = 1;

    /// <summary>Séparateur entre la note et le texte (· - – • |).</summary>
    public string CardsSeparator { get; set; } = "-";

    public bool CardsShowLogo { get; set; } = true;

    public int CardsLogoSize { get; set; } = 120;

    /// <summary>before (note avant le texte) | after</summary>
    public string CardsPosition { get; set; } = "before";

    // ---------------------------------------------------------------- Avancé
    /// <summary>Masque l'étoile TMDb d'origine de Jellyfin.</summary>
    public bool HideDefaultStar { get; set; } = true;

    /// <summary>Injecte automatiquement le script dans index.html. À désactiver si tu l'ajoutes toi-même.</summary>
    public bool InjectScript { get; set; } = true;

    // ---------------------------------------------------------------- Helpers
    public static List<string> ParseSources(string? csv)
    {
        var list = (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .Where(s => AllSources.Contains(s))
            .Distinct()
            .ToList();
        foreach (var s in AllSources)
        {
            if (!list.Contains(s))
            {
                list.Add(s);
            }
        }

        return list;
    }

    public static string Pick(string? value, string fallback, params string[] allowed)
    {
        return value != null && allowed.Contains(value) ? value : fallback;
    }

    public static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));
}
