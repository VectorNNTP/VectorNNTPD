using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolShards;
using VectorNNTPD.StorageService.Tests.TestDoubles;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    public sealed class SpoolShardInitializationTests
    {
        [Fact]
        public async Task Missing_directories_are_created_and_a_second_pass_accepts_them()
        {
            var directories = new MemorySpoolShardDirectorySystem();
            var service = CreateService(directories, @"C:\spool\one");

            await service.StartAsync(CancellationToken.None);
            var first = service.LastResult;
            Assert.NotNull(first);
            Assert.Equal(SpoolShardLayout.DirectoryCount, first.Value.Created);
            Assert.Equal(0, first.Value.AlreadyPresent);

            await service.StartAsync(CancellationToken.None);
            var second = service.LastResult;
            Assert.NotNull(second);
            Assert.Equal(0, second.Value.Created);
            Assert.Equal(SpoolShardLayout.DirectoryCount, second.Value.AlreadyPresent);
            Assert.Equal(SpoolShardLayout.DirectoryCount, directories.Count);
        }

        [Fact]
        public async Task A_partial_tree_is_resumed_without_recreating_existing_directories()
        {
            var directories = new MemorySpoolShardDirectorySystem();
            var spool = @"D:\pools\cache";
            var service = CreateService(directories, spool);
            for (var i = 0; i < 100; i++)
            {
                var path = Path.Combine(spool, SpoolShardLayout.RelativeDirectories[i].Replace('/', Path.DirectorySeparatorChar));
                Assert.Equal(SpoolDirectoryCreateResult.Created, directories.CreateDirectory(path));
            }

            await service.StartAsync(CancellationToken.None);

            var result = service.LastResult;
            Assert.NotNull(result);
            Assert.Equal(SpoolShardLayout.DirectoryCount - 100, result.Value.Created);
            Assert.Equal(100, result.Value.AlreadyPresent);
            Assert.Equal(SpoolShardLayout.DirectoryCount, directories.Count);
        }

        [Fact]
        public async Task Duplicate_catalog_paths_are_initialized_once()
        {
            var directories = new MemorySpoolShardDirectorySystem();
            var catalog = new SegmentPoolCatalog();
            catalog.Publish(
            [
                new SegmentPoolDevice("disk-a", [@"E:\spool"]),
                new SegmentPoolDevice("disk-b", [@"E:\spool"]),
            ]);
            var service = new SpoolShardInitializationService(
                catalog,
                directories,
                NullLogger<SpoolShardInitializationService>.Instance,
                TimeProvider.System,
                concurrency: 4);

            await service.StartAsync(CancellationToken.None);

            Assert.NotNull(service.LastResult);
            Assert.Equal(SpoolShardLayout.DirectoryCount, service.LastResult.Value.Created);
            Assert.Single(service.LastResult.Value.Spools);
        }

        [Fact]
        public async Task Empty_catalog_fails_before_any_create()
        {
            var directories = new MemorySpoolShardDirectorySystem();
            var service = new SpoolShardInitializationService(
                new SegmentPoolCatalog(),
                directories,
                NullLogger<SpoolShardInitializationService>.Instance);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Contains("validated spool", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, directories.Count);
            Assert.Null(service.LastResult);
        }

        [Fact]
        public async Task Shared_gate_limits_creates_across_spools()
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fourth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var directories = new GatedDirectorySystem(hold.Task, fourth, limit: 4);
            var catalog = Catalog(@"S:\a", @"S:\b", @"S:\c", @"S:\d", @"S:\e");
            var service = new SpoolShardInitializationService(
                catalog,
                directories,
                NullLogger<SpoolShardInitializationService>.Instance,
                TimeProvider.System,
                concurrency: 4);

            var starting = service.StartAsync(CancellationToken.None);
            await fourth.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4, directories.MaxInFlight);
            Assert.False(directories.Exceeded);
            hold.TrySetResult();
            await starting.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(directories.Exceeded);
            Assert.Equal(5L * SpoolShardLayout.DirectoryCount, service.LastResult!.Value.Created);
        }

        [Fact]
        public async Task A_slow_spool_does_not_keep_another_spool_from_starting()
        {
            var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fastCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var directories = new IndependentDirectorySystem(@"S:\slow", @"S:\fast", slowEntered, fastCreated);
            var service = new SpoolShardInitializationService(
                Catalog(@"S:\slow", @"S:\fast"),
                directories,
                NullLogger<SpoolShardInitializationService>.Instance,
                TimeProvider.System,
                concurrency: 4);

            var starting = service.StartAsync(CancellationToken.None);
            await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fastCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await starting.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(2, service.LastResult!.Value.Spools.Count);
            Assert.Equal(2L * SpoolShardLayout.DirectoryCount, service.LastResult.Value.Created);
        }

        [Fact]
        public async Task Cancellation_stops_initialization_without_publishing_a_result()
        {
            var cts = new CancellationTokenSource();
            var directories = new CancelOnFirstCreate(cts);
            var service = CreateService(directories, @"C:\spool\cancel", concurrency: 2);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(cts.Token));
            Assert.Null(service.LastResult);
        }

        [Fact]
        public async Task One_spool_failure_is_reported_after_the_other_spool_finishes()
        {
            var directories = new FailSpoolDirectorySystem(@"S:\bad");
            var service = new SpoolShardInitializationService(
                Catalog(@"S:\bad", @"S:\good"),
                directories,
                NullLogger<SpoolShardInitializationService>.Instance,
                TimeProvider.System,
                concurrency: 4);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Contains("shard create denied", error.Message, StringComparison.Ordinal);
            Assert.Contains(@"S:\good", directories.CreatedRoots, StringComparer.Ordinal);
            Assert.True(directories.GoodCreated == SpoolShardLayout.DirectoryCount);
            Assert.Null(service.LastResult);
        }

        [Fact]
        public async Task Real_directory_initialization_is_idempotent_and_creates_no_article_files()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-spool-shards-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(root);
            try
            {
                var directories = new FileSpoolShardDirectorySystem();
                var service = CreateService(directories, root, concurrency: SpoolShardLayout.InitializationConcurrency);
                var started = TimeProvider.System.GetTimestamp();
                await service.StartAsync(CancellationToken.None);
                var elapsed = TimeProvider.System.GetElapsedTime(started);
                var first = service.LastResult;
                Assert.NotNull(first);
                Assert.Equal(SpoolShardLayout.DirectoryCount, first.Value.Created);
                Assert.Equal(0, first.Value.AlreadyPresent);
                Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));

                await service.StartAsync(CancellationToken.None);
                var second = service.LastResult;
                Assert.NotNull(second);
                Assert.Equal(0, second.Value.Created);
                Assert.Equal(SpoolShardLayout.DirectoryCount, second.Value.AlreadyPresent);

                Console.WriteLine(
                    "spool-shard-init platform={0} directories={1} concurrency={2} elapsedMilliseconds={3} temp={4}",
                    OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other",
                    SpoolShardLayout.DirectoryCount,
                    SpoolShardLayout.InitializationConcurrency,
                    (long)elapsed.TotalMilliseconds,
                    root);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task A_file_where_a_shard_directory_belongs_fails_initialization()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-spool-file-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(Path.Combine(root, "0"), [1]);
            try
            {
                var service = CreateService(new FileSpoolShardDirectorySystem(), root, concurrency: 4);
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
                Assert.Null(service.LastResult);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void File_spool_directory_system_reports_created_and_already_present()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-spool-dir-" + Guid.NewGuid().ToString("n"));
            var leaf = Path.Combine(root, "shard");
            var directories = new FileSpoolShardDirectorySystem();
            try
            {
                Assert.Equal(SpoolDirectoryCreateResult.Created, directories.CreateDirectory(leaf));
                Assert.True(Directory.Exists(leaf));
                Assert.Equal(SpoolDirectoryCreateResult.AlreadyPresent, directories.CreateDirectory(leaf));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact]
        public void File_spool_directory_system_rejects_an_existing_file_path()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-spool-notdir-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(root);
            var filePath = Path.Combine(root, "not-a-dir");
            File.WriteAllBytes(filePath, [1]);
            var directories = new FileSpoolShardDirectorySystem();
            try
            {
                var error = Assert.Throws<IOException>(() => directories.CreateDirectory(filePath));
                Assert.Contains("not a directory", error.Message, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static SpoolShardInitializationService CreateService(
            ISpoolShardDirectorySystem directories,
            string spool,
            int concurrency = 8)
        {
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("disk", [spool])]);
            return new SpoolShardInitializationService(
                catalog,
                directories,
                NullLogger<SpoolShardInitializationService>.Instance,
                TimeProvider.System,
                concurrency);
        }

        private static SegmentPoolCatalog Catalog(params string[] spools)
        {
            var catalog = new SegmentPoolCatalog();
            var devices = new SegmentPoolDevice[spools.Length];
            for (var i = 0; i < spools.Length; i++)
            {
                devices[i] = new SegmentPoolDevice("disk-" + i, [spools[i]]);
            }

            catalog.Publish(devices);
            return catalog;
        }

        private sealed class GatedDirectorySystem(Task hold, TaskCompletionSource fourth, int limit) : ISpoolShardDirectorySystem
        {
            private int _inFlight;
            private int _max;

            public int MaxInFlight => Volatile.Read(ref _max);

            public bool Exceeded { get; private set; }

            public SpoolDirectoryCreateResult CreateDirectory(string fullPath)
            {
                var now = Interlocked.Increment(ref _inFlight);
                if (now > limit)
                {
                    Exceeded = true;
                }

                var max = Volatile.Read(ref _max);
                while (now > max && Interlocked.CompareExchange(ref _max, now, max) != max)
                {
                    max = Volatile.Read(ref _max);
                }

                if (now == limit)
                {
                    fourth.TrySetResult();
                }

                hold.Wait(TimeSpan.FromSeconds(10));
                Interlocked.Decrement(ref _inFlight);
                return SpoolDirectoryCreateResult.Created;
            }
        }

        private sealed class IndependentDirectorySystem(
            string slowRoot,
            string fastRoot,
            TaskCompletionSource slowEntered,
            TaskCompletionSource fastCreated) : ISpoolShardDirectorySystem
        {
            public SpoolDirectoryCreateResult CreateDirectory(string fullPath)
            {
                if (fullPath.StartsWith(slowRoot, StringComparison.Ordinal))
                {
                    slowEntered.TrySetResult();
                    fastCreated.Task.Wait(TimeSpan.FromSeconds(10));
                }
                else if (fullPath.StartsWith(fastRoot, StringComparison.Ordinal))
                {
                    fastCreated.TrySetResult();
                }

                return SpoolDirectoryCreateResult.Created;
            }
        }

        private sealed class CancelOnFirstCreate(CancellationTokenSource cancellation) : ISpoolShardDirectorySystem
        {
            public SpoolDirectoryCreateResult CreateDirectory(string fullPath)
            {
                cancellation.Cancel();
                return SpoolDirectoryCreateResult.Created;
            }
        }

        private sealed class FailSpoolDirectorySystem(string failingRoot) : ISpoolShardDirectorySystem
        {
            private readonly HashSet<string> _roots = new(StringComparer.Ordinal);

            public IReadOnlySet<string> CreatedRoots => _roots;

            public int GoodCreated { get; private set; }

            public SpoolDirectoryCreateResult CreateDirectory(string fullPath)
            {
                if (fullPath.StartsWith(failingRoot, StringComparison.Ordinal))
                {
                    throw new IOException("shard create denied");
                }

                lock (_roots)
                {
                    _roots.Add(fullPath.StartsWith(@"S:\good", StringComparison.Ordinal) ? @"S:\good" : fullPath);
                    if (fullPath.StartsWith(@"S:\good", StringComparison.Ordinal))
                    {
                        GoodCreated++;
                    }
                }

                return SpoolDirectoryCreateResult.Created;
            }
        }
    }
}
