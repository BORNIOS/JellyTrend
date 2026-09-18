using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyTrend.Logging;
using Jellyfin.Plugin.JellyTrend.Services.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Layer 1: turns a user's history into an aggregated consumption per title.
/// </summary>
/// <remarks>
/// <para>
/// This is the only layer that reads the history. Its first run walks everything Jellyfin already knows
/// (<b>bootstrap</b>), so a JellyTrend installed today on a server used for a year has a complete profile
/// from the first run; later runs only refresh what changed.
/// </para>
/// <para>
/// It aggregates by title, never by episode: a series watched through contributes one record carrying the
/// series id, so a 62-episode binge cannot weigh 62 times in the profile.
/// </para>
/// </remarks>
public static class ConsumptionAggregator
{
    private const int MaxHistoryItems = 5000;

    /// <summary>How long an unfinished title must sit untouched before it counts as abandoned.</summary>
    private static readonly TimeSpan AbandonAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// Builds the aggregated consumption of a user from the history Jellyfin already has (bootstrap).
    /// </summary>
    /// <param name="user">User to aggregate.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="nowUtc">Current UTC time, used to decide abandonment.</param>
    /// <returns>One record per title the user has interacted with.</returns>
    public static Dictionary<Guid, ConsumptionRecord> Build(
        User user,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userDataManager);

        var items = CollectHistory(user, libraryManager);
        if (items.Count == 0)
        {
            return [];
        }

        var userData = ApiCompat.GetUserData(userDataManager, items, user);
        var records = new Dictionary<Guid, ConsumptionRecord>();

        foreach (var item in items)
        {
            userData.TryGetValue(item.Id, out var data);
            var record = BuildRecord(item, data, nowUtc);
            if (record is not null)
            {
                records[item.Id] = record;
            }
        }

        return records;
    }

    /// <summary>
    /// Builds the consumption of one title. Pure enough to be tested without a server.
    /// </summary>
    /// <param name="item">Title the user interacted with.</param>
    /// <param name="data">Jellyfin user data for that title, or null when unknown.</param>
    /// <param name="nowUtc">Current UTC time.</param>
    /// <returns>The record, or null when the interaction does not deserve one.</returns>
    public static ConsumptionRecord? BuildRecord(BaseItem item, UserItemData? data, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (data is null)
        {
            return null;
        }

        var progress = Progress(data, item);
        var playCount = Math.Max(data.PlayCount, data.Played ? 1 : 0);
        var lastPlayed = data.LastPlayedDate;

        // Abandono: se empezo, no se termino, paso el tiempo y no se retomo. El peso lo decide
        // InteractionWeight, que ademas exige exposicion minima.
        var abandoned = !data.Played
            && progress >= InteractionWeight.AbandonThreshold
            && lastPlayed is { } seen
            && nowUtc - seen > AbandonAfter;

        var weight = InteractionWeight.Compute(progress, playCount, data.IsFavorite, abandoned);
        if (weight == 0d && !abandoned)
        {
            // Reproduccion accidental: no ensena nada y no se guarda.
            return null;
        }

        return new ConsumptionRecord
        {
            ItemId = item.Id,
            SeriesId = item is MediaBrowser.Controller.Entities.TV.Episode episode && episode.SeriesId != Guid.Empty
                ? episode.SeriesId
                : null,
            PlayCount = playCount,
            Progress = progress,
            Favorite = data.IsFavorite,
            Abandoned = abandoned,
            LastPlayedUtc = lastPlayed?.ToUniversalTime(),
            Weight = weight,
            UpdatedAtUtc = nowUtc
        };
    }

    /// <summary>
    /// Persists the aggregated consumption of a user.
    /// </summary>
    /// <param name="userId">User the consumption belongs to.</param>
    /// <param name="records">Records to store.</param>
    public static void Save(Guid userId, IReadOnlyDictionary<Guid, ConsumptionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (!JellyTrendStore.Active || records.Count == 0)
        {
            return;
        }

        var documents = records.ToDictionary(
            static pair => pair.Key,
            static pair => JsonSerializer.Serialize(pair.Value));

        var written = JellyTrendStore.WriteConsumption(userId, documents);
        JellyTrendLog.Info($"[Perfil] Consumo agregado: {records.Count} titulos ({written} filas escritas).");
    }

    /// <summary>
    /// Reads the aggregated consumption of a user.
    /// </summary>
    /// <param name="userId">User to read.</param>
    /// <returns>The stored records, empty when there are none.</returns>
    public static Dictionary<Guid, ConsumptionRecord> Load(Guid userId)
    {
        var documents = JellyTrendStore.ReadConsumption(userId);
        var records = new Dictionary<Guid, ConsumptionRecord>();

        foreach (var pair in documents)
        {
            try
            {
                var record = JsonSerializer.Deserialize<ConsumptionRecord>(pair.Value);
                if (record is not null)
                {
                    records[pair.Key] = record;
                }
            }
            catch (JsonException ex)
            {
                JellyTrendLog.Warn($"[Perfil] Consumo ilegible para el titulo {pair.Key}: {ex.Message}");
            }
        }

        return records;
    }

    private static List<BaseItem> CollectHistory(User user, ILibraryManager libraryManager)
    {
        var items = new List<BaseItem>();

        items.AddRange(libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsPlayed = true,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = MaxHistoryItems
        }));

        // Lo empezado y no terminado tambien describe interes (y de ahi salen los abandonos).
        items.AddRange(libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsResumable = true,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = MaxHistoryItems
        }));

        return items
            .GroupBy(static item => item.Id)
            .Select(static group => group.First())
            .ToList();
    }

    private static double Progress(UserItemData data, BaseItem item)
    {
        if (data.Played)
        {
            return 1.0;
        }

        var runtime = item.RunTimeTicks ?? 0;
        if (runtime <= 0)
        {
            return 0d;
        }

        var position = data.PlaybackPositionTicks;
        return Math.Clamp((double)position / runtime, 0d, 1d);
    }
}
