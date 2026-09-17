using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Scores an unwatched movie against the user's taste profile.
/// </summary>
/// <remarks>
/// <para>
/// For each facet family the similarity is the share of the movie's <em>rarity-weighted</em> traits
/// that the user actually likes:
/// <c>sim = Σ share(trait)·idf(trait) / Σ idf(trait)</c>.
/// </para>
/// <para>
/// Two properties matter here. First, relevance is a ratio, not a raw count, so a movie with many
/// unrelated traits is diluted instead of boosted, and a single stray genre can no longer saturate
/// the score. Second, family weights are renormalized over the families the user actually has data
/// for, so the engine behaves identically whether the person data came from SQL or from the
/// library manager.
/// </para>
/// </remarks>
internal static class RecommendationScorer
{
    /// <summary>Weight of the taste match in the final score.</summary>
    internal const double TasteWeight = 0.68d;

    /// <summary>Weight of the community rating in the final score.</summary>
    internal const double QualityWeight = 0.22d;

    /// <summary>Weight of the "recently released" bonus in the final score.</summary>
    internal const double FreshnessWeight = 0.10d;

    /// <summary>Weight of the genre family.</summary>
    internal const double GenreFamilyWeight = 0.45d;

    /// <summary>Weight of the people family (cast and crew).</summary>
    internal const double PeopleFamilyWeight = 0.25d;

    /// <summary>Weight of the tag family.</summary>
    internal const double TagFamilyWeight = 0.20d;

    /// <summary>Weight of the studio family.</summary>
    internal const double StudioFamilyWeight = 0.10d;

    /// <summary>Rating considered neutral when normalizing quality.</summary>
    internal const double RatingFloor = 5.0d;

    /// <summary>Quality assigned to movies without a community rating.</summary>
    internal const double NeutralQuality = 0.45d;

    /// <summary>Years after which a movie stops receiving the freshness bonus.</summary>
    internal const int FreshnessYears = 3;

    /// <summary>
    /// Scores one candidate against the profile.
    /// </summary>
    /// <param name="movie">Candidate movie.</param>
    /// <param name="profile">Taste profile of the user.</param>
    /// <param name="index">Facet index providing the IDF weights.</param>
    /// <param name="nowUtc">Current UTC time.</param>
    /// <returns>The scored candidate.</returns>
    public static ScoredCandidate Score(
        CandidateItem movie,
        TasteProfile profile,
        FacetIndex index,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(index);

        double weightedSum = 0;
        double familyWeight = 0;

        if (profile.HasGenres && movie.Genres.Count > 0)
        {
            weightedSum += GenreFamilyWeight * FamilySimilarity(movie.Genres, profile.GenreShare, index.GenreIdf);
            familyWeight += GenreFamilyWeight;
        }

        if (profile.HasPeople && movie.People.Count > 0)
        {
            weightedSum += PeopleFamilyWeight * PersonSimilarity(movie.People, profile, index);
            familyWeight += PeopleFamilyWeight;
        }

        if (profile.HasTags && movie.Tags.Count > 0)
        {
            weightedSum += TagFamilyWeight * FamilySimilarity(movie.Tags, profile.TagShare, index.TagIdf);
            familyWeight += TagFamilyWeight;
        }

        if (profile.HasStudios && movie.Studios.Count > 0)
        {
            weightedSum += StudioFamilyWeight * FamilySimilarity(movie.Studios, profile.StudioShare, index.StudioIdf);
            familyWeight += StudioFamilyWeight;
        }

        var taste = familyWeight > 0 ? weightedSum / familyWeight : 0d;
        var quality = movie.CommunityRating is { } rating
            ? Math.Clamp((rating - RatingFloor) / (10d - RatingFloor), 0d, 1d)
            : NeutralQuality;
        var freshness = movie.PremiereDate is { } premiere && premiere > nowUtc.AddYears(-FreshnessYears) ? 1d : 0d;

        var score = ((TasteWeight * taste) + (QualityWeight * quality) + (FreshnessWeight * freshness))
            / (TasteWeight + QualityWeight + FreshnessWeight);

        return new ScoredCandidate(movie, score, taste, quality);
    }

    /// <summary>
    /// Similarity of a movie's traits with one facet family of the profile.
    /// </summary>
    /// <param name="facets">Trait names of the movie.</param>
    /// <param name="share">Profile share lookup.</param>
    /// <param name="idf">IDF lookup.</param>
    /// <returns>Value between 0 and 1.</returns>
    internal static double FamilySimilarity(
        IReadOnlyList<string> facets,
        Func<string, double> share,
        Func<string, double> idf)
    {
        double numerator = 0;
        double denominator = 0;

        foreach (var facet in facets)
        {
            if (string.IsNullOrWhiteSpace(facet))
            {
                continue;
            }

            var weight = idf(facet);
            if (weight <= 0)
            {
                continue;
            }

            denominator += weight;
            numerator += share(facet) * weight;
        }

        return denominator > 0 ? numerator / denominator : 0d;
    }

    private static double PersonSimilarity(
        IReadOnlyList<Guid> people,
        TasteProfile profile,
        FacetIndex index)
    {
        double numerator = 0;
        double denominator = 0;

        foreach (var person in people)
        {
            var weight = index.PersonIdf(person);
            if (weight <= 0)
            {
                continue;
            }

            denominator += weight;
            numerator += profile.PersonShare(person) * weight;
        }

        return denominator > 0 ? numerator / denominator : 0d;
    }
}
