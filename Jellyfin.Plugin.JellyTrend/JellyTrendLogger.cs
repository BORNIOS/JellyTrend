using System;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Jellyfin.Plugin.JellyTrend;

/// <summary>
/// Writes a single <see cref="ILogger"/> message to the plugin's Serilog logger.
/// </summary>
internal sealed class JellyTrendLogger : ILogger
{
    private readonly Serilog.ILogger _serilogLogger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTrendLogger"/> class.
    /// </summary>
    /// <param name="serilogLogger">The Serilog logger to write to.</param>
    public JellyTrendLogger(Serilog.ILogger serilogLogger)
    {
        _serilogLogger = serilogLogger;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel)
        => logLevel != LogLevel.None && _serilogLogger.IsEnabled(MapLevel(logLevel));

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        _serilogLogger.Write(MapLevel(logLevel), exception, "{Message}", message);
    }

    private static Serilog.Events.LogEventLevel MapLevel(LogLevel level)
        => level switch
        {
            LogLevel.Trace => Serilog.Events.LogEventLevel.Verbose,
            LogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
            LogLevel.Information => Serilog.Events.LogEventLevel.Information,
            LogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
            LogLevel.Error => Serilog.Events.LogEventLevel.Error,
            LogLevel.Critical => Serilog.Events.LogEventLevel.Fatal,
            _ => Serilog.Events.LogEventLevel.Debug
        };
}
