using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTrend.Api;

/// <summary>
/// Data transfer object describing a matched trending library item for the carousel.
/// </summary>
public sealed class TrendingItemDto
{
    /// <summary>
    /// Gets or sets the local library item id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the item name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the item overview.
    /// </summary>
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets the runtime type name of the item (e.g. Movie, Series).
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the TMDB id.
    /// </summary>
    public string? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the backdrop image URL.
    /// </summary>
    public string? BackdropImageUrl { get; set; }

    /// <summary>
    /// Gets or sets the primary image URL.
    /// </summary>
    public string? PrimaryImageUrl { get; set; }

    /// <summary>
    /// Gets or sets the production year.
    /// </summary>
    public int? ProductionYear { get; set; }

    /// <summary>
    /// Gets or sets the community rating.
    /// </summary>
    public float? CommunityRating { get; set; }

    /// <summary>
    /// Gets the list of genres.
    /// </summary>
    public IReadOnlyList<string>? Genres { get; init; }

    /// <summary>
    /// Gets the list of actor names.
    /// </summary>
    public IReadOnlyList<string>? Actors { get; init; }

    /// <summary>
    /// Gets or sets the logo image URL.
    /// </summary>
    public string? LogoImageUrl { get; set; }

    /// <summary>
    /// Gets or sets the disc image URL.
    /// </summary>
    public string? DiscImageUrl { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the item has been played by the viewer.
    /// </summary>
    public bool? IsPlayed { get; set; }

    /// <summary>
    /// Gets or sets the viewer's playback position in ticks.
    /// </summary>
    public long? PlaybackPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets the item runtime in ticks.
    /// </summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets the media streams.
    /// </summary>
    public IReadOnlyList<MediaStreamDto>? MediaStreams { get; init; }
}
