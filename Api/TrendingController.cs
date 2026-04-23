using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTrend.ScheduledTask;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Api;

/// <summary>
/// REST surface for JellyTrend.
///
/// Endpoints:
///   GET  /JellyTrend/Trending       — JSON array of matched library items
///   GET  /JellyTrend/Status         — plugin health/config summary
///   GET  /JellyTrend/jellyTrend.js  — serves the embedded carousel script
///   GET  /JellyTrend/jellyTrend.css — serves the embedded carousel stylesheet
/// </summary>
[ApiController]
[Route("JellyTrend")]
public sealed class TrendingController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TrendingController> _logger;

    public TrendingController(ILibraryManager libraryManager, ILogger<TrendingController> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    // ── Trending items ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the list of locally-matched trending items, ready for the carousel to consume.
    /// Requires an authenticated Jellyfin session.
    /// </summary>
    [HttpGet("Trending")]
    [Authorize]
    [ProducesResponseType(typeof(IEnumerable<TrendingItemDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TrendingItemDto>>> GetTrending()
    {
        var cache = await ReadCacheAsync().ConfigureAwait(false);
        if (cache is null)
            return Ok(Array.Empty<TrendingItemDto>());

        var dtos = cache.ItemIds
            .Select(id => _libraryManager.GetItemById(id))
            .Where(item => item is not null)
            .Select(item => new TrendingItemDto
            {
                Id = item!.Id,
                Name = item.Name,
                Overview = item.Overview,
                Type = item.GetType().Name,
                TmdbId = item.ProviderIds.TryGetValue("Tmdb", out var tid) ? tid : null,
                // Jellyfin image endpoints accept quality/fill params for client-side optimisation.
                // The JS uses BackdropImageUrl as a CSS background-image, so we request a wide,
                // high-quality version. PrimaryImageUrl is used as a thumbnail fallback.
                BackdropImageUrl = $"/Items/{item.Id}/Images/Backdrop?fillWidth=1920&quality=90",
                PrimaryImageUrl  = $"/Items/{item.Id}/Images/Primary?fillWidth=400&quality=85",
                ProductionYear = item.ProductionYear,
                CommunityRating = item.CommunityRating
            });

        return Ok(dtos);
    }

    // ── Status ──────────────────────────────────────────────────────────────────

    [HttpGet("Status")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> GetStatus()
    {
        var cfg = Plugin.Instance?.Configuration;
        var cache = await ReadCacheAsync().ConfigureAwait(false);

        return Ok(new
        {
            Version = Plugin.Instance?.Version?.ToString(),
            TmdbKeyConfigured = !string.IsNullOrWhiteSpace(cfg?.TmdbApiKey),
            EnableBannerMode = cfg?.EnableBannerMode,
            MaxItems = cfg?.MaxItems,
            SyncIntervalHours = cfg?.SyncIntervalHours,
            CachedItemCount = cache?.ItemIds.Count ?? 0,
            LastUpdated = cache?.LastUpdated
        });
    }

    // ── Static web assets ───────────────────────────────────────────────────────

    [HttpGet("jellyTrend.js")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600)]
    public IActionResult GetScript()
        => ServeEmbeddedResource("Jellyfin.Plugin.JellyTrend.Web.jellyTrend.js", "application/javascript");

    [HttpGet("jellyTrend.css")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600)]
    public IActionResult GetStyles()
        => ServeEmbeddedResource("Jellyfin.Plugin.JellyTrend.Web.jellyTrend.css", "text/css");

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private IActionResult ServeEmbeddedResource(string resourceName, string contentType)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            _logger.LogWarning("JellyTrend: Embedded resource '{Name}' not found.", resourceName);
            return NotFound();
        }

        return File(stream, contentType);
    }

    private static async Task<TrendingCache?> ReadCacheAsync()
    {
        var dataPath = Path.Combine(Plugin.Instance!.PluginFolder, "trending.json");
        if (!System.IO.File.Exists(dataPath))
            return null;

        try
        {
            var json = await System.IO.File.ReadAllTextAsync(dataPath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<TrendingCache>(json);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class TrendingItemDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public string? Type { get; set; }
    public string? TmdbId { get; set; }
    public string? BackdropImageUrl { get; set; }
    public string? PrimaryImageUrl { get; set; }
    public int? ProductionYear { get; set; }
    public float? CommunityRating { get; set; }
}
