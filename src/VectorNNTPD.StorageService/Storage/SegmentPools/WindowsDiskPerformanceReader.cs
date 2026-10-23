using Microsoft.Win32.SafeHandles;

namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Reads <c>IOCTL_DISK_PERFORMANCE</c> and keeps one handle per physical disk.
    /// Desired access stays 0. Handles are disposed on <see cref="Dispose"/> and when a query fails.
    /// </summary>
    internal sealed class WindowsDiskPerformanceReader : IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<uint, HeldDisk> _handles = [];
        private readonly Func<uint, (SafeFileHandle Handle, string DevicePath, int Win32Error)> _open;
        private readonly Func<SafeFileHandle, string, (bool Ok, WindowsStorageNative.DiskPerformanceSnapshot Snapshot, string? Error)> _query;
        private bool _disposed;

        /// <summary>Opens physical drives with <c>CreateFileW</c> and queries them with <c>IOCTL_DISK_PERFORMANCE</c>.</summary>
        public WindowsDiskPerformanceReader()
            : this(OpenPhysicalDrive, QueryPhysicalDrive)
        {
        }

        /// <summary>Uses the supplied open and query delegates so tests can avoid a real disk.</summary>
        /// <param name="open">Opens <c>\\.\PhysicalDriveN</c>. The reader owns the handle.</param>
        /// <param name="query">Reads one snapshot. A false result drops the cached handle.</param>
        internal WindowsDiskPerformanceReader(
            Func<uint, (SafeFileHandle Handle, string DevicePath, int Win32Error)> open,
            Func<SafeFileHandle, string, (bool Ok, WindowsStorageNative.DiskPerformanceSnapshot Snapshot, string? Error)> query)
        {
            ArgumentNullException.ThrowIfNull(open);
            ArgumentNullException.ThrowIfNull(query);
            _open = open;
            _query = query;
        }

        /// <summary>Number of times a handle was opened. A reused handle does not increment this.</summary>
        internal int OpenCount { get; private set; }

        /// <summary>
        /// Reads cumulative counters for <paramref name="deviceId"/>.
        /// </summary>
        /// <param name="deviceId">Identity <c>disk:N</c>.</param>
        /// <param name="counters">Counters when the method returns <see langword="true"/>.</param>
        /// <param name="error">Diagnostic when the method returns <see langword="false"/>. This is not a zero sample.</param>
        /// <returns><see langword="true"/> when a snapshot was mapped.</returns>
        public bool TryRead(string deviceId, out BlockDeviceCounters counters, out string? error)
        {
            counters = default;
            error = null;
            if (!deviceId.StartsWith("disk:", StringComparison.Ordinal)
                || !uint.TryParse(deviceId.AsSpan(5), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var disk))
            {
                error = "device id '" + deviceId + "' is not a Windows physical disk.";
                return false;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    error = "Windows disk performance reader is disposed.";
                    return false;
                }

                SafeFileHandle reusable;
                string devicePath;
                if (_handles.TryGetValue(disk, out var held)
                    && held.Handle is { IsInvalid: false, IsClosed: false } cached)
                {
                    reusable = cached;
                    devicePath = held.DevicePath;
                }
                else
                {
                    // TryGetValue failed or the cached handle is unusable; dispose any remnant.
                    if (_handles.TryGetValue(disk, out var stale))
                    {
                        stale.Handle.Dispose();
                        _handles.Remove(disk);
                    }

                    var opened = _open(disk);
                    OpenCount++;
                    // IsInvalid alone is not portable for test stubs: IntPtr.Zero is stdin on
                    // Linux. Production opens set Win32Error when CreateFileW fails.
                    if (opened.Handle.IsInvalid || opened.Win32Error != 0)
                    {
                        opened.Handle.Dispose();
                        error = WindowsStorageNative.FormatWin32Failure("CreateFileW", opened.DevicePath, opened.Win32Error, "IOCTL_DISK_PERFORMANCE");
                        return false;
                    }

                    held = new HeldDisk(opened.Handle, opened.DevicePath);
                    _handles[disk] = held;
                    reusable = held.Handle;
                    devicePath = held.DevicePath;
                }

                var queried = _query(reusable, devicePath);
                if (!queried.Ok)
                {
                    held.Handle.Dispose();
                    _handles.Remove(disk);
                    error = queried.Error ?? "IOCTL_DISK_PERFORMANCE failed.";
                    return false;
                }

                return TryMap(queried.Snapshot, out counters, out error);
            }
        }

        /// <summary>Closes every cached disk handle. A second call is a no-op.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                foreach (var held in _handles.Values)
                {
                    held.Handle.Dispose();
                }

                _handles.Clear();
            }
        }

        /// <summary>
        /// Maps a snapshot into counters. Negative native counters are a failed read, not zeros.
        /// Read and write times are response time, not seek time. Queue depth is the current outstanding count.
        /// Average queue depth is not present in this IOCTL.
        /// </summary>
        /// <param name="data">IOCTL result.</param>
        /// <param name="counters">Mapped counters when the method returns <see langword="true"/>.</param>
        /// <param name="error">Diagnostic when a counter is negative.</param>
        /// <returns><see langword="true"/> when every time and byte field is non-negative.</returns>
        internal static bool TryMap(WindowsStorageNative.DiskPerformanceSnapshot data, out BlockDeviceCounters counters, out string? error)
        {
            counters = default;
            if (data.BytesRead < 0 || data.BytesWritten < 0 || data.ReadTime < 0 || data.WriteTime < 0 || data.IdleTime < 0 || data.QueryTime < 0)
            {
                error = "IOCTL_DISK_PERFORMANCE returned a negative counter.";
                return false;
            }

            error = null;
            counters = new BlockDeviceCounters(
                ReadBytes: (ulong)data.BytesRead,
                WriteBytes: (ulong)data.BytesWritten,
                ReadOperations: data.ReadCount,
                WriteOperations: data.WriteCount,
                ReadBusyMicroseconds: (ulong)(data.ReadTime / 10),
                WriteBusyMicroseconds: (ulong)(data.WriteTime / 10),
                LatencyAvailable: true,
                IdleTimeAvailable: true,
                IdleMicroseconds: (ulong)(data.IdleTime / 10),
                QueryTimeMicroseconds: (ulong)(data.QueryTime / 10),
                CurrentQueueAvailable: true,
                CurrentQueueDepth: data.QueueDepth,
                OperationCountsAre32Bit: true);
            return true;
        }

        private static (SafeFileHandle Handle, string DevicePath, int Win32Error) OpenPhysicalDrive(uint disk)
        {
            WindowsStorageNative.OpenPerformanceHandle(disk, out var handle, out var path, out var error);
            return (handle, path, error);
        }

        private static (bool Ok, WindowsStorageNative.DiskPerformanceSnapshot Snapshot, string? Error) QueryPhysicalDrive(SafeFileHandle handle, string devicePath)
        {
            var ok = WindowsStorageNative.TryQueryPerformance(handle, devicePath, out var snapshot, out var error);
            return (ok, snapshot, error);
        }

        private readonly record struct HeldDisk(SafeFileHandle Handle, string DevicePath);
    }
}
