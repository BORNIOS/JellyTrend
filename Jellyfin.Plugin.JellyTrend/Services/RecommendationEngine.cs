using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend.Api;
using Jellyfin.Plugin.JellyTrend.Logging;
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
    /// <param name="features">Cache persistente de caracteristicas por item, compartida por la ejecucion.</param>
    /// <param name="providerState">
    /// Estado del backend opcional de base de datos. Cuando esta disponible se usa solo para descubrir
    /// ids candidatos mas rapido; el motor produce la misma clase de lista sin el.
    /// </param>
    /// <returns>The recommended item ids and the diagnostics of the run.</returns>
    public static RecommendationResult BuildRecommendations(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        User user,
        IReadOnlySet<Guid> trendingItemIds,
        int maxItems,
        ILogger logger,
        FeatureStore features,
        ProviderState? providerState = null)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userDataManager);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(trendingItemIds);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(features);

        var source = CandidateSource.Create(
            libraryManager,
            userDataManager,
            providerState ?? new ProviderState(),
            logger,
            features);
        var topParentIds = LibraryScope.Resolve(libraryManager, user);
        var watched = source.GetWatched(user, DateTime.UtcNow);

        if (watched.Count == 0)
        {
            var coldIds = BuildColdStart(source, user, trendingItemIds, maxItems, topParentIds);
            return Report(user, maxItems, new RecommendationResult(
                coldIds,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "fuente={0} | alcance={1} bibliotecas | sin historial: fila por calidad y variedad | lista({2})",
                    source.Description,
                    topParentIds.Count,
                    coldIds.Count)));
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
            return Report(user, maxItems, new RecommendationResult(
                [],
                $"fuente={source.Description} | alcance={topParentIds.Count} bibliotecas | sin candidatos que puntuar"));
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

        return Report(user, maxItems, new RecommendationResult(selectedIds, BuildDiagnostics(source, profile, scored, selectedIds)));
    }

    // Deja el resultado en los dos logs: el del servidor (tarea) y el propio del plugin, que es el que
    // se consulta primero cuando una fila sale vacia o inesperada.
    private static RecommendationResult Report(User user, int maxItems, RecommendationResult result)
    {
        JellyTrendLog.Info(string.Format(
            CultureInfo.InvariantCulture,
            "[Recomendaciones] '{0}' (max={1}): {2}",
            user.Username,
            maxItems,
            result.Diagnostics));

        return result;
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

    private static string BuildDiagnostics(
        CandidateSource source,
        TasteProfile profile,
        List<ScoredCandidate> scored,
        List<Guid> selectedIds)
    {
        var selected = new HashSet<Guid>(selectedIds);
        var selectedMovies = scored
            .Where(candidate => selected.Contains(candidate.Movie.Id))
            .Select(static candidate => candidate.Movie)
            .ToList();

        var profileSummary = string.Format(
            CultureInfo.InvariantCulture,
            "generos {0} | tags {1}",
            Describe(profile.TopGenres(MaxDiagnosticFacets)),
            Describe(profile.TopTags(MaxDiagnosticFacets)));

        // Composición por genero identidad (el que manda para este usuario) y no por cada co-genero:
        // contar todos los generos hacia parecer que la fila es de Drama cuando en realidad son
        // titulos de Terror o Animacion que ademas llevan Drama.
        var composition = string.Join(", ", selectedMovies
            .GroupBy(
                movie => RecommendationSelector.BestGenre(movie, profile)
                    ?? (movie.Genres.Count > 0 ? movie.Genres[0] : "(sin genero)"),
                StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDiagnosticFacets)
            .Select(static group => string.Format(CultureInfo.InvariantCulture, "{0} {1}", group.Key, group.Count())));

        return string.Format(
            CultureInfo.InvariantCulture,
            "fuente={0} | perfil({1} vistos, {2} personas): {3} | lista({4} de {5} candidatos, por genero identidad): {6}",
            source.Description,
            profile.ItemCount,
            profile.PersonCount,
            profileSummary,
            selectedIds.Count,
            scored.Count,
            composition);
    }

    private static string Describe(IReadOnlyList<KeyValuePair<string, double>> facets)
        => string.Join(", ", facets.Select(
            static facet => string.Format(CultureInfo.InvariantCulture, "{0} {1:P0}", facet.Key, facet.Value)));
}
