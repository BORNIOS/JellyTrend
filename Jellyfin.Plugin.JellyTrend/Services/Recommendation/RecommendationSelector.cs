using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Turns scored candidates into the final row.
/// </summary>
/// <remarks>
/// <para>
/// Ranking by score alone is what allowed a single stray genre to dominate the row. The selection
/// therefore adds two mathematical guards on top of relevance:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Proportional quota per genre.</b> A genre can only fill
/// <c>ceil(share · maxItems · slack)</c> slots, so a genre that is 8% of the user's profile cannot
/// take 12% of the row while a genre that is 33% of it is under-served.
/// </description></item>
/// <item><description>
/// <b>MMR (maximal marginal relevance).</b> Each pick maximizes
/// <c>λ·relevance − (1−λ)·max similarity</c> against what is already selected, using the Jaccard
/// distance over genres, tags and people, so near-duplicates do not stack up.
/// </description></item>
/// </list>
/// <para>
/// A small exploration budget keeps room for titles outside the profile instead of producing a
/// perfectly closed filter bubble.
/// </para>
/// </remarks>
internal static class RecommendationSelector
{
    /// <summary>Balance between relevance and diversity in MMR.</summary>
    internal const double MmrLambda = 0.75d;

    /// <summary>How much head-room a genre gets over its proportional share.</summary>
    internal const double QuotaSlack = 1.5d;

    /// <summary>Minimum slots reserved for a genre present in the profile.</summary>
    internal const int MinimumGenreQuota = 2;

    /// <summary>Minimum number of watched movies for quotas and exploration to mean anything.</summary>
    internal const int MinimumProfileItems = 3;

    /// <summary>Taste match below which a movie counts as off-profile exploration.</summary>
    internal const double OffProfileTasteThreshold = 0.10d;

    /// <summary>Share of the row reserved for off-profile exploration.</summary>
    internal const double ExplorationShare = 0.10d;

    /// <summary>Maximum number of movies of the same franchise.</summary>
    internal const int MaxPerFranchise = 2;

    /// <summary>
    /// How many top-scored candidates are examined per pick when applying MMR. Wide enough that the
    /// quotas and the exploration budget are honoured from the best-scored candidates that are still
    /// eligible, instead of falling through to a relaxed pass with a short window.
    /// </summary>
    internal const int MmrScanWindow = 320;

    /// <summary>Number of relaxation passes used to fill the row.</summary>
    internal const int RelaxationPasses = 3;

    /// <summary>
    /// Selects the recommended ids.
    /// </summary>
    /// <param name="scored">All scored candidates.</param>
    /// <param name="profile">Taste profile of the user.</param>
    /// <param name="maxItems">Maximum number of ids to return.</param>
    /// <returns>The selected movie ids, best first.</returns>
    public static List<Guid> Select(IReadOnlyList<ScoredCandidate> scored, TasteProfile profile, int maxItems)
    {
        ArgumentNullException.ThrowIfNull(scored);
        ArgumentNullException.ThrowIfNull(profile);

        if (maxItems <= 0 || scored.Count == 0)
        {
            return [];
        }

        var pool = scored
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Movie.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var topScore = pool[0].Score;
        var normalizer = topScore > 0 ? topScore : 1d;
        var quota = BuildGenreQuota(profile, maxItems);
        var explorationBudget = Math.Max(1, (int)Math.Ceiling(maxItems * ExplorationShare));

        var selected = new List<ScoredCandidate>(Math.Min(maxItems, pool.Count));
        var taken = new HashSet<Guid>();
        var releases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var franchiseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var explorationUsed = 0;

        // Con un perfil insuficiente (usuario nuevo) las cuotas y el presupuesto de exploración no
        // tienen sentido: se ordena por calidad y variedad, que es lo único que se puede afirmar.
        var enforceQuota = profile.HasGenres && profile.ItemCount >= MinimumProfileItems;

        // Tres pasadas: reglas completas, luego cuotas relajadas y, si la biblioteca es tan pequeña o
        // está tan agrupada que aún faltan puestos, también franquicia y exploración. La fila se llena
        // siempre; lo único que nunca se relaja es el descarte de duplicados exactos.
        for (var pass = 0; pass < RelaxationPasses && selected.Count < maxItems; pass++)
        {
            var relaxQuota = pass >= 1;
            var relaxFranchise = pass >= 2;
            var relaxExploration = pass >= 2;

            while (selected.Count < maxItems)
            {
                ScoredCandidate? best = null;
                var bestMmr = double.NegativeInfinity;
                var examined = 0;

                foreach (var candidate in pool)
                {
                    if (taken.Contains(candidate.Movie.Id))
                    {
                        continue;
                    }

                    if (examined++ >= MmrScanWindow)
                    {
                        break;
                    }

                    if (!IsEligible(candidate, profile, quota, genreCounts, franchiseCounts, releases, enforceQuota, relaxQuota, relaxFranchise, relaxExploration, explorationUsed, explorationBudget))
                    {
                        continue;
                    }

                    var mmr = Mmr(candidate, selected, normalizer);
                    if (mmr > bestMmr)
                    {
                        bestMmr = mmr;
                        best = candidate;
                    }
                }

                if (best is not { } pick)
                {
                    break;
                }

                taken.Add(pick.Movie.Id);
                selected.Add(pick);
                releases.Add(pick.Movie.DedupeKey);

                var franchiseKey = pick.Movie.FranchiseKey;
                franchiseCounts[franchiseKey] = franchiseCounts.GetValueOrDefault(franchiseKey) + 1;

                var bestGenre = BestGenre(pick.Movie, profile);
                if (bestGenre is not null)
                {
                    genreCounts[bestGenre] = genreCounts.GetValueOrDefault(bestGenre) + 1;
                }

                if (pick.Taste < OffProfileTasteThreshold)
                {
                    explorationUsed++;
                }
            }
        }

        return selected.Select(static candidate => candidate.Movie.Id).ToList();
    }

    /// <summary>
    /// Builds the per-genre quota from the profile distribution.
    /// </summary>
    /// <param name="profile">Taste profile of the user.</param>
    /// <param name="maxItems">Size of the row.</param>
    /// <returns>Maximum slots allowed per genre.</returns>
    internal static Dictionary<string, int> BuildGenreQuota(TasteProfile profile, int maxItems)
    {
        var quota = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var genre in profile.GenreDistribution)
        {
            quota[genre.Key] = Math.Max(
                MinimumGenreQuota,
                (int)Math.Ceiling(genre.Value * maxItems * QuotaSlack));
        }

        return quota;
    }

    private static bool IsEligible(
        ScoredCandidate candidate,
        TasteProfile profile,
        Dictionary<string, int> quota,
        Dictionary<string, int> genreCounts,
        Dictionary<string, int> franchiseCounts,
        HashSet<string> releases,
        bool enforceQuota,
        bool relaxQuota,
        bool relaxFranchise,
        bool relaxExploration,
        int explorationUsed,
        int explorationBudget)
    {
        // Two entries of the same movie (different releases or duplicated library entries) never both fit.
        if (releases.Contains(candidate.Movie.DedupeKey))
        {
            return false;
        }

        if (!relaxFranchise && franchiseCounts.GetValueOrDefault(candidate.Movie.FranchiseKey) >= MaxPerFranchise)
        {
            return false;
        }

        if (!relaxExploration
            && candidate.Taste < OffProfileTasteThreshold
            && explorationUsed >= explorationBudget)
        {
            return false;
        }

        if (!enforceQuota || relaxQuota)
        {
            return true;
        }

        var bestGenre = BestGenre(candidate.Movie, profile);
        return bestGenre is null || genreCounts.GetValueOrDefault(bestGenre) < quota[bestGenre];
    }

    // The genre that best identifies the movie for this user: the one with the largest profile share.
    internal static string? BestGenre(CandidateItem movie, TasteProfile profile)
    {
        string? best = null;
        var bestShare = 0d;

        foreach (var genre in movie.Genres)
        {
            var share = profile.GenreShare(genre);
            if (share > bestShare)
            {
                bestShare = share;
                best = genre;
            }
        }

        return best;
    }

    private static double Mmr(ScoredCandidate candidate, List<ScoredCandidate> selected, double normalizer)
    {
        var relevance = candidate.Score / normalizer;
        if (selected.Count == 0)
        {
            return relevance;
        }

        var maxSimilarity = 0d;
        foreach (var chosen in selected)
        {
            var similarity = Jaccard(candidate.Movie.TraitKeys, chosen.Movie.TraitKeys);
            if (similarity > maxSimilarity)
            {
                maxSimilarity = similarity;
            }
        }

        return (MmrLambda * relevance) - ((1d - MmrLambda) * maxSimilarity);
    }

    private static double Jaccard(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0d;
        }

        var intersection = 0;
        foreach (var key in left)
        {
            if (right.Contains(key))
            {
                intersection++;
            }
        }

        var union = left.Count + right.Count - intersection;
        return union > 0 ? intersection / (double)union : 0d;
    }
}
