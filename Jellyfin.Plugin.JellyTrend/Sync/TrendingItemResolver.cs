using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyTrend.Sync;

/// <summary>
/// Resolves the CURRENT library item behind a trending entry.
///
/// <para>
/// DESIGN: a library item GUID is NOT a stable identifier in Jellyfin. A rescan, a re-import, a
/// path change or a database migration (e.g. SQLite → Postgres) can replace an item with a brand
/// new GUID while keeping the same TMDB provider id. Caches that persist only the library GUID
/// (like <c>trending.json</c>) therefore end up pointing at dead items after the library changes,
/// breaking folder navigation (no seasons) and local image copying.
/// </para>
///
/// <para>
/// The TMDB id is the canonical, stable key. The stored library GUID is used as a fast path only:
/// when it still resolves it is returned directly; otherwise the current library item is re-matched
/// by its TMDB provider id. This makes the plugin resilient to library re-imports without forcing a
/// resync.
/// </para>
/// </summary>
public static class TrendingItemResolver
{
    /// <summary>
    /// Returns the library item that currently represents the given trending entry, re-matching by
    /// TMDB when the stored id is stale.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="itemId">The library id stored in the cache (may be stale).</param>
    /// <param name="tmdbId">The canonical TMDB id, or <c>null</c> when unknown.</param>
    /// <param name="isSeries">Whether the entry is a series (<see langword="true"/>) or a movie.</param>
    /// <returns>The current library item, or <c>null</c> when it cannot be resolved.</returns>
    public static BaseItem? ResolveCurrentItem(
        ILibraryManager libraryManager,
        Guid itemId,
        string? tmdbId,
        bool isSeries)
    {
        var kind = isSeries ? BaseItemKind.Series : BaseItemKind.Movie;

        var byId = libraryManager.GetItemById(itemId);
        if (byId is not null && byId.ChannelId == Guid.Empty && MatchesKind(byId, isSeries))
        {
            return byId;
        }

        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return null;
        }

        // Los items sombra de canal copian el Tmdb provider id de la librería y NO se marcan como
        // virtuales, así que aparecen en esta búsqueda. Solo un item real de librería (ChannelId
        // vacío) del tipo esperado es un match válido; de lo contrario la navegación a temporadas
        // y la copia de imágenes fallarían contra la sombra en lugar de la serie real.
        var matches = libraryManager.GetItemList(new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Tmdb"] = tmdbId },
            IncludeItemTypes = [kind],
            IsVirtualItem = false,
            Limit = 10
        });

        foreach (var match in matches)
        {
            if (match.ChannelId == Guid.Empty && MatchesKind(match, isSeries))
            {
                return match;
            }
        }

        return null;
    }

    private static bool MatchesKind(BaseItem item, bool isSeries)
        => isSeries ? item is Series : item is Movie;
}
