using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend;

/// <summary>
/// Hosted service que inicializa el logger dedicado del plugin al arrancar Jellyfin,
/// pasándole el directorio oficial de logs del servidor. Al apagar, cierra el archivo
/// (flush). Se registra como <see cref="IHostedService"/> en el contenedor de DI.
/// </summary>
internal sealed class JellyTrendLogInitializer : IHostedService
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTrendLogInitializer"/> class.
    /// </summary>
    /// <param name="applicationPaths">The server application paths (log directory).</param>
    /// <param name="loggerFactory">The server's logger factory (fallback).</param>
    public JellyTrendLogInitializer(IApplicationPaths applicationPaths, ILoggerFactory loggerFactory)
    {
        _applicationPaths = applicationPaths;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        JellyTrendLog.Initialize(_applicationPaths, _loggerFactory);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        JellyTrendLog.Shutdown();
        return Task.CompletedTask;
    }
}
