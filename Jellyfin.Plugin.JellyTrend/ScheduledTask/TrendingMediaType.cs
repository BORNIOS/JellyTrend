using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTrend.ScheduledTask;

/// <summary>
/// The media type of a trending item.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrendingMediaType
{
    /// <summary>
    /// A movie.
    /// </summary>
    Movie,

    /// <summary>
    /// A TV series.
    /// </summary>
    Series
}
