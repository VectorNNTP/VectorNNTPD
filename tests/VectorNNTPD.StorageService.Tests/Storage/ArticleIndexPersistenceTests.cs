using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Storage.ArticleIndex;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;
using VectorNNTPD.StorageService.Storage.SpoolShards;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    public sealed class ArticleIndexPersistenceTests
    {
        [Fact]
        public async Task A_stored_article_is_indexed_after_the_file_exists_with_one_timestamp_and_the_spool_root()
        {
            var spool = ArticleIndexTestSupport.NewDirectory();
            var database = ArticleIndexTestSupport.NewDirectory();
            var catalog = Catalog(spool);
            var clock = new FakeTimeProvider();
            clock.SetUtcNow(new DateTimeOffset(2026, 10, 13, 7, 0, 0, TimeSpan.Zero));
            var expected = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var monitor = Monitor(catalog, spool, clock);
            var index = await ArticleIndexTestSupport.StartAsync(database, catalog);
            try
            {
                await WaitForSpaceAsync(monitor, spool);
                var processor = Processor(catalog, monitor, index, clock);
                var article = Build("<indexed@example.test>");
                Assert.False(index.Contains(article.ArtId));
                Assert.Equal(AcceptedArticleProcessResult.Completed, await processor.ProcessAsync(article, CancellationToken.None));
                var path = SpoolArticlePath.CombineArticle(spool, article.ArtId);
                Assert.True(File.Exists(path));
                Assert.True(index.Contains(article.ArtId));
                Assert.True(index.TryGetIndexedRecord(article.ArtId, out var record));
                Assert.Equal(spool, record.SpoolDirectory);
                Assert.Equal(expected, record.ReceivedAtUnixMilliseconds);
                Assert.Equal(expected, record.LastAccessedAtUnixMilliseconds);
                Assert.True(index.TryGetRetrievableArticle(article.ArtId, out var retrieved));
                Assert.Equal(path, retrieved);

                Assert.Equal(AcceptedArticleProcessResult.Completed, await processor.ProcessAsync(article, CancellationToken.None));
                Assert.True(index.TryGetIndexedRecord(article.ArtId, out var duplicate));
                Assert.Equal(expected, duplicate.ReceivedAtUnixMilliseconds);
                Assert.Equal(expected, duplicate.LastAccessedAtUnixMilliseconds);
                Assert.Equal(spool, duplicate.SpoolDirectory);
                Assert.Equal(1, index.CopyOldest(spool, new ArticleIndexRecord[2]));

                var rewritten = Build("<indexed@example.test>", "replaced-body");
                Assert.Equal(article.ArtId, rewritten.ArtId);
                Assert.True(index.Contains(rewritten.ArtId));
                Assert.Equal(AcceptedArticleProcessResult.Completed, await processor.ProcessAsync(rewritten, CancellationToken.None));
                var replaced = await File.ReadAllTextAsync(path);
                Assert.Contains("replaced-body", replaced, StringComparison.Ordinal);
                Assert.DoesNotContain("body-line", replaced, StringComparison.Ordinal);
                Assert.True(index.TryGetIndexedRecord(article.ArtId, out var after));
                Assert.Equal(expected, after.ReceivedAtUnixMilliseconds);
                Assert.Equal(expected, after.LastAccessedAtUnixMilliseconds);
                Assert.Equal(1, index.CopyOldest(spool, new ArticleIndexRecord[2]));
                await ArticleIndexTestSupport.StopAsync(index);
                index = await ArticleIndexTestSupport.StartAsync(database, catalog);
                Assert.True(index.Contains(article.ArtId));
                Assert.True(index.TryGetRetrievableArticle(article.ArtId, out var afterRestart));
                Assert.Equal(path, afterRestart);
                Assert.Contains("replaced-body", await File.ReadAllTextAsync(afterRestart), StringComparison.Ordinal);
                Assert.True(index.TryGetIndexedRecord(article.ArtId, out var reloaded));
                Assert.Equal(expected, reloaded.ReceivedAtUnixMilliseconds);
                Assert.Equal(1, index.CopyOldest(spool, new ArticleIndexRecord[2]));
            }
            finally
            {
                await ArticleIndexTestSupport.StopAsync(index);
                ArticleIndexTestSupport.Delete(spool);
                ArticleIndexTestSupport.Delete(database);
            }
        }

        [Fact]
        public async Task A_failed_commit_leaves_the_file_unindexed_until_a_later_delivery()
        {
            var spool = ArticleIndexTestSupport.NewDirectory();
            var database = ArticleIndexTestSupport.NewDirectory();
            var catalog = Catalog(spool);
            var probe = new StaticFreeSpaceProbe();
            var monitor = Monitor(catalog, probe);
            await monitor.StartAsync(CancellationToken.None);
            var failing = await ArticleIndexTestSupport.StartAsync(database, catalog, new ArticleIndexSettings
            {
                BatchSize = 1,
                FlushIntervalMilliseconds = 10,
                QueueCapacity = 8,
                SummaryIntervalMilliseconds = 60_000,
                BeforeCommit = () => throw new InvalidOperationException("index unavailable"),
            });
            ArticleIndexService? repaired = null;
            try
            {
                probe.Set(spool, total: 100_000_000_000, available: 80_000_000_000);
                await WaitForSpaceAsync(monitor, spool);
                var processor = Processor(catalog, monitor, failing, TimeProvider.System);
                var article = Build("<repair@example.test>");
                Assert.False(failing.Contains(article.ArtId));
                Assert.Equal(AcceptedArticleProcessResult.Failed, await processor.ProcessAsync(article, CancellationToken.None));
                Assert.True(File.Exists(SpoolArticlePath.CombineArticle(spool, article.ArtId)));
                Assert.False(failing.Contains(article.ArtId));
                await ArticleIndexTestSupport.StopAsync(failing);

                repaired = await ArticleIndexTestSupport.StartAsync(database, catalog);
                var report = await repaired.Reconciliation.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, report.FilesSeen);
                Assert.Equal(0, report.MissingQueued);
                Assert.Equal(0, report.RowsProbed);
                Assert.False(repaired.Contains(article.ArtId));
                Assert.True(File.Exists(SpoolArticlePath.CombineArticle(spool, article.ArtId)));
                var again = Processor(catalog, monitor, repaired, TimeProvider.System);
                Assert.Equal(AcceptedArticleProcessResult.Completed, await again.ProcessAsync(article, CancellationToken.None));
                Assert.True(repaired.Contains(article.ArtId));
                Assert.True(repaired.TryGetRetrievableArticle(article.ArtId, out _));
                Assert.True(repaired.TryGetIndexedRecord(article.ArtId, out var record));
                Assert.Equal(spool, record.SpoolDirectory);
                Assert.Equal(1, repaired.CopyOldest(spool, new ArticleIndexRecord[2]));
                await ArticleIndexTestSupport.StopAsync(repaired);
                repaired = null;
            }
            finally
            {
                await monitor.StopAsync(CancellationToken.None);
                if (failing.Execution is not null)
                {
                    await failing.StopAsync(CancellationToken.None);
                }

                if (repaired is not null)
                {
                    await repaired.StopAsync(CancellationToken.None);
                }

                ArticleIndexTestSupport.Delete(spool);
                ArticleIndexTestSupport.Delete(database);
            }
        }

        [Fact]
        public async Task Concurrent_deliveries_overwrite_in_order_and_leave_one_row()
        {
            var spool = ArticleIndexTestSupport.NewDirectory();
            var database = ArticleIndexTestSupport.NewDirectory();
            var catalog = Catalog(spool);
            var clock = new FakeTimeProvider();
            clock.SetUtcNow(new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero));
            var expected = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var monitor = Monitor(catalog, spool, clock);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var indexWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var index = await ArticleIndexTestSupport.StartAsync(database, catalog, new ArticleIndexSettings
            {
                BatchSize = 1,
                FlushIntervalMilliseconds = 60_000,
                QueueCapacity = 8,
                SummaryIntervalMilliseconds = 60_000,
                BeforeCommit = () =>
                {
                    indexWaiting.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                },
            });
            try
            {
                await WaitForSpaceAsync(monitor, spool);
                var processor = Processor(catalog, monitor, index, clock);
                var first = Build("<concurrent@example.test>", "alpha-body");
                var second = Build("<concurrent@example.test>", "beta-body");
                Assert.Equal(first.ArtId, second.ArtId);
                var firstTask = processor.ProcessAsync(first, CancellationToken.None).AsTask();
                await indexWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                index.OnReservationWaiting = () => secondEntered.TrySetResult();
                var secondTask = processor.ProcessAsync(second, CancellationToken.None).AsTask();
                await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(secondTask.IsCompleted);
                clock.Advance(TimeSpan.FromSeconds(2));
                InvokeSample(monitor);
                release.TrySetResult();
                Assert.Equal(AcceptedArticleProcessResult.Completed, await firstTask.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(AcceptedArticleProcessResult.Completed, await secondTask.WaitAsync(TimeSpan.FromSeconds(5)));
                var path = SpoolArticlePath.CombineArticle(spool, first.ArtId);
                var text = await File.ReadAllTextAsync(path);
                Assert.Contains("beta-body", text, StringComparison.Ordinal);
                Assert.DoesNotContain("alpha-body", text, StringComparison.Ordinal);
                Assert.True(index.TryGetIndexedRecord(first.ArtId, out var record));
                Assert.Equal(expected, record.ReceivedAtUnixMilliseconds);
                Assert.Equal(expected, record.LastAccessedAtUnixMilliseconds);
                Assert.Equal(spool, record.SpoolDirectory);
                Assert.Equal(1, index.CopyOldest(spool, new ArticleIndexRecord[2]));
                await ArticleIndexTestSupport.StopAsync(index);
                index = await ArticleIndexTestSupport.StartAsync(database, catalog);
                Assert.True(index.Contains(first.ArtId));
                Assert.Equal(1, index.PresenceCount);
                Assert.Contains("beta-body", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
                Assert.True(index.TryGetIndexedRecord(first.ArtId, out var reloaded));
                Assert.Equal(expected, reloaded.ReceivedAtUnixMilliseconds);
            }
            finally
            {
                release.TrySetResult();
                await ArticleIndexTestSupport.StopAsync(index);
                ArticleIndexTestSupport.Delete(spool);
                ArticleIndexTestSupport.Delete(database);
            }
        }

        [Fact]
        public async Task A_repeat_delivery_overwrites_the_assigned_spool()
        {
            var left = ArticleIndexTestSupport.NewDirectory();
            var right = ArticleIndexTestSupport.NewDirectory();
            var database = ArticleIndexTestSupport.NewDirectory();
            var clock = new FakeTimeProvider();
            clock.SetUtcNow(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));
            var expected = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var leftCatalog = Catalog(left);
            var monitor = Monitor(leftCatalog, left, clock);
            var index = await ArticleIndexTestSupport.StartAsync(database, leftCatalog);
            try
            {
                await WaitForSpaceAsync(monitor, left);
                var processor = Processor(leftCatalog, monitor, index, clock);
                var article = Build("<pinned@example.test>", "left-body");
                Assert.Equal(AcceptedArticleProcessResult.Completed, await processor.ProcessAsync(article, CancellationToken.None));
                await ArticleIndexTestSupport.StopAsync(index);

                var both = new SegmentPoolCatalog();
                both.Publish([new SegmentPoolDevice("block:test", [right, left])]);
                var probe = new StaticFreeSpaceProbe();
                probe.Set(left, total: 100_000_000_000, available: 80_000_000_000);
                probe.Set(right, total: 100_000_000_000, available: 90_000_000_000);
                monitor = Monitor(both, probe, clock);
                await WaitForSpaceAsync(monitor, left);
                await WaitForSpaceAsync(monitor, right);
                clock.Advance(TimeSpan.FromHours(1));
                InvokeSample(monitor);
                index = await ArticleIndexTestSupport.StartAsync(database, both);
                var again = Processor(both, monitor, index, clock);
                var repeat = Build("<pinned@example.test>", "still-on-left");
                Assert.Equal(AcceptedArticleProcessResult.Completed, await again.ProcessAsync(repeat, CancellationToken.None));
                var leftPath = SpoolArticlePath.CombineArticle(left, article.ArtId);
                var rightPath = SpoolArticlePath.CombineArticle(right, article.ArtId);
                Assert.Contains("still-on-left", await File.ReadAllTextAsync(leftPath), StringComparison.Ordinal);
                Assert.False(File.Exists(rightPath));
                Assert.True(index.TryGetIndexedRecord(article.ArtId, out var record));
                Assert.Equal(left, record.SpoolDirectory);
                Assert.Equal(expected, record.ReceivedAtUnixMilliseconds);
                Assert.Equal(1, index.CopyOldest(left, new ArticleIndexRecord[2]));
                Assert.Equal(0, index.CopyOldest(right, new ArticleIndexRecord[2]));
                Assert.Equal(1, index.PresenceCount);
            }
            finally
            {
                await ArticleIndexTestSupport.StopAsync(index);
                ArticleIndexTestSupport.Delete(left);
                ArticleIndexTestSupport.Delete(right);
                ArticleIndexTestSupport.Delete(database);
            }
        }

        private static SegmentPoolCatalog Catalog(string spool)
        {
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:test", [spool])]);
            return catalog;
        }

        private static BlockDeviceMonitorService Monitor(
            SegmentPoolCatalog catalog,
            StaticFreeSpaceProbe probe,
            TimeProvider? time = null) =>
            new(
                catalog,
                new RisingCounters(),
                NullLogger<BlockDeviceMonitorService>.Instance,
                TimeSpan.FromHours(1),
                time ?? TimeProvider.System,
                probe);

        private static BlockDeviceMonitorService Monitor(
            SegmentPoolCatalog catalog,
            string spool,
            TimeProvider? time = null)
        {
            var probe = new StaticFreeSpaceProbe();
            probe.Set(spool, total: 100_000_000_000, available: 80_000_000_000);
            return Monitor(catalog, probe, time);
        }

        private static SpoolArticlePersistenceProcessor Processor(
            SegmentPoolCatalog catalog,
            BlockDeviceMonitorService monitor,
            IArticleIndex index,
            TimeProvider time)
        {
            return new SpoolArticlePersistenceProcessor(
                "cache01.usenet.ninja",
                catalog,
                monitor,
                new SpoolWriteGate(),
                new SpoolArticleIdentityGate(),
                new SpoolHealth(time),
                new SpoolPressure(),
                new SpoolArticlePublisher(new FileSpoolArticleFiles()),
                new SpoolPersistenceCounters(),
                new StorageServerOptions
                {
                    SegmentPools = new SegmentPoolsOptions { CriticalPercent = 85, HighPercent = 79, LowPercent = 75 },
                    SpoolPersistence = new SpoolPersistenceOptions(),
                },
                NullLogger.Instance,
                time,
                index);
        }

        private static AcceptedArticle Build(string messageId) => Build(messageId, "body-line");

        private static AcceptedArticle Build(string messageId, string body)
        {
            var parser = new NntpArticleParser("nntpd01.usenet.ninja");
            var text = "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: "
                + messageId
                + "\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: vatp\r\n\r\n"
                + body
                + "\r\n";
            var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(text));
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            return new AcceptedArticle(created.Record.ArtId, created.Record.ArtHash, created.Record.ArtSize, created.Record.ArtData);
        }

        private static async Task WaitForSpaceAsync(BlockDeviceMonitorService monitor, string spool)
        {
            InvokeSample(monitor);
            var deadline = TimeProvider.System.GetTimestamp();
            while (!monitor.Observations.Space.ContainsKey(spool))
            {
                if (TimeProvider.System.GetElapsedTime(deadline) > TimeSpan.FromSeconds(2))
                {
                    throw new TimeoutException("spool space was not sampled");
                }

                await Task.Delay(10);
            }
        }

        private static void InvokeSample(BlockDeviceMonitorService monitor) =>
            monitor.GetType().GetMethod("Sample", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(monitor, null);

        private sealed class StaticFreeSpaceProbe : ISpoolFreeSpaceProbe
        {
            private readonly Dictionary<string, (long Total, long Available)> _space = new(StringComparer.Ordinal);

            public void Set(string path, long total, long available) => _space[path] = (total, available);

            public bool TryRead(string spoolRoot, out long totalBytes, out long availableBytes)
            {
                if (!_space.TryGetValue(spoolRoot, out var sample))
                {
                    totalBytes = 0;
                    availableBytes = 0;
                    return false;
                }

                totalBytes = sample.Total;
                availableBytes = sample.Available;
                return true;
            }
        }

        private sealed class RisingCounters : IBlockDeviceCounterSource
        {
            public bool TryRead(string deviceId, out BlockDeviceCounters counters, out string? error)
            {
                counters = new BlockDeviceCounters(1000, 1000, 1000, 1000, 1000, 1000, LatencyAvailable: true);
                error = null;
                return true;
            }
        }
    }
}
