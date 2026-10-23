using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RatingsCache;

/// <summary>Jellyfin Ratings Cache : notes IMDb / Rotten Tomatoes / TMDB / Letterboxd mises en cache côté serveur.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Jellyfin Ratings Cache";

    public override Guid Id => Guid.Parse("86e58b19-d945-49a1-93ff-af4b91792b99");

    public override string Description =>
        "Notes (IMDb, Rotten Tomatoes, Popcorn, TMDB, Letterboxd) calculées et mises en cache côté serveur, affichées sans latence sur les pages et les cartes.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "JellyfinRatingsCache",
                DisplayName = "Ratings Cache",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "star"
            }
        };
    }
}
