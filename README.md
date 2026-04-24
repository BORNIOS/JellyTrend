<div align="center">

🌐 &nbsp;[**English**](README.en.md)&nbsp; · &nbsp;**Español**

<br>

# 🎬 JellyTrend

**Plugin para Jellyfin** que sincroniza las películas en tendencia de TMDB con tu biblioteca local  
y ofrece un carrusel estilo Netflix en la pantalla de inicio.

<br>

[![Last Commit](https://img.shields.io/github/last-commit/BORNIOS/JellyTrend?style=flat-square&color=00A4DC&label=último%20commit)](https://github.com/BORNIOS/JellyTrend/commits/main)
[![Commit Activity](https://img.shields.io/github/commit-activity/m/BORNIOS/JellyTrend?style=flat-square&color=00A4DC&label=commits%2Fmes)](https://github.com/BORNIOS/JellyTrend/graphs/commit-activity)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11.x-00A4DC?style=flat-square&logo=jellyfin&logoColor=white)](https://jellyfin.org)
[![Downloads](https://img.shields.io/github/downloads/BORNIOS/JellyTrend/total?style=flat-square&color=00A4DC&label=descargas)](https://github.com/BORNIOS/JellyTrend/releases)

[![Discord](https://img.shields.io/badge/Discord-Comunidad_Jellyfin-5865F2?style=flat-square&logo=discord&logoColor=white)](https://discord.jellyfin.org)
[![Reddit](https://img.shields.io/badge/Reddit-r%2Fjellyfin-FF4500?style=flat-square&logo=reddit&logoColor=white)](https://www.reddit.com/r/jellyfin)
[![License](https://img.shields.io/github/license/BORNIOS/JellyTrend?style=flat-square&color=555)](LICENSE)

</div>

---

<br>

![Banner JellyTrend](Screenshots/Banner.png)

<br>

## ✨ Características

- 🎥 **Carrusel estilo Netflix** en la pantalla de inicio de Jellyfin Web
- 📡 **Canal dedicado** en la sección *Canales* con las películas en tendencia y listas para reproducir
- 🔄 **Sincronización bidireccional** con tu biblioteca: estado visto, progreso de reproducción, favoritos y valoración
- 🛡️ **Reproducción 100 % local** — TMDB solo alimenta la lista de tendencias; la fuente siempre es tu servidor
- 🔍 **Metadatos enriquecidos** — géneros, reparto, estudios y clasificación tomados del ítem de biblioteca

---

## ⚙️ Requisitos

| | |
|---|---|
| **Servidor** | Jellyfin compatible con `Jellyfin.Controller` 10.11.x |
| **TMDB** | Clave de API gratuita en [themoviedb.org](https://www.themoviedb.org/settings/api) |

---

## 🚀 Instalación

1. Descarga la última versión desde [**Releases**](https://github.com/BORNIOS/JellyTrend/releases).
2. Copia el archivo `.dll` en el directorio de plugins de tu instalación de Jellyfin.
3. Reinicia Jellyfin.
4. Ve a **Panel → Plugins → JellyTrend** y configura tu clave de TMDB.

> 💡 Ubicaciones comunes del directorio de plugins:
> - **Linux / Docker:** `/config/plugins/`
> - **Windows:** `%APPDATA%\Jellyfin\plugins\`

---

## 🛠️ Configuración

Desde la página del plugin puedes ajustar:

| Parámetro | Descripción |
|---|---|
| **Clave TMDB** | Tu API key de The Movie Database |
| **Idioma / Región** | Para filtrar tendencias por localización |
| **Nombre del canal** | Cómo aparecerá en la sección Canales |
| **Canal activo** | Activa o desactiva el canal de tendencias |
| **Máximo de ítems** | Cuántas películas se sincronizan por ciclo |
| **Intervalo de sync** | Con qué frecuencia se actualiza la lista |

<br>

![Configuración JellyTrend](Screenshots/Settings.png)

---

## 📺 Canal de tendencias

El canal aparece en la sección **Canales** de Jellyfin con las mismas películas en tendencia, listas para reproducir directamente desde tu biblioteca.

<br>

![Canal JellyTrend](Screenshots/Channel.png)

---

## 🔄 Sincronía biblioteca ↔ canal

Jellyfin crea un **ítem sombra** por cada entrada del canal con su propio `Id` interno. JellyTrend mantiene ambos alineados:

- Si marcas una película como **vista en tu biblioteca**, el canal lo reflejará automáticamente.
- Si la reproduces **desde el canal**, la biblioteca queda igualmente actualizada.
- **Continuar viendo** usa siempre el ítem de biblioteca como referencia canónica — el canal no guarda progreso parcial para evitar duplicados en esa sección.

<details>
<summary>🔧 Detalles técnicos de sincronización</summary>

<br>

1. **Replica datos de usuario** (visto, posición, favoritos, valoración) entre el ítem de biblioteca y el ítem sombra del canal, vía eventos de reproducción y el pipeline de guardado del servidor.
2. **Enriquece `ChannelItemInfo`** con géneros, estudios, reparto, fechas y clasificación, tomados del ítem de biblioteca.
3. Tras cada sync TMDB (y tras el arranque del servidor), **`TrendingShadowMetadataSync`** vuelca reparto y metadatos al ítem sombra en base de datos (`UpdatePeopleAsync`, campos de texto y `UpdateImagesAsync`), porque Jellyfin no reaplica el reparto a sombras ya existentes solo con el canal.

**Nota sobre imágenes del canal:** el modelo de canales expone una URL principal por ítem. El carrusel web (`/JellyTrend/Trending`) usa `/Items/{id}/Images/...` con el Id de la biblioteca, por lo que backdrop, logo, etc., aparecen cuando existen en tu biblioteca.

</details>

---

## 🌐 API interna

| Endpoint | Descripción |
|---|---|
| `GET /JellyTrend/Trending` | Lista en tendencia con géneros, actores y estado de reproducción |
| `GET /JellyTrend/Status` | Resumen de configuración y caché |
| `GET /JellyTrend/jellyTrend.js` | Script del carrusel |
| `GET /JellyTrend/jellyTrend.css` | Estilos del carrusel |

---

## 🧑‍💻 Desarrollo

```bash
dotnet build
```

Copia el ensamblado generado al directorio de plugins de Jellyfin según tu instalación y reinicia el servidor.

---

## 🤝 Comunidad

¿Dudas, sugerencias o has encontrado un bug? Abre un [issue](https://github.com/BORNIOS/JellyTrend/issues) o únete a la comunidad oficial de Jellyfin:

[![Discord](https://img.shields.io/badge/Discord-Únete_a_la_comunidad-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.jellyfin.org)
[![Reddit](https://img.shields.io/badge/Reddit-r%2Fjellyfin-FF4500?style=for-the-badge&logo=reddit&logoColor=white)](https://www.reddit.com/r/jellyfin)

---

<div align="center">

Hecho con ❤️ para la comunidad Jellyfin &nbsp;·&nbsp; [⭐ Star en GitHub](https://github.com/BORNIOS/JellyTrend)

</div>