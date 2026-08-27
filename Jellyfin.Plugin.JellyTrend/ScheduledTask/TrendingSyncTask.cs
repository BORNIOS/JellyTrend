using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTrend.ExternalAPI;
using Jellyfin.Plugin.JellyTrend.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.ScheduledTask;

/// <summary>
/// Scheduled task that fetches TMDB trending content and caches matched library items.
/// </summary>
public sealed class TrendingSyncTask : IScheduledTask
{
    private static readonly Uri TmdbBackdropBaseUrl = BuildTmdbImageBaseUri("original");
    private static readonly Uri TmdbPosterBaseUrl = BuildTmdbImageBaseUri("w780");

    private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = true };

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly TmdbClient _tmdbClient;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrendingSyncTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="tmdbClient">The TMDB client.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public TrendingSyncTask(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        TmdbClient tmdbClient,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _tmdbClient = tmdbClient;
        _logger = loggerFactory.CreateLogger<TrendingSyncTask>();
    }

    /// <inheritdoc />
    public string Name => "JellyTrend: Sync Trending Content";

    /// <inheritdoc />
    public string Description => "Fetches trending movies and TV shows from TMDB and caches matched library items.";

    /// <inheritdoc />
    public string Category => "JellyTrend";

    /// <inheritdoc />
    public string Key => "JellyTrendSync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;

        if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            _logger.LogWarning("TMDB API key no configurada — sync omitido.");
            return;
        }

        using var scope = JellyTrendLog.TaskScope.Begin("Sync de tendencias TMDB");
        try
        {
            progress.Report(0);
            _logger.LogDebug("Máximo {Max} títulos por tipo desde TMDB.", config.MaxItems);

            // ── 1. Obtener IDs trending de TMDB (semanal, películas y series) ──
            var trendingMovies = await _tmdbClient
                .GetTrendingMoviesAsync(config.TmdbApiKey, config.MaxItems, config.TmdbLanguage, config.TmdbRegion, cancellationToken)
                .ConfigureAwait(false);
            var trendingShows = await _tmdbClient
                .GetTrendingTvAsync(config.TmdbApiKey, config.MaxItems, config.TmdbLanguage, config.TmdbRegion, cancellationToken)
                .ConfigureAwait(false);
            progress.Report(30);

            // ── 2. Emparejar con la librería local ─────────────────────────────
            var matchedItems = new List<TrendingCacheEntry>();

            foreach (var tmdbItem in trendingMovies)
            {
                var tmdbId = tmdbItem.Id.ToString(CultureInfo.InvariantCulture);
                var match = FindByTmdbId(tmdbId, BaseItemKind.Movie);
                if (match is not null)
                {
                    matchedItems.Add(new TrendingCacheEntry
                    {
                        ItemId = match.Id,
                        MediaType = TrendingMediaType.Movie,
                        TmdbId = tmdbId,
                        TmdbBackdropPath = tmdbItem.BackdropPath,
                        TmdbPosterPath = tmdbItem.PosterPath
                    });
                    _logger.LogDebug("Match '{Name}' (TMDB {Id})", match.Name, tmdbId);
                }
            }

            foreach (var tmdbItem in trendingShows)
            {
                var tmdbId = tmdbItem.Id.ToString(CultureInfo.InvariantCulture);
                var match = FindByTmdbId(tmdbId, BaseItemKind.Series);
                if (match is not null)
                {
                    matchedItems.Add(new TrendingCacheEntry
                    {
                        ItemId = match.Id,
                        MediaType = TrendingMediaType.Series,
                        TmdbId = tmdbId,
                        TmdbBackdropPath = tmdbItem.BackdropPath,
                        TmdbPosterPath = tmdbItem.PosterPath
                    });
                    _logger.LogDebug("Match serie '{Name}' (TMDB {Id})", match.Name, tmdbId);
                }
            }

            progress.Report(70);

            var movieCount = matchedItems.Count(static item => item.MediaType == TrendingMediaType.Movie);
            var seriesCount = matchedItems.Count(static item => item.MediaType == TrendingMediaType.Series);

            // ── 3. Completar imágenes locales faltantes (patrón oficial Jellyfin) ─
            foreach (var matched in matchedItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EnsureLocalImagesAsync(matched, cancellationToken).ConfigureAwait(false);
            }

            progress.Report(85);

            // ── 4. Guardar caché JSON ───────────────────────────────────────────
            var cache = new TrendingCache { Items = matchedItems, LastUpdated = DateTime.UtcNow };
            var dataPath = Path.Combine(Plugin.Instance.PluginFolder, "trending.json");
            await File.WriteAllTextAsync(
                dataPath,
                JsonSerializer.Serialize(cache, CacheJsonOptions),
                cancellationToken).ConfigureAwait(false);

            await TrendingShadowMetadataSync
                .SyncAllAsync(_libraryManager, matchedItems, _logger, cancellationToken)
                .ConfigureAwait(false);

            progress.Report(100);
            scope.Complete($"{movieCount} películas y {seriesCount} series emparejadas de {trendingMovies.Count}+{trendingShows.Count} de TMDB");
        }
        catch (OperationCanceledException)
        {
            scope.Cancel("cancelada (usuario o apagado del servidor)");
            throw;
        }
        catch (Exception ex)
        {
            scope.Fail(ex, "error al sincronizar tendencias");
            throw;
        }
    }

    private async Task EnsureLocalImagesAsync(TrendingCacheEntry cacheEntry, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(cacheEntry.ItemId);
        if (item is null)
        {
            return;
        }

        var backdropUrl = BuildTmdbImageUrl(cacheEntry.TmdbBackdropPath, TmdbBackdropBaseUrl);
        var posterUrl = BuildTmdbImageUrl(cacheEntry.TmdbPosterPath, TmdbPosterBaseUrl);

        try
        {
            if (!item.HasImage(ImageType.Backdrop, 0) && !string.IsNullOrWhiteSpace(backdropUrl))
            {
                await _providerManager
                    .SaveImage(item, backdropUrl, ImageType.Backdrop, null, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!item.HasImage(ImageType.Primary, 0) && !string.IsNullOrWhiteSpace(posterUrl))
            {
                await _providerManager
                    .SaveImage(item, posterUrl, ImageType.Primary, null, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "No se pudieron completar imágenes para '{Name}' ({ItemId}).",
                item.Name,
                item.Id);
        }
    }

    private static string? BuildTmdbImageUrl(string? imagePath, Uri baseUrl)
        => string.IsNullOrWhiteSpace(imagePath) ? null : new Uri(baseUrl, imagePath).ToString();

    private static Uri BuildTmdbImageBaseUri(string size)
        => new(string.Concat("https", "://", "image.tmdb.org", "/t/p/", size));

    private BaseItem? FindByTmdbId(string tmdbId, BaseItemKind kind)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Tmdb"] = tmdbId },
            IncludeItemTypes = [kind],
            IsVirtualItem = false,
            Limit = 10
        });

        foreach (var item in items)
        {
            // Excluir sombras de canal (copian el Tmdb provider id y no se marcan como virtuales)
            // y validar que el tipo coincida: el provider puede ignorar IncludeItemTypes en algunas
            // combinaciones de filtros.
            if (item.ChannelId == Guid.Empty && MatchesKind(item, kind))
            {
                return item;
            }
        }

        return null;
    }

    private static bool MatchesKind(BaseItem item, BaseItemKind kind)
        => kind switch
        {
            BaseItemKind.Movie => item is Movie,
            BaseItemKind.Series => item is Series,
            _ => true
        };

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // El horario real se gestiona desde Dashboard → Tareas; esto es solo el intervalo
        // por defecto (24 h) para la primera instalación.
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        ];
    }
}
