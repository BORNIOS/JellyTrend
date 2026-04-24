using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTrend;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Sync;

/// <summary>
/// Jellyfin solo aplica <c>UpdatePeopleAsync</c> al crear ítems sombra del canal; los ya existentes
/// no reciben reparto ni metadatos desde <see cref="MediaBrowser.Controller.Channels.ChannelItemInfo"/>.
/// Copiamos metadatos clave y el reparto desde la película de biblioteca hacia el sombra.
/// </summary>
public static class TrendingShadowMetadataSync
{
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

        return libraryManager.GetItemList(query).FirstOrDefault();
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
        shadow.ProductionYear = library.ProductionYear;
        shadow.PremiereDate = library.PremiereDate;
        shadow.DateCreated = library.DateCreated;
        shadow.Genres = library.Genres?.ToArray() ?? Array.Empty<string>();
        shadow.Studios = library.Studios?.ToArray() ?? Array.Empty<string>();
        shadow.ProviderIds = new Dictionary<string, string>(library.ProviderIds, StringComparer.OrdinalIgnoreCase);

        if (library is Video libVideo && shadow is Video shVideo)
        {
            shVideo.Tagline = libVideo.Tagline;
            shVideo.RunTimeTicks = libVideo.RunTimeTicks ?? shVideo.RunTimeTicks;
        }

        var parent = libraryManager.GetItemById(shadow.ParentId);
        if (parent is not null)
        {
            await libraryManager
                .UpdateItemAsync(shadow, parent, ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);
        }

        await libraryManager.UpdateImagesAsync(shadow, forceUpdate: true).ConfigureAwait(false);
    }
}
