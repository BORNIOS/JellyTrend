using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Api;

/// <summary>
/// Proveedor de mentira, con la forma que tiene el de PostgreSQL: clase sellada y constructor con
/// <see cref="ILogger{T}"/>.
/// </summary>
public sealed class FakeRecommendationQueryProvider : IRecommendationQueryProvider
{
    /// <summary>Prefix of the TMDB ids this fake reports, so tests can recognize its answers.</summary>
    public const string TmdbIdPrefix = "falso-";

    /// <summary>Number of candidates the random query returns.</summary>
    public const int SampleSize = 4;

    private readonly ILogger<FakeRecommendationQueryProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeRecommendationQueryProvider"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public FakeRecommendationQueryProvider(ILogger<FakeRecommendationQueryProvider> logger) => _logger = logger;

    /// <summary>Gets the number of queries received, to prove the provider is really being used.</summary>
    public int Queries { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<RecommendationItem> GetPlayedMovies(Guid userId, int limit) => Answer(limit);

    /// <inheritdoc />
    public IReadOnlyList<RecommendationItem> GetResumableMovies(Guid userId, int limit) => [];

    /// <inheritdoc />
    public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByGenres(Guid userId, IReadOnlyList<string> genres, IReadOnlyList<Guid> topParentIds, int limit)
        => Answer(limit);

    /// <inheritdoc />
    public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByPersons(Guid userId, IReadOnlyList<Guid> personIds, IReadOnlyList<Guid> topParentIds, int limit)
        => Answer(limit);

    /// <inheritdoc />
    public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByTags(Guid userId, IReadOnlyList<string> tags, IReadOnlyList<Guid> topParentIds, int limit)
        => Answer(limit);

    /// <inheritdoc />
    public IReadOnlyList<RecommendationItem> GetRandomUnwatchedMovies(Guid userId, IReadOnlyList<Guid> topParentIds, int limit)
        => Answer(Math.Min(limit, SampleSize));

    private IReadOnlyList<RecommendationItem> Answer(int limit)
    {
        Queries++;
        _logger.LogDebug("Consulta de prueba {Queries} ({Limit})", Queries, limit);

        return Enumerable.Range(1, Math.Max(limit, 0))
            .Select(index => new RecommendationItem(
                new Guid(index, 7, 7, new byte[8]),
                TmdbIdPrefix + index,
                ["Terror"],
                ["prueba"],
                ["Estudio"],
                7.5f,
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)))
            .ToList();
    }
}
