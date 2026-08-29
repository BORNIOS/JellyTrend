using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyTrend.Api;

/// <summary>
/// Lightweight wrapper around a candidate item used during the scoring phase of
/// <see cref="RecommendationEngine"/>.
/// Supports construction from both a full <see cref="BaseItem"/> (SQLite fallback)
/// and a <see cref="RecommendationItem"/> projection (optimised provider path).
/// </summary>
internal sealed class ScoringItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ScoringItem"/> class from a full <see cref="BaseItem"/> (ILibraryManager fallback).
    /// </summary>
    /// <param name="item">The full library item.</param>
    public ScoringItem(BaseItem item)
    {
        Id = item.Id;
        Genres = item.Genres ?? [];
        Tags = item.Tags ?? [];
        Studios = item.Studios ?? [];
        CommunityRating = item.CommunityRating;
        PremiereDate = item.PremiereDate;
        DedupeKey = item.ProviderIds.TryGetValue("Tmdb", out var tmdb) && !string.IsNullOrWhiteSpace(tmdb)
            ? "tmdb:" + tmdb
            : "item:" + item.Id.ToString("N");
        BaseItem = item;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ScoringItem"/> class from a lightweight <see cref="RecommendationItem"/> projection.
    /// </summary>
    /// <param name="projection">The lightweight projection from the optimised provider path.</param>
    public ScoringItem(RecommendationItem projection)
    {
        Id = projection.Id;
        Genres = [.. projection.Genres];
        Tags = [.. projection.Tags];
        Studios = [.. projection.Studios];
        CommunityRating = projection.CommunityRating;
        PremiereDate = projection.PremiereDate;
        DedupeKey = !string.IsNullOrWhiteSpace(projection.TmdbId)
            ? "tmdb:" + projection.TmdbId
            : "item:" + projection.Id.ToString("N");
        BaseItem = null; // full BaseItem not available in the projection
    }

    /// <summary>Gets the item GUID.</summary>
    public Guid Id { get; }

    /// <summary>Gets genre names.</summary>
    public string[] Genres { get; }

    /// <summary>Gets tag names.</summary>
    public string[] Tags { get; }

    /// <summary>Gets studio names.</summary>
    public string[] Studios { get; }

    /// <summary>Gets the community rating (0-10), or <see langword="null"/> if unavailable.</summary>
    public float? CommunityRating { get; }

    /// <summary>Gets the premiere date, or <see langword="null"/> if unavailable.</summary>
    public DateTime? PremiereDate { get; }

    /// <summary>
    /// Gets the deduplication key — TMDB-based when available so different editions of
    /// the same film don't appear twice in the result list.
    /// </summary>
    public string DedupeKey { get; }

    /// <summary>
    /// Gets the underlying <see cref="BaseItem"/>, or <see langword="null"/> when constructed
    /// from a lightweight projection. Used to load person data via <c>ILibraryManager.GetPeople</c>.
    /// </summary>
    public BaseItem? BaseItem { get; }
}
