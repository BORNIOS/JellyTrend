using System;
using System.IO;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// Localiza las carpetas del repositorio que las pruebas leen (código fuente, manifiesto, build.yaml).
/// </summary>
internal static class TestPaths
{
    /// <summary>Gets the repository root, found by walking up from the test output folder.</summary>
    internal static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>Gets the plugin source directory.</summary>
    internal static string PluginSourceDirectory => Path.Combine(RepositoryRoot, "Jellyfin.Plugin.JellyTrend");

    /// <summary>Gets the panel HTML file used by the plugin.</summary>
    internal static string PanelFile => Path.Combine(PluginSourceDirectory, "Web", "configurationPage.html");

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Jellyfin.Plugin.JellyTrend.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("No se encontró la raíz del repositorio (Jellyfin.Plugin.JellyTrend.sln).");
    }
}
