using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VPT.Einkr.CairnsAgency;

/// <summary>A minimal daily-rolling file logger, so a service running with no console still leaves a trail Josh can read.</summary>
public sealed class FileLoggerProvider(string directory, LogLevel minimum) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly object _gate = new();

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, n => new FileLogger(n, this));
    public void Dispose() { }

    internal void Write(LogLevel level, string category, string message, Exception? ex)
    {
        if (level < minimum) return;
        try
        {
            Directory.CreateDirectory(directory);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString()[..3].ToUpperInvariant()}] {category.Split('.').Last()}: {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}";
            lock (_gate) File.AppendAllText(Path.Combine(directory, $"agency-{DateTime.Now:yyyyMMdd}.log"), line);
        }
        catch { /* logging must never take the service down */ }
    }

    private sealed class FileLogger(string category, FileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._minimum;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }

    private LogLevel _minimum => minimum;
}
