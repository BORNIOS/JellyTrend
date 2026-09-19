using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Document frequency per facet over the universe of movies under consideration, used to weight
/// facets by rarity (IDF). A genre present in half the library says very little about taste, while a
/// tag appearing in a handful of titles is highly discriminative, so the former must weigh less.
/// </summary>
internal sealed class FacetIndex
{
    private readonly Dictionary<string, int> _genreCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _tagCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _studioCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, int> _personCounts = [];

    /// <summary>Gets the number of movies indexed.</summary>
    public int MovieCount { get; private set; }

    /// <summary>Adds one movie and its facets to the index.</summary>
    /// <param name="genres">Genre names.</param>
    /// <param name="tags">Tag names.</param>
    /// <param name="studios">Studio names.</param>
    /// <param name="people">Person ids.</param>
    public void Add(
        IReadOnlyList<string> genres,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> studios,
        IReadOnlyList<Guid> people)
    {
        MovieCount++;
        CountValues(_genreCounts, genres);
        CountValues(_tagCounts, tags);
        CountValues(_studioCounts, studios);

        foreach (var person in people)
        {
            _personCounts[person] = _personCounts.GetValueOrDefault(person) + 1;
        }
    }

    /// <summary>Gets the inverse document frequency of a genre.</summary>
    /// <param name="genre">Genre name.</param>
    /// <returns>The IDF weight, always positive.</returns>
    public double GenreIdf(string genre) => Idf(_genreCounts.GetValueOrDefault(genre));

    /// <summary>Gets the inverse document frequency of a tag.</summary>
    /// <param name="tag">Tag name.</param>
    /// <returns>The IDF weight, always positive.</returns>
    public double TagIdf(string tag) => Idf(_tagCounts.GetValueOrDefault(tag));

    /// <summary>Gets the inverse document frequency of a studio.</summary>
    /// <param name="studio">Studio name.</param>
    /// <returns>The IDF weight, always positive.</returns>
    public double StudioIdf(string studio) => Idf(_studioCounts.GetValueOrDefault(studio));

    /// <summary>Gets the inverse document frequency of a person.</summary>
    /// <param name="personId">Person id.</param>
    /// <returns>The IDF weight, always positive.</returns>
    public double PersonIdf(Guid personId) => Idf(_personCounts.GetValueOrDefault(personId));

    // log(1 + N / (1 + n)): monotonic decreasing in n (rarer facets weigh more) and bounded below by 0,
    // so a facet present in every movie of the library contributes nothing but never subtracts.
    private double Idf(int documentCount)
        => Math.Log(1d + (MovieCount / (double)(1 + documentCount)));

    private static void CountValues(Dictionary<string, int> map, IReadOnlyList<string> values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                map[value] = map.GetValueOrDefault(value) + 1;
            }
        }
    }
}
