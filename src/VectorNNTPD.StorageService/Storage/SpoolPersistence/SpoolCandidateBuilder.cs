using System.Buffers;
using VectorNNTPD.StorageService.Storage.SegmentPools;

namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{

    /// <summary>Builds selector inputs from the validation catalog and one monitor snapshot.</summary>
    internal static class SpoolCandidateBuilder
    {
        /// <summary>
        /// Builds one candidate per validated spool path. A missing or stale free-space sample
        /// sets <see cref="SpoolCandidate.SpaceKnown"/> false and is not reported as zero.
        /// A missing device rate is not zero. A missing device sample does not block the write.
        /// </summary>
        /// <param name="catalog">Devices and spool paths published by validation.</param>
        /// <param name="snapshot">Latest consistent monitor view.</param>
        /// <param name="gate">In-flight publish counts.</param>
        /// <param name="health">Recent filesystem failures. Not device load.</param>
        /// <param name="inFlightLimit">Per-spool publish cap.</param>
        /// <param name="time">Clock used to judge freshness.</param>
        /// <returns>The candidates for one selection. Order follows the catalog and is not significant.</returns>
        internal static SpoolCandidate[] Build(
            SegmentPoolCatalog catalog,
            SpoolObservationSnapshot snapshot,
            SpoolWriteGate gate,
            SpoolHealth health,
            int inFlightLimit,
            TimeProvider time)
        {
            var count = Count(catalog);
            var rented = ArrayPool<SpoolCandidate>.Shared.Rent(Math.Max(count, 1));
            try
            {
                var written = Fill(catalog, snapshot, gate, health, inFlightLimit, time, rented);
                var result = new SpoolCandidate[written];
                rented.AsSpan(0, written).CopyTo(result);
                return result;
            }
            finally
            {
                ArrayPool<SpoolCandidate>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// Writes one candidate per validated spool path into <paramref name="destination"/>.
        /// The same rules as <see cref="Build"/> apply. The caller owns <paramref name="destination"/>
        /// and may overwrite in-flight counts before selection.
        /// </summary>
        /// <param name="catalog">Devices and spool paths published by validation.</param>
        /// <param name="snapshot">Latest consistent monitor view.</param>
        /// <param name="gate">In-flight publish counts.</param>
        /// <param name="health">Recent filesystem failures. Not device load.</param>
        /// <param name="inFlightLimit">Per-spool publish cap.</param>
        /// <param name="time">Clock used to judge freshness.</param>
        /// <param name="destination">Buffer of at least one slot per catalog path.</param>
        /// <returns>The number of candidates written.</returns>
        internal static int Fill(
            SegmentPoolCatalog catalog,
            SpoolObservationSnapshot snapshot,
            SpoolWriteGate gate,
            SpoolHealth health,
            int inFlightLimit,
            TimeProvider time,
            Span<SpoolCandidate> destination)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(gate);
            ArgumentNullException.ThrowIfNull(health);
            ArgumentNullException.ThrowIfNull(time);
            var now = time.GetTimestamp();
            var snapshotFresh = snapshot.Timestamp != 0
                && time.GetElapsedTime(snapshot.Timestamp, now) <= SpoolSelectionPolicy.SampleFreshness;
            var index = 0;
            foreach (var device in catalog.Devices)
            {
                var unavailable = snapshot.UnavailableDevices.Contains(device.DeviceId);
                var hasDevice = snapshot.Devices.TryGetValue(device.DeviceId, out var sample);
                var deviceFresh = snapshotFresh && hasDevice && !unavailable;
                foreach (var path in device.PoolPaths)
                {
                    if ((uint)index >= (uint)destination.Length)
                    {
                        throw new InvalidOperationException("Candidate buffer is smaller than the spool catalog.");
                    }

                    var spaceKnown = false;
                    long total = 0;
                    long available = 0;
                    if (snapshot.Space.TryGetValue(path, out var space)
                        && time.GetElapsedTime(space.ProbeTimestamp, now) <= SpoolSelectionPolicy.SampleFreshness
                        && space.TotalBytes > 0
                        && space.AvailableBytes >= 0
                        && space.AvailableBytes <= space.TotalBytes)
                    {
                        spaceKnown = true;
                        total = space.TotalBytes;
                        available = space.AvailableBytes;
                        if (available > 0)
                        {
                            health.RecoverDiskFull(path);
                        }
                    }

                    destination[index++] = new SpoolCandidate(
                        path,
                        device.DeviceId,
                        total,
                        available,
                        spaceKnown,
                        Initialized: true,
                        AcceptsWrites: health.AcceptsWrites(path),
                        deviceFresh,
                        deviceFresh ? sample.ActiveTimePercent : null,
                        deviceFresh ? sample.WriteLatencyMilliseconds : null,
                        deviceFresh ? sample.AverageQueueDepth : null,
                        deviceFresh ? sample.CurrentQueueDepth : null,
                        gate.InFlight(path),
                        inFlightLimit);
                }
            }

            return index;
        }

        /// <summary>Number of spool paths in <paramref name="catalog"/>.</summary>
        /// <param name="catalog">Validated catalog. Must not be null.</param>
        /// <returns>The path count.</returns>
        private static int Count(SegmentPoolCatalog catalog)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            var count = 0;
            foreach (var device in catalog.Devices)
            {
                count += device.PoolPaths.Count;
            }

            return count;
        }
    }
}
