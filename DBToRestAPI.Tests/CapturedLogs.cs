using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DBToRestAPI.Tests;

/// <summary>
/// Keeps every log entry the engine writes, at every level, so a test can look for one.
/// </summary>
public sealed class CapturedLogs : ILoggerProvider
{
    public sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception)
    {
        /// <summary>What a log sink writes: the message, then the exception with its inner ones.</summary>
        public string Text => Exception == null ? Message : Message + Environment.NewLine + Exception;
    }

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyCollection<Entry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private sealed class Logger(CapturedLogs logs, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => logs._entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), exception));
    }
}
