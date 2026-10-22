using Microsoft.Extensions.Logging;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;

namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Samples validated physical devices and filesystem free space about once per second.
    /// It does not evict, reject articles, or change advertisements. Selection reads the snapshot.
    /// </summary>
    internal sealed class BlockDeviceMonitorService : IApplicationService
    {
        private const int ErrorLogInterval = 60;

        private readonly SegmentPoolCatalog _catalog;
        private readonly IBlockDeviceCounterSource _counters;
        private readonly ILogger<BlockDeviceMonitorService> _logger;
        private readonly ISpoolFreeSpaceProbe? _freeSpace;
        private readonly TimeProvider _time;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _runCts = new();
        private readonly object _publishGate = new();
        private readonly Dictionary<string, DeviceCursor> _cursors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _commitsAfterProbeOpen = new(StringComparer.Ordinal);
        private SpoolObservationSnapshot _observations = SpoolObservationSnapshot.Empty;
        private Task? _execution;
        private int _started;

        /// <summary>Initializes a one-second monitor.</summary>
        /// <param name="catalog">Devices published by successful validation.</param>
        /// <param name="counters">Platform counter source.</param>
        /// <param name="logger">Sample and error logger.</param>
        /// <param name="freeSpace">Filesystem capacity probe sampled with the device counters.</param>
        public BlockDeviceMonitorService(
            SegmentPoolCatalog catalog,
            IBlockDeviceCounterSource counters,
            ILogger<BlockDeviceMonitorService> logger,
            ISpoolFreeSpaceProbe freeSpace)
            : this(catalog, counters, logger, TimeSpan.FromSeconds(1), TimeProvider.System, freeSpace)
        {
        }

        /// <summary>Initializes a monitor with an explicit interval and clock.</summary>
        /// <param name="catalog">Devices published by successful validation.</param>
        /// <param name="counters">Platform counter source.</param>
        /// <param name="logger">Sample and error logger.</param>
        /// <param name="interval">Delay between samples.</param>
        /// <param name="time">Clock used for the delay and for elapsed time.</param>
        /// <param name="freeSpace">Filesystem capacity probe. Null leaves free space unknown.</param>
        internal BlockDeviceMonitorService(
            SegmentPoolCatalog catalog,
            IBlockDeviceCounterSource counters,
            ILogger<BlockDeviceMonitorService> logger,
            TimeSpan interval,
            TimeProvider time,
            ISpoolFreeSpaceProbe? freeSpace = null)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(counters);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(time);
            if (interval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(interval));
            }

            _catalog = catalog;
            _counters = counters;
            _logger = logger;
            _interval = interval;
            _time = time;
            _freeSpace = freeSpace;
        }

        /// <summary>Latest consistent device and free-space view.</summary>
        internal SpoolObservationSnapshot Observations
        {
            get
            {
                lock (_publishGate)
                {
                    return _observations;
                }
            }
        }

        /// <summary>
        /// Subtracts <paramref name="bytes"/> from the cached available space for <paramref name="spoolPath"/>
        /// and records the commit so a probe already in progress cannot add those bytes back.
        /// A missing cache entry is left unchanged. Duplicate and failed publishes do not call this.
        /// </summary>
        /// <param name="spoolPath">Canonical spool path.</param>
        /// <param name="bytes">Bytes renamed into that spool. Negative values are ignored.</param>
        internal void NoteCommittedBytes(string spoolPath, long bytes)
        {
            if (bytes <= 0 || string.IsNullOrEmpty(spoolPath))
            {
                return;
            }

            lock (_publishGate)
            {
                if (!_observations.Space.TryGetValue(spoolPath, out var current))
                {
                    return;
                }

                var space = new Dictionary<string, SpoolSpaceObservation>(_observations.Space, StringComparer.Ordinal);
                var available = current.AvailableBytes > bytes ? current.AvailableBytes - bytes : 0;
                space[spoolPath] = current with { AvailableBytes = available };
                _observations = _observations with { Space = space };
                var pending = _commitsAfterProbeOpen.GetValueOrDefault(spoolPath);
                _commitsAfterProbeOpen[spoolPath] = pending > long.MaxValue - bytes ? long.MaxValue : pending + bytes;
            }
        }

        /// <inheritdoc />
        public string Name => "BlockDeviceMonitor";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <summary>Completed rate samples. The first sample for a device is a baseline and is not counted.</summary>
        internal int CompletedSamples { get; private set; }

        /// <summary>Counter reads that failed or devices that disappeared.</summary>
        internal int CollectionFailures { get; private set; }

        /// <summary>Most recent published sample, when one exists.</summary>
        internal PublishedSample? Latest { get; private set; }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return Task.CompletedTask;
            }

            _execution = RunAsync(_runCts.Token);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
            var execution = _execution;
            if (execution is null)
            {
                return;
            }

            try
            {
                await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                ExpectedException.Ignore(ex);
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Sample();
                    await Task.Delay(_interval, _time, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                ExpectedException.Ignore(ex);
            }
        }

        private void Sample()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unavailable = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, PublishedSample> devices;
            Dictionary<string, SpoolSpaceObservation> space;
            lock (_publishGate)
            {
                devices = new Dictionary<string, PublishedSample>(_observations.Devices, StringComparer.Ordinal);
                space = new Dictionary<string, SpoolSpaceObservation>(_observations.Space, StringComparer.Ordinal);
            }

            foreach (var device in _catalog.Devices)
            {
                seen.Add(device.DeviceId);
                var stamp = _time.GetTimestamp();
                try
                {
                    if (!_counters.TryRead(device.DeviceId, out var current, out var error))
                    {
                        NoteFailure(device.DeviceId, error ?? "counters unavailable");
                        unavailable.Add(device.DeviceId);
                        devices.Remove(device.DeviceId);
                        continue;
                    }

                    if (!_cursors.TryGetValue(device.DeviceId, out var cursor) || cursor.Counters is null)
                    {
                        _cursors[device.DeviceId] = new DeviceCursor(current, stamp, ConsecutiveFailures: 0);
                        continue;
                    }

                    var elapsed = _time.GetElapsedTime(cursor.Timestamp, stamp);
                    var rate = BlockDeviceRates.Compute(cursor.Counters.Value, current, elapsed);
                    _cursors[device.DeviceId] = new DeviceCursor(current, stamp, ConsecutiveFailures: 0);
                    if (rate.Skipped)
                    {
                        SegmentPoolLogMessages.SampleSkipped(_logger, device.DeviceId, rate.SkipReason ?? "skipped");
                        continue;
                    }

                    var published = new PublishedSample(
                        device.DeviceId,
                        device.PoolPaths,
                        rate.ReadBytesPerSecond,
                        rate.WriteBytesPerSecond,
                        rate.ReadIops,
                        rate.WriteIops,
                        rate.TotalIops,
                        rate.ReadLatencyMilliseconds,
                        rate.WriteLatencyMilliseconds,
                        rate.ActiveTimePercent,
                        rate.AverageQueueDepth,
                        rate.CurrentQueueDepth,
                        (long)elapsed.TotalMilliseconds,
                        rate.CounterResets);
                    Latest = published;
                    devices[device.DeviceId] = published;
                    unavailable.Remove(device.DeviceId);
                    CompletedSamples++;
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        SegmentPoolLogMessages.DeviceSample(
                            _logger,
                            device.DeviceId,
                            string.Join(",", device.PoolPaths),
                            FormatOptional(rate.ReadBytesPerSecond),
                            FormatOptional(rate.WriteBytesPerSecond),
                            FormatOptional(rate.ReadIops),
                            FormatOptional(rate.WriteIops),
                            FormatOptional(rate.TotalIops),
                            FormatOptional(rate.ReadLatencyMilliseconds),
                            FormatOptional(rate.WriteLatencyMilliseconds),
                            FormatOptional(rate.ActiveTimePercent),
                            FormatOptional(rate.AverageQueueDepth),
                            FormatOptional(rate.CurrentQueueDepth),
                            published.IntervalMilliseconds,
                            rate.CounterResets);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    NoteFailure(device.DeviceId, ex.GetType().Name + ": " + ex.Message);
                    unavailable.Add(device.DeviceId);
                    devices.Remove(device.DeviceId);
                }
            }

            foreach (var known in _cursors.Keys.Where(key => !seen.Contains(key)).ToArray())
            {
                SegmentPoolLogMessages.CountersUnavailable(_logger, known, "device disappeared");
                CollectionFailures++;
                _cursors.Remove(known);
                unavailable.Add(known);
                devices.Remove(known);
            }

            if (_freeSpace is not null)
            {
                var probedAt = _time.GetTimestamp();
                foreach (var device in _catalog.Devices)
                {
                    foreach (var path in device.PoolPaths)
                    {
                        var opened = OpenProbeWindow(path);
                        if (_freeSpace.TryRead(path, out var total, out var available))
                        {
                            space[path] = new SpoolSpaceObservation(total, available, probedAt);
                        }
                        else
                        {
                            RestoreProbeCommits(path, opened);
                        }
                    }
                }
            }

            lock (_publishGate)
            {
                foreach (var path in space.Keys.ToArray())
                {
                    var probed = space[path];
                    var during = _commitsAfterProbeOpen.GetValueOrDefault(path);
                    var available = probed.AvailableBytes;
                    if (during > 0)
                    {
                        available = during >= available ? 0 : available - during;
                    }

                    space[path] = probed with { AvailableBytes = available };
                }

                _observations = new SpoolObservationSnapshot(_time.GetTimestamp(), devices, unavailable, space);
            }
        }

        /// <summary>
        /// Drops commits that finished before this probe. Those bytes are assumed to be in the probe result.
        /// Commits that land after this call stay in the counter and are subtracted from the probe.
        /// </summary>
        /// <param name="path">Canonical spool path.</param>
        /// <returns>The commit total that was cleared.</returns>
        private long OpenProbeWindow(string path)
        {
            lock (_publishGate)
            {
                return _commitsAfterProbeOpen.Remove(path, out var opened) ? opened : 0;
            }
        }

        /// <summary>Puts commits back when the probe itself failed, so a later probe can still account for them.</summary>
        /// <param name="path">Canonical spool path.</param>
        /// <param name="opened">Commits cleared by <see cref="OpenProbeWindow"/>.</param>
        private void RestoreProbeCommits(string path, long opened)
        {
            lock (_publishGate)
            {
                var added = _commitsAfterProbeOpen.GetValueOrDefault(path);
                var restored = opened > 0 && added > long.MaxValue - opened ? long.MaxValue : opened + added;
                if (restored > 0)
                {
                    _commitsAfterProbeOpen[path] = restored;
                }
                else
                {
                    _commitsAfterProbeOpen.Remove(path);
                }
            }
        }

        private void NoteFailure(string deviceId, string error)
        {
            CollectionFailures++;
            var count = 1;
            if (_cursors.TryGetValue(deviceId, out var cursor))
            {
                count = cursor.ConsecutiveFailures + 1;
            }

            _cursors[deviceId] = new DeviceCursor(null, 0, count);

            if (count == 1 || count % ErrorLogInterval == 0)
            {
                SegmentPoolLogMessages.CountersUnavailable(_logger, deviceId, error);
            }
        }

        private static string FormatOptional(long? value) =>
            value is { } number
                ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "unavailable";

        private static string FormatOptional(double? value) =>
            value is { } number
                ? number.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : "unavailable";

        /// <summary>
        /// One published monitor sample. Null fields were not measured.
        /// Zero fields were measured and were zero. This sample does not select a disk.
        /// </summary>
        /// <param name="DeviceId">Physical device identity from validation.</param>
        /// <param name="PoolPaths">Pool directories associated with the device.</param>
        /// <param name="ReadBytesPerSecond">Read throughput. Zero means no bytes were read.</param>
        /// <param name="WriteBytesPerSecond">Write throughput. Zero means no bytes were written.</param>
        /// <param name="ReadIops">Completed reads per second.</param>
        /// <param name="WriteIops">Completed writes per second.</param>
        /// <param name="TotalIops">Sum of read and write IOPS when both exist.</param>
        /// <param name="ReadLatencyMilliseconds">Average OS read response time in milliseconds. Not seek time.</param>
        /// <param name="WriteLatencyMilliseconds">Average OS write response time in milliseconds. Not seek time.</param>
        /// <param name="ActiveTimePercent">Percent of the interval with outstanding I/O. Not clamped, and not CPU I/O wait.</param>
        /// <param name="AverageQueueDepth">Average outstanding I/O over the interval. Unavailable on Windows.</param>
        /// <param name="CurrentQueueDepth">Outstanding requests at this sample. Not an average.</param>
        /// <param name="IntervalMilliseconds">Elapsed monotonic interval.</param>
        /// <param name="CounterResets">Counters that decreased during the interval, or <c>none</c>.</param>
        internal readonly record struct PublishedSample(
            string DeviceId,
            IReadOnlyList<string> PoolPaths,
            long? ReadBytesPerSecond,
            long? WriteBytesPerSecond,
            double? ReadIops,
            double? WriteIops,
            double? TotalIops,
            double? ReadLatencyMilliseconds,
            double? WriteLatencyMilliseconds,
            double? ActiveTimePercent,
            double? AverageQueueDepth,
            long? CurrentQueueDepth,
            long IntervalMilliseconds,
            string CounterResets);

        private readonly record struct DeviceCursor(
            BlockDeviceCounters? Counters,
            long Timestamp,
            int ConsecutiveFailures);
    }
}
