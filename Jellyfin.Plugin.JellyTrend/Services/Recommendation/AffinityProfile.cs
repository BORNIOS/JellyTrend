using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Layer 3: the stored taste profile, indexed so a candidate can be scored against it without reading
/// the watch history again.
/// </summary>
/// <remarks>
/// <para>
/// Building the profile is expensive (it walks the history and the facets of every consumed title); using
/// it is not. This type is the "using it" half: it loads the affinities once per run and answers two
/// questions per candidate — how much does the user like this value, and is this pair one of theirs.
/// </para>
/// <para>
/// Weights are never summed twice: when the same value arrives more than once the strongest wins, so the
/// lookup stays a maximum and not a total.
/// </para>
/// </remarks>
internal sealed class AffinityProfile
{
    private readonly Dictionary<string, Dictionary<string, double>> _singles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _pairs = new(StringComparer.Ordinal);

    private AffinityProfile(int count) => Count = count;

    /// <summary>Gets the number of affinities the stored profile carries.</summary>
    public int Count { get; }

    /// <summary>Gets the number of combined affinities the stored profile carries.</summary>
    public int PairCount => _pairs.Count;

    /// <summary>
    /// Indexes a stored profile.
    /// </summary>
    /// <param name="affinities">Affinities as they were read from the store.</param>
    /// <returns>The indexed profile.</returns>
    public static AffinityProfile From(IReadOnlyList<AffinityRecord> affinities)
    {
        ArgumentNullException.ThrowIfNull(affinities);

        var profile = new AffinityProfile(affinities.Count);

        foreach (var affinity in affinities)
        {
            if (string.IsNullOrWhiteSpace(affinity.Facet)
                || string.IsNullOrWhiteSpace(affinity.Value)
                || affinity.Weight <= 0d)
            {
                continue;
            }

            var facet = affinity.Facet;

            if (!string.IsNullOrWhiteSpace(affinity.PairedFacet) && !string.IsNullOrWhiteSpace(affinity.PairedValue))
            {
                var key = PairKey(facet, affinity.Value, affinity.PairedFacet, affinity.PairedValue);
                profile._pairs[key] = Math.Max(profile._pairs.GetValueOrDefault(key), affinity.Weight);
                continue;
            }

            if (!profile._singles.TryGetValue(facet, out var values))
            {
                values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                profile._singles[facet] = values;
            }

            values[affinity.Value] = Math.Max(values.GetValueOrDefault(affinity.Value), affinity.Weight);
        }

        return profile;
    }

    /// <summary>
    /// Canonical key of a combined affinity. The order must not matter: "terror + ciencia ficcion" and
    /// "ciencia ficcion + terror" are the same preference, and layer 2 stores them that way.
    /// </summary>
    /// <param name="facet">First facet.</param>
    /// <param name="value">First value.</param>
    /// <param name="pairedFacet">Second facet.</param>
    /// <param name="pairedValue">Second value.</param>
    /// <returns>The canonical key.</returns>
    public static string PairKey(string facet, string value, string pairedFacet, string pairedValue)
    {
        var first = $"{facet}|{value}";
        var second = $"{pairedFacet}|{pairedValue}";
        return string.CompareOrdinal(first, second) <= 0 ? $"{first}|{second}" : $"{second}|{first}";
    }

    /// <summary>
    /// Indicates whether the profile learned anything about a facet.
    /// </summary>
    /// <param name="facet">Facet name ("genre", "actor"...).</param>
    /// <returns><see langword="true"/> when there is at least one value for the facet.</returns>
    public bool HasFacet(string facet) => _singles.ContainsKey(facet);

    /// <summary>
    /// Gets how much the user likes a value.
    /// </summary>
    /// <param name="facet">Facet name.</param>
    /// <param name="value">Value of the facet.</param>
    /// <returns>The learned weight, normalized so the strongest preference is 1; 0 when unknown.</returns>
    public double WeightOf(string facet, string value)
        => _singles.TryGetValue(facet, out var values) ? values.GetValueOrDefault(value) : 0d;

    /// <summary>
    /// Indicates whether a pair of values is one of the user's combined preferences.
    /// </summary>
    /// <param name="facet">First facet.</param>
    /// <param name="value">First value.</param>
    /// <param name="pairedFacet">Second facet.</param>
    /// <param name="pairedValue">Second value.</param>
    /// <returns><see langword="true"/> when the pair is in the profile.</returns>
    public bool HasPair(string facet, string value, string pairedFacet, string pairedValue)
        => _pairs.ContainsKey(PairKey(facet, value, pairedFacet, pairedValue));

    /// <summary>
    /// Gets the strongest values of a facet, used to discover the candidate pool.
    /// </summary>
    /// <param name="facet">Facet name.</param>
    /// <param name="count">Maximum number of values.</param>
    /// <returns>The strongest values, strongest first.</returns>
    public IReadOnlyList<string> TopValues(string facet, int count)
    {
        if (!_singles.TryGetValue(facet, out var values))
        {
            return [];
        }

        return
        [
            .. values
                .OrderByDescending(static value => value.Value)
                .ThenBy(static value => value.Key, StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .Select(static value => value.Key)
        ];
    }

    /// <summary>
    /// Gets the share of the profile each value of a facet accounts for, normalized to add up to 1.
    /// </summary>
    /// <param name="facet">Facet name.</param>
    /// <returns>The distribution, or an empty one when the facet is unknown.</returns>
    public IReadOnlyDictionary<string, double> Distribution(string facet)
    {
        if (!_singles.TryGetValue(facet, out var values))
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        var total = values.Values.Sum();
        if (total <= 0d)
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        return values.ToDictionary(
            static value => value.Key,
            value => value.Value / total,
            StringComparer.OrdinalIgnoreCase);
    }
}
