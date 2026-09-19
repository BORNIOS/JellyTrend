using System;
using System.Collections.Generic;

using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.JellyTrend.Api;

/// <summary>
/// Optional database-provider-specific index backend for local id lookups.
/// </summary>
/// <remarks>
/// <para>
/// This contract is separate from <see cref="IRecommendationQueryProvider"/> on purpose. Adding members to
/// the recommendation contract would stop an already installed provider —compiled against the older
/// contract— from loading, and with it the whole optimisation. A provider that implements only the
/// recommendation contract keeps working exactly as before; this one is detected independently and the
/// trending sync uses it only when it is there.
/// </para>
/// <para>
/// <b>Contract:</b> must never throw for recoverable errors (return an empty dictionary instead) and must
/// be safe to call from a background task thread.
/// </para>
/// </remarks>
public interface ILibraryIndexQueryProvider
{
    /// <summary>
    /// Resolves TMDB ids to local library item ids in a single query, instead of one query per id.
    /// </summary>
    /// <param name="tmdbIds">TMDB ids to resolve.</param>
    /// <param name="kind">Item kind the ids belong to (movies and series share the TMDB id space).</param>
    /// <returns>TMDB id to local item id; ids without a local match are simply absent.</returns>
    IReadOnlyDictionary<string, Guid> GetItemIdsByTmdbIds(IReadOnlyList<string> tmdbIds, BaseItemKind kind);
}
