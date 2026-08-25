<div align="center">

![Logo JellyTrend](Jellyfin.Plugin.JellyTrend/Resources/logo.png)

🌐 &nbsp;[**English**](README.en.md)&nbsp; · &nbsp;**Español**

<br>

# 🎬 JellyTrend

**Plugin para Jellyfin** que sincroniza las tendencias de TMDB (películas **y series**) con tu
biblioteca local, muestra un carrusel estilo Netflix en la pantalla de inicio y genera
recomendaciones personalizadas por usuario.

<br>

[![Last Commit](https://img.shields.io/github/last-commit/BORNIOS/JellyTrend?style=flat-square&color=00A4DC&label=último%20commit)](https://github.com/BORNIOS/JellyTrend/commits/main)
[![Commit Activity](https://img.shields.io/github/commit-activity/m/BORNIOS/JellyTrend?style=flat-square&color=00A4DC&label=commits%2Fmes)](https://github.com/BORNIOS/JellyTrend/graphs/commit-activity)
[![CI Build](https://img.shields.io/github/actions/workflow/status/BORNIOS/JellyTrend/build.yaml?style=flat-square&color=00A4DC&label=CI)](https://github.com/BORNIOS/JellyTrend/actions/workflows/build.yaml)
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

- 🎥 **Carrusel estilo Netflix** en la pantalla de inicio de Jellyfin Web, personalizado por usuario (oculta lo que ya viste).
- 📡 **Dos canales** en la sección *Canales*: **Trendings** (tendencias de TMDB que **ya tienes en tu biblioteca**) y **Recomendados** (sugerencias personalizadas por usuario).
- ▶️ **Reproducción 100 % local** — TMDB solo alimenta las listas; la fuente siempre es tu servidor.
- 📚 **Navegación por temporadas** en el canal de **Trendings** (serie → temporada → episodio) para empezar a ver desde la temporada que quieras.
- 🎯 **Recomendaciones personalizadas** por usuario: priorizan las mejores calificaciones de TMDB, limitan sagas (máx. 2 por franquicia) y ocultan lo ya visto / en progreso.
- 🔄 **Sincronización bidireccional** biblioteca ↔ canal: estado visto, progreso, favoritos y valoración.
- 🔍 **Metadatos enriquecidos** — géneros, reparto, estudios, tags y clasificación tomados del ítem de biblioteca (los canales no parecen una «copia»).

---

## ⚙️ Requisitos

| | |
|---|---|
| **Servidor** | Jellyfin compatible con `Jellyfin.Controller` **10.11.x** |
| **TMDB** | Clave de API gratuita en [themoviedb.org](https://www.themoviedb.org/settings/api) |

> ℹ️ El plugin solo muestra contenido que **ya existe en tu biblioteca**. Si no hay coincidencias
> (tendencias que no tengas), la fila del carrusel o el canal pueden verse vacíos. Es normal.

---

## 🚀 Instalación

### Opción A — Desde el repositorio (recomendada)

1. En Jellyfin ve a **Panel → Avanzado → Repositorios de plugins**.
2. Pulsa **Añadir repositorio** y usa la URL del **manifest**:

   ```
   https://raw.githubusercontent.com/BORNIOS/JellyTrend/main/manifest.json
   ```

   > ⚠️ La URL debe apuntar al `manifest.json` (Jellyfin la usa tal cual). Añadir
   > `https://github.com/BORNIOS/JellyTrend` **no funciona**.

3. Guarda y ve a **Catálogo**, busca **JellyTrend** e **Instala**.
4. Configura tu clave de TMDB en **Panel → Plugins → JellyTrend**.
5. Reinicia Jellyfin si lo solicita.

### Opción B — Manual

1. Descarga la última versión desde [**Releases**](https://github.com/BORNIOS/JellyTrend/releases).
2. Copia el archivo `.dll` en el directorio de plugins de tu instalación.
3. Reinicia Jellyfin.
4. Ve a **Panel → Plugins → JellyTrend** y configura tu clave de TMDB.

> 💡 Ubicaciones comunes del directorio de plugins:
> - **Linux / Docker:** `/config/plugins/`
> - **Windows:** `%LOCALAPPDATA%\jellyfin\plugins\`

---

## 🛠️ Configuración

Desde la página del plugin puedes ajustar:

| Sección | Parámetro | Descripción |
|---|---|---|
| **TMDB** | Clave API | Tu API key de The Movie Database (obligatoria) |
| **TMDB** | Idioma / Región | Títulos y sinopsis de TMDB; país para orientar el trending |
| **Trendings** | Activar canal | Muestra el canal de tendencias en *Canales* |
| **Trendings** | Nombre del canal | Cómo aparece en *Canales* (p. ej. «JellyTrend - Trending Now») |
| **Trendings** | Máximo de ítems | Cuántos títulos en tendencia se guardan |
| **Trendings** | Intervalo (horas) | Cada cuánto se actualiza la lista desde TMDB (1–168 h) |
| **Trendings** | Carrusel | Muestra el banner en la página de inicio |
| **Recomendaciones** | Activar fila | Muestra la fila «Recomendados» (canal por usuario) |
| **Recomendaciones** | Nombre del canal | Cómo aparece en *Canales* (p. ej. «Recomendados») |
| **Recomendaciones** | Máximo por usuario | Cuántas recomendaciones se generan por usuario |
| **Recomendaciones** | Intervalo (horas) | Cada cuánto se regeneran (1–720 h; 168 = semanal) |

Además, la página tiene dos botones de acción: **«Sincronizar Trendings ahora»** y
**«Generar Recomendaciones ahora»**, y una sección de **estado** (versión, clave TMDB,
ítems en caché y última sincronización).

<br>

<p align="center">
  <img alt="Configuración JellyTrend 1" src="Screenshots/Settings1.png" width="45%" />
  &nbsp;
  <img alt="Configuración JellyTrend 2" src="Screenshots/Settings2.png" width="45%" />
</p>

---

## 📺 Canales

El plugin registra **dos canales** visibles en *Canales* de todos los clientes (web, móvil,
Roku, Android TV, iOS, etc.):

### 📡 Trendings
- Muestra las películas y series en tendencia de TMDB que **ya tienes en tu biblioteca** (emparejadas por `TMDB id` durante la sincronización). **Nunca muestra contenido que no tengas.**
- **Películas**: reproducibles directamente desde tu biblioteca.
- **Series**: aparecen como carpetas y se navegan por **temporadas** (serie → temporada → episodio), para que elijas desde qué temporada empezar.

### 🎯 Recomendados
- Fila/fuente **por usuario** generada a partir del historial de cada usuario.
- Contenido: **solo películas** (sin series).
- Prioriza las **mejores calificaciones de TMDB**, limita sagas y **oculta lo ya visto y lo en progreso**.

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

1. **Replica datos de usuario** (visto, posición, favoritos, valoración) entre el ítem de biblioteca y el ítem sombra del canal.
2. **Enriquece `ChannelItemInfo`** con géneros, estudios, reparto, tags, fechas y clasificación del ítem de biblioteca.
3. Tras cada sync TMDB (y tras el arranque), **`TrendingShadowMetadataSync`** vuelca reparto y metadatos al ítem sombra (`UpdatePeopleAsync` y campos de texto) y referencia directamente los archivos de imagen de la biblioteca — Jellyfin no reaplica el reparto a sombras existentes solo con el canal.

**Nota sobre imágenes:** el carrusel web (`/JellyTrend/Trending`) usa `/Items/{id}/Images/...` con el `Id` de la biblioteca, por lo que backdrop, logo, etc. aparecen cuando existen en tu biblioteca.

</details>

---

## 📁 Datos y ubicación de archivos

El plugin guarda sus datos dentro del **directorio de datos de Jellyfin** (de aquí en adelante `{DataDir}`). En **Windows** suele ser `%LOCALAPPDATA%\jellyfin\`; en **Linux/Docker**, `/config/`.

| Qué | Ruta |
|---|---|
| Plugin (DLL) | `{DataDir}/plugins/Jellyfin.Plugin.JellyTrend/` |
| Configuración del plugin | `{DataDir}/config/plugins/Jellyfin.Plugin.JellyTrend.xml` |
| Caché de tendencias (trending.json) | `{DataDir}/plugins/Jellyfin.Plugin.JellyTrend/trending.json` |
| **Recomendaciones por usuario** | `{DataDir}/plugins/Jellyfin.Plugin.JellyTrend/recommendations/{userId}.json` |
| Arte/caché de los canales | `{DataDir}/metadata/channels/` |

> 🗑️ **Reiniciar recomendaciones:** borra el archivo `recommendations/{tu-userId}.json`
> (o ejecuta de nuevo «Generar Recomendaciones ahora»).

---

## 🖼️ Personalizar las imágenes de los canales

Por defecto las imágenes de los canales (**Trendings** y **Recomendados**) vienen embebidas en el
plugin. Jellyfin guarda el arte de los canales en `{DataDir}/metadata/channels/`:

- **Windows:** `%LOCALAPPDATA%\jellyfin\metadata\channels\`
- **Linux / Docker:** `/config/metadata/channels/`

Para **personalizar** la imagen de un canal: localiza la carpeta del canal dentro de esa ruta,
**sustituye el archivo de imagen** por el tuyo (mismo nombre) y **reinicia Jellyfin** (o
re-sincroniza el canal desde *Panel → Canales*). También puedes reemplazar directamente los
archivos `channel-trendings.png` y `channel-recommendations.png` del código fuente y recompilar
si prefieres personalizar por defecto.

---

## ❓ Preguntas frecuentes (FAQ)

<details>
<summary><b>¿Por qué el carrusel o el canal no muestran nada?</b></summary>

1. Verifica que la **clave TMDB** esté configurada y que haya conexión.
2. Confirma que la opción **Carrusel** (para el banner) o **Activar canal** estén activadas.
3. El plugin **solo muestra contenido que ya tienes en tu biblioteca**. Si ninguna tendencia de TMDB coincide con tus ítems (emparejadas por `TMDB id`), la lista estará vacía. Ejecuta «Sincronizar Trendings ahora» y refresca.
</details>

<details>
<summary><b>¿Por qué faltan títulos del trending de TMDB?</b></summary>

Por diseño, el plugin **no añade ni muestra contenido que no esté en tu biblioteca**. Solo se
muestran las tendencias que coinciden (películas y series) con ítems locales, emparejadas por
`TMDB id`. Si no tienes el título, no aparece.
</details>

<details>
<summary><b>¿Mis datos salen de mi servidor? / ¿Es privado?</b></summary>

La **reproducción es 100 % local**. TMDB solo se consulta para obtener la **lista de tendencias**
(títulos e ids) y metadatos. Las **recomendaciones se calculan localmente** a partir del historial
de cada usuario y se guardan en tu servidor (`recommendations/{userId}.json`). No se envía tu
historial a ningún servicio externo.
</details>

<details>
<summary><b>¿Cómo funciona con varios usuarios?</b></summary>

El **carrusel y las recomendaciones son por usuario**: cada usuario ve su propio idioma, su
propio estado «visto/en progreso» y sus propias recomendaciones. Las recomendaciones se generan
para **todos** los usuarios en cada ciclo.
</details>

<details>
<summary><b>¿Por qué veo la saga completa recomendada?</b></summary>

El algoritmo limita las recomendaciones a **2 por franquicia** (p. ej. máx. 2 de «Actividad
Paranormal») y prioriza las **mejores calificaciones de TMDB**. Si ves varias de la misma saga,
revisa que tus ítems tengan calificación en la metadata (suele venir en el `.nfo`).
</details>

<details>
<summary><b>¿Cómo reinicio mis recomendaciones?</b></summary>

Borra tu archivo en `{DataDir}/plugins/Jellyfin.Plugin.JellyTrend/recommendations/{userId}.json`
o pulsa **«Generar Recomendaciones ahora»** en la configuración.
</details>

<details>
<summary><b>¿Puedo cambiar la imagen de un canal?</b></summary>

Sí. Sustituye el archivo de imagen en `{DataDir}/metadata/channels/` y reinicia Jellyfin.
Consulta la sección **«Personalizar las imágenes de los canales»**.
</details>

<details>
<summary><b>¿En qué canal está la navegación por temporadas?</b></summary>

Solo en **Trendings** (serie → temporada → episodio). **Recomendados** incluye únicamente
**películas**, por lo que no hay temporadas.
</details>

<details>
<summary><b>¿Cómo actualizo el plugin?</b></summary>

Si lo instalaste desde el **repositorio**, Jellyfin mostrará la actualización automáticamente
(Actividades → actualizaciones o al reiniciar). También puedes descargar el `.dll` desde
[Releases](https://github.com/BORNIOS/JellyTrend/releases) y sustituirlo manualmente.
</details>

<details>
<summary><b>¿Por qué el banner sigue mostrando contenido que ya vi?</b></summary>

El carrusel se actualiza automáticamente (hasta cada ~15 s) y oculta lo ya visto. Si el cambio no
aparece, **recarga la página** o vuelve a la pantalla de inicio. El filtro usa el estado del
usuario autenticado.
</details>

---

## 🌐 API interna

| Endpoint | Descripción |
|---|---|
| `GET /JellyTrend/Trending` | Tendencias emparejadas con géneros, actores y estado de reproducción del usuario |
| `GET /JellyTrend/Status` | Resumen de configuración y caché |
| `GET /JellyTrend/jellyTrend.js` | Script del carrusel |
| `GET /JellyTrend/jellyTrend.css` | Estilos del carrusel |

---

## 🧑‍💻 Desarrollo

El proyecto sigue la estructura del [template oficial de plugins de Jellyfin](https://github.com/jellyfin/jellyfin-plugin-template):
el código fuente vive en `Jellyfin.Plugin.JellyTrend/` y la raíz contiene los manifiestos del
repositorio, la configuración de analyzers y los workflows de CI.

```bash
# Compilar el plugin (Release)
dotnet build Jellyfin.Plugin.JellyTrend.sln -c Release

# Publicar una salida independiente del plugin
dotnet publish Jellyfin.Plugin.JellyTrend/Jellyfin.Plugin.JellyTrend.csproj -c Release -o publish
```

Copia el `Jellyfin.Plugin.JellyTrend.dll` generado al directorio de plugins de Jellyfin
(p. ej. `%LOCALAPPDATA%\jellyfin\plugins\Jellyfin.Plugin.JellyTrend\`) y reinicia el servidor.

### Debugging con VS Code

El repo incluye un setup `.vscode/` con tareas que compilan el plugin, lo copian a tu carpeta de
plugins y permiten depurar con el servidor corriendo. **Las rutas locales se configuran una sola
vez en un archivo `.env`** (copia `.env.example` a `.env` y ajusta):

```dotenv
JELLYFIN_SERVER_DIR=D:\jellyfin-portable          # donde vive jellyfin.exe / jellyfin.dll
JELLYFIN_WEB_DIR=D:\jellyfin-portable\jellyfin-web
JELLYFIN_DATA_DIR=C:\Users\<tu-usuario>\AppData\Local\jellyfin
```

Flujo de trabajo:

1. **Detén Jellyfin** (en Windows el DLL queda bloqueado mientras corre).
2. Ejecuta la tarea de VS Code **`build-and-copy`**: compila en Debug y copia el `DLL + PDB` a `$JELLYFIN_DATA_DIR/plugins/$PLUGIN_NAME/`.
3. Ejecuta la tarea **`start-server`**: arranca tu Jellyfin portable.
4. Pulsa **F5** → **Attach to Jellyfin** y elige el proceso `jellyfin`.

Las tareas de VS Code (`build`, `build-and-copy`, `start-server`, `dev-info`) cargan el `.env`
automáticamente; usa **`dev-info`** para verificar que las rutas existen.

### Calidad de código

El proyecto activa los analyzers de Jellyfin (StyleCop, Serilog, Multithreading) con
warnings-as-errors. Mantén el build limpio:

```bash
dotnet build Jellyfin.Plugin.JellyTrend.sln -c Release   # debe reportar 0 warnings / 0 errores
```

---

## 🤝 Comunidad

¿Dudas, sugerencias o has encontrado un bug? Abre un [issue](https://github.com/BORNIOS/JellyTrend/issues) o únete a la comunidad oficial de Jellyfin:

[![Discord](https://img.shields.io/badge/Discord-Únete_a_la_comunidad-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.jellyfin.org)
[![Reddit](https://img.shields.io/badge/Reddit-r%2Fjellyfin-FF4500?style=for-the-badge&logo=reddit&logoColor=white)](https://www.reddit.com/r/jellyfin)

---

<div align="center">

Hecho con ❤️ para la comunidad Jellyfin &nbsp;·&nbsp; [⭐ Star en GitHub](https://github.com/BORNIOS/JellyTrend)

</div>
