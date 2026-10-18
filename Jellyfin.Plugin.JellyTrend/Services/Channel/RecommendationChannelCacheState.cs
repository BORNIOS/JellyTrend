using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellyTrend.Services.Channel;

/// <summary>
/// Keeps a lightweight, per-user revision for the Recommendations channel cache.
/// </summary>
/// <remarks>
/// Jellyfin persists channel responses by <see cref="MediaBrowser.Controller.Channels.IHasCacheKey"/>.
/// A playback update must therefore change that key immediately; otherwise the channel can keep
/// returning a cached row until the next configured rotation window.
/// </remarks>
internal static class RecommendationChannelCacheState
{
    private static readonly ConcurrentDictionary<Guid, long> Revisions = new();

    /// <summary>Gets the current cache revision for one user.</summary>
    /// <param name="userId">The Jellyfin user identifier.</param>
    /// <returns>The revision incorporated into that user's channel cache key.</returns>
    public static long GetRevision(Guid userId)
        => userId == Guid.Empty ? 0 : Revisions.TryGetValue(userId, out var revision) ? revision : 0;

    /// <summary>
    /// Invalidates a user's channel cache only when the changed library item belongs to that
    /// user's stored recommendation pool.
    /// </summary>
    /// <param name="userId">The Jellyfin user whose playback state changed.</param>
    /// <param name="itemId">The source library item identifier, never a channel-shadow identifier.</param>
    /// <returns><c>true</c> when the cache revision changed.</returns>
    public static bool InvalidateCandidate(Guid userId, Guid itemId)
    {
        if (userId == Guid.Empty || itemId == Guid.Empty)
        {
            return false;
        }

        var recommendations = RecommendationStorage.Read(userId);
        if (recommendations is null || !recommendations.ItemIds.Contains(itemId))
        {
            return false;
        }

        Revisions.AddOrUpdate(userId, 1, static (_, revision) => revision + 1);
        return true;
    }
}
