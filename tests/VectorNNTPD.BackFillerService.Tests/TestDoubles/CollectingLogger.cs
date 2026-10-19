using Microsoft.Extensions.Logging;

namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>One captured log line with level, event id, formatted message text, and logger state.</summary>
    /// <param name="Level">Log level passed to <see cref="ILogger.Log{TState}"/>.</param>
    /// <param name="EventId">Event id passed to the log call.</param>
    /// <param name="Message">Formatted message; a second entry may be added for <see cref="Exception.Message"/>.</param>
    /// <param name="State">Logger state passed to <see cref="ILogger.Log{TState}"/>. Null on the extra exception-message entry.</param>
    internal readonly record struct CollectedLog(LogLevel Level, EventId EventId, string Message, object? State = null);

    /// <summary>
    /// <see cref="ILogger{T}"/> double that records formatted messages (and exception messages) for assertions.
    /// Does not forward to a real logging pipeline; scopes are no-ops and all levels are enabled.
    /// </summary>
    internal sealed class CollectingLogger<T> : ILogger<T>
    {
        /// <summary>Captured log entries guarded for concurrent assertions.</summary>
        private readonly List<CollectedLog> _entries = [];

        /// <summary>Formatted messages in capture order (thread-safe snapshot).</summary>
        internal IReadOnlyList<string> Messages
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries.Select(static entry => entry.Message)];
                }
            }
        }

        /// <summary>Structured capture entries in insertion order (thread-safe snapshot).</summary>
        internal IReadOnlyList<CollectedLog> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var message = formatter(state, exception);
            lock (_entries)
            {
                _entries.Add(new CollectedLog(logLevel, eventId, message, state));
                if (exception is not null)
                {
                    _entries.Add(new CollectedLog(logLevel, eventId, exception.Message));
                }
            }
        }

        /// <summary>No-op logging scope returned from <see cref="BeginScope{TState}"/>.</summary>
        private sealed class NullScope : IDisposable
        {
            /// <summary>Shared no-op scope instance.</summary>
            public static NullScope Instance { get; } = new();

            /// <inheritdoc />
            public void Dispose()
            {
            }
        }
    }
}
