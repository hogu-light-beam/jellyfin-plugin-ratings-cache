using Jellyfin.Plugin.RatingsCache.Services;
using Jellyfin.Plugin.RatingsCache.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RatingsCache;

/// <summary>Enregistre les services du plugin dans l'injection de dépendances de Jellyfin.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PluginLog>();
        serviceCollection.AddSingleton<RatingsStore>();
        serviceCollection.AddSingleton<PayloadService>();
        serviceCollection.AddSingleton<InjectionState>();
        serviceCollection.AddSingleton<RatingsSyncService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<RatingsSyncService>());

        // Injection automatique du script dans index.html, sans plugin tiers ni modification de fichier
        serviceCollection.AddSingleton<IStartupFilter, IndexInjectionStartupFilter>();
    }
}
