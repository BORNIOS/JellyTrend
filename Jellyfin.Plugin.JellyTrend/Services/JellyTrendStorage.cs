using System;
using System.IO;

using Jellyfin.Plugin.JellyTrend.Logging;

namespace Jellyfin.Plugin.JellyTrend.Services;

/// <summary>
/// Resolves the paths of the plugin's persistent data and migrates the files that older versions kept
/// inside the installation folder.
/// </summary>
/// <remarks>
/// <para>
/// Plugin data cannot live inside the plugin folder: Jellyfin replaces that folder when the plugin is
/// updated or reinstalled, so the feature cache, the stored recommendations and the trending list would be
/// lost on every update. They live under <c>{DataPath}/JellyTrend</c>, which is data and survives updates.
/// </para>
/// <para>
/// The path is never written by hand: it is derived from <c>IApplicationPaths.DataPath</c>, so it follows
/// whatever the server is configured with (including portable installs). The first call after the upgrade
/// moves the legacy files out of the plugin folder once, and skips any file that already exists at the
/// destination so a newer copy is never overwritten.
/// </para>
/// </remarks>
public static class JellyTrendStorage
{
    private const string FolderName = "JellyTrend";
    private const string FeaturesFileName = "features.json";
    private const string TrendingFileName = "trending.json";
    private const string RecommendationsFolderName = "recommendations";

    private static string? _dataPath;

    /// <summary>
    /// Gets the folder that holds every JellyTrend data file.
    /// </summary>
    /// <value>Absolute path of the data folder, or an empty string while the storage is not initialized.</value>
    public static string Folder
        => string.IsNullOrEmpty(_dataPath) ? string.Empty : Path.Combine(_dataPath, FolderName);

    /// <summary>
    /// Gets the full path of the feature cache file.
    /// </summary>
    /// <value>Absolute path, or an empty string while the storage is not initialized.</value>
    public static string FeaturesFile => Combine(FeaturesFileName);

    /// <summary>
    /// Gets the full path of the trending cache file.
    /// </summary>
    /// <value>Absolute path, or an empty string while the storage is not initialized.</value>
    public static string TrendingFile => Combine(TrendingFileName);

    /// <summary>
    /// Gets the folder that holds one recommendation file per user.
    /// </summary>
    /// <value>Absolute path, or an empty string while the storage is not initialized.</value>
    public static string RecommendationsFolder => Combine(RecommendationsFolderName);

    /// <summary>
    /// Points the storage at the server data directory and creates the folder when it is missing.
    /// </summary>
    /// <param name="dataPath">Server data directory (<c>IApplicationPaths.DataPath</c>).</param>
    public static void Initialize(string dataPath)
    {
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            return;
        }

        _dataPath = dataPath;
        EnsureFolder();
    }

    /// <summary>
    /// Creates the data folder when it is missing. Safe to call before any write.
    /// </summary>
    public static void EnsureFolder()
    {
        var folder = Folder;
        if (folder.Length == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            JellyTrendLog.Warn($"[Almacen] No se pudo crear la carpeta de datos '{folder}': {ex.Message}");
        }
    }

    /// <summary>
    /// Moves the files written by older versions inside the plugin folder into the data folder.
    /// </summary>
    /// <param name="pluginFolder">Folder that holds the plugin assembly, or null when it is unknown.</param>
    public static void MigrateFromPluginFolder(string? pluginFolder)
    {
        if (string.IsNullOrWhiteSpace(pluginFolder) || Folder.Length == 0 || !Directory.Exists(pluginFolder))
        {
            return;
        }

        EnsureFolder();
        MoveLegacyFile(Path.Combine(pluginFolder, FeaturesFileName), FeaturesFile);
        MoveLegacyFile(Path.Combine(pluginFolder, TrendingFileName), TrendingFile);
        MoveLegacyRecommendations(Path.Combine(pluginFolder, RecommendationsFolderName));
    }

    private static string Combine(string name)
        => Folder.Length == 0 ? string.Empty : Path.Combine(Folder, name);

    private static void MoveLegacyFile(string source, string destination)
    {
        var fileName = Path.GetFileName(source);

        try
        {
            // Nada que mover, o el destino ya manda: no se pisa una copia mas nueva.
            if (!File.Exists(source) || File.Exists(destination))
            {
                return;
            }

            var folder = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.Move(source, destination);
            JellyTrendLog.Info($"[Almacen] Datos movidos fuera de la carpeta del plugin: {fileName}");
        }
        catch (Exception ex)
        {
            JellyTrendLog.Warn($"[Almacen] No se pudo migrar '{fileName}': {ex.Message}");
        }
    }

    private static void MoveLegacyRecommendations(string legacyFolder)
    {
        try
        {
            if (!Directory.Exists(legacyFolder))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(legacyFolder, "*.json"))
            {
                MoveLegacyFile(file, Path.Combine(RecommendationsFolder, Path.GetFileName(file)));
            }

            // Solo se retira la carpeta heredada si quedo vacia: sin residuos, y sin borrar nada pendiente.
            if (Directory.GetFileSystemEntries(legacyFolder).Length == 0)
            {
                Directory.Delete(legacyFolder);
            }
        }
        catch (Exception ex)
        {
            JellyTrendLog.Warn($"[Almacen] No se pudieron migrar las recomendaciones guardadas: {ex.Message}");
        }
    }
}
