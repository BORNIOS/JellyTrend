using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTrend.Configuration;

/// <summary>
/// Plugin configuration for JellyTrend.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Default channel name shown under Channels in all clients.
    /// </summary>
    public const string DefaultChannelName = "JellyTrend - Trending Now";

    /// <summary>
    /// Default channel name for the personalized recommendations row.
    /// </summary>
    public const string DefaultRecommendationChannelName = "Recomendados";

    /// <summary>
    /// Gets or sets the TMDB v3 API key.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum number of trending items to keep.
    /// </summary>
    public int MaxItems { get; set; } = 20;

    /// <summary>
    /// Gets or sets a value indicating whether the home banner carousel is enabled.
    /// </summary>
    public bool EnableBannerMode { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether TV series are shown in the trending channel and home
    /// banner alongside movies. When false, only movies are shown.
    /// </summary>
    public bool EnableTrendingSeries { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the channel is visible under Channels in all clients
    /// (Roku, Android TV, web, iOS). The channel shows directly playable trending items.
    /// </summary>
    public bool EnableChannel { get; set; } = true;

    /// <summary>Gets or sets the channel name shown under Channels in Jellyfin.</summary>
    public string ChannelName { get; set; } = DefaultChannelName;

    /// <summary>
    /// Gets or sets a value indicating whether the personalized "Recomendados" row
    /// (a channel visible under Channels in all clients) is enabled.
    /// </summary>
    public bool EnableRecommendationRow { get; set; } = true;

    /// <summary>
    /// Gets or sets the channel name shown under Channels for the recommendations row.
    /// </summary>
    public string RecommendationChannelName { get; set; } = DefaultRecommendationChannelName;

    /// <summary>
    /// Gets or sets the maximum number of recommended items generated per user.
    /// </summary>
    public int RecommendationMaxItems { get; set; } = 20;

    /// <summary>
    /// Gets or sets the BCP-47 language tag passed to TMDB (e.g. es-MX, en-US, pt-BR).
    /// Controls the language of titles, overviews and metadata returned.
    /// Leave empty for TMDB's default (en-US).
    /// </summary>
    public string TmdbLanguage { get; set; } = "es-MX";

    /// <summary>
    /// Gets or sets the ISO 3166-1 alpha-2 region code passed to TMDB (e.g. MX, US, ES, BR).
    /// Filters trending results to content available/popular in that country.
    /// Leave empty for global trending.
    /// </summary>
    public string TmdbRegion { get; set; } = "MX";
}
