using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Web;

/// <summary>
/// Injects the JellyTrend script tag into Jellyfin Web's index.html.
///
/// Strategies (in order):
///   1. Direct — resolves index.html relative to WebRootPath and serves the
///      modified file directly (works for standard on-premise Jellyfin installs).
///   2. Buffer — captures the response body from the pipeline as a fallback.
///
/// Both strategies set Cache-Control: no-store so the injected page is never
/// cached at CDN/proxy layers (e.g. Cloudflare).
/// </summary>
public sealed class ScriptInjectionMiddleware : IMiddleware
{
    private const string ScriptTag =
        "\n    <script src=\"/JellyTrend/jellyTrend.js\"></script>";

    private const string Marker = "/JellyTrend/jellyTrend.js";

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ScriptInjectionMiddleware> _logger;

    public ScriptInjectionMiddleware(IWebHostEnvironment env, ILogger<ScriptInjectionMiddleware> logger)
    {
        _env = env;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!ShouldIntercept(context))
        {
            await next(context);
            return;
        }

        var indexPath = ResolveIndexHtmlPath();
        if (indexPath is not null)
        {
            await ServeDirectAsync(context, indexPath);
            return;
        }

        await ServeBufferedAsync(context, next);
    }

    private string? ResolveIndexHtmlPath()
    {
        var webRoot = _env.WebRootPath;
        if (string.IsNullOrEmpty(webRoot)) return null;

        var baseDir = Directory.GetParent(webRoot)?.FullName;
        if (baseDir is null) return null;

        foreach (var subdir in new[] { "jellyfin-web", "web" })
        {
            var candidate = Path.Combine(baseDir, subdir, "index.html");
        }

        return null;
    }

    private async Task ServeDirectAsync(HttpContext context, string indexPath)
    {
        var html = await File.ReadAllTextAsync(indexPath);

        if (!html.Contains(Marker, System.StringComparison.Ordinal)
            && html.Contains("</head>", System.StringComparison.OrdinalIgnoreCase))
        {
            html = html.Replace("</head>", ScriptTag + "\n</head>", System.StringComparison.OrdinalIgnoreCase);
            _logger.LogInformation("JellyTrend: script inyectado en '{P}'.", indexPath);
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode  = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes);
    }

    private async Task ServeBufferedAsync(HttpContext context, RequestDelegate next)
    {
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        await next(context);

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var contentType = context.Response.ContentType ?? string.Empty;
        var isHtml = contentType.Contains("text/html", System.StringComparison.OrdinalIgnoreCase);

        if (!isHtml || context.Response.Headers.ContainsKey("Content-Encoding"))
        {
            await buffer.CopyToAsync(originalBody);
            return;
        }

        var html = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync();

        if (html.Length == 0)
        {
            _logger.LogWarning("JellyTrend: buffer vacío — el middleware de ficheros estáticos " +
                "no pasó por el stream. Revisa la configuración de Cloudflare (ver README).");
            return;
        }

        if (!html.Contains(Marker, System.StringComparison.Ordinal)
            && html.Contains("</head>", System.StringComparison.OrdinalIgnoreCase))
        {
            html = html.Replace("</head>", ScriptTag + "\n</head>", System.StringComparison.OrdinalIgnoreCase);
            _logger.LogInformation("JellyTrend: script inyectado (estrategia buffer).");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        if (!context.Response.HasStarted)
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.ContentLength = bytes.Length;
        }

        await originalBody.WriteAsync(bytes);
    }

    private static bool ShouldIntercept(HttpContext context)
    {
        if (Plugin.Instance?.Configuration.EnableBannerMode != true) return false;
        if (!HttpMethods.IsGet(context.Request.Method)) return false;

        var path = context.Request.Path.Value ?? string.Empty;
        return path.Equals("/web/index.html", System.StringComparison.OrdinalIgnoreCase)
            || path.Equals("/web/", System.StringComparison.OrdinalIgnoreCase)
            || path.Equals("/", System.StringComparison.OrdinalIgnoreCase);
    }
}
