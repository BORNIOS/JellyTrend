# Resumen de verificación — JellyTrend

> Documento generado automáticamente por `Tools/Generar-ResumenEjecutivo.ps1` a partir del
> resultado real de la suite (`Tests/TestResults/verificacion.trx`). No se edita a mano.

## Resultado

| Métrica | Valor |
|---|---|
| Total | 16 |
| Correctas | 16 |
| Fallidas | 0 |
| Omitidas | 0 |
| Estado | OK |

## Detalle por clase

| Clase | Total | Correctas | Fallidas |
|---|---|---|---|
| ConfigurationTests | 3 | 3 | 0 |
| LoggingTests | 4 | 4 | 0 |
| PluginMetadataTests | 5 | 5 | 0 |
| WebPageTests | 4 | 4 | 0 |

## Cómo reproducirlo

```bash
dotnet test Tests/Jellyfin.Plugin.JellyTrend.Tests

# Suite completa + regeneración de este resumen
./Tools/Generar-ResumenEjecutivo.ps1
```
