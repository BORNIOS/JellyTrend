using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend.Services.Recommendation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Services.Sync;

/// <summary>
/// Refreshes the taste profile of a user as soon as they finish watching something.
/// </summary>
/// <remarks>
/// <para>
/// Without this the profile only moves once a week: someone who watches three horror films on Friday would
/// keep seeing Friday's row until the weekly task ran. The refresh is incremental — only the title that was
/// played is re-read from the library, and the profile is recomputed from the aggregated consumption — so it
/// is cheap enough to run on an event.
/// </para>
/// <para>
/// Playback events are chatty (progress is reported every few seconds), so only the end of playback counts
/// and each user is refreshed at most once every <see cref="RefreshInterval"/>. The work runs on a
/// background task: the event must not block the playback pipeline, and a failure there must never be
/// visible to whoever is watching.
/// </para>
/// </remarks>
public sealed class ConsumptionRefreshConsumer : IEventConsumer<PlaybackStopEventArgs>
{
    /// <summary>Minimum time between two refreshes of the same user.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, DateTime> _lastRefreshUtc = new();
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<ConsumptionRefreshConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsumptionRefreshConsumer"/> class.
    /// </summary>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ConsumptionRefreshConsumer}"/> interface.</param>
    public ConsumptionRefreshConsumer(
        IUserDataManager userDataManager,
        ILogger<ConsumptionRefreshConsumer> logger)
    {
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <summary>
    /// Refreshes the profile of every user that stopped watching the title.
    /// </summary>
    /// <param name="eventArgs">The playback stop event arguments.</param>
    /// <returns>A completed task: the refresh continues in the background.</returns>
    public Task OnEvent(PlaybackStopEventArgs eventArgs)
    {
        var item = eventArgs.Item;

        // Los items virtuales son las sombras de los canales del propio plugin: lo que enseña la
        // biblioteca es el item real, y de ese es del que aprende el perfil.
        if (item is null || item.IsVirtualItem)
        {
            return Task.CompletedTask;
        }

        foreach (var user in eventArgs.Users)
        {
            if (!ShouldRefresh(user.Id))
            {
                continue;
            }

            _ = Task.Run(() => Refresh(user, item));
        }

        return Task.CompletedTask;
    }

    private bool ShouldRefresh(Guid userId)
    {
        var now = DateTime.UtcNow;
        if (_lastRefreshUtc.TryGetValue(userId, out var last) && now - last < RefreshInterval)
        {
            return false;
        }

        _lastRefreshUtc[userId] = now;
        return true;
    }

    private void Refresh(User user, BaseItem item)
    {
        try
        {
            // La cache de caracteristicas se abre aqui, dentro del refresco ya espaciado, y no en cada
            // evento: es la unica lectura cara que necesita la capa 2.
            var features = FeatureStore.Open(JellyTrendStorage.Folder);
            var data = _userDataManager.GetUserData(user, item);

            TasteProfileService.RefreshItem(
                user.Id,
                user.Username,
                item,
                data,
                id => features.TryGet(id, out var cached) ? cached : null,
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // Un refresco que falla no puede romper la reproduccion ni la corrida semanal: la proxima
            // vez que este titulo se reproduzca, o la tarea semanal, lo recuperan.
            _logger.LogDebug(ex, "JellyTrend: refresco incremental del perfil omitido para '{User}'.", user.Username);
        }
    }
}
