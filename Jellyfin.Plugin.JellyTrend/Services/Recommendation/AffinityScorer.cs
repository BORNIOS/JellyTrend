using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Scores an unwatched title against the <em>stored</em> taste profile instead of against the raw watch
/// history.
/// </summary>
/// <remarks>
/// <para>
/// The difference with <see cref="RecommendationScorer"/> is where the taste comes from. There the profile
/// is rebuilt on every run from the history and knows four families (genre, tag, studio, people by id);
/// here it is read from what layer 2 already learned, so nine families take part (the four plus director,
/// actor, writer, collection and decade) and the combined affinities are counted too.
/// </para>
/// <para>
/// Each family scores as "how much of what this title is made of does the user like", a ratio in 0-1, and
/// the families are averaged with the agreed distribution (the same one layer 2 used to learn, so no
/// family can gain influence just by having more values). The combined affinities then add a bounded
/// bonus: taste usually lives in the pair ("terror + ciencia ficcion"), and a title that hits one of the
/// user's pairs is not just a title that hits two of their values.
/// </para>
/// </remarks>
internal static class AffinityScorer
{
    /// <summary>Most a matched pair of affinities can add on top of the single ones.</summary>
    internal const double PairBonusFactor = 0.15d;

    /// <summary>Families scored, in the order of the agreed distribution.</summary>
    internal static readonly IReadOnlyList<string> Families =
    [
        "genre",
        "tag",
        "studio",
        "director",
        "actor",
        "writer",
        "collection",
        "decade",
        "rating"
    ];

    /// <summary>Cast positions that count, the same cap layer 2 applies when learning.</summary>
    private const int MainCast = 4;

    /// <summary>Genres per title used to build combined affinities, the same cap as layer 2.</summary>
    private const int PairGenres = 2;

    /// <summary>
    /// Scores one candidate against the stored profile.
    /// </summary>
    /// <param name="movie">Candidate movie.</param>
    /// <param name="features">Facets of the movie, taken from the feature cache.</param>
    /// <param name="profile">Stored taste profile of the user.</param>
    /// <param name="nowUtc">Current UTC time.</param>
    /// <returns>The scored candidate.</returns>
    public static ScoredCandidate Score(
        CandidateItem movie,
        ItemFeatures features,
        AffinityProfile profile,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(profile);

        var taste = Similarity(features, profile);
        var quality = features.CommunityRating is { } rating
            ? Math.Clamp((rating - RecommendationScorer.RatingFloor) / (10d - RecommendationScorer.RatingFloor), 0d, 1d)
            : RecommendationScorer.NeutralQuality;
        var freshness = features.PremiereDate is { } premiere && premiere > nowUtc.AddYears(-RecommendationScorer.FreshnessYears)
            ? 1d
            : 0d;

        var score = ((RecommendationScorer.TasteWeight * taste)
            + (RecommendationScorer.QualityWeight * quality)
            + (RecommendationScorer.FreshnessWeight * freshness))
            / (RecommendationScorer.TasteWeight + RecommendationScorer.QualityWeight + RecommendationScorer.FreshnessWeight);

        return new ScoredCandidate(movie, score, taste, quality);
    }

    /// <summary>
    /// Similarity of a title with the stored profile.
    /// </summary>
    /// <param name="features">Facets of the movie.</param>
    /// <param name="profile">Stored taste profile of the user.</param>
    /// <returns>Value between 0 and 1.</returns>
    internal static double Similarity(ItemFeatures features, AffinityProfile profile)
    {
        double matched = 0;
        double available = 0;

        foreach (var family in Families)
        {
            var values = ValuesOf(features, family);
            if (values.Count == 0 || !profile.HasFacet(family))
            {
                continue;
            }

            var weight = TasteProfileBuilder.FacetWeights.TryGetValue(family, out var facetWeight) ? facetWeight : 0d;
            if (weight <= 0d)
            {
                continue;
            }

            available += weight;

            double likes = 0;
            foreach (var value in values)
            {
                likes += profile.WeightOf(family, value);
            }

            matched += weight * Math.Clamp(likes / values.Count, 0d, 1d);
        }

        if (available <= 0d)
        {
            return 0d;
        }

        var taste = matched / available;
        return Math.Clamp(taste * (1d + (PairBonusFactor * PairRatio(features, profile))), 0d, 1d);
    }

    /// <summary>
    /// Share of the title's combined affinities that are also in the profile.
    /// </summary>
    /// <param name="features">Facets of the movie.</param>
    /// <param name="profile">Stored taste profile of the user.</param>
    /// <returns>Value between 0 and 1; 0 when the title has no pair to match.</returns>
    internal static double PairRatio(ItemFeatures features, AffinityProfile profile)
    {
        if (profile.PairCount == 0)
        {
            return 0d;
        }

        var genres = features.Genres
            .Where(static genre => !string.IsNullOrWhiteSpace(genre))
            .Take(PairGenres)
            .ToList();
        var director = features.Directors.FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name));
        var actor = features.Actors.FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name));

        double total = 0;
        double hits = 0;

        for (var i = 0; i < genres.Count; i++)
        {
            for (var j = i + 1; j < genres.Count; j++)
            {
                total++;
                if (profile.HasPair("genre", genres[i], "genre", genres[j]))
                {
                    hits++;
                }
            }

            if (director is not null)
            {
                total++;
                if (profile.HasPair("genre", genres[i], "director", director))
                {
                    hits++;
                }
            }

            if (actor is not null)
            {
                total++;
                if (profile.HasPair("genre", genres[i], "actor", actor))
                {
                    hits++;
                }
            }
        }

        return total > 0 ? hits / total : 0d;
    }

    // The values of a title for one family, named the way layer 2 stored them.
    private static IReadOnlyList<string> ValuesOf(ItemFeatures features, string family) => family switch
    {
        "genre" => features.Genres,
        "tag" => features.Tags,
        "studio" => features.Studios,
        "director" => features.Directors,
        "actor" => features.Actors.Take(MainCast).ToList(),
        "writer" => features.Writers,
        "collection" => features.Collection is { } collection ? [collection] : [],
        "decade" => FacetLabels.DecadeOf(features.PremiereDate) is { } decade ? [decade] : [],
        "rating" => FacetLabels.RatingBucket(features.CommunityRating) is { } rating ? [rating] : [],
        _ => []
    };
}
