using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyTrend.Channel;

/// <summary>
/// Shared builders for channel items so the trending and recommended channels render
/// exactly alike. Series always surface the SERIES root artwork (a season is the only
/// image fallback — never an episode); movies mirror their library primary image.
/// </summary>
internal static class ChannelItemFactory
{
    private static readonly Uri TmdbBackdropBaseUrl = BuildTmdbImageBaseUri("original");
    private static readonly Uri TmdbPosterBaseUrl = BuildTmdbImageBaseUri("w780");

    /// <summary>
    /// Builds a playable movie channel item mirroring the library item's artwork.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="appHost">The server application host.</param>
    /// <param name="item">The library movie.</param>
    /// <param name="tmdbPosterPath">Optional TMDB poster path fallback.</param>
    /// <param name="tmdbBackdropPath">Optional TMDB backdrop path fallback.</param>
    /// <returns>The channel item.</returns>
    public static ChannelItemInfo BuildMovieItem(
        ILibraryManager libraryManager,
        IServerApplicationHost appHost,
        BaseItem item,
        string? tmdbPosterPath,
        string? tmdbBackdropPath)
    {
        var info = new ChannelItemInfo
        {
            Id = item.Id.ToString(),
            Name = item.Name,
            Overview = item.Overview,
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Movie,
            ImageUrl = ResolveItemImageUrl(libraryManager, appHost, item, tmdbPosterPath, tmdbBackdropPath),
            ProductionYear = item.ProductionYear,
            CommunityRating = item.CommunityRating,
            RunTimeTicks = item.RunTimeTicks,
            DateModified = item.DateModified,
            DateCreated = item.DateCreated,
            PremiereDate = item.PremiereDate,
            OriginalTitle = item.OriginalTitle,
            OfficialRating = item.OfficialRating,
            Genres = item.Genres is { Length: > 0 } g ? [.. g] : [],
            Studios = item.Studios is { Length: > 0 } s ? [.. s] : [],
            Tags = item.Tags is { Length: > 0 } t ? [.. t] : [],
            HomePageUrl = item.HomePageUrl,
            ProviderIds = new Dictionary<string, string>(item.ProviderIds)
        };

        AddPeople(libraryManager, info, item);
        return info;
    }

    /// <summary>
    /// Builds a series folder channel item using only the SERIES root metadata and artwork.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="appHost">The server application host.</param>
    /// <param name="item">The library series.</param>
    /// <param name="tmdbPosterPath">Optional TMDB poster path fallback.</param>
    /// <param name="tmdbBackdropPath">Optional TMDB backdrop path fallback.</param>
    /// <returns>The channel item, or <c>null</c> when the item is not a series.</returns>
    public static ChannelItemInfo? BuildSeriesFolderItem(
        ILibraryManager libraryManager,
        IServerApplicationHost appHost,
        BaseItem item,
        string? tmdbPosterPath,
        string? tmdbBackdropPath)
    {
        if (item is not Series)
        {
            return null;
        }

        var info = new ChannelItemInfo
        {
            Id = item.Id.ToString(),
            Name = item.Name,
            Overview = item.Overview,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Series,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Episode,
            ImageUrl = ResolveItemImageUrl(libraryManager, appHost, item, tmdbPosterPath, tmdbBackdropPath),
            ProductionYear = item.ProductionYear,
            CommunityRating = item.CommunityRating,
            RunTimeTicks = item.RunTimeTicks,
            DateModified = item.DateModified,
            PremiereDate = item.PremiereDate,
            DateCreated = item.DateCreated,
            OriginalTitle = item.OriginalTitle,
            OfficialRating = item.OfficialRating,
            Genres = item.Genres is { Length: > 0 } g ? [.. g] : [],
            Studios = item.Studios is { Length: > 0 } s ? [.. s] : [],
            Tags = item.Tags is { Length: > 0 } t ? [.. t] : [],
            HomePageUrl = item.HomePageUrl,
            ProviderIds = new Dictionary<string, string>(item.ProviderIds)
        };

        AddPeople(libraryManager, info, item);
        return info;
    }

    /// <summary>
    /// Returns the children of a series (its seasons) or a season (its episodes) for channel
    /// folder navigation, mirroring the library's series → season → episode hierarchy.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="appHost">The server application host.</param>
    /// <param name="folderId">The series or season id.</param>
    /// <returns>The child channel items.</returns>
    public static List<ChannelItemInfo> GetFolderChildren(
        ILibraryManager libraryManager,
        IServerApplicationHost appHost,
        Guid folderId)
    {
        var item = libraryManager.GetItemById(folderId);
        return item switch
        {
            Series => BuildSeriesSeasons(libraryManager, appHost, folderId),
            Season => BuildSeasonEpisodes(libraryManager, appHost, folderId),
            _ => []
        };
    }

    /// <summary>
    /// Builds the season folder items of a series (specials excluded) for channel navigation.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="appHost">The server application host.</param>
    /// <param name="seriesId">The library series id.</param>
    /// <returns>The season channel items.</returns>
    public static List<ChannelItemInfo> BuildSeriesSeasons(
        ILibraryManager libraryManager,
        IServerApplicationHost appHost,
        Guid seriesId)
    {
        var series = libraryManager.GetItemById(seriesId);
        if (series is not Series)
        {
            return [];
        }

        var seasons = libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = seriesId,
            IncludeItemTypes = [BaseItemKind.Season],
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.IndexNumber, SortOrder.Ascending)]
        });

        var result = new List<ChannelItemInfo>(seasons.Count);
        foreach (var season in seasons)
        {
            if (season.IndexNumber is <= 0)
            {
                continue;   // skip specials (season 0)
            }

            var providerIds = season.ProviderIds.Count > 0
                ? new Dictionary<string, string>(season.ProviderIds)
                : new Dictionary<string, string>(series.ProviderIds);

            result.Add(new ChannelItemInfo
            {
                Id = season.Id.ToString(),
                Name = season.Name,
                Overview = season.Overview,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Season,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Episode,
                ImageUrl = ResolveSeasonImageUrl(appHost, season, series),
                IndexNumber = season.IndexNumber,
                ProductionYear = season.ProductionYear,
                PremiereDate = season.PremiereDate,
                DateCreated = season.DateCreated,
                Genres = series.Genres is { Length: > 0 } g ? [.. g] : [],
                Studios = series.Studios is { Length: > 0 } s ? [.. s] : [],
                Tags = series.Tags is { Length: > 0 } t ? [.. t] : [],
                ProviderIds = providerIds
            });
        }

        return result;
    }

    /// <summary>
    /// Builds the playable episode items of a season for channel folder navigation.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="appHost">The server application host.</param>
    /// <param name="seasonId">The library season id.</param>
    /// <returns>The episode channel items.</returns>
    public static List<ChannelItemInfo> BuildSeasonEpisodes(
        ILibraryManager libraryManager,
        IServerApplicationHost appHost,
        Guid seasonId)
    {
        var seasonItem = libraryManager.GetItemById(seasonId);
        if (seasonItem is not Season season)
        {
            return [];
        }

        var series = season.Series;

        var episodes = libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = seasonId,
            IncludeItemTypes = [BaseItemKind.Episode],
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.IndexNumber, SortOrder.Ascending)]
        });

        var result = new List<ChannelItemInfo>(episodes.Count);
        foreach (var episode in episodes)
        {
            if (string.IsNullOrEmpty(episode.Path) || !File.Exists(episode.Path))
            {
                continue;
            }

            Dictionary<string, string> providerIds;
            if (episode.ProviderIds.Count > 0)
            {
                providerIds = new Dictionary<string, string>(episode.ProviderIds);
            }
            else if (series is not null)
            {
                providerIds = new Dictionary<string, string>(series.ProviderIds);
            }
            else
            {
                providerIds = [];
            }

            result.Add(new ChannelItemInfo
            {
                Id = episode.Id.ToString(),
                Name = episode.Name,
                Overview = episode.Overview,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Episode,
                ImageUrl = series is not null
                    ? ResolveEpisodeImageUrl(appHost, episode, series)
                    : ResolveEpisodeImageUrl(appHost, episode, season),
                ProductionYear = episode.ProductionYear,
                CommunityRating = episode.CommunityRating,
                RunTimeTicks = episode.RunTimeTicks,
                PremiereDate = episode.PremiereDate,
                DateCreated = episode.DateCreated,
                IndexNumber = episode.IndexNumber,
                ParentIndexNumber = episode.ParentIndexNumber,
                Genres = series is not null && series.Genres is { Length: > 0 } g ? [.. g] : [],
                Studios = series is not null && series.Studios is { Length: > 0 } s ? [.. s] : [],
                Tags = series is not null && series.Tags is { Length: > 0 } t ? [.. t] : [],
                OfficialRating = episode.OfficialRating ?? series?.OfficialRating,
                ProviderIds = providerIds
            });
        }

        return result;
    }

    /// <summary>
    /// Resolves the season folder artwork: the season's own art first, then the series root art.
    /// </summary>
    private static string ResolveSeasonImageUrl(IServerApplicationHost appHost, BaseItem season, BaseItem series)
    {
        if (season.HasImage(ImageType.Primary, 0))
        {
            return BuildItemImageUrl(appHost, season.Id, ImageType.Primary);
        }

        if (season.HasImage(ImageType.Backdrop, 0))
        {
            return BuildItemImageUrl(appHost, season.Id, ImageType.Backdrop);
        }

        if (series.HasImage(ImageType.Primary, 0))
        {
            return BuildItemImageUrl(appHost, series.Id, ImageType.Primary);
        }

        if (series.HasImage(ImageType.Backdrop, 0))
        {
            return BuildItemImageUrl(appHost, series.Id, ImageType.Backdrop);
        }

        return string.Empty;
    }

    /// <summary>
    /// Resolves the playable media sources for a library item (used by IRequiresMediaInfoCallback).
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="id">The channel item id (a library item id).</param>
    /// <returns>The media sources, or an empty sequence when the item has no playable file.</returns>
    public static IEnumerable<MediaSourceInfo> GetMediaInfo(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        string id)
    {
        if (!Guid.TryParse(id, out var guid))
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        var item = libraryManager.GetItemById(guid);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        var sources = mediaSourceManager.GetStaticMediaSources(item, true, null);
        if (sources.Count > 0)
        {
            return sources;
        }

        return
        [
            new MediaSourceInfo
            {
                Id = id,
                Name = item.Name,
                Path = item.Path,
                Protocol = MediaProtocol.File,
                IsRemote = false,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                RunTimeTicks = item.RunTimeTicks,
                Container = Path.GetExtension(item.Path)?.TrimStart('.') ?? string.Empty,
            }
        ];
    }

    /// <summary>
    /// Adds the item's cast and creators (actors first) to the channel item so the detail
    /// view shows real people instead of an empty, shadow-like cast.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="info">The channel item to enrich.</param>
    /// <param name="item">The source library item.</param>
    private static void AddPeople(ILibraryManager libraryManager, ChannelItemInfo info, BaseItem item)
    {
        var people = libraryManager.GetPeople(item)
            .Where(p => p.Type is PersonKind.Actor or PersonKind.Director or PersonKind.Writer)
            .OrderByDescending(p => p.Type == PersonKind.Actor)
            .ThenBy(p => p.SortOrder ?? int.MaxValue)
            .Take(24)
            .ToList();

        if (people.Count > 0)
        {
            info.People = people;
        }
    }

    private static string ResolveItemImageUrl(
        ILibraryManager libraryManager,
        IServerApplicationHost appHost,
        BaseItem item,
        string? tmdbPosterPath,
        string? tmdbBackdropPath)
    {
        // Series: root-level artwork only — the series' own images first, with a
        // season as fallback when the series has no local art. Never an episode.
        if (item is Series)
        {
            if (item.HasImage(ImageType.Primary, 0))
            {
                return BuildItemImageUrl(appHost, item.Id, ImageType.Primary);
            }

            if (item.HasImage(ImageType.Backdrop, 0))
            {
                return BuildItemImageUrl(appHost, item.Id, ImageType.Backdrop);
            }

            var season = FindSeasonWithArtwork(libraryManager, item.Id);
            if (season is not null)
            {
                return season.HasImage(ImageType.Primary, 0)
                    ? BuildItemImageUrl(appHost, season.Id, ImageType.Primary)
                    : BuildItemImageUrl(appHost, season.Id, ImageType.Backdrop);
            }

            return BuildTmdbImageUrl(tmdbPosterPath, TmdbPosterBaseUrl)
                ?? BuildTmdbImageUrl(tmdbBackdropPath, TmdbBackdropBaseUrl)
                ?? string.Empty;
        }

        // Movies: mirror the library item's primary (folder.jpg), then backdrop.
        if (item.HasImage(ImageType.Primary, 0))
        {
            return BuildItemImageUrl(appHost, item.Id, ImageType.Primary);
        }

        if (item.HasImage(ImageType.Backdrop, 0))
        {
            return BuildItemImageUrl(appHost, item.Id, ImageType.Backdrop);
        }

        return BuildTmdbImageUrl(tmdbPosterPath, TmdbPosterBaseUrl)
            ?? BuildTmdbImageUrl(tmdbBackdropPath, TmdbBackdropBaseUrl)
            ?? string.Empty;
    }

    private static BaseItem? FindSeasonWithArtwork(ILibraryManager libraryManager, Guid seriesId)
    {
        var seasons = libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = seriesId,
            IncludeItemTypes = [BaseItemKind.Season],
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)]
        });

        return seasons.FirstOrDefault(static s => s.HasImage(ImageType.Primary, 0) || s.HasImage(ImageType.Backdrop, 0));
    }

    private static string ResolveEpisodeImageUrl(IServerApplicationHost appHost, BaseItem episode, BaseItem series)
    {
        // Inside the series folder, episode art is fine; fall back to the series
        // root artwork so cards never render blank.
        if (episode.HasImage(ImageType.Primary, 0))
        {
            return BuildItemImageUrl(appHost, episode.Id, ImageType.Primary);
        }

        if (series.HasImage(ImageType.Primary, 0))
        {
            return BuildItemImageUrl(appHost, series.Id, ImageType.Primary);
        }

        if (series.HasImage(ImageType.Backdrop, 0))
        {
            return BuildItemImageUrl(appHost, series.Id, ImageType.Backdrop);
        }

        return string.Empty;
    }

    private static string BuildItemImageUrl(IServerApplicationHost appHost, Guid itemId, ImageType type)
        => $"{appHost.GetLocalApiUrl("localhost")}/Items/{itemId}/Images/{type}";

    private static string? BuildTmdbImageUrl(string? imagePath, Uri baseUrl)
        => string.IsNullOrWhiteSpace(imagePath) ? null : new Uri(baseUrl, imagePath).ToString();

    private static Uri BuildTmdbImageBaseUri(string size)
        => new(string.Concat("https", "://", "image.tmdb.org", "/t/p/", size));
}
