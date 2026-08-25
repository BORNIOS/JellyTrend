using System;
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

    public static Guid GetPluginChannelFolderId(ILibraryManager libraryManager)
    {
        var name = GetConfiguredChannelName();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return libraryManager.GetNewItemId("Channel " + name, typeof(MediaBrowser.Controller.Channels.Channel));
    }
}
