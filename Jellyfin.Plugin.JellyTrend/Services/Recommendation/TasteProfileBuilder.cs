using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Layer 2: turns the aggregated consumption into a taste profile (affinities per facet and combined).
/// </summary>
/// <remarks>
/// <para>
/// The facet weights are the agreed distribution and add up to 1.00. They describe <b>taste</b>: what the
/// content is made of. How much each title contributes is decided outside, by
/// <see cref="InteractionWeight"/>, so a title watched three times and favourited teaches roughly twice
/// as much as one watched once. Quality signals (rating, popularity) carry the smallest weights on
/// purpose: they break ties between close affinities, they never justify recommending something the user
/// has no affinity with.
/// </para>
/// <para>
/// Two safeguards from the specification are built in: the main cast is <b>saturated</b>
/// (<c>1 - e^-x</c>), so five known actors help without multiplying the score, and combined affinities
/// ("horror + science fiction", "Fincher + crime") are stored, because taste often lives in the pair and
/// not in the single value.
/// </para>
/// </remarks>
public static class TasteProfileBuilder
{
    /// <summary>Weight of each facet. The distribution adds up to 1.00.</summary>
    public static readonly IReadOnlyDictionary<string, double> FacetWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["genre"] = 0.22,
        ["director"] = 0.16,
        ["actor"] = 0.14,
        ["tag"] = 0.14,
        ["collection"] = 0.07,
        ["writer"] = 0.06,
        ["studio"] = 0.05,
        ["decade"] = 0.05,
        ["language"] = 0.03,
        ["rating"] = 0.03,
        ["popularity"] = 0.03,
        ["runtime"] = 0.02
    };

    /// <summary>How much more a combined affinity counts than the average of its two facets.</summary>
    private const double PairBonus = 1.15;

    /// <summary>Cast positions that count: a big cast must not dominate the profile.</summary>
    private const int MainCast = 4;

    /// <summary>Genres per title used to build combined affinities.</summary>
    private const int PairGenres = 2;

    /// <summary>Weight used for a facet that is not in the distribution.</summary>
    private const double UnknownFacetWeight = 0.01;

    /// <summary>
    /// Builds the profile of a user from the titles they consumed.
    /// </summary>
    /// <param name="items">Consumed titles with their content facets and their interaction weight.</param>
    /// <returns>The affinities, strongest first, normalized so the strongest one is 1.</returns>
    public static IReadOnlyList<AffinityRecord> Build(IReadOnlyList<ProfileItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return [];
        }

        var singles = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        var pairs = new Dictionary<string, double>(StringComparer.Ordinal);
        var pairLabels = new Dictionary<string, (string Facet, string Value, string PairedFacet, string PairedValue)>(StringComparer.Ordinal);
        var actorTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var magnitude = Math.Abs(item.Weight);
            if (magnitude == 0d)
            {
                continue;
            }

            Add(singles, "genre", item.Genres, magnitude);
            Add(singles, "tag", item.Tags, magnitude);
            Add(singles, "studio", item.Studios, magnitude);
            Add(singles, "director", item.Directors, magnitude);
            Add(singles, "writer", item.Writers, magnitude);
            Add(singles, "collection", item.Collection is null ? [] : [item.Collection], magnitude);
            Add(singles, "decade", item.Decade is null ? [] : [item.Decade], magnitude);
            Add(singles, "language", item.Language is null ? [] : [item.Language], magnitude);
            Add(singles, "rating", Bucket(item.Rating), magnitude);
            Add(singles, "runtime", RuntimeBucket(item.Runtime), magnitude);

            // La saturacion se aplica al final: aqui solo se acumula cuanto reparto conocido hay.
            foreach (var actor in item.Actors.Take(MainCast))
            {
                if (!string.IsNullOrWhiteSpace(actor))
                {
                    actorTotals[actor] = actorTotals.GetValueOrDefault(actor) + magnitude;
                }
            }

            AddPairs(pairs, pairLabels, item, magnitude);
        }

        foreach (var actor in actorTotals)
        {
            Accumulate(singles, "actor", actor.Key, 1 - Math.Exp(-actor.Value));
        }

        var records = new List<AffinityRecord>();

        foreach (var facet in singles)
        {
            var facetWeight = WeightOf(facet.Key);
            foreach (var value in facet.Value)
            {
                records.Add(new AffinityRecord
                {
                    Facet = facet.Key,
                    Value = value.Key,
                    Weight = value.Value * facetWeight
                });
            }
        }

        foreach (var pair in pairs)
        {
            var label = pairLabels[pair.Key];
            var facetWeight = (WeightOf(label.Facet) + WeightOf(label.PairedFacet)) / 2;
            records.Add(new AffinityRecord
            {
                Facet = label.Facet,
                Value = label.Value,
                PairedFacet = label.PairedFacet,
                PairedValue = label.PairedValue,
                Weight = pair.Value * facetWeight * PairBonus
            });
        }

        var strongest = records.Count == 0 ? 0d : records.Max(static record => Math.Abs(record.Weight));
        if (strongest > 0d)
        {
            foreach (var record in records)
            {
                record.Weight = Math.Round(record.Weight / strongest, 2);
            }
        }

        return [.. records.OrderByDescending(static record => record.Weight)];
    }

    private static double WeightOf(string facet)
        => FacetWeights.TryGetValue(facet, out var weight) ? weight : UnknownFacetWeight;

    private static string[] Bucket(double? rating) => FacetLabels.RatingBucket(rating) is { } label ? [label] : [];

    private static string[] RuntimeBucket(int? minutes) => FacetLabels.RuntimeBucket(minutes) is { } label ? [label] : [];

    private static void Add(
        Dictionary<string, Dictionary<string, double>> target,
        string facet,
        IReadOnlyList<string> values,
        double weight)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Accumulate(target, facet, value, weight);
            }
        }
    }

    private static void Accumulate(
        Dictionary<string, Dictionary<string, double>> target,
        string facet,
        string value,
        double weight)
    {
        if (!target.TryGetValue(facet, out var values))
        {
            values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            target[facet] = values;
        }

        values[value] = values.GetValueOrDefault(value) + weight;
    }

    private static void AddPairs(
        Dictionary<string, double> pairs,
        Dictionary<string, (string Facet, string Value, string PairedFacet, string PairedValue)> labels,
        ProfileItem item,
        double magnitude)
    {
        var genres = item.Genres.Where(static genre => !string.IsNullOrWhiteSpace(genre)).Take(PairGenres).ToList();

        for (var i = 0; i < genres.Count; i++)
        {
            for (var j = i + 1; j < genres.Count; j++)
            {
                AddPair(pairs, labels, "genre", genres[i], "genre", genres[j], magnitude);
            }
        }

        var leadDirector = item.Directors.FirstOrDefault(static director => !string.IsNullOrWhiteSpace(director));
        var leadActor = item.Actors.FirstOrDefault(static actor => !string.IsNullOrWhiteSpace(actor));

        foreach (var genre in genres)
        {
            if (leadDirector is not null)
            {
                AddPair(pairs, labels, "genre", genre, "director", leadDirector, magnitude);
            }

            if (leadActor is not null)
            {
                AddPair(pairs, labels, "genre", genre, "actor", leadActor, magnitude);
            }
        }
    }

    private static void AddPair(
        Dictionary<string, double> pairs,
        Dictionary<string, (string Facet, string Value, string PairedFacet, string PairedValue)> labels,
        string facet,
        string value,
        string pairedFacet,
        string pairedValue,
        double weight)
    {
        // El par no tiene orden: "terror + ciencia ficcion" es el mismo par en cualquier orden.
        var first = (Facet: facet, Value: value);
        var second = (Facet: pairedFacet, Value: pairedValue);

        if (string.CompareOrdinal($"{first.Facet}|{first.Value}", $"{second.Facet}|{second.Value}") > 0)
        {
            (first, second) = (second, first);
        }

        var key = $"{first.Facet}|{first.Value}|{second.Facet}|{second.Value}";
        pairs[key] = pairs.GetValueOrDefault(key) + weight;
        labels[key] = (first.Facet, first.Value, second.Facet, second.Value);
    }
}
