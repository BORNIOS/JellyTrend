using System;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// How one user consumes one title, aggregated: the input of the taste profile.
/// </summary>
public sealed class ConsumptionRecord
{
    /// <summary>Gets or sets the title the user interacted with.</summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the series this title belongs to, when it is an episode. Aggregation is per work, so
    /// bingeing 62 episodes contributes a single preference instead of 62.
    /// </summary>
    public Guid? SeriesId { get; set; }

    /// <summary>Gets or sets how many times the title was played.</summary>
    public int PlayCount { get; set; }

    /// <summary>Gets or sets the fraction of the title reproduced (0-1).</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets a value indicating whether the user marked it as a favourite.</summary>
    public bool Favorite { get; set; }

    /// <summary>Gets or sets a value indicating whether it was started, left unfinished and never resumed.</summary>
    public bool Abandoned { get; set; }

    /// <summary>Gets or sets when it was last played, in UTC.</summary>
    public DateTime? LastPlayedUtc { get; set; }

    /// <summary>Gets or sets how much this consumption teaches the profile.</summary>
    public double Weight { get; set; }

    /// <summary>Gets or sets when this record was aggregated, in UTC.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
