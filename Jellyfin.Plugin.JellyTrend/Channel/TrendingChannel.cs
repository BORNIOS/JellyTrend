using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend.Configuration;
using Jellyfin.Plugin.JellyTrend.ScheduledTask;
using Jellyfin.Plugin.JellyTrend.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
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
/// Exposes trending movies and series as a Jellyfin channel visible under "Channels" in ALL
/// clients (Roku, Android TV, iOS, web) and in home "recently added" sections via
/// ISupportsLatestMedia.
///
/// Movies link directly to local library files; series are browsable folders whose episodes
/// play from the local library. Series always surface the SERIES root metadata and artwork
/// (a season is used only as a fallback, never an episode). The channel refreshes
/// automatically after each TrendingSyncTask run because DataVersion is derived from the
/// trending.json file timestamp.
/// </summary>
public sealed class TrendingChannel : IChannel, IRequiresMediaInfoCallback, ISupportsLatestMedia
{
    // Bump cuando cambia la lógica de generación de items del canal (p. ej. resolución de librería)
    // para forzar a Jellyfin a re-fetch y re-materializar las sombras con los ExternalId correctos.
    private const string DataVersionSchema = "3";

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IServerApplicationHost _appHost;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<TrendingChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrendingChannel"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TrendingChannel}"/> interface.</param>
    public TrendingChannel(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IServerApplicationHost appHost,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILogger<TrendingChannel> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _appHost = appHost;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _logger = logger;
    }

    // ── IChannel properties ───────────────────────────────────────────────────

    /// <inheritdoc />
    public string Name => Plugin.Instance?.Configuration.ChannelName ?? PluginConfiguration.DefaultChannelName;

    /// <inheritdoc />
    public string Description => "Películas y series en tendencia según TMDB, actualizadas semanalmente.";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <summary>
    /// Gets a version string that changes whenever the trending data changes OR the plugin
    /// configuration is saved, forcing Jellyfin to re-fetch channel items. Derived from
    /// trending.json mtime (data changes) plus the plugin config file mtime (every save,
    /// including the series toggle). Including the config mtime guarantees a NEW cache
    /// version on every toggle so Jellyfin always re-fetches and purges the old series
    /// shadow items — a plain -s0/-s1 toggle could reuse an older on-disk cache and keep
    /// serving stale shadows on the home row.
    /// </summary>
    public string DataVersion
    {
        get
        {
            var path = Path.Combine(Plugin.Instance!.PluginFolder, "trending.json");
            var dataTicks = File.Exists(path)
                ? File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture)
                : "1";

            var configPath = Plugin.Instance?.ConfigurationFilePath;
            var configTicks = !string.IsNullOrEmpty(configPath) && File.Exists(configPath)
                ? File.GetLastWriteTimeUtc(configPath).Ticks.ToString(CultureInfo.InvariantCulture)
                : "0";

            var showSeries = Plugin.Instance?.Configuration.EnableTrendingSeries == true;
            var pluginVersion = typeof(TrendingChannel).Assembly.GetName().Version?.ToString() ?? "0";
            return $"{pluginVersion}-{DataVersionSchema}-{dataTicks}-{configTicks}-s{(showSeries ? "1" : "0")}";
        }
    }

    // ── IChannel methods ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures() => new InternalChannelFeatures
    {
        ContentTypes = [ChannelMediaContentType.Movie, ChannelMediaContentType.Episode],
        MediaTypes = [ChannelMediaType.Video]
    };

    /// <inheritdoc />
    public bool IsEnabledFor(string userId)
        => Plugin.Instance?.Configuration.EnableChannel == true;

    /// <inheritdoc />
    public Task<ChannelItemResult> GetChannelItems(
        InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        // Folder navigation mirrors the library hierarchy: a series folder returns
        // its seasons, and a season folder returns its episodes (playable locally).
        if (Guid.TryParse(query.FolderId, out var folderGuid) && folderGuid != Guid.Empty)
        {
            var children = ChannelItemFactory.GetFolderChildren(_libraryManager, _appHost, folderGuid);
            return Task.FromResult(new ChannelItemResult
            {
                Items = children,
                TotalRecordCount = children.Count
            });
        }

        var viewer = TryGetUser(query.UserId.ToString());
        var items = BuildChannelItems(viewer);
        _logger.LogDebug("JellyTrend Canal: devolviendo {Count} items.", items.Count);
        return Task.FromResult(new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        });
    }

    /// <summary>
    /// Serves the embedded channel-trendings.png as the channel's primary image.
    /// </summary>
    /// <param name="type">The requested image type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channel image response.</returns>
    public Task<DynamicImageResponse> GetChannelImage(
        ImageType type, CancellationToken cancellationToken)
    {
        var stream = GetType().Assembly
            .GetManifestResourceStream("Jellyfin.Plugin.JellyTrend.Web.channel-trendings.png");

        if (stream is null)
        {
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        return Task.FromResult(new DynamicImageResponse
        {
            HasImage = true,
            Format = ImageFormat.Png,
            Stream = stream,
        });
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages()
        => [ImageType.Primary];

    // ── ISupportsLatestMedia ──────────────────────────────────────────────────
    // This is what makes items from this channel appear in Jellyfin's home
    // "Medios agregados recientemente" / "Recently Added" section rows.

    /// <inheritdoc />
    public Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(
        ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        var viewer = TryGetUser(request.UserId);
        return Task.FromResult<IEnumerable<ChannelItemInfo>>(BuildChannelItems(viewer));
    }

    // ── IRequiresMediaInfoCallback ────────────────────────────────────────────
    // Returns full MediaSourceInfo (all audio tracks, subtitles, codec metadata)
    // so Jellyfin clients can make a proper direct-play decision.

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(
        string id, CancellationToken cancellationToken)
    {
        return Task.FromResult(ChannelItemFactory.GetMediaInfo(_libraryManager, _mediaSourceManager, id));
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private User? TryGetUser(string? userId)
    {
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out var guid) || guid == Guid.Empty)
        {
            return null;
        }

        try
        {
            return _userManager.GetUserById(guid);
        }
        catch
        {
            return null;
        }
    }

    private List<ChannelItemInfo> BuildChannelItems(User? viewer = null)
    {
        var cache = ReadCache();
        if (cache is null)
        {
            return [];
        }

        cache.Normalize();

        var showSeries = Plugin.Instance?.Configuration.EnableTrendingSeries ?? true;

        var result = new List<ChannelItemInfo>(cache.Items.Count);

        foreach (var cacheItem in cache.Items)
        {
            // Resolución canónica: el GUID de librería es solo fast-path; si quedó stale (librería
            // re-importada/migrada) se re-matchea por TMDB al item actual, así las series navegan
            // a sus temporadas y las películas siguen siendo reproducibles.
            var item = TrendingItemResolver.ResolveCurrentItem(
                _libraryManager, cacheItem.ItemId, cacheItem.TmdbId, cacheItem.MediaType == TrendingMediaType.Series);
            if (item is null)
            {
                continue;
            }

            if (cacheItem.MediaType == TrendingMediaType.Series)
            {
                // Cuando el admin desactiva las series, el canal muestra solo películas.
                if (!showSeries)
                {
                    continue;
                }

                // Series appear in the row as folders, always using the SERIES root
                // metadata and artwork (a season is the only image fallback — never
                // an episode).
                var seriesItem = BuildSeriesChannelItem(item, cacheItem);
                if (seriesItem is not null)
                {
                    result.Add(seriesItem);
                }

                continue;
            }

            // Movies must point to a real, playable file.
            if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
            {
                continue;
            }

            // Hide already-watched movies. In-progress titles stay visible so the
            // user can resume them. Series are never filtered (episode-level tracking
            // is too granular for a series-root item).
            if (viewer is not null && IsMovieWatched(viewer, item))
            {
                continue;
            }

            result.Add(BuildMovieChannelItem(item, cacheItem));
        }

        _logger.LogDebug("JellyTrend Canal: {Count} títulos en el canal.", result.Count);
        return result;
    }

    private bool IsMovieWatched(User viewer, BaseItem item)
    {
        var ud = _userDataManager.GetUserData(viewer, item);
        return ud is not null && ud.Played;
    }

    private ChannelItemInfo? BuildSeriesChannelItem(BaseItem item, TrendingCacheEntry cacheItem)
        => ChannelItemFactory.BuildSeriesFolderItem(_libraryManager, _appHost, item, cacheItem.TmdbPosterPath, cacheItem.TmdbBackdropPath);

    private ChannelItemInfo BuildMovieChannelItem(BaseItem item, TrendingCacheEntry cacheItem)
        => ChannelItemFactory.BuildMovieItem(_libraryManager, _appHost, item, cacheItem.TmdbPosterPath, cacheItem.TmdbBackdropPath);

    private static TrendingCache? ReadCache()
    {
        var path = Path.Combine(Plugin.Instance!.PluginFolder, "trending.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var cache = JsonSerializer.Deserialize<TrendingCache>(File.ReadAllText(path));
            cache?.Normalize();
            return cache;
        }
        catch
        {
            return null;
        }
    }
}
