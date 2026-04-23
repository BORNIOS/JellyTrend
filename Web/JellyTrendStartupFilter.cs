using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.JellyTrend.Web;

/// <summary>
/// Registers ScriptInjectionMiddleware at the very front of the ASP.NET Core pipeline
/// so it intercepts the Jellyfin web client's index.html before any other middleware.
/// Jellyfin honours IStartupFilter implementations registered via DI during plugin loading.
/// </summary>
public sealed class JellyTrendStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            // Place our middleware first so it wraps the response body before
            // static-file middleware writes to it.
            builder.UseMiddleware<ScriptInjectionMiddleware>();
            next(builder);
        };
    }
}
