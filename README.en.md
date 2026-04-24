# JellyTrend

**Jellyfin** plugin that pulls trending movies from **TMDB**, matches them to your local library, and provides:

- A **Netflix-style banner carousel** in the web UI (when enabled).
- A **channel** under *Channels* with the same playable titles.

The **actual playback source** is always your Jellyfin server: files come from your library. TMDB only drives the trending list and discovery metadata.

## Requirements

- A Jellyfin server version compatible with the `Jellyfin.Controller` package referenced in the `.csproj` (e.g. 10.11.x).
- A TMDB API key (plugin settings).

## Settings

From the plugin page you can configure, among others:

- TMDB key, language, and region.
- Channel name and whether the channel is enabled.
- Maximum items and sync interval.

## Library ↔ channel sync

Jellyfin creates a **shadow `BaseItem`** for each channel entry with its own internal `Id`. The link to your real library movie is stored in `ExternalId` (the library item `Guid`). That is why watch state and resume position used to diverge from the library.

**JellyTrend** now:

1. **Mirrors user data** (played, resume position, favorite, rating, etc.) between the library item and the channel shadow item, using playback events and the server’s user-data save pipeline. **Resume ticks** are treated as canonical on the **library** item only: the shadow does not keep a partial resume position, so **Continue watching** does not list the same title twice (library + channel).
2. **Enriches `ChannelItemInfo`** with genres, studios, cast, dates, and parental rating from the same library item.
3. After each **TMDB sync** (and shortly after server startup), **`TrendingShadowMetadataSync`** pushes cast and metadata into the shadow row in the database (`UpdatePeopleAsync`, text fields, and `UpdateImagesAsync`), because Jellyfin does not re-apply cast to existing channel shadows from `ChannelItemInfo` alone.

So if you mark a movie played under *Movies*, the channel can reflect it (after save/refresh depending on the client), and if you watch from the channel, the library stays in sync. To resume mid-playback, use **Continue watching** on the **library** entry (the channel shadow intentionally does not store partial progress).

### Channel UI image limits

The channel item model exposes **one primary image URL** per entry; logos, disc art, or extra backdrops depend on how each client resolves images for the shadow item. The web carousel (`/JellyTrend/Trending`) uses `/Items/{id}/Images/...` with the **library** `Id`, so backdrop, logo, etc. appear there whenever they exist in your library.

## Internal API

- `GET /JellyTrend/Trending` — trending list with genres, cast, and play state for the authenticated user.
- `GET /JellyTrend/Status` — configuration and cache summary.
- `GET /JellyTrend/jellyTrend.js` / `jellyTrend.css` — carousel assets.

## Development

```bash
dotnet build
```

Copy the plugin assembly into your Jellyfin plugins folder according to your install layout.

---

*Spanish documentation (default): [README.md](README.md).*
