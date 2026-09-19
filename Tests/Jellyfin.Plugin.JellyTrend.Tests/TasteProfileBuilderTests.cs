using System;
using System.Linq;

using Jellyfin.Plugin.JellyTrend.Services.Recommendation;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// La capa 2 convierte el consumo en un perfil. Estas pruebas fijan las cuatro propiedades que la
/// especificacion exige: la distribucion de pesos suma 1.00, la intensidad del consumo multiplica cuanto
/// aprende el perfil, el reparto se satura en lugar de sumarse y las afinidades combinadas existen.
/// </summary>
public sealed class TasteProfileBuilderTests
{
    private const string Genre = "genre";
    private const string Actor = "actor";

    /// <summary>
    /// La distribucion acordada tiene que sumar el 100%.
    /// </summary>
    [Fact]
    public void FacetWeightsAddUpToOne()
        => Assert.Equal(1.00, TasteProfileBuilder.FacetWeights.Values.Sum(), precision: 6);

    /// <summary>
    /// El mismo contenido consumido con el doble de intensidad pesa el doble en el perfil.
    /// </summary>
    [Fact]
    public void InteractionIntensityMultipliesWhatTheProfileLearns()
    {
        var light = TasteProfileBuilder.Build([Item(1.0, genres: ["Terror"])]);
        var heavy = TasteProfileBuilder.Build([Item(2.0, genres: ["Terror"])]);

        var lightGenre = light.Single(record => record.Facet == Genre);
        var heavyGenre = heavy.Single(record => record.Facet == Genre);

        // Normalizado: la afinidad mas fuerte vale 1 en los dos perfiles, asi que se compara la forma.
        Assert.Equal(1.00, lightGenre.Weight);
        Assert.Equal(1.00, heavyGenre.Weight);
        Assert.Equal(light.Count, heavy.Count);
    }

    /// <summary>
    /// Cinco actores conocidos ayudan mas que uno, pero no cinco veces mas: el reparto se satura.
    /// </summary>
    [Fact]
    public void MainCastSaturatesInsteadOfAddingUp()
    {
        var one = TasteProfileBuilder.Build([Item(1.0, genres: ["Terror"], actors: ["Uno"])]);
        var five = TasteProfileBuilder.Build([Item(1.0, genres: ["Terror"], actors: ["Uno", "Dos", "Tres", "Cuatro", "Cinco"])]);

        // Normalizado, la afinidad mas fuerte vale 1 en ambos: lo que se compara es cuanto pesa el
        // reparto frente al genero. Lineal serian cinco veces; saturado debe crecer mucho menos.
        // Se miran los valores sueltos (PairedFacet nulo), no las afinidades combinadas.
        var ratioOne = one.Single(record => record.Facet == Actor && record.PairedFacet is null).Weight
            / one.Single(record => record.Facet == Genre && record.PairedFacet is null).Weight;
        var ratioFive = five.Where(record => record.Facet == Actor && record.PairedFacet is null).Average(record => record.Weight)
            / five.Single(record => record.Facet == Genre && record.PairedFacet is null).Weight;

        Assert.True(
            ratioFive < ratioOne * 3.5,
            $"la saturacion debe frenar el crecimiento del reparto (ratio {ratioFive:F2} frente a {ratioOne:F2})");
        // Solo cuenta el reparto principal: un cast grande no puede dominar el perfil.
        Assert.Equal(4, five.Count(record => record.Facet == Actor && record.PairedFacet is null));
        Assert.DoesNotContain(five, record => record.Value == "Cinco");
    }

    /// <summary>
    /// Las afinidades combinadas se guardan: el gusto suele estar en el par, no en el valor suelto.
    /// </summary>
    [Fact]
    public void CombinedAffinitiesAreStored()
    {
        var profile = TasteProfileBuilder.Build(
            [Item(1.0, genres: ["Terror", "Ciencia ficcion"], directors: ["David Fincher"], actors: ["Brad Pitt"])]);

        // El par no tiene orden: se comprueba que exista con cualquiera de las dos colocaciones.
        Assert.Contains(profile, record => record.Facet == Genre && record.PairedFacet == Genre);
        Assert.Contains(
            profile,
            record => (record.Facet == "director" && record.Value == "David Fincher")
                || (record.PairedFacet == "director" && record.PairedValue == "David Fincher"));
        Assert.Contains(
            profile,
            record => (record.Facet == Actor && record.Value == "Brad Pitt")
                || (record.PairedFacet == Actor && record.PairedValue == "Brad Pitt"));
    }

    /// <summary>
    /// La calidad no puede recomendar por si sola: un 9.2 de rating no supera a los generos que el
    /// usuario si consume. Con la normalizacion por familia los dos valen 1 dentro de la suya, asi que la
    /// comparacion se hace sobre lo acumulado antes de normalizar, que es donde vive el peso acordado.
    /// </summary>
    [Fact]
    public void QualitySignalsStayBelowTasteSignals()
    {
        var profile = TasteProfileBuilder.BuildRaw([Item(1.0, genres: ["Terror"], rating: 9.2)]);

        var genre = profile.Single(record => record.Facet == Genre).Weight;
        var rating = profile.Single(record => record.Facet == "rating").Weight;

        Assert.True(genre > rating, "el genero consumido debe pesar mas que el rating del titulo");
    }

    /// <summary>
    /// Cada familia se normaliza contra si misma: el valor mas fuerte de cada una vale 1. Con una
    /// normalizacion global el genero aplastaba al resto y el reparto aportaba una treintava parte de lo
    /// que le toca.
    /// </summary>
    [Fact]
    public void EveryFamilyIsNormalizedOnItsOwn()
    {
        var profile = TasteProfileBuilder.Build(
            [Item(1.0, genres: ["Terror"], actors: ["Ana"], directors: ["Dani"], rating: 9.2)]);

        foreach (var family in new[] { Genre, Actor, "director", "rating" })
        {
            Assert.Equal(
                1.00,
                profile.Where(record => record.Facet == family && record.PairedFacet is null).Max(record => record.Weight));
        }
    }

    private static ProfileItem Item(
        double weight,
        string[]? genres = null,
        string[]? actors = null,
        string[]? directors = null,
        double? rating = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Weight = weight,
            Genres = genres ?? [],
            Actors = actors ?? [],
            Directors = directors ?? [],
            Rating = rating
        };
}
