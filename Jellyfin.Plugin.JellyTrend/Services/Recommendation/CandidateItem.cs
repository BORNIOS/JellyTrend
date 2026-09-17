using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// An unwatched movie offered to the scorer, with the facets used to compare it against the taste
/// profile plus the keys used to de-duplicate releases and to limit same-franchise repetition.
/// </summary>
internal sealed class CandidateItem
{
    private static readonly Regex TrailingYearRegex = new(@"\s*\(\d{4}\)$|\s+\d{4}$", RegexOptions.Compiled);
    private static readonly Regex TrailingRomanRegex = new(@"\s+(?=[ivxlcdm]+$)(?:m{0,4}(?:cm|cd|d?c{0,3})?(?:xc|xl|l?x{0,3})?(?:ix|iv|v?i{0,3})?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="CandidateItem"/> class.
    /// </summary>
    /// <param name="id">Movie id.</param>
    /// <param name="name">Movie name.</param>
    /// <param name="tmdbId">TMDB provider id, or <see langword="null"/>.</param>
    /// <param name="genres">Genre names.</param>
    /// <param name="tags">Tag names.</param>
    /// <param name="studios">Studio names.</param>
    /// <param name="people">Ids of the cast and crew that matter.</param>
    /// <param name="communityRating">Community rating (0-10), or <see langword="null"/>.</param>
    /// <param name="premiereDate">Premiere date in UTC, or <see langword="null"/>.</param>
    public CandidateItem(
        Guid id,
        string name,
        string? tmdbId,
        IReadOnlyList<string> genres,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> studios,
        IReadOnlyList<Guid> people,
        float? communityRating,
        DateTime? premiereDate)
    {
        Id = id;
        Name = name ?? string.Empty;
        TmdbId = tmdbId;
        Genres = genres;
        Tags = tags;
        Studios = studios;
        People = people;
        CommunityRating = communityRating;
        PremiereDate = premiereDate;
        DedupeKey = string.IsNullOrWhiteSpace(tmdbId) ? "item:" + id.ToString("N") : "tmdb:" + tmdbId;
        FranchiseKey = BuildFranchiseKey(Name, id);
        TraitKeys = BuildTraitKeys(genres, tags, people);
    }

    /// <summary>Gets the movie id.</summary>
    public Guid Id { get; }

    /// <summary>Gets the movie name.</summary>
    public string Name { get; }

    /// <summary>Gets the TMDB provider id, when available.</summary>
    public string? TmdbId { get; }

    /// <summary>Gets the genre names.</summary>
    public IReadOnlyList<string> Genres { get; }

    /// <summary>Gets the tag names.</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>Gets the studio names.</summary>
    public IReadOnlyList<string> Studios { get; }

    /// <summary>Gets the person ids that count for scoring.</summary>
    public IReadOnlyList<Guid> People { get; }

    /// <summary>Gets the community rating.</summary>
    public float? CommunityRating { get; }

    /// <summary>Gets the premiere date in UTC.</summary>
    public DateTime? PremiereDate { get; }

    /// <summary>Gets the key that identifies the same movie across different library entries.</summary>
    public string DedupeKey { get; }

    /// <summary>Gets the key that groups sequels and remakes of the same franchise.</summary>
    public string FranchiseKey { get; }

    /// <summary>Gets the genre, tag and person traits used to measure how similar two movies are.</summary>
    public IReadOnlySet<string> TraitKeys { get; }

    private static HashSet<string> BuildTraitKeys(
        IReadOnlyList<string> genres,
        IReadOnlyList<string> tags,
        IReadOnlyList<Guid> people)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var genre in genres)
        {
            keys.Add("g:" + genre);
        }

        foreach (var tag in tags)
        {
            keys.Add("t:" + tag);
        }

        foreach (var person in people)
        {
            keys.Add("p:" + person.ToString("N"));
        }

        return keys;
    }

    // "Saga: Parte II (2024)" and "Saga II" collapse to "saga" so a franchise cannot flood the row.
    // A plain trailing number is deliberately NOT stripped: it grouped unrelated titles such as
    // "Terror 1" and "Terror 2" into a single franchise and left the row short of items.
    private static string BuildFranchiseKey(string name, Guid id)
    {
        var normalized = name.ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return id.ToString("N");
        }

        normalized = TrailingYearRegex.Replace(normalized, string.Empty);
        var colon = normalized.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            normalized = normalized[..colon];
        }

        normalized = TrailingRomanRegex.Replace(normalized, string.Empty);
        return normalized.Trim();
    }
}
