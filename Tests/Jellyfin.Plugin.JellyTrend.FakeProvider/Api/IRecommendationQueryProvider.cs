using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTrend.Api;

/// <summary>
/// Copia local del contrato de JellyTrend, exactamente como la hace otro plugin (mismo nombre completo,
/// ensamblado distinto). Su proposito es que el tipo que usan las pruebas no sea el de JellyTrend.
/// </summary>
public interface IRecommendationQueryProvider
{
    IReadOnlyList<RecommendationItem> GetPlayedMovies(Guid userId, int limit);

    IReadOnlyList<RecommendationItem> GetResumableMovies(Guid userId, int limit);

    IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByGenres(
        Guid userId,
        IReadOnlyList<string> genres,
        IReadOnlyList<Guid> topParentIds,
        int limit);

    IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByPersons(
        Guid userId,
        IReadOnlyList<Guid> personIds,
        IReadOnlyList<Guid> topParentIds,
        int limit);

    IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByTags(
        Guid userId,
        IReadOnlyList<string> tags,
        IReadOnlyList<Guid> topParentIds,
        int limit);

    IReadOnlyList<RecommendationItem> GetRandomUnwatchedMovies(
        Guid userId,
        IReadOnlyList<Guid> topParentIds,
        int limit);
}

/// <summary>
/// Proyección mínima, con los mismos nombres de propiedad que la de JellyTrend.
/// </summary>
/// <param name="Id">Identificador del item.</param>
/// <param name="TmdbId">Identificador de TMDB.</param>
/// <param name="Genres">Géneros.</param>
/// <param name="Tags">Etiquetas.</param>
/// <param name="Studios">Estudios.</param>
/// <param name="CommunityRating">Valoración.</param>
/// <param name="PremiereDate">Fecha de estreno.</param>
public sealed record RecommendationItem(
    Guid Id,
    string? TmdbId,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Studios,
    float? CommunityRating,
    DateTime? PremiereDate);
