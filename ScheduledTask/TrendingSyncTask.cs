using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTrend.ExternalAPI;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.ScheduledTask;

public sealed class TrendingSyncTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly TmdbClient _tmdbClient;
    private readonly ILogger<TrendingSyncTask> _logger;

    public TrendingSyncTask(
        ILibraryManager libraryManager,
        TmdbClient tmdbClient,
        ILogger<TrendingSyncTask> logger)
    {
        _libraryManager = libraryManager;
        _tmdbClient = tmdbClient;
        _logger = logger;
    }

    public string Name        => "JellyTrend: Sync Trending Content";
    public string Description => "Fetches trending movies from TMDB and caches matched library items.";
    public string Category    => "JellyTrend";
    public string Key         => "JellyTrendSync";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;

        if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            _logger.LogWarning("JellyTrend: TMDB API key no configurada — sync omitido.");
            return;
        }

        progress.Report(0);
        _logger.LogDebug("JellyTrend: Iniciando sync (máximo {Max} películas desde TMDB).", config.MaxItems);

        // ── 1. Obtener IDs trending de TMDB (semanal, solo películas) ──────────
        var trendingMovies = await _tmdbClient
            .GetTrendingMoviesAsync(config.TmdbApiKey, config.MaxItems, config.TmdbLanguage, config.TmdbRegion, cancellationToken)
            .ConfigureAwait(false);
        progress.Report(30);

        // ── 2. Emparejar con la librería local ─────────────────────────────────
        var matchedIds = new List<Guid>();

        foreach (var tmdbId in trendingMovies.Select(m => m.Id.ToString()))
        {
            var match = FindByTmdbId(tmdbId, BaseItemKind.Movie);
            if (match is not null)
            {
                matchedIds.Add(match.Id);
                _logger.LogDebug("JellyTrend: Match '{Name}' (TMDB {Id})", match.Name, tmdbId);
            }
        }
        progress.Report(70);

        _logger.LogInformation(
            "JellyTrend: {Matched} películas emparejadas de {Total} en TMDB trending semanal.",
            matchedIds.Count, trendingMovies.Count);

        // ── 3. Guardar caché JSON ───────────────────────────────────────────────
        var cache    = new TrendingCache { ItemIds = matchedIds, LastUpdated = DateTime.UtcNow };
        var dataPath = Path.Combine(Plugin.Instance!.PluginFolder, "trending.json");
        await File.WriteAllTextAsync(
            dataPath,
            JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        progress.Report(100);
        _logger.LogInformation("JellyTrend: Sync completo — {Count} películas en caché.", matchedIds.Count);
    }

    private BaseItem? FindByTmdbId(string tmdbId, BaseItemKind kind)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Tmdb"] = tmdbId },
            IncludeItemTypes = [kind],
            IsVirtualItem    = false
        }).FirstOrDefault();
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var intervalHours = Plugin.Instance?.Configuration.SyncIntervalHours ?? 24;
        return
        [
            new TaskTriggerInfo
            {
                Type          = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(intervalHours).Ticks
            }
        ];
    }
}
