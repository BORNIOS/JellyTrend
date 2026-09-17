using System;
using System.Collections.Generic;
using System.Linq;

using Jellyfin.Plugin.JellyTrend.Services.Recommendation;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// Fija la matemática del motor de recomendaciones. El caso que originó estas pruebas es real: un
/// usuario cuyo perfil es Terror/Suspense recibía una fila con más Drama y Romance del que ve,
/// porque la afinidad se contaba en crudo y el rating comunitario pesaba el 60% de la puntuación.
/// </summary>
public sealed class RecommendationScoringTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Dos efectos de la ponderación por rareza: el género común de la biblioteca pesa menos en el
    /// perfil, y un candidato que arrastra ese género común queda diluido frente a uno puro.
    /// </summary>
    [Fact]
    public void CommonLibraryGenresAreDiscountedAndDiluteCandidates()
    {
        var index = new FacetIndex();
        for (var i = 0; i < 100; i++)
        {
            index.Add(["Drama"], [], [], []);
        }

        index.Add(["Terror"], [], [], []);

        Assert.True(
            index.GenreIdf("Terror") > index.GenreIdf("Drama"),
            "Un género presente en toda la biblioteca debe discriminar menos.");

        // El usuario vio una película de cada género: sin IDF serían 50/50, con IDF manda Terror.
        var mixed = new List<TasteItem> { Taste(1, ["Drama"], 1), Taste(2, ["Terror"], 1) };
        var profile = TasteProfile.Build(mixed, index);

        Assert.True(
            profile.GenreShare("Terror") > profile.GenreShare("Drama"),
            $"Terror debería pesar más que Drama: {profile.GenreShare("Terror"):F3} vs {profile.GenreShare("Drama"):F3}.");

        var pure = Candidate(10, "Solo Terror", ["Terror"], rating: 7);
        var diluted = Candidate(11, "Terror con Drama", ["Terror", "Drama"], rating: 7);
        var common = Candidate(12, "Solo Drama", ["Drama"], rating: 7);

        var scorePure = RecommendationScorer.Score(pure, profile, index, Now).Taste;
        var scoreDiluted = RecommendationScorer.Score(diluted, profile, index, Now).Taste;
        var scoreCommon = RecommendationScorer.Score(common, profile, index, Now).Taste;

        Assert.True(scorePure > scoreDiluted, $"Arrastrar Drama debe diluir: {scorePure:F3} vs {scoreDiluted:F3}.");
        Assert.True(scoreDiluted > scoreCommon, $"Terror debe seguir aportando: {scoreDiluted:F3} vs {scoreCommon:F3}.");
    }

    /// <summary>
    /// El caso del usuario: 18 películas de Terror y 2 de Romance en el historial. Romance solo puede
    /// ocupar su cuota proporcional, no inundar la fila.
    /// </summary>
    [Fact]
    public void ProfileProportionLimitsTheWeakerGenre()
    {
        var watched = new List<TasteItem>();
        for (var i = 0; i < 18; i++)
        {
            watched.Add(Taste(1000 + i, ["Terror"], weight: 1));
        }

        for (var i = 0; i < 2; i++)
        {
            watched.Add(Taste(2000 + i, ["Romance"], weight: 1));
        }

        var candidates = new List<CandidateItem>();
        for (var i = 0; i < 40; i++)
        {
            candidates.Add(Candidate(3000 + i, $"Terror {i}", ["Terror"], rating: 7));
            candidates.Add(Candidate(4000 + i, $"Romance {i}", ["Romance"], rating: 7));
        }

        var ids = Recommend(watched, candidates, maxItems: 10);
        var byId = candidates.ToDictionary(candidate => candidate.Id);
        var terror = ids.Count(id => byId[id].Genres.Contains("Terror"));
        var romance = ids.Count(id => byId[id].Genres.Contains("Romance"));

        Assert.True(terror >= 7, $"Terror debería dominar la fila; obtuvo {terror} de {ids.Count}.");
        Assert.True(romance <= 2, $"Romance debía quedar en su cuota proporcional; obtuvo {romance} de {ids.Count}.");
    }

    [Fact]
    public void HighRatingCannotOutvoteTaste()
    {
        var watched = Enumerable.Range(0, 10).Select(i => Taste(100 + i, ["Terror"], weight: 1)).ToList();

        var candidates = Enumerable.Range(0, 12)
            .Select(i => Candidate(200 + i, $"Terror {i}", ["Terror"], rating: 7))
            .Append(Candidate(900, "Romance perfecta", ["Romance"], rating: 10))
            .ToList();

        var ids = Recommend(watched, candidates, maxItems: 5);
        var byId = candidates.ToDictionary(candidate => candidate.Id);

        Assert.Equal(new[] { "Terror" }, byId[ids[0]].Genres);
        Assert.DoesNotContain(TestId(900), ids);
    }

    [Fact]
    public void GenresOutsideTheProfileCannotFloodTheRow()
    {
        var watched = Enumerable.Range(0, 12).Select(i => Taste(100 + i, ["Terror"], weight: 1)).ToList();

        var terror = Enumerable.Range(0, 20)
            .Select(i => Candidate(200 + i, $"Terror {i}", ["Terror"], rating: 6))
            .ToList();

        // Drama es el género más abundante de la biblioteca y el rating es alto: antes entraba en masa.
        var drama = Enumerable.Range(0, 30)
            .Select(i => Candidate(500 + i, $"Drama {i}", ["Drama"], rating: 9))
            .ToList();

        var ids = Recommend(watched, [.. terror, .. drama], maxItems: 10);
        var byId = terror.Concat(drama).ToDictionary(candidate => candidate.Id);
        var dramaCount = ids.Count(id => byId[id].Genres.Contains("Drama"));
        var terrorCount = ids.Count(id => byId[id].Genres.Contains("Terror"));

        Assert.True(terrorCount >= 8, $"Terror debería llenar la fila; obtuvo {terrorCount} de {ids.Count}.");
        Assert.True(dramaCount <= 2, $"Drama debía quedar en el presupuesto de exploración; obtuvo {dramaCount}.");
    }

    [Fact]
    public void SimilarTitlesDoNotStackUp()
    {
        var watched = Enumerable.Range(0, 10).Select(i => Taste(100 + i, ["Terror", "Suspense"], weight: 1)).ToList();

        // Clones: mismo género, mismos tags, mismas personas y mejor rating.
        var clones = Enumerable.Range(0, 10)
            .Select(i => Candidate(700 + i, $"Saga {i}", ["Terror", "Suspense"], rating: 9, tags: ["slasher"], people: [Guid.Parse("11111111-1111-1111-1111-111111111111")]))
            .ToList();

        var varied = Enumerable.Range(0, 10)
            .Select(i => Candidate(800 + i, $"Terror {i}", ["Terror"], rating: 7, tags: [$"tag-{i}"], people: [Guid.NewGuid()]))
            .ToList();

        var ids = Recommend(watched, [.. clones, .. varied], maxItems: 10);
        var cloneIds = clones.Select(clone => clone.Id).ToHashSet();
        var cloneCount = ids.Count(cloneIds.Contains);

        Assert.True(cloneCount <= 6, $"El pase de diversidad debía repartir la fila; entraron {cloneCount} clones de {ids.Count}.");
        Assert.True(ids.Count == 10);
    }

    [Fact]
    public void SameReleaseAndSameFranchiseAreCapped()
    {
        var watched = Enumerable.Range(0, 10).Select(i => Taste(100 + i, ["Terror"], weight: 1)).ToList();

        var duplicates = new List<CandidateItem>
        {
            Candidate(900, "Terror Duplicado", ["Terror"], rating: 8, tmdbId: "555"),
            Candidate(901, "Terror Duplicado (copia)", ["Terror"], rating: 8, tmdbId: "555"),
            Candidate(902, "Saga Sangrienta", ["Terror"], rating: 8),
            Candidate(903, "Saga Sangrienta II", ["Terror"], rating: 8),
            Candidate(904, "Saga Sangrienta III", ["Terror"], rating: 8)
        };

        var fillers = Enumerable.Range(0, 20)
            .Select(i => Candidate(1000 + i, $"Otro {i}", ["Terror"], rating: 7))
            .ToList();

        var ids = Recommend(watched, [.. duplicates, .. fillers], maxItems: 8);

        Assert.True(
            ids.Count(id => id == TestId(900) || id == TestId(901)) <= 1,
            "Dos entradas de la misma película no pueden aparecer juntas.");
        Assert.True(
            ids.Count(id => id == TestId(902) || id == TestId(903) || id == TestId(904)) <= 2,
            "Una franquicia no puede ocupar más de dos puestos.");
        Assert.Equal(8, ids.Count);
    }

    [Fact]
    public void EngagementWeightsRecentAndRewatchedTitlesHigher()
    {
        var recent = EngagementModel.Weight(true, 1, 1, false, Now.AddDays(-5), Now);
        var old = EngagementModel.Weight(true, 1, 1, false, Now.AddDays(-720), Now);
        var rewatched = EngagementModel.Weight(true, 1, 4, false, Now.AddDays(-5), Now);
        var favorite = EngagementModel.Weight(true, 1, 1, true, Now.AddDays(-5), Now);
        var abandoned = EngagementModel.Weight(false, 0.2, 1, false, Now.AddDays(-5), Now);

        Assert.True(recent > old, "Una película vista hace días debe pesar más que una de hace dos años.");
        Assert.True(rewatched > recent, "Un segundo visionado debe pesar más que uno único.");
        Assert.True(favorite > recent, "Una favorita debe pesar más que una vista normal.");
        Assert.True(abandoned < recent, "Una película abandonada debe pesar menos que una terminada.");
        Assert.True(old >= EngagementModel.MinimumRecency, "El peso nunca debe caer a cero por antigüedad.");
    }

    [Fact]
    public void UserWithoutHistoryGetsQualityOrderedDiverseRow()
    {
        var candidates = Enumerable.Range(0, 20)
            .Select(i => Candidate(300 + i, $"Peli {i}", i % 2 == 0 ? ["Terror"] : ["Comedia"], rating: 5 + (i % 5)))
            .ToList();

        var index = new FacetIndex();
        foreach (var candidate in candidates)
        {
            index.Add(candidate.Genres, candidate.Tags, candidate.Studios, candidate.People);
        }

        var profile = TasteProfile.Build([], index);
        var scored = candidates
            .Select(candidate => RecommendationScorer.Score(candidate, profile, index, Now))
            .ToList();

        var ids = RecommendationSelector.Select(scored, profile, maxItems: 6);
        var byId = candidates.ToDictionary(candidate => candidate.Id);

        Assert.Equal(6, ids.Count);
        Assert.True(ids.All(id => byId[id].CommunityRating >= 6), "Sin historial debe ordenarse por calidad.");
        Assert.Equal(2, ids.Select(id => byId[id].Genres[0]).Distinct().Count());
    }

    private static List<Guid> Recommend(
        IReadOnlyList<TasteItem> watched,
        IReadOnlyList<CandidateItem> candidates,
        int maxItems)
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

        var profile = TasteProfile.Build(watched, index);
        var scored = candidates
            .Select(candidate => RecommendationScorer.Score(candidate, profile, index, Now))
            .ToList();

        return RecommendationSelector.Select(scored, profile, maxItems);
    }

    private static TasteItem Taste(int id, string[] genres, double weight)
        => new(TestId(id), genres, [], [], [], weight);

    private static CandidateItem Candidate(
        int id,
        string name,
        string[] genres,
        double rating,
        string? tmdbId = null,
        string[]? tags = null,
        Guid[]? people = null)
        => new(
            TestId(id),
            name,
            tmdbId,
            genres,
            tags ?? [],
            [],
            people ?? [],
            (float)rating,
            Now.AddYears(-1));

    // Guid deterministas para poder afirmar sobre ids concretos en las aserciones.
    private static Guid TestId(int value) => new(value, 0, 0, new byte[8]);
}
