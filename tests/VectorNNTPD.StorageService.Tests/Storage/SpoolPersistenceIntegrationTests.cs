using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;
using VectorNNTPD.StorageService.Storage.SpoolShards;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    public sealed class SpoolPersistenceIntegrationTests
    {
        [Fact]
        public async Task Processor_preserves_canonical_dot_stuffed_body_through_path_traverse_and_spool_write()
        {
            var root = NewRoot();
            try
            {
                var probe = new StaticFreeSpaceProbe();
                probe.Set(root, total: 100_000_000_000, available: 80_000_000_000);
                var catalog = new SegmentPoolCatalog();
                catalog.Publish([new SegmentPoolDevice("block:a", [root])]);
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new RisingCounters(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromMilliseconds(20),
                    TimeProvider.System,
                    probe);
                await monitor.StartAsync(CancellationToken.None);
                try
                {
                    await WaitAsync(() => monitor.Observations.Space.ContainsKey(root));
                    var processor = new SpoolArticlePersistenceProcessor(
                        "cache01.usenet.ninja",
                        catalog,
                        monitor,
                        new SpoolWriteGate(),
                        new SpoolArticleIdentityGate(),
                        new SpoolHealth(TimeProvider.System),
                        new SpoolPressure(),
                        new SpoolArticlePublisher(new FileSpoolArticleFiles()),
                        new SpoolPersistenceCounters(),
                        new StorageServerOptions { SpoolPersistence = new SpoolPersistenceOptions() },
                        NullLogger.Instance,
                        TimeProvider.System);

                    // Logical destuffed body (after TAKETHIS unstuff). Normalize → CanonicalV1 stuffed ArtData.
                    var destuffed =
                        "Path: peer.example\r\n" +
                        "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                        "Message-ID: <persist-dots@example.test>\r\n" +
                        "Newsgroups: alt.test\r\n" +
                        "From: user@example.test\r\n" +
                        "Subject: dots\r\n" +
                        "\r\n" +
                        "Hello\r\n" +
                        ".leading\r\n" +
                        "..leading\r\n" +
                        "...leading\r\n";
                    var created = ArticleRecordFactory.TryCreate(
                        new NntpArticleParser("cache01.usenet.ninja"),
                        Encoding.ASCII.GetBytes(destuffed),
                        ArticlePathMode.Normalize);
                    Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
                    var queuedBody = BodyOf(created.Record.ArtData.Span);
                    Assert.Equal("Hello\r\n..leading\r\n...leading\r\n....leading\r\n", queuedBody);

                    var article = new AcceptedArticle(
                        created.Record.ArtId,
                        created.Record.ArtHash,
                        created.Record.ArtSize,
                        created.Record.ArtData);
                    Assert.Equal(AcceptedArticleProcessResult.Completed, await processor.ProcessAsync(article, CancellationToken.None));

                    var path = SpoolArticlePath.CombineArticle(root, article.ArtId);
                    var stored = Encoding.ASCII.GetString(File.ReadAllBytes(path));
                    Assert.StartsWith("Path: cache01.usenet.ninja!", HeaderLine(stored, "Path"), StringComparison.Ordinal);
                    Assert.Equal(queuedBody, BodyOf(Encoding.ASCII.GetBytes(stored)));
                    Assert.DoesNotContain(".....\r\n", stored, StringComparison.Ordinal);
                }
                finally
                {
                    await monitor.StopAsync(CancellationToken.None);
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task Processor_writes_the_rewritten_article_to_the_roomier_spool()
        {
            var rootA = NewRoot();
            var rootB = NewRoot();
            try
            {
                var probe = new StaticFreeSpaceProbe();
                probe.Set(rootA, total: 100_000_000_000, available: 20_000_000_000);
                probe.Set(rootB, total: 100_000_000_000, available: 80_000_000_000);
                var catalog = new SegmentPoolCatalog();
                catalog.Publish(
                [
                    new SegmentPoolDevice("block:a", [rootA]),
                    new SegmentPoolDevice("block:b", [rootB]),
                ]);
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new RisingCounters(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromMilliseconds(20),
                    TimeProvider.System,
                    probe);
                await monitor.StartAsync(CancellationToken.None);
                try
                {
                    await WaitAsync(() => monitor.Observations.Space.ContainsKey(rootB));
                    var options = new StorageServerOptions
                    {
                        SegmentPools = new SegmentPoolsOptions { CriticalPercent = 85, HighPercent = 79, LowPercent = 75 },
                        SpoolPersistence = new SpoolPersistenceOptions(),
                    };
                    var counters = new SpoolPersistenceCounters();
                    var processor = new SpoolArticlePersistenceProcessor(
                        "cache01.usenet.ninja",
                        catalog,
                        monitor,
                        new SpoolWriteGate(),
                        new SpoolArticleIdentityGate(),
                        new SpoolHealth(TimeProvider.System),
                        new SpoolPressure(),
                        new SpoolArticlePublisher(new FileSpoolArticleFiles()),
                        counters,
                        options,
                        NullLogger.Instance,
                        TimeProvider.System);
                    var article = Build("<persist-path@example.test>");
                    var first = await processor.ProcessAsync(article, CancellationToken.None);
                    var second = await processor.ProcessAsync(article, CancellationToken.None);

                    Assert.Equal(AcceptedArticleProcessResult.Completed, first);
                    Assert.Equal(AcceptedArticleProcessResult.Completed, second);
                    Assert.Equal(1, counters.Stored);
                    Assert.Equal(1, counters.Duplicates);
                    var relative = SpoolArticlePath.RelativePath(article.ArtId);
                    var path = SpoolArticlePath.CombineUnderRoot(rootB, relative);
                    Assert.True(File.Exists(path));
                    Assert.False(File.Exists(SpoolArticlePath.CombineUnderRoot(rootA, relative)));
                    var text = Encoding.ASCII.GetString(File.ReadAllBytes(path));
                    Assert.Contains("Path: cache01.usenet.ninja!", text, StringComparison.Ordinal);
                    Assert.Contains("body-line\r\n", text, StringComparison.Ordinal);
                    Assert.Contains("<persist-path@example.test>", text, StringComparison.Ordinal);
                }
                finally
                {
                    await monitor.StopAsync(CancellationToken.None);
                }
            }
            finally
            {
                Directory.Delete(rootA, recursive: true);
                Directory.Delete(rootB, recursive: true);
            }
        }

        [Fact]
        public async Task Known_free_space_below_the_article_waits()
        {
            var root = NewRoot();
            try
            {
                var probe = new StaticFreeSpaceProbe();
                probe.Set(root, total: 100, available: 10);
                var catalog = new SegmentPoolCatalog();
                catalog.Publish([new SegmentPoolDevice("block:a", [root])]);
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new RisingCounters(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromMilliseconds(20),
                    TimeProvider.System,
                    probe);
                await monitor.StartAsync(CancellationToken.None);
                try
                {
                    await WaitAsync(() => monitor.Observations.Space.ContainsKey(root));
                    var pressure = new SpoolPressure();
                    var processor = new SpoolArticlePersistenceProcessor(
                        "cache01.usenet.ninja",
                        catalog,
                        monitor,
                        new SpoolWriteGate(),
                        new SpoolArticleIdentityGate(),
                        new SpoolHealth(TimeProvider.System),
                        pressure,
                        new SpoolArticlePublisher(new FileSpoolArticleFiles()),
                        new SpoolPersistenceCounters(),
                        new StorageServerOptions(),
                        NullLogger.Instance,
                        TimeProvider.System);
                    var result = await processor.ProcessAsync(Build("<full@example.test>"), CancellationToken.None);
                    Assert.Equal(AcceptedArticleProcessResult.WaitingForCapacity, result);
                    Assert.True(pressure.IsSaturated);
                    Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
                }
                finally
                {
                    await monitor.StopAsync(CancellationToken.None);
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task Concurrent_same_id_publishes_on_one_spool_and_the_second_waits()
        {
            var rootA = NewRoot();
            var rootB = NewRoot();
            try
            {
                var probe = new StaticFreeSpaceProbe();
                probe.Set(rootA, total: 100_000_000_000, available: 40_000_000_000);
                probe.Set(rootB, total: 100_000_000_000, available: 40_000_000_000);
                var catalog = new SegmentPoolCatalog();
                catalog.Publish(
                [
                    new SegmentPoolDevice("block:a", [rootA]),
                    new SegmentPoolDevice("block:b", [rootB]),
                ]);
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new RisingCounters(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromMilliseconds(20),
                    TimeProvider.System,
                    probe);
                await monitor.StartAsync(CancellationToken.None);
                try
                {
                    await WaitAsync(() => monitor.Observations.Space.ContainsKey(rootA));
                    var files = new HoldingArticleFiles();
                    var identity = new SpoolArticleIdentityGate();
                    var processor = new SpoolArticlePersistenceProcessor(
                        "cache01.usenet.ninja",
                        catalog,
                        monitor,
                        new SpoolWriteGate(),
                        identity,
                        new SpoolHealth(TimeProvider.System),
                        new SpoolPressure(),
                        new SpoolArticlePublisher(files),
                        new SpoolPersistenceCounters(),
                        new StorageServerOptions(),
                        NullLogger.Instance,
                        TimeProvider.System);
                    var article = Build("<same-id@example.test>");
                    var first = Task.Run(() => processor.ProcessAsync(article, CancellationToken.None).AsTask());
                    await files.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    var second = Task.Run(() => processor.ProcessAsync(article, CancellationToken.None).AsTask());
                    await WaitAsync(() => identity.Reservations(article.ArtId) >= 2);
                    Assert.Equal(1, files.WritesStarted);
                    files.Release.TrySetResult();
                    var results = await Task.WhenAll(first, second);
                    Assert.All(results, static result => Assert.Equal(AcceptedArticleProcessResult.Completed, result));
                    Assert.Equal(1, files.FinalFiles);
                }
                finally
                {
                    await monitor.StopAsync(CancellationToken.None);
                }
            }
            finally
            {
                Directory.Delete(rootA, recursive: true);
                Directory.Delete(rootB, recursive: true);
            }
        }

        [Fact]
        public void Disk_full_recovers_from_free_space_and_access_failure_waits_out_a_cooldown()
        {
            var time = new FakeTimeProvider();
            var diskFull = new SpoolHealth(time);
            diskFull.Failure("full", close: true);
            Assert.False(diskFull.AcceptsWrites("full"));
            diskFull.RecoverDiskFull("full");
            Assert.True(diskFull.AcceptsWrites("full"));

            var denied = new SpoolHealth(time);
            denied.Failure("denied", close: false);
            denied.Failure("denied", close: false);
            denied.Failure("denied", close: false);
            Assert.False(denied.AcceptsWrites("denied"));
            denied.RecoverDiskFull("denied");
            Assert.False(denied.AcceptsWrites("denied"));
            time.Advance(SpoolSelectionPolicy.FailureRetry);
            Assert.True(denied.AcceptsWrites("denied"));
            denied.Success("denied");
            Assert.True(denied.AcceptsWrites("denied"));
        }

        [Fact]
        public void Real_filesystem_probe_reports_positive_capacity()
        {
            var root = NewRoot();
            try
            {
                var probe = new FilesystemSpoolFreeSpaceProbe();
                Assert.True(probe.TryRead(root, out var total, out var available));
                Assert.True(total > 0);
                Assert.InRange(available, 0, total);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task Committed_bytes_reduce_the_cached_free_space_until_the_next_probe()
        {
            var root = NewRoot();
            try
            {
                var probe = new StaticFreeSpaceProbe();
                probe.Set(root, total: 10_000, available: 8_000);
                var catalog = new SegmentPoolCatalog();
                catalog.Publish([new SegmentPoolDevice("block:a", [root])]);
                var time = new FakeTimeProvider();
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new RisingCounters(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromHours(1),
                    time,
                    probe);
                InvokeSample(monitor);
                InvokeSample(monitor);
                Assert.Equal(8_000, monitor.Observations.Space[root].AvailableBytes);
                monitor.NoteCommittedBytes(root, 500);
                Assert.Equal(7_500, monitor.Observations.Space[root].AvailableBytes);
                probe.Set(root, total: 10_000, available: 7_500);
                InvokeSample(monitor);
                Assert.Equal(7_500, monitor.Observations.Space[root].AvailableBytes);
                probe.Set(root, total: 10_000, available: 9_000);
                InvokeSample(monitor);
                Assert.Equal(9_000, monitor.Observations.Space[root].AvailableBytes);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task A_probe_in_progress_does_not_restore_a_commit()
        {
            var root = NewRoot();
            try
            {
                var probe = new StaticFreeSpaceProbe();
                probe.Set(root, total: 10_000, available: 8_000);
                var monitor = MonitorFor(root, probe);
                InvokeSample(monitor);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                probe.BeforeReturn = () =>
                {
                    entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                };
                var sampling = Task.Run(() => InvokeSample(monitor));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                monitor.NoteCommittedBytes(root, 500);
                release.TrySetResult();
                await sampling.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(7_500, monitor.Observations.Space[root].AvailableBytes);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task A_duplicate_does_not_subtract_free_space_and_a_failed_write_does_not_either()
        {
            var root = NewRoot();
            try
            {
                var probe = new StaticFreeSpaceProbe();
                probe.Set(root, total: 10_000_000, available: 8_000_000);
                var monitor = MonitorFor(root, probe);
                InvokeSample(monitor);
                var processor = Processor(root, monitor, new SpoolArticlePublisher(new FileSpoolArticleFiles()));
                var before = monitor.Observations.Space[root].AvailableBytes;
                var article = Build("<account@example.test>");
                Assert.Equal(AcceptedArticleProcessResult.Completed, await processor.ProcessAsync(article, CancellationToken.None));
                var stored = monitor.Observations.Space[root].AvailableBytes;
                Assert.True(stored < before);
                Assert.Equal(AcceptedArticleProcessResult.Completed, await processor.ProcessAsync(article, CancellationToken.None));
                Assert.Equal(stored, monitor.Observations.Space[root].AvailableBytes);

                var full = new DiskFullFiles();
                var failing = Processor(root, monitor, new SpoolArticlePublisher(full));
                var again = monitor.Observations.Space[root].AvailableBytes;
                Assert.Equal(
                    AcceptedArticleProcessResult.WaitingForCapacity,
                    await failing.ProcessAsync(Build("<full-write@example.test>"), CancellationToken.None));
                Assert.Equal(again, monitor.Observations.Space[root].AvailableBytes);
                Assert.Equal(0, full.FinalFiles);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task Occupancy_above_high_percent_still_publishes()
        {
            var root = NewRoot();
            try
            {
                var probe = new StaticFreeSpaceProbe();
                // 80% used: above High (79) but at or below Critical (85), so placement remains eligible.
                probe.Set(root, total: 1_000_000, available: 200_000);
                var monitor = MonitorFor(root, probe);
                InvokeSample(monitor);
                var pressure = new SpoolPressure();
                var processor = Processor(root, monitor, new SpoolArticlePublisher(new FileSpoolArticleFiles()), pressure);
                var result = await processor.ProcessAsync(Build("<high-percent@example.test>"), CancellationToken.None);
                Assert.Equal(AcceptedArticleProcessResult.Completed, result);
                Assert.False(pressure.IsSaturated);
                Assert.NotEmpty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task A_disk_full_spool_does_not_block_another_spool()
        {
            var rootA = NewRoot();
            var rootB = NewRoot();
            try
            {
                var probe = new StaticFreeSpaceProbe();
                // rootA has more free bytes but will fail writes; rootB must stay ≤ Critical to be selectable.
                probe.Set(rootA, total: 40L << 30, available: 20L << 30);
                probe.Set(rootB, total: 40L << 30, available: 10L << 30);
                var catalog = new SegmentPoolCatalog();
                catalog.Publish(
                [
                    new SegmentPoolDevice("block:a", [rootA]),
                    new SegmentPoolDevice("block:b", [rootB]),
                ]);
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new RisingCounters(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromHours(1),
                    TimeProvider.System,
                    probe);
                InvokeSample(monitor);
                var files = new DiskFullFiles(rootA);
                var processor = new SpoolArticlePersistenceProcessor(
                    "cache01.usenet.ninja",
                    catalog,
                    monitor,
                    new SpoolWriteGate(),
                    new SpoolArticleIdentityGate(),
                    new SpoolHealth(TimeProvider.System),
                    new SpoolPressure(),
                    new SpoolArticlePublisher(files),
                    new SpoolPersistenceCounters(),
                    new StorageServerOptions(),
                    NullLogger.Instance,
                    TimeProvider.System);
                var article = Build("<fallback@example.test>");
                Assert.Equal(AcceptedArticleProcessResult.Completed, await processor.ProcessAsync(article, CancellationToken.None));
                Assert.Equal(1, files.FinalFiles);
                Assert.Contains(rootB, files.FinalPath, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(rootA, recursive: true);
                Directory.Delete(rootB, recursive: true);
            }
        }

        private static BlockDeviceMonitorService MonitorFor(string root, ISpoolFreeSpaceProbe probe)
        {
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:a", [root])]);
            return new BlockDeviceMonitorService(
                catalog,
                new RisingCounters(),
                NullLogger<BlockDeviceMonitorService>.Instance,
                TimeSpan.FromHours(1),
                TimeProvider.System,
                probe);
        }

        private static SpoolArticlePersistenceProcessor Processor(
            string root,
            BlockDeviceMonitorService monitor,
            SpoolArticlePublisher publisher,
            SpoolPressure? pressure = null)
        {
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:a", [root])]);
            return new SpoolArticlePersistenceProcessor(
                "cache01.usenet.ninja",
                catalog,
                monitor,
                new SpoolWriteGate(),
                new SpoolArticleIdentityGate(),
                new SpoolHealth(TimeProvider.System),
                pressure ?? new SpoolPressure(),
                publisher,
                new SpoolPersistenceCounters(),
                new StorageServerOptions(),
                NullLogger.Instance,
                TimeProvider.System);
        }

        private static void InvokeSample(BlockDeviceMonitorService monitor) =>
            monitor.GetType().GetMethod("Sample", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(monitor, null);

        private static string NewRoot()
        {
            var path = Path.Join(Path.GetTempPath(), "vectornntp-spool-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static AcceptedArticle Build(string messageId)
        {
            var parser = new NntpArticleParser("nntpd01.usenet.ninja");
            var text = "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: "
                + messageId
                + "\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: vatp\r\n\r\nbody-line\r\n";
            var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(text), ArticlePathMode.Normalize);
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            return new AcceptedArticle(created.Record.ArtId, created.Record.ArtHash, created.Record.ArtSize, created.Record.ArtData);
        }

        private static string BodyOf(ReadOnlySpan<byte> article)
        {
            var text = Encoding.ASCII.GetString(article);
            var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.True(split >= 0);
            return text[(split + 4)..];
        }

        private static string HeaderLine(string article, string name)
        {
            var prefix = name + ": ";
            var start = article.IndexOf(prefix, StringComparison.Ordinal);
            Assert.True(start >= 0, name);
            var end = article.IndexOf("\r\n", start, StringComparison.Ordinal);
            Assert.True(end > start);
            return article[start..end];
        }

        private static async Task WaitAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("condition was not met");
                }

                await Task.Delay(10);
            }
        }

        private sealed class HoldingArticleFiles : ISpoolArticleFiles
        {
            private readonly object _gate = new();
            private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

            public int WritesStarted { get; private set; }

            public int FinalFiles
            {
                get
                {
                    lock (_gate)
                    {
                        return _files.Keys.Count(static path => !path.Contains(".writing-", StringComparison.Ordinal));
                    }
                }
            }

            public TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool Exists(string path)
            {
                lock (_gate)
                {
                    return _files.ContainsKey(path);
                }
            }

            public long Length(string path)
            {
                lock (_gate)
                {
                    return _files[path].Length;
                }
            }

            public bool ContentsEqual(string path, ReadOnlySpan<byte> content)
            {
                lock (_gate)
                {
                    return _files.TryGetValue(path, out var stored) && stored.AsSpan().SequenceEqual(content);
                }
            }

            public void CreateDirectory(string path)
            {
            }

            public void Delete(string path)
            {
                lock (_gate)
                {
                    _files.Remove(path);
                }
            }

            public void WriteAll(string path, ReadOnlySpan<byte> content, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    _files[path] = content.ToArray();
                    WritesStarted++;
                }

                if (WritesStarted == 1)
                {
                    FirstWrite.TrySetResult();
                }

                Release.Task.GetAwaiter().GetResult();
            }

            public void MoveNew(string source, string destination)
            {
                lock (_gate)
                {
                    if (!_files.Remove(source, out var bytes))
                    {
                        throw new IOException("missing");
                    }

                    _files[destination] = bytes;
                }
            }
        }

        private sealed class DiskFullFiles : ISpoolArticleFiles
        {
            private readonly string? _prefix;
            private readonly object _gate = new();
            private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

            public DiskFullFiles(string? prefix = null) => _prefix = prefix;

            public int FinalFiles
            {
                get
                {
                    lock (_gate)
                    {
                        return _files.Keys.Count(static path => !path.Contains(".writing-", StringComparison.Ordinal));
                    }
                }
            }

            public string FinalPath
            {
                get
                {
                    lock (_gate)
                    {
                        return _files.Keys.First(static path => !path.Contains(".writing-", StringComparison.Ordinal));
                    }
                }
            }

            public bool Exists(string path)
            {
                lock (_gate)
                {
                    return _files.ContainsKey(path);
                }
            }

            public long Length(string path)
            {
                lock (_gate)
                {
                    return _files[path].Length;
                }
            }

            public bool ContentsEqual(string path, ReadOnlySpan<byte> content)
            {
                lock (_gate)
                {
                    return _files.TryGetValue(path, out var stored) && stored.AsSpan().SequenceEqual(content);
                }
            }

            public void CreateDirectory(string path)
            {
            }

            public void Delete(string path)
            {
                lock (_gate)
                {
                    _files.Remove(path);
                }
            }

            public void WriteAll(string path, ReadOnlySpan<byte> content, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_prefix is null || path.StartsWith(_prefix, StringComparison.Ordinal))
                {
                    throw new IOException("disk full") { HResult = unchecked((int)0x80070070) };
                }

                lock (_gate)
                {
                    _files[path] = content.ToArray();
                }
            }

            public void MoveNew(string source, string destination)
            {
                lock (_gate)
                {
                    if (!_files.Remove(source, out var bytes))
                    {
                        throw new IOException("missing");
                    }

                    _files[destination] = bytes;
                }
            }
        }

        private sealed class StaticFreeSpaceProbe : ISpoolFreeSpaceProbe
        {
            private readonly Dictionary<string, (long Total, long Available)> _space = new(StringComparer.Ordinal);

            public void Set(string path, long total, long available) => _space[path] = (total, available);

            public Action? BeforeReturn { get; set; }

            public bool TryRead(string spoolRoot, out long totalBytes, out long availableBytes)
            {
                BeforeReturn?.Invoke();
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
            private ulong _value = 1000;

            public bool TryRead(string deviceId, out BlockDeviceCounters counters, out string? error)
            {
                _value += 1000;
                counters = new BlockDeviceCounters(_value, _value, _value, _value, _value, _value, LatencyAvailable: true);
                error = null;
                return true;
            }
        }
    }
}
