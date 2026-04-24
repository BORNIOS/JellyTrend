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
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.JellyTrend;

/// <summary>
/// Registers plugin services with the Jellyfin DI container.
/// Jellyfin calls this automatically when loading the plugin assembly.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TmdbClient>();
        serviceCollection.AddSingleton<IScheduledTask, TrendingSyncTask>();
        serviceCollection.AddSingleton<IChannel, TrendingChannel>();

        serviceCollection.AddSingleton<TrendingLibraryLinkService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TrendingLibraryLinkService>());
        serviceCollection.AddSingleton<IEventConsumer<PlaybackStopEventArgs>>(sp => sp.GetRequiredService<TrendingLibraryLinkService>());
        serviceCollection.AddSingleton<IEventConsumer<PlaybackProgressEventArgs>>(sp => sp.GetRequiredService<TrendingLibraryLinkService>());

        // IStartupFilter is evaluated by ASP.NET Core during pipeline construction.
        // JellyTrendStartupFilter wraps the app builder to prepend ScriptInjectionMiddleware.
        serviceCollection.AddTransient<IStartupFilter, JellyTrendStartupFilter>();
        serviceCollection.AddTransient<ScriptInjectionMiddleware>();
    }
}
