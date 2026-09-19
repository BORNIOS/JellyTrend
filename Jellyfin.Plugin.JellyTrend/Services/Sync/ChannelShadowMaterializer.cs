using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Services.Sync;

/// <summary>
/// Creates the channel shadow items of the JellyTrend channels on purpose, instead of waiting for a client
/// to browse the channel.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin persists a channel item the first time someone asks the channel for its contents. That made the
/// shadows depend on a client having opened the channel: measured on a real server, the recommendations
/// channel had 50 shadows with images and cast, and the trending channel had <b>none</b>. Without a shadow
/// there is nowhere to copy metadata to, so the row is served live from the channel provider — with the
/// remote TMDB image as the only artwork and no cast on the detail page.
/// </para>
/// <para>
/// Asking the channel manager for the items is the same call the API controller makes, so the faces the
/// channel already serves are the ones persisted: no id is invented here and no item is created twice. The
/// call is idempotent — an item that already exists is returned, not duplicated — which is what makes it
/// safe to run after every sync.
/// </para>
/// </remarks>
public sealed class ChannelShadowMaterializer
{
    /// <summary>Ceiling per channel, so a broken provider cannot materialize the whole library at once.</summary>
    private const int MaxItemsPerChannel = 500;

    private readonly IChannelManager _channelManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<ChannelShadowMaterializer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelShadowMaterializer"/> class.
    /// </summary>
    /// <param name="channelManager">Instance of the <see cref="IChannelManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ChannelShadowMaterializer}"/> interface.</param>
    public ChannelShadowMaterializer(
        IChannelManager channelManager,
        IUserManager userManager,
        ILogger<ChannelShadowMaterializer> logger)
    {
        _channelManager = channelManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Materializes the shadows of the given channels and returns how many items came back.
    /// </summary>
    /// <param name="channelFolderIds">Channel folder ids to materialize.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of channel items returned by the manager.</returns>
    public async Task<int> MaterializeAsync(IReadOnlyList<Guid> channelFolderIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelFolderIds);

        if (channelFolderIds.Count == 0)
        {
            return 0;
        }

        // El canal decide que sirve segun lo que cada usuario ya vio, y la sombra es una sola fila por
        // canal e item: se recorre cada usuario para que la union de sus vistas quede persistida, en lugar
        // de dejar fuera a quien no coincide con el primer usuario de la lista.
        var users = _userManager.GetUsers().ToList();
        if (users.Count == 0)
        {
            _logger.LogDebug("JellyTrend: sin usuarios no se materializan sombras.");
            return 0;
        }

        var materialized = 0;

        foreach (var channelId in channelFolderIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var served = 0;

            foreach (var user in users)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var result = await _channelManager
                        .GetChannelItems(
                            new InternalItemsQuery
                            {
                                ChannelIds = [channelId],

                                // El canal filtra por lo que cada uno ha visto y resuelve el espectador a
                                // partir de este usuario: sin el, la consulta llegaba sin espectador y
                                // devolvia cero elementos.
                                User = user,
                                Limit = MaxItemsPerChannel
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    served += result.Items.Count;
                    materialized += result.Items.Count;
                }
                catch (Exception ex)
                {
                    // Materializar es una mejora: si el canal falla, la fila se sigue sirviendo en vivo como
                    // antes en lugar de romper la tarea.
                    _logger.LogWarning(
                        ex,
                        "JellyTrend: no se pudieron materializar las sombras del canal {ChannelId} para '{User}'.",
                        channelId,
                        user.Username);
                }
            }

            _logger.LogInformation(
                "JellyTrend: canal {ChannelId} materializado para {Users} usuarios ({Count} elementos servidos).",
                channelId,
                users.Count,
                served);
        }

        return materialized;
    }
}
