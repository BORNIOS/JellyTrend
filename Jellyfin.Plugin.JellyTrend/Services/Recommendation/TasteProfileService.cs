using System;
using System.Collections.Generic;

using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend.Logging;
using Jellyfin.Plugin.JellyTrend.Services.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Keeps the taste profile of a user up to date: layer 1 (consumption) then layer 2 (affinities).
/// </summary>
/// <remarks>
/// <para>
/// This is the piece the weekly task no longer has to do. The first run walks the whole history
/// (bootstrap) so a JellyTrend installed today on a long-used server starts with a real profile; later
/// runs refresh only what changed.
/// </para>
/// <para>
/// The profile is stored, not recomputed per recommendation: the weekly run reads it and combines it with
/// the candidate pool. Below <see cref="ColdStartThreshold"/> consumed titles the profile is too thin to
/// personalize, and the caller should fall back to what the server itself rates highly.
/// </para>
/// </remarks>
internal static class TasteProfileService
{
    /// <summary>Consumed titles below which the profile is not trustworthy yet.</summary>
    public const int ColdStartThreshold = 10;

    /// <summary>
    /// Rebuilds the profile of a user from their history and stores it.
    /// </summary>
    /// <param name="user">User to profile.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="facets">Looks up the cached facets of a title, or null when there are none.</param>
    /// <param name="nowUtc">Current UTC time.</param>
    /// <returns>The rebuilt profile, strongest first.</returns>
    public static IReadOnlyList<AffinityRecord> Rebuild(
        User user,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        Func<Guid, ItemFeatures?> facets,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(facets);

        var consumption = ConsumptionAggregator.Build(user, libraryManager, userDataManager, nowUtc);
        ConsumptionAggregator.Save(user.Id, consumption);

        return RebuildAndStore(user.Id, user.Username, consumption, facets);
    }

    /// <summary>
    /// Refreshes the consumption of a single title and the profile that depends on it, without waiting for
    /// the weekly run.
    /// </summary>
    /// <remarks>
    /// Called from the playback events. It is the same math as <see cref="Rebuild"/>, but only one title is
    /// re-read: the record of the played title is replaced and the profile is recomputed from the stored
    /// consumption, which is cheap because layer 2 works on the aggregated records and not on the history.
    /// </remarks>
    /// <param name="userId">User who played the title.</param>
    /// <param name="username">Name of the user, for the log.</param>
    /// <param name="item">Title that was played.</param>
    /// <param name="data">User data of that title after the playback.</param>
    /// <param name="facets">Looks up the cached facets of a title, or null when there are none.</param>
    /// <param name="nowUtc">Current UTC time.</param>
    /// <returns>The refreshed profile, strongest first.</returns>
    public static IReadOnlyList<AffinityRecord> RefreshItem(
        Guid userId,
        string username,
        BaseItem item,
        UserItemData? data,
        Func<Guid, ItemFeatures?> facets,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(facets);

        var consumption = ConsumptionAggregator.Load(userId);
        var record = ConsumptionAggregator.BuildRecord(item, data, nowUtc);

        if (record is null)
        {
            consumption.Remove(item.Id);
        }
        else
        {
            consumption[item.Id] = record;
        }

        ConsumptionAggregator.Save(userId, consumption);

        return RebuildAndStore(userId, username, consumption, facets);
    }

    /// <summary>
    /// Indicates whether the stored history is enough to personalize, or the user is still cold.
    /// </summary>
    /// <param name="userId">User to check.</param>
    /// <returns><see langword="true"/> when the profile has enough history behind it.</returns>
    public static bool HasEnoughHistory(Guid userId)
        => ConsumptionAggregator.Load(userId).Count >= ColdStartThreshold;

    // Capa 2: el consumo (ya agregado) se convierte en afinidades y queda guardado. El reparto por rol de
    // cada titulo se toma de la cache de caracteristicas, que es la que sabe quien dirigio, escribio y
    // actuo, ademas del genero, las etiquetas y el estudio.
    private static IReadOnlyList<AffinityRecord> RebuildAndStore(
        Guid userId,
        string username,
        Dictionary<Guid, ConsumptionRecord> consumption,
        Func<Guid, ItemFeatures?> facets)
    {
        var items = new List<ProfileItem>(consumption.Count);

        foreach (var record in consumption)
        {
            var features = facets(record.Key);

            items.Add(new ProfileItem
            {
                Id = record.Key,
                Weight = record.Value.Weight,
                Genres = features?.Genres ?? [],
                Tags = features?.Tags ?? [],
                Studios = features?.Studios ?? [],
                Directors = features?.Directors ?? [],
                Actors = features?.Actors ?? [],
                Writers = features?.Writers ?? [],
                Collection = features?.Collection,
                Decade = FacetLabels.DecadeOf(features?.PremiereDate),
                Rating = features?.CommunityRating
            });
        }

        var profile = TasteProfileBuilder.Build(items);
        var written = JellyTrendStore.WriteAffinities(userId, profile);

        JellyTrendLog.Info(
            $"[Perfil] {username}: {consumption.Count} titulos consumidos, {profile.Count} afinidades ({written} filas en el almacen).");

        return profile;
    }
}
