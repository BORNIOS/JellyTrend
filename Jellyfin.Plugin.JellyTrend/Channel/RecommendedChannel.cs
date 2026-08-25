using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTrend.Api;
using Jellyfin.Plugin.JellyTrend.Configuration;
using Jellyfin.Plugin.JellyTrend.ScheduledTask;
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
/// Exposes a per-user personalized "Recomendados" row as a Jellyfin channel, visible under
/// "Channels" in ALL clients and on the home screen via ISupportsLatestMedia. Recommendations
/// are generated weekly by RecommendationSyncTask and stored per user; each user only ever
/// sees their own unwatched, in-progress-free recommendations (movies only).
/// </summary>
public sealed class RecommendedChannel : IChannel, ISupportsLatestMedia, IRequiresMediaInfoCallback
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<RecommendedChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecommendedChannel"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{RecommendedChannel}"/> interface.</param>
    public RecommendedChannel(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IServerApplicationHost appHost,
        ILogger<RecommendedChannel> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _appHost = appHost;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => Plugin.Instance?.Configuration.RecommendationChannelName ?? PluginConfiguration.DefaultRecommendationChannelName;

    /// <inheritdoc />
    public string Description => "Recomendaciones personalizadas según tu historial de visualización.";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <summary>
    /// Gets a version string that changes whenever any user's recommendations change, forcing
    /// Jellyfin to re-fetch the channel items after each weekly sync.
    /// </summary>
    public string DataVersion
    {
        get
        {
            var lastModified = RecommendationStorage.GetLastModifiedUtc();
            return lastModified == DateTime.MinValue
                ? "1"
                : lastModified.Ticks.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        ContentTypes = [ChannelMediaContentType.Movie, ChannelMediaContentType.Episode],
        MediaTypes = [ChannelMediaType.Video]
    };

    /// <inheritdoc />
    public bool IsEnabledFor(string userId)
        => Plugin.Instance?.Configuration.EnableRecommendationRow == true;

    /// <summary>
    /// Serves the embedded channel-recommendations.png as the channel's primary image.
    /// </summary>
    /// <param name="type">The requested image type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channel image response.</returns>
    public Task<DynamicImageResponse> GetChannelImage(
        ImageType type, CancellationToken cancellationToken)
    {
        var stream = GetType().Assembly
            .GetManifestResourceStream("Jellyfin.Plugin.JellyTrend.Web.channel-recommendations.png");

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

    /// <inheritdoc />
    public Task<ChannelItemResult> GetChannelItems(
        InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        // Folder navigation mirrors the library hierarchy: a series folder returns
        // its seasons, and a season folder returns its episodes.
        if (Guid.TryParse(query.FolderId, out var folderGuid) && folderGuid != Guid.Empty)
        {
            var children = ChannelItemFactory.GetFolderChildren(_libraryManager, _appHost, folderGuid);
            return Task.FromResult(new ChannelItemResult
            {
                Items = children,
                TotalRecordCount = children.Count
            });
        }

        var items = BuildChannelItems(query.UserId);
        _logger.LogDebug("JellyTrend Recomendados: devolviendo {Count} items.", items.Count);
        return Task.FromResult(new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        });
    }

    /// <inheritdoc />
    public Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(
        ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        var items = Guid.TryParse(request.UserId, out var userId)
            ? BuildChannelItems(userId)
            : [];
        return Task.FromResult<IEnumerable<ChannelItemInfo>>(items);
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(
        string id, CancellationToken cancellationToken)
    {
        return Task.FromResult(ChannelItemFactory.GetMediaInfo(_libraryManager, _mediaSourceManager, id));
    }

    private List<ChannelItemInfo> BuildChannelItems(Guid userId)
    {
        var data = RecommendationStorage.Read(userId);
        if (data is null || data.ItemIds.Count == 0)
        {
            return [];
        }

        var result = new List<ChannelItemInfo>(data.ItemIds.Count);
        foreach (var id in data.ItemIds)
        {
            var item = _libraryManager.GetItemById(id);
            if (item is null || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
            {
                continue;
            }

            result.Add(ChannelItemFactory.BuildMovieItem(_libraryManager, _appHost, item, null, null));
        }

        return result;
    }
}
