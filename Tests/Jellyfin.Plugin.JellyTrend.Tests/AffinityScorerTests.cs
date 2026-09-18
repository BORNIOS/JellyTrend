using System;

using Jellyfin.Plugin.JellyTrend.Services.Recommendation;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// La capa 3 puntua contra el perfil guardado. Estas pruebas fijan lo que el motor promete: cada familia
/// pesa lo que dice la distribucion acordada (no lo que le deje una normalizacion), cubrir mas del perfil
/// vale mas, y las afinidades combinadas suman sobre los valores sueltos.
/// </summary>
public sealed class AffinityScorerTests
{
    /// <summary>
    /// Un titulo que solo encaja en una familia menor no puede puntuar como uno que coincide con el genero
    /// que el usuario consume: es el defecto que tenia el perfil cuando la normalizacion era global.
    /// </summary>
    [Fact]
    public void AFamilyOnlyCandidateCollectsOnlyItsShare()
    {
        var profile = Profile(
            new ProfileItem { Id = Guid.NewGuid(), Weight = 1.0, Genres = ["Terror"], Actors = ["Ana"], Rating = 9.2 });

        var byGenre = AffinityScorer.Similarity(Features(genres: ["Terror"]), profile);
        var byActor = AffinityScorer.Similarity(Features(actors: ["Ana"]), profile);
        var byRating = AffinityScorer.Similarity(Features(rating: 9.2f), profile);

        Assert.True(byGenre > byActor, $"el genero (0.22) pesa mas que el reparto (0.14): {byGenre:F3} frente a {byActor:F3}");
        Assert.True(byActor > byRating, $"el reparto (0.14) pesa mas que la nota (0.03): {byActor:F3} frente a {byRating:F3}");
        Assert.True(byRating > 0d, "la nota tiene que aportar algo, aunque poco");
    }

    /// <summary>
    /// Cubrir mas familias vale mas que cubrir una sola: la diferencia entre "le va a gustar" y "no le
    /// disgusta".
    /// </summary>
    [Fact]
    public void CoveringMoreOfTheProfileScoresHigher()
    {
        var profile = Profile(
            new ProfileItem { Id = Guid.NewGuid(), Weight = 1.0, Genres = ["Terror"], Actors = ["Ana"], Directors = ["Dani"] });

        var single = AffinityScorer.Similarity(Features(genres: ["Terror"]), profile);
        var triple = AffinityScorer.Similarity(Features(genres: ["Terror"], actors: ["Ana"], directors: ["Dani"]), profile);

        Assert.True(triple > single, $"coincidir en tres familias debe valer mas: {triple:F3} frente a {single:F3}");
    }

    /// <summary>
    /// El par suma sobre los valores sueltos: "terror + Dani" no es solo terror mas Dani.
    /// </summary>
    [Fact]
    public void ACombinedAffinityAddsOnTopOfTheSingleOnes()
    {
        var profile = Profile(
            new ProfileItem { Id = Guid.NewGuid(), Weight = 1.0, Genres = ["Terror"], Directors = ["Dani"] });

        var matchingPair = AffinityScorer.Similarity(Features(genres: ["Terror"], directors: ["Dani"]), profile);
        var withoutPair = AffinityScorer.Similarity(Features(genres: ["Terror"], directors: ["Otro"]), profile);

        Assert.True(
            matchingPair > withoutPair,
            $"el par del usuario debe sumar sobre la coincidencia suelta: {matchingPair:F3} frente a {withoutPair:F3}");
    }

    private static AffinityProfile Profile(ProfileItem item)
        => AffinityProfile.From(TasteProfileBuilder.Build([item]));

    private static ItemFeatures Features(
        string[]? genres = null,
        string[]? actors = null,
        string[]? directors = null,
        float? rating = null)
        => new(
            genres ?? [],
            [],
            [],
            [],
            rating,
            null,
            directors ?? [],
            actors ?? [],
            [],
            null);
}
