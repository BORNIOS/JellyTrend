using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
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
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<TrendingController> _logger;

    public TrendingController(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILogger<TrendingController> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
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

        var viewer = ResolveCurrentUser();

        var dtos = cache.ItemIds
            .Select(id => _libraryManager.GetItemById(id))
            .Where(item => item is not null)
            .Select(item =>
            {
                var i = item!;
                var logoUrl = $"/Items/{i.Id}/Images/Logo";
                var discUrl = $"/Items/{i.Id}/Images/Disc";

                bool? played = null;
                long? positionTicks = null;
                if (viewer is not null)
                {
                    var ud = _userDataManager.GetUserData(viewer, i);
                    if (ud is not null)
                    {
                        played = ud.Played;
                        positionTicks = ud.PlaybackPositionTicks;
                    }
                }

                var genres = i.Genres is { Length: > 0 } g ? new List<string>(g) : null;
                var actors = _libraryManager.GetPeople(i)
                    .Where(p => p.Type == PersonKind.Actor)
                    .Select(p => p.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Take(20)
                    .ToList();
                if (actors.Count == 0)
                {
                    actors = null;
                }

                return new TrendingItemDto
                {
                    Id = i.Id,
                    Name = i.Name,
                    Overview = i.Overview,
                    Type = i.GetType().Name,
                    TmdbId = i.ProviderIds.TryGetValue("Tmdb", out var tid) ? tid : null,
                    BackdropImageUrl = $"/Items/{i.Id}/Images/Backdrop?fillWidth=1920&quality=90",
                    PrimaryImageUrl = $"/Items/{i.Id}/Images/Primary?fillWidth=400&quality=85",
                    ProductionYear = i.ProductionYear,
                    CommunityRating = i.CommunityRating,
                    Genres = genres,
                    Actors = actors,
                    LogoImageUrl = logoUrl,
                    DiscImageUrl = discUrl,
                    IsPlayed = played,
                    PlaybackPositionTicks = positionTicks,
                    RunTimeTicks = i.RunTimeTicks,
                    MediaStreams = null
                };
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

    private User? ResolveCurrentUser()
    {
        var idText = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(idText) || !Guid.TryParse(idText, out var userId))
        {
            return null;
        }

        return _userManager.GetUserById(userId);
    }

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
    public List<string>? Genres { get; set; }
    public List<string>? Actors { get; set; }
    public string? LogoImageUrl { get; set; }
    public string? DiscImageUrl { get; set; }
    public bool? IsPlayed { get; set; }
    public long? PlaybackPositionTicks { get; set; }
    public long? RunTimeTicks { get; set; }
    public List<MediaStreamDto>? MediaStreams { get; set; }
}

public sealed class MediaStreamDto
{
    public string? Type { get; set; } // Audio, Video, Subtitle
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public string? DisplayTitle { get; set; }
    public int? Channels { get; set; }
    public int? BitRate { get; set; }
    public int? SampleRate { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}
