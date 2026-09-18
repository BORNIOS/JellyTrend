using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend.Controllers;
using Jellyfin.Plugin.JellyTrend.Logging;
using Jellyfin.Plugin.JellyTrend.Services;
using Jellyfin.Plugin.JellyTrend.Services.Backend;
using Jellyfin.Plugin.JellyTrend.Services.Channel;
using Jellyfin.Plugin.JellyTrend.Services.Models;
using Jellyfin.Plugin.JellyTrend.Services.Recommendation;
using Jellyfin.Plugin.JellyTrend.Services.Store;
using Jellyfin.Plugin.JellyTrend.Services.Sync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Tasks;

/// <summary>
/// Weekly task that analyzes each user's watch history and builds a personalized
/// "Recomendados" row, persisted per user under the plugin folder.
/// </summary>
public sealed class RecommendationSyncTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly DatabaseBackend _backend;
    private readonly ChannelShadowMaterializer _shadowMaterializer;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecommendationSyncTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="backend">Backend de base de datos detectado al arrancar el plugin.</param>
    /// <param name="shadowMaterializer">Crea las sombras de los canales antes de copiarles metadatos.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public RecommendationSyncTask(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        DatabaseBackend backend,
        ChannelShadowMaterializer shadowMaterializer,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _backend = backend;
        _shadowMaterializer = shadowMaterializer;
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

        // Historia de la corrida en el almacen: la tabla sync_run deja de estar siempre vacia.
        var runId = JellyTrendStore.BeginRun("recommendations");
        var stopwatch = Stopwatch.StartNew();
        var generated = 0;
        var failed = 0;

        // La cache de caracteristicas por item se guarda en disco al terminar, de modo que un reinicio
        // del servidor no obligue a releer personas y facetas de toda la biblioteca.
        var features = FeatureStore.Open(JellyTrendStorage.Folder);

        try
        {
            var trendingItemIds = LoadTrendingItemIds();
            progress.Report(0);

            // El backend se detecto y se comprobo al arrancar el plugin. Aqui solo se lee su resultado:
            // el estado del run es compartido para que un proveedor que falle se descarte UNA vez, en
            // lugar de reintentarse con cada usuario.
            var providerState = _backend.CreateRunState();
            _logger.LogDebug("[Recomendaciones] Backend: {Backend}.", _backend.Recommendation);

            // Se genera un pool mayor que la fila visible: los ids ya vistos se descartan al servir y
            // asi la fila sigue llena en lugar de quedarse corta hasta la proxima corrida semanal. El
            // factor es configurable porque tambien es el material que usa la rotacion.
            var poolSize = Math.Max(1, config.RecommendationMaxItems) * Math.Clamp(config.RecommendationPoolFactor, 1, 10);

            var allRecommendedIds = new List<Guid>();

            for (var i = 0; i < users.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var user = users[i];

                try
                {
                    // Capa 1 y capa 2: el perfil se reconstruye desde el historial del usuario (bootstrap la
                    // primera vez, incremental despues) y queda guardado. La fila deja de partir del
                    // historial crudo: parte del perfil.
                    TasteProfileService.Rebuild(
                        user,
                        _libraryManager,
                        _userDataManager,
                        id => features.TryGet(id, out var cached) ? cached : null,
                        DateTime.UtcNow);

                    var result = RecommendationEngine.BuildRecommendations(
                        _libraryManager,
                        _userDataManager,
                        user,
                        trendingItemIds,
                        poolSize,
                        _logger,
                        features,
                        providerState,
                        _userManager);

                    _logger.LogInformation("[Recomendaciones] '{User}': {Diagnostics}", user.Username, result.Diagnostics);

                    RecommendationStorage.Write(user.Id, new UserRecommendations
                    {
                        ItemIds = [.. result.ItemIds],
                        UpdatedAt = DateTime.UtcNow
                    });

                    allRecommendedIds.AddRange(result.ItemIds);
                    generated++;
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
                // Primero se crean las sombras del canal (si un cliente no lo ha abierto nunca, no
                // existen), y solo despues se les copian metadatos, reparto e imagenes locales.
                var channels = ChannelIdentity.GetAllChannelFolderIds(_libraryManager).ToList();
                var materialized = await _shadowMaterializer
                    .MaterializeAsync(channels, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogDebug("[Recomendaciones] Sombras materializadas en {Channels} canales: {Count} elementos.", channels.Count, materialized);

                await TrendingShadowMetadataSync
                    .SyncAllAsync(_libraryManager, allRecommendedIds, _logger, cancellationToken)
                    .ConfigureAwait(false);
            }

            scope.Complete($"{generated} usuarios con recomendaciones, {failed} con errores de {users.Count}");
            JellyTrendStore.CompleteRun(
                runId,
                failed == 0 ? "ok" : generated > 0 ? "partial" : "failed",
                generated,
                failed,
                (int)stopwatch.ElapsedMilliseconds,
                null);

            _logger.LogInformation("[Recomendaciones] Cache de caracteristicas: {Count} peliculas.", features.Count);
        }
        catch (OperationCanceledException)
        {
            scope.Cancel("cancelada (usuario o apagado del servidor)");
            JellyTrendStore.CompleteRun(runId, "canceled", generated, failed, (int)stopwatch.ElapsedMilliseconds, null);
            throw;
        }
        catch (Exception ex)
        {
            scope.Fail(ex, "error al procesar recomendaciones");
            JellyTrendStore.CompleteRun(runId, "failed", generated, failed, (int)stopwatch.ElapsedMilliseconds, ex.Message);
            throw;
        }
        finally
        {
            // Aunque falle la ejecucion, lo aprendido de la biblioteca se guarda: la proxima vez
            // empezara con la cache caliente.
            features.Flush();
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
        => JellyTrendStore.ReadTrendingCache()?.Items.Select(static entry => entry.ItemId).ToHashSet() ?? [];
}
