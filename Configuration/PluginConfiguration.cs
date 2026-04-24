using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTrend.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public const string DefaultChannelName = "JellyTrend - Trending Now";

    public string TmdbApiKey { get; set; } = string.Empty;

    public int SyncIntervalHours { get; set; } = 24;

    public int MaxItems { get; set; } = 20;

    public bool EnableBannerMode { get; set; } = true;

    /// <summary>
    /// Expone un canal visible en la sección Canales de todos los clientes
    /// (Roku, Android TV, web, iOS). El canal muestra los items trending reproducibles directamente.
    /// </summary>
    public bool EnableChannel { get; set; } = true;

    /// <summary>Nombre del canal que aparece en la sección Canales de Jellyfin.</summary>
    public string ChannelName { get; set; } = DefaultChannelName;

    /// <summary>
    /// BCP-47 language tag passed to TMDB (e.g. es-MX, en-US, pt-BR).
    /// Controls the language of titles, overviews and metadata returned.
    /// Leave empty for TMDB's default (en-US).
    /// </summary>
    public string TmdbLanguage { get; set; } = "es-MX";

    /// <summary>
    /// ISO 3166-1 alpha-2 region code passed to TMDB (e.g. MX, US, ES, BR).
    /// Filters trending results to content available/popular in that country.
    /// Leave empty for global trending.
    /// </summary>
    public string TmdbRegion { get; set; } = "MX";
}
