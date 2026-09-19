using System;
using System.Collections.Generic;
using System.Linq;

using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTrend.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Services.Backend;

/// <summary>
/// Resolves TMDB ids to local library items, using the database provider index when it is available.
/// </summary>
/// <remarks>
/// The trending sync used to run one library query per TMDB id (about 120 queries per run at the
/// configured size). When the database provider offers the index, the same mapping costs one query per
/// item kind. The per-id path stays as the fallback: trending must keep working with or without a provider.
/// </remarks>
internal sealed class LibraryIndexResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryIndexQueryProvider? _index;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryIndexResolver"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager used for the fallback lookups.</param>
    /// <param name="index">Database provider index, or null when there is none.</param>
    /// <param name="logger">Logger.</param>
    public LibraryIndexResolver(ILibraryManager libraryManager, ILibraryIndexQueryProvider? index, ILogger logger)
    {
        _libraryManager = libraryManager;
        _index = index;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether the resolver can use the database provider index.
    /// </summary>
    public bool UsingIndex => _index is not null;

    /// <summary>
    /// Resolves the given TMDB ids to local items of the requested kind.
    /// </summary>
    /// <param name="tmdbIds">TMDB ids to resolve.</param>
    /// <param name="kind">Kind of the items those ids belong to.</param>
    /// <returns>TMDB id to local item, only for the ids that match a real library item.</returns>
    public Dictionary<string, BaseItem> Resolve(IReadOnlyList<string> tmdbIds, BaseItemKind kind)
    {
        var matches = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
        if (tmdbIds.Count == 0)
        {
            return matches;
        }

        var queries = 0;

        if (_index is not null)
        {
            try
            {
                queries++;
                foreach (var pair in _index.GetItemIdsByTmdbIds(tmdbIds, kind))
                {
                    var item = Validate(_libraryManager.GetItemById(pair.Value), kind);
                    if (item is not null)
                    {
                        matches[pair.Key] = item;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Tendencias] El indice del proveedor de base de datos fallo; se resuelve id por id.");
                matches.Clear();
            }
        }

        if (matches.Count == 0)
        {
            // Tambien se llega aqui cuando el indice contesta vacio: puede ser correcto (nada de TMDB esta
            // en la biblioteca) o pode ser que sus consultas no encuentren nada. En ambos casos la lista de
            // tendencias no debe quedar vacia, asi que se comprueba id por id.
            if (_index is not null)
            {
                _logger.LogWarning("[Tendencias] El indice del proveedor no resolvio ninguno de los {Ids} ids; se comprueba id por id.", tmdbIds.Count);
            }

            foreach (var tmdbId in tmdbIds.Where(static id => !string.IsNullOrWhiteSpace(id)))
            {
                queries++;
                var item = FindByTmdbId(tmdbId, kind);
                if (item is not null)
                {
                    matches[tmdbId] = item;
                }
            }
        }

        _logger.LogInformation(
            "[Tendencias] Resolucion {Kind}: {Matches} de {Ids} ids locales con {Queries} consultas ({Path}).",
            kind,
            matches.Count,
            tmdbIds.Count,
            queries,
            _index is null ? "sin indice del proveedor" : "con indice del proveedor");

        return matches;
    }

    private static BaseItem? Validate(BaseItem? item, BaseItemKind kind)
    {
        if (item is null || item.IsVirtualItem || item.ChannelId != Guid.Empty || !MatchesKind(item, kind))
        {
            return null;
        }

        return item;
    }

    private static bool MatchesKind(BaseItem item, BaseItemKind kind)
        => kind switch
        {
            BaseItemKind.Movie => item is Movie,
            BaseItemKind.Series => item is Series,
            _ => true
        };

    private BaseItem? FindByTmdbId(string tmdbId, BaseItemKind kind)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Tmdb"] = tmdbId },
            IncludeItemTypes = [kind],
            IsVirtualItem = false,
            Limit = 10
        });

        foreach (var item in items)
        {
            // Excluir sombras de canal (copian el Tmdb provider id y no se marcan como virtuales)
            // y validar que el tipo coincida: el provider puede ignorar IncludeItemTypes en algunas
            // combinaciones de filtros.
            var valid = Validate(item, kind);
            if (valid is not null)
            {
                return valid;
            }
        }

        return null;
    }
}
