using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend.Api;
using Jellyfin.Plugin.JellyTrend.Services.Backend;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// El backend de base de datos se detecta una vez al arrancar. Registrarse no basta: un proveedor que no
/// contesta se ve igual que uno que funciona hasta que se le pregunta, así que el estado distingue
/// "presente" de "verificado" y lo que se descarta no se vuelve a ofrecer.
/// </summary>
public class DatabaseBackendTests
{
    [Fact]
    public void UnContratoQueNadieOfreceQuedaComoNoDisponible()
    {
        var backend = new DatabaseBackend();

        backend.Detect(new FakeServices());

        // El indice de biblioteca no lo ofrece nadie todavia: no hay nada que ofrecer ni que comprobar.
        Assert.False(backend.IndexUsable);
        Assert.Null(backend.UsableLibraryIndex);
        Assert.DoesNotContain("indice de biblioteca", backend.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ElProveedorPresenteSeUsaAunqueLaComprobacionSigaEnCurso()
    {
        var provider = new EmptyProvider();
        var backend = new DatabaseBackend();

        backend.Detect(new FakeServices(provider));

        Assert.Same(provider, backend.CreateRunState().Provider);
        Assert.Contains("comprobando", backend.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void LaComprobacionRegistraLaMuestraMedida()
    {
        var backend = new DatabaseBackend();
        backend.Detect(new FakeServices(new EmptyProvider()));

        backend.VerifyRecommendations(5);

        Assert.True(backend.RecommendationUsable);
        Assert.Contains("verificado, 5 candidatos", backend.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UnProveedorDescartadoEnUnaEjecucionNoVuelveALasSiguientes()
    {
        var backend = new DatabaseBackend();
        backend.Detect(new FakeServices(new EmptyProvider()));

        var run = backend.CreateRunState();
        run.Reject();

        Assert.False(backend.RecommendationUsable);
        Assert.Null(backend.CreateRunState().Provider);
        Assert.Contains("descartado: fallo o respuesta vacia durante una ejecucion", backend.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ElIndiceSoloSeOfreceSiSuComprobacionPaso()
    {
        var index = new EmptyIndex();
        var backend = new DatabaseBackend();
        backend.Detect(new FakeServices(index));

        Assert.Same(index, backend.UsableLibraryIndex);

        backend.RejectIndex("no resolvio un id TMDB que si esta en la biblioteca");

        Assert.Null(backend.UsableLibraryIndex);
    }

    /// <summary>
    /// El caso que motivo todo esto: el proveedor esta registrado pero su consulta no ve las peliculas de
    /// la biblioteca. Sin la sonda, el unico sintoma era una fila vacia; con ella, el backend queda
    /// descartado y el log dice por que.
    /// </summary>
    [Fact]
    public void UnProveedorQueNoVeLasPeliculasQuedaDescartadoConSuMotivo()
    {
        var backend = new DatabaseBackend();
        backend.Detect(new FakeServices(new EmptyProvider()));

        Probe(backend, new EmptyProvider(), hasUnwatchedMovies: true);

        Assert.False(backend.RecommendationUsable);
        Assert.Null(backend.CreateRunState().Provider);
        Assert.Contains("descartado: devolvio 0 candidatos", backend.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UnProveedorQueDevuelveCandidatosQuedaVerificado()
    {
        var backend = new DatabaseBackend();
        var provider = new SampleProvider(3);
        backend.Detect(new FakeServices(provider));

        Probe(backend, provider, hasUnwatchedMovies: true);

        Assert.True(backend.RecommendationUsable);
        Assert.Contains("verificado, 3 candidatos", backend.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SinPeliculasSinVerLaComprobacionNoEsConcluyenteYElProveedorSigueDisponible()
    {
        var backend = new DatabaseBackend();
        var provider = new EmptyProvider();
        backend.Detect(new FakeServices(provider));

        Probe(backend, provider, hasUnwatchedMovies: false);

        Assert.True(backend.RecommendationUsable);
        Assert.Contains("sin comprobar: la biblioteca no tiene peliculas sin ver", backend.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UnRegistroIncompatibleEnElContenedorNoRompeLaDeteccion()
    {
        // Caso real: el plugin de PostgreSQL registra su copia del contrato contra nuestro tipo, asi que
        // resolverlo lanza InvalidCastException. Eso no debe tumbar el arranque ni la busqueda.
        var backend = new DatabaseBackend();

        var error = Record.Exception(() => backend.Detect(new ThrowingServices(), NullLoggerFactory.Instance));

        Assert.Null(error);
        Assert.False(backend.IndexUsable);
    }

    private static void Probe(DatabaseBackend backend, IRecommendationQueryProvider provider, bool hasUnwatchedMovies)
    {
        var users = DispatchProxy.Create<IUserManager, Users>();
        var library = DispatchProxy.Create<ILibraryManager, MovieLibrary>();
        ((MovieLibrary)(object)library).HasUnwatchedMovies = hasUnwatchedMovies;

        new DatabaseBackendStartupService(
                backend,
                new FakeServices(provider),
                users,
                library,
                NullLoggerFactory.Instance)
            .Probe();
    }

    /// <summary>Proveedor registrado que no contesta nada: sirve para probar el ciclo de estado.</summary>
    private sealed class EmptyProvider : IRecommendationQueryProvider
    {
        public IReadOnlyList<RecommendationItem> GetPlayedMovies(Guid userId, int limit) => [];

        public IReadOnlyList<RecommendationItem> GetResumableMovies(Guid userId, int limit) => [];

        public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByGenres(Guid userId, IReadOnlyList<string> genres, IReadOnlyList<Guid> topParentIds, int limit) => [];

        public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByPersons(Guid userId, IReadOnlyList<Guid> personIds, IReadOnlyList<Guid> topParentIds, int limit) => [];

        public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByTags(Guid userId, IReadOnlyList<string> tags, IReadOnlyList<Guid> topParentIds, int limit) => [];

        public IReadOnlyList<RecommendationItem> GetRandomUnwatchedMovies(Guid userId, IReadOnlyList<Guid> topParentIds, int limit) => [];
    }

    /// <summary>Indice de biblioteca registrado, sin respuesta real.</summary>
    private sealed class EmptyIndex : ILibraryIndexQueryProvider
    {
        public IReadOnlyDictionary<string, Guid> GetItemIdsByTmdbIds(IReadOnlyList<string> tmdbIds, BaseItemKind kind)
            => new Dictionary<string, Guid>(StringComparer.Ordinal);
    }

    /// <summary>Contenedor mínimo: devuelve el primer servicio del tipo pedido.</summary>
    private sealed class FakeServices : IServiceProvider
    {
        private readonly List<object> _services;

        public FakeServices(params object[] services) => _services = [.. services];

        public object? GetService(Type serviceType)
            => _services.FirstOrDefault(service => serviceType.IsInstanceOfType(service));
    }

    /// <summary>Proveedor que devuelve la muestra pedida, para la comprobación positiva.</summary>
    private sealed class SampleProvider : IRecommendationQueryProvider
    {
        private readonly int _count;

        public SampleProvider(int count) => _count = count;

        public IReadOnlyList<RecommendationItem> GetPlayedMovies(Guid userId, int limit) => [];

        public IReadOnlyList<RecommendationItem> GetResumableMovies(Guid userId, int limit) => [];

        public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByGenres(Guid userId, IReadOnlyList<string> genres, IReadOnlyList<Guid> topParentIds, int limit) => [];

        public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByPersons(Guid userId, IReadOnlyList<Guid> personIds, IReadOnlyList<Guid> topParentIds, int limit) => [];

        public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByTags(Guid userId, IReadOnlyList<string> tags, IReadOnlyList<Guid> topParentIds, int limit) => [];

        public IReadOnlyList<RecommendationItem> GetRandomUnwatchedMovies(Guid userId, IReadOnlyList<Guid> topParentIds, int limit)
            => Enumerable.Range(0, Math.Min(_count, limit))
                .Select(index => new RecommendationItem(Guid.NewGuid(), null, [], [], [], null, null))
                .ToList();
    }

    /// <summary>Contenedor vacío: reproduce un Jellyfin donde nadie registró nada.</summary>
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Contenedor que falla al resolver, como cuando el otro plugin registró algo incompatible.</summary>
    private sealed class ThrowingServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => throw new InvalidOperationException("registro incompatible");
    }

    /// <summary>Usuarios falsos: la comprobación necesita uno con el que preguntar.</summary>
    // No puede ser sealed: DispatchProxy genera una subclase en tiempo de ejecución.
    private class Users : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(IUserManager.GetUsers)
                ? (IReadOnlyList<User>)new List<User> { new("tester", "Default", "Default") }
                : null;
    }

    /// <summary>Biblioteca falsa: solo responde si hay películas sin ver.</summary>
    private class MovieLibrary : DispatchProxy
    {
        public bool HasUnwatchedMovies { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(ILibraryManager.GetItemList)
                ? HasUnwatchedMovies
                    ? new List<BaseItem> { new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid() } }
                    : new List<BaseItem>()
                : null;
    }
}
