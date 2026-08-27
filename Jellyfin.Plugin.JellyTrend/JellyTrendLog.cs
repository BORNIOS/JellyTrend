using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Jellyfin.Plugin.JellyTrend;

/// <summary>
/// Logger propio del plugin JellyTrend. Escribe en un archivo de log dedicado dentro del
/// directorio de logs oficial de Jellyfin (<c>log/JellyTrend-YYYYMM.log</c>), con rotación
/// mensual y límites de tamaño/retención para que nunca crezca sin control. Los mensajes
/// llevan la categoría "JellyTrend" (más una sub-categoría por área) en el campo
/// SourceContext, por lo que son fáciles de localizar y filtrar.
/// </summary>
internal static class JellyTrendLog
{
    private const int FileSizeLimitBytes = 10 * 1024 * 1024;
    private const int RetainedFileCount = 6;

    private static readonly object SyncRoot = new();
    private static Serilog.ILogger? _serilog;
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    /// <summary>
    /// Initializes the plugin's dedicated file logger (idempotent). Se llama una vez al
    /// arrancar desde <see cref="JellyTrendLogInitializer"/> con el directorio de logs real.
    /// Si el archivo no puede crearse (permisos, bloqueo…), cae al logger del servidor para
    /// no perder mensajes.
    /// </summary>
    /// <param name="applicationPaths">The server application paths (log directory).</param>
    /// <param name="serverLoggerFactory">The server's logger factory, used as fallback.</param>
    public static void Initialize(IApplicationPaths? applicationPaths, ILoggerFactory serverLoggerFactory)
    {
        lock (SyncRoot)
        {
            if (_factory is not NullLoggerFactory)
            {
                return;   // ya inicializado
            }

            var logDirectory = applicationPaths?.LogDirectoryPath;
            if (!string.IsNullOrWhiteSpace(logDirectory))
            {
                try
                {
                    _serilog = new LoggerConfiguration()
                        .MinimumLevel.Debug()
                        .WriteTo.File(
                            path: Path.Combine(logDirectory, "JellyTrend-.log"),
                            formatProvider: CultureInfo.InvariantCulture,
                            rollingInterval: RollingInterval.Month,
                            fileSizeLimitBytes: FileSizeLimitBytes,
                            rollOnFileSizeLimit: true,
                            retainedFileCountLimit: RetainedFileCount,
                            shared: true,
                            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                        .CreateLogger();

                    _factory = new JellyTrendLoggerFactory(_serilog);
                    return;
                }
                catch
                {
                    // El archivo no pudo crearse — se usa el logger del servidor.
                }
            }

            _factory = serverLoggerFactory ?? NullLoggerFactory.Instance;
        }
    }

    /// <summary>
    /// Initializes the logger using only the server factory (fallback path invoked from
    /// constructors before the initializer runs).
    /// </summary>
    /// <param name="serverLoggerFactory">The server's logger factory.</param>
    public static void Initialize(ILoggerFactory serverLoggerFactory)
        => Initialize(null, serverLoggerFactory);

    /// <summary>
    /// Disposes the dedicated file logger, flushing pending writes.
    /// </summary>
    public static void Shutdown()
    {
        lock (SyncRoot)
        {
            (_serilog as IDisposable)?.Dispose();
            _serilog = null;
        }
    }

    /// <summary>
    /// Creates (or reuses) a logger under the shared "JellyTrend" category.
    /// </summary>
    /// <param name="area">Optional sub-area, e.g. "Recommended", "Trending" or "Sync".</param>
    /// <returns>The logger instance.</returns>
    public static ILogger CreateLogger(string area = "")
        => _factory.CreateLogger(string.IsNullOrEmpty(area) ? "JellyTrend" : "JellyTrend." + area);

    /// <summary>
    /// Scope de una ejecución de tarea programada: registra el inicio en Debug y emite
    /// una única línea de resumen en Info al completar (o Error al fallar), incluyendo la
    /// duración. Los detalles internos de cada tarea deben quedar en Debug para que el log
    /// no se convierta en un registro gigante: por cada ejecución solo se escribe una línea
    /// visible en nivel Info.
    /// </summary>
    internal sealed class TaskScope : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _task;
        private readonly Stopwatch _stopwatch;
        private bool _done;

        private TaskScope(ILogger logger, string task)
        {
            _logger = logger;
            _task = task;
            _stopwatch = Stopwatch.StartNew();
            _logger.LogDebug("Ejecutando {Task}.", task);
        }

        /// <summary>
        /// Starts a new task execution scope.
        /// </summary>
        /// <param name="logger">The plugin logger.</param>
        /// <param name="task">The human-readable task name.</param>
        /// <returns>The task scope to complete or dispose.</returns>
        public static TaskScope Begin(ILogger logger, string task) => new(logger, task);

        /// <summary>
        /// Marks the task as completed, emitting a single Info line with duration and summary.
        /// </summary>
        /// <param name="summary">A short one-line summary of the outcome.</param>
        public void Complete(string summary)
        {
            if (_done)
            {
                return;
            }

            _done = true;
            _logger.LogInformation("Tarea {Task} completada en {Elapsed} ms: {Summary}.", _task, _stopwatch.ElapsedMilliseconds, summary);
        }

        /// <summary>
        /// Marks the task as failed, emitting an Error line with the exception and duration.
        /// </summary>
        /// <param name="exception">The exception that caused the failure.</param>
        /// <param name="summary">A short one-line summary of the failure.</param>
        public void Fail(Exception exception, string summary)
        {
            if (_done)
            {
                return;
            }

            _done = true;
            _logger.LogError(exception, "Tarea {Task} falló tras {Elapsed} ms: {Summary}.", _task, _stopwatch.ElapsedMilliseconds, summary);
        }

        /// <summary>
        /// Marks the task as cancelled (e.g. server shutdown or manual cancel), a normal
        /// condition that should not pollute the log.
        /// </summary>
        /// <param name="summary">A short one-line summary.</param>
        public void Cancel(string summary)
        {
            if (_done)
            {
                return;
            }

            _done = true;
            _logger.LogInformation("Tarea {Task} cancelada tras {Elapsed} ms: {Summary}.", _task, _stopwatch.ElapsedMilliseconds, summary);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Si nadie llamó Complete/Fail/Cancel explícitamente, cierra la ejecución en
            // Debug para no dejar una ejecución huérfana y evitar duplicar líneas visibles.
            if (!_done)
            {
                _done = true;
                _logger.LogDebug("Tarea {Task} finalizada tras {Elapsed} ms sin resumen.", _task, _stopwatch.ElapsedMilliseconds);
            }
        }
    }
}
