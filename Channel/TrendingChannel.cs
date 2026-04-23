using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTrend.ScheduledTask;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Channel;

/// <summary>
/// Exposes trending movies as a Jellyfin channel visible under "Channels" in ALL clients
/// (Roku, Android TV, iOS, web) and in home "recently added" sections via ISupportsLatestMedia.
///
/// Items link directly to local library files — no physical copies, no .strm files.
/// The channel refreshes automatically after each TrendingSyncTask run because
/// DataVersion is derived from the trending.json file timestamp.
/// </summary>
public sealed class TrendingChannel : IChannel, IRequiresMediaInfoCallback, ISupportsLatestMedia
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<TrendingChannel> _logger;

    public TrendingChannel(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IServerApplicationHost appHost,
        ILogger<TrendingChannel> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _appHost = appHost;
        _logger = logger;
    }

    // ── IChannel properties ───────────────────────────────────────────────────

    public string Name        => Plugin.Instance?.Configuration.ChannelName ?? "JellyTrend - Trending Now";
    public string Description => "Películas en tendencia según TMDB, actualizadas semanalmente.";
    public string HomePageUrl => string.Empty;
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    // Changing DataVersion forces Jellyfin to re-fetch channel items.
    // Derived from trending.json mtime so the channel refreshes automatically after every sync.
    public string DataVersion
    {
        get
        {
            var path = Path.Combine(Plugin.Instance!.PluginFolder, "trending.json");
            return File.Exists(path)
                ? File.GetLastWriteTimeUtc(path).Ticks.ToString()
                : "1";
        }
    }

    // ── IChannel methods ──────────────────────────────────────────────────────

    public InternalChannelFeatures GetChannelFeatures() => new InternalChannelFeatures
    {
        ContentTypes = [ChannelMediaContentType.Movie],
        MediaTypes   = [ChannelMediaType.Video]
    };

    public bool IsEnabledFor(string userId)
        => Plugin.Instance?.Configuration.EnableChannel == true;

    public Task<ChannelItemResult> GetChannelItems(
        InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var items = BuildChannelItems();
        _logger.LogDebug("JellyTrend Canal: devolviendo {Count} items.", items.Count);
        return Task.FromResult(new ChannelItemResult
        {
            Items            = items,
            TotalRecordCount = items.Count
        });
    }

    // Serve the embedded channel-poster.png as the channel's primary image.
    public Task<DynamicImageResponse> GetChannelImage(
        ImageType type, CancellationToken cancellationToken)
    {
        var stream = GetType().Assembly
            .GetManifestResourceStream("Jellyfin.Plugin.JellyTrend.Web.channel-poster.png");

        if (stream is null)
            return Task.FromResult(new DynamicImageResponse { HasImage = false });

        return Task.FromResult(new DynamicImageResponse
        {
            HasImage = true,
            Format   = ImageFormat.Png,
            Stream   = stream,
        });
    }

    public IEnumerable<ImageType> GetSupportedChannelImages()
        => [ImageType.Primary];

    // ── ISupportsLatestMedia ──────────────────────────────────────────────────
    // This is what makes items from this channel appear in Jellyfin's home
    // "Medios agregados recientemente" / "Recently Added" section rows.

    public Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(
        ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<ChannelItemInfo>>(BuildChannelItems());
    }

    // ── IRequiresMediaInfoCallback ────────────────────────────────────────────
    // Returns full MediaSourceInfo (all audio tracks, subtitles, codec metadata)
    // so Jellyfin clients can make a proper direct-play decision.

    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(
        string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var guid))
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());

        var item = _libraryManager.GetItemById(guid);
        if (item is null || string.IsNullOrEmpty(item.Path))
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());

        var sources = _mediaSourceManager.GetStaticMediaSources(item, true, null);

        if (sources.Count > 0)
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(sources);

        // Fallback: bare MediaSourceInfo if the library has no stored stream data.
        var fallback = new MediaSourceInfo
        {
            Id                   = id,
            Name                 = item.Name,
            Path                 = item.Path,
            Protocol             = MediaProtocol.File,
            IsRemote             = false,
            SupportsDirectPlay   = true,
            SupportsDirectStream = true,
            SupportsTranscoding  = true,
            RunTimeTicks         = item.RunTimeTicks,
            Container            = Path.GetExtension(item.Path)?.TrimStart('.') ?? string.Empty,
        };
        return Task.FromResult<IEnumerable<MediaSourceInfo>>([fallback]);
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private List<ChannelItemInfo> BuildChannelItems()
    {
        var cache = ReadCache();
        if (cache is null) return [];

        var result = new List<ChannelItemInfo>(cache.ItemIds.Count);

        foreach (var id in cache.ItemIds)
        {
            var item = _libraryManager.GetItemById(id);
            if (item is null) continue;

            if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
                continue;

            result.Add(new ChannelItemInfo
            {
                Id              = item.Id.ToString(),
                Name            = item.Name,
                Overview        = item.Overview,
                Type            = ChannelItemType.Media,
                MediaType       = ChannelMediaType.Video,
                ContentType     = ChannelMediaContentType.Movie,
                ImageUrl        = $"{_appHost.GetLocalApiUrl("localhost")}/Items/{item.Id}/Images/Primary",
                ProductionYear  = item.ProductionYear,
                CommunityRating = item.CommunityRating,
                RunTimeTicks    = item.RunTimeTicks,
                DateModified    = item.DateModified,
                ProviderIds     = new Dictionary<string, string>(item.ProviderIds),
            });
        }

        _logger.LogInformation("JellyTrend Canal: {Count} películas en el canal.", result.Count);
        return result;
    }

    private static TrendingCache? ReadCache()
    {
        var path = Path.Combine(Plugin.Instance!.PluginFolder, "trending.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<TrendingCache>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
