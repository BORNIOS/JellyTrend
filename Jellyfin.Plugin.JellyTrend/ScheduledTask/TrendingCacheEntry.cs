using System;

namespace Jellyfin.Plugin.JellyTrend.ScheduledTask;

/// <summary>
/// A single cached trending item linking a TMDB entry to a local library item.
/// </summary>
public sealed class TrendingCacheEntry
{
    /// <summary>
    /// Gets or sets the local library item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the media type (movie or series).
    /// </summary>
    public TrendingMediaType MediaType { get; set; }

    /// <summary>
    /// Gets or sets the TMDB id.
    /// </summary>
    public string? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the TMDB backdrop path.
    /// </summary>
    public string? TmdbBackdropPath { get; set; }

    /// <summary>
    /// Gets or sets the TMDB poster path.
    /// </summary>
    public string? TmdbPosterPath { get; set; }
}
