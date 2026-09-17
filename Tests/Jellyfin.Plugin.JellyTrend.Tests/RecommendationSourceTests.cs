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
        var source = CandidateSource.Create(BuildLibrary(), BuildUserData(), null, NullLogger.Instance);

        Assert.False(source.UsingProvider);
        Assert.Contains("sin proveedor", source.Description, StringComparison.Ordinal);

        var watched = source.GetWatched(NewUser(), Now);
        var candidates = source.GetCandidates(NewUser(), ["Terror"], [], [], []);

        Assert.Single(watched);
        Assert.Equal(WatchedId, watched[0].Id);
        Assert.Single(candidates);
        Assert.Equal(UnwatchedId, candidates[0].Id);
        Assert.Contains("Terror", candidates[0].Genres, StringComparer.OrdinalIgnoreCase);
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
        var source = CandidateSource.Create(BuildLibrary(), BuildUserData(), provider, logger);

        Assert.True(source.UsingProvider);

        var candidates = source.GetCandidates(NewUser(), ["Terror"], [], [], []);

        Assert.False(source.UsingProvider);
        Assert.Contains("proveedor descartado", source.Description, StringComparison.Ordinal);
        Assert.Single(candidates);
        Assert.Contains(logger.Messages, message => message.Contains("no devolvió candidatos", StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderThatThrowsIsRejectedAndTheRowIsStillBuilt()
    {
        var logger = new CapturingLogger();
        var provider = new StubProvider(throwOnCall: true, answer: []);
        var source = CandidateSource.Create(BuildLibrary(), BuildUserData(), provider, logger);

        var candidates = source.GetCandidates(NewUser(), ["Terror"], [], [], []);

        Assert.False(source.UsingProvider);
        Assert.Single(candidates);
        Assert.Contains(logger.Messages, message => message.Contains("falló", StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderCandidatesAreEnrichedWithTheSameData()
    {
        var provider = new StubProvider(throwOnCall: false, answer: [new RecommendationItem(UnwatchedId, "55", ["Terror"], [], [], 8f, null)]);
        var source = CandidateSource.Create(BuildLibrary(), BuildUserData(), provider, logger: NullLogger.Instance);

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

    private static User NewUser() => new("tester", "Default", "Default");

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

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(ILibraryManager.GetItemList):
                    var query = (InternalItemsQuery)args![0]!;
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

                case nameof(ILibraryManager.GetPeopleByItems):
                    return new Dictionary<Guid, IReadOnlyList<PersonInfo>>
                    {
                        [UnwatchedId] =
                        [
                            new PersonInfo { Id = new Guid(9, 0, 0, new byte[8]), Name = "Actor", Type = PersonKind.Actor }
                        ]
                    };

                case nameof(ILibraryManager.GetUserRootFolder):
                    return null!;

                default:
                    return Default(targetMethod?.ReturnType);
            }
        }

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
            if (targetMethod?.Name == nameof(IUserDataManager.GetUserDataBatch))
            {
                return Data;
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
        {
            var proxy = DispatchProxy.Create<TInterface, TProxy>();
            configure((TProxy)(object)proxy);
            return proxy;
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

        public IReadOnlyList<RecommendationItem> GetPlayedMovies(Guid userId, int limit) => Answer();

        public IReadOnlyList<RecommendationItem> GetResumableMovies(Guid userId, int limit) => Answer();

        public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByGenres(Guid userId, IReadOnlyList<string> genres, IReadOnlyList<Guid> topParentIds, int limit) => Answer();

        public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByPersons(Guid userId, IReadOnlyList<Guid> personIds, IReadOnlyList<Guid> topParentIds, int limit) => Answer();

        public IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByTags(Guid userId, IReadOnlyList<string> tags, IReadOnlyList<Guid> topParentIds, int limit) => Answer();

        public IReadOnlyList<RecommendationItem> GetRandomUnwatchedMovies(Guid userId, IReadOnlyList<Guid> topParentIds, int limit) => Answer();

        private IReadOnlyList<RecommendationItem> Answer()
            => _throwOnCall
                ? throw new InvalidOperationException("el proveedor no está disponible")
                : _answer;
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
