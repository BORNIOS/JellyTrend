using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend.Api;
using Jellyfin.Plugin.JellyTrend.Sync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.ScheduledTask;

/// <summary>
/// Weekly task that analyzes each user's watch history and builds a personalized
/// "Recomendados" row, persisted per user under the plugin folder.
/// </summary>
public sealed class RecommendationSyncTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecommendationSyncTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public RecommendationSyncTask(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _logger = loggerFactory.CreateLogger<RecommendationSyncTask>();
    }

    /// <inheritdoc />
    public string Name => "JellyTrend: Build Recommendations";

    /// <inheritdoc />
    public string Description => "Analyzes each user's watch history and builds a personalized 'Recomendados' row (hides already-watched and in-progress items).";

    /// <inheritdoc />
    public string Category => "JellyTrend";

    /// <inheritdoc />
    public string Key => "JellyTrendRecommendations";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!config.EnableRecommendationRow)
        {
            _logger.LogInformation("Fila de recomendaciones desactivada — tarea omitida.");
            return;
        }

        var users = _userManager.GetUsers().ToList();
        if (users.Count == 0)
        {
            _logger.LogInformation("Sin usuarios — tarea de recomendaciones omitida.");
            return;
        }

        using var scope = JellyTrendLog.TaskScope.Begin("Recomendaciones semanales");
        try
        {
            var trendingItemIds = LoadTrendingItemIds();
            progress.Report(0);

            var allRecommendedIds = new List<Guid>();
            var generated = 0;
            var failed = 0;
            for (var i = 0; i < users.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var user = users[i];

                try
                {
                    var ids = RecommendationEngine.BuildRecommendations(
                        _libraryManager,
                        _userDataManager,
                        user,
                        trendingItemIds,
                        config.RecommendationMaxItems);

                    RecommendationStorage.Write(user.Id, new UserRecommendations
                    {
                        ItemIds = ids,
                        UpdatedAt = DateTime.UtcNow
                    });

                    allRecommendedIds.AddRange(ids);
                    generated++;
                    _logger.LogDebug("{Count} recomendaciones para '{User}'.", ids.Count, user.Username);
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex, "Fallo al generar recomendaciones para '{User}'.", user.Username);
                }

                progress.Report((i + 1) * 100d / users.Count);
            }

            // Copiar metadatos, reparto e imágenes LOCALES a los items sombra del canal de
            // Recomendados (mismo tratamiento que el canal de tendencias): así las tarjetas
            // muestran el poster local y el detalle no queda como un item sombra pobre.
            if (allRecommendedIds.Count > 0)
            {
                await TrendingShadowMetadataSync
                    .SyncAllAsync(_libraryManager, allRecommendedIds, _logger, cancellationToken)
                    .ConfigureAwait(false);
            }

            scope.Complete($"{generated} usuarios con recomendaciones, {failed} con errores de {users.Count}");
        }
        catch (OperationCanceledException)
        {
            scope.Cancel("cancelada (usuario o apagado del servidor)");
            throw;
        }
        catch (Exception ex)
        {
            scope.Fail(ex, "error al procesar recomendaciones");
            throw;
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // El horario real se gestiona desde Dashboard → Tareas; esto es solo el intervalo
        // por defecto (semanal = 168 h) para la primera instalación.
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(168).Ticks
            }
        ];
    }

    private static HashSet<Guid> LoadTrendingItemIds()
    {
        var path = Path.Combine(Plugin.Instance!.PluginFolder, "trending.json");
        if (!File.Exists(path))
        {
            return new HashSet<Guid>();
        }

        try
        {
            var cache = JsonSerializer.Deserialize<TrendingCache>(File.ReadAllText(path));
            cache?.Normalize();
            return cache?.Items.Select(static x => x.ItemId).ToHashSet() ?? new HashSet<Guid>();
        }
        catch
        {
            return new HashSet<Guid>();
        }
    }
}
