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
using Jellyfin.Plugin.JellyTrend.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
///   GET  /JellyTrend/jellyTrend.css — serves the embedded carousel stylesheet.
/// </summary>
[ApiController]
[Route("JellyTrend")]
public sealed class TrendingController : ControllerBase
{
    private static readonly Uri TmdbBackdropBaseUrl = BuildTmdbImageBaseUri("original");
    private static readonly Uri TmdbPosterBaseUrl = BuildTmdbImageBaseUri("w780");

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<TrendingController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrendingController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TrendingController}"/> interface.</param>
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
    /// <returns>The list of matched trending items.</returns>
    [HttpGet("Trending")]
    [Authorize]
    [ProducesResponseType(typeof(IEnumerable<TrendingItemDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TrendingItemDto>>> GetTrending()
    {
        var cache = await ReadCacheAsync().ConfigureAwait(false);
        if (cache is null)
        {
            return Ok(Array.Empty<TrendingItemDto>());
        }

        var viewer = ResolveCurrentUser();
        var showSeries = Plugin.Instance?.Configuration.EnableTrendingSeries ?? true;

        cache.Normalize();

        var dtos = cache.Items
            .Where(entry => showSeries || entry.MediaType != TrendingMediaType.Series)
            .Select(cacheEntry => new
            {
                Entry = cacheEntry,
                Item = TrendingItemResolver.ResolveCurrentItem(
                    _libraryManager, cacheEntry.ItemId, cacheEntry.TmdbId, cacheEntry.MediaType == TrendingMediaType.Series)
            })
            .Where(x => x.Item is not null)
            .Select(x => BuildTrendingDto(x.Entry, x.Item!, viewer));

        // Hide titles the current user has already watched (standard Jellyfin practice).
        // In-progress items stay visible so the carousel can offer to resume them.
        var visible = dtos.Where(dto => dto.IsPlayed != true);

        return Ok(visible);
    }

    /// <summary>
    /// Maps a matched trending cache entry and its library item to the carousel DTO.
    /// </summary>
    /// <param name="entry">The trending cache entry.</param>
    /// <param name="item">The matched library item (not null).</param>
    /// <param name="viewer">The authenticated user, or <c>null</c> when unavailable.</param>
    /// <returns>The trending DTO.</returns>
    private TrendingItemDto BuildTrendingDto(TrendingCacheEntry entry, BaseItem item, User? viewer)
    {
        var logoUrl = item.HasImage(ImageType.Logo, 0) ? $"/Items/{item.Id}/Images/Logo/0" : null;
        var discUrl = item.HasImage(ImageType.Disc, 0) ? $"/Items/{item.Id}/Images/Disc/0" : null;
        var backdropUrl = ResolveImageUrl(item, ImageType.Backdrop, entry.TmdbBackdropPath, TmdbBackdropBaseUrl, 1920, 90);
        var primaryUrl = ResolveImageUrl(item, ImageType.Primary, entry.TmdbPosterPath, TmdbPosterBaseUrl, 400, 85);

        bool? played = null;
        long? positionTicks = null;
        if (viewer is not null)
        {
            var ud = _userDataManager.GetUserData(viewer, item);
            if (ud is not null)
            {
                played = ud.Played;
                positionTicks = ud.PlaybackPositionTicks;
            }
        }

        var genres = item.Genres is { Length: > 0 } g ? new List<string>(g) : null;
        var actors = _libraryManager.GetPeople(item)
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
            Id = item.Id,
            Name = item.Name,
            Overview = item.Overview,
            Type = item.GetType().Name,
            TmdbId = entry.TmdbId ?? (item.ProviderIds.TryGetValue("Tmdb", out var tid) ? tid : null),
            BackdropImageUrl = backdropUrl,
            PrimaryImageUrl = primaryUrl,
            ProductionYear = item.ProductionYear,
            CommunityRating = item.CommunityRating,
            Genres = genres,
            Actors = actors,
            LogoImageUrl = logoUrl,
            DiscImageUrl = discUrl,
            IsPlayed = played,
            PlaybackPositionTicks = positionTicks,
            RunTimeTicks = item.RunTimeTicks,
            MediaStreams = null
        };
    }

    // ── Status ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a summary of the plugin configuration and cache state.
    /// </summary>
    /// <returns>A summary of the plugin configuration and cache state.</returns>
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
            EnableTrendingSeries = cfg?.EnableTrendingSeries,
            MaxItems = cfg?.MaxItems,
            CachedItemCount = cache?.Items.Count ?? 0,
            LastUpdated = cache?.LastUpdated
        });
    }

    // ── Static web assets ───────────────────────────────────────────────────────

    /// <summary>
    /// Serves the embedded carousel script.
    /// </summary>
    /// <returns>The carousel script.</returns>
    [HttpGet("jellyTrend.js")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600)]
    public IActionResult GetScript()
        => ServeEmbeddedResource("Jellyfin.Plugin.JellyTrend.Web.jellyTrend.js", "application/javascript");

    /// <summary>
    /// Serves the embedded carousel stylesheet.
    /// </summary>
    /// <returns>The carousel stylesheet.</returns>
    [HttpGet("jellyTrend.css")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600)]
    public IActionResult GetStyles()
        => ServeEmbeddedResource("Jellyfin.Plugin.JellyTrend.Web.jellyTrend.css", "text/css");

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private User? ResolveCurrentUser()
    {
        // Jellyfin 10.11 authenticates with InternalClaimTypes.UserId
        // ("Jellyfin-UserId", value = user id in "N" format), NOT
        // ClaimTypes.NameIdentifier. Reading the wrong claim made this
        // return null, so the carousel never filtered watched titles.
        var idText = User.FindFirstValue("Jellyfin-UserId")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

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
        {
            return null;
        }

        try
        {
            var json = await System.IO.File.ReadAllTextAsync(dataPath).ConfigureAwait(false);
            var cache = JsonSerializer.Deserialize<TrendingCache>(json);
            cache?.Normalize();
            return cache;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveImageUrl(
        BaseItem item,
        ImageType imageType,
        string? tmdbPath,
        Uri tmdbBaseUrl,
        int fillWidth,
        int quality)
    {
        if (item.HasImage(imageType, 0))
        {
            return $"/Items/{item.Id}/Images/{imageType}/0?fillWidth={fillWidth}&quality={quality}";
        }

        return string.IsNullOrWhiteSpace(tmdbPath)
            ? null
            : new Uri(tmdbBaseUrl, tmdbPath).ToString();
    }

    private static Uri BuildTmdbImageBaseUri(string size)
        => new(string.Concat("https", "://", "image.tmdb.org", "/t/p/", size));
}
