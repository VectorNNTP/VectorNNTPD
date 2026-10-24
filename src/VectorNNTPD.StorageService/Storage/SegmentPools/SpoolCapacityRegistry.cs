using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;

namespace VectorNNTPD.StorageService.Storage.SegmentPools
{
    /// <summary>
    /// Thread-safe per-spool capacity state derived from <see cref="BlockDeviceMonitorService"/> samples.
    /// One entry exists for each catalog path; directories that share a <see cref="SegmentPoolDevice.DeviceId"/>
    /// share physical free space but keep distinct membership and eviction monitors.
    /// </summary>
    internal sealed class SpoolCapacityRegistry
    {
        private readonly SegmentPoolCatalog _catalog;
        private readonly BlockDeviceMonitorService _monitor;
        private readonly StorageServerOptions _options;
        private readonly TimeProvider _time;
        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        /// <summary>Initializes the registry.</summary>
        public SpoolCapacityRegistry(
            SegmentPoolCatalog catalog,
            BlockDeviceMonitorService monitor,
            IOptions<StorageServerOptions> options,
            TimeProvider time)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(monitor);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(time);
            _catalog = catalog;
            _monitor = monitor;
            _options = options.Value;
            _time = time;
        }

        /// <summary>Number of configured spool paths currently tracked (catalog size after refresh).</summary>
        internal int MonitorCount
        {
            get
            {
                EnsureEntries();
                return _entries.Count;
            }
        }

        /// <summary>Rebuilds entries from the catalog and refreshes from the latest monitor snapshot.</summary>
        internal void RefreshFromObservations()
        {
            EnsureEntries();
            var pools = _options.SegmentPools ?? new SegmentPoolsOptions();
            var observations = _monitor.Observations;
            var now = _time.GetTimestamp();
            foreach (var entry in _entries.Values)
            {
                entry.Apply(observations, pools, now, _time);
            }
        }

        /// <summary>Returns a snapshot for <paramref name="spoolDirectory"/> when tracked.</summary>
        internal bool TryGet(string spoolDirectory, out SpoolCapacityState state)
        {
            EnsureEntries();
            var key = Canonical(spoolDirectory);
            if (!_entries.TryGetValue(key, out var entry))
            {
                state = default;
                return false;
            }

            state = entry.Snapshot();
            return true;
        }

        /// <summary>Copies all current spool states in catalog order.</summary>
        internal SpoolCapacityState[] SnapshotAll()
        {
            EnsureEntries();
            RefreshFromObservations();
            var list = new List<SpoolCapacityState>(_entries.Count);
            foreach (var device in _catalog.Devices)
            {
                foreach (var path in device.PoolPaths)
                {
                    if (_entries.TryGetValue(Canonical(path), out var entry))
                    {
                        list.Add(entry.Snapshot());
                    }
                }
            }

            return list.ToArray();
        }

        /// <summary>Returns whether any spool is currently eligible for new article placement.</summary>
        internal bool HasEligibleDestination()
        {
            EnsureEntries();
            RefreshFromObservations();
            foreach (var entry in _entries.Values)
            {
                if (entry.Snapshot().PlacementEligible)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Marks whether the named spool's eviction monitor is actively reclaiming.</summary>
        internal void SetEvictionActive(string spoolDirectory, bool active)
        {
            EnsureEntries();
            if (_entries.TryGetValue(Canonical(spoolDirectory), out var entry))
            {
                entry.SetEvictionActive(active);
            }
        }

        /// <summary>Enumerates configured spool paths with their device ids.</summary>
        internal IReadOnlyList<(string SpoolDirectory, string DeviceId)> EnumerateSpools()
        {
            EnsureEntries();
            var list = new List<(string, string)>();
            foreach (var device in _catalog.Devices)
            {
                foreach (var path in device.PoolPaths)
                {
                    list.Add((Canonical(path), device.DeviceId));
                }
            }

            return list;
        }

        /// <summary>Returns sibling spool paths that share <paramref name="deviceId"/>.</summary>
        internal IReadOnlyList<string> PathsOnDevice(string deviceId)
        {
            EnsureEntries();
            var list = new List<string>();
            foreach (var device in _catalog.Devices)
            {
                if (!string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var path in device.PoolPaths)
                {
                    list.Add(Canonical(path));
                }
            }

            return list;
        }

        private void EnsureEntries()
        {
            foreach (var device in _catalog.Devices)
            {
                foreach (var path in device.PoolPaths)
                {
                    var canonical = Canonical(path);
                    _entries.GetOrAdd(canonical, static (p, id) => new Entry(p, id), device.DeviceId);
                }
            }
        }

        private static string Canonical(string spoolDirectory) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(spoolDirectory));

        private sealed class Entry
        {
            private readonly object _gate = new();
            private SpoolCapacityState _state;

            internal Entry(string spoolDirectory, string deviceId)
            {
                _state = new SpoolCapacityState(
                    spoolDirectory,
                    deviceId,
                    TotalBytes: 0,
                    AvailableBytes: 0,
                    UsedPercent: 0,
                    MeasurementValid: false,
                    ProbeTimestamp: 0,
                    PlacementEligible: false,
                    EvictionRequired: false,
                    EvictionActive: false);
            }

            internal void Apply(
                SpoolObservationSnapshot observations,
                SegmentPoolsOptions pools,
                long now,
                TimeProvider time)
            {
                lock (_gate)
                {
                    var path = _state.SpoolDirectory;
                    var valid = false;
                    long total = 0;
                    long available = 0;
                    long probe = 0;
                    var used = 0;
                    if (observations.Space.TryGetValue(path, out var space)
                        && space.TotalBytes > 0
                        && space.AvailableBytes >= 0
                        && space.AvailableBytes <= space.TotalBytes
                        && space.ProbeTimestamp != 0
                        && time.GetElapsedTime(space.ProbeTimestamp, now) <= SpoolSelectionPolicy.SampleFreshness)
                    {
                        valid = true;
                        total = space.TotalBytes;
                        available = space.AvailableBytes;
                        probe = space.ProbeTimestamp;
                        used = SpoolSelector.UsedPercent(total, available);
                    }

                    var placementEligible = valid && used <= pools.CriticalPercent;
                    var evictionRequired = valid && used >= pools.HighPercent;
                    _state = _state with
                    {
                        TotalBytes = total,
                        AvailableBytes = available,
                        UsedPercent = used,
                        MeasurementValid = valid,
                        ProbeTimestamp = probe,
                        PlacementEligible = placementEligible,
                        EvictionRequired = evictionRequired,
                    };
                }
            }

            internal void SetEvictionActive(bool active)
            {
                lock (_gate)
                {
                    _state = _state with { EvictionActive = active };
                }
            }

            internal SpoolCapacityState Snapshot()
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }
    }
}
