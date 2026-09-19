using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// Colección para las pruebas que usan el estado estático del almacén
/// (<c>JellyTrendStorage.Initialize</c>). xUnit ejecuta las clases en paralelo por omisión, así que
/// sin esto otra clase que inicialice el almacén puede cambiar la carpeta de datos mientras la
/// prueba de migración la está comprobando (fallo intermitente observado en la suite).
/// </summary>
[CollectionDefinition(JellyTrendStorageStateCollection.Name, DisableParallelization = true)]
public sealed class JellyTrendStorageStateCollection
{
    /// <summary>Nombre de la colección.</summary>
    public const string Name = "estado-del-almacen";
}
