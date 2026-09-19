using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyTrend.Api;
using Jellyfin.Plugin.JellyTrend.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Single entry point for the library data the engine needs: the user's watched movies (always read
/// through <see cref="ILibraryManager"/> so engagement signals and people are available) and the
/// pool of unwatched movies to score.
/// </summary>
/// <remarks>
/// <para>
/// The optional <see cref="IRecommendationQueryProvider"/> (the PostgreSQL provider plugin) is an
/// <em>accelerator</em>, never a requirement. It is used only to discover candidate ids; the items
/// are then materialized through the library manager, which keeps genres, tags, studios, names and
/// people identical in both modes. If the provider is missing, throws, or answers nothing while the
/// library clearly has movies, the source falls back to <see cref="ILibraryManager"/> for the rest of
/// the run and says so in the log — a silent empty answer is what made a broken query invisible.
/// </para>
/// </remarks>
internal sealed class CandidateSource
{
    private const int MaxWatchedItems = 1000;
    private const int BroadPoolLimit = 400;
    private const int MaxCandidatesPerFacet = 150;

    /// <summary>Posiciones de reparto que cuentan, igual que en la capa 2.</summary>
    private const int MainCast = 4;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager? _userManager;
    private readonly ProviderState _providerState;
    private readonly ILogger _logger;
    private readonly FeatureStore _features;

    private CandidateSource(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IUserManager? userManager,
        ProviderState providerState,
        ILogger logger,
        FeatureStore features)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _providerState = providerState;
        _logger = logger;
        _features = features;
    }

    /// <summary>Gets a value indicating whether candidate discovery is currently running on the provider.</summary>
    public bool UsingProvider => _providerState.IsUsable;

    /// <summary>Gets the data source description used in the task log.</summary>
    public string Description => UsingProvider ? "proveedor de base de datos" : "ILibraryManager";

    /// <summary>
    /// Creates the source.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="providerState">Estado del backend opcional de base de datos; <c>null</c> equivale a no tener proveedor.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="features">Cache persistente de caracteristicas por item.</param>
    /// <param name="userManager">
    /// Gestor de usuarios, necesario para medir la popularidad local (lo que ve el resto del servidor);
    /// sin el, el arranque en frio solo puede contar la calidad y la novedad.
    /// </param>
    /// <returns>The candidate source.</returns>
    public static CandidateSource Create(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        ProviderState? providerState,
        ILogger logger,
        FeatureStore features,
        IUserManager? userManager = null)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userDataManager);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(features);

        return new CandidateSource(
            libraryManager,
            userDataManager,
            userManager,
            providerState ?? new ProviderState(),
            logger,
            features);
    }

    /// <summary>
    /// Reads the user's watched and in-progress movies with the engagement signals needed to weigh them.
    /// </summary>
    /// <param name="user">Target user.</param>
    /// <param name="nowUtc">Current UTC time.</param>
    /// <returns>The watched movies, most engaged first is not guaranteed.</returns>
    public IReadOnlyList<TasteItem> GetWatched(User user, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(user);

        var items = new List<BaseItem>();
        items.AddRange(_libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie],
            IsPlayed = true,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = MaxWatchedItems
        }));

        items.AddRange(_libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie],
            IsResumable = true,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = MaxWatchedItems
        }));

        var distinct = items
            .GroupBy(static item => item.Id)
            .Select(static group => group.First())
            .ToList();

        if (distinct.Count == 0)
        {
            return [];
        }

        var userData = ApiCompat.GetUserData(_userDataManager, distinct, user);
        var people = GetPeopleByItem(distinct);

        return distinct
            .Select(item => BuildTasteItem(item, userData, people, nowUtc))
            .ToList();
    }

    /// <summary>
    /// Builds the pool of unwatched movies to score.
    /// </summary>
    /// <param name="user">Target user.</param>
    /// <param name="genres">Genres taken from the taste profile.</param>
    /// <param name="tags">Tags taken from the taste profile.</param>
    /// <param name="people">Person ids taken from the taste profile.</param>
    /// <param name="topParentIds">Library ids the user can see.</param>
    /// <returns>The candidate movies.</returns>
    public List<CandidateItem> GetCandidates(
        User user,
        IReadOnlyList<string> genres,
        IReadOnlyList<string> tags,
        IReadOnlyList<Guid> people,
        IReadOnlyList<Guid> topParentIds)
    {
        ArgumentNullException.ThrowIfNull(user);

        var ids = new HashSet<Guid>();

        if (UsingProvider && !TryCollectFromProvider(user, genres, tags, people, topParentIds, ids))
        {
            RejectProvider();
        }

        var scoped = ids.Count;
        if (ids.Count == 0)
        {
            CollectFromLibrary(user, genres, tags, people, topParentIds, ids);
            scoped = ids.Count;

            if (ids.Count == 0 && topParentIds.Count > 0)
            {
                // El alcance por vista no siempre coincide con el TopParentId de los items: Jellyfin
                // agrupa bibliotecas y esas vistas llevan un id sintetico, asi que el filtro no deja
                // pasar nada. Antes de devolver una fila vacia se reintenta sin alcance.
                var warning = string.Format(
                    CultureInfo.InvariantCulture,
                    "[Recomendaciones] 0 candidatos dentro del alcance de {0} vistas; se reintenta sin alcance.",
                    topParentIds.Count);
                _logger.LogWarning("[Recomendaciones] 0 candidatos dentro del alcance de {Views} vistas; se reintenta sin alcance.", topParentIds.Count);
                JellyTrendLog.Warn(warning);
                CollectFromLibrary(user, genres, tags, people, [], ids);
            }
        }

        var candidates = Materialize(ids);

        JellyTrendLog.Info(string.Format(
            CultureInfo.InvariantCulture,
            "[Recomendaciones] fuente={0} | alcance={1} | candidatos: dentro del alcance={2}, tras facetas={3}, materializados={4} (generos={5}, tags={6}, personas={7})",
            Description,
            DescribeScope(topParentIds),
            scoped,
            ids.Count,
            candidates.Count,
            genres.Count,
            tags.Count,
            people.Count));

        return candidates;
    }

    /// <summary>
    /// Describe el alcance por sus nombres, no solo por el numero de ids.
    /// </summary>
    /// <param name="topParentIds">Ids usados como alcance.</param>
    /// <returns>Numero de ids y los nombres que se han podido resolver.</returns>
    private string DescribeScope(IReadOnlyList<Guid> topParentIds)
    {
        var names = topParentIds
            .Select(id => _libraryManager.GetItemById(id)?.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .Take(4)
            .ToList();

        return names.Count == 0
            ? $"{topParentIds.Count} ids"
            : $"{topParentIds.Count} ids: {string.Join(", ", names)}";
    }

    private bool TryCollectFromProvider(
        User user,
        IReadOnlyList<string> genres,
        IReadOnlyList<string> tags,
        IReadOnlyList<Guid> people,
        IReadOnlyList<Guid> topParentIds,
        HashSet<Guid> ids)
    {
        try
        {
            var answer = _providerState.Provider!;
            var found = 0;

            if (genres.Count > 0)
            {
                var byGenre = answer.GetUnwatchedMoviesByGenres(user.Id, genres, topParentIds, MaxCandidatesPerFacet);
                found += byGenre.Count;
                AddIds(ids, byGenre);
            }

            if (tags.Count > 0)
            {
                var byTag = answer.GetUnwatchedMoviesByTags(user.Id, tags, topParentIds, MaxCandidatesPerFacet);
                found += byTag.Count;
                AddIds(ids, byTag);
            }

            if (people.Count > 0)
            {
                var byPerson = answer.GetUnwatchedMoviesByPersons(user.Id, people, topParentIds, MaxCandidatesPerFacet);
                found += byPerson.Count;
                AddIds(ids, byPerson);
            }

            if (found > 0)
            {
                return true;
            }

            // An empty answer is only acceptable when the library really has nothing to offer.
            if (HasUnwatchedLibrary(user, topParentIds))
            {
                _logger.LogWarning(
                    "[Recomendaciones] El proveedor de base de datos no devolvió candidatos aunque la biblioteca tiene películas sin ver; se usa ILibraryManager.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Recomendaciones] El proveedor de base de datos falló; se usa ILibraryManager.");
            return false;
        }
    }

    private void CollectFromLibrary(
        User user,
        IReadOnlyList<string> genres,
        IReadOnlyList<string> tags,
        IReadOnlyList<Guid> people,
        IReadOnlyList<Guid> topParentIds,
        HashSet<Guid> ids)
    {
        // Broad pool first: guarantees a rich set to score even when the profile is thin, and gives
        // the diversity pass something to choose from.
        AddIds(ids, _libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie],
            IsPlayed = false,
            TopParentIds = topParentIds.ToArray(),
            OrderBy = [(ItemSortBy.CommunityRating, SortOrder.Descending)],
            Limit = BroadPoolLimit
        }));

        if (genres.Count > 0)
        {
            AddIds(ids, _libraryManager.GetItemList(new InternalItemsQuery
            {
                User = user,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie],
                IsPlayed = false,
                TopParentIds = topParentIds.ToArray(),
                Genres = genres.ToArray(),
                Limit = MaxCandidatesPerFacet
            }));
        }

        if (tags.Count > 0)
        {
            AddIds(ids, _libraryManager.GetItemList(new InternalItemsQuery
            {
                User = user,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie],
                IsPlayed = false,
                TopParentIds = topParentIds.ToArray(),
                Tags = tags.ToArray(),
                Limit = MaxCandidatesPerFacet
            }));
        }

        if (people.Count > 0)
        {
            AddIds(ids, _libraryManager.GetItemList(new InternalItemsQuery
            {
                User = user,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie],
                IsPlayed = false,
                TopParentIds = topParentIds.ToArray(),
                PersonIds = people.ToArray(),
                Limit = MaxCandidatesPerFacet
            }));
        }
    }

    // Materializes ids into full items so both modes score exactly the same data.
    private List<CandidateItem> Materialize(HashSet<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie],
            ItemIds = ids.ToArray()
        });

        // Los ítems virtuales son sombras de canal (los canales de tendencias y recomendados del propio
        // plugin, entre otros): recomendar una sombra de nosotros mismos no aporta nada y ensucia el pool.
        items = items.Where(static item => !item.IsVirtualItem).ToList();

        if (items.Count == 0)
        {
            return [];
        }

        var people = GetPeopleByItem(items);

        return items
            .Select(item => new CandidateItem(
                item.Id,
                item.Name ?? string.Empty,
                item.ProviderIds.TryGetValue("Tmdb", out var tmdb) ? tmdb : null,
                item.Genres ?? [],
                item.Tags ?? [],
                item.Studios ?? [],
                people.TryGetValue(item.Id, out var personIds) ? personIds : [],
                item.CommunityRating,
                item.PremiereDate))
            .ToList();
    }

    private bool HasUnwatchedLibrary(User user, IReadOnlyList<Guid> topParentIds)
    {
        var probe = _libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie],
            IsPlayed = false,
            Limit = 1
        });

        return probe.Count > 0;
    }

    private Dictionary<Guid, IReadOnlyList<Guid>> GetPeopleByItem(IReadOnlyList<BaseItem> items)
    {
        if (items.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<Guid>>();
        }

        var people = new Dictionary<Guid, IReadOnlyList<Guid>>(items.Count);
        var missing = new List<BaseItem>();

        foreach (var item in items)
        {
            // Una entrada con personas pero sin nombres viene de antes de que el reparto se guardara por
            // rol y no sirve para el perfil: se lee de nuevo y la propia lectura la reescribe. Una entrada
            // sin personas es un titulo sin reparto util, y esa si se respeta.
            if (_features.TryGet(item.Id, out var cached) && (cached.People.Count == 0 || cached.HasRoles))
            {
                if (cached.People.Count > 0)
                {
                    people[item.Id] = cached.People;
                }

                continue;
            }

            missing.Add(item);
        }

        if (missing.Count > 0)
        {
            var loaded = ApiCompat.GetPeopleByItems(_libraryManager, missing);
            foreach (var item in missing)
            {
                var itemPeople = loaded.TryGetValue(item.Id, out var found) ? found : [];
                var relevant = RelevantPeople(itemPeople);

                // Se guarda el item completo (generos, etiquetas, estudios, personas por id y por nombre)
                // para que las siguientes ejecuciones no vuelvan a leer nada de este titulo.
                _features.Set(item.Id, new ItemFeatures(
                    item.Genres ?? [],
                    item.Tags ?? [],
                    item.Studios ?? [],
                    relevant,
                    item.CommunityRating,
                    item.PremiereDate,
                    Names(itemPeople, PersonKind.Director),
                    Names(itemPeople, PersonKind.Actor, MainCast),
                    Names(itemPeople, PersonKind.Writer),
                    null));

                if (relevant.Count > 0)
                {
                    people[item.Id] = relevant;
                }
            }
        }

        return people;
    }

    /// <summary>
    /// Mide lo que el resto del servidor ve cada titulo, para el arranque en frio de un usuario nuevo.
    /// </summary>
    /// <param name="candidates">Titulos candidatos.</param>
    /// <param name="viewer">Usuario al que se le va a recomendar.</param>
    /// <returns>Engagement agregado de cada titulo, normalizado de forma que el mas visto vale 1.</returns>
    public Dictionary<Guid, double> GetLocalPopularity(IReadOnlyList<CandidateItem> candidates, User viewer)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(viewer);

        var popularity = new Dictionary<Guid, double>();
        if (candidates.Count == 0)
        {
            return popularity;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie],
            ItemIds = [.. candidates.Select(static candidate => candidate.Id)]
        });

        if (items.Count == 0)
        {
            return popularity;
        }

        var viewers = new List<User> { viewer };
        if (_userManager is not null)
        {
            viewers.AddRange(_userManager.GetUsers().Where(user => user.Id != viewer.Id));
        }

        foreach (var user in viewers)
        {
            Dictionary<Guid, UserItemData> userData;
            try
            {
                userData = ApiCompat.GetUserData(_userDataManager, items, user);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "JellyTrend: sin datos de '{User}' para la popularidad local.", user.Username);
                continue;
            }

            foreach (var item in items)
            {
                if (!userData.TryGetValue(item.Id, out var data) || data is null)
                {
                    continue;
                }

                var engagement = data.PlayCount
                    + (data.IsFavorite ? 2 : 0)
                    + (data.Played ? 1 : 0);

                if (engagement > 0)
                {
                    popularity[item.Id] = popularity.GetValueOrDefault(item.Id) + engagement;
                }
            }
        }

        var maximum = popularity.Count == 0 ? 0d : popularity.Values.Max();
        if (maximum > 0d)
        {
            foreach (var id in popularity.Keys.ToList())
            {
                popularity[id] /= maximum;
            }
        }

        return popularity;
    }

    private static List<string> Names(IReadOnlyList<PersonInfo> people, PersonKind kind, int limit = int.MaxValue)
        => [.. people
            .Where(person => person.Type == kind && !string.IsNullOrWhiteSpace(person.Name))
            .Select(static person => person.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(limit)];

    private static TasteItem BuildTasteItem(
        BaseItem item,
        Dictionary<Guid, UserItemData> userData,
        Dictionary<Guid, IReadOnlyList<Guid>> people,
        DateTime nowUtc)
    {
        userData.TryGetValue(item.Id, out var data);

        var runtime = item.RunTimeTicks ?? 0;
        var progress = runtime > 0
            ? Math.Clamp((data?.PlaybackPositionTicks ?? 0) / (double)runtime, 0d, 1d)
            : 0d;

        var weight = EngagementModel.Weight(
            data?.Played ?? true,
            progress,
            data?.PlayCount ?? 1,
            data?.IsFavorite ?? false,
            data?.LastPlayedDate,
            nowUtc);

        return new TasteItem(
            item.Id,
            item.Genres ?? [],
            item.Tags ?? [],
            item.Studios ?? [],
            people.TryGetValue(item.Id, out var personIds) ? personIds : [],
            weight);
    }

    private static List<Guid> RelevantPeople(IReadOnlyList<PersonInfo> people)
    {
        var ids = new List<Guid>(people.Count);
        foreach (var person in people)
        {
            if (person.Id == Guid.Empty)
            {
                continue;
            }

            if (person.Type is PersonKind.Actor or PersonKind.Director or PersonKind.Writer)
            {
                ids.Add(person.Id);
            }
        }

        return ids;
    }

    private static void AddIds(HashSet<Guid> ids, IReadOnlyList<BaseItem> items)
    {
        foreach (var item in items)
        {
            ids.Add(item.Id);
        }
    }

    private static void AddIds(HashSet<Guid> ids, IReadOnlyList<RecommendationItem> items)
    {
        foreach (var item in items)
        {
            ids.Add(item.Id);
        }
    }

    private void RejectProvider()
        => _providerState.Reject();
}
