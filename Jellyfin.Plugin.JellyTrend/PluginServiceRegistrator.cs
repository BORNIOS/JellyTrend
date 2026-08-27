using Jellyfin.Plugin.JellyTrend.Channel;
using Jellyfin.Plugin.JellyTrend.ExternalAPI;
using Jellyfin.Plugin.JellyTrend.ScheduledTask;
using Jellyfin.Plugin.JellyTrend.Sync;
using Jellyfin.Plugin.JellyTrend.Web;
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
        serviceCollection.AddSingleton<IChannel, TrendingChannel>();
        serviceCollection.AddSingleton<IChannel, RecommendedChannel>();

        serviceCollection.AddSingleton<TrendingLibraryLinkService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TrendingLibraryLinkService>());
        serviceCollection.AddSingleton<IEventConsumer<PlaybackStopEventArgs>>(sp => sp.GetRequiredService<TrendingLibraryLinkService>());
        serviceCollection.AddSingleton<IEventConsumer<PlaybackProgressEventArgs>>(sp => sp.GetRequiredService<TrendingLibraryLinkService>());

        // ScriptInjectionService patches index.html on disk at startup so the banner
        // script is served without any pipeline middleware (IStartupFilter is not
        // reliable for dynamically loaded plugins).
        serviceCollection.AddSingleton<ScriptInjectionService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ScriptInjectionService>());

        // JellyTrendLog is initialized directly in Plugin.cs constructor — no hosted service needed.
    }
}
