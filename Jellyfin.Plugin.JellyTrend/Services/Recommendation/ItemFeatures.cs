using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// The per-item data the engine needs but that belongs to the item, not to the user: the cast and
/// crew, the genre list, the tags and the studios. Caching it lets a run (and the next ones) avoid
/// reading the same facets once per user.
/// </summary>
/// <remarks>
/// The cast is kept twice on purpose: by id (what the history-based profile needs to weigh people) and
/// by name (what the stored profile remembers, since a profile outlives the ids of one library).
/// </remarks>
/// <param name="Genres">Genre names.</param>
/// <param name="Tags">Tag names.</param>
/// <param name="Studios">Studio names.</param>
/// <param name="People">Ids of the relevant cast and crew.</param>
/// <param name="CommunityRating">Community rating (0-10), or null.</param>
/// <param name="PremiereDate">Premiere date in UTC, or null.</param>
/// <param name="Directors">Names of the directors.</param>
/// <param name="Actors">Names of the main cast.</param>
/// <param name="Writers">Names of the writers.</param>
/// <param name="Collection">Name of the collection the title belongs to, or null.</param>
internal sealed record ItemFeatures(
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Studios,
    IReadOnlyList<Guid> People,
    float? CommunityRating,
    DateTime? PremiereDate,
    IReadOnlyList<string> Directors,
    IReadOnlyList<string> Actors,
    IReadOnlyList<string> Writers,
    string? Collection)
{
    /// <summary>
    /// Gets a value indicating whether the entry carries the role-separated cast, or comes from a cache
    /// written by an older version of the plugin and has to be read again.
    /// </summary>
    public bool HasRoles => Directors.Count > 0 || Actors.Count > 0 || Writers.Count > 0;
}
