using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyTrend.Api;
using Jellyfin.Plugin.JellyTrend.Services.Recommendation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// El motor no puede depender de la tecnología de base de datos: el proveedor es un acelerador
/// opcional. Estas pruebas fijan las tres situaciones que importan —sin proveedor, con un proveedor
/// que no responde y con uno que falla— y comprueban que en todas se generan candidatos desde
/// <see cref="ILibraryManager"/> y queda constancia en el log.
/// </summary>
public sealed class RecommendationSourceTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid WatchedId = new(1, 0, 0, new byte[8]);
    private static readonly Guid UnwatchedId = new(2, 0, 0, new byte[8]);

    [Fact]
    public void WithoutProviderTheLibraryManagerFeedsTheEngine()
    {
        var source = CandidateSource.Create(BuildLibrary(), BuildUserData(), null, NullLogger.Instance, Store());

        Assert.False(source.UsingProvider);
        Assert.Equal("ILibraryManager", source.Description, StringComparer.Ordinal);

        var watched = source.GetWatched(NewUser(), Now);
        var candidates = source.GetCandidates(NewUser(), ["Terror"], [], [], []);

        Assert.Single(watched);
        Assert.Equal(WatchedId, watched[0].Id);
        Assert.Single(candidates);
        Assert.Equal(UnwatchedId, candidates[0].Id);
        Assert.Contains("Terror", candidates[0].Genres, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Regresión del fallo de producción: cuando las bibliotecas están agrupadas, los ids de las vistas
    /// no coinciden con el <c>TopParentId</c> de las películas, así que la consulta con alcance no
    /// devuelve nada. Antes eso dejaba la fila vacía para todos los usuarios; ahora se reintenta sin
    /// alcance y se deja constancia en los dos logs.
    /// </summary>
    [Fact]
    public void LibraryScopeThatMatchesNothingFallsBackToTheWholeLibrary()
    {
        var logger = new CapturingLogger();
        var source = CandidateSource.Create(BuildLibrary(), BuildUserData(), null, logger, Store());

        var candidates = source.GetCandidates(NewUser(), ["Terror"], [], [], [Guid.NewGuid()]);

        Assert.Single(candidates);
        Assert.Equal(UnwatchedId, candidates[0].Id);
        Assert.Contains(logger.Messages, message => message.Contains("se reintenta sin alcance", StringComparison.Ordinal));
    }

    /// <summary>
    /// Este es el fallo que el motor anterior no podía ver: un proveedor que contesta vacío y deja la
    /// fila sin candidatos sin decir nada. Ahora se detecta (la biblioteca sí tiene películas), se
    /// registra y se sigue con ILibraryManager.
    /// </summary>
    [Fact]
    public void ProviderThatAnswersNothingIsRejectedAndLogged()
    {
        var logger = new CapturingLogger();
        var provider = new StubProvider(throwOnCall: false, answer: []);
        var source = CandidateSource.Create(BuildLibrary(), BuildUserData(), new ProviderState(provider), logger, Store());

        Assert.True(source.UsingProvider);

        var candidates = source.GetCandidates(NewUser(), ["Terror"], [], [], []);

        Assert.False(source.UsingProvider);
        Assert.Equal("ILibraryManager", source.Description, StringComparer.Ordinal);
        Assert.Single(candidates);
        Assert.Contains(logger.Messages, message => message.Contains("no devolvió candidatos", StringComparison.Ordinal));
    }

    /// <summary>
    /// El proveedor roto es un estado del entorno, no del usuario: se descarta una vez y no se vuelve a
    /// preguntar por los demas usuarios de la ejecucion.
    /// </summary>
    [Fact]
    public void ARejectedProviderIsNotRetriedForTheNextUser()
    {
        var logger = new CapturingLogger();
        var provider = new StubProvider(throwOnCall: false, answer: []);
        var shared = new ProviderState(provider);

        CandidateSource.Create(BuildLibrary(), BuildUserData(), shared, logger, Store())
            .GetCandidates(NewUser(), ["Terror"], [], [], []);

        var queriesAfterFirstUser = provider.Queries;
        Assert.True(queriesAfterFirstUser > 0);

        var second = CandidateSource.Create(BuildLibrary(), BuildUserData(), shared, logger, Store());
        var candidates = second.GetCandidates(NewUser(), ["Terror"], [], [], []);

        Assert.Equal(queriesAfterFirstUser, provider.Queries);
        Assert.False(second.UsingProvider);
        Assert.Single(candidates);
    }

    [Fact]
    public void ProviderThatThrowsIsRejectedAndTheRowIsStillBuilt()
    {
        var logger = new CapturingLogger();
        var provider = new StubProvider(throwOnCall: true, answer: []);
        var source = CandidateSource.Create(BuildLibrary(), BuildUserData(), new ProviderState(provider), logger, Store());

        var candidates = source.GetCandidates(NewUser(), ["Terror"], [], [], []);

        Assert.False(source.UsingProvider);
        Assert.Single(candidates);
        Assert.Contains(logger.Messages, message => message.Contains("falló", StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderCandidatesAreEnrichedWithTheSameData()
    {
        var provider = new StubProvider(throwOnCall: false, answer: [new RecommendationItem(UnwatchedId, "55", ["Terror"], [], [], 8f, null)]);
        var source = CandidateSource.Create(BuildLibrary(), BuildUserData(), new ProviderState(provider), NullLogger.Instance, Store());

        var candidates = source.GetCandidates(NewUser(), ["Terror"], [], [], []);

        Assert.True(source.UsingProvider);

        // El proveedor solo aporta los ids: géneros, rating y personas salen de la biblioteca, así que
        // ambos modos puntúan exactamente los mismos datos.
        var candidate = Assert.Single(candidates);
        Assert.Equal(UnwatchedId, candidate.Id);
        Assert.Equal("Terror", Assert.Single(candidate.Genres));
        Assert.Equal(7f, candidate.CommunityRating);
        Assert.Null(candidate.TmdbId);
    }

    /// <summary>
    /// La cache debe sobrevivir a un reinicio del servidor: una segunda instancia sobre el mismo
    /// archivo no puede volver a consultar la biblioteca. Con la cache solo en memoria, el primer
    /// usuario de cada ejecucion pagaba otra vez las ~600 consultas de personas.
    /// </summary>
    [Fact]
    public void FeatureCacheSurvivesARestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "jellytrend-features-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        var uniqueId = new Guid(987654, 0, 0, new byte[8]);
        var unwatched = Movie(uniqueId, "Solo una vez", played: false, "Terror");

        var library = FakeProxy.Create<ILibraryManager, LibraryFake>(
            fake =>
            {
                fake.ScopeMatchesLibrary = true;
                fake.Unwatched = [unwatched];
            },
            out var fakeLibrary);

        var first = FeatureStore.Open(folder);
        var firstSource = CandidateSource.Create(library, BuildUserData(), null, NullLogger.Instance, first);
        firstSource.GetCandidates(NewUser(), ["Terror"], [], [], []);
        first.Flush();

        var readsAfterFirstRun = fakeLibrary.PeopleRequests.Count(id => id == uniqueId);

        // Segundo arranque: otra instancia de la cache sobre el mismo archivo.
        var second = FeatureStore.Open(folder);
        var secondSource = CandidateSource.Create(library, BuildUserData(), null, NullLogger.Instance, second);
        var candidates = secondSource.GetCandidates(NewUser(), ["Terror"], [], [], []);

        Assert.Single(candidates);
        Assert.Equal(1, readsAfterFirstRun);
        Assert.Equal(1, fakeLibrary.PeopleRequests.Count(id => id == uniqueId));
    }

    private static FeatureStore Store() => FeatureStore.Open(null);

    private static User NewUser() => new("tester", "Default", "Default");

    /// <summary>
    /// Los ítems virtuales son sombras de canal (incluidos los canales del propio plugin): no deben
    /// entrar en la fila porque no son contenido de la biblioteca.
    /// </summary>
    [Fact]
    public void VirtualItemsAreNotOfferedAsCandidates()
    {
        var virtualMovie = Movie(UnwatchedId, "Sombra de canal", played: false, "Terror");
        virtualMovie.IsVirtualItem = true;

        var library = FakeProxy.Create<ILibraryManager, LibraryFake>(
            fake =>
            {
                fake.ScopeMatchesLibrary = true;
                fake.Unwatched = [virtualMovie];
            });

        var source = CandidateSource.Create(library, BuildUserData(), null, NullLogger.Instance, Store());

        Assert.Empty(source.GetCandidates(NewUser(), ["Terror"], [], [], []));
    }

    private static Movie Movie(Guid id, string name, bool played, params string[] genres) => new()
    {
        Id = id,
        Name = name,
        Genres = genres,
        Tags = [],
        Studios = [],
        CommunityRating = 7f,
        PremiereDate = Now.AddYears(-1)
    };

    private static ILibraryManager BuildLibrary() => FakeProxy.Create<ILibraryManager, LibraryFake>(
        fake =>
        {
            fake.Played = [Movie(WatchedId, "Vista", played: true, "Terror")];
            fake.Unwatched = [Movie(UnwatchedId, "Sin ver", played: false, "Terror")];
        });

    private static IUserDataManager BuildUserData() => FakeProxy.Create<IUserDataManager, UserDataFake>(
        fake => fake.Data = new Dictionary<Guid, UserItemData>
        {
            [WatchedId] = new() { Key = WatchedId.ToString("N"), Played = true, PlayCount = 2, LastPlayedDate = Now.AddDays(-3) }
        });

    /// <summary>
    /// Fake <see cref="ILibraryManager"/> que solo responde lo que el motor usa: listar items y
    /// obtener las personas en lote.
    /// </summary>
    public class LibraryFake : DispatchProxy
    {
        public IReadOnlyList<BaseItem> Played { get; set; } = [];

        public IReadOnlyList<BaseItem> Unwatched { get; set; } = [];

        /// <summary>Ids de los ítems cuyas personas se han consultado, para poder afirmar sobre cachés.</summary>
        public List<Guid> PeopleRequests { get; } = [];

        /// <summary>
        /// Cuando es falso (el caso de produccion con bibliotecas agrupadas) cualquier consulta con
        /// alcance devuelve vacio, porque los ids de vista no coinciden con el TopParentId de los items.
        /// </summary>
        public bool ScopeMatchesLibrary { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(ILibraryManager.GetItemList):
                    var query = (InternalItemsQuery)args![0]!;
                    if (query.TopParentIds is { Length: > 0 } && !ScopeMatchesLibrary)
                    {
                        return Array.Empty<BaseItem>();
                    }

                    if (query.ItemIds is { Length: > 0 })
                    {
                        return Unwatched.Where(item => query.ItemIds.Contains(item.Id)).ToList();
                    }

                    if (query.IsPlayed == true)
                    {
                        return Played;
                    }

                    // La consulta de "en curso" no filtra por IsPlayed: en este escenario no hay ninguna.
                    return query.IsPlayed == false ? Unwatched : [];

#if NET10_0_OR_GREATER
                case nameof(ILibraryManager.GetPeopleByItems):
                    foreach (var id in (IReadOnlyList<Guid>)args![0]!)
                    {
                        PeopleRequests.Add(id);
                    }

                    return new Dictionary<Guid, IReadOnlyList<PersonInfo>>
                    {
                        [UnwatchedId] = FakePeople
                    };
#endif

                case nameof(ILibraryManager.GetPeople):
                    // La linea 10.11 resuelve las personas item a item (ApiCompat).
                    PeopleRequests.Add(((BaseItem)args![0]!).Id);
                    return FakePeople;

                case nameof(ILibraryManager.GetUserRootFolder):
                    return null!;

                default:
                    return Default(targetMethod?.ReturnType);
            }
        }

        private static IReadOnlyList<PersonInfo> FakePeople =>
        [
            new PersonInfo { Id = new Guid(9, 0, 0, new byte[8]), Name = "Actor", Type = PersonKind.Actor }
        ];

        private static object? Default(Type? returnType)
        {
            if (returnType is null || returnType == typeof(void))
            {
                return null;
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

    /// <summary>
    /// Fake <see cref="IUserDataManager"/> que devuelve los datos de reproducción por lote.
    /// </summary>
    public class UserDataFake : DispatchProxy
    {
        public Dictionary<Guid, UserItemData> Data { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
#if NET10_0_OR_GREATER
            if (targetMethod?.Name == nameof(IUserDataManager.GetUserDataBatch))
            {
                return Data;
            }
#endif

            if (targetMethod?.Name == nameof(IUserDataManager.GetUserData))
            {
                // La linea 10.11 lee los datos de usuario item a item (ApiCompat).
                var item = (BaseItem)args![1]!;
                return Data.TryGetValue(item.Id, out var data) ? data : null;
            }

            return targetMethod?.ReturnType?.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    private static class FakeProxy
    {
        public static TInterface Create<TInterface, TProxy>(Action<TProxy> configure)
            where TInterface : class
            where TProxy : DispatchProxy
            => Create<TInterface, TProxy>(configure, out _);

        public static TInterface Create<TInterface, TProxy>(Action<TProxy> configure, out TProxy proxy)
            where TInterface : class
            where TProxy : DispatchProxy
        {
            var created = DispatchProxy.Create<TInterface, TProxy>();
            proxy = (TProxy)(object)created;
            configure(proxy);
            return created;
        }
    }

    /// <summary>Proveedor de base de datos que puede responder vacío o fallar, a voluntad.</summary>
    private sealed class StubProvider : IRecommendationQueryProvider
    {
        private readonly bool _throwOnCall;
        private readonly IReadOnlyList<RecommendationItem> _answer;

        public StubProvider(bool throwOnCall, IReadOnlyList<RecommendationItem> answer)
        {
            _throwOnCall = throwOnCall;
            _answer = answer;
        }

        /// <summary>Gets the number of queries the source actually sent to the provider.</summary>
        public int Queries { get; private set; }

        public IReadOnlyList<RecommendationItem> GetPlayedMovies(Guid userId, int limit) => Answer();

        public IReadOnlyList<RecommendationItem> GetResumableMovies(Guid userId, int limit) => Answer();

        public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByGenres(Guid userId, IReadOnlyList<string> genres, IReadOnlyList<Guid> topParentIds, int limit) => Answer();

        public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByPersons(Guid userId, IReadOnlyList<Guid> personIds, IReadOnlyList<Guid> topParentIds, int limit) => Answer();

        public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByTags(Guid userId, IReadOnlyList<string> tags, IReadOnlyList<Guid> topParentIds, int limit) => Answer();

        public IReadOnlyList<RecommendationItem> GetRandomUnwatchedMovies(Guid userId, IReadOnlyList<Guid> topParentIds, int limit) => Answer();

        private IReadOnlyList<RecommendationItem> Answer()
        {
            Queries++;

            return _throwOnCall
                ? throw new InvalidOperationException("el proveedor no está disponible")
                : _answer;
        }
    }

    /// <summary>Logger de pruebas que guarda los mensajes para poder afirmar sobre ellos.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
