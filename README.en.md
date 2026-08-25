<div align="center">

![JellyTrend Logo](Jellyfin.Plugin.JellyTrend/Resources/logo.png)

🌐 &nbsp;**English**&nbsp; · &nbsp;[**Español**](README.md)

<br>

# 🎬 JellyTrend

**Jellyfin plugin** that syncs trending movies from TMDB with your local library
and delivers a Netflix-style banner carousel on the home screen.

<br>

[![Last Commit](https://img.shields.io/github/last-commit/BORNIOS/JellyTrend?style=flat-square&color=00A4DC)](https://github.com/BORNIOS/JellyTrend/commits/main)
[![Commit Activity](https://img.shields.io/github/commit-activity/m/BORNIOS/JellyTrend?style=flat-square&color=00A4DC)](https://github.com/BORNIOS/JellyTrend/graphs/commit-activity)
[![CI Build](https://img.shields.io/github/actions/workflow/status/BORNIOS/JellyTrend/build.yaml?style=flat-square&color=00A4DC&label=CI)](https://github.com/BORNIOS/JellyTrend/actions/workflows/build.yaml)
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
- 🎯 **Personalized recommendations** per user (the “Recomendados” row), prioritizing the best TMDB ratings and hiding what you already watched
- 📚 **Season navigation** in the channels (series → season → episode)
- 🛡️ **100 % local playback** — TMDB only drives the trending list; your server is always the source
- 🔍 **Enriched metadata** — genres, cast, studios, tags, and parental rating pulled from your library item

---

## ⚙️ Requirements

| | |
|---|---|
| **Server** | Jellyfin compatible with `Jellyfin.Controller` 10.11.x |
| **TMDB** | Free API key from [themoviedb.org](https://www.themoviedb.org/settings/api) |

---

## 🚀 Installation

### Option A — From the repository (recommended)

1. In Jellyfin go to **Dashboard → Advanced → Plugin repositories**.
2. Click **Add repository** and use the manifest URL:

   ```
   https://raw.githubusercontent.com/BORNIOS/JellyTrend/main/manifest.json
   ```

3. Save and go to **Catalog**, search for **JellyTrend** and **Install**.
4. Configure your TMDB key in **Dashboard → Plugins → JellyTrend**.

### Option B — Manual

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

| Section | Parameter | Description |
|---|---|---|
| **TMDB** | API key | Your The Movie Database API key |
| **TMDB** | Language / Region | Filter trends by locale |
| **Trending** | Enable channel | Show the trending channel under *Channels* |
| **Trending** | Channel name | How it appears in the Channels section |
| **Trending** | Max items | How many trending titles are kept |
| **Trending** | Interval (hours) | How often the list refreshes from TMDB |
| **Trending** | Carousel | Show the banner on the home screen |
| **Recommendations** | Enable row | Show the per-user “Recomendados” row |
| **Recommendations** | Channel name | How it appears in the Channels section |
| **Recommendations** | Max per user | How many recommendations are generated per user |
| **Recommendations** | Interval (hours) | How often recommendations are rebuilt |

<br>

<p align="center">
  <img alt="JellyTrend Settings 1" src="Screenshots/Settings1.png" width="45%" />
  &nbsp;
  <img alt="JellyTrend Settings 2" src="Screenshots/Settings2.png" width="45%" />
</p>

---

## 📺 Trending Channel

The channel appears in Jellyfin's **Channels** section with the same trending movies, ready to play directly from your library. **Series** are browsed by **season** (series → season → episode) so you can start watching from whichever season you want.

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
3. After each TMDB sync (and shortly after server startup), **`TrendingShadowMetadataSync`** pushes cast and metadata into the shadow row in the database (`UpdatePeopleAsync` and text fields), and references the library movie's image files directly — because Jellyfin does not re-apply cast to existing channel shadows from `ChannelItemInfo` alone.

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

The project follows the layout of the [official Jellyfin plugin template](https://github.com/jellyfin/jellyfin-plugin-template):
the source code lives in `Jellyfin.Plugin.JellyTrend/` and the root holds the repository
manifests, the analyzers configuration and the CI workflows.

```bash
# Build the plugin (Release)
dotnet build Jellyfin.Plugin.JellyTrend.sln -c Release

# Publish a standalone plugin output
dotnet publish Jellyfin.Plugin.JellyTrend/Jellyfin.Plugin.JellyTrend.csproj -c Release -o publish
```

Copy the generated `Jellyfin.Plugin.JellyTrend.dll` into your Jellyfin plugins folder
(e.g. `%APPDATA%\Jellyfin\plugins\Jellyfin.Plugin.JellyTrend\`) and restart the server.

### Debugging with VS Code

The repo includes a `.vscode/` setup that builds the plugin, copies it into your Jellyfin
plugins folder and lets you debug against a running server. **Local paths are configured once
in a `.env` file** (copy `.env.example` to `.env` and adjust):

```dotenv
JELLYFIN_SERVER_DIR=D:\jellyfin-portable          # where jellyfin.exe / jellyfin.dll live
JELLYFIN_WEB_DIR=D:\jellyfin-portable\jellyfin-web
JELLYFIN_DATA_DIR=C:\Users\<your-user>\AppData\Local\jellyfin
```

Workflow:

1. **Stop Jellyfin** (on Windows the DLL is locked while the server runs).
2. Run the VS Code **`build-and-copy`** task: builds in Debug and copies `DLL + PDB` to `$JELLYFIN_DATA_DIR/plugins/$PLUGIN_NAME/`.
3. Run the **`start-server`** task: starts your portable Jellyfin.
4. Press **F5** → **Attach to Jellyfin** and pick the `jellyfin` process.

The VS Code tasks (`build`, `build-and-copy`, `start-server`, `dev-info`) load the `.env`
automatically; use **`dev-info`** to verify the paths exist.

### Code quality

The project enables the Jellyfin analyzers (StyleCop, Serilog, Multithreading) with
warnings-as-errors. Keep the build clean:

```bash
dotnet build Jellyfin.Plugin.JellyTrend.sln -c Release   # must report 0 warnings / 0 errors
```

### Publishing a release

1. Make sure `main` is green (CI `build.yaml`).
2. Go to **Actions → 📦 Release Plugin → Run workflow** and enter the version (e.g. `2.0.0`).
3. The workflow: pins the version in `Directory.Build.props`, builds, produces the `ZIP` + `checksum`,
   **updates and commits `manifest.json` and `build.yaml` back to `main`**, and creates the **GitHub
   Release with an auto-generated changelog** (from PRs/commits), attaching the ZIP and the manifest.
4. The new version becomes installable from the repository inside Jellyfin.

> Jellyfin reads the repository directly from the manifest URL
> (`https://raw.githubusercontent.com/BORNIOS/JellyTrend/main/manifest.json`); the root
> `manifest.json` is kept up to date automatically with every release.

---

## 🤝 Community

Found a bug or have a suggestion? Open an [issue](https://github.com/BORNIOS/JellyTrend/issues) or join the official Jellyfin community:

[![Discord](https://img.shields.io/badge/Discord-Join_the_community-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.jellyfin.org)
[![Reddit](https://img.shields.io/badge/Reddit-r%2Fjellyfin-FF4500?style=for-the-badge&logo=reddit&logoColor=white)](https://www.reddit.com/r/jellyfin)

---

<div align="center">

Made with ❤️ for the Jellyfin community &nbsp;·&nbsp; [⭐ Star on GitHub](https://github.com/BORNIOS/JellyTrend)

</div>
