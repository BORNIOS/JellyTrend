using Jellyfin.Plugin.JellyTrend.Services;
using Jellyfin.Plugin.JellyTrend.Services.Backend;
using Jellyfin.Plugin.JellyTrend.Services.Channel;
using Jellyfin.Plugin.JellyTrend.Services.ExternalApi;
using Jellyfin.Plugin.JellyTrend.Services.Sync;
using Jellyfin.Plugin.JellyTrend.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.JellyTrend;

/// <summary>
/// Registers plugin services with the Jellyfin DI container.
/// Jellyfin calls this automatically when loading the plugin assembly.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Registers the plugin services with the Jellyfin DI container.
    /// </summary>
    /// <param name="serviceCollection">The service collection.</param>
    /// <param name="applicationHost">The server application host.</param>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TmdbClient>();
        serviceCollection.AddSingleton<IScheduledTask, TrendingSyncTask>();
        serviceCollection.AddSingleton<IScheduledTask, RecommendationSyncTask>();

        // El backend de base de datos (plugin PostgreSQL) se detecta y se comprueba UNA vez al arrancar:
        // las tareas de recomendaciones y de tendencias leen ese resultado en lugar de averiguarlo cada una.
        serviceCollection.AddSingleton<DatabaseBackend>();
        serviceCollection.AddSingleton<IHostedService, DatabaseBackendStartupService>();
        serviceCollection.AddSingleton<IChannel, TrendingChannel>();
        serviceCollection.AddSingleton<IChannel, RecommendedChannel>();

        serviceCollection.AddSingleton<TrendingLibraryLinkService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TrendingLibraryLinkService>());
        serviceCollection.AddSingleton<IEventConsumer<PlaybackStopEventArgs>>(sp => sp.GetRequiredService<TrendingLibraryLinkService>());
        serviceCollection.AddSingleton<IEventConsumer<PlaybackProgressEventArgs>>(sp => sp.GetRequiredService<TrendingLibraryLinkService>());

        // Refresco incremental del perfil: un evento de fin de reproduccion adelanta lo que antes solo
        // ocurria en la corrida semanal.
        serviceCollection.AddSingleton<ConsumptionRefreshConsumer>();
        serviceCollection.AddSingleton<IEventConsumer<PlaybackStopEventArgs>, ConsumptionRefreshConsumer>();

        // ScriptInjectionService patches index.html on disk at startup so the banner
        // script is served without any pipeline middleware (IStartupFilter is not
        // reliable for dynamically loaded plugins).
        serviceCollection.AddSingleton<ScriptInjectionService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ScriptInjectionService>());

        // JellyTrendLog is initialized directly in Plugin.cs constructor — no hosted service needed.
    }
}
