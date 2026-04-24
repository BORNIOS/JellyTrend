# JellyTrend

Plugin para **Jellyfin** que obtiene películas en tendencia desde **TMDB**, las cruza con tu biblioteca local y ofrece:

- Un **carrusel estilo banner** en la interfaz web (cuando está habilitado).
- Un **canal** en la sección *Canales* con esas mismas películas reproducibles.

La **fuente real de reproducción** siempre es tu servidor Jellyfin: los archivos son los de la biblioteca. TMDB solo alimenta la lista de tendencias y metadatos de descubrimiento.

## Requisitos

- Jellyfin compatible con la versión de `Jellyfin.Controller` referenciada en el `.csproj` (p. ej. 10.11.x).
- Una clave de API de TMDB (configuración del plugin).

## Configuración

En el panel del plugin puedes ajustar, entre otros:

- Clave TMDB, idioma y región.
- Nombre del canal y activación del canal.
- Número máximo de ítems y intervalo de sincronización.

## Sincronía biblioteca ↔ canal

Jellyfin crea, para cada entrada del canal, un **ítem sombra** con su propio `Id` interno. El vínculo con tu película de biblioteca se guarda en `ExternalId` (el `Guid` del ítem real). Por eso, el estado *visto* y la posición de reanudación no coincidían antes con la biblioteca.

**JellyTrend** ahora:

1. **Replica datos de usuario** (visto, posición, favoritos, valoración, etc.) entre el ítem de biblioteca y el ítem sombra del canal, usando los eventos de reproducción y el guardado de datos de usuario del servidor. La **posición de reanudación** se considera canónica solo en la biblioteca: el sombra no conserva ticks de progreso parcial, para que **«Continuar viendo»** no muestre la misma película dos veces (biblioteca + canal).
2. **Enriquece el `ChannelItemInfo`** con géneros, estudios, reparto, fechas y clasificación, tomados del mismo ítem de biblioteca.
3. Tras cada **sync TMDB** (y un poco después del arranque del servidor), **`TrendingShadowMetadataSync`** vuelca reparto y metadatos al ítem sombra en base de datos (`UpdatePeopleAsync`, campos de texto y `UpdateImagesAsync`), porque Jellyfin no reaplica el reparto a sombras ya existentes solo con el canal.

Así, si marcas una película como vista en *Películas*, el canal puede reflejarlo (tras guardar o refrescar según el cliente), y si la ves desde el canal, la biblioteca queda alineada. Para reanudar donde lo dejaste, usa **Continuar viendo** sobre la entrada de **biblioteca** (el sombra del canal no guarda progreso parcial a propósito).

### Limitaciones de la interfaz del canal

El modelo de canales de Jellyfin expone **una URL principal de imagen** por ítem; logos, disc art o fondos extra dependen de cómo cada cliente consulte imágenes del ítem sombra. El carrusel web (`/JellyTrend/Trending`) usa rutas `/Items/{id}/Images/...` sobre el **Id de biblioteca**, por eso allí sí verás backdrop, logo, etc., cuando existan en tu biblioteca.

## API interna

- `GET /JellyTrend/Trending` — lista en tendencia con géneros, actores y estado de reproducción del usuario autenticado.
- `GET /JellyTrend/Status` — resumen de configuración y caché.
- `GET /JellyTrend/jellyTrend.js` / `jellyTrend.css` — recursos del carrusel.

## Desarrollo

```bash
dotnet build
```

Copia el ensamblado del plugin al directorio de plugins de Jellyfin según tu instalación.

---

*Documentación en inglés: [README.en.md](README.en.md).*
