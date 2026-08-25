using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTrend;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Sync;

/// <summary>
/// Jellyfin solo aplica <c>UpdatePeopleAsync</c> al crear ítems sombra del canal; los ya existentes
/// no reciben reparto ni metadatos desde <see cref="MediaBrowser.Controller.Channels.ChannelItemInfo"/>.
/// Copiamos metadatos clave y el reparto desde la película de biblioteca hacia el sombra.
/// </summary>
public static class TrendingShadowMetadataSync
{
    /// <summary>
    /// Copies key metadata and cast from each library movie to its channel shadow item.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="libraryMovieIds">The library movie ids to sync.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task SyncAllAsync(
        ILibraryManager libraryManager,
        IReadOnlyList<Guid> libraryMovieIds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var id in libraryMovieIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var library = libraryManager.GetItemById(id);
                if (library is null)
                {
                    continue;
                }

                var shadow = FindShadowMovie(libraryManager, id);
                if (shadow is null)
                {
                    continue;
                }

                await SyncOneAsync(libraryManager, library, shadow, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "JellyTrend: no se pudo sincronizar metadatos del sombra para {ItemId}.", id);
            }
        }
    }

    /// <summary>
    /// Finds the channel shadow item associated with a library movie.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="libraryMovieId">The library movie id.</param>
    /// <returns>The shadow movie, or <c>null</c> if not found.</returns>
    public static BaseItem? FindShadowMovie(ILibraryManager libraryManager, Guid libraryMovieId)
    {
        var channelFolderId = ChannelIdentity.GetPluginChannelFolderId(libraryManager);
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
        var people = libraryManager.GetPeople(library);
        if (people.Count > 0)
        {
            await libraryManager.UpdatePeopleAsync(shadow, people, cancellationToken).ConfigureAwait(false);
        }

        shadow.Name = library.Name;
        shadow.OriginalTitle = library.OriginalTitle;
        shadow.Overview = library.Overview;
        shadow.OfficialRating = library.OfficialRating;
        shadow.CommunityRating = library.CommunityRating;
        shadow.CriticRating = library.CriticRating;
        shadow.ProductionYear = library.ProductionYear;
        shadow.PremiereDate = library.PremiereDate;
        shadow.DateCreated = library.DateCreated;
        shadow.Genres = library.Genres?.ToArray() ?? Array.Empty<string>();
        shadow.Studios = library.Studios?.ToArray() ?? Array.Empty<string>();
        shadow.Tags = library.Tags?.ToArray() ?? Array.Empty<string>();
        shadow.ProviderIds = new Dictionary<string, string>(library.ProviderIds, StringComparer.OrdinalIgnoreCase);

        if (library is Video libVideo && shadow is Video shVideo)
        {
            shVideo.Tagline = libVideo.Tagline;
            shVideo.RunTimeTicks = libVideo.RunTimeTicks ?? shVideo.RunTimeTicks;
        }

        CopyImages(library, shadow);

        var parent = libraryManager.GetItemById(shadow.ParentId);
        if (parent is not null)
        {
            await libraryManager
                .UpdateItemAsync(shadow, parent, ItemUpdateType.MetadataEdit | ItemUpdateType.ImageUpdate, cancellationToken)
                .ConfigureAwait(false);
        }
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
