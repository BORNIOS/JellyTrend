using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

using Jellyfin.Plugin.JellyTrend.Api;
using Jellyfin.Plugin.JellyTrend.Logging;
using Jellyfin.Plugin.JellyTrend.Services.Recommendation;
using Jellyfin.Plugin.JellyTrend.Tasks;

namespace Jellyfin.Plugin.JellyTrend.Services.Store;

/// <summary>
/// Single entry point for the plugin's persistent data: uses the database provider's store when it is
/// available and falls back to the JSON files otherwise.
/// </summary>
/// <remarks>
/// <para>
/// The store is optional. A provider that does not offer the contract, a server running SQLite, or a
/// provider that fails halfway must never break the plugin, so every call degrades: if the external store
/// throws, it is dropped for the rest of the session and the JSON path keeps working. That is why the
/// facade is the only place that knows about the contract — the callers just read and write.
/// </para>
/// <para>
/// The schema is created here, on demand, when the store is prepared. A server without JellyTrend never
/// gets these tables.
/// </para>
/// </remarks>
public static class JellyTrendStore
{
    private static IJellyTrendStoreProvider? _provider;
    private static string _description = "archivos JSON";

    /// <summary>
    /// Gets a stamp that changes every time the trending list is written, so consumers can refresh
    /// without looking at the file system.
    /// </summary>
    public static string DataStamp { get; private set; } = "1";

    /// <summary>
    /// Gets a stamp that changes every time a user's recommendations are written, so the channel
    /// discards its cached items after each run.
    /// </summary>
    public static string RecommendationStamp { get; private set; } = "1";

    /// <summary>
    /// Gets a value indicating whether the database store is active.
    /// </summary>
    public static bool Active => _provider is not null;

    /// <summary>
    /// Gets a short description of where the data is being kept, for the log.
    /// </summary>
    public static string Description => _description;

    /// <summary>
    /// Gets the version of the schema of the database store.
    /// </summary>
    /// <value>The version reported by the provider, or 0 when the store is not in use.</value>
    public static int SchemaVersion
        => Guard(static provider => provider.GetSchemaVersion(), 0, "leer la version del esquema");

    /// <summary>
    /// Adopts the store offered by another plugin, replacing any previous one.
    /// </summary>
    /// <param name="provider">The store, or null to keep using JSON files.</param>
    public static void Use(IJellyTrendStoreProvider? provider)
    {
        _provider = provider;
        _description = provider is null ? "archivos JSON" : "proveedor de base de datos";
    }

    /// <summary>
    /// Creates the schema when the database store is in use.
    /// </summary>
    /// <returns><see langword="true"/> when the database store is ready; false to stay on JSON.</returns>
    public static bool Prepare()
    {
        var provider = _provider;
        if (provider is null)
        {
            return false;
        }

        try
        {
            if (!provider.EnsureSchema())
            {
                Drop("no se pudo preparar el esquema");
                return false;
            }

            _description = provider.DescribeBackend();
            JellyTrendLog.Info($"[Almacen] Datos persistentes en {_description}");

            // Copia de cortesia: si el proveedor desaparece, lo que ya estaba guardado queda en disco.
            StoreDump.Write();

            return true;
        }
        catch (Exception ex)
        {
            Drop(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Reads the trending list from the database store.
    /// </summary>
    /// <returns>The cached trending list, or null when the database store has nothing or is not in use.</returns>
    public static TrendingCache? ReadTrending()
    {
        var json = Guard(static provider => provider.GetTrendingJson(), null, "leer tendencias");
        return Deserialize<TrendingCache>(json);
    }

    /// <summary>
    /// Writes the trending list to the database store, when it is in use.
    /// </summary>
    /// <param name="cache">The trending cache to persist.</param>
    public static void WriteTrending(TrendingCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        var items = cache.Items ?? [];
        var ids = items.Select(static entry => entry.ItemId).ToArray();
        var entries = items.Select(static entry => JsonSerializer.Serialize(entry)).ToArray();
        var syncedAt = cache.LastUpdated == default ? DateTime.UtcNow : cache.LastUpdated;

        Guard(
            provider =>
            {
                provider.ReplaceTrending(ids, entries, syncedAt);
                return true;
            },
            false,
            "guardar tendencias");
    }

    /// <summary>
    /// Reads the trending list from wherever it lives: the database store when it is in use, the JSON
    /// file otherwise.
    /// </summary>
    /// <returns>The cached trending list, or null when there is nothing stored.</returns>
    public static TrendingCache? ReadTrendingCache()
    {
        var fromStore = ReadTrending();
        if (fromStore is not null)
        {
            fromStore.Normalize();
            return fromStore;
        }

        var path = JellyTrendStorage.TrendingFile;
        if (path.Length == 0 || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var cache = JsonSerializer.Deserialize<TrendingCache>(File.ReadAllText(path));
            cache?.Normalize();
            return cache;
        }
        catch (Exception ex)
        {
            JellyTrendLog.Warn($"[Almacen] Cache de tendencias ilegible: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes the trending list: to the database store when it is in use, to the JSON file otherwise.
    /// </summary>
    /// <param name="cache">The trending cache to persist.</param>
    public static void WriteTrendingCache(TrendingCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        DataStamp = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

        if (Active)
        {
            // Con el almacen externo el archivo sobra: los canales leen por ReadTrendingCache, que
            // consulta la base primero. Se retira la copia heredada para no dejar dos verdades.
            WriteTrending(cache);
            DeleteRetiredFile(JellyTrendStorage.TrendingFile, "tendencias");
            return;
        }

        var path = JellyTrendStorage.TrendingFile;
        if (path.Length == 0)
        {
            JellyTrendLog.Warn("[Almacen] Sin carpeta de datos: la cache de tendencias no se guardo.");
            return;
        }

        try
        {
            JellyTrendStorage.EnsureFolder();
            File.WriteAllText(path, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex)
        {
            JellyTrendLog.Warn($"[Almacen] No se pudo guardar la cache de tendencias: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the cached features of every item from the database store.
    /// </summary>
    /// <returns>Features keyed by item id; empty when the database store is not in use.</returns>
    public static IReadOnlyDictionary<Guid, string> ReadItemFeatures()
    {
        var json = Guard(static provider => provider.GetItemFeaturesJson(), null, "leer caracteristicas");
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<Guid, string>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var features = new Dictionary<Guid, string>();

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (Guid.TryParse(property.Name, out var id))
                {
                    features[id] = property.Value.GetRawText();
                }
            }

            return features;
        }
        catch (JsonException ex)
        {
            JellyTrendLog.Warn($"[Almacen] Caracteristicas ilegibles en el almacen externo: {ex.Message}");
            return new Dictionary<Guid, string>();
        }
    }

    /// <summary>
    /// Writes the cached features of the given items to the database store, when it is in use.
    /// </summary>
    /// <param name="items">Feature documents, keyed by item id.</param>
    /// <param name="hashes">Fingerprint per item id, used to skip unchanged rows.</param>
    public static void WriteItemFeatures(IReadOnlyDictionary<Guid, string> items, IReadOnlyDictionary<Guid, string> hashes)
    {
        ArgumentNullException.ThrowIfNull(items);

        var ids = new Guid[items.Count];
        var facets = new string[items.Count];
        var contentHashes = new string[items.Count];
        var index = 0;

        foreach (var pair in items)
        {
            ids[index] = pair.Key;
            facets[index] = pair.Value;
            contentHashes[index] = hashes is not null && hashes.TryGetValue(pair.Key, out var hash) ? hash : string.Empty;
            index++;
        }

        Guard(
            provider =>
            {
                provider.ReplaceItemFeatures(ids, facets, contentHashes);
                return true;
            },
            false,
            "guardar caracteristicas");

        // La copia que dejo la version anterior (o un servidor sin proveedor) ya no es la fuente.
        DeleteRetiredFile(JellyTrendStorage.FeaturesFile, "caracteristicas");
    }

    /// <summary>
    /// Reads the aggregated consumption of a user from the database store.
    /// </summary>
    /// <param name="userId">User to read.</param>
    /// <returns>Consumption documents keyed by item id; empty when the store is not in use.</returns>
    public static IReadOnlyDictionary<Guid, string> ReadConsumption(Guid userId)
    {
        var json = Guard(provider => provider.GetUserConsumption(userId), null, "leer consumo");
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<Guid, string>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var consumption = new Dictionary<Guid, string>();

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (Guid.TryParse(property.Name, out var id))
                {
                    consumption[id] = property.Value.GetRawText();
                }
            }

            return consumption;
        }
        catch (JsonException ex)
        {
            JellyTrendLog.Warn($"[Almacen] Consumo ilegible en el almacen externo: {ex.Message}");
            return new Dictionary<Guid, string>();
        }
    }

    /// <summary>
    /// Writes the aggregated consumption of a user, when the database store is in use.
    /// </summary>
    /// <param name="userId">User the consumption belongs to.</param>
    /// <param name="items">Consumption documents keyed by item id.</param>
    /// <returns>The number of rows written; 0 when the store is not in use.</returns>
    public static int WriteConsumption(Guid userId, IReadOnlyDictionary<Guid, string> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var ids = new Guid[items.Count];
        var documents = new string[items.Count];
        var index = 0;

        foreach (var pair in items)
        {
            ids[index] = pair.Key;
            documents[index] = pair.Value;
            index++;
        }

        return Guard(
            provider => provider.ReplaceConsumption(userId, ids, documents),
            0,
            "guardar consumo");
    }

    /// <summary>
    /// Writes the learned taste profile of a user, when the database store is in use.
    /// </summary>
    /// <param name="userId">User the profile belongs to.</param>
    /// <param name="affinities">Affinities to store.</param>
    /// <returns>The number of rows written; 0 when the store is not in use.</returns>
    public static int WriteAffinities(Guid userId, IReadOnlyList<AffinityRecord> affinities)
    {
        ArgumentNullException.ThrowIfNull(affinities);

        // Copia de cortesia: el perfil se guarda SIEMPRE en disco, tambien con el almacen externo activo.
        // Si el proveedor desaparece (se desinstala, se cambia a SQLite, falla), la capa 3 sigue teniendo
        // perfil que leer en lugar de empezar de cero.
        WriteProfileFile(userId, affinities);

        var facets = new string[affinities.Count];
        var values = new string[affinities.Count];
        var pairedFacets = new string[affinities.Count];
        var pairedValues = new string[affinities.Count];
        var weights = new double[affinities.Count];

        for (var i = 0; i < affinities.Count; i++)
        {
            var affinity = affinities[i];
            facets[i] = affinity.Facet;
            values[i] = affinity.Value;
            pairedFacets[i] = affinity.PairedFacet ?? string.Empty;
            pairedValues[i] = affinity.PairedValue ?? string.Empty;
            weights[i] = affinity.Weight;
        }

        return Guard(
            provider => provider.ReplaceAffinities(userId, facets, values, pairedFacets, pairedValues, weights),
            0,
            "guardar el perfil");
    }

    /// <summary>
    /// Reads the learned taste profile of a user from the database store.
    /// </summary>
    /// <param name="userId">User to read.</param>
    /// <returns>The stored affinities; empty when the store is not in use or has none.</returns>
    public static IReadOnlyList<AffinityRecord> ReadAffinities(Guid userId)
    {
        var json = Guard(provider => provider.GetAffinities(userId), null, "leer el perfil");
        var stored = ParseProfile(json);

        return stored.Count > 0 ? stored : ReadProfileFile(userId);
    }

    /// <summary>
    /// Reads the recommendations of a user from the database store.
    /// </summary>
    /// <param name="userId">User to read.</param>
    /// <returns>The recommended item ids; empty when the database store has nothing or is not in use.</returns>
    public static Guid[] ReadRecommendations(Guid userId)
        => Guard(provider => provider.GetUserRecommendations(userId), [], "leer recomendaciones");

    /// <summary>
    /// Writes the recommendations of a user to the database store, when it is in use.
    /// </summary>
    /// <param name="userId">User the recommendations belong to.</param>
    /// <param name="itemIds">Recommended item ids, best first.</param>
    public static void WriteRecommendations(Guid userId, IReadOnlyList<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var ids = itemIds.ToArray();
        var scores = new double[ids.Length];
        var generatedAt = DateTime.UtcNow;

        Guard(
            provider =>
            {
                provider.ReplaceUserRecommendations(userId, ids, scores, generatedAt, Guid.Empty);
                return true;
            },
            false,
            "guardar recomendaciones");

        // El canal cachea sus items por version: sin este sello, una escritura no obligaria a releer.
        RecommendationStamp = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Lists the users that have recommendations in the database store.
    /// </summary>
    /// <returns>User ids, newest first; empty when the database store is not in use.</returns>
    public static Guid[] ReadUsersWithRecommendations()
        => Guard(static provider => provider.GetUsersWithRecommendations(), [], "listar usuarios con recomendaciones");

    /// <summary>
    /// Reads the items a user must not be shown again from the database store.
    /// </summary>
    /// <param name="userId">User to read.</param>
    /// <returns>The suppressed item ids; empty when the database store is not in use.</returns>
    public static Guid[] ReadSuppressed(Guid userId)
        => Guard(provider => provider.GetSuppressed(userId), [], "leer elementos ocultos");

    /// <summary>
    /// Writes the items a user must not be shown again, when the database store is in use.
    /// </summary>
    /// <param name="userId">User the suppression belongs to.</param>
    /// <param name="itemIds">Suppressed item ids.</param>
    /// <param name="reason">Why they are hidden ("played", "in_progress").</param>
    public static void WriteSuppressed(Guid userId, IReadOnlyList<Guid> itemIds, string reason)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var ids = itemIds.ToArray();
        var reasons = Enumerable.Repeat(reason, ids.Length).ToArray();

        Guard(
            provider =>
            {
                provider.ReplaceSuppressed(userId, ids, reasons);
                return true;
            },
            false,
            "guardar elementos ocultos");
    }

    /// <summary>
    /// Records the start of a run in the database store, when it is in use.
    /// </summary>
    /// <param name="kind">Kind of run ("recommendations" or "trending").</param>
    /// <returns>The run id, or <see cref="Guid.Empty"/> when the store is not in use.</returns>
    public static Guid BeginRun(string kind)
        => Guard(provider => provider.StartRun(kind, DateTime.UtcNow), Guid.Empty, "registrar la corrida");

    /// <summary>
    /// Records the outcome of a run in the database store, when it is in use.
    /// </summary>
    /// <param name="runId">Run to close.</param>
    /// <param name="status">Outcome ("ok", "partial", "failed" or "canceled").</param>
    /// <param name="usersOk">Users that finished without errors.</param>
    /// <param name="usersFailed">Users that failed.</param>
    /// <param name="durationMs">Wall-clock duration in milliseconds.</param>
    /// <param name="message">Optional detail.</param>
    public static void CompleteRun(Guid runId, string status, int usersOk, int usersFailed, int durationMs, string? message)
    {
        if (runId == Guid.Empty)
        {
            return;
        }

        Guard(
            provider =>
            {
                provider.FinishRun(runId, status, usersOk, usersFailed, durationMs, message);
                return true;
            },
            false,
            "cerrar la corrida");
    }

    // Ruta del perfil en disco. Vive junto al resto de los datos del plugin y es la unica pieza que el
    // almacen no puede tener en exclusiva: es de donde la capa 3 lee cuando no hay proveedor.
    private static string ProfilePath(Guid userId)
        => JellyTrendStorage.Folder.Length == 0
            ? string.Empty
            : Path.Combine(JellyTrendStorage.Folder, $"perfil-{userId:N}.json");

    private static List<AffinityRecord> ParseProfile(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AffinityRecord>>(json) ?? [];
        }
        catch (JsonException ex)
        {
            JellyTrendLog.Warn($"[Almacen] Perfil ilegible en el almacen externo: {ex.Message}");
            return [];
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "El identificador es un Guid formateado 'N' (solo hexadecimal, sin separadores) y la ruta se compone con Path.Combine, asi que no puede salirse de la carpeta de datos. El analizador no puede verlo porque el Guid llega de una peticion HTTP.")]
    private static List<AffinityRecord> ReadProfileFile(Guid userId)
    {
        var path = ProfilePath(userId);
        if (path.Length == 0 || !File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AffinityRecord>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            JellyTrendLog.Warn($"[Almacen] Perfil de disco ilegible: {ex.Message}");
            return [];
        }
    }

    private static void WriteProfileFile(Guid userId, IReadOnlyList<AffinityRecord> affinities)
    {
        var path = ProfilePath(userId);
        if (path.Length == 0)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(affinities));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            JellyTrendLog.Warn($"[Almacen] No se pudo guardar el perfil en disco: {ex.Message}");
        }
    }

    private static void DeleteRetiredFile(string path, string name)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                JellyTrendLog.Info($"[Almacen] Copia JSON de {name} retirada: los datos viven en la base.");
            }
        }
        catch (Exception ex)
        {
            JellyTrendLog.Warn($"[Almacen] No se pudo retirar la copia JSON de {name}: {ex.Message}");
        }
    }

    private static T Guard<T>(Func<IJellyTrendStoreProvider, T> action, T fallback, string operation)
    {
        var provider = _provider;
        if (provider is null)
        {
            return fallback;
        }

        try
        {
            return action(provider);
        }
        catch (Exception ex)
        {
            Drop($"{operation}: {ex.Message}");
            return fallback;
        }
    }

    private static void Drop(string reason)
    {
        if (_provider is not null)
        {
            JellyTrendLog.Warn(string.Format(
                CultureInfo.InvariantCulture,
                "[Almacen] Se descarta el almacen externo ({0}); se siguen usando los archivos JSON.",
                reason));
        }

        _provider = null;
        _description = "archivos JSON";
    }

    private static T? Deserialize<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException ex)
        {
            JellyTrendLog.Warn($"[Almacen] Documento ilegible en el almacen externo: {ex.Message}");
            return null;
        }
    }
}
