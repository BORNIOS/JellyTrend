using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// Colección para las pruebas que usan el estado estático del logger del plugin
/// (<c>JellyTrendLog.SetLogDirectory</c>). xUnit ejecuta las clases en paralelo por omisión, así que
/// sin esto otra clase que escriba en el log puede cambiar el directorio mientras la prueba de
/// rotación diaria lo está comprobando (fallo intermitente observado en la suite).
/// </summary>
[CollectionDefinition(LogStateCollection.Name, DisableParallelization = true)]
public sealed class LogStateCollection
{
    /// <summary>Nombre de la colección.</summary>
    public const string Name = "estado-del-log";
}
