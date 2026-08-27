using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Web;

/// <summary>
/// Patches Jellyfin Web's index.html on disk to inject the JellyTrend script tag
/// when banner mode is enabled. Runs at server startup (<see cref="StartAsync"/>) and
/// removes the tag on shutdown (<see cref="StopAsync"/>).
///
/// This approach works regardless of how the ASP.NET Core pipeline is ordered,
/// since it operates directly on the static file that Jellyfin's own static-file
/// middleware serves — no request interception required.
/// </summary>
public sealed class ScriptInjectionService : IHostedService
{
    private const string Marker = "/JellyTrend/jellyTrend.js";
    private const string HeadTag = "</head>";

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ScriptInjectionService> _logger;

    // Path that was patched — used by StopAsync to restore the file.
    private string? _patchedPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionService"/> class.
    /// </summary>
    /// <param name="env">The web host environment.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ScriptInjectionService}"/> interface.</param>
    public ScriptInjectionService(IWebHostEnvironment env, ILogger<ScriptInjectionService> logger)
    {
        _env = env;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.EnableBannerMode != true)
        {
            JellyTrendLog.Info("[Banner] Desactivado — index.html no modificado.");
            return;
        }

        var indexPath = ResolveIndexHtmlPath();
        if (indexPath is null)
        {
            JellyTrendLog.Warn("[Banner] No se encontró index.html en el sistema de archivos.");
            return;
        }

        try
        {
            var html = await File.ReadAllTextAsync(indexPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            if (html.Contains(Marker, StringComparison.Ordinal))
            {
                JellyTrendLog.Info($"[Banner] Script ya presente en '{indexPath}'.");
                _patchedPath = indexPath;
                return;
            }

            if (!html.Contains(HeadTag, StringComparison.OrdinalIgnoreCase))
            {
                JellyTrendLog.Warn($"[Banner] No se encontró </head> en '{indexPath}'.");
                return;
            }

            var patched = html.Replace(
                HeadTag,
                BuildScriptTag() + Environment.NewLine + HeadTag,
                StringComparison.OrdinalIgnoreCase);

            await File.WriteAllTextAsync(indexPath, patched, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            _patchedPath = indexPath;
            JellyTrendLog.Info($"[Banner] Script inyectado en '{indexPath}'.");
            _logger.LogInformation("JellyTrend: banner script inyectado en '{P}'.", indexPath);
        }
        catch (Exception ex)
        {
            JellyTrendLog.Error("[Banner] Error al parchear index.html", ex);
            _logger.LogError(ex, "JellyTrend: error al parchear index.html.");
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_patchedPath is null || !File.Exists(_patchedPath))
        {
            return;
        }

        try
        {
            var html = await File.ReadAllTextAsync(_patchedPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            if (!html.Contains(Marker, StringComparison.Ordinal))
            {
                return;
            }

            // Remove the injected line (tag + trailing newline).
            var injected = BuildScriptTag() + Environment.NewLine;
            var restored = html.Replace(injected, string.Empty, StringComparison.Ordinal);
            await File.WriteAllTextAsync(_patchedPath, restored, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            JellyTrendLog.Info($"[Banner] Script eliminado de '{_patchedPath}'.");
        }
        catch (Exception ex)
        {
            JellyTrendLog.Error("[Banner] Error al restaurar index.html", ex);
        }
    }

    private string? ResolveIndexHtmlPath()
    {
        var webRoot = _env.WebRootPath;
        JellyTrendLog.Info($"[Banner] WebRootPath={webRoot ?? "null"}");

        if (!string.IsNullOrEmpty(webRoot))
        {
            // Some installs serve directly from WebRootPath.
            var direct = Path.Combine(webRoot, "index.html");
            if (File.Exists(direct))
            {
                return direct;
            }

            // Standard layout: WebRootPath = .../jellyfin-web, parent has jellyfin-web/ or web/.
            var baseDir = Directory.GetParent(webRoot)?.FullName;
            if (baseDir is not null)
            {
                foreach (var subdir in new[] { "jellyfin-web", "web" })
                {
                    var candidate = Path.Combine(baseDir, subdir, "index.html");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        // Last-resort: well-known Jellyfin install paths.
        foreach (var candidate in GetWellKnownIndexPaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static System.Collections.Generic.IEnumerable<string> GetWellKnownIndexPaths()
    {
        var exeDir = AppContext.BaseDirectory;
        yield return Path.Combine(exeDir, "jellyfin-web", "index.html");
        yield return Path.Combine(exeDir, "web", "index.html");

        // Common Linux package paths.
        yield return "/usr/share/jellyfin/web/index.html";
        yield return "/usr/lib/jellyfin/web/index.html";
        yield return "/opt/jellyfin/web/index.html";
    }

    private static string BuildScriptTag()
    {
        var version = Plugin.Instance?.Version?.ToString() ?? "0";
        return $"    <script src=\"/JellyTrend/jellyTrend.js?v={version}\"></script>";
    }
}
