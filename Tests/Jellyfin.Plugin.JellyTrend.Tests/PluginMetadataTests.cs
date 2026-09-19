using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTrend;
using MediaBrowser.Controller.Plugins;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// La versión, el guid y el ABI del plugin viven en cuatro sitios (Directory.Build.props,
/// build.yaml, manifest.json y el propio ensamblado). Estas pruebas fallan cuando uno se
/// queda atrás: es exactamente la deriva que ya ocurrió (build.yaml en 2.0.3.0 mientras el
/// ensamblado era 2.0.4.0).
/// </summary>
public sealed class PluginMetadataTests
{
    [Fact]
    public void VersionAndGuidMatchBetweenPropsAndBuildYaml()
    {
        var props = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "Directory.Build.props"));
        var buildYaml = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "build.yaml"));

        Assert.Equal(FirstMatch(props, "<Version>([^<]+)</Version>"), YamlValue(buildYaml, "version"));
        Assert.Equal(FirstMatch(props, "<AssemblyVersion>([^<]+)</AssemblyVersion>"), YamlValue(buildYaml, "version"));
        Assert.Equal(YamlValue(buildYaml, "guid"), Manifest()[0].GetProperty("guid").GetString());
    }

    [Fact]
    public void PluginIdentityMatchesTheManifest()
    {
        var plugin = TestEnvironment.CreatePlugin();
        var manifest = Manifest()[0];

        Assert.Equal(manifest.GetProperty("guid").GetString(), plugin.Id.ToString());
        Assert.Equal(manifest.GetProperty("name").GetString(), plugin.Name);
    }

    [Fact]
    public void FrameworkAndTargetAbiMatchTheCompiledTargets()
    {
        var csproj = File.ReadAllText(Path.Combine(
            TestPaths.PluginSourceDirectory,
            "Jellyfin.Plugin.JellyTrend.csproj"));
        var buildYaml = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "build.yaml"));

        var targetFramework = FirstMatch(csproj, "<TargetFramework>([^<]+)</TargetFramework>");
        var jellyfinVersion = FirstMatch(csproj, "<JellyfinVersion>([^<]+)</JellyfinVersion>");

        Assert.Equal(targetFramework, YamlValue(buildYaml, "framework"));

        // JellyfinVersion 12.1.0 -> targetAbi 12.1.0.0
        var version = Version.Parse(jellyfinVersion);
        var expectedAbi = $"{version.Major}.{version.Minor}.0.0";
        Assert.Equal(expectedAbi, YamlValue(buildYaml, "targetAbi"));
    }

    [Fact]
    public void AssemblyExposesExactlyOneServiceRegistrator()
    {
        var registrators = typeof(Plugin).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && typeof(IPluginServiceRegistrator).IsAssignableFrom(type))
            .ToArray();

        Assert.Single(registrators);
    }

    /// <summary>
    /// El plugin declara la ruta del panel como recurso embebido. Si el archivo se mueve o se
    /// renombra y la ruta no se actualiza, Jellyfin sirve un 404 y el compilador no dice nada.
    /// </summary>
    [Fact]
    public void PanelResourceDeclaredByThePluginExistsInTheAssembly()
    {
        var plugin = TestEnvironment.CreatePlugin();
        var page = Assert.Single(plugin.GetPages());
        var assembly = typeof(Plugin).Assembly;

        Assert.Contains(page.EmbeddedResourcePath, assembly.GetManifestResourceNames());

        using var stream = assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(stream);
    }

    private static JsonElement[] Manifest()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "manifest.json")));

        // Clone: el JsonDocument se libera al salir del método y los elementos dejarían de ser válidos.
        return document.RootElement.Clone().EnumerateArray().ToArray();
    }

    private static string YamlValue(string yaml, string key)
        => Regex.Match(yaml, $"(?m)^{Regex.Escape(key)}:\\s*\"?([^\"\\r\\n]+)\"?\\s*$").Groups[1].Value.Trim();

    private static string FirstMatch(string text, string pattern)
        => Regex.Match(text, pattern).Groups[1].Value.Trim();
}
