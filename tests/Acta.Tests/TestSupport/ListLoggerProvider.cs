using Microsoft.Extensions.Logging;

namespace Acta.Tests.TestSupport;

/// <summary>
/// Lightweight <see cref="ILoggerProvider"/> test double: every logged message is appended, along
/// with its <see cref="LogLevel"/>, to a thread-safe in-memory list — so a test can assert that a
/// specific message was logged at a specific level (e.g. the D14 "SINGLE-PROCESS" startup
/// warning) without standing up a real logging sink.
/// </summary>
public sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly List<Entry> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>A single captured log entry: the level it was logged at and its rendered message.</summary>
    /// <param name="Level">The <see cref="LogLevel"/> the entry was logged at.</param>
    /// <param name="Message">The fully rendered (formatter-applied) log message.</param>
    public readonly record struct Entry(LogLevel Level, string Message);

    /// <summary>A thread-safe snapshot of every entry captured so far, in logging order.</summary>
    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new ListLogger(this);

    /// <inheritdoc/>
    public void Dispose()
    {
        // No unmanaged resources or event subscriptions to release — an in-memory sink only.
    }

    private void Add(LogLevel level, string message)
    {
        lock (_gate)
        {
            _entries.Add(new Entry(level, message));
        }
    }

    private sealed class ListLogger(ListLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => owner.Add(logLevel, formatter(state, exception));
    }
}
