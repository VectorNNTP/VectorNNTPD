using System.Diagnostics;
using Xunit.Abstractions;
using VectorNNTPD.StorageService.Storage.ArticleIndex;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    /// <summary>
    /// Measures the article index on the process temp volume.
    /// The numbers are not an NVMe or 10 Gbps result.
    /// </summary>
    public sealed class ArticleIndexBenchTests
    {
        private readonly ITestOutputHelper _output;

        public ArticleIndexBenchTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Temp_volume_reports_insert_update_read_startup_and_shutdown()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            var drive = DriveInfo.GetDrives().FirstOrDefault(candidate =>
                root.StartsWith(candidate.Name, StringComparison.OrdinalIgnoreCase));
            Write(
                "environment os={0} processors={1} path={2} driveType={3} format={4}",
                Environment.OSVersion,
                Environment.ProcessorCount,
                root,
                drive?.DriveType.ToString() ?? "unknown",
                drive?.DriveFormat ?? "unknown");
            Write("This volume was not identified as a dedicated NVMe device.");
            var flushMilliseconds = 20;
            var configuredFlush = Environment.GetEnvironmentVariable("VECTORNNTP_ARTICLE_INDEX_BENCH_FLUSH_MS");
            if (int.TryParse(configuredFlush, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedFlush) && parsedFlush > 0)
            {
                flushMilliseconds = parsedFlush;
            }

            Write("flushIntervalMs={0}", flushMilliseconds);

            try
            {
                var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool, new ArticleIndexSettings
                {
                    BatchSize = 512,
                    FlushIntervalMilliseconds = flushMilliseconds,
                    QueueCapacity = 8192,
                    SummaryIntervalMilliseconds = 60_000,
                });
                var insertLatency = await MeasureAsync(4096, 64, i => index.InsertStoredAsync(ArticleIndexTestSupport.Id(i), spool, 1_000_000 + i, CancellationToken.None).AsTask());
                Report("batched-inserts", insertLatency);
                Assert.Equal(4096, index.PresenceCount);
                Assert.Equal(0, index.Counters.Failures);

                var updateLatency = await MeasureAsync(4096, 64, i => index.TouchAsync(ArticleIndexTestSupport.Id(i), spool, 2_000_000 + i, CancellationToken.None).AsTask());
                Report("batched-updates", updateLatency);

                var mixed = Stopwatch.StartNew();
                var writers = Task.Run(async () =>
                {
                    await Parallel.ForEachAsync(
                        Enumerable.Range(4096, 1024),
                        new ParallelOptions { MaxDegreeOfParallelism = 32 },
                        async (i, token) => await index.InsertStoredAsync(ArticleIndexTestSupport.Id(i), spool, 3_000_000 + i, token));
                });
                var readers = Task.Run(() =>
                {
                    for (var i = 0; i < 4096; i++)
                    {
                        Assert.True(index.Presence.Contains(ArticleIndexTestSupport.Id(i)));
                        Assert.True(index.TryGetIndexedRecord(ArticleIndexTestSupport.Id(i), out _));
                    }
                });
                await Task.WhenAll(writers, readers);
                mixed.Stop();
                Write(
                    "mixed inserts={0} reads={1} elapsedMs={2:0.0} membership={3}",
                    1024,
                    4096,
                    mixed.Elapsed.TotalMilliseconds,
                    index.PresenceCount);

                var databaseBytes = new FileInfo(index.DatabasePath).Length;
                var wal = index.DatabasePath + "-wal";
                var walBytes = File.Exists(wal) ? new FileInfo(wal).Length : 0;
                Write("databaseBytes={0} walBytes={1} batches={2} queueCapacity={3}", databaseBytes, walBytes, index.Counters.Batches, index.Counters.QueueCapacity);

                await ArticleIndexTestSupport.StopAsync(index);
                var reloadStarted = Stopwatch.GetTimestamp();
                var reloaded = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
                var reloadMs = Stopwatch.GetElapsedTime(reloadStarted).TotalMilliseconds;
                Write(
                    "startup rows={0} reportedMs={1} wallMs={2:0.0} estimatedBytes={3}",
                    reloaded.Counters.StartupRows,
                    reloaded.Counters.StartupMilliseconds,
                    reloadMs,
                    reloaded.PresenceEstimatedBytes);
                Assert.Equal(5120, reloaded.Counters.StartupRows);

                var membership = Stopwatch.StartNew();
                for (var i = 0; i < 5120; i++)
                {
                    Assert.True(reloaded.Contains(ArticleIndexTestSupport.Id(i)));
                }

                membership.Stop();
                Assert.False(reloaded.TryGetRetrievableArticle(ArticleIndexTestSupport.Id(0), out _));
                Write("membership-hits count={0} elapsedMs={1:0.0}", 5120, membership.Elapsed.TotalMilliseconds);

                var unbatchedRoot = root + "-unbatched";
                var unbatched = await ArticleIndexTestSupport.StartSpoolAsync(unbatchedRoot, spool, new ArticleIndexSettings
                {
                    BatchSize = 1,
                    FlushIntervalMilliseconds = 1,
                    QueueCapacity = 256,
                    SummaryIntervalMilliseconds = 60_000,
                });
                var single = await MeasureAsync(256, 8, i => unbatched.InsertStoredAsync(ArticleIndexTestSupport.Id(i), spool, 4_000_000 + i, CancellationToken.None).AsTask());
                Report("unbatched-inserts", single);
                await ArticleIndexTestSupport.StopAsync(unbatched);
                await ArticleIndexTestSupport.StopAsync(reloaded);
                ArticleIndexTestSupport.Delete(unbatchedRoot);
            }
            finally
            {
                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        private void Write(string format, params object?[] args)
        {
            var line = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args);
            Console.WriteLine(line);
            _output.WriteLine(line);
        }

        [Fact]
        public async Task Configured_row_count_measures_inserts_updates_eviction_checkpoint_and_startup()
        {
            var rows = 10_000;
            var configured = Environment.GetEnvironmentVariable("VECTORNNTP_ARTICLE_INDEX_BENCH_ROWS");
            if (int.TryParse(configured, out var parsed) && parsed > 0)
            {
                rows = parsed;
            }

            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            var drive = DriveInfo.GetDrives().FirstOrDefault(candidate =>
                root.StartsWith(candidate.Name, StringComparison.OrdinalIgnoreCase));
            Write(
                "scale-environment rows={0} flushIntervalMs={1} os={2} processors={3} driveType={4} format={5} path={6}",
                rows,
                ArticleIndexSettings.DefaultFlushIntervalMilliseconds,
                Environment.OSVersion,
                Environment.ProcessorCount,
                drive?.DriveType.ToString() ?? "unknown",
                drive?.DriveFormat ?? "unknown",
                root);
            Write("This volume was not identified as a dedicated NVMe device. Set VECTORNNTP_ARTICLE_INDEX_BENCH_ROWS to repeat at another size.");
            try
            {
                var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool, new ArticleIndexSettings
                {
                    BatchSize = 512,
                    FlushIntervalMilliseconds = ArticleIndexSettings.DefaultFlushIntervalMilliseconds,
                    QueueCapacity = 8192,
                    SummaryIntervalMilliseconds = 60_000,
                });
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var gen0 = GC.CollectionCount(0);
                var inserts = await MeasureAsync(rows, 64, i => index.InsertStoredAsync(ArticleIndexTestSupport.Id(i), spool, 1_000_000 + i, CancellationToken.None).AsTask());
                Report("scale-inserts", inserts);
                var updates = await MeasureAsync(rows, 64, i => index.TouchAsync(ArticleIndexTestSupport.Id(i), spool, 2_000_000 + i, CancellationToken.None).AsTask());
                Report("scale-updates", updates);
                var oldest = new ArticleIndexRecord[64];
                var evictionStarted = Stopwatch.GetTimestamp();
                var copied = index.CopyOldest(spool, oldest);
                var evictionMs = Stopwatch.GetElapsedTime(evictionStarted).TotalMilliseconds;
                Write("scale-eviction copied={0} elapsedMs={1:0.000} firstAccessed={2}", copied, evictionMs, oldest[0].LastAccessedAtUnixMilliseconds);
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                var wal = index.DatabasePath + "-wal";
                Write(
                    "scale-resources databaseBytes={0} walBytes={1} batches={2} checkpoints={3} checkpointBusy={4} checkpointMs={5} maxCheckpointMs={6} lastLogFrames={7} lastCheckpointedFrames={8} failures={9} retries={10} allocatedBytes={11} gen0={12} queueDepth={13}",
                    new FileInfo(index.DatabasePath).Length,
                    File.Exists(wal) ? new FileInfo(wal).Length : 0,
                    index.Counters.Batches,
                    index.Counters.Checkpoints,
                    index.Counters.CheckpointBusy,
                    index.Counters.CheckpointMilliseconds,
                    index.Counters.MaxCheckpointMilliseconds,
                    index.Counters.LastCheckpointLogFrames,
                    index.Counters.LastCheckpointedFrames,
                    index.Counters.Failures,
                    index.Counters.Retries,
                    allocated,
                    GC.CollectionCount(0) - gen0,
                    index.Counters.QueueDepth);
                Assert.Equal(0, index.Counters.Failures);
                Assert.Equal(rows, index.PresenceCount);
                Assert.InRange(index.Counters.QueueDepth, 0, index.Counters.QueueCapacity);
                await ArticleIndexTestSupport.StopAsync(index);
                Write(
                    "scale-shutdown checkpoints={0} checkpointBusy={1} checkpointMs={2} maxCheckpointMs={3} lastLogFrames={4} lastCheckpointedFrames={5}",
                    index.Counters.Checkpoints,
                    index.Counters.CheckpointBusy,
                    index.Counters.CheckpointMilliseconds,
                    index.Counters.MaxCheckpointMilliseconds,
                    index.Counters.LastCheckpointLogFrames,
                    index.Counters.LastCheckpointedFrames);

                var reloadStarted = Stopwatch.GetTimestamp();
                var reloaded = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
                var wallMs = Stopwatch.GetElapsedTime(reloadStarted).TotalMilliseconds;
                var scanMs = Math.Max(1, reloaded.Counters.StartupMilliseconds);
                Write(
                    "scale-startup rows={0} scanMs={1} wallMs={2:0.0} rowsPerSecond={3:0.0} estimatedBytes={4}",
                    reloaded.Counters.StartupRows,
                    reloaded.Counters.StartupMilliseconds,
                    wallMs,
                    reloaded.Counters.StartupRows * 1000d / scanMs,
                    reloaded.PresenceEstimatedBytes);
                var projectedSeconds = 521_000_000d / (reloaded.Counters.StartupRows * 1000d / scanMs);
                Write(
                    "scale-projection 521000000 rows would take about {0:0.0} seconds if the scan stayed at this rate. This is a linear projection from {1} rows on this volume, not a measurement.",
                    projectedSeconds,
                    reloaded.Counters.StartupRows);
                Assert.Equal(rows, reloaded.Counters.StartupRows);
                await ArticleIndexTestSupport.StopAsync(reloaded);
            }
            finally
            {
                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        [Fact]
        public async Task Several_spools_on_the_temp_volume_report_aggregate_rates()
        {
            var rowsPerSpool = int.TryParse(Environment.GetEnvironmentVariable("VECTORNNTP_ARTICLE_INDEX_SPOOL_ROWS"), out var configured) && configured > 0
                ? configured
                : 512;
            var rootForDrive = ArticleIndexTestSupport.NewDirectory();
            var drive = DriveInfo.GetDrives().FirstOrDefault(candidate =>
                rootForDrive.StartsWith(candidate.Name, StringComparison.OrdinalIgnoreCase));
            Write(
                "multi-spool-environment rowsPerSpool={0} flushIntervalMs={1} os={2} processors={3} driveType={4} format={5}",
                rowsPerSpool,
                ArticleIndexSettings.DefaultFlushIntervalMilliseconds,
                Environment.OSVersion,
                Environment.ProcessorCount,
                drive?.DriveType.ToString() ?? "unknown",
                drive?.DriveFormat ?? "unknown");
            Write("This volume was not identified as a dedicated NVMe or SATA spool. Payload files were not written.");
            ArticleIndexTestSupport.Delete(rootForDrive);

            foreach (var spoolCount in new[] { 1, 4, 8, 16, 22 })
            {
                await MeasureSpoolCountAsync(spoolCount, rowsPerSpool);
            }

            await MeasureOneBlockedSpoolAsync();
        }

        private async Task MeasureSpoolCountAsync(int spoolCount, int rowsPerSpool)
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spools = new string[spoolCount];
            ArticleIndexService? index = null;
            try
            {
                for (var i = 0; i < spoolCount; i++)
                {
                    spools[i] = ArticleIndexTestSupport.NewDirectory();
                }

                var budget = ArticleIndexCacheBudget.ForSpoolCount(spoolCount);
                index = await ArticleIndexTestSupport.StartAsync(root, ArticleIndexTestSupport.Catalog(spools), new ArticleIndexSettings
                {
                    BatchSize = 512,
                    FlushIntervalMilliseconds = ArticleIndexSettings.DefaultFlushIntervalMilliseconds,
                    QueueCapacity = 8192,
                    SummaryIntervalMilliseconds = 60_000,
                });
                var total = spoolCount * rowsPerSpool;
                var degree = Math.Min(256, Math.Max(64, spoolCount * 8));
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;
                var inserts = await MeasureAsync(total, degree, item =>
                {
                    var spool = item % spoolCount;
                    return index.InsertStoredAsync(ArticleIndexTestSupport.Id(item), spools[spool], 1_000_000 + item, CancellationToken.None).AsTask();
                });
                Report($"spools={spoolCount} inserts", inserts);
                var updates = await MeasureAsync(total, degree, item =>
                {
                    var spool = item % spoolCount;
                    return index.TouchAsync(ArticleIndexTestSupport.Id(item), spools[spool], 2_000_000 + item, CancellationToken.None).AsTask();
                });
                Report($"spools={spoolCount} updates", updates);
                var sample = Math.Min(total, 4096);
                var lookup = Stopwatch.StartNew();
                for (var i = 0; i < sample; i++)
                {
                    Assert.True(index.Contains(ArticleIndexTestSupport.Id(i)));
                }

                lookup.Stop();
                long failures = 0;
                long retries = 0;
                long batches = 0;
                long checkpoints = 0;
                long walBytes = 0;
                long maxQueue = 0;
                foreach (var spool in spools)
                {
                    var counters = index.CountersFor(spool);
                    failures += counters.Failures;
                    retries += counters.Retries;
                    batches += counters.Batches;
                    checkpoints += counters.Checkpoints;
                    maxQueue = Math.Max(maxQueue, counters.QueueDepth);
                    var wal = index.DatabasePathFor(spool) + "-wal";
                    if (File.Exists(wal))
                    {
                        walBytes += new FileInfo(wal).Length;
                    }
                }

                var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                Write(
                    "spools={0} rowsPerSpool={1} insertPerSec={2:0.0} perSpoolInsertPerSec={3:0.0} updatePerSec={4:0.0} lookupCount={5} lookupMs={6:0.0} writerKiB={7} readerKiB={8} readers={9} batches={10} checkpoints={11} walBytes={12} failures={13} retries={14} maxQueueDepth={15} allocatedBytes={16} workingSetBefore={17} workingSetAfter={18}",
                    spoolCount,
                    rowsPerSpool,
                    total / inserts.Elapsed.TotalSeconds,
                    total / inserts.Elapsed.TotalSeconds / spoolCount,
                    total / updates.Elapsed.TotalSeconds,
                    sample,
                    lookup.Elapsed.TotalMilliseconds,
                    budget.WriterCacheKibibytes,
                    budget.ReaderCacheKibibytes,
                    budget.ReaderCount,
                    batches,
                    checkpoints,
                    walBytes,
                    failures,
                    retries,
                    maxQueue,
                    allocated,
                    workingSetBefore,
                    Process.GetCurrentProcess().WorkingSet64);
                Assert.Equal(0, failures);
                Assert.Equal(total, index.PresenceCount);
                await ArticleIndexTestSupport.StopAsync(index);
                var reloadStarted = Stopwatch.GetTimestamp();
                var reloaded = await ArticleIndexTestSupport.StartAsync(root, ArticleIndexTestSupport.Catalog(spools));
                var wallMs = Stopwatch.GetElapsedTime(reloadStarted).TotalMilliseconds;
                Write(
                    "spools={0} startupRows={1} scanMs={2} wallMs={3:0.0}",
                    spoolCount,
                    reloaded.Counters.StartupRows,
                    reloaded.Counters.StartupMilliseconds,
                    wallMs);
                Assert.Equal(total, reloaded.Counters.StartupRows);
                await ArticleIndexTestSupport.StopAsync(reloaded);
                index = null;
            }
            finally
            {
                if (index is not null)
                {
                    await ArticleIndexTestSupport.StopAsync(index);
                }

                ArticleIndexTestSupport.Delete(root);
                foreach (var spool in spools)
                {
                    if (spool is not null)
                    {
                        ArticleIndexTestSupport.Delete(spool);
                    }
                }
            }
        }

        private async Task MeasureOneBlockedSpoolAsync()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spools = new[]
            {
                ArticleIndexTestSupport.NewDirectory(),
                ArticleIndexTestSupport.NewDirectory(),
                ArticleIndexTestSupport.NewDirectory(),
                ArticleIndexTestSupport.NewDirectory(),
            };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ArticleIndexService? index = null;
            try
            {
                index = await ArticleIndexTestSupport.StartAsync(root, ArticleIndexTestSupport.Catalog(spools), new ArticleIndexSettings
                {
                    BatchSize = 1,
                    FlushIntervalMilliseconds = 60_000,
                    QueueCapacity = 8,
                    SummaryIntervalMilliseconds = 60_000,
                    BeforeCommitSpool = spools[0],
                    BeforeCommit = () =>
                    {
                        entered.TrySetResult();
                        release.Task.GetAwaiter().GetResult();
                    },
                });
                var blocked = index.InsertStoredAsync(ArticleIndexTestSupport.Id(9_000_000), spools[0], 1, CancellationToken.None).AsTask();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var others = await MeasureAsync(768, 24, item =>
                {
                    var spool = 1 + (item % 3);
                    return index.InsertStoredAsync(ArticleIndexTestSupport.Id(9_100_000 + item), spools[spool], 2_000 + item, CancellationToken.None).AsTask();
                });
                Report("blocked-spool others", others);
                Assert.False(blocked.IsCompleted);
                Assert.Equal(0, index.CountersFor(spools[1]).Failures);
                release.TrySetResult();
                Assert.True((await blocked.WaitAsync(TimeSpan.FromSeconds(5))).Inserted);
                await ArticleIndexTestSupport.StopAsync(index);
                index = null;
            }
            finally
            {
                release.TrySetResult();
                if (index is not null)
                {
                    await ArticleIndexTestSupport.StopAsync(index);
                }

                ArticleIndexTestSupport.Delete(root);
                foreach (var spool in spools)
                {
                    ArticleIndexTestSupport.Delete(spool);
                }
            }
        }

        private void Report(string name, Measurement measurement)
        {
            var line = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} count={1} elapsedMs={2:0.0} perSecond={3:0.0} p50Ms={4:0.000} p95Ms={5:0.000} p99Ms={6:0.000}",
                name,
                measurement.Count,
                measurement.Elapsed.TotalMilliseconds,
                measurement.Count / measurement.Elapsed.TotalSeconds,
                measurement.P50,
                measurement.P95,
                measurement.P99);
            Write(line);
        }

        private static async Task<Measurement> MeasureAsync(int count, int degree, Func<int, Task> operation)
        {
            var samples = new double[count];
            var elapsed = Stopwatch.StartNew();
            await Parallel.ForEachAsync(
                Enumerable.Range(0, count),
                new ParallelOptions { MaxDegreeOfParallelism = degree },
                async (i, _) =>
                {
                    var started = Stopwatch.GetTimestamp();
                    await operation(i);
                    samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                });
            elapsed.Stop();
            Array.Sort(samples);
            return new Measurement(count, elapsed.Elapsed, Percentile(samples, 0.50), Percentile(samples, 0.95), Percentile(samples, 0.99));
        }

        private static double Percentile(double[] sorted, double quantile)
        {
            var index = (int)Math.Ceiling(quantile * sorted.Length) - 1;
            if (index < 0)
            {
                index = 0;
            }

            return sorted[index];
        }

        private readonly record struct Measurement(int Count, TimeSpan Elapsed, double P50, double P95, double P99);
    }
}
