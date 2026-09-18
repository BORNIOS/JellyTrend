extern alias fakeprovider;

using System;
using System.Collections.Generic;

using Jellyfin.Plugin.JellyTrend.Services.Backend;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// Ningún plugin se presenta ante otro: el que necesita el dato lo busca. Estas pruebas usan un plugin de
/// mentira que replica el contrato con el mismo nombre completo pero en otro ensamblado (exactamente lo que
/// hace el plugin de PostgreSQL) y comprueban que JellyTrend lo encuentra y lo usa igual.
/// </summary>
public class ForeignProviderDiscoveryTests
{
    [Fact]
    public void EncuentraElProveedorDeOtroPluginAunqueNoCompartaTipos()
    {
        LoadFakePlugin();

        var backend = new DatabaseBackend();
        backend.Detect(new NoServices(), NullLoggerFactory.Instance);

        Assert.True(backend.RecommendationUsable);
        Assert.NotNull(backend.RecommendationProvider);
        Assert.Contains("Jellyfin.Plugin.JellyTrend.FakeProvider", backend.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void LasRespuestasDelOtroPluginLleganTraducidasANuestroTipo()
    {
        LoadFakePlugin();

        var backend = new DatabaseBackend();
        backend.Detect(new NoServices(), NullLoggerFactory.Instance);

        var items = backend.RecommendationProvider!.GetUnwatchedMoviesByGenres(
            Guid.NewGuid(),
            ["Terror"],
            [],
            3);

        Assert.Equal(3, items.Count);
        Assert.Equal("falso-1", items[0].TmdbId);
        Assert.Equal(["Terror"], items[0].Genres);
        Assert.Equal(["prueba"], items[0].Tags);
        Assert.Equal(7.5f, items[0].CommunityRating);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), items[0].PremiereDate);
    }

    [Fact]
    public void UnContratoQueNingunPluginOfreceQuedaComoNoDisponible()
    {
        var backend = new DatabaseBackend();
        backend.Detect(new NoServices(), NullLoggerFactory.Instance);

        // El plugin de mentira solo replica el contrato de recomendaciones, no el indice de biblioteca.
        Assert.False(backend.IndexUsable);
        Assert.Null(backend.UsableLibraryIndex);
    }

    // Como en Jellyfin: el ensamblado del otro plugin esta cargado en el proceso.
    private static void LoadFakePlugin()
        => _ = typeof(fakeprovider::Jellyfin.Plugin.JellyTrend.Api.FakeRecommendationQueryProvider).Assembly;

    /// <summary>Contenedor vacío: reproduce un Jellyfin donde nadie registró nada.</summary>
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
