using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Plugin.JellyTrend.Api;
using Jellyfin.Plugin.JellyTrend.Logging;

namespace Jellyfin.Plugin.JellyTrend.Services.Store;

/// <summary>
/// Stores JellyTrend-owned data through the PostgreSQL provider's isolated schema host.
/// </summary>
/// <remarks>
/// The provider assembly remains optional and is deliberately not referenced at compile time. The host
/// instance and its public contract are discovered from the running Jellyfin plugin container, while all
/// database values remain parameterized and confined to JellyTrend's registered schema.
/// </remarks>
internal sealed class PostgresSchemaStore : IJellyTrendStoreProvider
{
    private const int CurrentSchemaVersion = 5;
    private const string SchemaName = "jellytrend_d3b07384d9a14e2b8c3f1234567890ab";

    private readonly object _host;
    private readonly Type _hostContract;
    private readonly Type _requestType;
    private readonly Type _migrationType;
    private readonly Type _parameterType;
    private object? _schema;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresSchemaStore"/> class.
    /// </summary>
    /// <param name="host">Foreign PostgreSQL schema-host instance.</param>
    /// <param name="contract">Foreign schema-host contract type.</param>
    public PostgresSchemaStore(object host, Type contract)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _hostContract = contract ?? throw new ArgumentNullException(nameof(contract));
        var assembly = contract.Assembly;
        _requestType = RequireType(assembly, "Jellyfin.Database.Providers.Postgres.Api.PluginSchemaRequest");
        _migrationType = RequireType(assembly, "Jellyfin.Database.Providers.Postgres.Api.PluginSchemaMigration");
        _parameterType = RequireType(assembly, "Jellyfin.Database.Providers.Postgres.Api.PluginSqlParameter");
    }

    /// <inheritdoc />
    public bool EnsureSchema()
    {
        try
        {
            var request = Activator.CreateInstance(_requestType, Plugin.Instance!.Id, SchemaName, false)!;
            _schema = OpenOrMigrate(request);
            ApplyMigrations();
            Execute("INSERT INTO schema_version (version) VALUES (@version) ON CONFLICT (version) DO NOTHING", Param("version", CurrentSchemaVersion));
            JellyTrendLog.Info($"[Almacen] PostgreSQL disponible mediante PG Provider; esquema privado {SchemaName} listo.");
            return true;
        }
        catch (Exception ex)
        {
            JellyTrendLog.Warn($"[Almacen] No se pudo preparar el esquema privado de PostgreSQL: {ex.GetBaseException().Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public int GetSchemaVersion() => Query("SELECT coalesce(max(version), 0) FROM schema_version", static reader => reader.Read() ? reader.GetInt32(0) : 0);

    /// <inheritdoc />
    public string DescribeBackend() => $"PostgreSQL mediante PG Provider, esquema {SchemaName}";

    /// <inheritdoc />
    public int ReplaceItemFeatures(Guid[] itemIds, string[] facetsJson, string[] contentHashes)
        => Execute(
            "INSERT INTO item_features (item_id, facets, content_hash, computed_at) SELECT item_id, facets, content_hash, now() FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(item_id uuid, facets jsonb, content_hash text) ON CONFLICT (item_id) DO UPDATE SET facets = EXCLUDED.facets, content_hash = EXCLUDED.content_hash, computed_at = now()",
            Param("rows", JsonSerializer.Serialize(Enumerable.Range(0, itemIds.Length).Where(index => itemIds[index] != Guid.Empty).Select(index => new { item_id = itemIds[index], facets = ReadJson(facetsJson, index), content_hash = ReadText(contentHashes, index) }))));

    /// <inheritdoc />
    public string?[] GetItemFeatures(Guid[] itemIds)
    {
        var found = Query(
            "SELECT item_id::text, facets::text FROM item_features WHERE item_id IN (SELECT value::uuid FROM jsonb_array_elements_text(CAST(@ids AS jsonb)) AS value)",
            static reader => ReadStringMap(reader),
            Param("ids", JsonSerializer.Serialize(itemIds)));
        return itemIds.Select(id => found.TryGetValue(id.ToString(), out var value) ? value : null).ToArray();
    }

    /// <inheritdoc />
    public string? GetItemFeaturesJson() => Query("SELECT jsonb_object_agg(item_id::text, facets)::text FROM item_features", static reader => ReadNullableString(reader));

    /// <inheritdoc />
    public void ReplaceTrending(Guid[] itemIds, string[] entriesJson, DateTime syncedAt)
    {
        Execute(
            "WITH raw_input AS (SELECT item_id, entry, media_type FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(item_id uuid, entry jsonb, media_type text)), input AS (SELECT DISTINCT ON (item_id) item_id, entry, media_type FROM raw_input ORDER BY item_id), upsert AS (INSERT INTO trending_item (item_id, entry, media_type, synced_at) SELECT item_id, entry, media_type, @synced FROM input ON CONFLICT (item_id) DO UPDATE SET entry = EXCLUDED.entry, media_type = EXCLUDED.media_type, synced_at = EXCLUDED.synced_at RETURNING item_id) DELETE FROM trending_item WHERE item_id NOT IN (SELECT item_id FROM input)",
            Param("synced", DateTime.SpecifyKind(syncedAt, DateTimeKind.Utc)),
            Param("rows", JsonSerializer.Serialize(Enumerable.Range(0, itemIds.Length).Where(index => itemIds[index] != Guid.Empty).Select(index => new { item_id = itemIds[index], entry = ReadJson(entriesJson, index), media_type = ReadMediaType(entriesJson, index) }))));
    }

    /// <inheritdoc />
    public string? GetTrendingJson() => Query("SELECT jsonb_build_object('Items', coalesce(jsonb_agg(entry ORDER BY item_id), '[]'::jsonb), 'LastUpdated', max(synced_at))::text FROM trending_item HAVING count(*) > 0", static reader => ReadNullableString(reader));

    /// <inheritdoc />
    public void ReplaceUserRecommendations(Guid userId, Guid[] itemIds, double[] scores, DateTime generatedAt, Guid runId)
    {
        Execute("DELETE FROM user_recommendation WHERE user_id = @user", Param("user", userId));
        Execute(
            "INSERT INTO user_recommendation (user_id, item_id, rank, score, run_id, generated_at) SELECT @user, item_id, rank, score, @run, @generated FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(item_id uuid, rank integer, score double precision)",
            Param("user", userId),
            Param("run", runId),
            Param("generated", DateTime.SpecifyKind(generatedAt, DateTimeKind.Utc)),
            Param("rows", JsonSerializer.Serialize(Enumerable.Range(0, itemIds.Length).Where(index => itemIds[index] != Guid.Empty).Select(index => new { item_id = itemIds[index], rank = index, score = index < scores.Length ? scores[index] : 0d }))));
    }

    /// <inheritdoc />
    public Guid[] GetUserRecommendations(Guid userId) => ReadGuidsInstance("SELECT item_id FROM user_recommendation WHERE user_id = @user ORDER BY rank", Param("user", userId));

    /// <inheritdoc />
    public Guid[] GetUsersWithRecommendations() => ReadGuidsInstance("SELECT user_id FROM user_recommendation GROUP BY user_id ORDER BY max(generated_at) DESC");

    /// <inheritdoc />
    public void ReplaceSuppressed(Guid userId, Guid[] itemIds, string[] reasons)
    {
        Execute("DELETE FROM user_suppression WHERE user_id = @user", Param("user", userId));
        Execute(
            "INSERT INTO user_suppression (user_id, item_id, reason) SELECT @user, item_id, reason FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(item_id uuid, reason text)",
            Param("user", userId),
            Param("rows", JsonSerializer.Serialize(Enumerable.Range(0, itemIds.Length).Where(index => itemIds[index] != Guid.Empty).Select(index => new { item_id = itemIds[index], reason = ReadText(reasons, index, "played") }))));
    }

    /// <inheritdoc />
    public Guid[] GetSuppressed(Guid userId) => ReadGuidsInstance("SELECT item_id FROM user_suppression WHERE user_id = @user", Param("user", userId));

    /// <inheritdoc />
    public int ReplaceConsumption(Guid userId, Guid[] itemIds, string[] consumptionJson)
        => Execute(
            "INSERT INTO user_item_consumption (user_id, item_id, data, updated_at) SELECT @user, item_id, data, now() FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(item_id uuid, data jsonb) ON CONFLICT (user_id, item_id) DO UPDATE SET data = EXCLUDED.data, updated_at = now()",
            Param("user", userId),
            Param("rows", JsonSerializer.Serialize(Enumerable.Range(0, itemIds.Length).Where(index => itemIds[index] != Guid.Empty).Select(index => new { item_id = itemIds[index], data = ReadJson(consumptionJson, index) }))));

    /// <inheritdoc />
    public string? GetUserConsumption(Guid userId) => Query("SELECT jsonb_object_agg(item_id::text, data)::text FROM user_item_consumption WHERE user_id = @user", static reader => ReadNullableString(reader), Param("user", userId));

    /// <inheritdoc />
    public int ReplaceAffinities(Guid userId, string[] facets, string[] values, string[] pairedFacets, string[] pairedValues, double[] weights)
    {
        Execute("DELETE FROM user_affinity WHERE user_id = @user", Param("user", userId));
        Execute("DELETE FROM user_affinity_pair WHERE user_id = @user", Param("user", userId));
        var entries = Enumerable.Range(0, facets.Length).Where(index => !string.IsNullOrWhiteSpace(facets[index]) && !string.IsNullOrWhiteSpace(ReadText(values, index))).Select(index => new { facet = facets[index], value = ReadText(values, index), paired_facet = ReadText(pairedFacets, index), paired_value = ReadText(pairedValues, index), weight = index < weights.Length ? weights[index] : 0d }).ToArray();
        var single = entries.Where(entry => string.IsNullOrWhiteSpace(entry.paired_facet)).ToArray();
        var paired = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.paired_facet)).ToArray();
        var count = Execute("INSERT INTO user_affinity (user_id, facet, value, weight, updated_at) SELECT @user, facet, value, weight::real, now() FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(facet text, value text, weight double precision)", Param("user", userId), Param("rows", JsonSerializer.Serialize(single)));
        return count + Execute("INSERT INTO user_affinity_pair (user_id, facet, value, paired_facet, paired_value, weight, updated_at) SELECT @user, facet, value, paired_facet, paired_value, weight::real, now() FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(facet text, value text, paired_facet text, paired_value text, weight double precision)", Param("user", userId), Param("rows", JsonSerializer.Serialize(paired)));
    }

    /// <inheritdoc />
    public string? GetAffinities(Guid userId) => Query("SELECT coalesce(jsonb_agg(entry), '[]'::jsonb)::text FROM (SELECT jsonb_build_object('Facet', facet, 'Value', value, 'Weight', weight) AS entry FROM user_affinity WHERE user_id = @user UNION ALL SELECT jsonb_build_object('Facet', facet, 'Value', value, 'PairedFacet', paired_facet, 'PairedValue', paired_value, 'Weight', weight) FROM user_affinity_pair WHERE user_id = @user) parts", static reader => ReadNullableString(reader), Param("user", userId));

    /// <inheritdoc />
    public Guid StartRun(string kind, DateTime startedAt)
    {
        var id = Guid.NewGuid();
        Execute("INSERT INTO sync_run (run_id, kind, started_at, status) VALUES (@id, @kind, @started, 'running')", Param("id", id), Param("kind", kind), Param("started", DateTime.SpecifyKind(startedAt, DateTimeKind.Utc)));
        return id;
    }

    /// <inheritdoc />
    public void FinishRun(Guid runId, string status, int usersOk, int usersFailed, int durationMs, string? message)
        => Execute("UPDATE sync_run SET finished_at = now(), status = @status, users_ok = @ok, users_failed = @failed, duration_ms = @duration, message = @message WHERE run_id = @id", Param("id", runId), Param("status", status), Param("ok", usersOk), Param("failed", usersFailed), Param("duration", durationMs), Param("message", message));

    private object OpenOrMigrate(object request)
    {
        try
        {
            return InvokeTask("MigratePreRegistrySchemaAsync", request, CancellationToken.None)!;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist", StringComparison.Ordinal) || ex.Message.Contains("already exists", StringComparison.Ordinal))
        {
            return InvokeTask("OpenSchemaAsync", request, CancellationToken.None)!;
        }
    }

    private void ApplyMigrations()
    {
        var migrations = new[]
        {
            "CREATE TABLE IF NOT EXISTS schema_version (version integer PRIMARY KEY, applied_at timestamp with time zone NOT NULL DEFAULT now())",
            "CREATE TABLE IF NOT EXISTS item_features (item_id uuid PRIMARY KEY, facets jsonb NOT NULL, content_hash text NOT NULL, computed_at timestamp with time zone NOT NULL DEFAULT now())",
            "CREATE INDEX IF NOT EXISTS ix_jt_item_features_facets ON item_features USING gin (facets)",
            "CREATE TABLE IF NOT EXISTS trending_item (item_id uuid PRIMARY KEY, entry jsonb NOT NULL, media_type text, synced_at timestamp with time zone NOT NULL)",
            "CREATE TABLE IF NOT EXISTS user_recommendation (user_id uuid NOT NULL, item_id uuid NOT NULL, rank integer NOT NULL, score double precision NOT NULL DEFAULT 0, run_id uuid NOT NULL, generated_at timestamp with time zone NOT NULL, PRIMARY KEY (user_id, item_id))",
            "CREATE INDEX IF NOT EXISTS ix_jt_user_recommendation_rank ON user_recommendation (user_id, rank)",
            "CREATE TABLE IF NOT EXISTS user_suppression (user_id uuid NOT NULL, item_id uuid NOT NULL, reason text NOT NULL, PRIMARY KEY (user_id, item_id))",
            "CREATE TABLE IF NOT EXISTS sync_run (run_id uuid PRIMARY KEY, kind text NOT NULL, started_at timestamp with time zone NOT NULL, finished_at timestamp with time zone, status text, users_ok integer NOT NULL DEFAULT 0, users_failed integer NOT NULL DEFAULT 0, duration_ms integer, message text)",
            "CREATE TABLE IF NOT EXISTS user_item_consumption (user_id uuid NOT NULL, item_id uuid NOT NULL, data jsonb NOT NULL, updated_at timestamp with time zone NOT NULL DEFAULT now(), PRIMARY KEY (user_id, item_id))",
            "CREATE TABLE IF NOT EXISTS user_affinity (user_id uuid NOT NULL, facet text NOT NULL, value text NOT NULL, weight real NOT NULL, updated_at timestamp with time zone NOT NULL DEFAULT now(), PRIMARY KEY (user_id, facet, value))",
            "CREATE TABLE IF NOT EXISTS user_affinity_pair (user_id uuid NOT NULL, facet text NOT NULL, value text NOT NULL, paired_facet text NOT NULL, paired_value text NOT NULL, weight real NOT NULL, updated_at timestamp with time zone NOT NULL DEFAULT now(), PRIMARY KEY (user_id, facet, value, paired_facet, paired_value))"
        };
        var array = Array.CreateInstance(_migrationType, migrations.Length);
        for (var index = 0; index < migrations.Length; index++)
        {
            array.SetValue(Activator.CreateInstance(_migrationType, $"jellytrend-v{CurrentSchemaVersion:D2}-{index:D2}", migrations[index]), index);
        }

        InvokeTask("ApplyMigrationsAsync", _schema!, array, CancellationToken.None);
        Execute("UPDATE sync_run SET finished_at = now(), status = 'interrupted', message = coalesce(message, 'Interrupted before the previous Jellyfin process stopped') WHERE status = 'running' AND finished_at IS NULL");
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
        => (int)(InvokeTask("ExecuteAsync", _schema!, sql, BuildParameters(parameters), CancellationToken.None) ?? 0);

    private T Query<T>(string sql, Func<DbDataReader, T> project, params (string Name, object? Value)[] parameters)
    {
        var method = _hostContract.GetMethods().Single(candidate => candidate.Name == "QueryAsync" && candidate.IsGenericMethodDefinition);
        var task = (Task)method.MakeGenericMethod(typeof(T)).Invoke(_host, [_schema!, sql, project, BuildParameters(parameters), CancellationToken.None])!;
        task.GetAwaiter().GetResult();
        return (T)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private object? InvokeTask(string name, params object?[] arguments)
    {
        var method = _hostContract.GetMethods().Single(candidate =>
        {
            var parameters = candidate.GetParameters();
            return candidate.Name == name
                && parameters.Length == arguments.Length
                && parameters.Select(static parameter => parameter.ParameterType)
                    .Zip(arguments, static (parameterType, argument) => argument is null ? !parameterType.IsValueType : parameterType.IsInstanceOfType(argument))
                    .All(static compatible => compatible);
        });
        var task = (Task)method.Invoke(_host, arguments)!;
        task.GetAwaiter().GetResult();
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private Array BuildParameters(IEnumerable<(string Name, object? Value)> parameters)
    {
        var values = parameters.ToArray();
        var array = Array.CreateInstance(_parameterType, values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            array.SetValue(Activator.CreateInstance(_parameterType, values[index].Name, values[index].Value), index);
        }

        return array;
    }

    private static (string Name, object? Value) Param(string name, object? value) => (name, value);

    private static Type RequireType(Assembly assembly, string name) => assembly.GetType(name, throwOnError: true)!;

    private Guid[] ReadGuidsInstance(string sql, params (string Name, object? Value)[] parameters)
        => Query(
            sql,
            static reader =>
            {
                var values = new List<Guid>();
                while (reader.Read())
                {
                    values.Add(reader.GetGuid(0));
                }

                return values.ToArray();
            },
            parameters);

    private static Dictionary<string, string> ReadStringMap(DbDataReader reader)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    private static string? ReadNullableString(DbDataReader reader) => reader.Read() && !reader.IsDBNull(0) ? reader.GetString(0) : null;

    private static string ReadJson(string[] values, int index) => ReadText(values, index, "{}");

    private static string ReadText(string[] values, int index, string fallback = "") => index < values.Length ? values[index] ?? fallback : fallback;

    private static string? ReadMediaType(string[] entriesJson, int index)
    {
        if (index >= entriesJson.Length)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(entriesJson[index]);
            return document.RootElement.TryGetProperty("MediaType", out var mediaType) ? mediaType.ToString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
