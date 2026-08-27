using System;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Jellyfin.Plugin.JellyTrend;

/// <summary>
/// Adapta la API <c>Microsoft.Extensions.Logging.ILoggerFactory</c> al logger de Serilog
/// del plugin, de modo que el código existente que usa <see cref="ILogger"/> siga
/// funcionando sin cambios y termine escribiendo en el archivo de log dedicado.
/// </summary>
internal sealed class JellyTrendLoggerFactory : ILoggerFactory
{
    private readonly Serilog.ILogger _serilogLogger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTrendLoggerFactory"/> class.
    /// </summary>
    /// <param name="serilogLogger">The Serilog logger to write to.</param>
    public JellyTrendLoggerFactory(Serilog.ILogger serilogLogger)
    {
        _serilogLogger = serilogLogger;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
        => new JellyTrendLogger(_serilogLogger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, categoryName));

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <inheritdoc />
    public void Dispose() => (_serilogLogger as IDisposable)?.Dispose();
}
