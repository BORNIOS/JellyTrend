using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using Jellyfin.Plugin.JellyTrend.Logging;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// File-backed cache of <see cref="ItemFeatures"/> so the work done for one run is not thrown away when
/// the server restarts.
/// </summary>
/// <remarks>
/// <para>
/// A cache that only lives in memory is useless on a server that restarts often: the first user of
/// every run would pay the reads again. This one keeps the features in a JSON file next to the other
/// plugin data and reloads them at startup, so people and facets are read from the library once and
/// then reused.
/// </para>
/// <para>
/// Writes are atomic (temporary file plus replace) to avoid leaving a half-written cache behind, and
/// the file carries a version so a change in what is cached invalidates it instead of reading stale
/// data. If the file is missing, unreadable or from another version, the store simply starts empty and
/// is rebuilt: it is a cache, never a source of truth.
/// </para>
/// </remarks>
internal sealed class FeatureStore
{
    private const string FileName = "features.json";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string? _path;
    private readonly Dictionary<Guid, ItemFeatures> _features;
    private bool _dirty;

    private FeatureStore(string? path, Dictionary<Guid, ItemFeatures> features)
    {
        _path = path;
        _features = features;
    }

    /// <summary>Gets the number of cached items, for diagnostics.</summary>
    public int Count => _features.Count;

    /// <summary>
    /// Opens the cache that lives in the given plugin folder.
    /// </summary>
    /// <param name="pluginFolder">Plugin folder, or null when it is unknown (cache disabled).</param>
    /// <returns>The store.</returns>
    public static FeatureStore Open(string? pluginFolder)
    {
        if (string.IsNullOrWhiteSpace(pluginFolder))
        {
            return new FeatureStore(null, []);
        }

        var path = Path.Combine(pluginFolder, FileName);
        return new FeatureStore(path, Load(path));
    }

    /// <summary>
    /// Gets the cached features of an item.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <param name="features">The cached features, when present.</param>
    /// <returns>Whether the item was cached.</returns>
    public bool TryGet(Guid id, out ItemFeatures features) => _features.TryGetValue(id, out features!);

    /// <summary>
    /// Adds or replaces the features of an item.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <param name="features">Features to remember.</param>
    public void Set(Guid id, ItemFeatures features)
    {
        _features[id] = features;
        _dirty = true;
    }

    /// <summary>
    /// Writes the cache to disk when something changed.
    /// </summary>
    public void Flush()
    {
        if (!_dirty || _path is null)
        {
            return;
        }

        try
        {
            var payload = new CacheFile
            {
                Version = CurrentVersion,
                Items = _features.ToDictionary(
                    static pair => pair.Key.ToString("N"),
                    static pair => (CachedItem?)new CachedItem
                    {
                        Genres = [.. pair.Value.Genres],
                        Tags = [.. pair.Value.Tags],
                        Studios = [.. pair.Value.Studios],
                        People = [.. pair.Value.People.Select(static person => person.ToString("N"))],
                        CommunityRating = pair.Value.CommunityRating,
                        PremiereDate = pair.Value.PremiereDate
                    })
            };

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(payload, SerializerOptions));
            File.Move(temporary, _path, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex)
        {
            // Una cache que no se puede escribir no puede romper la tarea: se sigue con la memoria.
            JellyTrendLog.Warn($"[Recomendaciones] No se pudo guardar la cache de caracteristicas: {ex.Message}");
        }
    }

    private static Dictionary<Guid, ItemFeatures> Load(string path)
    {
        var features = new Dictionary<Guid, ItemFeatures>();

        try
        {
            if (!File.Exists(path))
            {
                return features;
            }

            var payload = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), SerializerOptions);
            if (payload?.Version != CurrentVersion || payload.Items is null)
            {
                return features;
            }

            foreach (var pair in payload.Items)
            {
                if (!Guid.TryParseExact(pair.Key, "N", out var id) || pair.Value is null)
                {
                    continue;
                }

                features[id] = new ItemFeatures(
                    pair.Value.Genres ?? [],
                    pair.Value.Tags ?? [],
                    pair.Value.Studios ?? [],
                    [.. (pair.Value.People ?? []).Select(static person => Guid.TryParseExact(person, "N", out var personId) ? personId : Guid.Empty).Where(static personId => personId != Guid.Empty)],
                    pair.Value.CommunityRating,
                    pair.Value.PremiereDate);
            }
        }
        catch (Exception ex)
        {
            JellyTrendLog.Warn($"[Recomendaciones] Cache de caracteristicas ilegible, se reconstruira: {ex.Message}");
        }

        return features;
    }

    private sealed class CacheFile
    {
        public int Version { get; set; }

        public Dictionary<string, CachedItem?>? Items { get; set; }
    }

    private sealed class CachedItem
    {
        public string[]? Genres { get; set; }

        public string[]? Tags { get; set; }

        public string[]? Studios { get; set; }

        public string[]? People { get; set; }

        public float? CommunityRating { get; set; }

        public DateTime? PremiereDate { get; set; }
    }
}
