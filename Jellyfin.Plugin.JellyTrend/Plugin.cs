using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.JellyTrend.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend;

/// <summary>
/// The main JellyTrend plugin.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="loggerFactory">The server logger factory (used to locate the log directory).</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
    {
        _ = loggerFactory;
        Instance = this;
        InitializeLog(applicationPaths);
        JellyTrendLog.Info($"=== JellyTrend v{Version} cargado. Log: {JellyTrendLog.CurrentLogPath} ===");
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "JellyTrend";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("d3b07384-d9a1-4e2b-8c3f-1234567890ab");

    /// <inheritdoc />
    public override string Description =>
        "Syncs TMDB trending movies & shows with the local library and provides a Netflix-style banner carousel.";

    /// <summary>
    /// Gets the directory that contains this plugin's DLL (e.g. .../plugins/JellyTrend_1.0.0).
    /// Use this instead of DataFolderPath, which Jellyfin resolves to a separate folder
    /// named after the assembly ("Jellyfin.Plugin.JellyTrend") rather than the plugin folder.
    /// </summary>
    public string PluginFolder =>
        Path.GetDirectoryName(AssemblyFilePath)
        ?? Path.GetDirectoryName(GetType().Assembly.Location)
        ?? string.Empty;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace!;

        return
        [
            new PluginPageInfo
            {
                Name = "JellyTrend",
                DisplayName = "JellyTrend",
                EmbeddedResourcePath = $"{ns}.Configuration.configPage.html",
                EnableInMainMenu = true
            }
        ];
    }

    private static void InitializeLog(IApplicationPaths applicationPaths)
    {
        JellyTrendLog.SetLogDirectory(
            applicationPaths.LogDirectoryPath,
            Path.Combine(applicationPaths.ProgramDataPath, "log"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "jellyfin", "log"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jellyfin", "Server", "log"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Jellyfin", "Server", "log"));
    }
}
