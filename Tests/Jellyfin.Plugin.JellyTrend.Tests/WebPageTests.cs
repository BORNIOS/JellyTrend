using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTrend;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// El panel es la única interfaz del plugin: si un id desaparece o una clave de traducción se
/// añade solo en un idioma, el usuario ve campos vacíos o textos en el idioma equivocado.
/// </summary>
public sealed class WebPageTests
{
    private static readonly string[] RequiredIds =
    [
        "jellyTrendConfigPage",
        "jellyTrendConfigForm",
        "tmdbApiKey",
        "tmdbLanguage",
        "tmdbRegion",
        "maxItems",
        "enableBannerMode",
        "enableTrendingSeries",
        "enableChannel",
        "channelName",
        "enableRecommendationRow",
        "recommendationChannelName",
        "recommendationMaxItems",
        "jellyTrendSaveBtn",
        "jtRunNow",
        "jtRunRecNow",
        "jtSaveStatus",
        "jtStatusInfo"
    ];

    [Fact]
    public void PanelLivesInWebAndDeclaresEveryRequiredField()
    {
        Assert.True(File.Exists(TestPaths.PanelFile), $"No existe el panel en {TestPaths.PanelFile}");

        var html = File.ReadAllText(TestPaths.PanelFile);
        foreach (var id in RequiredIds)
        {
            Assert.Contains($"id=\"{id}\"", html, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Detecta ids usados por el script que no existen en el marcado (un getElementById que
    /// devuelve null rompe la página sin error de compilación).
    /// </summary>
    [Fact]
    public void EveryElementTheScriptLooksUpExistsInTheMarkup()
    {
        var html = File.ReadAllText(TestPaths.PanelFile);
        var declared = IdsDeclared(html);
        var used = Regex.Matches(html, "getElementById\\(\\s*['\"]([A-Za-z0-9_-]+)['\"]\\s*\\)")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var missing = used.Where(id => !declared.Contains(id)).ToArray();
        Assert.True(missing.Length == 0, $"El script usa ids que no existen: {string.Join(", ", missing)}");
    }

    [Fact]
    public void BothLanguagePacksDefineTheSameKeys()
    {
        var html = File.ReadAllText(TestPaths.PanelFile);
        var en = KeysOf(ExtractObject(html, "en: {"));
        var es = KeysOf(ExtractObject(html, "es: {"));

        Assert.NotEmpty(en);
        Assert.Equal(en.Count, es.Count);

        var onlyInEnglish = en.Except(es, StringComparer.Ordinal).ToArray();
        var onlyInSpanish = es.Except(en, StringComparer.Ordinal).ToArray();

        Assert.True(
            onlyInEnglish.Length == 0 && onlyInSpanish.Length == 0,
            $"Claves solo en inglés: {string.Join(", ", onlyInEnglish)} | solo en español: {string.Join(", ", onlyInSpanish)}");
    }

    /// <summary>
    /// El recurso embebido tiene que ser el archivo actual: si el panel se edita y la copia
    /// embebida queda vieja, el servidor sirve una versión distinta a la del repositorio.
    /// </summary>
    [Fact]
    public void EmbeddedPanelIsTheSameFileAsTheOneInTheRepository()
    {
        var plugin = TestEnvironment.CreatePlugin();
        var page = Assert.Single(plugin.GetPages());
        var assembly = typeof(Plugin).Assembly;

        using var stream = assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var embedded = reader.ReadToEnd();
        var onDisk = File.ReadAllText(TestPaths.PanelFile);

        Assert.Equal(Normalize(onDisk), Normalize(embedded));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static HashSet<string> IdsDeclared(string html)
        => Regex.Matches(html, "id=\"([A-Za-z0-9_-]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> KeysOf(string block)
        => Regex.Matches(block, "(?m)^\\s*([A-Za-z_][A-Za-z0-9_]*)\\s*:")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    // Devuelve el bloque {...} que abre en `marker`, equilibrando llaves.
    private static string ExtractObject(string html, string marker)
    {
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No se encontró el bloque '{marker}' en el panel.");

        var open = html.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < html.Length; i++)
        {
            if (html[i] == '{')
            {
                depth++;
            }
            else if (html[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return html[open..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"El bloque '{marker}' no está equilibrado.");
    }
}
