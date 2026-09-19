using System;
using System.IO;
using System.Reflection;
using Jellyfin.Plugin.JellyTrend;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// Construye una instancia de <see cref="Plugin"/> sobre una carpeta temporal para poder
/// comprobar metadatos y páginas registradas sin un servidor Jellyfin en marcha.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>Gets the throwaway data folder used by the fake application paths.</summary>
    internal static string TempRoot { get; } = CreateTempRoot();

    /// <summary>
    /// Creates the plugin over the throwaway folder.
    /// </summary>
    /// <returns>The plugin instance.</returns>
    internal static Plugin CreatePlugin()
        => new(FakeApplicationPaths.Create(TempRoot), new FakeXmlSerializer(), NullLoggerFactory.Instance);

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "jellytrend-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "log"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        return root;
    }

    // El serializador XML solo se usa al guardar configuración; las pruebas no escriben nada.
    private sealed class FakeXmlSerializer : IXmlSerializer
    {
        public object DeserializeFromFile(Type type, string file)
            => Activator.CreateInstance(type)!;

        public object DeserializeFromBytes(Type type, byte[] buffer)
            => Activator.CreateInstance(type)!;

        public object DeserializeFromStream(Type type, Stream stream)
            => Activator.CreateInstance(type)!;

        public void SerializeToFile(object obj, string file)
        {
        }

        public void SerializeToStream(object obj, Stream stream)
        {
        }
    }

    // DispatchProxy evita tener que implementar las ~20 propiedades de IApplicationPaths.
    // No puede ser sealed: DispatchProxy genera una subclase en tiempo de ejecución.
    private class FakeApplicationPaths : DispatchProxy
    {
        private string _root = string.Empty;

        internal static IApplicationPaths Create(string root)
        {
            var proxy = Create<IApplicationPaths, FakeApplicationPaths>();
            // El proxy se crea como IApplicationPaths; el tipo real es esta clase, de ahí el doble cast.
            ((FakeApplicationPaths)(object)proxy).SetRoot(root);
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                return null;
            }

            return targetMethod.Name switch
            {
                "get_ProgramDataPath" => _root,
                "get_LogDirectoryPath" => Path.Combine(_root, "log"),
                "get_ConfigurationDirectoryPath" => Path.Combine(_root, "config"),
                "get_PluginConfigurationsPath" => Path.Combine(_root, "config", "plugins"),
                "get_DataPath" => Path.Combine(_root, "data"),
                "get_CachePath" => Path.Combine(_root, "cache"),
                "get_MetadataPath" => Path.Combine(_root, "metadata"),
                "get_PluginsPath" => Path.Combine(_root, "plugins"),
                "get_SystemConfigurationFilePath" => Path.Combine(_root, "config", "system.xml"),
                "get_TempDirectory" => Path.GetTempPath(),
                _ => targetMethod.ReturnType == typeof(string) ? _root : null
            };
        }

        private void SetRoot(string root) => _root = root;
    }
}
