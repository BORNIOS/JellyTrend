using System;
using System.Collections.Generic;
using System.IO;

using Jellyfin.Plugin.JellyTrend.Services;
using Jellyfin.Plugin.JellyTrend.Services.Channel;
using Jellyfin.Plugin.JellyTrend.Services.Models;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// Verifies that only a consumed recommendation changes the per-user channel cache key revision.
/// </summary>
[Collection(JellyTrendStorageStateCollection.Name)]
public sealed class RecommendationChannelCacheStateTests
{
    /// <summary>
    /// A watched candidate must invalidate its user's cached channel response, while an unrelated
    /// playback must leave the key stable.
    /// </summary>
    [Fact]
    public void CandidatePlaybackChangesOnlyThatUsersCacheRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), "jellytrend-cache-" + Path.GetRandomFileName());
        var userId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var unrelatedId = Guid.NewGuid();

        try
        {
            JellyTrendStorage.Initialize(Path.Combine(root, "data"));
            RecommendationStorage.Write(userId, new UserRecommendations
            {
                ItemIds = [candidateId],
                Scores = [1.0],
            });

            var initial = RecommendationChannelCacheState.GetRevision(userId);
            Assert.False(RecommendationChannelCacheState.InvalidateCandidate(userId, unrelatedId));
            Assert.Equal(initial, RecommendationChannelCacheState.GetRevision(userId));

            Assert.True(RecommendationChannelCacheState.InvalidateCandidate(userId, candidateId));
            Assert.Equal(initial + 1, RecommendationChannelCacheState.GetRevision(userId));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
