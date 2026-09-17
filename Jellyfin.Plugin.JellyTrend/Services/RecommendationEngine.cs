using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend.Api;
using Jellyfin.Plugin.JellyTrend.Services.Recommendation;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Services;

/// <summary>
/// Builds personalized recommendations from a user's watch history.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline: watch history → engagement-weighted, IDF-scaled taste profile (a probability
/// distribution over genres, tags, studios and people) → candidate pool → per-movie similarity →
/// proportional quotas plus MMR diversity. See <see cref="TasteProfile"/>,
/// <see cref="RecommendationScorer"/> and <see cref="RecommendationSelector"/> for the math, and
/// <see cref="CandidateSource"/> for how the engine works with or without a database provider.
/// </para>
/// <para>
/// Because weights are engagement- and rarity-based, the genres the user actually watches dominate
/// the row, genres that are simply common in the library (Drama in a drama-heavy library) are
/// discounted, and the community rating can no longer outvote taste the way it did when it carried
/// 60% of the score.
/// </para>
/// </remarks>
internal static class RecommendationEngine
{
    private const int TopGenresForCandidates = 8;
    private const int TopTagsForCandidates = 12;
    private const int TopPeopleForCandidates = 12;
    private const int MaxDiagnosticFacets = 5;

    /// <summary>
    /// Builds the recommendation item ids for a user.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="user">The target user.</param>
    /// <param name="trendingItemIds">Ids already shown in the trending row, excluded from results.</param>
    /// <param name="maxItems">Maximum number of recommendations.</param>
    /// <param name="logger">Logger used for data-source diagnostics.</param>
    /// <param name="queryProvider">
    /// Optional database-provider-specific backend. When present it is used only to discover
    /// candidate ids faster; the engine produces the same kind of list without it.
    /// </param>
    /// <returns>The recommended item ids and the diagnostics of the run.</returns>
    public static RecommendationResult BuildRecommendations(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        User user,
        IReadOnlySet<Guid> trendingItemIds,
        int maxItems,
        ILogger logger,
        IRecommendationQueryProvider? queryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userDataManager);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(trendingItemIds);
        ArgumentNullException.ThrowIfNull(logger);

        var source = CandidateSource.Create(libraryManager, userDataManager, queryProvider, logger);
        var topParentIds = GetTopParentIds(libraryManager, user);
        var watched = source.GetWatched(user, DateTime.UtcNow);

        if (watched.Count == 0)
        {
            var coldIds = BuildColdStart(source, user, trendingItemIds, maxItems, topParentIds);
            return new RecommendationResult(
                coldIds,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "fuente={0} | sin historial: fila por calidad y variedad | lista({1})",
                    source.Description,
                    coldIds.Count));
        }

        // Provisional profile, built from the watch history alone, only to decide which facets are
        // worth querying. The definitive profile is rebuilt over the real universe below.
        var seedProfile = BuildProfile(watched, []);
        var candidates = source.GetCandidates(
            user,
            seedProfile.TopGenres(TopGenresForCandidates).Select(static genre => genre.Key).ToList(),
            seedProfile.TopTags(TopTagsForCandidates).Select(static tag => tag.Key).ToList(),
            seedProfile.TopPeople(TopPeopleForCandidates).Select(static person => person.Key).ToList(),
            topParentIds);

        if (candidates.Count == 0)
        {
            return new RecommendationResult([], $"fuente={source.Description} | sin candidatos que puntuar");
        }

        var index = new FacetIndex();
        foreach (var item in watched)
        {
            index.Add(item.Genres, item.Tags, item.Studios, item.People);
        }

        foreach (var candidate in candidates)
        {
            index.Add(candidate.Genres, candidate.Tags, candidate.Studios, candidate.People);
        }

        var profile = TasteProfile.Build(watched, index);
        var watchedIds = watched.Select(static item => item.Id).ToHashSet();
        var nowUtc = DateTime.UtcNow;

        var scored = candidates
            .Where(candidate => !watchedIds.Contains(candidate.Id))
            .Where(candidate => !trendingItemIds.Contains(candidate.Id))
            .Select(candidate => RecommendationScorer.Score(candidate, profile, index, nowUtc))
            .ToList();

        var selectedIds = RecommendationSelector.Select(scored, profile, maxItems);

        return new RecommendationResult(selectedIds, BuildDiagnostics(source, profile, scored, selectedIds));
    }

    // Without history there is nothing to model, so the row is the best-rated diverse set: the scorer
    // yields a zero taste term and the selector still applies the diversity pass.
    private static List<Guid> BuildColdStart(
        CandidateSource source,
        User user,
        IReadOnlySet<Guid> trendingItemIds,
        int maxItems,
        IReadOnlyList<Guid> topParentIds)
    {
        var candidates = source.GetCandidates(user, [], [], [], topParentIds);
        if (candidates.Count == 0)
        {
            return [];
        }

        var index = new FacetIndex();
        foreach (var candidate in candidates)
        {
            index.Add(candidate.Genres, candidate.Tags, candidate.Studios, candidate.People);
        }

        var nowUtc = DateTime.UtcNow;
        var scored = candidates
            .Where(candidate => !trendingItemIds.Contains(candidate.Id))
            .Select(candidate => RecommendationScorer.Score(candidate, TasteProfile.Build([], index), index, nowUtc))
            .ToList();

        return RecommendationSelector.Select(scored, TasteProfile.Build([], index), maxItems);
    }

    private static TasteProfile BuildProfile(IReadOnlyList<TasteItem> watched, IReadOnlyList<CandidateItem> candidates)
    {
        var index = new FacetIndex();
        foreach (var item in watched)
        {
            index.Add(item.Genres, item.Tags, item.Studios, item.People);
        }

        foreach (var candidate in candidates)
        {
            index.Add(candidate.Genres, candidate.Tags, candidate.Studios, candidate.People);
        }

        return TasteProfile.Build(watched, index);
    }

    private static List<Guid> GetTopParentIds(ILibraryManager libraryManager, User user)
        => libraryManager
            .GetUserRootFolder()
            .GetChildren(user, true)
            .Select(static folder => folder.Id)
            .ToList();

    private static string BuildDiagnostics(
        CandidateSource source,
        TasteProfile profile,
        List<ScoredCandidate> scored,
        List<Guid> selectedIds)
    {
        var selected = new HashSet<Guid>(selectedIds);

        var profileSummary = string.Join(", ", profile.TopGenres(MaxDiagnosticFacets).Select(
            static genre => string.Format(CultureInfo.InvariantCulture, "{0} {1:P0}", genre.Key, genre.Value)));

        var composition = string.Join(", ", scored
            .Where(candidate => selected.Contains(candidate.Movie.Id))
            .SelectMany(static candidate => candidate.Movie.Genres)
            .GroupBy(static genre => genre, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDiagnosticFacets)
            .Select(static group => string.Format(CultureInfo.InvariantCulture, "{0} {1}", group.Key, group.Count())));

        return string.Format(
            CultureInfo.InvariantCulture,
            "fuente={0} | perfil({1} vistos, {2} personas): {3} | lista({4} de {5} candidatos): {6}",
            source.Description,
            profile.ItemCount,
            profile.PersonCount,
            profileSummary,
            selectedIds.Count,
            scored.Count,
            composition);
    }
}
