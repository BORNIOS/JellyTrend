using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend.Api;
using Jellyfin.Plugin.JellyTrend.Logging;
using Jellyfin.Plugin.JellyTrend.Services.Recommendation;
using Jellyfin.Plugin.JellyTrend.Services.Store;
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

    /// <summary>Peso de la popularidad local en el arranque en frio.</summary>
    private const double PopularityWeight = 0.55d;

    /// <summary>Peso de la calidad (nota de la comunidad) en el arranque en frio.</summary>
    private const double QualityWeight = 0.35d;

    /// <summary>Peso de la novedad en el arranque en frio.</summary>
    private const double FreshnessWeight = 0.10d;

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
    /// <param name="userManager">
    /// Gestor de usuarios, usado solo en el arranque en frio para medir la popularidad local (lo que ya ve
    /// el resto del servidor) de quien todavia no tiene historial suficiente.
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
        ProviderState? providerState = null,
        IUserManager? userManager = null)
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
            features,
            userManager);
        var topParentIds = LibraryScope.Resolve(libraryManager, user);
        var watched = source.GetWatched(user, DateTime.UtcNow);
        var nowUtc = DateTime.UtcNow;

        // Capa 3: manda el perfil que la capa 2 ya guardo. El historial crudo solo decide cuando no hay
        // perfil guardado (primera corrida tras instalar, o almacen ilegible).
        var storeProfile = AffinityProfile.From(JellyTrendStore.ReadAffinities(user.Id));
        if (storeProfile.Count > 0 && watched.Count >= TasteProfileService.ColdStartThreshold)
        {
            return Report(user, maxItems, BuildFromStoreProfile(
                source, user, watched, storeProfile, trendingItemIds, maxItems, topParentIds, features, nowUtc));
        }

        if (watched.Count == 0)
        {
            var coldIds = BuildColdStart(source, user, trendingItemIds, maxItems, topParentIds, nowUtc);
            return Report(user, maxItems, new RecommendationResult(
                coldIds,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "fuente={0} | alcance={1} bibliotecas | sin historial: fila por popularidad local, calidad y variedad | lista({2})",
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

    // Without history there is nothing to model, so the row is what the server itself already values: how
    // much the other users watch each title (local popularity), how well rated it is and how recent. The
    // selector still applies its diversity pass, and a title nobody watches and nobody rated is left for
    // the exploration budget instead of filling the row.
    private static List<Guid> BuildColdStart(
        CandidateSource source,
        User user,
        IReadOnlySet<Guid> trendingItemIds,
        int maxItems,
        IReadOnlyList<Guid> topParentIds,
        DateTime nowUtc)
    {
        var candidates = source.GetCandidates(user, [], [], [], topParentIds);
        if (candidates.Count == 0)
        {
            return [];
        }

        var popularity = source.GetLocalPopularity(candidates, user);

        var index = new FacetIndex();
        foreach (var candidate in candidates)
        {
            index.Add(candidate.Genres, candidate.Tags, candidate.Studios, candidate.People);
        }

        var scored = candidates
            .Where(candidate => !trendingItemIds.Contains(candidate.Id))
            .Select(candidate => ColdStartScore(candidate, popularity.GetValueOrDefault(candidate.Id), nowUtc))
            .ToList();

        return RecommendationSelector.Select(scored, TasteProfile.Build([], index), maxItems);
    }

    // Sin historial no hay gusto que aplicar: el "parecido" que se reporta es el interes medido en el
    // servidor, de modo que lo que nadie mira y nadie valora cuente como exploracion y no desplace a lo
    // que si se ve en casa.
    private static ScoredCandidate ColdStartScore(CandidateItem movie, double popularity, DateTime nowUtc)
    {
        var quality = movie.CommunityRating is { } rating
            ? Math.Clamp((rating - RecommendationScorer.RatingFloor) / (10d - RecommendationScorer.RatingFloor), 0d, 1d)
            : RecommendationScorer.NeutralQuality;
        var freshness = movie.PremiereDate is { } premiere && premiere > nowUtc.AddYears(-RecommendationScorer.FreshnessYears) ? 1d : 0d;
        var score = (PopularityWeight * popularity) + (QualityWeight * quality) + (FreshnessWeight * freshness);

        return new ScoredCandidate(
            movie,
            score,
            Math.Max(popularity, RecommendationSelector.OffProfileTasteThreshold),
            quality);
    }

    // Capa 3: la fila se arma contra el perfil guardado. El historial solo se usa para dos cosas que el
    // perfil no guarda: saber que titulos ya se vieron (para no repetirlos) y, cuando el perfil conoce
    // personas, ampliar el pool de candidatos con las que el usuario ya vio.
    private static RecommendationResult BuildFromStoreProfile(
        CandidateSource source,
        User user,
        IReadOnlyList<TasteItem> watched,
        AffinityProfile storeProfile,
        IReadOnlySet<Guid> trendingItemIds,
        int maxItems,
        IReadOnlyList<Guid> topParentIds,
        FeatureStore features,
        DateTime nowUtc)
    {
        var people = storeProfile.HasFacet("actor") || storeProfile.HasFacet("director")
            ? watched.SelectMany(static item => item.People).Distinct().Take(TopPeopleForCandidates).ToList()
            : [];

        var candidates = source.GetCandidates(
            user,
            storeProfile.TopValues("genre", TopGenresForCandidates),
            storeProfile.TopValues("tag", TopTagsForCandidates),
            people,
            topParentIds);

        if (candidates.Count == 0)
        {
            return new RecommendationResult(
                [],
                string.Format(
                    CultureInfo.InvariantCulture,
                    "fuente={0} | perfil guardado({1} afinidades) | sin candidatos que puntuar",
                    source.Description,
                    storeProfile.Count));
        }

        var watchedIds = watched.Select(static item => item.Id).ToHashSet();
        var scored = candidates
            .Where(candidate => !watchedIds.Contains(candidate.Id))
            .Where(candidate => !trendingItemIds.Contains(candidate.Id))
            .Select(candidate => AffinityScorer.Score(candidate, FacetsOf(candidate, features), storeProfile, nowUtc))
            .ToList();

        var selectionProfile = TasteProfile.FromAffinities(storeProfile, watched.Count);
        var selectedIds = RecommendationSelector.Select(scored, selectionProfile, maxItems);

        return new RecommendationResult(
            selectedIds,
            BuildStoreDiagnostics(source, storeProfile, selectionProfile, scored, selectedIds));
    }

    // La fuente materializa cada candidato y guarda sus facetas, asi que la cache deberia tenerlas todas;
    // si falta alguna se reconstruye con lo que el propio candidato trae, para no descartar el titulo.
    private static ItemFeatures FacetsOf(CandidateItem candidate, FeatureStore features)
        => features.TryGet(candidate.Id, out var cached)
            ? cached
            : new ItemFeatures(
                candidate.Genres,
                candidate.Tags,
                candidate.Studios,
                candidate.People,
                candidate.CommunityRating,
                candidate.PremiereDate,
                [],
                [],
                [],
                null);

    private static string BuildStoreDiagnostics(
        CandidateSource source,
        AffinityProfile storeProfile,
        TasteProfile profile,
        List<ScoredCandidate> scored,
        List<Guid> selectedIds)
    {
        var selected = new HashSet<Guid>(selectedIds);
        var selectedMovies = scored
            .Where(candidate => selected.Contains(candidate.Movie.Id))
            .Select(static candidate => candidate.Movie)
            .ToList();

        return string.Format(
            CultureInfo.InvariantCulture,
            "fuente={0} | perfil guardado({1} afinidades, {2} pares): generos {3} | lista({4} de {5} candidatos, por genero identidad): {6}",
            source.Description,
            storeProfile.Count,
            storeProfile.PairCount,
            Describe(Weighted(storeProfile, "genre")),
            selectedIds.Count,
            scored.Count,
            Composition(selectedMovies, profile));
    }

    // Afinidades mas fuertes de una faceta, leidas del perfil guardado (no de las cuotas de seleccion, que
    // son proporciones de otra cosa).
    private static IReadOnlyList<KeyValuePair<string, double>> Weighted(AffinityProfile profile, string facet)
        =>
        [
            .. profile
                .TopValues(facet, MaxDiagnosticFacets)
                .Select(value => new KeyValuePair<string, double>(value, profile.WeightOf(facet, value)))
        ];

    // Composición por genero identidad (el que manda para este usuario) y no por cada co-genero: contar
    // todos los generos hacia parecer que la fila es de Drama cuando en realidad son titulos de Terror o
    // Animacion que ademas llevan Drama.
    private static string Composition(IReadOnlyList<CandidateItem> selectedMovies, TasteProfile profile)
        => string.Join(", ", selectedMovies
            .GroupBy(
                movie => RecommendationSelector.BestGenre(movie, profile)
                    ?? (movie.Genres.Count > 0 ? movie.Genres[0] : "(sin genero)"),
                StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDiagnosticFacets)
            .Select(static group => string.Format(CultureInfo.InvariantCulture, "{0} {1}", group.Key, group.Count())));

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

        var composition = Composition(selectedMovies, profile);

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
