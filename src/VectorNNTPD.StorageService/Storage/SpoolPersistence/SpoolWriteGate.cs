namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{
    /// <summary>
    /// Counts in-flight publishes and pending article bytes per spool/device.
    /// Pending bytes are keyed by device id so directories that share a volume share projected capacity.
    /// Each <see cref="TryReserve"/> lease is released exactly once via <see cref="Exit(in SpoolSelection)"/>
    /// so concurrent writers on the same spool cannot pop each other's pending bytes.
    /// </summary>
    internal sealed class SpoolWriteGate
    {
        private readonly Dictionary<string, int> _inFlight = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _pendingByDevice = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        /// <summary>
        /// Selects the best eligible spool using in-flight counts and pending bytes under this lock,
        /// then reserves it. Concurrent callers cannot all herd onto one obsolete free-space reading.
        /// </summary>
        /// <param name="observed">Candidates from the latest monitor snapshot. In-flight and available fields are adjusted in place.</param>
        /// <param name="requiredBytes">Rewritten article length passed to the selector and reserved on success.</param>
        /// <param name="criticalPercent">Used percent above which a spool is ineligible.</param>
        /// <param name="selection">The reserved spool when the method returns <see langword="true"/>.</param>
        /// <returns><see langword="false"/> when every spool is ineligible or cannot fit.</returns>
        internal bool TryReserve(
            Span<SpoolCandidate> observed,
            long requiredBytes,
            int criticalPercent,
            out SpoolSelection selection)
        {
            lock (_gate)
            {
                if (observed.Length == 0)
                {
                    selection = default;
                    return false;
                }

                var needed = requiredBytes < 1 ? 1 : requiredBytes;
                for (var i = 0; i < observed.Length; i++)
                {
                    var candidate = observed[i];
                    var inFlight = _inFlight.TryGetValue(candidate.SpoolRoot, out var current) ? current : 0;
                    var accepts = candidate.AcceptsWrites;
                    if (candidate.SpaceKnown
                        && candidate.TotalBytes > 0
                        && SpoolSelector.UsedPercent(candidate.TotalBytes, candidate.AvailableBytes) > criticalPercent)
                    {
                        // Critical uses the measured sample, not pending-projected available bytes.
                        accepts = false;
                    }

                    var pending = _pendingByDevice.TryGetValue(candidate.DeviceId, out var reserved)
                        ? reserved
                        : 0;
                    var available = candidate.AvailableBytes;
                    if (candidate.SpaceKnown && pending > 0)
                    {
                        available = available > pending ? available - pending : 0;
                    }

                    observed[i] = candidate with
                    {
                        InFlightWrites = inFlight,
                        AvailableBytes = available,
                        AcceptsWrites = accepts,
                    };
                }

                // Critical already applied above; pass 100 so projected available cannot re-trigger it.
                var chosen = SpoolSelector.Select(observed, needed, criticalPercent: 100);
                if (chosen is not { } selected)
                {
                    selection = default;
                    return false;
                }

                _inFlight[selected.SpoolRoot] = (_inFlight.TryGetValue(selected.SpoolRoot, out var count) ? count : 0) + 1;
                _pendingByDevice[selected.DeviceId] =
                    (_pendingByDevice.TryGetValue(selected.DeviceId, out var pendingBytes) ? pendingBytes : 0) + needed;
                selection = selected with { ReservedBytes = needed };
                return true;
            }
        }

        /// <summary>Reserves one publish slot when the spool is under <paramref name="limit"/>.</summary>
        internal bool TryEnter(string spoolRoot, int limit)
        {
            lock (_gate)
            {
                _inFlight.TryGetValue(spoolRoot, out var current);
                if (current >= limit)
                {
                    return false;
                }

                _inFlight[spoolRoot] = current + 1;
                return true;
            }
        }

        /// <summary>
        /// Releases the pending-byte lease and publish slot from <see cref="TryReserve"/>.
        /// Safe under concurrent writers: subtracts this selection's bytes only.
        /// </summary>
        /// <param name="selection">Lease returned by <see cref="TryReserve"/>.</param>
        internal void Exit(in SpoolSelection selection)
        {
            if (string.IsNullOrEmpty(selection.SpoolRoot))
            {
                return;
            }

            lock (_gate)
            {
                ReleasePending(selection.DeviceId, selection.ReservedBytes);
                DecrementInFlight(selection.SpoolRoot);
            }
        }

        /// <summary>Releases one publish slot from <see cref="TryEnter"/> (no pending-byte lease).</summary>
        /// <param name="spoolRoot">Canonical spool path.</param>
        internal void Exit(string spoolRoot)
        {
            lock (_gate)
            {
                DecrementInFlight(spoolRoot);
            }
        }

        private void ReleasePending(string deviceId, long bytes)
        {
            if (string.IsNullOrEmpty(deviceId) || bytes <= 0)
            {
                return;
            }

            if (!_pendingByDevice.TryGetValue(deviceId, out var pending))
            {
                return;
            }

            var next = pending > bytes ? pending - bytes : 0;
            if (next == 0)
            {
                _pendingByDevice.Remove(deviceId);
            }
            else
            {
                _pendingByDevice[deviceId] = next;
            }
        }

        private void DecrementInFlight(string spoolRoot)
        {
            if (!_inFlight.TryGetValue(spoolRoot, out var current) || current <= 0)
            {
                return;
            }

            if (current == 1)
            {
                _inFlight.Remove(spoolRoot);
                return;
            }

            _inFlight[spoolRoot] = current - 1;
        }

        /// <summary>Current reservations for <paramref name="spoolRoot"/>.</summary>
        internal int InFlight(string spoolRoot)
        {
            lock (_gate)
            {
                return _inFlight.TryGetValue(spoolRoot, out var current) ? current : 0;
            }
        }

        /// <summary>Pending article bytes projected against <paramref name="deviceId"/>.</summary>
        internal long PendingBytes(string deviceId)
        {
            lock (_gate)
            {
                return _pendingByDevice.TryGetValue(deviceId, out var pending) ? pending : 0;
            }
        }
    }
}
