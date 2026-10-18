using Serilog;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Parsing;
using Serilog.Sinks.File;
using VectorNNTPD.Common.Logging;

namespace VectorNNTPD.Common.Tests.Logging
{
    /// <summary>
    /// Locks rolling checkpoints, path-segment rejection, and cancellation during archive.
    /// </summary>
    public sealed class GzipRollingFileCheckpointTests
    {
        /// <summary>Daily rolling checkpoint handles leap day and the next day with the original <see cref="DateTimeKind"/>.</summary>
        [Fact]
        public void Daily_checkpoint_crosses_29_February_and_keeps_kind()
        {
            using var sink = Create(RollingInterval.Day);
            var leap = new DateTime(2024, 2, 29, 15, 4, 5, DateTimeKind.Utc);

            Assert.Equal(new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc), sink.Checkpoint(leap));
            Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), sink.NextCheckpoint(leap));
        }

        /// <summary>Monthly checkpoint rolls December to January and handles a month-end date.</summary>
        [Fact]
        public void Monthly_checkpoint_crosses_December_and_month_end()
        {
            using var sink = Create(RollingInterval.Month);
            var end = new DateTime(2024, 12, 31, 23, 0, 0, DateTimeKind.Utc);
            var january = new DateTime(2024, 1, 31, 8, 0, 0, DateTimeKind.Unspecified);

            Assert.Equal(new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc), sink.Checkpoint(end));
            Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), sink.NextCheckpoint(end));
            Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), sink.Checkpoint(january));
            Assert.Equal(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Unspecified), sink.NextCheckpoint(january));
        }

        /// <summary>Year 9999 has a checkpoint and its next checkpoint cannot be represented.</summary>
        [Fact]
        public void Yearly_checkpoint_rejects_a_year_that_cannot_be_represented()
        {
            using var sink = Create(RollingInterval.Year);
            var last = new DateTime(9999, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.Equal(new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc), sink.Checkpoint(last));
            Assert.Throws<ArgumentOutOfRangeException>(() => sink.NextCheckpoint(last));
        }

        /// <summary>Hourly checkpoint crosses midnight and preserves <see cref="DateTimeKind"/>.</summary>
        [Fact]
        public void Hourly_checkpoint_crosses_midnight()
        {
            using var sink = Create(RollingInterval.Hour);
            var instant = new DateTime(2024, 2, 29, 23, 30, 0, DateTimeKind.Local);

            Assert.Equal(new DateTime(2024, 2, 29, 23, 0, 0, DateTimeKind.Local), sink.Checkpoint(instant));
            Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Local), sink.NextCheckpoint(instant));
        }

        /// <summary>A rolling path whose file name is <c>..</c> is rejected before a file is opened.</summary>
        [Fact]
        public void Log_file_name_cannot_leave_the_directory()
        {
            var directory = Directory.CreateTempSubdirectory("common-roll-name-").FullName;
            try
            {
                Assert.Throws<ArgumentException>(() => Create(RollingInterval.Infinite, Path.Join(directory, "..")));
            }
            finally
            {
                Directory.Delete(directory);
            }
        }

        /// <summary>
        /// A completion handler that throws <see cref="OperationCanceledException"/> propagates, and a non-cancellation failure is self-logged while compression continues.
        /// </summary>
        [Fact]
        public void Completed_handler_cancellation_propagates_and_other_failures_still_compress()
        {
            var directory = Directory.CreateTempSubdirectory("common-roll-cancel-").FullName;
            var cancelDirectory = Directory.CreateTempSubdirectory("common-roll-cancel-oce-").FullName;
            try
            {
                var path = Path.Join(directory, "app-.log");
                var clock = new MutableTime(new DateTime(2026, 10, 1, 8, 0, 0));
                using (var sink = Create(RollingInterval.Day, path, clock, _ => throw new InvalidOperationException("handler-broke")))
                {
                    sink.Emit(Event("day-one"));
                    clock.AdvanceDays(1);
                    var diagnostic = SelfLogCapture.Capture(() => sink.Emit(Event("day-two")));
                    Assert.Contains("Completed-log handler failed", diagnostic, StringComparison.Ordinal);
                    Assert.Contains("handler-broke", diagnostic, StringComparison.Ordinal);
                }

                Assert.True(File.Exists(Path.Join(directory, "app-20261001.log.gz")));

                var cancelPath = Path.Join(cancelDirectory, "app-.log");
                var cancelClock = new MutableTime(new DateTime(2026, 10, 3, 8, 0, 0));
                using var cancelled = Create(
                    RollingInterval.Day,
                    cancelPath,
                    cancelClock,
                    _ => throw new OperationCanceledException("stop-archive"));
                cancelled.Emit(Event("day-three"));
                cancelClock.AdvanceDays(1);
                var error = Assert.Throws<OperationCanceledException>(() => cancelled.Emit(Event("day-four")));
                Assert.Equal("stop-archive", error.Message);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
                Directory.Delete(cancelDirectory, recursive: true);
            }
        }

        /// <summary>Builds a rolling sink.</summary>
        /// <param name="interval">Rolling interval under test.</param>
        /// <param name="path">Optional file path. A temp checkpoint name is used when omitted.</param>
        /// <param name="time">Clock for roll checkpoints. <see langword="null"/> uses <see cref="TimeProvider.System"/>.</param>
        /// <param name="onCompleted">Completion callback, or <see langword="null"/>.</param>
        /// <returns>The sink. The caller disposes it.</returns>
        private static GzipRollingFileSink Create(
            RollingInterval interval,
            string? path = null,
            TimeProvider? time = null,
            Action<string>? onCompleted = null)
        {
            path ??= Path.Join(Path.GetTempPath(), "common-checkpoint-.log");
            return new GzipRollingFileSink(
                path,
                new MessageTemplateTextFormatter("{Message}"),
                fileSizeLimitBytes: null,
                retainedFileCountLimit: 2,
                encoding: null,
                buffered: false,
                interval,
                rollOnFileSizeLimit: false,
                onCompletedFile: onCompleted,
                timeProvider: time);
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

        /// <summary>Clock whose local time the test advances and which reports that time as UTC.</summary>
        private sealed class MutableTime : TimeProvider
        {
            /// <summary>Creates a clock starting at <paramref name="local"/>.</summary>
            /// <param name="local">Initial local time.</param>
            internal MutableTime(DateTime local) => Local = local;

            /// <summary>Current local time returned by <see cref="GetUtcNow"/> with offset zero.</summary>
            internal DateTime Local { get; private set; }

            /// <summary>Adds <paramref name="days"/> to <see cref="Local"/>.</summary>
            /// <param name="days">Number of days to add.</param>
            internal void AdvanceDays(int days) => Local = Local.AddDays(days);

            /// <inheritdoc />
            public override DateTimeOffset GetUtcNow() => new(Local, TimeSpan.Zero);

            /// <inheritdoc />
            public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        }
    }
}
