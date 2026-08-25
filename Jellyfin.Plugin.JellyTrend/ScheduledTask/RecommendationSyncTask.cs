using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend.Api;
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
    private readonly ILogger<RecommendationSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecommendationSyncTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{RecommendationSyncTask}"/> interface.</param>
    public RecommendationSyncTask(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILogger<RecommendationSyncTask> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _logger = logger;
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
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!config.EnableRecommendationRow)
        {
            _logger.LogInformation("JellyTrend: fila de recomendaciones desactivada — tarea omitida.");
            return Task.CompletedTask;
        }

        var trendingItemIds = LoadTrendingItemIds();
        var users = _userManager.GetUsers().ToList();
        if (users.Count == 0)
        {
            _logger.LogInformation("JellyTrend: sin usuarios — tarea de recomendaciones omitida.");
            return Task.CompletedTask;
        }

        progress.Report(0);
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

                _logger.LogInformation("JellyTrend: {Count} recomendaciones para '{User}'.", ids.Count, user.Username);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyTrend: fallo al generar recomendaciones para '{User}'.", user.Username);
            }

            progress.Report((i + 1) * 100d / users.Count);
        }

        _logger.LogInformation("JellyTrend: recomendaciones completadas para {Count} usuarios.", users.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var intervalHours = Plugin.Instance?.Configuration.RecommendationSyncIntervalHours ?? 168;
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(intervalHours).Ticks
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
