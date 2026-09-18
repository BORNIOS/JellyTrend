using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Jellyfin.Plugin.JellyTrend.Logging;

namespace Jellyfin.Plugin.JellyTrend.Services.Store;

/// <summary>
/// Courtesy copy of everything the database store holds, written next to the plugin data.
/// </summary>
/// <remarks>
/// <para>
/// The store is optional, so the data must survive losing it: a server that switches from the provider to
/// SQLite, or that uninstalls it, would otherwise start from nothing. Every time the store is prepared this
/// snapshot is refreshed, and the profile of each user is kept separately (see
/// <see cref="JellyTrendStore.WriteAffinities"/>) because that one is read back at runtime, not only by a
/// human.
/// </para>
/// <para>
/// It is a snapshot, not a live format: nothing in the plugin reads it back automatically, and it is written
/// in a single pass with the raw documents of the store so nothing is interpreted, and therefore nothing is
/// lost, on the way out.
/// </para>
/// </remarks>
internal static class StoreDump
{
    private const string FileName = "volcado-almacen.json";

    /// <summary>
    /// Writes the snapshot when the database store is in use.
    /// </summary>
    public static void Write()
    {
        if (!JellyTrendStore.Active || JellyTrendStorage.Folder.Length == 0)
        {
            return;
        }

        try
        {
            var users = JellyTrendStore.ReadUsersWithRecommendations();

            var payload = new
            {
                version = 1,
                dumpedAtUtc = DateTime.UtcNow,
                store = JellyTrendStore.Description,
                trending = JellyTrendStore.ReadTrendingCache(),
                features = JellyTrendStore.ReadItemFeatures(),
                users = users
                    .Select(userId => new
                    {
                        userId,
                        recommendations = JellyTrendStore.ReadRecommendations(userId),
                        consumption = JellyTrendStore.ReadConsumption(userId),
                        affinities = JellyTrendStore.ReadAffinities(userId)
                    })
                    .ToList()
            };

            var path = Path.Combine(JellyTrendStorage.Folder, FileName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(payload));
            File.Move(temporary, path, overwrite: true);

            JellyTrendLog.Info($"[Almacen] Volcado de cortesia escrito: {FileName} ({users.Length} usuarios con recomendaciones).");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            JellyTrendLog.Warn($"[Almacen] No se pudo escribir el volcado de cortesia: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the snapshot back, for diagnostics.
    /// </summary>
    /// <returns>The raw JSON of the last snapshot, or null when there is none.</returns>
    public static string? Read()
        => JellyTrendStorage.Folder.Length == 0
            ? null
            : ReadFile(Path.Combine(JellyTrendStorage.Folder, FileName));

    private static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            JellyTrendLog.Warn($"[Almacen] Volcado de cortesia ilegible: {ex.Message}");
            return null;
        }
    }
}
