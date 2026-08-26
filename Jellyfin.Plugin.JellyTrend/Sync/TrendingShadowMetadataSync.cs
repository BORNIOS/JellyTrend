using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTrend;
using Jellyfin.Plugin.JellyTrend.ScheduledTask;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Sync;

/// <summary>
/// Jellyfin solo aplica <c>UpdatePeopleAsync</c> al crear ítems sombra del canal; los ya existentes
/// no reciben reparto ni metadatos desde <see cref="MediaBrowser.Controller.Channels.ChannelItemInfo"/>.
/// Copiamos metadatos clave, el reparto y las imágenes LOCALES desde la película de biblioteca hacia el
/// sombra, en cualquiera de los canales de JellyTrend (tendencias y recomendaciones).
/// </summary>
public static class TrendingShadowMetadataSync
{
    /// <summary>
    /// Copies key metadata, cast and local images from each trending cache entry to its channel
    /// shadow item, for every JellyTrend channel (trending and recommendations) where the shadow
    /// exists. Entries whose stored id is stale are re-matched by TMDB first. Also removes any
    /// episode shadow items so the trending row never shows episodes.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="cacheEntries">The trending cache entries (movies and series) to sync.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task SyncAllAsync(
        ILibraryManager libraryManager,
        IReadOnlyList<TrendingCacheEntry> cacheEntries,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var channelFolderIds = ChannelIdentity.GetAllChannelFolderIds(libraryManager).ToList();

        // Trendings NUNCA muestra episodios en la fila del home: Jellyfin materializa shadows de
        // episodio al navegar a una temporada del canal, y la row del home los aplana (los trata
        // como items latest). Se limpian en cada sync (arranque + sincronizaciones) para que la
        // row quede con películas y series, nunca episodios.
        await CleanupEpisodeShadowsAsync(libraryManager, logger, cancellationToken).ConfigureAwait(false);

        foreach (var entry in cacheEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.ItemId == Guid.Empty)
            {
                continue;
            }

            try
            {
                // Resolución canónica: el GUID de librería es fast-path; si quedó stale (librería
                // re-importada/migrada) se re-matchea por TMDB al item actual de librería para copiar
                // metadatos/reparto/imágenes correctos a la sombra.
                var library = TrendingItemResolver.ResolveCurrentItem(
                    libraryManager, entry.ItemId, entry.TmdbId, entry.MediaType == TrendingMediaType.Series);
                if (library is null)
                {
                    continue;
                }

                await SyncLibraryItemAsync(libraryManager, channelFolderIds, library, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "JellyTrend: no se pudo sincronizar metadatos del sombra para {ItemId}.", entry.ItemId);
            }
        }
    }

    /// <summary>
    /// Synchronizes shadows for raw library item ids (recommendations storage), resolving each id
    /// directly without TMDB re-matching.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="libraryItemIds">The library item ids (movies and series) to sync.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task SyncAllAsync(
        ILibraryManager libraryManager,
        IReadOnlyList<Guid> libraryItemIds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var channelFolderIds = ChannelIdentity.GetAllChannelFolderIds(libraryManager).ToList();

        await CleanupEpisodeShadowsAsync(libraryManager, logger, cancellationToken).ConfigureAwait(false);

        foreach (var id in libraryItemIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (id == Guid.Empty)
            {
                continue;
            }

            try
            {
                var library = libraryManager.GetItemById(id);
                if (library is null)
                {
                    continue;
                }

                await SyncLibraryItemAsync(libraryManager, channelFolderIds, library, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "JellyTrend: no se pudo sincronizar metadatos del sombra para {ItemId}.", id);
            }
        }
    }

    private static async Task SyncLibraryItemAsync(
        ILibraryManager libraryManager,
        IReadOnlyList<Guid> channelFolderIds,
        BaseItem library,
        CancellationToken cancellationToken)
    {
        foreach (var channelFolderId in channelFolderIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shadow = FindChannelShadow(libraryManager, channelFolderId, library, library.Id);
            if (shadow is null)
            {
                continue;
            }

            await SyncOneAsync(libraryManager, library, shadow, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes the Episode shadow items persisted inside the JellyTrend channels. Episodes are
    /// materialized by Jellyfin when a client browses into a series season of the channel; the home
    /// "latest" row flattens all non-folder channel shadows, so those episodes would pollute the
    /// trending row. Trending must NEVER show episodes, so they are removed on every sync.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task CleanupEpisodeShadowsAsync(
        ILibraryManager libraryManager,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var channelFolderIds = ChannelIdentity.GetAllChannelFolderIds(libraryManager).ToList();

        foreach (var channelFolderId in channelFolderIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var episodes = libraryManager.GetItemList(new InternalItemsQuery
            {
                ChannelIds = new[] { channelFolderId },
                IncludeItemTypes = new[] { BaseItemKind.Episode }
            });

            if (episodes.Count == 0)
            {
                continue;
            }

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var options = new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false };
                var parent = episode.ParentId != Guid.Empty ? libraryManager.GetItemById(episode.ParentId) : null;
                if (parent is not null)
                {
                    libraryManager.DeleteItem(episode, options, parent, false);
                }
                else
                {
                    libraryManager.DeleteItem(episode, options);
                }
            }

            logger.LogDebug("JellyTrend: {Count} episodios sombra eliminados del canal {ChannelId}.", episodes.Count, channelFolderId);
        }
    }

    /// <summary>
    /// Finds the channel shadow item for a library movie or series inside a specific JellyTrend channel.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="channelFolderId">The JellyTrend channel folder id to search in.</param>
    /// <param name="library">The library item (movie or series).</param>
    /// <param name="libraryId">The library item id.</param>
    /// <returns>The shadow item, or <c>null</c> if not found.</returns>
    private static BaseItem? FindChannelShadow(
        ILibraryManager libraryManager,
        Guid channelFolderId,
        BaseItem library,
        Guid libraryId)
    {
        var kind = library is Series ? BaseItemKind.Series : BaseItemKind.Movie;
        var query = new InternalItemsQuery
        {
            ChannelIds = new[] { channelFolderId },
            ExternalId = libraryId.ToString("D"),
            IncludeItemTypes = new[] { kind },
            Limit = 1
        };

        var items = libraryManager.GetItemList(query);
        return items.Count > 0 ? items[0] : null;
    }

    /// <summary>
    /// Finds the channel shadow item associated with a library movie inside a specific JellyTrend channel.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="channelFolderId">The JellyTrend channel folder id to search in.</param>
    /// <param name="libraryMovieId">The library movie id.</param>
    /// <returns>The shadow movie, or <c>null</c> if not found.</returns>
    public static BaseItem? FindShadowMovie(
        ILibraryManager libraryManager,
        Guid channelFolderId,
        Guid libraryMovieId)
    {
        var query = new InternalItemsQuery
        {
            ChannelIds = new[] { channelFolderId },
            ExternalId = libraryMovieId.ToString("D"),
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Limit = 1
        };

        var items = libraryManager.GetItemList(query);
        return items.Count > 0 ? items[0] : null;
    }

    private static async Task SyncOneAsync(
        ILibraryManager libraryManager,
        BaseItem library,
        BaseItem shadow,
        CancellationToken cancellationToken)
    {
        // Solo se persiste cuando algo cambió. Sin esto, cada sync reescribe TODAS las sombras
        // (reparto + metadatos + imágenes) y con Postgres lento provoca timeouts que abortan
        // parte del pase → series que se quedan sin imágenes.
        var changed = false;

        // Reparto: solo se reescribe si el sombra tiene menos reparto que la biblioteca.
        var people = libraryManager.GetPeople(library);
        if (people.Count > 0)
        {
            var shadowPeople = libraryManager.GetPeople(shadow);
            if (shadowPeople.Count < people.Count)
            {
                await libraryManager.UpdatePeopleAsync(shadow, people, cancellationToken).ConfigureAwait(false);
                changed = true;
            }
        }

        changed |= SyncCoreMetadata(shadow, library);
        changed |= SyncCollections(shadow, library);
        changed |= SyncNumericFields(shadow, library);

        if (library is Video libVideo && shadow is Video shVideo)
        {
            changed |= SyncVideoMetadata(shVideo, libVideo);
        }

        var imagesBefore = shadow.ImageInfos.Length;
        CopyImages(library, shadow);
        if (shadow.ImageInfos.Length != imagesBefore)
        {
            changed = true;
        }

        // Asegurar que el sombra quede como item virtual (sin Path local heredado de versiones
        // anteriores que embebían MediaSources y lo volvían LocationType FileSystem).
        if (shadow.Path is not null)
        {
            shadow.Path = null;
            changed = true;
        }

        // Los items sombra del canal pueden quedar huérfanos (ParentId vacío) tras cambios
        // de versión; GetItemById no acepta Guid.Empty, así que solo se persiste cuando hay
        // un parent real y hubo cambios.
        if (changed && shadow.ParentId != Guid.Empty)
        {
            var parent = libraryManager.GetItemById(shadow.ParentId);
            if (parent is not null)
            {
                await libraryManager
                    .UpdateItemAsync(shadow, parent, ItemUpdateType.MetadataEdit | ItemUpdateType.ImageUpdate, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool SyncCoreMetadata(BaseItem shadow, BaseItem library)
    {
        var changed = false;
        if (shadow.Name != library.Name)
        {
            shadow.Name = library.Name;
            changed = true;
        }

        if (shadow.OriginalTitle != library.OriginalTitle)
        {
            shadow.OriginalTitle = library.OriginalTitle;
            changed = true;
        }

        if (shadow.Overview != library.Overview)
        {
            shadow.Overview = library.Overview;
            changed = true;
        }

        if (shadow.OfficialRating != library.OfficialRating)
        {
            shadow.OfficialRating = library.OfficialRating;
            changed = true;
        }

        if (shadow.Container != library.Container)
        {
            shadow.Container = library.Container;
            changed = true;
        }

        if (shadow.HomePageUrl != library.HomePageUrl)
        {
            shadow.HomePageUrl = library.HomePageUrl;
            changed = true;
        }

        if (shadow.CustomRating != library.CustomRating)
        {
            shadow.CustomRating = library.CustomRating;
            changed = true;
        }

        return changed;
    }

    private static bool SyncCollections(BaseItem shadow, BaseItem library)
    {
        var changed = false;
        if (!SameStrings(shadow.Genres, library.Genres))
        {
            shadow.Genres = library.Genres?.ToArray() ?? Array.Empty<string>();
            changed = true;
        }

        if (!SameStrings(shadow.Studios, library.Studios))
        {
            shadow.Studios = library.Studios?.ToArray() ?? Array.Empty<string>();
            changed = true;
        }

        if (!SameStrings(shadow.Tags, library.Tags))
        {
            shadow.Tags = library.Tags?.ToArray() ?? Array.Empty<string>();
            changed = true;
        }

        if (!SameStrings(shadow.ProductionLocations, library.ProductionLocations))
        {
            shadow.ProductionLocations = library.ProductionLocations?.ToArray() ?? Array.Empty<string>();
            changed = true;
        }

        if (!SameProviderIds(shadow.ProviderIds, library.ProviderIds))
        {
            shadow.ProviderIds = new Dictionary<string, string>(library.ProviderIds, StringComparer.OrdinalIgnoreCase);
            changed = true;
        }

        return changed;
    }

    private static bool SyncNumericFields(BaseItem shadow, BaseItem library)
    {
        var changed = false;
        if (!Equals(shadow.CommunityRating, library.CommunityRating))
        {
            shadow.CommunityRating = library.CommunityRating;
            changed = true;
        }

        if (!Equals(shadow.CriticRating, library.CriticRating))
        {
            shadow.CriticRating = library.CriticRating;
            changed = true;
        }

        if (shadow.ProductionYear != library.ProductionYear)
        {
            shadow.ProductionYear = library.ProductionYear;
            changed = true;
        }

        if (shadow.PremiereDate != library.PremiereDate)
        {
            shadow.PremiereDate = library.PremiereDate;
            changed = true;
        }

        if (shadow.DateCreated != library.DateCreated)
        {
            shadow.DateCreated = library.DateCreated;
            changed = true;
        }

        if (shadow.Width != library.Width)
        {
            shadow.Width = library.Width;
            changed = true;
        }

        if (shadow.Height != library.Height)
        {
            shadow.Height = library.Height;
            changed = true;
        }

        if (shadow.TotalBitrate != library.TotalBitrate)
        {
            shadow.TotalBitrate = library.TotalBitrate;
            changed = true;
        }

        return changed;
    }

    private static bool SyncVideoMetadata(Video shadow, Video library)
    {
        var changed = false;
        if (shadow.Tagline != library.Tagline)
        {
            shadow.Tagline = library.Tagline;
            changed = true;
        }

        if (library.RunTimeTicks.HasValue && shadow.RunTimeTicks != library.RunTimeTicks)
        {
            shadow.RunTimeTicks = library.RunTimeTicks;
            changed = true;
        }

        if (shadow.HasSubtitles != library.HasSubtitles)
        {
            shadow.HasSubtitles = library.HasSubtitles;
            changed = true;
        }

        if (shadow.VideoType != library.VideoType)
        {
            shadow.VideoType = library.VideoType;
            changed = true;
        }

        if (shadow.Video3DFormat != library.Video3DFormat)
        {
            shadow.Video3DFormat = library.Video3DFormat;
            changed = true;
        }

        if (shadow.Timestamp != library.Timestamp)
        {
            shadow.Timestamp = library.Timestamp;
            changed = true;
        }

        if (shadow.DefaultVideoStreamIndex != library.DefaultVideoStreamIndex)
        {
            shadow.DefaultVideoStreamIndex = library.DefaultVideoStreamIndex;
            changed = true;
        }

        if (shadow.AspectRatio != library.AspectRatio)
        {
            shadow.AspectRatio = library.AspectRatio;
            changed = true;
        }

        return changed;
    }

    private static bool SameStrings(string[]? a, string[]? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    private static bool SameProviderIds(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var pair in a)
        {
            if (!b.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Makes the shadow item render every image of its library counterpart (primary, backdrops, logo,
    /// thumb…) by referencing the library item's local image files directly. Channel shadow items have
    /// no real media path (their <c>Path</c> is the channel item URL), so copying to
    /// <c>shadow.GetImagePath()</c> produces an invalid filesystem path; referencing the source files
    /// avoids any file IO and is served as-is by Jellyfin.
    /// </summary>
    /// <param name="library">The source library item.</param>
    /// <param name="shadow">The destination channel shadow item.</param>
    private static void CopyImages(BaseItem library, BaseItem shadow)
    {
        if (library.ImageInfos.Length == 0)
        {
            return;
        }

        var referencedImages = new List<ItemImageInfo>(library.ImageInfos.Length);
        foreach (var imageInfo in library.ImageInfos)
        {
            if (string.IsNullOrEmpty(imageInfo.Path) || !File.Exists(imageInfo.Path))
            {
                continue;
            }

            referencedImages.Add(new ItemImageInfo
            {
                Path = imageInfo.Path,
                Type = imageInfo.Type,
                DateModified = imageInfo.DateModified,
                Width = imageInfo.Width,
                Height = imageInfo.Height
            });
        }

        if (referencedImages.Count > 0)
        {
            shadow.ImageInfos = referencedImages.ToArray();
        }
    }
}
