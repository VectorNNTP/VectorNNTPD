using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage.ArticleIndex;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;
using VectorNNTPD.StorageService.Storage.SpoolShards;

namespace VectorNNTPD.StorageService.Tests.Storage
{
    public sealed class PerSpoolCapacityTests
    {
        [Fact]
        public async Task Eight_configured_spools_produce_eight_independent_monitors()
        {
            var roots = Enumerable.Range(0, 8).Select(_ => ArticleIndexTestSupport.NewDirectory()).ToArray();
            try
            {
                var catalog = new SegmentPoolCatalog();
                var devices = roots
                    .Select((root, i) => new SegmentPoolDevice(
                        "block:" + i,
                        [Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))]))
                    .ToArray();
                catalog.Publish(devices);
                using var host = BuildMonitorHost(catalog);
                var service = host.GetRequiredService<SpoolEvictionMonitorService>();
                await service.StartAsync(CancellationToken.None);
                Assert.Equal(8, service.Monitors.Count);
                Assert.Equal(8, service.Monitors.Select(static m => m.SpoolDirectory).Distinct(StringComparer.Ordinal).Count());
                await service.StopAsync(CancellationToken.None);
            }
            finally
            {
                foreach (var root in roots)
                {
                    ArticleIndexTestSupport.Delete(root);
                }
            }
        }

        [Fact]
        public async Task One_configured_spool_produces_one_monitor()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            try
            {
                var catalog = ArticleIndexTestSupport.Catalog(root);
                using var host = BuildMonitorHost(catalog);
                var service = host.GetRequiredService<SpoolEvictionMonitorService>();
                await service.StartAsync(CancellationToken.None);
                Assert.Single(service.Monitors);
                Assert.Equal(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                    service.Monitors[0].SpoolDirectory);
                await service.StopAsync(CancellationToken.None);
            }
            finally
            {
                ArticleIndexTestSupport.Delete(root);
            }
        }

        [Fact]
        public async Task Independent_spool_at_high_evicts_while_sibling_stays_idle()
        {
            var db = ArticleIndexTestSupport.NewDirectory();
            var hot = ArticleIndexTestSupport.NewDirectory();
            var cool = ArticleIndexTestSupport.NewDirectory();
            ArticleIndexService? index = null;
            BlockDeviceMonitorService? monitor = null;
            try
            {
                var catalog = new SegmentPoolCatalog();
                var hotPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hot));
                var coolPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cool));
                catalog.Publish(
                [
                    new SegmentPoolDevice("block:hot", [hotPath]),
                    new SegmentPoolDevice("block:cool", [coolPath]),
                ]);
                monitor = new BlockDeviceMonitorService(
                    catalog,
                    new PlatformBlockDeviceCounterSource(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromMinutes(1),
                    TimeProvider.System,
                    freeSpace: null);
                var options = Options.Create(new StorageServerOptions
                {
                    DbDir = db,
                    SegmentPools = new SegmentPoolsOptions { LowPercent = 75, HighPercent = 79, CriticalPercent = 85 },
                });
                var files = new FileSpoolArticleFiles();
                var leases = new ArticleReadLeaseRegistry();
                var eviction = new SpoolLfuEviction(options, monitor, files, leases, NullLogger<ArticleIndexService>.Instance);
                index = new ArticleIndexService(
                    options.Value,
                    catalog,
                    NullLogger<ArticleIndexService>.Instance,
                    TimeProvider.System,
                    ArticleIndexTestSupport.FastSettings(),
                    eviction);
                await index.StartAsync(CancellationToken.None);

                for (var i = 0; i < 4; i++)
                {
                    var hotId = ArticleIndexTestSupport.Id(200 + i);
                    var coolId = ArticleIndexTestSupport.Id(300 + i);
                    ArticleIndexTestSupport.PlacePayload(hot, hotId, new string('h', 40));
                    ArticleIndexTestSupport.PlacePayload(cool, coolId, new string('c', 40));
                    await index.InsertStoredAsync(hotId, hot, i, CancellationToken.None, artSizeBytes: 40);
                    await index.InsertStoredAsync(coolId, cool, i, CancellationToken.None, artSizeBytes: 40);
                }

                monitor.SetSpaceObservationForTests(hotPath, totalBytes: 1000, availableBytes: 200); // 80%
                monitor.SetSpaceObservationForTests(coolPath, totalBytes: 1000, availableBytes: 400); // 60%
                await index.EvictUntilBelowHighAsync(hot, CancellationToken.None);
                await index.EvictUntilBelowHighAsync(cool, CancellationToken.None);

                Assert.True(index.PresenceCount < 8);
                Assert.Equal(4, CountPresence(index, cool, 300, 4));
                Assert.True(CountPresence(index, hot, 200, 4) < 4);
            }
            finally
            {
                if (index is not null)
                {
                    await ArticleIndexTestSupport.StopAsync(index);
                }

                if (monitor is not null)
                {
                    await monitor.StopAsync(CancellationToken.None);
                }

                ArticleIndexTestSupport.Delete(db);
                ArticleIndexTestSupport.Delete(hot);
                ArticleIndexTestSupport.Delete(cool);
            }
        }

        [Fact]
        public void Shared_device_note_freed_updates_sibling_cached_space()
        {
            var left = ArticleIndexTestSupport.NewDirectory();
            var right = ArticleIndexTestSupport.NewDirectory();
            try
            {
                var leftPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
                var rightPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
                var catalog = new SegmentPoolCatalog();
                catalog.Publish([new SegmentPoolDevice("block:shared", [leftPath, rightPath])]);
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new PlatformBlockDeviceCounterSource(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromMinutes(1),
                    TimeProvider.System,
                    freeSpace: null);
                monitor.SetSpaceObservationForTests(leftPath, 1000, 200);
                monitor.SetSpaceObservationForTests(rightPath, 1000, 200);
                monitor.NoteFreedBytes(leftPath, 50);
                Assert.Equal(250, monitor.Observations.Space[leftPath].AvailableBytes);
                Assert.Equal(250, monitor.Observations.Space[rightPath].AvailableBytes);
            }
            finally
            {
                ArticleIndexTestSupport.Delete(left);
                ArticleIndexTestSupport.Delete(right);
            }
        }

        [Fact]
        public async Task One_monitor_failure_does_not_stop_sibling_monitors()
        {
            var roots = Enumerable.Range(0, 2).Select(_ => ArticleIndexTestSupport.NewDirectory()).ToArray();
            try
            {
                var catalog = new SegmentPoolCatalog();
                catalog.Publish(
                [
                    new SegmentPoolDevice("block:0", [Path.GetFullPath(roots[0])]),
                    new SegmentPoolDevice("block:1", [Path.GetFullPath(roots[1])]),
                ]);
                using var host = BuildMonitorHost(catalog);
                var service = host.GetRequiredService<SpoolEvictionMonitorService>();
                await service.StartAsync(CancellationToken.None);
                Assert.Equal(2, service.Monitors.Count);
                foreach (var monitor in service.Monitors)
                {
                    Assert.False(monitor.Execution!.IsCompleted);
                }

                await service.StopAsync(CancellationToken.None);
                Assert.All(service.Monitors, static m => Assert.True(m.Execution!.IsCompleted));
            }
            finally
            {
                foreach (var root in roots)
                {
                    ArticleIndexTestSupport.Delete(root);
                }
            }
        }

        [Fact]
        public void Placement_selects_spool_with_most_available_bytes()
        {
            var small = new SpoolCandidate(
                "small",
                "d1",
                TotalBytes: 1000,
                AvailableBytes: 400,
                SpaceKnown: true,
                Initialized: true,
                AcceptsWrites: true,
                DeviceSampleFresh: true,
                ActiveTimePercent: 1,
                WriteLatencyMilliseconds: 1,
                AverageQueueDepth: null,
                CurrentQueueDepth: null,
                InFlightWrites: 0,
                InFlightLimit: 4);
            var large = new SpoolCandidate(
                "large",
                "d2",
                TotalBytes: 10_000,
                AvailableBytes: 2000,
                SpaceKnown: true,
                Initialized: true,
                AcceptsWrites: true,
                DeviceSampleFresh: true,
                ActiveTimePercent: 1,
                WriteLatencyMilliseconds: 1,
                AverageQueueDepth: null,
                CurrentQueueDepth: null,
                InFlightWrites: 0,
                InFlightLimit: 4);
            Assert.Equal("large", SpoolSelector.Select([small, large], 10, criticalPercent: 85)?.SpoolRoot);
        }

        private static int CountPresence(ArticleIndexService index, string spool, int idBase, int count)
        {
            var present = 0;
            for (var i = 0; i < count; i++)
            {
                if (index.Contains(ArticleIndexTestSupport.Id(idBase + i)))
                {
                    present++;
                }
            }

            _ = spool;
            return present;
        }

        private static ServiceProvider BuildMonitorHost(SegmentPoolCatalog catalog)
        {
            var services = new ServiceCollection();
            services.AddSingleton(catalog);
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(Options.Create(new StorageServerOptions
            {
                SegmentPools = new SegmentPoolsOptions { CriticalPercent = 85, HighPercent = 79, LowPercent = 75 },
            }));
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton<BlockDeviceMonitorService>(sp => new BlockDeviceMonitorService(
                sp.GetRequiredService<SegmentPoolCatalog>(),
                new PlatformBlockDeviceCounterSource(),
                NullLogger<BlockDeviceMonitorService>.Instance,
                TimeSpan.FromMinutes(1),
                TimeProvider.System,
                freeSpace: null));
            services.AddSingleton<SpoolCapacityRegistry>();
            services.AddSingleton<SpoolEvictionMonitorService>();
            return services.BuildServiceProvider();
        }
    }
}
