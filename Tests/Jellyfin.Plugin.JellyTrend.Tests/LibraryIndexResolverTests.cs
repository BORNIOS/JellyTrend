using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTrend.Api;
using Jellyfin.Plugin.JellyTrend.Services.Backend;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// El emparejamiento de tendencias cuesta hoy una consulta por id de TMDB. El índice del proveedor de
/// base de datos lo baja a una consulta por tipo, pero solo debe usarse cuando contesta: sin índice, o
/// con un índice que falla o devuelve basura, la lista de tendencias debe salir igual.
/// </summary>
public class LibraryIndexResolverTests
{
    [Fact]
    public void ConIndiceElEmparejamientoNoConsultaLaBibliotecaItemPorItem()
    {
        var movie = Movie(new Guid(1, 0, 0, new byte[8]), "55");
        var (library, fake) = Library(movie);
        var index = new IndexStub(new Dictionary<string, Guid>(StringComparer.Ordinal) { ["55"] = movie.Id });

        var matches = new LibraryIndexResolver(library, index, NullLogger.Instance)
            .Resolve(new List<string> { "55" }, BaseItemKind.Movie);

        Assert.Equal(movie.Id, matches["55"].Id);
        Assert.Equal(0, fake.ListQueries);
        Assert.Equal(1, index.Calls);
        Assert.Equal(["55"], index.Requested);
    }

    [Fact]
    public void SinIndiceSeConsultaUnaVezPorId()
    {
        var movie = Movie(new Guid(2, 0, 0, new byte[8]), "66");
        var (library, fake) = Library(movie);

        var matches = new LibraryIndexResolver(library, null, NullLogger.Instance)
            .Resolve(new List<string> { "66" }, BaseItemKind.Movie);

        Assert.Equal(movie.Id, matches["66"].Id);
        Assert.Equal(1, fake.ListQueries);
    }

    [Fact]
    public void ElIndiceSeValidaContraElTipoPedido()
    {
        var series = Series(new Guid(3, 0, 0, new byte[8]), "77");
        var (library, _) = Library(series);
        var index = new IndexStub(new Dictionary<string, Guid>(StringComparer.Ordinal) { ["77"] = series.Id });

        var matches = new LibraryIndexResolver(library, index, NullLogger.Instance)
            .Resolve(new List<string> { "77" }, BaseItemKind.Movie);

        Assert.Empty(matches);
    }

    [Fact]
    public void UnIndiceQueFallaNoDejaLaListaVacia()
    {
        var movie = Movie(new Guid(4, 0, 0, new byte[8]), "88");
        var (library, fake) = Library(movie);
        var index = new IndexStub(new Dictionary<string, Guid>(StringComparer.Ordinal), throwOnCall: true);

        var matches = new LibraryIndexResolver(library, index, NullLogger.Instance)
            .Resolve(new List<string> { "88" }, BaseItemKind.Movie);

        Assert.Equal(movie.Id, matches["88"].Id);
        Assert.Equal(1, fake.ListQueries);
    }

    private static Movie Movie(Guid id, string tmdbId)
    {
        var movie = new Movie { Id = id, Name = "Pelicula" };
        movie.ProviderIds["Tmdb"] = tmdbId;
        return movie;
    }

    private static Series Series(Guid id, string tmdbId)
    {
        var series = new Series { Id = id, Name = "Serie" };
        series.ProviderIds["Tmdb"] = tmdbId;
        return series;
    }

    private static (ILibraryManager Manager, IndexLibraryFake Fake) Library(params BaseItem[] items)
    {
        var proxy = DispatchProxy.Create<ILibraryManager, IndexLibraryFake>();
        var fake = (IndexLibraryFake)(object)proxy;
        fake.Items.AddRange(items);
        return (proxy, fake);
    }

    /// <summary>Indice del proveedor: devuelve un mapa fijo o falla, a voluntad.</summary>
    private sealed class IndexStub : ILibraryIndexQueryProvider
    {
        private readonly Dictionary<string, Guid> _answer;
        private readonly bool _throwOnCall;

        public IndexStub(Dictionary<string, Guid> answer, bool throwOnCall = false)
        {
            _answer = answer;
            _throwOnCall = throwOnCall;
        }

        public int Calls { get; private set; }

        public List<string> Requested { get; } = [];

        public IReadOnlyDictionary<string, Guid> GetItemIdsByTmdbIds(IReadOnlyList<string> tmdbIds, BaseItemKind kind)
        {
            Calls++;
            Requested.AddRange(tmdbIds);

            if (_throwOnCall)
            {
                throw new InvalidOperationException("el indice no esta disponible");
            }

            return _answer;
        }
    }

    /// <summary>Biblioteca falsa: resuelve por id y por provider id, y cuenta las consultas.</summary>
    // No puede ser sealed: DispatchProxy genera una subclase en tiempo de ejecución.
    private class IndexLibraryFake : DispatchProxy
    {
        public List<BaseItem> Items { get; } = [];

        /// <summary>Gets the number of GetItemList calls, to prove the index avoided them.</summary>
        public int ListQueries { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(ILibraryManager.GetItemList):
                    ListQueries++;
                    var query = (InternalItemsQuery)args![0]!;
                    return query.HasAnyProviderId is { Count: > 0 } providerIds
                        ? Items.Where(item => providerIds.All(pair =>
                            item.ProviderIds.TryGetValue(pair.Key, out var value) && value == pair.Value)).ToList()
                        : Items.ToList();

                case nameof(ILibraryManager.GetItemById):
                    var id = (Guid)args![0]!;
                    return Items.FirstOrDefault(item => item.Id == id);

                default:
                    return null;
            }
        }
    }
}
