using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Channel;

/// <summary>
/// Exposes a per-user personalized "Recomendados" row as a Jellyfin channel, visible under
/// "Channels" in ALL clients and on the home screen via ISupportsLatestMedia. Recommendations
/// are generated weekly by RecommendationSyncTask and stored per user; each user only ever
/// sees their own unwatched, in-progress-free recommendations (movies only).
/// </summary>
public sealed class RecommendedChannel : IChannel, ISupportsLatestMedia, IRequiresMediaInfoCallback, IHasCacheKey
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IServerApplicationHost _appHost;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecommendedChannel"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="httpContextAccessor">Instance of the <see cref="IHttpContextAccessor"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public RecommendedChannel(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IServerApplicationHost appHost,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IHttpContextAccessor httpContextAccessor,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _appHost = appHost;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _httpContextAccessor = httpContextAccessor;
        _logger = loggerFactory.CreateLogger<RecommendedChannel>();
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
    /// Jellyfin to re-fetch the channel items after each weekly sync. El prefijo se incrementa
    /// cuando cambian las reglas de generación de los items (filtro por usuario, imágenes
    /// locales, sync de sombras): al desplegar una versión nueva, Jellyfin descarta las caches
    /// de items creadas por versiones anteriores.
    /// </summary>
    public string DataVersion
    {
        get
        {
            var lastModified = RecommendationStorage.GetLastModifiedUtc();
            var ticks = lastModified == DateTime.MinValue ? 0 : lastModified.Ticks;
            return "JT3-" + ticks.ToString(CultureInfo.InvariantCulture);
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
    /// Returns a per-user cache key so Jellyfin never mixes the recommendations of
    /// different users in the channel's on-disk cache (the home-row refresh arrives
    /// without a user id; the real viewer is resolved from the authenticated request).
    /// </summary>
    /// <param name="userId">The user id passed by Jellyfin (null/empty on the home-row refresh).</param>
    /// <returns>A cache key scoped to the real viewer.</returns>
    public string? GetCacheKey(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            userId = ResolveUserId(Guid.Empty).ToString("N", CultureInfo.InvariantCulture);
        }

        return "u" + userId;
    }

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
        // Both paths go through BuildChannelItems: an unparseable/empty user id falls back
        // to any stored recommendations (same as the Trending channel), so the row never
        // reports empty when data exists.
        var items = Guid.TryParse(request.UserId, out var userId)
            ? BuildChannelItems(userId)
            : BuildChannelItems(Guid.Empty);
        return Task.FromResult<IEnumerable<ChannelItemInfo>>(items);
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(
        string id, CancellationToken cancellationToken)
    {
        return Task.FromResult(ChannelItemFactory.GetMediaInfo(_libraryManager, _mediaSourceManager, id));
    }

    /// <summary>
    /// Resolves the authenticated user from the current HTTP request. Every Jellyfin client
    /// sends its access token, so the authenticated user is the real viewer — unlike the
    /// <c>query.UserId</c> that Jellyfin passes as <see cref="Guid.Empty"/> on the home-row
    /// refresh. The channel therefore decides by itself what to send to the user.
    /// </summary>
    /// <param name="fallback">The user id passed by Jellyfin, used when no authenticated user is available.</param>
    /// <returns>The authenticated user id when available, otherwise <paramref name="fallback"/>.</returns>
    private Guid ResolveUserId(Guid fallback)
    {
        var http = _httpContextAccessor.HttpContext;
        var idText = http?.User?.FindFirstValue("Jellyfin-UserId")
            ?? http?.User?.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!string.IsNullOrEmpty(idText) && Guid.TryParse(idText, out var userId) && userId != Guid.Empty)
        {
            return userId;
        }

        return fallback;
    }

    private List<ChannelItemInfo> BuildChannelItems(Guid userId)
    {
        // Jellyfin calls the channel with an empty user id (Guid.Empty) on the home-row refresh.
        // Resolve the authenticated user from the HTTP request instead, falling back to the stored
        // recommendations only when there is no authenticated context.
        var resolved = ResolveUserId(userId);
        var viewer = resolved == Guid.Empty ? null : SafeGetUser(resolved);
        var data = RecommendationStorage.Read(resolved) ?? RecommendationStorage.ReadAny();
        if (data is null || data.ItemIds.Count == 0)
        {
            _logger.LogDebug("Sin recomendaciones para el usuario {UserId}.", resolved);
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

            // Solo contenido no reproducido previamente: se descarta lo ya visto y lo que
            // está en progreso. Esto protege también contra el fallback ReadAny() (que en
            // ausencia de contexto autenticado puede leer las recomendaciones de otro usuario)
            // y contra lo que el usuario haya visto después del último sync semanal.
            if (viewer is not null && IsAlreadyWatched(viewer, item))
            {
                continue;
            }

            // Se construye el ChannelItemInfo IGUAL que TrendingChannel (ExternalId = guid de
            // librería plano, sin MediaSources embebidos): así Jellyfin materializa un item
            // sombra VIRTUAL del canal (LocationType Remote, sin Path local), la reproducción
            // se resuelve por el callback GetChannelItemMediaInfo y las imágenes/metadatos
            // locales las copia TrendingShadowMetadataSync (mismo tratamiento que trending).
            result.Add(ChannelItemFactory.BuildMovieItem(_libraryManager, _appHost, item, null, null));
        }

        _logger.LogDebug("Leídos {Total} ids, devueltos {Count} para el usuario {UserId}.", data.ItemIds.Count, result.Count, resolved);
        return result;
    }

    private User? SafeGetUser(Guid userId)
    {
        try
        {
            return _userManager.GetUserById(userId);
        }
        catch
        {
            return null;
        }
    }

    private bool IsAlreadyWatched(User viewer, BaseItem item)
    {
        var userData = _userDataManager.GetUserData(viewer, item);
        return userData is not null && (userData.Played || userData.PlaybackPositionTicks > 0);
    }
}
