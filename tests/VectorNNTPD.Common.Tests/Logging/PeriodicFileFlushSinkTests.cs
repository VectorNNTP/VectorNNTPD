using System.Reflection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Parsing;
using Serilog.Sinks.File;
using VectorNNTPD.Common.Logging;

namespace VectorNNTPD.Common.Tests.Logging
{
    /// <summary>
    /// Locks periodic flush ordering: a pending write is flushed on dispose, a timer failure is retried, and dispose runs once.
    /// </summary>
    public sealed class PeriodicFileFlushSinkTests
    {
        /// <summary>Dispose flushes a buffered event, and a second dispose does nothing.</summary>
        [Fact]
        public void Dispose_flushes_the_last_event_and_a_second_dispose_is_safe()
        {
            var directory = Directory.CreateTempSubdirectory("common-flush-").FullName;
            try
            {
                var path = Path.Join(directory, "app-.log");
                var rolling = new GzipRollingFileSink(
                    path,
                    new MessageTemplateTextFormatter("{Message}"),
                    fileSizeLimitBytes: null,
                    retainedFileCountLimit: 2,
                    encoding: null,
                    buffered: true,
                    RollingInterval.Infinite,
                    rollOnFileSizeLimit: false);
                using var sink = new PeriodicFileFlushSink(rolling, TimeSpan.FromHours(1));
                sink.Emit(Event("flushed-marker"));
                sink.Dispose();
                sink.Dispose();

                var written = File.ReadAllText(Directory.GetFiles(directory, "app*.log").Single());
                Assert.Contains("flushed-marker", written, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        /// <summary>An in-flight flush holds disposal until it leaves the flush lock.</summary>
        [Fact]
        public async Task Flush_callback_blocks_disposal_until_the_callback_leaves_the_lock()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var inner = new BlockingFlushSink(entered, release);
            using var sink = new PeriodicFileFlushSink(inner, TimeSpan.FromHours(1));
            sink.Emit(Event("race"));

            var callback = Task.Run(() => InvokeOnTick(sink));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var disposal = Task.Run(sink.Dispose);
            Assert.False(disposal.IsCompleted);
            release.SetResult();
            await callback.WaitAsync(TimeSpan.FromSeconds(2));
            await disposal.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, inner.FlushCount);
            Assert.Equal(1, inner.DisposeCount);
        }

        /// <summary>A timer flush failure is self-logged, and dispose retries that flush and then propagates it.</summary>
        [Fact]
        public void Timer_callback_contains_a_flush_failure()
        {
            var inner = new FaultingFlushSink { FlushException = new IOException("timer flush") };
            using var sink = new PeriodicFileFlushSink(inner, TimeSpan.FromHours(1));
            sink.Emit(Event("marker"));

            var diagnostic = SelfLogCapture.Capture(() => InvokeOnTick(sink));

            Assert.Contains("timer callback failed", diagnostic, StringComparison.Ordinal);
            Assert.Contains("timer flush", diagnostic, StringComparison.Ordinal);
            var disposeDiagnostic = SelfLogCapture.Capture(() =>
            {
                var error = Assert.Throws<IOException>(sink.Dispose);
                Assert.Equal("timer flush", error.Message);
            });
            Assert.Contains("failed during disposal", disposeDiagnostic, StringComparison.Ordinal);
            Assert.Equal(2, inner.FlushCount);
            Assert.Equal(1, inner.DisposeCount);
            AssertTimerDisposed(sink);
        }

        /// <summary>A flush that fails once succeeds on the next timer callback.</summary>
        [Fact]
        public void Timer_flush_failure_is_retried_by_the_next_callback()
        {
            var inner = new FaultingFlushSink
            {
                FlushException = new IOException("transient flush"),
                FailuresRemaining = 1,
            };
            using var sink = new PeriodicFileFlushSink(inner, TimeSpan.FromHours(1));
            sink.Emit(Event("marker"));

            var diagnostic = SelfLogCapture.Capture(() => InvokeOnTick(sink));
            InvokeOnTick(sink);
            sink.Dispose();

            Assert.Contains("timer callback failed", diagnostic, StringComparison.Ordinal);
            Assert.Equal(2, inner.FlushCount);
            Assert.Equal(1, inner.DisposeCount);
            AssertTimerDisposed(sink);
        }

        /// <summary>A final flush failure still disposes the inner sink and the timer.</summary>
        [Fact]
        public void Final_flush_failure_still_disposes_the_sink_and_timer()
        {
            var inner = new FaultingFlushSink { FlushException = new IOException("final flush") };
            using var sink = new PeriodicFileFlushSink(inner, TimeSpan.FromHours(1));
            sink.Emit(Event("marker"));

            var diagnostic = SelfLogCapture.Capture(() =>
            {
                var ex = Assert.Throws<IOException>(sink.Dispose);
                Assert.Equal("final flush", ex.Message);
            });

            Assert.Contains("failed during disposal", diagnostic, StringComparison.Ordinal);
            Assert.Equal(1, inner.DisposeCount);
            AssertTimerDisposed(sink);
        }

        /// <summary>An inner-sink dispose failure still disposes the timer, and the dispose exception propagates.</summary>
        [Fact]
        public void Inner_sink_disposal_failure_still_disposes_the_timer()
        {
            var inner = new FaultingFlushSink { DisposeException = new InvalidOperationException("sink dispose") };
            var sink = new PeriodicFileFlushSink(inner, TimeSpan.FromHours(1));

            var ex = Assert.Throws<InvalidOperationException>(sink.Dispose);

            Assert.Equal("sink dispose", ex.Message);
            Assert.Equal(1, inner.DisposeCount);
            AssertTimerDisposed(sink);
        }

        /// <summary>When flush and dispose both fail, the flush exception is thrown and the dispose failure is self-logged.</summary>
        [Fact]
        public void Flush_failure_stays_primary_when_disposal_also_fails()
        {
            var inner = new FaultingFlushSink
            {
                FlushException = new IOException("primary flush"),
                DisposeException = new NotSupportedException("secondary dispose"),
            };
            var sink = new PeriodicFileFlushSink(inner, TimeSpan.FromHours(1));
            sink.Emit(Event("marker"));

            var diagnostic = SelfLogCapture.Capture(() =>
            {
                var ex = Assert.Throws<IOException>(sink.Dispose);
                Assert.Equal("primary flush", ex.Message);
            });

            Assert.Contains("sink disposal failed", diagnostic, StringComparison.Ordinal);
            Assert.Contains("secondary dispose", diagnostic, StringComparison.Ordinal);
            Assert.Equal(1, inner.DisposeCount);
            AssertTimerDisposed(sink);
        }

        /// <summary>Two overlapping dispose calls dispose the inner sink once.</summary>
        [Fact]
        public async Task Concurrent_dispose_disposes_the_inner_sink_once()
        {
            var inner = new FaultingFlushSink();
            var sink = new PeriodicFileFlushSink(inner, TimeSpan.FromHours(1));
            var first = Task.Run(sink.Dispose);
            var second = Task.Run(sink.Dispose);
            await Task.WhenAll(first, second);

            Assert.Equal(1, inner.DisposeCount);
            AssertTimerDisposed(sink);
        }

        /// <summary>Builds an information event whose rendered message is <paramref name="text"/>.</summary>
        /// <param name="text">Message text.</param>
        /// <returns>The event.</returns>
        private static LogEvent Event(string text) =>
            new(
                DateTimeOffset.UtcNow,
                LogEventLevel.Information,
                exception: null,
                new MessageTemplateParser().Parse(text),
                []);

        /// <summary>Invokes the private timer callback.</summary>
        /// <param name="sink">Periodic flush sink.</param>
        private static void InvokeOnTick(PeriodicFileFlushSink sink)
        {
            var onTick = typeof(PeriodicFileFlushSink).GetMethod(
                "OnTick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onTick);
            onTick.Invoke(sink, [null]);
        }

        /// <summary>Asserts the timer rejects a further change because it has been disposed.</summary>
        /// <param name="sink">Disposed sink.</param>
        private static void AssertTimerDisposed(PeriodicFileFlushSink sink)
        {
            var timerField = typeof(PeriodicFileFlushSink).GetField(
                "_timer",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var timer = Assert.IsType<Timer>(timerField?.GetValue(sink));
            Assert.False(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        }

        /// <summary>Inner sink that blocks flush until released.</summary>
        private sealed class BlockingFlushSink : ILogEventSink, IFlushableFileSink, IDisposable
        {
            /// <summary>Set when flush begins.</summary>
            private readonly TaskCompletionSource _entered;

            /// <summary>Completed by the test to unblock flush.</summary>
            private readonly TaskCompletionSource _release;

            /// <summary>Flush entries.</summary>
            private int _flushCount;

            /// <summary>Dispose calls.</summary>
            private int _disposeCount;

            /// <summary>Creates a sink that signals <paramref name="entered"/> and waits on <paramref name="release"/>.</summary>
            /// <param name="entered">Set when flush begins.</param>
            /// <param name="release">Completes to unblock flush.</param>
            internal BlockingFlushSink(TaskCompletionSource entered, TaskCompletionSource release)
            {
                _entered = entered;
                _release = release;
            }

            /// <summary>Number of flush calls that entered the gate.</summary>
            internal int FlushCount => Volatile.Read(ref _flushCount);

            /// <summary>Number of dispose calls observed.</summary>
            internal int DisposeCount => Volatile.Read(ref _disposeCount);

            /// <inheritdoc />
            public void Emit(LogEvent logEvent)
            {
            }

            /// <inheritdoc />
            public void FlushToDisk()
            {
                Interlocked.Increment(ref _flushCount);
                _entered.TrySetResult();
                _release.Task.GetAwaiter().GetResult();
            }

            /// <inheritdoc />
            public void Dispose()
            {
                Interlocked.Increment(ref _disposeCount);
            }
        }

        /// <summary>Inner sink that throws on flush or dispose.</summary>
        private sealed class FaultingFlushSink : ILogEventSink, IFlushableFileSink, IDisposable
        {
            /// <summary>Dispose calls.</summary>
            private int _disposeCount;

            /// <summary>Flush attempts.</summary>
            private int _flushCount;

            /// <summary>Flush attempts that still throw. Starts so every flush throws until the test sets <see cref="FailuresRemaining"/>.</summary>
            private int _failuresRemaining = int.MaxValue;

            /// <summary>Exception thrown from flush when failures remain.</summary>
            public Exception? FlushException { get; init; }

            /// <summary>Exception thrown from dispose when set.</summary>
            public Exception? DisposeException { get; init; }

            /// <summary>Remaining flush attempts that throw <see cref="FlushException"/>.</summary>
            internal int FailuresRemaining
            {
                get => _failuresRemaining;
                init => _failuresRemaining = value;
            }

            /// <summary>Flush attempts observed.</summary>
            internal int FlushCount => Volatile.Read(ref _flushCount);

            /// <summary>Dispose attempts observed.</summary>
            internal int DisposeCount => Volatile.Read(ref _disposeCount);

            /// <inheritdoc />
            public void Emit(LogEvent logEvent)
            {
            }

            /// <inheritdoc />
            public void FlushToDisk()
            {
                Interlocked.Increment(ref _flushCount);
                if (FlushException is not null && Interlocked.Decrement(ref _failuresRemaining) >= 0)
                {
                    throw FlushException;
                }
            }

            /// <inheritdoc />
            public void Dispose()
            {
                Interlocked.Increment(ref _disposeCount);
                if (DisposeException is not null)
                {
                    throw DisposeException;
                }
            }
        }
    }
}
