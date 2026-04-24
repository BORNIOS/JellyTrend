using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend;
using Jellyfin.Plugin.JellyTrend.ScheduledTask;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Sync;

/// <summary>
/// Replica <see cref="UserItemData"/> entre la película de biblioteca y el ítem sombra del canal.
/// La reanudación parcial se mantiene solo en biblioteca; el sombra recibe visto/favoritos/valoración
/// alineados para evitar duplicados en «Continuar viendo».
/// </summary>
public sealed class TrendingLibraryLinkService
    : IHostedService,
        IEventConsumer<PlaybackStopEventArgs>,
        IEventConsumer<PlaybackProgressEventArgs>
{
    private static readonly TimeSpan ProgressMirrorInterval = TimeSpan.FromSeconds(90);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<TrendingLibraryLinkService> _logger;

    private int _mirrorSaveDepth;
    private readonly ConcurrentDictionary<string, DateTime> _lastProgressMirrorUtc = new();

    public TrendingLibraryLinkService(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILogger<TrendingLibraryLinkService> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(40), CancellationToken.None).ConfigureAwait(false);
                    var cache = ReadTrendingCache();
                    if (cache is null || cache.ItemIds.Count == 0)
                    {
                        return;
                    }

                    await TrendingShadowMetadataSync
                        .SyncAllAsync(_libraryManager, cache.ItemIds, _logger, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "JellyTrend: sincronización diferida de metadatos sombra omitida.");
                }
            },
            CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _mirrorSaveDepth) > 0)
        {
            return;
        }

        if (e is not UserDataSaveEventArgs args)
        {
            return;
        }

        try
        {
            HandleUserDataSaved(args);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyTrend: error al replicar datos de usuario.");
        }
    }

    public Task OnEvent(PlaybackStopEventArgs eventArgs)
    {
        MirrorPlaybackEvent(eventArgs.Item, eventArgs.Users, eventArgs.PlaybackPositionTicks, eventArgs.PlayedToCompletion);
        return Task.CompletedTask;
    }

    public Task OnEvent(PlaybackProgressEventArgs eventArgs)
    {
        MirrorPlaybackEvent(eventArgs.Item, eventArgs.Users, eventArgs.PlaybackPositionTicks, playedToCompletion: null);
        return Task.CompletedTask;
    }

    private void MirrorPlaybackEvent(
        BaseItem? playedItem,
        IReadOnlyList<User> users,
        long? playbackPositionTicks,
        bool? playedToCompletion)
    {
        if (playedItem is null || users.Count == 0)
        {
            return;
        }

        if (!IsJellyTrendChannelShadow(playedItem) && !IsTrendingLibraryMovie(playedItem))
        {
            return;
        }

        foreach (var user in users)
        {
            try
            {
                var sourceData = _userDataManager.GetUserData(user, playedItem);
                if (sourceData is null)
                {
                    continue;
                }

                if (IsJellyTrendChannelShadow(playedItem)
                    && Guid.TryParse(playedItem.ExternalId, out var libraryId))
                {
                    var libraryItem = _libraryManager.GetItemById(libraryId);
                    if (libraryItem is null)
                    {
                        continue;
                    }

                    var copy = CloneForTarget(user, sourceData, libraryItem);
                    ApplyPlaybackHints(copy, libraryItem, playbackPositionTicks, playedToCompletion);
                    SaveMirrored(user, libraryItem, copy);
                    ResyncShadowUserDataWithoutPartialResume(user, libraryItem, playedItem);
                }
                else if (IsTrendingLibraryMovie(playedItem))
                {
                    var shadow = TrendingShadowMetadataSync.FindShadowMovie(_libraryManager, playedItem.Id);
                    if (shadow is null)
                    {
                        continue;
                    }

                    var copy = CloneForTarget(user, sourceData, shadow);
                    ApplyPlaybackHints(copy, playedItem, playbackPositionTicks, playedToCompletion);
                    copy.PlaybackPositionTicks = 0;
                    SaveMirrored(user, shadow, copy);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyTrend: error al replicar reproducción para el usuario {UserId}.", user.Id);
            }
        }
    }

    private void HandleUserDataSaved(UserDataSaveEventArgs args)
    {
        if (args.SaveReason == UserDataSaveReason.PlaybackProgress
            && !ShouldAllowProgressMirror(args.UserId, args.Item.Id))
        {
            return;
        }

        var user = _userManager.GetUserById(args.UserId);
        if (user is null)
        {
            return;
        }

        if (IsJellyTrendChannelShadow(args.Item)
            && Guid.TryParse(args.Item.ExternalId, out var libraryId))
        {
            var libraryItem = _libraryManager.GetItemById(libraryId);
            if (libraryItem is null)
            {
                return;
            }

            var data = CloneForTarget(user, args.UserData, libraryItem);
            SaveMirrored(user, libraryItem, data);
            ResyncShadowUserDataWithoutPartialResume(user, libraryItem, args.Item);
        }
        else if (IsTrendingLibraryMovie(args.Item))
        {
            var shadow = TrendingShadowMetadataSync.FindShadowMovie(_libraryManager, args.Item.Id);
            if (shadow is null)
            {
                return;
            }

            var data = CloneForTarget(user, args.UserData, shadow);
            data.PlaybackPositionTicks = 0;
            SaveMirrored(user, shadow, data);
        }
    }

    /// <summary>
    /// La fila «Continuar viendo» usa ítems con posición de reanudación; si biblioteca y sombra la tienen,
    /// aparecen duplicados. Tras volcar al sombra → biblioteca, el sombra queda sin ticks de reanudación.
    /// </summary>
    private void ResyncShadowUserDataWithoutPartialResume(User user, BaseItem libraryItem, BaseItem shadow)
    {
        var libData = _userDataManager.GetUserData(user, libraryItem);
        if (libData is null)
        {
            return;
        }

        var copy = CloneForTarget(user, libData, shadow);
        copy.PlaybackPositionTicks = 0;
        SaveMirrored(user, shadow, copy);
    }

    private bool ShouldAllowProgressMirror(Guid userId, Guid itemId)
    {
        var key = $"{userId:N}:{itemId:N}";
        var now = DateTime.UtcNow;
        if (_lastProgressMirrorUtc.TryGetValue(key, out var last) && now - last < ProgressMirrorInterval)
        {
            return false;
        }

        _lastProgressMirrorUtc[key] = now;
        return true;
    }

    private static void ApplyPlaybackHints(
        UserItemData target,
        BaseItem runtimeItem,
        long? playbackPositionTicks,
        bool? playedToCompletion)
    {
        if (playedToCompletion == true && runtimeItem.RunTimeTicks > 0)
        {
            target.PlaybackPositionTicks = 0;
            target.Played = true;
        }
        else if (playbackPositionTicks.HasValue)
        {
            target.PlaybackPositionTicks = playbackPositionTicks.Value;
        }
    }

    private void SaveMirrored(User user, BaseItem target, UserItemData data)
    {
        Interlocked.Increment(ref _mirrorSaveDepth);
        try
        {
            _userDataManager.SaveUserData(user, target, data, UserDataSaveReason.UpdateUserData, CancellationToken.None);
        }
        finally
        {
            Interlocked.Decrement(ref _mirrorSaveDepth);
        }
    }

    private UserItemData CloneForTarget(User user, UserItemData source, BaseItem target)
    {
        var targetExisting = _userDataManager.GetUserData(user, target);
        var key = targetExisting?.Key ?? target.GetUserDataKeys()[0];

        return new UserItemData
        {
            Key = key,
            PlaybackPositionTicks = source.PlaybackPositionTicks,
            Played = source.Played,
            PlayCount = source.PlayCount,
            LastPlayedDate = source.LastPlayedDate,
            IsFavorite = source.IsFavorite,
            Likes = source.Likes,
            Rating = source.Rating,
            AudioStreamIndex = source.AudioStreamIndex,
            SubtitleStreamIndex = source.SubtitleStreamIndex
        };
    }

    private bool IsJellyTrendChannelShadow(BaseItem item)
    {
        if (string.IsNullOrEmpty(item.ExternalId) || item.ChannelId == Guid.Empty)
        {
            return false;
        }

        var folderId = ChannelIdentity.GetPluginChannelFolderId(_libraryManager);
        return item.ChannelId == folderId;
    }

    private bool IsTrendingLibraryMovie(BaseItem item)
    {
        if (item.ChannelId != Guid.Empty || string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        var cache = ReadTrendingCache();
        return cache is not null && cache.ItemIds.Contains(item.Id);
    }

    private static TrendingCache? ReadTrendingCache()
    {
        var inst = Plugin.Instance;
        if (inst is null)
        {
            return null;
        }

        var path = Path.Combine(inst.PluginFolder, "trending.json");
        if (!File.Exists(path))
        {
            return null;
        }

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
