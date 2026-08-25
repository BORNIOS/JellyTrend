using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTrend.ScheduledTask;

/// <summary>
/// Persisted to {DataFolderPath}/trending.json between sync runs.
/// The API controller reads this file instead of re-querying the library on every request.
/// </summary>
public sealed class TrendingCache
{
    /// <summary>
    /// Gets or sets the list of trending cache entries.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1002", Justification = "Settable List required for System.Text.Json round-tripping of the cache file.")]
    [SuppressMessage("Microsoft.Usage", "CA2227", Justification = "Settable List required for System.Text.Json round-tripping of the cache file.")]
    public List<TrendingCacheEntry> Items { get; set; } = new();

    /// <summary>
    /// Sets the legacy item ids, used to migrate older cache files.
    /// </summary>
    [JsonPropertyName("ItemIds")]
    [SuppressMessage("Microsoft.Design", "CA1002", Justification = "Settable List required for deserializing legacy cache files.")]
    [SuppressMessage("Microsoft.Usage", "CA2227", Justification = "Settable List required for deserializing legacy cache files.")]
    [SuppressMessage("Microsoft.Design", "CA1044", Justification = "Private getter keeps legacy data write-only while remaining serializable.")]
    public List<Guid> LegacyItemIds { private get; set; } = new();

    /// <summary>
    /// Gets or sets the timestamp of the last cache update.
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Migrates legacy caches (which only stored item ids) into full entries.
    /// </summary>
    public void Normalize()
    {
        if (Items.Count != 0)
        {
            return;
        }

        if (LegacyItemIds.Count == 0)
        {
            return;
        }

        Items = LegacyItemIds.Select(static id => new TrendingCacheEntry
        {
            ItemId = id,
            MediaType = TrendingMediaType.Movie
        }).ToList();
    }
}
