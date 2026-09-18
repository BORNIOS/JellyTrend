using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTrend;

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

    /// <summary>
    /// Gets or sets the share of the trending list reserved for TV series, as a percentage of
    /// <see cref="MaxItems"/>. Ignored while <see cref="EnableTrendingSeries"/> is false.
    /// </summary>
    public int TrendingSeriesShare { get; set; } = 50;

    /// <summary>
    /// Gets or sets a value indicating whether the recommendations channel is listed under Channels in
    /// all clients. Independent from the home row, which is what Jellyfin asks the channel for its latest
    /// media: with the channel hidden the row does not appear either.
    /// </summary>
    public bool EnableRecommendationChannel { get; set; } = true;

    /// <summary>
    /// Gets or sets how often the served slice of the recommendation pool rotates, in hours. 1 keeps the
    /// previous behaviour (a different slice every hour); 0 always serves the best ranked titles first.
    /// </summary>
    public int RecommendationRotationHours { get; set; } = 1;

    /// <summary>
    /// Gets or sets how many times the visible row is generated and stored. The extra titles are the
    /// material the rotation uses once part of the row has been watched; more titles cost more time in
    /// the weekly run.
    /// </summary>
    public int RecommendationPoolFactor { get; set; } = 2;

    /// <summary>
    /// Gets or sets the number of consumed titles below which a profile counts as cold and the row falls
    /// back to what the rest of the server watches.
    /// </summary>
    public int ColdStartMinWatched { get; set; } = 10;

    /// <summary>
    /// Gets or sets an alternative folder for the JSON data files. Empty keeps them under
    /// <c>{DataPath}/JellyTrend</c>, which is what survives a plugin update. Only an absolute path is
    /// accepted; a relative one is ignored so the data never lands next to the process.
    /// </summary>
    public string JsonDataPath { get; set; } = string.Empty;
}
