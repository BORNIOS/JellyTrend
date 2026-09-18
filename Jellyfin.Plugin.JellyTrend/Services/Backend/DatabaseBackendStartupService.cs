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
        Announce("Backend de base de datos detectado al arrancar el plugin");

        if (_backend.RecommendationProvider is not null || _backend.LibraryIndex is not null)
        {
            // La comprobacion consulta la base de datos: va en segundo plano para no retrasar el arranque
            // del servidor, y todo fallo queda dentro de Probe.
            _ = Task.Run(Probe, CancellationToken.None);
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
            _backend.RejectRecommendations($"la comprobacion fallo ({ex.GetType().Name})");
            _logger.LogWarning(ex, "[JellyTrend] La comprobacion del proveedor de base de datos fallo; se usara ILibraryManager.");
        }
        finally
        {
            Announce("Comprobacion del backend terminada");
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

        _backend.RejectRecommendations("devolvio 0 candidatos aunque la biblioteca tiene peliculas sin ver");

        // Caso real de la linea 10.11: la base guarda el nombre del tipo CLR en BaseItems."Type", asi que
        // una consulta que filtra por Type = 'Movie' no devuelve ninguna fila. Se dice aqui, con el aviso,
        // para que el sintoma no vuelva a parecer un problema del motor de recomendaciones.
        _logger.LogWarning(
            "[JellyTrend] El proveedor de base de datos esta instalado pero no ve las peliculas de la biblioteca; se usara ILibraryManager. Revise en el plugin de base de datos los filtros por tipo de item (BaseItems.\"Type\" guarda el nombre del tipo CLR, no 'Movie').");
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

    private void Announce(string message)
    {
        _logger.LogInformation("[JellyTrend] {Message}: {Summary}", message, _backend.Summary);
        JellyTrendLog.Info($"[JellyTrend] {message}: {_backend.Summary}");
    }
}
