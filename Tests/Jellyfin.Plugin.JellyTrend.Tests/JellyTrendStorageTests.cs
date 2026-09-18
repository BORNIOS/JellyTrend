using System;
using System.IO;

using Jellyfin.Plugin.JellyTrend.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// Los datos del plugin no pueden vivir en la carpeta de instalacion: Jellyfin la reemplaza al
/// actualizar (y al reinstalar el ZIP), asi que la cache, las recomendaciones y las tendencias se
/// perderian en cada actualizacion. Estas pruebas fijan que la migracion los mueve a la carpeta de
/// datos, que nunca pisa una copia mas nueva y que no deja residuos.
/// </summary>
[Collection(JellyTrendStorageStateCollection.Name)]
public sealed class JellyTrendStorageTests
{
    private const string Cached = "{\"Items\":[]}";

    /// <summary>
    /// La migracion mueve la cache de caracteristicas, la de tendencias y las recomendaciones
    /// fuera de la carpeta del plugin, y retira la carpeta heredada cuando queda vacia.
    /// </summary>
    [Fact]
    public void MovesLegacyFilesIntoTheDataFolder()
    {
        var root = TempRoot();
        var plugin = Path.Combine(root, "plugins", "JellyTrend_1.0.0.0");
        var legacyRecommendations = Path.Combine(plugin, "recommendations");
        Directory.CreateDirectory(legacyRecommendations);
        File.WriteAllText(Path.Combine(plugin, "features.json"), Cached);
        File.WriteAllText(Path.Combine(plugin, "trending.json"), Cached);
        File.WriteAllText(Path.Combine(legacyRecommendations, "usuario.json"), Cached);

        try
        {
            JellyTrendStorage.Initialize(Path.Combine(root, "data"));
            JellyTrendStorage.MigrateFromPluginFolder(plugin);

            Assert.True(File.Exists(JellyTrendStorage.FeaturesFile), "la cache de caracteristicas no llego a la carpeta de datos");
            Assert.True(File.Exists(JellyTrendStorage.TrendingFile), "la cache de tendencias no llego a la carpeta de datos");
            Assert.True(
                File.Exists(Path.Combine(JellyTrendStorage.RecommendationsFolder, "usuario.json")),
                "las recomendaciones guardadas no llegaron a la carpeta de datos");
            Assert.False(File.Exists(Path.Combine(plugin, "features.json")), "quedo el archivo heredado en la carpeta del plugin");
            Assert.False(File.Exists(Path.Combine(plugin, "trending.json")), "quedo el archivo heredado en la carpeta del plugin");
            Assert.False(Directory.Exists(legacyRecommendations), "quedo la carpeta de recomendaciones heredada");
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// Una copia que ya vive en la carpeta de datos nunca se pisa con el archivo heredado: al migrar,
    /// el destino manda.
    /// </summary>
    [Fact]
    public void KeepsTheCopyThatAlreadyLivesInTheDataFolder()
    {
        var root = TempRoot();
        var plugin = Path.Combine(root, "plugins", "JellyTrend_1.0.0.0");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "trending.json"), "heredado");

        try
        {
            JellyTrendStorage.Initialize(Path.Combine(root, "data"));
            File.WriteAllText(JellyTrendStorage.TrendingFile, "actual");

            JellyTrendStorage.MigrateFromPluginFolder(plugin);

            Assert.Equal("actual", File.ReadAllText(JellyTrendStorage.TrendingFile));
        }
        finally
        {
            Delete(root);
        }
    }

    private static string TempRoot()
        => Path.Combine(Path.GetTempPath(), "jellytrend-" + Path.GetRandomFileName());

    private static void Delete(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
