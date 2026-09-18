using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// The per-item data the engine needs but that belongs to the item, not to the user: the cast and
/// crew, the genre list, the tags and the studios. Caching it lets a run (and the next ones) avoid
/// reading the same facets once per user.
/// </summary>
/// <param name="Genres">Genre names.</param>
/// <param name="Tags">Tag names.</param>
/// <param name="Studios">Studio names.</param>
/// <param name="People">Ids of the relevant cast and crew.</param>
/// <param name="CommunityRating">Community rating (0-10), or null.</param>
/// <param name="PremiereDate">Premiere date in UTC, or null.</param>
internal sealed record ItemFeatures(
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Studios,
    IReadOnlyList<Guid> People,
    float? CommunityRating,
    DateTime? PremiereDate);
