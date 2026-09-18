using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// The user's taste as a probability distribution over facets: how much of the profile each genre,
/// tag, studio and person accounts for. Weights are engagement-weighted and IDF-scaled, then
/// normalized per family, so a genre the user watches 75% of the time cannot be outranked by a
/// single stray title the way raw counting allowed.
/// </summary>
internal sealed class TasteProfile
{
    private readonly Dictionary<string, double> _genreShares;
    private readonly Dictionary<string, double> _tagShares;
    private readonly Dictionary<string, double> _studioShares;
    private readonly Dictionary<Guid, double> _personShares;

    private TasteProfile(
        Dictionary<string, double> genreShares,
        Dictionary<string, double> tagShares,
        Dictionary<string, double> studioShares,
        Dictionary<Guid, double> personShares,
        int itemCount)
    {
        _genreShares = genreShares;
        _tagShares = tagShares;
        _studioShares = studioShares;
        _personShares = personShares;
        ItemCount = itemCount;
    }

    /// <summary>Gets the number of watched movies that fed the profile.</summary>
    public int ItemCount { get; }

    /// <summary>Gets the profile share of every genre, used to derive the per-genre quotas.</summary>
    public IReadOnlyDictionary<string, double> GenreDistribution => _genreShares;

    /// <summary>Gets the number of people the profile tracks.</summary>
    public int PersonCount => _personShares.Count;

    /// <summary>Gets a value indicating whether the profile has genre information.</summary>
    public bool HasGenres => _genreShares.Count > 0;

    /// <summary>Gets a value indicating whether the profile has tag information.</summary>
    public bool HasTags => _tagShares.Count > 0;

    /// <summary>Gets a value indicating whether the profile has studio information.</summary>
    public bool HasStudios => _studioShares.Count > 0;

    /// <summary>Gets a value indicating whether the profile has people information.</summary>
    public bool HasPeople => _personShares.Count > 0;

    /// <summary>
    /// Builds the profile from the user's watched movies.
    /// </summary>
    /// <param name="items">Watched movies with their engagement weight.</param>
    /// <param name="index">Facet index used for the IDF scaling.</param>
    /// <returns>The taste profile.</returns>
    public static TasteProfile Build(IReadOnlyList<TasteItem> items, FacetIndex index)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(index);

        var genreWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var tagWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var studioWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var personWeights = new Dictionary<Guid, double>();

        foreach (var item in items)
        {
            if (item.Weight <= 0)
            {
                continue;
            }

            Accumulate(genreWeights, item.Genres, item.Weight, index.GenreIdf);
            Accumulate(studioWeights, item.Studios, item.Weight, index.StudioIdf);

            foreach (var tag in item.Tags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    tagWeights[tag] = tagWeights.GetValueOrDefault(tag) + (item.Weight * index.TagIdf(tag));
                }
            }

            foreach (var person in item.People)
            {
                personWeights[person] = personWeights.GetValueOrDefault(person) + (item.Weight * index.PersonIdf(person));
            }
        }

        return new TasteProfile(
            ToShares(genreWeights),
            ToShares(tagWeights),
            ToShares(studioWeights),
            ToShares(personWeights),
            items.Count);
    }

    /// <summary>
    /// Builds the profile the selector needs (per-genre quotas, "which genre identifies this movie for
    /// this user") from the profile layer 2 already stored, without touching the watch history.
    /// </summary>
    /// <param name="profile">Stored taste profile.</param>
    /// <param name="itemCount">Number of consumed titles behind the profile.</param>
    /// <returns>The taste profile used for selection.</returns>
    public static TasteProfile FromAffinities(AffinityProfile profile, int itemCount)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new TasteProfile(
            Shares(profile, "genre"),
            Shares(profile, "tag"),
            Shares(profile, "studio"),
            [],
            itemCount);

        static Dictionary<string, double> Shares(AffinityProfile source, string facet)
            => source.Distribution(facet).ToDictionary(
                static entry => entry.Key,
                static entry => entry.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets the share of the profile accounted for by a genre.</summary>
    /// <param name="genre">Genre name.</param>
    /// <returns>Value between 0 and 1.</returns>
    public double GenreShare(string genre) => _genreShares.GetValueOrDefault(genre);

    /// <summary>Gets the share of the profile accounted for by a tag.</summary>
    /// <param name="tag">Tag name.</param>
    /// <returns>Value between 0 and 1.</returns>
    public double TagShare(string tag) => _tagShares.GetValueOrDefault(tag);

    /// <summary>Gets the share of the profile accounted for by a studio.</summary>
    /// <param name="studio">Studio name.</param>
    /// <returns>Value between 0 and 1.</returns>
    public double StudioShare(string studio) => _studioShares.GetValueOrDefault(studio);

    /// <summary>Gets the share of the profile accounted for by a person.</summary>
    /// <param name="personId">Person id.</param>
    /// <returns>Value between 0 and 1.</returns>
    public double PersonShare(Guid personId) => _personShares.GetValueOrDefault(personId);

    /// <summary>Gets the strongest genres of the profile, for diagnostics.</summary>
    /// <param name="count">Maximum number of genres to return.</param>
    /// <returns>Genre name and share pairs, strongest first.</returns>
    public IReadOnlyList<KeyValuePair<string, double>> TopGenres(int count)
        => Top(_genreShares, count);

    /// <summary>Gets the strongest tags of the profile.</summary>
    /// <param name="count">Maximum number of tags to return.</param>
    /// <returns>Tag name and share pairs, strongest first.</returns>
    public IReadOnlyList<KeyValuePair<string, double>> TopTags(int count)
        => Top(_tagShares, count);

    /// <summary>Gets the strongest people of the profile.</summary>
    /// <param name="count">Maximum number of people to return.</param>
    /// <returns>Person id and share pairs, strongest first.</returns>
    public IReadOnlyList<KeyValuePair<Guid, double>> TopPeople(int count)
        => _personShares
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(count)
            .ToList();

    private static List<KeyValuePair<string, double>> Top(Dictionary<string, double> shares, int count)
        => shares
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();

    private static void Accumulate(
        Dictionary<string, double> weights,
        IReadOnlyList<string> facets,
        double itemWeight,
        Func<string, double> idf)
    {
        foreach (var facet in facets)
        {
            if (!string.IsNullOrWhiteSpace(facet))
            {
                weights[facet] = weights.GetValueOrDefault(facet) + (itemWeight * idf(facet));
            }
        }
    }

    private static Dictionary<TKey, double> ToShares<TKey>(Dictionary<TKey, double> weights)
        where TKey : notnull
    {
        var total = weights.Values.Sum();
        if (total <= 0)
        {
            return [];
        }

        var shares = new Dictionary<TKey, double>(weights.Count);
        foreach (var pair in weights)
        {
            shares[pair.Key] = pair.Value / total;
        }

        return shares;
    }
}
