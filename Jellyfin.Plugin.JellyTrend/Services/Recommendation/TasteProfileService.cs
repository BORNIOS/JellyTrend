using System;
using System.Collections.Generic;

using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend.Logging;
using Jellyfin.Plugin.JellyTrend.Services.Store;
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
                Studios = features?.Studios ?? []
            });
        }

        var profile = TasteProfileBuilder.Build(items);
        var written = JellyTrendStore.WriteAffinities(user.Id, profile);

        JellyTrendLog.Info(
            $"[Perfil] {user.Username}: {consumption.Count} titulos consumidos, {profile.Count} afinidades ({written} filas en el almacen).");

        return profile;
    }

    /// <summary>
    /// Indicates whether the stored history is enough to personalize, or the user is still cold.
    /// </summary>
    /// <param name="userId">User to check.</param>
    /// <returns><see langword="true"/> when the profile has enough history behind it.</returns>
    public static bool HasEnoughHistory(Guid userId)
        => ConsumptionAggregator.Load(userId).Count >= ColdStartThreshold;
}
