using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyTrend.Api;

/// <summary>
/// Lightweight wrapper around a watched item used to aggregate affinity facets
/// (genres, tags, studios, people) from the user's play history.
/// Supports construction from both a full <see cref="BaseItem"/> (SQLite fallback)
/// and a <see cref="RecommendationItem"/> projection (optimised provider path).
/// </summary>
internal sealed class AffinityItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AffinityItem"/> class from a full <see cref="BaseItem"/> (ILibraryManager fallback).
    /// </summary>
    /// <param name="item">The full library item.</param>
    public AffinityItem(BaseItem item)
    {
        Id = item.Id;
        Genres = item.Genres ?? [];
        Tags = item.Tags ?? [];
        Studios = item.Studios ?? [];
        BaseItem = item;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AffinityItem"/> class from a lightweight <see cref="RecommendationItem"/> projection.
    /// </summary>
    /// <param name="projection">The lightweight projection from the optimised provider path.</param>
    public AffinityItem(RecommendationItem projection)
    {
        Id = projection.Id;
        Genres = [.. projection.Genres];
        Tags = [.. projection.Tags];
        Studios = [.. projection.Studios];
        BaseItem = null; // people data not available in the lightweight projection
    }

    /// <summary>Gets the item GUID.</summary>
    public Guid Id { get; }

    /// <summary>Gets genre names.</summary>
    public string[] Genres { get; }

    /// <summary>Gets tag names.</summary>
    public string[] Tags { get; }

    /// <summary>Gets studio names.</summary>
    public string[] Studios { get; }

    /// <summary>
    /// Gets the underlying <see cref="BaseItem"/>, or <see langword="null"/> when constructed
    /// from a lightweight projection. Used to load person data via <c>ILibraryManager.GetPeople</c>.
    /// </summary>
    public BaseItem? BaseItem { get; }
}
