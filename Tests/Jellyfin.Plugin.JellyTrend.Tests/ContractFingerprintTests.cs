using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Jellyfin.Plugin.JellyTrend.Api;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// Huella del contrato cruzado entre JellyTrend y el plugin de PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// El otro plugin compila su propia copia de esta interfaz (mismo nombre completo, otro ensamblado) y
/// JellyTrend lo encuentra buscandola entre los ensamblados cargados, enlazando cada llamada por nombre.
/// Eso convierte los nombres en el contrato de facto: renombrar un metodo, cambiar un parametro o tocar las
/// propiedades de la proyeccion rompe la integracion sin error de compilacion en ninguno de los dos lados.
/// </para>
/// <para>
/// Esta prueba, junto con su gemela en el repo del plugin de PostgreSQL, obliga a que las dos versiones
/// avancen alineadas: si un lado cambia la forma, aqui falla y hay que actualizar el otro a la vez. La
/// dependencia sigue siendo opcional en tiempo de ejecucion: sin el otro plugin, JellyTrend funciona con
/// ILibraryManager.
/// </para>
/// </remarks>
public class ContractFingerprintTests
{
    [Fact]
    public void ElNombreDelContratoNoCambia()
    {
        Assert.Equal("Jellyfin.Plugin.JellyTrend.Api.IRecommendationQueryProvider", typeof(IRecommendationQueryProvider).FullName);
        Assert.Equal("Jellyfin.Plugin.JellyTrend.Api.RecommendationItem", typeof(RecommendationItem).FullName);
    }

    [Fact]
    public void ElContratoDeRecomendacionesTieneEstaForma()
    {
        Assert.Equal(
            [
                "GetPlayedMovies(Guid, Int32) -> IReadOnlyList<RecommendationItem>",
                "GetRandomUnwatchedMovies(Guid, IReadOnlyList<Guid>, Int32) -> IReadOnlyList<RecommendationItem>",
                "GetResumableMovies(Guid, Int32) -> IReadOnlyList<RecommendationItem>",
                "GetUnwatchedMoviesByGenres(Guid, IReadOnlyList<String>, IReadOnlyList<Guid>, Int32) -> IReadOnlyList<RecommendationItem>",
                "GetUnwatchedMoviesByPersons(Guid, IReadOnlyList<Guid>, IReadOnlyList<Guid>, Int32) -> IReadOnlyList<RecommendationItem>",
                "GetUnwatchedMoviesByTags(Guid, IReadOnlyList<String>, IReadOnlyList<Guid>, Int32) -> IReadOnlyList<RecommendationItem>"
            ],
            typeof(IRecommendationQueryProvider).GetMethods().Select(Describe).OrderBy(static text => text, StringComparer.Ordinal));
    }

    [Fact]
    public void LaProyeccionQueCruzaLosPluginsTieneEstasPropiedades()
    {
        Assert.Equal(
            [
                "CommunityRating: Nullable<Single>",
                "Genres: IReadOnlyList<String>",
                "Id: Guid",
                "PremiereDate: Nullable<DateTime>",
                "Studios: IReadOnlyList<String>",
                "Tags: IReadOnlyList<String>",
                "TmdbId: String"
            ],
            typeof(RecommendationItem).GetProperties().Select(static property => $"{property.Name}: {Friendly(property.PropertyType)}").OrderBy(static text => text, StringComparer.Ordinal));
    }

    [Fact]
    public void ElContratoDelIndiceDeBibliotecaTieneEstaForma()
    {
        Assert.Equal("Jellyfin.Plugin.JellyTrend.Api.ILibraryIndexQueryProvider", typeof(ILibraryIndexQueryProvider).FullName);
        Assert.Equal(
            ["GetItemIdsByTmdbIds(IReadOnlyList<String>, BaseItemKind) -> IReadOnlyDictionary<String, Guid>"],
            typeof(ILibraryIndexQueryProvider).GetMethods().Select(Describe));
    }

    private static string Describe(MethodInfo method)
        => $"{method.Name}({string.Join(", ", method.GetParameters().Select(static parameter => Friendly(parameter.ParameterType)))}) -> {Friendly(method.ReturnType)}";

    private static string Friendly(Type type)
    {
        if (type.IsGenericType && type.Name.Contains('`', StringComparison.Ordinal))
        {
            var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Friendly))}>";
        }

        return type.Name;
    }
}
