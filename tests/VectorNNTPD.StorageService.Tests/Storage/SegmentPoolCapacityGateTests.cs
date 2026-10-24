using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;

namespace VectorNNTPD.StorageService.Tests.Storage
{
    public sealed class SegmentPoolCapacityGateTests
    {
        [Theory]
        [InlineData(260)] // 74%
        [InlineData(250)] // 75%
        [InlineData(220)] // 78%
        public void Below_high_allows_admission(int availableBytes)
        {
            using var env = CapacityEnv.Create();
            env.SetAvailable(availableBytes);
            Assert.True(SpoolSelector.UsedPercent(1000, availableBytes) < 79);
            Assert.True(env.Gate.AllowsAdmission());
        }

        [Fact]
        public void Exactly_critical_still_allows_admission()
        {
            using var env = CapacityEnv.Create();
            env.SetAvailable(150); // 85%
            Assert.Equal(85, SpoolSelector.UsedPercent(1000, 150));
            Assert.True(env.Gate.AllowsAdmission());
        }

        [Fact]
        public void Above_critical_blocks_until_any_spool_recovers()
        {
            using var env = CapacityEnv.Create();
            env.SetAvailable(140); // 86%
            Assert.False(env.Gate.AllowsAdmission());

            // Recovery no longer requires LowPercent on every spool — eligibility is enough.
            env.SetAvailable(160); // 84% ≤ Critical
            Assert.True(env.Gate.AllowsAdmission());
        }

        [Fact]
        public void Missing_sample_fail_closes_admission()
        {
            using var env = CapacityEnv.Create(publishSample: false);
            Assert.False(env.Gate.AllowsAdmission());
        }

        [Fact]
        public void One_eligible_spool_allows_admission_when_sibling_is_unknown()
        {
            var rootA = ArticleIndexTestSupport.NewDirectory();
            var rootB = ArticleIndexTestSupport.NewDirectory();
            try
            {
                var catalog = new SegmentPoolCatalog();
                catalog.Publish(
                [
                    new SegmentPoolDevice("block:a", [Path.GetFullPath(rootA)]),
                    new SegmentPoolDevice("block:b", [Path.GetFullPath(rootB)]),
                ]);
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new PlatformBlockDeviceCounterSource(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromMinutes(1),
                    TimeProvider.System,
                    freeSpace: null);
                var options = Options.Create(new StorageServerOptions
                {
                    SegmentPools = new SegmentPoolsOptions
                    {
                        CriticalPercent = 85,
                        HighPercent = 79,
                        LowPercent = 75,
                    },
                });
                var registry = new SpoolCapacityRegistry(catalog, monitor, options, TimeProvider.System);
                var gate = new SegmentPoolCapacityGate(
                    registry,
                    monitor,
                    options,
                    NullLogger<SegmentPoolCapacityGate>.Instance);
                var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootA));
                monitor.SetSpaceObservationForTests(a, 1000, 500);
                Assert.True(gate.AllowsAdmission());
            }
            finally
            {
                ArticleIndexTestSupport.Delete(rootA);
                ArticleIndexTestSupport.Delete(rootB);
            }
        }

        [Fact]
        public void Zero_total_is_not_a_valid_sample()
        {
            var space = new Dictionary<string, SpoolSpaceObservation>(StringComparer.Ordinal)
            {
                ["/spool"] = new SpoolSpaceObservation(0, 0, 1),
            };
            var snapshot = new SpoolObservationSnapshot(
                1,
                new Dictionary<string, BlockDeviceMonitorService.PublishedSample>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                space);
            Assert.False(SegmentPoolCapacityGate.TryGetUsedPercent(snapshot, "/spool", out _));
        }

        private sealed class CapacityEnv : IDisposable
        {
            private readonly string _spoolDir;

            private CapacityEnv(
                string spoolDir,
                BlockDeviceMonitorService monitor,
                SegmentPoolCapacityGate gate,
                string canonicalSpool)
            {
                _spoolDir = spoolDir;
                Monitor = monitor;
                Gate = gate;
                CanonicalSpool = canonicalSpool;
            }

            internal BlockDeviceMonitorService Monitor { get; }

            internal SegmentPoolCapacityGate Gate { get; }

            internal string CanonicalSpool { get; }

            internal static CapacityEnv Create(bool publishSample = true)
            {
                var spoolDir = ArticleIndexTestSupport.NewDirectory();
                var catalog = ArticleIndexTestSupport.Catalog(spoolDir);
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new PlatformBlockDeviceCounterSource(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromMinutes(1),
                    TimeProvider.System,
                    freeSpace: null);
                var options = Options.Create(new StorageServerOptions
                {
                    SegmentPools = new SegmentPoolsOptions
                    {
                        CriticalPercent = 85,
                        HighPercent = 79,
                        LowPercent = 75,
                    },
                });
                var registry = new SpoolCapacityRegistry(catalog, monitor, options, TimeProvider.System);
                var gate = new SegmentPoolCapacityGate(
                    registry,
                    monitor,
                    options,
                    NullLogger<SegmentPoolCapacityGate>.Instance);
                var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spoolDir));
                var env = new CapacityEnv(spoolDir, monitor, gate, canonical);
                if (publishSample)
                {
                    env.SetAvailable(300);
                }

                return env;
            }

            internal void SetAvailable(int availableBytes) =>
                Monitor.SetSpaceObservationForTests(CanonicalSpool, totalBytes: 1000, availableBytes: availableBytes);

            public void Dispose() => ArticleIndexTestSupport.Delete(_spoolDir);
        }
    }
}
