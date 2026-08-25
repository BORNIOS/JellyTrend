using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyTrend.Api;

/// <summary>
/// Builds personalized recommendations from a user's watch history: aggregates the genres,
/// tags, people (actors, directors, writers) and studios the user watches the most, then
/// scores unwatched library items against those facets. Already-watched and in-progress
/// items are never recommended, and titles already shown in the trending row are excluded.
/// </summary>
internal static class RecommendationEngine
{
    private const int MaxAffinityItems = 300;
    private const int MaxCandidatesPerFacet = 150;
    private const int TopGenres = 5;
    private const int TopTags = 5;
    private const int TopPeople = 12;
    private const int ColdStartSample = 300;

    private const double FacetWeight = 0.4;
    private const double QualityWeight = 0.6;
    private const double RecencyBonus = 0.05;
    private const int MaxPerFranchise = 2;

    private static readonly Regex TrailingYearRegex = new(@"\s*\(\d{4}\)$|\s+\d{4}$", RegexOptions.Compiled);
    private static readonly Regex TrailingRomanRegex = new(@"\s+(?=[ivxlcdm]+$)(?:m{0,4}(?:cm|cd|d?c{0,3})?(?:xc|xl|l?x{0,3})?(?:ix|iv|v?i{0,3})?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrailingDigitRegex = new(@"\s+\d+$", RegexOptions.Compiled);

    /// <summary>
    /// Builds the recommendation item ids for a user.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="user">The target user.</param>
    /// <param name="trendingItemIds">Ids already shown in the trending row, excluded from results.</param>
    /// <param name="maxItems">Maximum number of recommendations.</param>
    /// <returns>The recommended item ids.</returns>
    public static List<Guid> BuildRecommendations(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        User user,
        IReadOnlySet<Guid> trendingItemIds,
        int maxItems)
    {
        var resumableIds = GetResumableItemIds(libraryManager, user);
        var watched = GetAffinityItems(libraryManager, userDataManager, user);
        if (watched.Count == 0)
        {
            return GetColdStartItems(libraryManager, user, resumableIds, trendingItemIds, maxItems);
        }

        var facets = AggregateFacets(libraryManager, watched);
        return ScoreCandidates(libraryManager, userDataManager, user, facets, resumableIds, trendingItemIds, maxItems);
    }

    private static HashSet<Guid> GetResumableItemIds(ILibraryManager libraryManager, User user)
    {
        return libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsResumable = true
        })
        .Select(static i => i.Id)
        .ToHashSet();
    }

    private static List<BaseItem> GetAffinityItems(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        User user)
    {
        var played = libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsPlayed = true,
            Limit = MaxAffinityItems
        });

        var resumable = libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsResumable = true,
            Limit = MaxAffinityItems
        });

        return played.Concat(resumable).GroupBy(static i => i.Id).Select(static g => g.First()).ToList();
    }

    private static Facets AggregateFacets(ILibraryManager libraryManager, List<BaseItem> watched)
    {
        var genreWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tagWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var studioWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var personWeights = new Dictionary<Guid, int>();

        foreach (var item in watched)
        {
            if (item.Genres is { Length: > 0 })
            {
                foreach (var genre in item.Genres)
                {
                    genreWeights[genre] = genreWeights.GetValueOrDefault(genre) + 1;
                }
            }

            if (item.Tags is { Length: > 0 })
            {
                foreach (var tag in item.Tags)
                {
                    tagWeights[tag] = tagWeights.GetValueOrDefault(tag) + 1;
                }
            }

            if (item.Studios is { Length: > 0 })
            {
                foreach (var studio in item.Studios)
                {
                    studioWeights[studio] = studioWeights.GetValueOrDefault(studio) + 1;
                }
            }

            foreach (var person in libraryManager.GetPeople(item))
            {
                if (person.Id == Guid.Empty)
                {
                    continue;
                }

                if (person.Type is PersonKind.Actor or PersonKind.Director or PersonKind.Writer)
                {
                    personWeights[person.Id] = personWeights.GetValueOrDefault(person.Id) + 1;
                }
            }
        }

        return new Facets
        {
            Genres = genreWeights.OrderByDescending(static kv => kv.Value).Take(TopGenres).Select(static kv => kv.Key).ToList(),
            Tags = tagWeights.OrderByDescending(static kv => kv.Value).Take(TopTags).Select(static kv => kv.Key).ToList(),
            PersonIds = personWeights.OrderByDescending(static kv => kv.Value).Take(TopPeople).Select(static kv => kv.Key).ToList(),
            GenreWeights = genreWeights,
            TagWeights = tagWeights,
            StudioWeights = studioWeights,
            PersonWeights = personWeights
        };
    }

    private static List<Guid> ScoreCandidates(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        User user,
        Facets facets,
        HashSet<Guid> resumableIds,
        IReadOnlySet<Guid> trendingItemIds,
        int maxItems)
    {
        var candidates = new Dictionary<Guid, BaseItem>();

        if (facets.Genres.Count > 0)
        {
            AddCandidates(libraryManager, candidates, new InternalItemsQuery
            {
                User = user,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                IsPlayed = false,
                Genres = facets.Genres,
                Limit = MaxCandidatesPerFacet
            });
        }

        if (facets.PersonIds.Count > 0)
        {
            AddCandidates(libraryManager, candidates, new InternalItemsQuery
            {
                User = user,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                IsPlayed = false,
                PersonIds = facets.PersonIds.ToArray(),
                Limit = MaxCandidatesPerFacet
            });
        }

        if (facets.Tags.Count > 0)
        {
            AddCandidates(libraryManager, candidates, new InternalItemsQuery
            {
                User = user,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                IsPlayed = false,
                Tags = facets.Tags.ToArray(),
                Limit = MaxCandidatesPerFacet
            });
        }

        var scored = new List<(BaseItem Item, double Score)>(candidates.Count);
        foreach (var item in candidates.Values)
        {
            var people = libraryManager.GetPeople(item);
            var facetScore = ComputeFacetScore(item, people, facets);
            var quality = Math.Clamp((item.CommunityRating ?? 0) / 10.0, 0.0, 1.0);
            var recency = item.PremiereDate is { } premiere && premiere > DateTime.UtcNow.AddYears(-3)
                ? RecencyBonus
                : 0.0;

            scored.Add((item, (facetScore * FacetWeight) + (quality * QualityWeight) + recency));
        }

        var ranked = scored
            .OrderByDescending(static x => x.Score)
            .ThenByDescending(static x => x.Item.CommunityRating ?? 0)
            .Select(static x => x.Item)
            .Where(i => !resumableIds.Contains(i.Id))
            .Where(i => !trendingItemIds.Contains(i.Id))
            .DistinctBy(GetDedupeKey);

        return SelectDiverse(ranked, maxItems);
    }

    private static double ComputeFacetScore(BaseItem item, IReadOnlyList<PersonInfo> people, Facets facets)
    {
        double numerator = 0;
        double denominator = 0;

        if (facets.Genres.Count > 0)
        {
            var maxWeight = (double)facets.Genres.Max(g => facets.GenreWeights[g]);
            var hit = 0.0;
            if (item.Genres is { Length: > 0 })
            {
                foreach (var genre in item.Genres)
                {
                    if (facets.Genres.Contains(genre))
                    {
                        hit += facets.GenreWeights[genre];
                    }
                }
            }

            numerator += Math.Min(1.0, hit / maxWeight);
            denominator += 1;
        }

        if (facets.Tags.Count > 0)
        {
            var maxWeight = (double)facets.Tags.Max(t => facets.TagWeights[t]);
            var hit = 0.0;
            if (item.Tags is { Length: > 0 })
            {
                foreach (var tag in item.Tags)
                {
                    if (facets.Tags.Contains(tag))
                    {
                        hit += facets.TagWeights[tag];
                    }
                }
            }

            numerator += Math.Min(1.0, hit / maxWeight);
            denominator += 1;
        }

        if (facets.PersonIds.Count > 0)
        {
            var maxWeight = (double)facets.PersonIds.Max(p => facets.PersonWeights[p]);
            var hit = 0.0;
            foreach (var person in people)
            {
                if (facets.PersonIds.Contains(person.Id))
                {
                    hit += facets.PersonWeights[person.Id];
                }
            }

            numerator += Math.Min(1.0, hit / maxWeight);
            denominator += 1;
        }

        if (facets.StudioWeights.Count > 0)
        {
            var maxWeight = facets.StudioWeights.Values.Prepend(0).Max();
            if (maxWeight > 0)
            {
                var hit = 0.0;
                if (item.Studios is { Length: > 0 })
                {
                    foreach (var studio in item.Studios)
                    {
                        hit += facets.StudioWeights.GetValueOrDefault(studio);
                    }
                }

                numerator += Math.Min(1.0, hit / maxWeight);
                denominator += 1;
            }
        }

        return denominator > 0 ? numerator / denominator : 0;
    }

    private static List<Guid> SelectDiverse(IEnumerable<BaseItem> ranked, int maxItems)
    {
        var result = new List<Guid>(maxItems);
        var franchiseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in ranked)
        {
            var key = FranchiseKey(item);
            if (franchiseCounts.GetValueOrDefault(key) >= MaxPerFranchise)
            {
                continue;
            }

            franchiseCounts[key] = franchiseCounts.GetValueOrDefault(key) + 1;
            result.Add(item.Id);

            if (result.Count >= maxItems)
            {
                break;
            }
        }

        return result;
    }

    private static string FranchiseKey(BaseItem item)
    {
        var name = item.Name?.ToLowerInvariant() ?? string.Empty;
        if (name.Length == 0)
        {
            return item.Id.ToString("N");
        }

        name = TrailingYearRegex.Replace(name, string.Empty);
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            name = name[..colon];
        }

        name = TrailingRomanRegex.Replace(name, string.Empty);
        name = TrailingDigitRegex.Replace(name, string.Empty);
        return name.Trim();
    }

    private static string GetDedupeKey(BaseItem item)
        => item.ProviderIds.TryGetValue("Tmdb", out var tmdb) && !string.IsNullOrWhiteSpace(tmdb)
            ? "tmdb:" + tmdb
            : "item:" + item.Id.ToString("N");

    private static List<Guid> GetColdStartItems(
        ILibraryManager libraryManager,
        User user,
        HashSet<Guid> resumableIds,
        IReadOnlySet<Guid> trendingItemIds,
        int maxItems)
    {
        var random = libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsPlayed = false,
            OrderBy = [(ItemSortBy.Random, SortOrder.Descending)],
            Limit = ColdStartSample
        });

        return random
            .Select(static i => i.Id)
            .Where(id => !resumableIds.Contains(id))
            .Where(id => !trendingItemIds.Contains(id))
            .Take(maxItems)
            .ToList();
    }

    private static void AddCandidates(
        ILibraryManager libraryManager,
        Dictionary<Guid, BaseItem> candidates,
        InternalItemsQuery query)
    {
        foreach (var item in libraryManager.GetItemList(query))
        {
            candidates.TryAdd(item.Id, item);
        }
    }

    private sealed class Facets
    {
        public List<string> Genres { get; init; } = new();

        public List<string> Tags { get; init; } = new();

        public List<Guid> PersonIds { get; init; } = new();

        public Dictionary<string, int> GenreWeights { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> TagWeights { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> StudioWeights { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<Guid, int> PersonWeights { get; init; } = new();
    }
}
