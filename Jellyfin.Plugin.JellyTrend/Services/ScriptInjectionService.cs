using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Jellyfin.Plugin.JellyTrend.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.JellyTrend.Services;

/// <summary>
/// Injects the JellyTrend banner script into the Jellyfin Web entry point at request time.
/// </summary>
/// <remarks>
/// <para>
/// The previous implementation patched <c>jellyfin-web/index.html</c> on disk. When Jellyfin runs as
/// a Windows service (Network Service, Local Service…), the installation directory is read-only for
/// the service account, so the write failed with <see cref="UnauthorizedAccessException"/> and the
/// banner was never injected.
/// </para>
/// <para>
/// This middleware buffers the SPA entry point response, inserts the script tag before
/// <c>&lt;/head&gt;</c> and writes it back. No file is modified, so no elevated permission is
/// needed. The cache-buster follows the deployed plugin version, and the script URL respects the
/// server base URL.
/// </para>
/// </remarks>
public sealed class ScriptInjectionService : IStartupFilter
{
    private const string Marker = "/JellyTrend/jellyTrend.js";
    private const string HeadTag = "</head>";

    private readonly ILogger<ScriptInjectionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionService"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{ScriptInjectionService}"/> interface.</param>
    public ScriptInjectionService(ILogger<ScriptInjectionService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            JellyTrendLog.Info("[Banner] Inyección en memoria activa; jellyfin-web no se modifica.");

            app.Use(async (context, nextMiddleware) =>
            {
                await InjectAsync(context, nextMiddleware).ConfigureAwait(false);
            });

            next(app);
        };
    }

    private async Task InjectAsync(HttpContext context, Func<Task> next)
    {
        if (Plugin.Instance?.Configuration.EnableBannerMode != true
            || !HttpMethods.IsGet(context.Request.Method)
            || context.Request.Headers.ContainsKey(HeaderNames.Range)
            || !IsWebEntryPoint(context.Request.Path.Value))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        // Prevent response compression while buffering: the injected HTML is clear text and the
        // final length differs from the file on disk.
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);

        // Force a 200 with the body: a 304 would make the client reuse a cached entry point that
        // predates the plugin, without the injected script.
        context.Request.Headers.Remove(HeaderNames.IfNoneMatch);
        context.Request.Headers.Remove(HeaderNames.IfModifiedSince);

        context.Response.OnStarting(() =>
        {
            if (IsHtml(context.Response))
            {
                context.Response.Headers.Remove(HeaderNames.ContentLength);
                context.Response.Headers.Remove(HeaderNames.ContentEncoding);
                context.Response.Headers.Remove(HeaderNames.ETag);
                context.Response.Headers.Remove(HeaderNames.LastModified);
            }

            return Task.CompletedTask;
        });

        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        if (context.Response.StatusCode != StatusCodes.Status200OK || !IsHtml(context.Response))
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var html = Encoding.UTF8.GetString(buffer.ToArray());
        var injected = InjectScript(html, context.Request.Path.Value ?? string.Empty);
        if (string.Equals(html, injected, StringComparison.Ordinal))
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (!context.Response.HasStarted)
        {
            context.Response.Headers.Remove(HeaderNames.ContentLength);
            context.Response.Headers.Remove(HeaderNames.ContentEncoding);
            context.Response.Headers.Remove(HeaderNames.ETag);
            context.Response.Headers.Remove(HeaderNames.LastModified);
        }

        _logger.LogDebug("JellyTrend: banner script injected into the web entry point.");
        await originalBody.WriteAsync(Encoding.UTF8.GetBytes(injected), context.RequestAborted).ConfigureAwait(false);
    }

    internal static string InjectScript(string html, string requestPath)
    {
        var tag = BuildScriptTag(requestPath);
        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return ReplaceExistingScriptTag(html, tag);
        }

        if (!html.Contains(HeadTag, StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        return html.Replace(HeadTag, tag + Environment.NewLine + HeadTag, StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildScriptTag(string requestPath)
    {
        // Respect a server mounted under a base URL: /jellyfin/web/ -> /jellyfin/JellyTrend/...
        var prefix = string.Empty;
        var webIndex = requestPath.LastIndexOf("/web/", StringComparison.OrdinalIgnoreCase);
        if (webIndex > 0)
        {
            prefix = requestPath[..webIndex];
        }

        var version = Plugin.Instance?.Version?.ToString() ?? "0";
        return $"    <script src=\"{prefix}/JellyTrend/jellyTrend.js?v={version}\"></script>";
    }

    internal static string ReplaceExistingScriptTag(string html, string currentTag)
    {
        var markerIndex = html.IndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return html;
        }

        // Only replace a complete script element that contains our fixed path. If a hand-edited
        // file contains the marker outside of a script tag, leave it untouched rather than risking
        // a corrupt response.
        var start = html.LastIndexOf("<script", markerIndex, StringComparison.OrdinalIgnoreCase);
        var end = html.IndexOf("</script>", markerIndex, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < markerIndex)
        {
            return html;
        }

        // BuildScriptTag owns its indentation. Jellyfin's production index is minified on one line,
        // so consume whitespace immediately before the tag rather than relying on a line boundary.
        while (start > 0
            && char.IsWhiteSpace(html[start - 1])
            && html[start - 1] is not '\r' and not '\n')
        {
            start--;
        }

        end += "</script>".Length;
        return string.Concat(html.AsSpan(0, start), currentTag, html.AsSpan(end));
    }

    private static bool IsWebEntryPoint(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.Equals("/", StringComparison.Ordinal)
            || path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHtml(HttpResponse response)
        => response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;
}
