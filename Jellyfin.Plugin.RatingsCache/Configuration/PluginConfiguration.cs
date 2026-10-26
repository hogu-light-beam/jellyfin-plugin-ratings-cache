using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RatingsCache;

/// <summary>Réglages du plugin (page "Ratings Cache" du tableau de bord).</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Clés des notes : imdb, rt (Rotten Tomatoes critiques), aud (Popcorn), tmdb, lb (Letterboxd).</summary>
    public static readonly string[] AllSources = { "imdb", "rt", "aud", "tmdb", "lb" };

    // ---------------------------------------------------------------- Limites (appliquées à l'enregistrement ET à la lecture)
    public const int TtlMin = 1, TtlMax = 365;
    public const int EmptyRetryMin = 1, EmptyRetryMax = 60;
    public const int IntervalMin = 5, IntervalMax = 1440;
    public const int BudgetMin = 10, BudgetMax = 20000;
    public const int DelayMin = 100, DelayMax = 10000;
    public const int DetailLogoMin = 60, DetailLogoMax = 200;
    public const int CardsLogoMin = 60, CardsLogoMax = 180;
    public const int CardsMaxRatingsMin = 1, CardsMaxRatingsMax = 3;
    public const int MediaBarLogoMin = 60, MediaBarLogoMax = 200;
    public const int MediaBarMaxRatingsMin = 1, MediaBarMaxRatingsMax = 5;
    public const int ReviewsPerSourceMin = 1, ReviewsPerSourceMax = 10;
    public const int ReviewsTotalLow = 1, ReviewsTotalHigh = 30;
    public const int ReviewsCharsMin = 100, ReviewsCharsMax = 1500;
    public const int ReviewsCacheMin = 1, ReviewsCacheMax = 90;
    public const int ApiKeyMaxLength = 128;
    public const int TokenMaxLength = 400;

    private static readonly Regex HourMinute = new(@"^([01]?\d|2[0-3]):([0-5]\d)$", RegexOptions.Compiled);

    /// <summary>Séparateurs autorisés : clé ASCII -> caractère (séquences \u : indépendant de l'encodage du fichier source).</summary>
    public static readonly Dictionary<string, string> Separators = new()
    {
        ["dash"] = "-",
        ["endash"] = "\u2013",
        ["dot"] = "\u00B7",
        ["bullet"] = "\u2022",
        ["bar"] = "|",
        ["slash"] = "/"
    };

    // ---------------------------------------------------------------- Source & cache
    public string MdbListApiKey { get; set; } = string.Empty;

    public bool SyncEnabled { get; set; } = true;

    /// <summary>Durée de validité d'une note avant rafraîchissement (jours).</summary>
    public int TtlDays { get; set; } = 14;

    /// <summary>Nouvel essai quand MDBList ne connaissait pas le titre (jours).</summary>
    public int EmptyRetryDays { get; set; } = 3;

    /// <summary>Fréquence de relecture de la bibliothèque et de rafraîchissement (minutes).</summary>
    public int SyncIntervalMinutes { get; set; } = 30;

    /// <summary>Requêtes MDBList maximum par jour. L'offre gratuite autorise 1000.</summary>
    public int DailyBudget { get; set; } = 900;

    /// <summary>Pause entre deux appels MDBList (millisecondes).</summary>
    public int CallDelayMs { get; set; } = 400;

    /// <summary>Lit le compteur réel du compte MDBList (partagé avec tes autres applications) avant de consommer le budget.</summary>
    public bool UseAccountQuota { get; set; } = true;

    /// <summary>Heure (UTC, HH:mm) de remise à zéro du quota MDBList. Sert à planifier la reprise automatique.</summary>
    public string QuotaResetUtc { get; set; } = "00:00";

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

    /// <summary>Séparateur entre la note et le texte : dash | endash | dot | bullet | bar | slash.</summary>
    public string CardsSeparator { get; set; } = "dash";

    public bool CardsShowLogo { get; set; } = true;

    public int CardsLogoSize { get; set; } = 120;

    /// <summary>before (note avant le texte) | after</summary>
    public string CardsPosition { get; set; } = "before";

    // ---------------------------------------------------------------- Plugin Media Bar (accueil)
    public bool MediaBarEnabled { get; set; } = true;

    /// <summary>Ordre de priorité des notes affichées dans la ligne d'infos de la Media Bar.</summary>
    public string MediaBarSources { get; set; } = "lb,imdb,tmdb,rt,aud";

    public int MediaBarMaxRatings { get; set; } = 3;

    public bool MediaBarShowLogo { get; set; } = true;

    public int MediaBarLogoSize { get; set; } = 130;

    /// <summary>start (au début) | afterDate | afterAge | end</summary>
    public string MediaBarPosition { get; set; } = "start";

    /// <summary>Masque l'étoile TMDb d'origine de la Media Bar quand le titre a au moins une note.</summary>
    public bool MediaBarHideStar { get; set; } = true;

    // ---------------------------------------------------------------- Critiques / commentaires
    public bool ReviewsEnabled { get; set; } = false;

    /// <summary>Clé API TMDb (v3) ou jeton « Read Access Token » (v4).</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Client ID Trakt (application gratuite sur trakt.tv/oauth/applications).</summary>
    public string TraktClientId { get; set; } = string.Empty;

    public bool ReviewsUseTmdb { get; set; } = true;

    public bool ReviewsUseTrakt { get; set; } = true;

    /// <summary>Nombre de critiques affichées par source.</summary>
    public int ReviewsPerSource { get; set; } = 3;

    /// <summary>Nombre total de critiques affichées.</summary>
    public int ReviewsTotalMax { get; set; } = 6;

    /// <summary>mixed (une seule liste) | bySource (une section par source)</summary>
    public string ReviewsGrouping { get; set; } = "mixed";

    /// <summary>likes | recent | rating</summary>
    public string ReviewsSort { get; set; } = "likes";

    /// <summary>Longueur affichée avant « Lire la suite ».</summary>
    public int ReviewsMaxChars { get; set; } = 400;

    public bool ReviewsShowAuthor { get; set; } = true;

    public bool ReviewsShowDate { get; set; } = true;

    public bool ReviewsShowRating { get; set; } = true;

    public bool ReviewsShowSource { get; set; } = true;

    public bool ReviewsShowLikes { get; set; } = true;

    public bool ReviewsHideSpoilers { get; set; } = true;

    /// <summary>Section repliée par défaut.</summary>
    public bool ReviewsCollapsed { get; set; } = false;

    /// <summary>top (avant le reste du contenu secondaire) | bottom</summary>
    public string ReviewsPosition { get; set; } = "top";

    /// <summary>Durée de conservation des critiques en cache (jours).</summary>
    public int ReviewsCacheDays { get; set; } = 7;

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

    /// <summary>Clé de séparateur valide (accepte aussi l'ancien format : le caractère lui-même).</summary>
    public static string NormalizeSeparator(string? value)
    {
        if (value != null && Separators.ContainsKey(value))
        {
            return value;
        }

        foreach (var kv in Separators)
        {
            if (kv.Value == value)
            {
                return kv.Key;
            }
        }

        return "dash";
    }

    public static string SeparatorChar(string? value) => Separators[NormalizeSeparator(value)];

    /// <summary>Nettoie une clé : sans espaces ni caractères de contrôle, longueur limitée.</summary>
    public static string CleanApiKey(string? key, int max = ApiKeyMaxLength)
    {
        var chars = (key ?? string.Empty).Where(ch => ch > 32 && ch < 127).Take(max);
        return new string(chars.ToArray());
    }

    /// <summary>Heure HH:mm valide, sinon 00:00.</summary>
    public static string NormalizeHourMinute(string? value)
    {
        var m = HourMinute.Match((value ?? string.Empty).Trim());
        return m.Success ? $"{int.Parse(m.Groups[1].Value):00}:{m.Groups[2].Value}" : "00:00";
    }

    /// <summary>Minutes écoulées depuis minuit UTC au moment de la remise à zéro du quota.</summary>
    public static int ResetMinutes(string? value)
    {
        var p = NormalizeHourMinute(value).Split(':');
        return (int.Parse(p[0]) * 60) + int.Parse(p[1]);
    }

    /// <summary>Ramène tous les réglages dans leurs limites. Appelée à l'enregistrement et au chargement.</summary>
    public void Sanitize()
    {
        MdbListApiKey = CleanApiKey(MdbListApiKey);
        TmdbApiKey = CleanApiKey(TmdbApiKey, TokenMaxLength);
        TraktClientId = CleanApiKey(TraktClientId);
        TtlDays = Clamp(TtlDays, TtlMin, TtlMax);
        EmptyRetryDays = Clamp(EmptyRetryDays, EmptyRetryMin, EmptyRetryMax);
        SyncIntervalMinutes = Clamp(SyncIntervalMinutes, IntervalMin, IntervalMax);
        DailyBudget = Clamp(DailyBudget, BudgetMin, BudgetMax);
        CallDelayMs = Clamp(CallDelayMs, DelayMin, DelayMax);
        QuotaResetUtc = NormalizeHourMinute(QuotaResetUtc);
        DetailLogoSize = Clamp(DetailLogoSize, DetailLogoMin, DetailLogoMax);
        CardsLogoSize = Clamp(CardsLogoSize, CardsLogoMin, CardsLogoMax);
        CardsMaxRatings = Clamp(CardsMaxRatings, CardsMaxRatingsMin, CardsMaxRatingsMax);
        MediaBarLogoSize = Clamp(MediaBarLogoSize, MediaBarLogoMin, MediaBarLogoMax);
        MediaBarMaxRatings = Clamp(MediaBarMaxRatings, MediaBarMaxRatingsMin, MediaBarMaxRatingsMax);
        ReviewsPerSource = Clamp(ReviewsPerSource, ReviewsPerSourceMin, ReviewsPerSourceMax);
        ReviewsTotalMax = Clamp(ReviewsTotalMax, ReviewsTotalLow, ReviewsTotalHigh);
        ReviewsMaxChars = Clamp(ReviewsMaxChars, ReviewsCharsMin, ReviewsCharsMax);
        ReviewsCacheDays = Clamp(ReviewsCacheDays, ReviewsCacheMin, ReviewsCacheMax);
        DetailOrder = string.Join(",", ParseSources(DetailOrder));
        CardsSources = string.Join(",", ParseSources(CardsSources));
        MediaBarSources = string.Join(",", ParseSources(MediaBarSources));
        DetailPosition = Pick(DetailPosition, "afterOfficialRating", "afterOfficialRating", "afterRuntime", "end");
        CardsLine = Pick(CardsLine, "secondary", "secondary", "first");
        CardsPosition = Pick(CardsPosition, "before", "before", "after");
        CardsSeparator = NormalizeSeparator(CardsSeparator);
        MediaBarPosition = Pick(MediaBarPosition, "start", "start", "afterDate", "afterAge", "end");
        ReviewsGrouping = Pick(ReviewsGrouping, "mixed", "mixed", "bySource");
        ReviewsSort = Pick(ReviewsSort, "likes", "likes", "recent", "rating");
        ReviewsPosition = Pick(ReviewsPosition, "top", "top", "bottom");
    }
}
