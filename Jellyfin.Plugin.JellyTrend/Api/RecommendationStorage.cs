using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyTrend.Api;

/// <summary>
/// Reads and writes per-user recommendation files under {PluginFolder}/recommendations/.
/// Each user gets their own file (userId as the filename) so the channel can read exactly
/// what to recommend for the requesting user without parsing unrelated data.
/// </summary>
public static class RecommendationStorage
{
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = true };

    private static string Folder
        => Path.Combine(Plugin.Instance!.PluginFolder, "recommendations");

    private static string GetUserFilePath(Guid userId)
        => Path.Combine(Folder, userId.ToString("D") + ".json");

    /// <summary>
    /// Reads the stored recommendations for a user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The stored recommendations, or <c>null</c> when none exist for that user.</returns>
    public static UserRecommendations? Read(Guid userId)
    {
        var path = GetUserFilePath(userId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UserRecommendations>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the most recently written recommendation file regardless of user. Jellyfin often
    /// queries channels with an empty user id (Guid.Empty), so a strict per-user lookup would
    /// return nothing and leave the channel empty. Falling back to any stored file mirrors how
    /// the Trending channel uses its shared cache: the channel always has content when
    /// recommendations exist.
    /// </summary>
    /// <returns>The stored recommendations, or <c>null</c> when no recommendation file exists.</returns>
    public static UserRecommendations? ReadAny()
    {
        var folder = Folder;
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var file = Directory.GetFiles(folder, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (file is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UserRecommendations>(File.ReadAllText(file));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the recommendations for a user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="data">The recommendations to persist.</param>
    public static void Write(Guid userId, UserRecommendations data)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(GetUserFilePath(userId), JsonSerializer.Serialize(data, CacheJsonOptions));
    }

    /// <summary>
    /// Returns the newest modification time across all recommendation files, used to bump
    /// the channel's DataVersion after each weekly sync.
    /// </summary>
    /// <returns>The newest file modification time (UTC), or <see cref="DateTime.MinValue"/> when empty.</returns>
    public static DateTime GetLastModifiedUtc()
    {
        var folder = Folder;
        if (!Directory.Exists(folder))
        {
            return DateTime.MinValue;
        }

        var files = Directory.GetFiles(folder, "*.json");
        return files.Length == 0 ? DateTime.MinValue : files.Max(File.GetLastWriteTimeUtc);
    }

    /// <summary>
    /// Returns every recommended library item id across all users (de-duplicated). Used to
    /// sync the channel shadow items of the recommendations channel on server startup.
    /// </summary>
    /// <returns>The unique recommended item ids.</returns>
    public static IReadOnlyList<Guid> ReadAllItemIds()
    {
        var folder = Folder;
        if (!Directory.Exists(folder))
        {
            return Array.Empty<Guid>();
        }

        var result = new HashSet<Guid>();
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            try
            {
                var data = JsonSerializer.Deserialize<UserRecommendations>(File.ReadAllText(file));
                if (data is null)
                {
                    continue;
                }

                foreach (var id in data.ItemIds)
                {
                    result.Add(id);
                }
            }
            catch
            {
                // Un solo archivo corrupto no debe impedir leer el resto.
            }
        }

        return result.ToList();
    }
}
