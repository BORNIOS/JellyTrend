using Jellyfin.Plugin.JellyTrend.Services.Recommendation;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// La intensidad con la que se consume un titulo decide cuanto aprende el perfil de el. Estas pruebas
/// fijan las dos propiedades que importan: que la reescucha y el favorito refuercen sin desmadrarse, y
/// que el abandono solo cuente tras exposicion suficiente y con un castigo mucho menor que el premio.
/// </summary>
public sealed class InteractionWeightTests
{
    /// <summary>
    /// La fraccion reproducida define el peso base, incluido el caso de la reproduccion accidental.
    /// </summary>
    /// <param name="progress">Fraccion vista.</param>
    /// <param name="expected">Peso esperado.</param>
    [Theory]
    [InlineData(0.01, 0.00)]
    [InlineData(0.04, 0.00)]
    [InlineData(0.10, 0.10)]
    [InlineData(0.30, 0.35)]
    [InlineData(0.60, 0.65)]
    [InlineData(0.85, 0.90)]
    [InlineData(0.99, 1.00)]
    [InlineData(1.00, 1.00)]
    public void ProgressDecidesTheBaseWeight(double progress, double expected)
        => Assert.Equal(expected, InteractionWeight.Compute(progress, playCount: 1, favorite: false, abandoned: false));

    /// <summary>
    /// Tres reproducciones y favorito pesan el doble que una sola: 1.60 x 1.30.
    /// </summary>
    [Fact]
    public void RewatchAndFavoriteMultiplyTheContribution()
    {
        var once = InteractionWeight.Compute(1.0, playCount: 1, favorite: false, abandoned: false);
        var threeTimesFavorite = InteractionWeight.Compute(1.0, playCount: 3, favorite: true, abandoned: false);

        Assert.Equal(1.00, once);
        Assert.Equal(2.08, threeTimesFavorite, precision: 2);
        Assert.True(threeTimesFavorite > once * 2 - 0.01);
    }

    /// <summary>
    /// El multiplicador de reescucha sube por vuelta y se topa: una obsesion no puede arrasar el perfil.
    /// </summary>
    /// <param name="playCount">Veces reproducida.</param>
    /// <param name="expected">Multiplicador esperado.</param>
    [Theory]
    [InlineData(1, 1.00)]
    [InlineData(2, 1.35)]
    [InlineData(3, 1.60)]
    [InlineData(4, 1.80)]
    [InlineData(40, 1.80)]
    public void RewatchMultiplierGrowsAndCaps(int playCount, double expected)
        => Assert.Equal(expected, InteractionWeight.FromPlayCount(playCount), precision: 2);

    /// <summary>
    /// Un abandono con exposicion real penaliza poco; por debajo del umbral se ignora, no se castiga.
    /// </summary>
    [Fact]
    public void AbandonPenalizesOnlyAfterRealExposure()
    {
        Assert.Equal(0.00, InteractionWeight.Compute(0.04, playCount: 1, favorite: false, abandoned: true));
        Assert.Equal(0.10, InteractionWeight.Compute(0.12, playCount: 1, favorite: false, abandoned: true));
        Assert.Equal(-0.20, InteractionWeight.Compute(0.30, playCount: 1, favorite: false, abandoned: true));
        Assert.Equal(-0.20, InteractionWeight.Compute(0.90, playCount: 1, favorite: false, abandoned: true));
    }
}
