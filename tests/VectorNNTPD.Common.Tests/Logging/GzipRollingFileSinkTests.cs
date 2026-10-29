using System.IO.Compression;
using System.Text;
using Serilog;
using Serilog.Sinks.File;
using VectorNNTPD.Common.Logging;

namespace VectorNNTPD.Common.Tests.Logging
{
    /// <summary>
    /// Locks daily gzip rolling: completed days are compressed, the active file is left plain, and retention counts both forms.
    /// </summary>
    public sealed class GzipRollingFileSinkTests
    {
        /// <summary>
        /// Asserts that advancing one local day compresses the completed log, leaves the new day uncompressed, and passes the completed bytes to the completion callback.
        /// </summary>
        [Fact]
        public void DailyRoll_CompressesTheCompletedDay_AndLeavesTheActiveLog()
        {
            using var dir = new TempDir();
            var time = new ManualTime(new DateTime(2026, 10, 1, 12, 0, 0));
            byte[]? completed = null;
            using (var logger = Create(dir.Path, time, retention: 14, onCompleted: path => completed = File.ReadAllBytes(path)))
            {
                logger.Information("day-one");
                time.AdvanceDays(1);
                logger.Information("day-two");
            }

            var dayOne = Path.Join(dir.Path, "app-20261001.log");
            var dayTwo = Path.Join(dir.Path, "app-20261002.log");
            Assert.False(File.Exists(dayOne));
            Assert.True(File.Exists(dayOne + ".gz"));
            Assert.True(File.Exists(dayTwo));
            Assert.False(File.Exists(dayTwo + ".gz"));
            Assert.NotNull(completed);
            Assert.Equal(completed, Inflate(dayOne + ".gz"));
            Assert.Contains("day-one", Encoding.UTF8.GetString(completed), StringComparison.Ordinal);
            Assert.Contains("day-two", File.ReadAllText(dayTwo), StringComparison.Ordinal);
            Assert.DoesNotContain("day-two", Encoding.UTF8.GetString(completed), StringComparison.Ordinal);
        }

        /// <summary>
        /// Asserts that a retention of three, after five daily rolls, keeps the active log and the two newest gzip files.
        /// </summary>
        [Fact]
        public void Retention_CountsLogAndGzipDays_IncludingTheActiveFile()
        {
            using var dir = new TempDir();
            var time = new ManualTime(new DateTime(2026, 10, 1, 8, 0, 0));
            using (var logger = Create(dir.Path, time, retention: 3))
            {
                for (var day = 0; day < 5; day++)
                {
                    logger.Information("day-{Day}", time.Local.ToString("yyyyMMdd"));
                    if (day < 4)
                    {
                        time.AdvanceDays(1);
                    }
                }
            }

            var files = Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(
                new[] { "app-20261003.log.gz", "app-20261004.log.gz", "app-20261005.log" },
                files);
        }

        /// <summary>
        /// Asserts that an existing gzip and an existing plain log are recognized on startup, the plain log is compressed, and a later restart drops files outside the retention window.
        /// </summary>
        [Fact]
        public void Restart_RecognizesExistingLogAndGzip_AndAppliesRetention()
        {
            using var dir = new TempDir();
            File.WriteAllBytes(Path.Join(dir.Path, "app-20261001.log.gz"), Gzip("already"));
            File.WriteAllText(Path.Join(dir.Path, "app-20261002.log"), "plain-yesterday");
            var time = new ManualTime(new DateTime(2026, 10, 4, 8, 0, 0));
            using (var logger = Create(dir.Path, time, retention: 3))
            {
                logger.Information("today");
            }

            Assert.False(File.Exists(Path.Join(dir.Path, "app-20261002.log")));
            Assert.Equal("plain-yesterday", Encoding.UTF8.GetString(Inflate(Path.Join(dir.Path, "app-20261002.log.gz"))));
            Assert.True(File.Exists(Path.Join(dir.Path, "app-20261001.log.gz")));
            Assert.True(File.Exists(Path.Join(dir.Path, "app-20261004.log")));
            Assert.False(File.Exists(Path.Join(dir.Path, "app-20261004.log.gz")));

            time.AdvanceDays(2);
            using (var logger = Create(dir.Path, time, retention: 3))
            {
                logger.Information("later");
            }

            var files = Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(
                new[] { "app-20261002.log.gz", "app-20261004.log.gz", "app-20261006.log" },
                files);
        }

        /// <summary>
        /// Asserts that a compressor which reports failure leaves the completed <c>.log</c> in place and does not leave a <c>.gz</c> or partial gzip beside it.
        /// </summary>
        [Fact]
        public void CompressionFailure_KeepsTheOriginalLog()
        {
            using var dir = new TempDir();
            var time = new ManualTime(new DateTime(2026, 10, 1, 8, 0, 0));
            using (var logger = Create(dir.Path, time, retention: 14, compressor: new FailingCompressor()))
            {
                logger.Information("keep-me");
                time.AdvanceDays(1);
                logger.Information("next");
            }

            var failed = Path.Join(dir.Path, "app-20261001.log");
            Assert.True(File.Exists(failed));
            Assert.False(File.Exists(failed + ".gz"));
            Assert.False(File.Exists(failed + GzipLogFileCompressor.PartialSuffix));
            Assert.Contains("keep-me", File.ReadAllText(failed), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Join(dir.Path, "app-20261002.log")));
        }

        /// <summary>
        /// Asserts that closing a logger on the same local day does not compress or delete the active file and does not invoke the completion callback, even when retention is one.
        /// </summary>
        [Fact]
        public void ActiveFile_IsNotCompressedOrDeleted()
        {
            using var dir = new TempDir();
            var time = new ManualTime(new DateTime(2026, 10, 5, 8, 0, 0));
            string? handed = null;
            using (var logger = Create(dir.Path, time, retention: 1, onCompleted: path => handed = path))
            {
                logger.Information("only-today");
            }

            var active = Path.Join(dir.Path, "app-20261005.log");
            Assert.True(File.Exists(active));
            Assert.False(File.Exists(active + ".gz"));
            Assert.Null(handed);
            Assert.Single(Directory.GetFiles(dir.Path));
        }

        /// <summary>
        /// Asserts that disposing an async buffered logger leaves that day's file plain, and that the next day's logger compresses the completed file.
        /// </summary>
        [Fact]
        public void AsyncFlush_ThenNextDay_CompressesTheCompletedFile()
        {
            using var dir = new TempDir();
            var time = new ManualTime(new DateTime(2026, 10, 1, 8, 0, 0));
            byte[]? completed = null;
            using (var logger = Create(dir.Path, time, retention: 14, async: true, onCompleted: path => completed = File.ReadAllBytes(path)))
            {
                logger.Information("buffered-day");
            }

            var dayOne = Path.Join(dir.Path, "app-20261001.log");
            Assert.True(File.Exists(dayOne));
            Assert.Contains("buffered-day", File.ReadAllText(dayOne), StringComparison.Ordinal);
            Assert.Null(completed);

            time.AdvanceDays(1);
            using (var logger = Create(dir.Path, time, retention: 14, async: true, onCompleted: path => completed = File.ReadAllBytes(path)))
            {
                logger.Information("next-buffered-day");
            }

            Assert.False(File.Exists(dayOne));
            Assert.NotNull(completed);
            Assert.Equal(completed, Inflate(dayOne + ".gz"));
            Assert.Contains("buffered-day", Encoding.UTF8.GetString(completed), StringComparison.Ordinal);
            Assert.Contains("next-buffered-day", File.ReadAllText(Path.Join(dir.Path, "app-20261002.log")), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Join(dir.Path, "app-20261002.log.gz")));
        }

        /// <summary>
        /// Asserts that a leftover partial gzip is removed on restart and the surviving source log is compressed.
        /// </summary>
        [Fact]
        public void InterruptedGzip_IsDiscarded_AndTheSourceIsCompressedOnRestart()
        {
            using var dir = new TempDir();
            var source = Path.Join(dir.Path, "app-20261001.log");
            File.WriteAllText(source, "survived-the-crash");
            File.WriteAllBytes(source + GzipLogFileCompressor.PartialSuffix, [1, 2, 3, 4]);
            var time = new ManualTime(new DateTime(2026, 10, 2, 8, 0, 0));
            using (var logger = Create(dir.Path, time, retention: 14))
            {
                logger.Information("today");
            }

            Assert.False(File.Exists(source));
            Assert.False(File.Exists(source + GzipLogFileCompressor.PartialSuffix));
            Assert.Equal("survived-the-crash", Encoding.UTF8.GetString(Inflate(source + ".gz")));
            Assert.True(File.Exists(Path.Join(dir.Path, "app-20261002.log")));
            Assert.False(File.Exists(Path.Join(dir.Path, "app-20261002.log.gz")));
        }

        /// <summary>
        /// Asserts that an existing <c>.gz</c> is left byte-for-byte unchanged and a leftover plain log beside it is removed.
        /// </summary>
        [Fact]
        public void ExistingGzip_IsNotRecompressed_AndLeftoverLogIsRemoved()
        {
            using var dir = new TempDir();
            var gzip = Gzip("stable");
            var path = Path.Join(dir.Path, "app-20261001.log");
            File.WriteAllBytes(path + ".gz", gzip);
            File.WriteAllText(path, "leftover");
            var time = new ManualTime(new DateTime(2026, 10, 2, 8, 0, 0));
            using (var logger = Create(dir.Path, time, retention: 14))
            {
                logger.Information("today");
            }

            Assert.Equal(gzip, File.ReadAllBytes(path + ".gz"));
            Assert.False(File.Exists(path));
        }

        /// <summary>
        /// Creates a debug logger writing <c>app-.log</c> through the daily gzip sink, optionally buffered on the async sink.
        /// </summary>
        /// <param name="directory">Directory that receives the rolled files.</param>
        /// <param name="time">Clock the sink uses for the local roll date.</param>
        /// <param name="retention">Retained file count, counting plain and gzip days.</param>
        /// <param name="async"><see langword="true"/> to buffer events before the gzip sink.</param>
        /// <param name="compressor">Compressor override, or <see langword="null"/> for the production compressor.</param>
        /// <param name="onCompleted">Callback receiving a completed file path, or <see langword="null"/>.</param>
        /// <returns>The logger. The caller disposes it to close the active file.</returns>
        private static Serilog.Core.Logger Create(
            string directory,
            ManualTime time,
            int retention,
            bool async = false,
            ILogFileCompressor? compressor = null,
            Action<string>? onCompleted = null)
        {
            var path = Path.Join(directory, "app-.log");
            var configuration = new LoggerConfiguration().MinimumLevel.Debug();
            if (async)
            {
                configuration.WriteTo.Async(
                    sink => sink.DailyGzipFile(
                        path,
                        buffered: true,
                        flushToDiskInterval: TimeSpan.FromSeconds(1),
                        rollingInterval: RollingInterval.Day,
                        rollOnFileSizeLimit: false,
                        retainedFileCountLimit: retention,
                        onCompletedFile: onCompleted,
                        compressor: compressor,
                        timeProvider: time),
                    bufferSize: 10,
                    blockWhenFull: true);
            }
            else
            {
                configuration.WriteTo.DailyGzipFile(
                    path,
                    buffered: false,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: false,
                    retainedFileCountLimit: retention,
                    onCompletedFile: onCompleted,
                    compressor: compressor,
                    timeProvider: time);
            }

            return configuration.CreateLogger();
        }

        /// <summary>
        /// Decompresses a gzip file into a byte array.
        /// </summary>
        /// <param name="path">Path of the <c>.gz</c> file.</param>
        /// <returns>The decompressed bytes.</returns>
        private static byte[] Inflate(string path)
        {
            using var input = File.OpenRead(path);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        /// <summary>
        /// Compresses the UTF-8 bytes of <paramref name="text"/> with the fastest gzip level.
        /// </summary>
        /// <param name="text">Text written into the gzip payload.</param>
        /// <returns>The gzip file bytes.</returns>
        private static byte[] Gzip(string text)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(Encoding.UTF8.GetBytes(text));
            }

            return output.ToArray();
        }

        /// <summary>
        /// Clock whose local time is set by the test and reported as UTC.
        /// </summary>
        private sealed class ManualTime : TimeProvider
        {
            /// <summary>
            /// Sets <see cref="Local"/> to <paramref name="local"/>.
            /// </summary>
            /// <param name="local">Initial local time. The sink reads it as UTC.</param>
            public ManualTime(DateTime local) => Local = local;

            /// <summary>Current local time returned by <see cref="GetUtcNow"/> with a zero offset.</summary>
            /// <value>The clock's current local time. <see cref="GetUtcNow"/> returns it with offset zero.</value>
            public DateTime Local { get; private set; }

            /// <summary>
            /// Adds <paramref name="days"/> to <see cref="Local"/>.
            /// </summary>
            /// <param name="days">Number of days to add. Negative values move backward.</param>
            public void AdvanceDays(int days) => Local = Local.AddDays(days);

            /// <summary>
            /// Returns <see cref="Local"/> as a UTC timestamp with offset zero.
            /// </summary>
            /// <returns>The current test time.</returns>
            public override DateTimeOffset GetUtcNow() => new(Local, TimeSpan.Zero);

            /// <summary>
            /// Returns UTC so the sink's local roll date matches <see cref="Local"/>.
            /// </summary>
            /// <value><see cref="TimeZoneInfo.Utc"/>, so the sink's local roll date matches <see cref="Local"/>.</value>
            public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        }

        /// <summary>
        /// Compressor that refuses every file so the sink must keep the source log.
        /// </summary>
        private sealed class FailingCompressor : ILogFileCompressor
        {
            /// <summary>
            /// Reports that compression did not finish and does not create or delete a file.
            /// </summary>
            /// <param name="sourceLogPath">Completed log path. It is not opened.</param>
            /// <returns>Always <see langword="false"/>.</returns>
            public bool TryCompressAndReplace(string sourceLogPath) => false;
        }

        /// <summary>
        /// Creates a unique temporary directory and deletes it on dispose.
        /// </summary>
        private sealed class TempDir : IDisposable
        {
            /// <summary>
            /// Creates <see cref="Path"/> under the process temp directory.
            /// </summary>
            public TempDir()
            {
                Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "vectornntp-gzip-roll-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            /// <summary>Absolute path of the directory created for one fact.</summary>
            /// <value>Absolute path of the directory created for one fact and deleted by <see cref="Dispose"/>.</value>
            public string Path { get; }

            /// <summary>
            /// Deletes <see cref="Path"/> recursively. <see cref="IOException"/> is ignored.
            /// </summary>
            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException ex)
                {
                    _ = ex; // observed; best-effort temp directory cleanup
                }
            }
        }
    }
}
