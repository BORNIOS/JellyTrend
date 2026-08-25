using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.JellyTrend.Api;

/// <summary>
/// Persisted per user to {PluginFolder}/recommendations/{userId}.json by the weekly
/// RecommendationSyncTask. Only item ids are stored; the channel resolves each id to
/// the library item at read time (same pattern as trending.json).
/// </summary>
public sealed class UserRecommendations
{
    /// <summary>
    /// Gets or sets the recommended library item ids for this user.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1002", Justification = "Settable List required for System.Text.Json round-tripping of the cache file.")]
    [SuppressMessage("Microsoft.Usage", "CA2227", Justification = "Settable List required for System.Text.Json round-tripping of the cache file.")]
    public List<Guid> ItemIds { get; set; } = new();

    /// <summary>
    /// Gets or sets the timestamp of the last recommendation generation.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
