using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.JellyTrend;
using Jellyfin.Plugin.JellyTrend.Logging;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// El plugin escribe su propio archivo diario sin dependencias externas. Estas pruebas fijan
/// las tres propiedades que importan: que no vuelva a entrar una librería de logging del
/// servidor, que el archivo exista con el nombre esperado y que un error llegue con su traza.
/// </summary>
[Collection(LogStateCollection.Name)]
public sealed class LoggingTests
{
    /// <summary>
    /// El plugin compilaba contra Serilog y Serilog.Sinks.File sin usarlos (restos de una
    /// implementación anterior): el ensamblado no los referenciaba, así que solo añadían una
    /// dependencia acoplada a la versión del servidor.
    /// </summary>
    [Fact]
    public void AssemblyDoesNotReferenceAnyExternalLoggingLibrary()
    {
        var referenced = typeof(Plugin).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(referenced, name => name.StartsWith("Serilog", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referenced, name => name.StartsWith("NLog", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referenced, name => name.StartsWith("log4net", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WritesToADailyFileNamedAfterThePlugin()
    {
        var folder = CreateTempFolder();
        JellyTrendLog.SetLogDirectory(folder);

        JellyTrendLog.Info("prueba de arranque");

        var expected = Path.Combine(folder, $"JellyTrend-{DateTimeOffset.Now:yyyy-MM-dd}.log");
        Assert.True(File.Exists(expected), $"No se creó el archivo diario esperado: {expected}");
        Assert.Equal(expected, JellyTrendLog.CurrentLogPath);

        var content = File.ReadAllText(expected);
        Assert.Contains("INFO", content, StringComparison.Ordinal);
        Assert.Contains("prueba de arranque", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorKeepsTheStackTraceAndTheInnerException()
    {
        var folder = CreateTempFolder();
        JellyTrendLog.SetLogDirectory(folder);

        Exception captured;
        try
        {
            throw new InvalidOperationException("fallo interno", new FormatException("causa original"));
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        JellyTrendLog.Error("no se pudo completar la sincronización", captured);

        var content = File.ReadAllText(JellyTrendLog.CurrentLogPath);
        Assert.Contains("no se pudo completar la sincronización", content, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), content, StringComparison.Ordinal);
        Assert.Contains("fallo interno", content, StringComparison.Ordinal);
        Assert.Contains(nameof(FormatException), content, StringComparison.Ordinal);
        Assert.Contains("causa original", content, StringComparison.Ordinal);

        // La traza completa incluye el método que lanzó, que es lo que hace útil el log.
        Assert.Contains(nameof(ErrorKeepsTheStackTraceAndTheInnerException), content, StringComparison.Ordinal);
    }

    /// <summary>
    /// La limpieza borra solo los archivos del plugin: si tocara vecinos del directorio de logs
    /// del servidor, borraría el historial de Jellyfin.
    /// </summary>
    [Fact]
    public void PurgeDeletesOnlyItsOwnOldFiles()
    {
        var folder = CreateTempFolder();
        JellyTrendLog.SetLogDirectory(folder);

        var oldPluginLog = Path.Combine(folder, $"JellyTrend-{DateTimeOffset.Now.AddDays(-8):yyyy-MM-dd}.log");
        var todayPluginLog = Path.Combine(folder, $"JellyTrend-{DateTimeOffset.Now:yyyy-MM-dd}.log");
        var foreignLog = Path.Combine(folder, "log_20260101.log");
        File.WriteAllText(oldPluginLog, "viejo");
        File.WriteAllText(todayPluginLog, "hoy");
        File.WriteAllText(foreignLog, "de Jellyfin");

        JellyTrendLog.PurgeOldLogs();

        Assert.False(File.Exists(oldPluginLog), "El archivo viejo del plugin debería haberse borrado.");
        Assert.True(File.Exists(todayPluginLog), "El archivo de hoy no debe borrarse.");
        Assert.True(File.Exists(foreignLog), "Un log ajeno al plugin no debe borrarse nunca.");
    }

    private static string CreateTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "jellytrend-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
