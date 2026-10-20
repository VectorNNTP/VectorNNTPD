using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Sinks.File;

namespace VectorNNTPD.Common.Logging
{
    /// <summary>
    /// Flushes an <see cref="IFlushableFileSink"/> on an interval and again when this sink is disposed.
    /// </summary>
    /// <remarks>
    /// Serilog disposes this object with the logger. <see cref="Dispose"/> stops the timer, waits until an
    /// in-flight callback has left the flush lock, flushes any remaining buffered write, then disposes the
    /// inner sink and the timer. A flush, inner-sink, or timer failure is reported through <see cref="SelfLog"/>
    /// so the diagnostic cannot re-enter this file sink, and is not rethrown: logger disposal must not turn a
    /// clean process exit into a failure. A timer callback contains every flush failure and leaves the
    /// checkpoint pending so a later tick or <see cref="Dispose"/> retries it. A second <see cref="Dispose"/>
    /// does nothing.
    /// </remarks>
    public sealed class PeriodicFileFlushSink : ILogEventSink, IDisposable
    {
        /// <summary>Serializes flush with dispose. Emit does not take this lock.</summary>
        private readonly object _gate = new();

        /// <summary>Inner sink that receives every event. Disposed by <see cref="Dispose"/> when it implements <see cref="IDisposable"/>.</summary>
        private readonly ILogEventSink _sink;

        /// <summary>Same instance as <see cref="_sink"/>, used for <see cref="IFlushableFileSink.FlushToDisk"/>.</summary>
        private readonly IFlushableFileSink _flushable;

        /// <summary>Periodic flush timer. Stopped and disposed by <see cref="Dispose"/>.</summary>
        private readonly Timer _timer;

        /// <summary><c>1</c> when an event has been emitted since the last successful flush. A failed flush sets it again.</summary>
        private int _flushRequired;

        /// <summary><c>1</c> after the first <see cref="Dispose"/>. The timer callback returns without flushing once this is set.</summary>
        private int _disposed;

        /// <summary>Wraps <paramref name="sink"/> and flushes it on <paramref name="flushInterval"/>.</summary>
        /// <param name="sink">Inner sink. It must implement <see cref="IFlushableFileSink"/>.</param>
        /// <param name="flushInterval">Delay before the first flush and between later flushes.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="sink"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="sink"/> is not an <see cref="IFlushableFileSink"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="flushInterval"/> is not positive.</exception>
        public PeriodicFileFlushSink(ILogEventSink sink, TimeSpan flushInterval)
        {
            ArgumentNullException.ThrowIfNull(sink);
            if (flushInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(flushInterval), flushInterval, "Flush interval must be positive.");
            }

            if (sink is not IFlushableFileSink flushable)
            {
                throw new ArgumentException("The sink must implement IFlushableFileSink.", nameof(sink));
            }

            _sink = sink;
            _flushable = flushable;
            _timer = new Timer(OnTick, null, flushInterval, flushInterval);
        }

        /// <summary>Writes one event and records that a later flush is required.</summary>
        /// <param name="logEvent">Event forwarded to the inner sink.</param>
        public void Emit(LogEvent logEvent)
        {
            _sink.Emit(logEvent);
            Interlocked.Exchange(ref _flushRequired, 1);
        }

        /// <summary>
        /// Stops the timer, flushes the last buffered write, and disposes the inner sink and timer.
        /// </summary>
        /// <remarks>
        /// Flush and dispose failures are written to <see cref="SelfLog"/> and do not escape.
        /// Cleanup of the inner sink and timer still runs after a flush failure.
        /// </remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException ex)
            {
                ReportDiagnostic("Periodic file flush timer was already stopped.", ex);
            }

            lock (_gate)
            {
                try
                {
                    FlushRequired();
                }
                // codeql[cs/catch-of-all-exceptions] A flush, dispose, or diagnostic failure must not escape this sink or skip the remaining cleanup.
                catch (Exception ex)
                {
                    ReportDiagnostic("Periodic file flush failed during disposal.", ex);
                }
            }

            try
            {
                (_sink as IDisposable)?.Dispose();
            }
            // codeql[cs/catch-of-all-exceptions] A flush, dispose, or diagnostic failure must not escape this sink or skip the remaining cleanup.
            catch (Exception ex)
            {
                ReportDiagnostic("Periodic file sink disposal failed.", ex);
            }

            try
            {
                _timer.Dispose();
            }
            // codeql[cs/catch-of-all-exceptions] A flush, dispose, or diagnostic failure must not escape this sink or skip the remaining cleanup.
            catch (Exception ex)
            {
                ReportDiagnostic("Periodic file flush timer disposal failed.", ex);
            }
        }

        /// <summary>
        /// Flushes when a write is pending and the sink has not been disposed. A flush failure is reported and left pending for a later tick.
        /// </summary>
        /// <param name="state">Unused timer state.</param>
        private void OnTick(object? state)
        {
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                lock (_gate)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }

                    FlushRequired();
                }
            }
            // codeql[cs/catch-of-all-exceptions] A flush, dispose, or diagnostic failure must not escape this sink or skip the remaining cleanup.
            catch (Exception ex)
            {
                ReportDiagnostic("Periodic file flush timer callback failed.", ex);
            }
        }

        /// <summary>
        /// Writes <paramref name="message"/> and <paramref name="exception"/> to <see cref="SelfLog"/>.
        /// A failure inside that write is ignored so timer and dispose paths cannot fail because diagnostics failed.
        /// </summary>
        private static void ReportDiagnostic(string message, Exception exception)
        {
            try
            {
                SelfLog.WriteLine("{0} {1}", message, exception);
            }
            // codeql[cs/catch-of-all-exceptions] A flush, dispose, or diagnostic failure must not escape this sink or skip the remaining cleanup.
            catch (Exception)
            {
                // The diagnostic channel is best-effort. It must not recurse into this sink or fault the timer thread.
            }
        }

        /// <summary>
        /// Flushes when <see cref="_flushRequired"/> is set. A flush failure restores the flag before the exception propagates.
        /// </summary>
        /// <remarks>
        /// The timer callback contains that exception and leaves the flag set, so a later tick retries the checkpoint.
        /// <see cref="Dispose"/> calls this method once more, so a failed timer flush is attempted again before the inner sink is disposed.
        /// </remarks>
        private void FlushRequired()
        {
            if (Interlocked.Exchange(ref _flushRequired, 0) != 1)
            {
                return;
            }

            try
            {
                _flushable.FlushToDisk();
            }
            catch
            {
                // A failed checkpoint must stay pending for the next tick and for disposal.
                Interlocked.Exchange(ref _flushRequired, 1);
                throw;
            }
        }
    }
}
