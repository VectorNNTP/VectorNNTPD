namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Reads Linux <c>/proc/diskstats</c> or Windows <c>IOCTL_DISK_PERFORMANCE</c>.
    /// Does not shell out and does not write to the pool. Windows disk handles are reused until
    /// <see cref="Dispose"/> or a failed query.
    /// </summary>
    internal sealed class PlatformBlockDeviceCounterSource : IBlockDeviceCounterSource, IDisposable
    {
        private readonly Func<string> _readDiskstats;
        private readonly WindowsDiskPerformanceReader _windows;
        private int _disposed;

        /// <summary>Uses <c>/proc/diskstats</c> on Linux and a cached disk-performance reader on Windows.</summary>
        public PlatformBlockDeviceCounterSource()
            : this(static () => File.ReadAllText("/proc/diskstats"), new WindowsDiskPerformanceReader())
        {
        }

        /// <summary>Uses <paramref name="readDiskstats"/> so tests can supply diskstats text.</summary>
        /// <param name="readDiskstats">Reads the current diskstats document.</param>
        internal PlatformBlockDeviceCounterSource(Func<string> readDiskstats)
            : this(readDiskstats, new WindowsDiskPerformanceReader())
        {
        }

        /// <summary>Uses the supplied Windows reader.</summary>
        /// <param name="readDiskstats">Reads the current diskstats document.</param>
        /// <param name="windows">Reader that owns physical-drive handles.</param>
        internal PlatformBlockDeviceCounterSource(Func<string> readDiskstats, WindowsDiskPerformanceReader windows)
        {
            ArgumentNullException.ThrowIfNull(readDiskstats);
            ArgumentNullException.ThrowIfNull(windows);
            _readDiskstats = readDiskstats;
            _windows = windows;
        }

        /// <inheritdoc />
        public bool TryRead(string deviceId, out BlockDeviceCounters counters, out string? error)
        {
            counters = default;
            error = null;
            if (Volatile.Read(ref _disposed) == 1)
            {
                error = "block-device counter source is disposed.";
                return false;
            }

            if (deviceId.StartsWith("disk:", StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows())
                {
                    error = "Windows disk counters are not available on this operating system.";
                    return false;
                }

                return _windows.TryRead(deviceId, out counters, out error);
            }

            if (deviceId.StartsWith("block:", StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsLinux())
                {
                    error = "Linux diskstats counters are not available on this operating system.";
                    return false;
                }

                try
                {
                    return LinuxDiskstats.TryParse(_readDiskstats(), deviceId["block:".Length..], out counters, out error);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = "diskstats could not be read: " + ex.GetType().Name + ": " + ex.Message;
                    return false;
                }
            }

            error = "no block-device counter source for '" + deviceId + "'.";
            return false;
        }

        /// <summary>Releases cached Windows disk handles. A second call is a no-op.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _windows.Dispose();
        }
    }
}
