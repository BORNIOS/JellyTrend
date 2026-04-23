using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTrend.ScheduledTask;

/// <summary>
/// Persisted to {DataFolderPath}/trending.json between sync runs.
/// The API controller reads this file instead of re-querying the library on every request.
/// </summary>
public sealed class TrendingCache
{
    public List<Guid> ItemIds { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
