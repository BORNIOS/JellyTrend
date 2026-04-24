<div align="center">

🌐 &nbsp;**English**&nbsp; · &nbsp;[**Español**](README.md)

<br>

# 🎬 JellyTrend

**Jellyfin plugin** that syncs trending movies from TMDB with your local library  
and delivers a Netflix-style banner carousel on the home screen.

<br>

[![Last Commit](https://img.shields.io/github/last-commit/BORNIOS/JellyTrend?style=flat-square&color=00A4DC)](https://github.com/BORNIOS/JellyTrend/commits/main)
[![Commit Activity](https://img.shields.io/github/commit-activity/m/BORNIOS/JellyTrend?style=flat-square&color=00A4DC)](https://github.com/BORNIOS/JellyTrend/graphs/commit-activity)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11.x-00A4DC?style=flat-square&logo=jellyfin&logoColor=white)](https://jellyfin.org)
[![Downloads](https://img.shields.io/github/downloads/BORNIOS/JellyTrend/total?style=flat-square&color=00A4DC)](https://github.com/BORNIOS/JellyTrend/releases)

[![Discord](https://img.shields.io/badge/Discord-Jellyfin_Community-5865F2?style=flat-square&logo=discord&logoColor=white)](https://discord.jellyfin.org)
[![Reddit](https://img.shields.io/badge/Reddit-r%2Fjellyfin-FF4500?style=flat-square&logo=reddit&logoColor=white)](https://www.reddit.com/r/jellyfin)
[![License](https://img.shields.io/github/license/BORNIOS/JellyTrend?style=flat-square&color=555)](LICENSE)

</div>

---

<br>

![JellyTrend Banner](Screenshots/Banner.png)

<br>

## ✨ Features

- 🎥 **Netflix-style banner carousel** on the Jellyfin Web home screen
- 📡 **Dedicated channel** under *Channels* with the same trending titles, ready to play
- 🔄 **Two-way sync** with your library: watched state, resume position, favorites, and ratings
- 🛡️ **100 % local playback** — TMDB only drives the trending list; your server is always the source
- 🔍 **Enriched metadata** — genres, cast, studios, and parental rating pulled from your library item

---

## ⚙️ Requirements

| | |
|---|---|
| **Server** | Jellyfin compatible with `Jellyfin.Controller` 10.11.x |
| **TMDB** | Free API key from [themoviedb.org](https://www.themoviedb.org/settings/api) |

---

## 🚀 Installation

1. Download the latest release from [**Releases**](https://github.com/BORNIOS/JellyTrend/releases).
2. Copy the `.dll` file into your Jellyfin plugins directory.
3. Restart Jellyfin.
4. Go to **Dashboard → Plugins → JellyTrend** and enter your TMDB API key.

> 💡 Common plugin directory locations:
> - **Linux / Docker:** `/config/plugins/`
> - **Windows:** `%APPDATA%\Jellyfin\plugins\`

---

## 🛠️ Settings

From the plugin page you can configure:

| Parameter | Description |
|---|---|
| **TMDB Key** | Your The Movie Database API key |
| **Language / Region** | Filter trends by locale |
| **Channel name** | How it appears in the Channels section |
| **Channel enabled** | Toggle the trending channel on or off |
| **Max items** | How many movies are synced per cycle |
| **Sync interval** | How often the trending list refreshes |

<br>

![JellyTrend Settings](Screenshots/Settings.png)

---

## 📺 Trending Channel

The channel appears in Jellyfin's **Channels** section with the same trending movies, ready to play directly from your library.

<br>

![JellyTrend Channel](Screenshots/Channel.png)

---

## 🔄 Library ↔ Channel sync

Jellyfin creates a **shadow item** for each channel entry with its own internal `Id`. JellyTrend keeps both in sync:

- Mark a movie **watched in your library** → the channel reflects it automatically.
- Watch it **from the channel** → the library is updated too.
- **Continue watching** always uses the library item as the canonical source — the channel shadow intentionally does not store partial progress to avoid duplicate entries in that section.

<details>
<summary>🔧 Technical sync details</summary>

<br>

1. **Mirrors user data** (played, resume position, favorite, rating, etc.) between the library item and the channel shadow, via playback events and the server's user-data save pipeline.
2. **Enriches `ChannelItemInfo`** with genres, studios, cast, dates, and parental rating from the same library item.
3. After each TMDB sync (and shortly after server startup), **`TrendingShadowMetadataSync`** pushes cast and metadata into the shadow row in the database (`UpdatePeopleAsync`, text fields, and `UpdateImagesAsync`), because Jellyfin does not re-apply cast to existing channel shadows from `ChannelItemInfo` alone.

**Channel image note:** the channel item model exposes one primary image URL per entry. The web carousel (`/JellyTrend/Trending`) uses `/Items/{id}/Images/...` with the library `Id`, so backdrop, logo, etc. appear whenever they exist in your library.

</details>

---

## 🌐 Internal API

| Endpoint | Description |
|---|---|
| `GET /JellyTrend/Trending` | Trending list with genres, cast, and play state for the authenticated user |
| `GET /JellyTrend/Status` | Configuration and cache summary |
| `GET /JellyTrend/jellyTrend.js` | Carousel script |
| `GET /JellyTrend/jellyTrend.css` | Carousel styles |

---

## 🧑‍💻 Development

```bash
dotnet build
```

Copy the generated assembly into your Jellyfin plugins folder and restart the server.

---

## 🤝 Community

Found a bug or have a suggestion? Open an [issue](https://github.com/BORNIOS/JellyTrend/issues) or join the official Jellyfin community:

[![Discord](https://img.shields.io/badge/Discord-Join_the_community-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.jellyfin.org)
[![Reddit](https://img.shields.io/badge/Reddit-r%2Fjellyfin-FF4500?style=for-the-badge&logo=reddit&logoColor=white)](https://www.reddit.com/r/jellyfin)

---

<div align="center">

Made with ❤️ for the Jellyfin community &nbsp;·&nbsp; [⭐ Star on GitHub](https://github.com/BORNIOS/JellyTrend)

</div>