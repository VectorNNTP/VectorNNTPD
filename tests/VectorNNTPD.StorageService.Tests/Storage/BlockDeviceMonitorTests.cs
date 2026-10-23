using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using VectorNNTPD.StorageService.Storage.SegmentPools;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    public sealed class BlockDeviceMonitorTests
    {
        [Fact]
        public void Throughput_uses_elapsed_time_and_keeps_zero_distinct_from_unavailable_latency()
        {
            var previous = new BlockDeviceCounters(1_000, 2_000, 4, 2, 4_000, 2_000, true);
            var current = new BlockDeviceCounters(5_000, 2_000, 8, 2, 12_000, 2_000, true);
            var sample = BlockDeviceRates.Compute(previous, current, TimeSpan.FromSeconds(2));
            Assert.False(sample.Skipped);
            Assert.Equal(2_000, sample.ReadBytesPerSecond);
            Assert.Equal(0, sample.WriteBytesPerSecond);
            Assert.Equal(2, sample.ReadIops);
            Assert.Equal(0, sample.WriteIops);
            Assert.Equal(2, sample.TotalIops);
            Assert.Equal(2, sample.ReadLatencyMilliseconds);
            Assert.Null(sample.WriteLatencyMilliseconds);
            Assert.Equal("none", sample.CounterResets);
        }

        [Fact]
        public void Active_time_queue_depth_and_iops_use_platform_units()
        {
            var previous = new BlockDeviceCounters(
                0, 0, 0, 0, 0, 0, true,
                DeviceBusyTimeAvailable: true,
                DeviceBusyMicroseconds: 0,
                WeightedQueueAvailable: true,
                WeightedQueueMicroseconds: 0,
                CurrentQueueAvailable: true,
                CurrentQueueDepth: 4);
            var current = previous with
            {
                ReadOperations = 4,
                WriteOperations = 2,
                ReadBusyMicroseconds = 8_000,
                WriteBusyMicroseconds = 2_000,
                DeviceBusyMicroseconds = 500_000,
                WeightedQueueMicroseconds = 2_000_000,
                CurrentQueueDepth = 1,
            };
            var sample = BlockDeviceRates.Compute(previous, current, TimeSpan.FromSeconds(1));
            Assert.Equal(4, sample.ReadIops);
            Assert.Equal(2, sample.WriteIops);
            Assert.Equal(6, sample.TotalIops);
            Assert.Equal(2, sample.ReadLatencyMilliseconds);
            Assert.Equal(1, sample.WriteLatencyMilliseconds);
            Assert.Equal(50, sample.ActiveTimePercent);
            Assert.Equal(2, sample.AverageQueueDepth);
            Assert.Equal(1, sample.CurrentQueueDepth);
        }

        [Fact]
        public void Windows_idle_time_is_active_percent_and_queue_average_stays_unavailable()
        {
            var previous = new BlockDeviceCounters(
                0, 0, 0, 0, 0, 0, true,
                IdleTimeAvailable: true,
                IdleMicroseconds: 1_000,
                QueryTimeMicroseconds: 5_000,
                CurrentQueueAvailable: true,
                CurrentQueueDepth: 0,
                OperationCountsAre32Bit: true);
            var current = previous with
            {
                IdleMicroseconds = 1_000 + 250_000,
                QueryTimeMicroseconds = 5_000 + 1_000_000,
                CurrentQueueDepth = 3,
            };
            var sample = BlockDeviceRates.Compute(previous, current, TimeSpan.FromSeconds(1));
            Assert.Equal(75, sample.ActiveTimePercent);
            Assert.Null(sample.AverageQueueDepth);
            Assert.Equal(0, sample.ReadBytesPerSecond);
            Assert.Equal(0, sample.ReadIops);
            Assert.Equal(3, sample.CurrentQueueDepth);
            Assert.Equal("none", sample.CounterResets);
        }

        [Fact]
        public void Active_percent_outside_zero_to_one_hundred_is_not_clamped()
        {
            var linuxPrevious = new BlockDeviceCounters(0, 0, 0, 0, 0, 0, true, DeviceBusyTimeAvailable: true);
            var linuxCurrent = linuxPrevious with { DeviceBusyMicroseconds = 2_000_000 };
            var linux = BlockDeviceRates.Compute(linuxPrevious, linuxCurrent, TimeSpan.FromSeconds(1));
            Assert.Equal(200, linux.ActiveTimePercent);

            var windowsPrevious = new BlockDeviceCounters(0, 0, 0, 0, 0, 0, true, IdleTimeAvailable: true);
            var windowsCurrent = windowsPrevious with { IdleMicroseconds = 150_000, QueryTimeMicroseconds = 100_000 };
            var windows = BlockDeviceRates.Compute(windowsPrevious, windowsCurrent, TimeSpan.FromSeconds(1));
            Assert.Equal(-50, windows.ActiveTimePercent);
        }

        [Fact]
        public void Read_counter_reset_does_not_hide_the_write_measurement()
        {
            var previous = new BlockDeviceCounters(100, 50, 4, 2, 4_000, 2_000, true);
            var current = new BlockDeviceCounters(10, 80, 4, 4, 4_000, 6_000, true);
            var sample = BlockDeviceRates.Compute(previous, current, TimeSpan.FromSeconds(2));
            Assert.False(sample.Skipped);
            Assert.Null(sample.ReadBytesPerSecond);
            Assert.Equal(15, sample.WriteBytesPerSecond);
            Assert.Equal(0, sample.ReadIops);
            Assert.Equal(1, sample.WriteIops);
            Assert.Equal(1, sample.TotalIops);
            Assert.Null(sample.ReadLatencyMilliseconds);
            Assert.Equal(2, sample.WriteLatencyMilliseconds);
            Assert.Equal("readBytes", sample.CounterResets);
        }

        [Fact]
        public void Windows_operation_wrap_is_not_a_reset_when_bytes_continue()
        {
            var previous = new BlockDeviceCounters(1_000, 0, uint.MaxValue - 1, 0, 0, 0, false, OperationCountsAre32Bit: true);
            var current = new BlockDeviceCounters(1_500, 0, 2, 0, 0, 0, false, OperationCountsAre32Bit: true);
            var sample = BlockDeviceRates.Compute(previous, current, TimeSpan.FromSeconds(1));
            Assert.Equal(500, sample.ReadBytesPerSecond);
            Assert.Equal(4, sample.ReadIops);
            Assert.Equal("none", sample.CounterResets);
        }

        [Fact]
        public void Operation_drop_without_a_high_water_count_is_a_reset()
        {
            var previous = new BlockDeviceCounters(1_000, 0, 100, 0, 0, 0, false, OperationCountsAre32Bit: true);
            var current = new BlockDeviceCounters(1_500, 0, 0, 0, 0, 0, false, OperationCountsAre32Bit: true);
            var sample = BlockDeviceRates.Compute(previous, current, TimeSpan.FromSeconds(1));
            Assert.Null(sample.ReadIops);
            Assert.Equal("readOperations", sample.CounterResets);
        }

        [Fact]
        public void Unavailable_latency_is_not_reported_as_zero()
        {
            var counters = new BlockDeviceCounters(10, 20, 1, 1, 0, 0, false);
            var sample = BlockDeviceRates.Compute(counters, counters, TimeSpan.FromSeconds(1));
            Assert.False(sample.Skipped);
            Assert.Equal(0, sample.ReadBytesPerSecond);
            Assert.Equal(0, sample.WriteBytesPerSecond);
            Assert.Null(sample.ReadLatencyMilliseconds);
            Assert.Null(sample.WriteLatencyMilliseconds);
            Assert.Null(sample.ActiveTimePercent);
            Assert.Null(sample.AverageQueueDepth);
            Assert.Null(sample.CurrentQueueDepth);
            Assert.Equal("none", sample.CounterResets);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void Invalid_interval_skips_the_sample(int milliseconds)
        {
            var counters = new BlockDeviceCounters(1, 1, 1, 1, 1, 1, true);
            var sample = BlockDeviceRates.Compute(counters, counters, TimeSpan.FromMilliseconds(milliseconds));
            Assert.True(sample.Skipped);
            Assert.Equal("invalid interval", sample.SkipReason);
            Assert.Null(sample.ReadBytesPerSecond);
            Assert.Null(sample.ReadIops);
        }

        [Fact]
        public void Linux_diskstats_uses_512_byte_sectors_and_service_time()
        {
            const string text = "   8       0 sda 10 0 20 40 5 0 8 10 0 0 0 0 0 0\n";
            Assert.True(LinuxDiskstats.TryParse(text, "sda", out var counters, out var error));
            Assert.Null(error);
            Assert.Equal(20ul * 512, counters.ReadBytes);
            Assert.Equal(8ul * 512, counters.WriteBytes);
            Assert.Equal(10ul, counters.ReadOperations);
            Assert.Equal(5ul, counters.WriteOperations);
            Assert.Equal(40_000ul, counters.ReadBusyMicroseconds);
            Assert.Equal(10_000ul, counters.WriteBusyMicroseconds);
            Assert.True(counters.LatencyAvailable);
            Assert.True(counters.DeviceBusyTimeAvailable);
            Assert.Equal(0ul, counters.DeviceBusyMicroseconds);
            Assert.True(counters.CurrentQueueAvailable);
            Assert.Equal(0ul, counters.CurrentQueueDepth);
            Assert.False(LinuxDiskstats.TryParse(text, "sdb", out _, out var missing));
            Assert.Contains("sdb", missing, StringComparison.Ordinal);
        }

        [Fact]
        public void Linux_diskstats_sums_nvme_namespaces_without_partitions_or_active_time()
        {
            const string text =
                "   8       0 sda 10 0 20 40 5 0 8 10 1 100 200 0 0 0\n"
                + "   8       1 sda1 10 0 20 40 5 0 8 10 9 900 900 0 0 0\n"
                + " 259       0 nvme0n1 2 0 4 8 0 0 0 0 1 50 80\n"
                + " 259       1 nvme0n1p1 2 0 4 8 0 0 0 0 9 9 9\n"
                + " 259       2 nvme0n2 3 0 6 12 1 0 2 4 2 70 90\n";
            Assert.True(LinuxDiskstats.TryParse(text, "sda", out var sda, out var sdaError));
            Assert.Null(sdaError);
            Assert.Equal(10ul, sda.ReadOperations);
            Assert.True(sda.DeviceBusyTimeAvailable);
            Assert.Equal(100_000ul, sda.DeviceBusyMicroseconds);
            Assert.Equal(1ul, sda.CurrentQueueDepth);
            Assert.True(LinuxDiskstats.TryParse(text, "nvme0", out var nvme, out var nvmeError));
            Assert.Null(nvmeError);
            Assert.Equal(5ul, nvme.ReadOperations);
            Assert.Equal(10ul * 512, nvme.ReadBytes);
            Assert.Equal(1ul, nvme.WriteOperations);
            Assert.Equal(2ul * 512, nvme.WriteBytes);
            Assert.Equal(3ul, nvme.CurrentQueueDepth);
            Assert.Equal(170_000ul, nvme.WeightedQueueMicroseconds);
            Assert.False(nvme.DeviceBusyTimeAvailable);
        }

        [Fact]
        public void Linux_diskstats_without_extended_fields_keeps_throughput()
        {
            const string text = "   8       0 sda 10 0 20 40 5 0 8 10\n";
            Assert.True(LinuxDiskstats.TryParse(text, "sda", out var counters, out var error));
            Assert.Null(error);
            Assert.Equal(20ul * 512, counters.ReadBytes);
            Assert.True(counters.LatencyAvailable);
            Assert.False(counters.DeviceBusyTimeAvailable);
            Assert.False(counters.WeightedQueueAvailable);
            Assert.False(counters.CurrentQueueAvailable);
        }

        [Fact]
        public async Task Monitor_samples_once_per_interval_and_associates_the_pool()
        {
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:sda", ["/mnt/a"])]);
            var source = new IncrementingCounters();
            var logger = new ListLogger<BlockDeviceMonitorService>();
            var monitor = new BlockDeviceMonitorService(catalog, source, logger, TimeSpan.FromMilliseconds(20), TimeProvider.System);
            await monitor.StartAsync(CancellationToken.None);
            try
            {
                await WaitAsync(() => monitor.CompletedSamples >= 1);
                Assert.Equal("block:sda", monitor.Latest!.Value.DeviceId);
                Assert.Contains("/mnt/a", monitor.Latest.Value.PoolPaths);
                Assert.True(monitor.Latest.Value.ReadBytesPerSecond is > 0);
                Assert.NotNull(monitor.Latest.Value.ReadLatencyMilliseconds);
                Assert.NotNull(monitor.Latest.Value.ReadIops);
                Assert.NotNull(monitor.Latest.Value.ActiveTimePercent);
                Assert.Equal(0, monitor.Latest.Value.CurrentQueueDepth);
                var text = logger.Text;
                Assert.Contains("readIops=", text, StringComparison.Ordinal);
                Assert.Contains("activeTimePercent=", text, StringComparison.Ordinal);
                Assert.Contains("currentQueueDepth=0", text, StringComparison.Ordinal);
                Assert.Contains("counterResets=none", text, StringComparison.Ordinal);
            }
            finally
            {
                await monitor.StopAsync(CancellationToken.None);
            }

            Assert.Equal(TaskStatus.RanToCompletion, monitor.Execution!.Status);
        }

        [Fact]
        public async Task Disappeared_device_is_reported_and_can_recover()
        {
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:sda", ["/mnt/a"])]);
            var source = new IncrementingCounters();
            var monitor = Monitor(catalog, source, TimeSpan.FromMilliseconds(20));
            await monitor.StartAsync(CancellationToken.None);
            try
            {
                await WaitAsync(() => monitor.CompletedSamples >= 1);
                catalog.Publish([]);
                await WaitAsync(() => monitor.CollectionFailures >= 1);
                catalog.Publish([new SegmentPoolDevice("block:sda", ["/mnt/a"])]);
                await WaitAsync(() => monitor.CompletedSamples >= 2);
            }
            finally
            {
                await monitor.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Transient_counter_errors_do_not_flood_and_do_not_fault_the_monitor()
        {
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:sda", ["/mnt/a"])]);
            var logger = new ListLogger<BlockDeviceMonitorService>();
            var monitor = new BlockDeviceMonitorService(
                catalog,
                new FailingCounters(),
                logger,
                TimeSpan.FromMilliseconds(5),
                TimeProvider.System);
            await monitor.StartAsync(CancellationToken.None);
            try
            {
                await WaitAsync(() => monitor.CollectionFailures >= 10);
                Assert.Null(monitor.Latest);
                Assert.Equal(1, logger.Count(LogLevel.Warning));
            }
            finally
            {
                await monitor.StopAsync(CancellationToken.None);
            }

            Assert.False(monitor.Execution!.IsFaulted);
        }

        [Fact]
        public async Task Start_returns_while_a_counter_read_is_blocked()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:sda", ["/mnt/a"])]);
            var monitor = Monitor(catalog, new BlockingCounters(entered, release), TimeSpan.FromSeconds(1));
            var started = monitor.StartAsync(CancellationToken.None);
            var completed = await Task.WhenAny(started, Task.Delay(TimeSpan.FromMilliseconds(500)));
            Assert.Same(started, completed);
            var ingestion = Task.Run(() => 1);
            Assert.Equal(1, await ingestion.WaitAsync(TimeSpan.FromSeconds(1)));
            release.TrySetResult();
            await monitor.StopAsync(CancellationToken.None);
        }

        [Fact]
        public void Unknown_device_is_not_a_zero_sample()
        {
            var source = new PlatformBlockDeviceCounterSource(static () => "   8       0 sda 1 0 1 1 1 0 1 1 0 0 0 0 0 0\n");
            using (source)
            {
                var read = source.TryRead("disk:99999", out var counters, out var error);
                Assert.False(read);
                Assert.False(string.IsNullOrWhiteSpace(error));
                Assert.Equal(default, counters);
            }
        }

        [Fact]
        public async Task Failed_read_reestablishes_a_baseline_instead_of_spanning_the_gap()
        {
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:sda", ["/mnt/a"])]);
            var source = new PhasedCounters();
            var monitor = Monitor(catalog, source, TimeSpan.FromMilliseconds(20));
            await monitor.StartAsync(CancellationToken.None);
            try
            {
                await WaitAsync(() => monitor.CompletedSamples >= 1);
                var seconds = monitor.Latest!.Value.IntervalMilliseconds / 1000d;
                Assert.True(seconds > 0);
                var rate = monitor.Latest!.Value.ReadBytesPerSecond;
                Assert.NotNull(rate);
                Assert.InRange(rate.Value, (long)(50 / seconds), (long)(200 / seconds));
            }
            finally
            {
                await monitor.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Replaced_device_does_not_inherit_the_previous_baseline()
        {
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:sda", ["/mnt/a"])]);
            var source = new SwitchingCounters();
            var monitor = Monitor(catalog, source, TimeSpan.FromMilliseconds(20));
            await monitor.StartAsync(CancellationToken.None);
            try
            {
                await WaitAsync(() => monitor.CompletedSamples >= 1 && monitor.Latest!.Value.DeviceId == "block:sda");
                catalog.Publish([new SegmentPoolDevice("block:sdb", ["/mnt/b"])]);
                await WaitAsync(() => monitor.Latest is { DeviceId: "block:sdb" });
                var seconds = monitor.Latest!.Value.IntervalMilliseconds / 1000d;
                var rate = monitor.Latest!.Value.ReadBytesPerSecond;
                Assert.NotNull(rate);
                Assert.InRange(rate.Value, (long)(20 / seconds), (long)(50 / seconds));
            }
            finally
            {
                await monitor.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public void Windows_reader_reuses_a_handle_and_dispose_stops_reads()
        {
            var opens = 0;
            var failQuery = true;
            using var reader = new WindowsDiskPerformanceReader(
                disk =>
                {
                    opens++;
                    return (new SafeFileHandle(new IntPtr(1), ownsHandle: false), @"\\.\PhysicalDrive" + disk.ToString(CultureInfo.InvariantCulture), 0);
                },
                (handle, path) =>
                {
                    _ = handle;
                    _ = path;
                    if (failQuery)
                    {
                        failQuery = false;
                        return (false, default, "query failed");
                    }

                    return (true, new WindowsStorageNative.DiskPerformanceSnapshot(10, 20, 30, 40, 50, 1, 2, 3, 60), null);
                });
            Assert.False(reader.TryRead("disk:1", out _, out var firstError));
            Assert.Equal("query failed", firstError);
            Assert.True(reader.TryRead("disk:1", out var counters, out var error), error);
            Assert.True(reader.TryRead("disk:1", out _, out error), error);
            Assert.Equal(2, opens);
            Assert.Equal(10ul, counters.ReadBytes);
            Assert.Equal(3ul, counters.ReadBusyMicroseconds);
            Assert.Equal(5ul, counters.IdleMicroseconds);
            Assert.Equal(6ul, counters.QueryTimeMicroseconds);
            Assert.Equal(3ul, counters.CurrentQueueDepth);
            Assert.True(counters.OperationCountsAre32Bit);
            Assert.False(counters.WeightedQueueAvailable);
            reader.Dispose();
            reader.Dispose();
            Assert.False(reader.TryRead("disk:1", out _, out var disposed));
            Assert.Contains("disposed", disposed, StringComparison.Ordinal);
        }

        [Fact]
        public void Windows_reader_replaces_an_invalid_cached_handle_without_null_dereference()
        {
            var opens = 0;
            using var reader = new WindowsDiskPerformanceReader(
                disk =>
                {
                    opens++;
                    // First open returns an immediately invalid handle; second open succeeds.
                    if (opens == 1)
                    {
                        return (new SafeFileHandle(IntPtr.Zero, ownsHandle: true), @"\\.\PhysicalDrive" + disk, 5);
                    }

                    return (new SafeFileHandle(new IntPtr(2), ownsHandle: false), @"\\.\PhysicalDrive" + disk, 0);
                },
                static (handle, path) =>
                {
                    _ = handle;
                    _ = path;
                    return (true, new WindowsStorageNative.DiskPerformanceSnapshot(1, 2, 3, 4, 5, 1, 1, 1, 10), null);
                });

            Assert.False(reader.TryRead("disk:7", out _, out var openError));
            Assert.Contains("CreateFileW", openError, StringComparison.Ordinal);
            Assert.True(reader.TryRead("disk:7", out var counters, out var error), error);
            Assert.Equal(2, opens);
            Assert.Equal(1ul, counters.ReadBytes);
        }

        [Fact]
        public void Negative_windows_performance_counters_are_not_zeros()
        {
            var snapshot = new WindowsStorageNative.DiskPerformanceSnapshot(-1, 0, 0, 0, 0, 0, 0, 0, 0);
            Assert.False(WindowsDiskPerformanceReader.TryMap(snapshot, out var counters, out var error));
            Assert.Equal(default, counters);
            Assert.Contains("negative", error, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Windows_physical_drive_performance_ioctl_scales_query_time()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var reader = new WindowsDiskPerformanceReader();
            Assert.True(reader.TryRead("disk:0", out var first, out var error), error);
            Assert.True(first.LatencyAvailable);
            Assert.True(first.IdleTimeAvailable);
            Assert.True(first.CurrentQueueAvailable);
            Assert.False(first.WeightedQueueAvailable);
            await Task.Delay(200);
            Assert.True(reader.TryRead("disk:0", out var second, out error), error);
            Assert.Equal(1, reader.OpenCount);
            var queryDelta = second.QueryTimeMicroseconds - first.QueryTimeMicroseconds;
            Assert.InRange(queryDelta, 50_000ul, 2_000_000ul);
            var sample = BlockDeviceRates.Compute(first, second, TimeSpan.FromMilliseconds(queryDelta / 1000d));
            Assert.False(sample.Skipped);
            Assert.NotNull(sample.ReadBytesPerSecond);
            Assert.NotNull(sample.WriteBytesPerSecond);
            Assert.NotNull(sample.ReadIops);
            Assert.NotNull(sample.WriteIops);
            Assert.NotNull(sample.ActiveTimePercent);
            Assert.Null(sample.AverageQueueDepth);
            Assert.NotNull(sample.CurrentQueueDepth);
            reader.Dispose();
            Assert.False(reader.TryRead("disk:0", out _, out var disposed));
            Assert.Contains("disposed", disposed, StringComparison.Ordinal);
        }

        [Fact]
        public void Linux_host_diskstats_bytes_match_512_byte_sectors()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            var text = File.ReadAllText("/proc/diskstats");
            string? identity = null;
            foreach (var line in text.Split('\n'))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 11 && LinuxBlockDeviceIdentity.IsAcceptedPhysicalName(fields[2]))
                {
                    identity = LinuxBlockDeviceIdentity.PhysicalIdentityName(fields[2]);
                    break;
                }
            }

            Assert.False(string.IsNullOrEmpty(identity));
            Assert.True(LinuxDiskstats.TryParse(text, identity!, out var counters, out var error), error);
            ulong sectors = 0;
            var rows = 0;
            foreach (var line in text.Split('\n'))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 11 || !ulong.TryParse(fields[5], out var readSectors))
                {
                    continue;
                }

                var wholeDisk = string.Equals(fields[2], identity, StringComparison.Ordinal);
                var nvmeNamespace = LinuxBlockDeviceIdentity.TryNvmeController(fields[2], out var controller)
                    && string.Equals(controller, identity, StringComparison.Ordinal);
                if (!wholeDisk && !nvmeNamespace)
                {
                    continue;
                }

                sectors += readSectors;
                rows++;
            }

            Assert.True(rows > 0);
            Assert.Equal(sectors * 512, counters.ReadBytes);
            Assert.True(counters.LatencyAvailable);
        }

        private static BlockDeviceCounters SampleCounters(ulong readBytes) =>
            new(readBytes, 0, 1, 0, 1_000, 0, true);

        private sealed class PhasedCounters : IBlockDeviceCounterSource
        {
            private int _step;

            public bool TryRead(string deviceId, out BlockDeviceCounters counters, out string? error)
            {
                var step = Interlocked.Increment(ref _step);
                error = null;
                if (step == 2)
                {
                    counters = default;
                    error = "temporary";
                    return false;
                }

                if (step < 2)
                {
                    counters = SampleCounters(100);
                    return true;
                }

                counters = SampleCounters(10_000ul + (ulong)(step - 3) * 100ul);
                return true;
            }
        }

        private sealed class SwitchingCounters : IBlockDeviceCounterSource
        {
            private readonly Dictionary<string, ulong> _bytes = new(StringComparer.Ordinal);

            public bool TryRead(string deviceId, out BlockDeviceCounters counters, out string? error)
            {
                _bytes.TryGetValue(deviceId, out var bytes);
                bytes += string.Equals(deviceId, "block:sdb", StringComparison.Ordinal) ? 30ul : 1_000_000ul;
                _bytes[deviceId] = bytes;
                counters = SampleCounters(bytes);
                error = null;
                return true;
            }
        }

        private static BlockDeviceMonitorService Monitor(SegmentPoolCatalog catalog, IBlockDeviceCounterSource source, TimeSpan interval) =>
            new(catalog, source, new ListLogger<BlockDeviceMonitorService>(), interval, TimeProvider.System);

        private static async Task WaitAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("condition was not met");
                }

                await Task.Delay(5);
            }
        }

        private sealed class IncrementingCounters : IBlockDeviceCounterSource
        {
            private ulong _bytes = 1000;
            private ulong _ops = 1;
            private ulong _busy = 1000;
            private ulong _active;
            private ulong _weighted;

            public bool TryRead(string deviceId, out BlockDeviceCounters counters, out string? error)
            {
                _bytes += 1000;
                _ops += 1;
                _busy += 1000;
                _active += 10_000;
                _weighted += 20_000;
                counters = new BlockDeviceCounters(
                    _bytes,
                    _bytes,
                    _ops,
                    _ops,
                    _busy,
                    _busy,
                    true,
                    DeviceBusyTimeAvailable: true,
                    DeviceBusyMicroseconds: _active,
                    WeightedQueueAvailable: true,
                    WeightedQueueMicroseconds: _weighted,
                    CurrentQueueAvailable: true,
                    CurrentQueueDepth: 0);
                error = null;
                return true;
            }
        }

        private sealed class FailingCounters : IBlockDeviceCounterSource
        {
            public bool TryRead(string deviceId, out BlockDeviceCounters counters, out string? error)
            {
                counters = default;
                error = "temporary";
                return false;
            }
        }

        private sealed class BlockingCounters : IBlockDeviceCounterSource
        {
            private readonly TaskCompletionSource _entered;
            private readonly TaskCompletionSource _release;

            public BlockingCounters(TaskCompletionSource entered, TaskCompletionSource release)
            {
                _entered = entered;
                _release = release;
            }

            public bool TryRead(string deviceId, out BlockDeviceCounters counters, out string? error)
            {
                _entered.TrySetResult();
                _release.Task.Wait(TimeSpan.FromSeconds(2));
                counters = default;
                error = "blocked";
                return false;
            }
        }

        private sealed class ListLogger<T> : ILogger<T>
        {
            private readonly List<LogLevel> _levels = [];

            private readonly List<string> _messages = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_levels)
                {
                    _levels.Add(logLevel);
                    _messages.Add(formatter(state, exception));
                }
            }

            public string Text
            {
                get
                {
                    lock (_levels)
                    {
                        return string.Join('\n', _messages);
                    }
                }
            }

            public int Count(LogLevel level)
            {
                lock (_levels)
                {
                    return _levels.Count(item => item == level);
                }
            }
        }
    }
}
