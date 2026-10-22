using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Tests.Fixtures;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    public sealed class SegmentPoolValidationTests
    {
        [Fact]
        public async Task Missing_directory_fails_inspection()
        {
            var missing = Path.Join(Path.GetTempPath(), "vectornntp-missing-" + Guid.NewGuid().ToString("N"));
            var inspection = Inspector().Inspect(missing, AppContext.BaseDirectory, CancellationToken.None);
            Assert.NotNull(inspection.Failure);
            Assert.Contains("does not exist", inspection.Failure, StringComparison.Ordinal);
            await Task.CompletedTask;
        }

        [Fact]
        public void File_is_not_a_directory()
        {
            var file = Path.Join(Path.GetTempPath(), "vectornntp-pool-file-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(file, "x");
            try
            {
                var inspection = Inspector().Inspect(file, AppContext.BaseDirectory, CancellationToken.None);
                Assert.NotNull(inspection.Failure);
                Assert.Contains("not a directory", inspection.Failure, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void Ordinary_directory_is_not_a_dedicated_mount()
        {
            var directory = Directory.CreateTempSubdirectory("vectornntp-pool-");
            try
            {
                var inspection = Inspector().Inspect(directory.FullName, AppContext.BaseDirectory, CancellationToken.None);
                Assert.NotNull(inspection.Failure);
                Assert.Contains("dedicated mount", inspection.Failure, StringComparison.Ordinal);
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }

        [Fact]
        public void Directory_under_the_root_mount_is_not_dedicated()
        {
            const string mountInfo = "22 1 8:1 / / rw - ext4 /dev/sda1 rw\n";
            var mount = LinuxMountTable.FindLongestMount(mountInfo, "/var/spool/cache");
            Assert.NotNull(mount);
            Assert.Equal("/", mount.Value.MountPoint);
            Assert.NotEqual("/var/spool/cache", mount.Value.MountPoint);
        }

        [Fact]
        public void Mountinfo_decodes_escaped_spaces_and_picks_the_longest_mount()
        {
            const string mountInfo =
                "22 1 8:1 / / rw - ext4 /dev/sda1 rw\n"
                + "50 22 8:16 / /mnt/pool\\040a rw - xfs /dev/sdb rw\n";
            var mount = LinuxMountTable.FindLongestMount(mountInfo, "/mnt/pool a");
            Assert.Equal("/mnt/pool a", mount!.Value.MountPoint);
            Assert.Equal("8:16", mount.Value.MajorMinor);
            Assert.Equal("xfs", mount.Value.FsType);
        }

        [Fact]
        public void Non_block_filesystems_fail_closed()
        {
            Assert.True(LinuxMountTable.IsNonBlockFilesystem("nfs"));
            Assert.True(LinuxMountTable.IsNonBlockFilesystem("overlay"));
            Assert.True(LinuxMountTable.IsNonBlockFilesystem("tmpfs"));
            Assert.False(LinuxMountTable.IsNonBlockFilesystem("xfs"));
            Assert.False(LinuxMountTable.IsNonBlockFilesystem("ext4"));
        }

        [Fact]
        public void Partition_collapses_to_the_parent_disk()
        {
            var sysfs = new MapSysfs();
            sysfs.AddDisk("8:0", "sda");
            sysfs.AddPartition("8:1", "sda1", "8:0");
            var identity = LinuxBlockDeviceIdentity.Resolve("8:1", sysfs);
            Assert.Null(identity.Failure);
            Assert.Equal(["block:sda"], identity.DeviceIds);
        }

        [Fact]
        public void Device_mapper_slaves_resolve_and_a_slave_less_mapper_fails()
        {
            var sysfs = new MapSysfs();
            sysfs.AddDisk("8:0", "sda");
            sysfs.AddDisk("8:16", "sdb");
            sysfs.AddLayer("253:0", "dm-0", ["8:0", "8:16"]);
            var resolved = LinuxBlockDeviceIdentity.Resolve("253:0", sysfs);
            Assert.Null(resolved.Failure);
            Assert.Equal(["block:sda", "block:sdb"], resolved.DeviceIds);

            var empty = new MapSysfs();
            empty.AddLayer("253:1", "dm-1", []);
            var unresolved = LinuxBlockDeviceIdentity.Resolve("253:1", empty);
            Assert.NotNull(unresolved.Failure);
            Assert.Contains("could not be established", unresolved.Failure, StringComparison.Ordinal);
            Assert.Empty(unresolved.DeviceIds);
        }

        [Theory]
        [InlineData("loop0")]
        [InlineData("vd0")]
        [InlineData("md0")]
        public void Virtual_names_fail_closed(string name)
        {
            var sysfs = new MapSysfs();
            sysfs.AddDisk("7:0", name);
            var identity = LinuxBlockDeviceIdentity.Resolve("7:0", sysfs);
            Assert.NotNull(identity.Failure);
            Assert.Contains(name, identity.Failure, StringComparison.Ordinal);
        }

        [Fact]
        public void Write_probe_round_trips_and_deletes_the_file()
        {
            var directory = Directory.CreateTempSubdirectory("vectornntp-probe-");
            try
            {
                var failure = new FileSegmentPoolWriteProbe().Probe(directory.FullName, CancellationToken.None);
                Assert.Null(failure);
                Assert.Empty(Directory.GetFiles(directory.FullName, ".vectornntp-pool-probe-*"));
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }

        [Fact]
        public void Write_probe_cleanup_failure_is_reported()
        {
            var directory = Directory.CreateTempSubdirectory("vectornntp-probe-");
            try
            {
                var probe = new FileSegmentPoolWriteProbe(static _ => throw new IOException("locked"));
                var failure = probe.Probe(directory.FullName, CancellationToken.None);
                Assert.NotNull(failure);
                Assert.Contains("probe cleanup failed", failure, StringComparison.Ordinal);
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }

        [Fact]
        public async Task Validation_failure_does_not_start_the_listener_or_advertisement()
        {
            var options = StorageServerTestOptions.CreateValid();
            options.SegmentPools.Directories = ["spool/cache"];
            var inspector = new MapInspector();
            inspector.Results["spool/cache"] = Failure("spool/cache", "does not exist.");
            var validation = new SegmentPoolValidationService(
                Options.Create(options),
                inspector,
                new SegmentPoolCatalog(),
                NullLogger<SegmentPoolValidationService>.Instance);
            var earlier = new SpyService("earlier");
            var listener = new SpyService("listener");
            var publisher = new SpyService("publisher");
            var manager = new ApplicationServiceManager(
                [earlier, validation, listener, publisher],
                options,
                NullLogger<ApplicationServiceManager>.Instance);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));
            Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, earlier.Starts);
            Assert.Equal(1, earlier.Stops);
            Assert.Equal(0, listener.Starts);
            Assert.Equal(0, publisher.Starts);
        }

        [Fact]
        public async Task Cancellation_during_validation_does_not_start_later_services()
        {
            using var cancellation = new CancellationTokenSource();
            var options = StorageServerTestOptions.CreateValid();
            options.SegmentPools.Directories = ["spool/cache"];
            var catalog = new SegmentPoolCatalog();
            var validation = new SegmentPoolValidationService(
                Options.Create(options),
                new CancelOnInspect(cancellation),
                catalog,
                NullLogger<SegmentPoolValidationService>.Instance);
            var earlier = new SpyService("earlier");
            var listener = new SpyService("listener");
            var publisher = new SpyService("publisher");
            var manager = new ApplicationServiceManager(
                [earlier, validation, listener, publisher],
                options,
                NullLogger<ApplicationServiceManager>.Instance);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StartAsync(cancellation.Token));
            Assert.Equal(1, earlier.Starts);
            Assert.Equal(1, earlier.Stops);
            Assert.Equal(0, listener.Starts);
            Assert.Equal(0, publisher.Starts);
            Assert.Empty(catalog.Devices);
        }

        [Fact]
        public async Task Inaccessible_and_unwritable_results_fail_startup()
        {
            await AssertFailureAsync("is not accessible: UnauthorizedAccessException.");
            await AssertFailureAsync("write/read probe failed: IOException: denied.");
            await AssertFailureAsync("probe cleanup failed: IOException: locked.");
            await AssertFailureAsync("physical device identity could not be established: sysfs has no block device 8:0.");
        }

        [Fact]
        public async Task Distinct_physical_devices_pass_and_are_published()
        {
            var options = StorageServerTestOptions.CreateValid();
            options.SegmentPools.Directories = ["a", "b"];
            var inspector = new MapInspector();
            inspector.Results["a"] = Success("a", "/mnt/a", ["disk:1"]);
            inspector.Results["b"] = Success("b", "/mnt/b", ["disk:2"]);
            var catalog = new SegmentPoolCatalog();
            var validation = Service(options, inspector, catalog);
            await validation.StartAsync(CancellationToken.None);
            Assert.Equal(2, catalog.Devices.Count);
            Assert.Contains(catalog.Devices, static device => device.DeviceId == "disk:1" && device.PoolPaths.Contains("/mnt/a"));
            Assert.Contains(catalog.Devices, static device => device.DeviceId == "disk:2" && device.PoolPaths.Contains("/mnt/b"));
        }

        [Fact]
        public async Task Same_disk_different_mounts_are_rejected()
        {
            var error = await OverlapAsync(["disk:1"], ["disk:1"]);
            Assert.Contains("share physical device", error, StringComparison.Ordinal);
            Assert.Contains("disk:1", error, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Partitions_of_the_same_disk_are_rejected()
        {
            var error = await OverlapAsync(["block:sda"], ["block:sda"]);
            Assert.Contains("share physical device", error, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Overlapping_device_sets_are_rejected()
        {
            var error = await OverlapAsync(["disk:1", "disk:2"], ["disk:2", "disk:3"]);
            Assert.Contains("disk:2", error, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(6)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(14)]
        [InlineData(15)]
        [InlineData(16)]
        public void Unsupported_windows_bus_types_fail_closed(byte bus)
        {
            Assert.False(WindowsStorageNative.IsAcceptedBus(bus));
            Assert.NotNull(WindowsStorageNative.RejectedBusMessage(1, bus));
        }

        [Fact]
        public void Fibre_channel_luns_are_not_treated_as_independent_physical_disks()
        {
            var message = WindowsStorageNative.RejectedBusMessage(3, 6);
            Assert.NotNull(message);
            Assert.Contains("Fibre Channel", message, StringComparison.Ordinal);
            Assert.Contains("not proven", message, StringComparison.Ordinal);
        }

        [Fact]
        public void Access_denied_names_the_volume_operation_and_is_not_success()
        {
            var message = WindowsStorageNative.FormatWin32Failure(
                "CreateFileW",
                @"\\.\Volume{test}",
                5,
                "IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS");
            Assert.Contains("Win32 error 5", message, StringComparison.Ordinal);
            Assert.Contains("ERROR_ACCESS_DENIED", message, StringComparison.Ordinal);
            Assert.Contains(@"\\.\Volume{test}", message, StringComparison.Ordinal);
            Assert.Contains("FILE_ANY_ACCESS", message, StringComparison.Ordinal);

            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var directory = Directory.CreateTempSubdirectory("vectornntp-denied-");
            try
            {
                var inspector = new PlatformSegmentPoolInspector(
                    new FileSegmentPoolWriteProbe(),
                    new LinuxSysfsReader(),
                    new ScriptedWindows(message));
                var inspection = inspector.Inspect(directory.FullName, null, CancellationToken.None);
                Assert.False(inspection.Succeeded);
                Assert.Empty(inspection.PhysicalDeviceIds);
                Assert.Contains("Win32 error 5", inspection.Failure, StringComparison.Ordinal);
                Assert.Contains(@"\\.\Volume{test}", inspection.Failure, StringComparison.Ordinal);
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }

        [Fact]
        public void Real_inspector_does_not_fail_with_win32_access_denied()
        {
            var directory = Directory.CreateTempSubdirectory("vectornntp-real-");
            try
            {
                var inspection = Inspector().Inspect(directory.FullName, AppContext.BaseDirectory, CancellationToken.None);
                Assert.NotNull(inspection.Failure);
                Assert.DoesNotContain("Win32 error 5", inspection.Failure, StringComparison.Ordinal);
                Assert.Contains("dedicated mount", inspection.Failure, StringComparison.Ordinal);
            }
            finally
            {
                directory.Delete(recursive: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var mounted = DebugSpoolCache();
            if (!Directory.Exists(mounted))
            {
                return;
            }

            var live = Inspector().Inspect(mounted, null, CancellationToken.None);
            Assert.DoesNotContain("Win32 error 5", live.Failure ?? string.Empty, StringComparison.Ordinal);
        }

        [Fact]
        public void Nvme_namespaces_on_one_controller_share_one_device()
        {
            var sysfs = new MapSysfs();
            sysfs.AddDisk("259:0", "nvme0n1");
            sysfs.AddDisk("259:1", "nvme0n2");
            sysfs.AddDisk("259:2", "nvme1n1");
            Assert.Equal(["block:nvme0"], LinuxBlockDeviceIdentity.Resolve("259:0", sysfs).DeviceIds);
            Assert.Equal(["block:nvme0"], LinuxBlockDeviceIdentity.Resolve("259:1", sysfs).DeviceIds);
            Assert.Equal(["block:nvme1"], LinuxBlockDeviceIdentity.Resolve("259:2", sysfs).DeviceIds);
        }

        [Fact]
        public void Nested_mount_does_not_use_the_parent_filesystem()
        {
            const string mountInfo =
                "22 1 8:1 / / rw - ext4 /dev/sda1 rw\n"
                + "60 22 8:16 / /mnt/pool rw - xfs /dev/sdb rw\n";
            var mount = LinuxMountTable.FindLongestMount(mountInfo, "/mnt/pool/cache");
            Assert.Equal("/mnt/pool", mount!.Value.MountPoint);
            Assert.NotEqual("/mnt/pool/cache", mount.Value.MountPoint);
        }

        [Theory]
        [InlineData(11)]
        [InlineData(17)]
        public void Direct_attached_windows_bus_types_are_accepted(byte bus)
        {
            Assert.True(WindowsStorageNative.IsAcceptedBus(bus));
        }

        private static async Task AssertFailureAsync(string failure)
        {
            var options = StorageServerTestOptions.CreateValid();
            options.SegmentPools.Directories = ["pool"];
            var inspector = new MapInspector();
            inspector.Results["pool"] = Failure("pool", failure);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Service(options, inspector, new SegmentPoolCatalog()).StartAsync(CancellationToken.None));
            Assert.Contains(failure, error.Message, StringComparison.Ordinal);
            Assert.Contains("pool", error.Message, StringComparison.Ordinal);
        }

        private static async Task<string> OverlapAsync(string[] left, string[] right)
        {
            var options = StorageServerTestOptions.CreateValid();
            options.SegmentPools.Directories = ["left", "right"];
            var inspector = new MapInspector();
            inspector.Results["left"] = Success("left", "/mnt/left", left);
            inspector.Results["right"] = Success("right", "/mnt/right", right);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Service(options, inspector, new SegmentPoolCatalog()).StartAsync(CancellationToken.None));
            return error.Message;
        }

        private static SegmentPoolValidationService Service(
            StorageServerOptions options,
            ISegmentPoolInspector inspector,
            SegmentPoolCatalog catalog) =>
            new(Options.Create(options), inspector, catalog, NullLogger<SegmentPoolValidationService>.Instance);

        private static PlatformSegmentPoolInspector Inspector() =>
            new(new FileSegmentPoolWriteProbe(), new LinuxSysfsReader());

        private static string DebugSpoolCache()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Join(dir.FullName, "VectorNNTPD.slnx")))
            {
                dir = dir.Parent;
            }

            return Path.Join(
                dir?.FullName ?? AppContext.BaseDirectory,
                "src",
                "VectorNNTPD.StorageService",
                "bin",
                "Debug",
                "net10.0",
                "spool",
                "cache");
        }

        private sealed class ScriptedWindows : IWindowsStorageQuery
        {
            private readonly string _failure;

            public ScriptedWindows(string failure) => _failure = failure;

            public string? GetVolumeMountPath(string path) => path;

            public WindowsStorageNative.VolumeIdentity DescribeVolume(string canonicalPath) =>
                new(canonicalPath, [], _failure);

            public bool TryGetFinalDirectory(string path, out string finalPath, out string? error)
            {
                finalPath = path;
                error = null;
                return true;
            }
        }

        private static SegmentPoolInspection Success(string configured, string canonical, string[] devices) =>
            new(configured, canonical, canonical, devices, null);

        private static SegmentPoolInspection Failure(string configured, string failure) =>
            new(configured, configured, null, [], failure);

        private sealed class CancelOnInspect : ISegmentPoolInspector
        {
            private readonly CancellationTokenSource _cancellation;

            public CancelOnInspect(CancellationTokenSource cancellation) => _cancellation = cancellation;

            public SegmentPoolInspection Inspect(string configuredPath, string? applicationBaseDirectory, CancellationToken cancellationToken)
            {
                _ = configuredPath;
                _ = applicationBaseDirectory;
                _cancellation.Cancel();
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private sealed class MapInspector : ISegmentPoolInspector
        {
            internal Dictionary<string, SegmentPoolInspection> Results { get; } = new(StringComparer.Ordinal);

            public SegmentPoolInspection Inspect(string configuredPath, string? applicationBaseDirectory, CancellationToken cancellationToken) =>
                Results[configuredPath];
        }

        private sealed class SpyService : IApplicationService
        {
            public SpyService(string name) => Name = name;

            public string Name { get; }

            public int Starts { get; private set; }

            public int Stops { get; private set; }

            public Task? Execution => null;

            public Task StartAsync(CancellationToken cancellationToken)
            {
                Starts++;
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken)
            {
                Stops++;
                return Task.CompletedTask;
            }
        }

        private sealed class MapSysfs : ILinuxSysfs
        {
            private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _parents = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string[]> _slaves = new(StringComparer.Ordinal);
            private readonly HashSet<string> _partitions = new(StringComparer.Ordinal);

            public void AddDisk(string majorMinor, string name) => _names[majorMinor] = name;

            public void AddPartition(string majorMinor, string name, string parent)
            {
                _names[majorMinor] = name;
                _parents[majorMinor] = parent;
                _partitions.Add(majorMinor);
            }

            public void AddLayer(string majorMinor, string name, string[] slaves)
            {
                _names[majorMinor] = name;
                _slaves[majorMinor] = slaves;
            }

            public bool Exists(string majorMinor) => _names.ContainsKey(majorMinor);

            public bool HasPartition(string majorMinor) => _partitions.Contains(majorMinor);

            public string? ParentMajorMinor(string majorMinor) =>
                _parents.TryGetValue(majorMinor, out var parent) ? parent : null;

            public IReadOnlyList<string> SlaveMajorMinors(string majorMinor) =>
                _slaves.TryGetValue(majorMinor, out var slaves) ? slaves : [];

            public string DeviceName(string majorMinor) => _names.TryGetValue(majorMinor, out var name) ? name : string.Empty;
        }
    }
}
