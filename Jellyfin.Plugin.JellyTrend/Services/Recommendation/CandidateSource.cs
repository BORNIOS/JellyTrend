using System;
using System.Collections.Generic;
using System.Linq;

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyTrend.Api;
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
    private const int MaxWatchedItems = 300;
    private const int BroadPoolLimit = 400;
    private const int MaxCandidatesPerFacet = 150;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IRecommendationQueryProvider? _provider;
    private readonly ILogger _logger;
    private bool _providerRejected;

    private CandidateSource(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IRecommendationQueryProvider? provider,
        ILogger logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _provider = provider;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether candidate discovery is currently running on the provider.</summary>
    public bool UsingProvider => _provider is not null && !_providerRejected;

    /// <summary>Gets the data source description used in the task log.</summary>
    public string Description => UsingProvider
        ? "proveedor de base de datos"
        : _provider is null ? "ILibraryManager (sin proveedor)" : "ILibraryManager (proveedor descartado)";

    /// <summary>
    /// Creates the source.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="provider">Optional database-provider backend.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>The candidate source.</returns>
    public static CandidateSource Create(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IRecommendationQueryProvider? provider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userDataManager);
        ArgumentNullException.ThrowIfNull(logger);

        return new CandidateSource(libraryManager, userDataManager, provider, logger);
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
            Limit = MaxWatchedItems
        }));

        items.AddRange(_libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie],
            IsResumable = true,
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

        var userData = _userDataManager.GetUserDataBatch(distinct, user);
        var people = GetPeopleByItem(distinct.Select(static item => item.Id).ToList());

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

        if (ids.Count == 0)
        {
            CollectFromLibrary(user, genres, tags, people, topParentIds, ids);
        }

        return Materialize(ids);
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
            var answer = _provider!;
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

        if (items.Count == 0)
        {
            return [];
        }

        var people = GetPeopleByItem(items.Select(static item => item.Id).ToList());

        return items
            .Select(item => new CandidateItem(
                item.Id,
                item.Name ?? string.Empty,
                item.ProviderIds.TryGetValue("Tmdb", out var tmdb) ? tmdb : null,
                item.Genres ?? [],
                item.Tags ?? [],
                item.Studios ?? [],
                RelevantPeople(people, item.Id),
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

    private IReadOnlyDictionary<Guid, IReadOnlyList<PersonInfo>> GetPeopleByItem(List<Guid> itemIds)
        => itemIds.Count == 0
            ? new Dictionary<Guid, IReadOnlyList<PersonInfo>>()
            : _libraryManager.GetPeopleByItems(itemIds);

    private static TasteItem BuildTasteItem(
        BaseItem item,
        Dictionary<Guid, UserItemData> userData,
        IReadOnlyDictionary<Guid, IReadOnlyList<PersonInfo>> people,
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
            RelevantPeople(people, item.Id),
            weight);
    }

    private static List<Guid> RelevantPeople(
        IReadOnlyDictionary<Guid, IReadOnlyList<PersonInfo>> people,
        Guid itemId)
    {
        if (!people.TryGetValue(itemId, out var itemPeople) || itemPeople.Count == 0)
        {
            return [];
        }

        var ids = new List<Guid>(itemPeople.Count);
        foreach (var person in itemPeople)
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
        => _providerRejected = true;
}
