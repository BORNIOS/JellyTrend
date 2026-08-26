using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyTrend.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyTrend;

/// <summary>
/// Jellyfin calcula el Id de la carpeta del canal igual que <c>ChannelManager</c>:
/// <c>GetNewItemId("Channel " + providerName, typeof(Channel))</c>.
/// </summary>
internal static class ChannelIdentity
{
    public static string GetConfiguredChannelName()
        => Plugin.Instance?.Configuration.ChannelName ?? PluginConfiguration.DefaultChannelName;

    public static string GetConfiguredRecommendationChannelName()
        => Plugin.Instance?.Configuration.RecommendationChannelName ?? PluginConfiguration.DefaultRecommendationChannelName;

    /// <summary>
    /// Gets the folder id of the trending channel ("JellyTrend - Trending Now").
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns>The channel folder id.</returns>
    public static Guid GetPluginChannelFolderId(ILibraryManager libraryManager)
        => GetChannelFolderId(libraryManager, GetConfiguredChannelName());

    /// <summary>
    /// Gets the folder id of the recommendations channel ("Recomendados").
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns>The channel folder id.</returns>
    public static Guid GetRecommendationChannelFolderId(ILibraryManager libraryManager)
        => GetChannelFolderId(libraryManager, GetConfiguredRecommendationChannelName());

    /// <summary>
    /// Returns the folder ids of every JellyTrend channel (trending + recommendations).
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns>The channel folder ids.</returns>
    public static IEnumerable<Guid> GetAllChannelFolderIds(ILibraryManager libraryManager)
    {
        yield return GetPluginChannelFolderId(libraryManager);
        yield return GetRecommendationChannelFolderId(libraryManager);
    }

    /// <summary>
    /// Determines whether a channel id belongs to a JellyTrend channel (trending or recommendations).
    /// </summary>
    /// <param name="channelId">The channel id to check.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns><c>true</c> when the channel is one of the JellyTrend channels.</returns>
    public static bool IsJellyTrendChannelId(Guid channelId, ILibraryManager libraryManager)
        => channelId == GetPluginChannelFolderId(libraryManager)
            || channelId == GetRecommendationChannelFolderId(libraryManager);

    private static Guid GetChannelFolderId(ILibraryManager libraryManager, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return libraryManager.GetNewItemId("Channel " + name, typeof(MediaBrowser.Controller.Channels.Channel));
    }
}
