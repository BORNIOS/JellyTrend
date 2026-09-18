using System;

using Jellyfin.Plugin.JellyTrend.Api;
using Jellyfin.Plugin.JellyTrend.Services.Recommendation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.JellyTrend.Services.Backend;

/// <summary>
/// Knows which external database backend is available and whether it actually answers.
/// </summary>
/// <remarks>
/// <para>
/// The PostgreSQL Database Provider plugin registers its query backends in the Jellyfin container, but it
/// does so in the name of this plugin: nothing is usable until JellyTrend asks for it. Detection therefore
/// happens once at server start (see <see cref="DatabaseBackendStartupService"/>) and the result lives here
/// as a singleton, so the recommendation task and the trending sync both talk to whatever was verified
/// instead of each one guessing.
/// </para>
/// <para>
/// Being registered is not the same as working: a provider whose queries return nothing is indistinguishable
/// from a missing one until it is exercised, so the state distinguishes "present" from "verified" and every
/// consumer only uses a backend that answered.
/// </para>
/// </remarks>
public sealed class DatabaseBackend
{
    private const string NotInstalled = "no instalado";

    private string _recommendationDetail = NotInstalled;
    private string _indexDetail = NotInstalled;

    /// <summary>Gets the recommendation backend offered by another plugin, when it is installed.</summary>
    public IRecommendationQueryProvider? RecommendationProvider { get; private set; }

    /// <summary>Gets the library index backend offered by another plugin, when it is installed.</summary>
    public ILibraryIndexQueryProvider? LibraryIndex { get; private set; }

    /// <summary>Gets a value indicating whether the recommendation backend may be used.</summary>
    public bool RecommendationUsable { get; private set; }

    /// <summary>Gets a value indicating whether the library index may be used.</summary>
    public bool IndexUsable { get; private set; }

    /// <summary>Gets the library index, but only when it may be used.</summary>
    public ILibraryIndexQueryProvider? UsableLibraryIndex => IndexUsable ? LibraryIndex : null;

    /// <summary>Gets one line describing the detected backend and its verification state.</summary>
    public string Summary
        => $"{Describe()} | indice de biblioteca: {_indexDetail}";

    private string Describe()
        => RecommendationProvider is null
            ? $"sin proveedor de base de datos ({_recommendationDetail}); se usa ILibraryManager"
            : $"{Describe(RecommendationProvider)}: {_recommendationDetail}";

    private static string Describe(IRecommendationQueryProvider provider)
    {
        if (!typeof(ForeignForwardingProxy).IsAssignableFrom(provider.GetType()))
        {
            return provider.GetType().Name;
        }

        return ((ForeignForwardingProxy)(object)provider).ForeignName;
    }

    /// <summary>
    /// Resolves the backends: first from the Jellyfin service container, and otherwise by looking for them
    /// among the plugins already loaded.
    /// </summary>
    /// <param name="services">The server service provider.</param>
    /// <param name="loggerFactory">Logger factory handed to a provider found by search.</param>
    public void Detect(IServiceProvider services, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        loggerFactory ??= NullLoggerFactory.Instance;

        RecommendationProvider = Detect<IRecommendationQueryProvider>(
            services,
            loggerFactory,
            out var recommendationDetail);
        _recommendationDetail = recommendationDetail;

        LibraryIndex = Detect<ILibraryIndexQueryProvider>(
            services,
            loggerFactory,
            out var indexDetail);
        _indexDetail = indexDetail;

        // Presente no significa utilizable: queda "comprobando" hasta que la sonda real confirme que
        // devuelve datos (ver DatabaseBackendStartupService).
        RecommendationUsable = RecommendationProvider is not null;
        IndexUsable = LibraryIndex is not null;
    }

    private static TProvider? Detect<TProvider>(
        IServiceProvider services,
        ILoggerFactory loggerFactory,
        out string detail)
        where TProvider : class
    {
        var registered = TryResolve<TProvider>(services, out var containerError);
        if (registered is not null)
        {
            detail = "registrado en el contenedor de Jellyfin; comprobando";
            return registered;
        }

        var found = ForeignProviderDiscovery.Find<TProvider>(loggerFactory, out var search);
        detail = found is null
            ? $"no disponible: {containerError ?? search}"
            : $"{search}; comprobando";

        return found;
    }

    /// <summary>
    /// Marks the recommendation backend as answering, with the measured sample size.
    /// </summary>
    /// <param name="sampleRows">Rows actually returned by the verification query.</param>
    public void VerifyRecommendations(int sampleRows)
    {
        RecommendationUsable = true;
        _recommendationDetail = $"verificado ({sampleRows} candidatos de muestra)";
    }

    /// <summary>
    /// Marks the recommendation backend as unusable for the rest of the server session.
    /// </summary>
    /// <param name="reason">Why it cannot be used.</param>
    public void RejectRecommendations(string reason)
    {
        RecommendationUsable = false;
        _recommendationDetail = reason;
    }

    /// <summary>
    /// Records that the recommendation backend could not be verified either way.
    /// </summary>
    /// <param name="reason">Why the verification was not conclusive.</param>
    public void InconclusiveRecommendations(string reason) => _recommendationDetail = $"sin comprobar ({reason})";

    /// <summary>
    /// Marks the library index as answering, with the measured sample size.
    /// </summary>
    /// <param name="resolvedIds">Ids actually resolved by the verification query.</param>
    public void VerifyIndex(int resolvedIds)
    {
        IndexUsable = true;
        _indexDetail = $"verificado ({resolvedIds} ids de muestra)";
    }

    /// <summary>
    /// Marks the library index as unusable for the rest of the server session.
    /// </summary>
    /// <param name="reason">Why it cannot be used.</param>
    public void RejectIndex(string reason)
    {
        IndexUsable = false;
        _indexDetail = reason;
    }

    /// <summary>
    /// Records that the library index could not be verified either way.
    /// </summary>
    /// <param name="reason">Why the verification was not conclusive.</param>
    public void InconclusiveIndex(string reason) => _indexDetail = $"sin comprobar ({reason})";

    /// <summary>
    /// Creates the state shared by one recommendation run, so a failure is discarded once per run
    /// instead of once per user.
    /// </summary>
    /// <returns>The run state, carrying the provider only when it may be used.</returns>
    internal ProviderState CreateRunState()
        => new(
            RecommendationUsable ? RecommendationProvider : null,
            () => RejectRecommendations("descartado durante una ejecucion (no devolvio candidatos o fallo)"),
            RecommendationProvider is null ? null : _recommendationDetail);

    private static T? TryResolve<T>(IServiceProvider services, out string? error)
        where T : class
    {
        try
        {
            error = null;
            return services.GetService<T>();
        }
        catch (Exception ex)
        {
            // Un proveedor de una version anterior puede no satisfacer esta interfaz: no es un fallo del
            // plugin, es simplemente "no hay backend", y no debe tumbar la tarea.
            error = $"no se pudo cargar ({ex.GetType().Name})";
            return null;
        }
    }
}
