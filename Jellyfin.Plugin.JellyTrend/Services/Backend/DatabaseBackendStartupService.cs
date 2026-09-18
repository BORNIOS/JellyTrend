using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Services.Backend;

/// <summary>
/// Detects and exercises the optional database backend when the server starts.
/// </summary>
/// <remarks>
/// Detection and use happen here rather than inside each task so the plugin knows from the start which
/// backend it will talk to. Registration alone proves nothing: a provider whose queries return no rows
/// looks exactly like a working one until it is asked, so the probe runs a real query and records how many
/// rows came back. Consumers (recommendations, trending) then read the resulting state instead of printing
/// that a provider exists.
/// </remarks>
internal sealed class DatabaseBackendStartupService : IHostedService
{
    private const int ProbeSampleSize = 5;

    private readonly DatabaseBackend _backend;
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseBackendStartupService"/> class.
    /// </summary>
    /// <param name="backend">Shared backend state.</param>
    /// <param name="services">The server service provider, where the provider plugins register their backends.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public DatabaseBackendStartupService(
        DatabaseBackend backend,
        IServiceProvider services,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory)
    {
        _backend = backend;
        _services = services;
        _loggerFactory = loggerFactory;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = loggerFactory.CreateLogger<DatabaseBackendStartupService>();
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _backend.Detect(_services, _loggerFactory);

        // Una sola linea en INFO: quien es el backend y como esta. El detalle, en Debug.
        _logger.LogDebug("[JellyTrend] Backend detectado: {Recommendation} | {Index}", _backend.Recommendation, _backend.Summary);

        if (_backend.RecommendationProvider is not null || _backend.LibraryIndex is not null)
        {
            // La comprobacion consulta la base de datos: va en segundo plano para no retrasar el arranque
            // del servidor, y todo fallo queda dentro de Probe.
            _ = Task.Run(Probe, CancellationToken.None);
        }
        else
        {
            // Sin nada que comprobar, la unica linea informativa se da aqui.
            Announce();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Exercises the detected backends with real queries and records the outcome.
    /// </summary>
    internal void Probe()
    {
        try
        {
            var user = _userManager.GetUsers().FirstOrDefault();
            if (user is null)
            {
                _backend.InconclusiveRecommendations("no hay usuarios con los que comprobar");
                return;
            }

            ProbeRecommendations(user);
            ProbeIndex();
        }
        catch (Exception ex)
        {
            _backend.RejectRecommendations($"error en la comprobacion ({ex.GetType().Name})");
            _logger.LogWarning("[JellyTrend] No se pudo comprobar el proveedor de base de datos ({Type}); se usa ILibraryManager.", ex.GetType().Name);
            _logger.LogDebug(ex, "[JellyTrend] Detalle de la comprobacion fallida del proveedor.");
        }
        finally
        {
            Announce();
        }
    }

    private void ProbeRecommendations(User user)
    {
        if (_backend.RecommendationProvider is null)
        {
            return;
        }

        var sample = _backend.RecommendationProvider.GetRandomUnwatchedMovies(user.Id, [], ProbeSampleSize);
        if (sample.Count > 0)
        {
            _backend.VerifyRecommendations(sample.Count);
            return;
        }

        if (!HasUnwatchedMovies(user))
        {
            _backend.InconclusiveRecommendations("la biblioteca no tiene peliculas sin ver");
            return;
        }

        _backend.RejectRecommendations("devolvio 0 candidatos");

        // El proveedor atrapa sus propios errores y devuelve vacio, asi que aqui no se puede saber la causa:
        // se dice lo medido y donde mirar, sin inventar el motivo.
        _logger.LogWarning(
            "[JellyTrend] El proveedor de base de datos no devolvio candidatos; se usa ILibraryManager. El motivo queda en el log del proveedor.");
    }

    private void ProbeIndex()
    {
        if (_backend.LibraryIndex is null)
        {
            return;
        }

        var sampleItem = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie],
            Limit = 5
        }).FirstOrDefault(static item => item.ProviderIds.ContainsKey("Tmdb"));

        if (sampleItem is null || !sampleItem.ProviderIds.TryGetValue("Tmdb", out var tmdbId) || string.IsNullOrWhiteSpace(tmdbId))
        {
            _backend.InconclusiveIndex("no hay peliculas con id TMDB para comprobar");
            return;
        }

        var resolved = _backend.LibraryIndex.GetItemIdsByTmdbIds([tmdbId], BaseItemKind.Movie);
        if (resolved.Count == 1 && resolved.Values.First() == sampleItem.Id)
        {
            _backend.VerifyIndex(resolved.Count);
            return;
        }

        _backend.RejectIndex("no resolvio un id TMDB que si esta en la biblioteca");
        _logger.LogWarning(
            "[JellyTrend] El indice del proveedor de base de datos no resuelve los ids de TMDB de la biblioteca; las tendencias se emparejaran consultando la biblioteca item por item.");
    }

    private bool HasUnwatchedMovies(User user)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie],
            IsPlayed = false,
            Limit = 1
        }).Count > 0;

    private void Announce()
    {
        _logger.LogInformation("[JellyTrend] Backend: {Backend}.", _backend.Recommendation);
        JellyTrendLog.Info($"[JellyTrend] Backend: {_backend.Recommendation}.");
    }
}
