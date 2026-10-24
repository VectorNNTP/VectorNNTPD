namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{
    /// <summary>The spool chosen for one article, including the pending-byte lease to release on <see cref="SpoolWriteGate.Exit(in SpoolSelection)"/>.</summary>
    /// <param name="SpoolRoot">Canonical directory that will receive the file.</param>
    /// <param name="DeviceId">Device that backs <paramref name="SpoolRoot"/>.</param>
    /// <param name="Busy">Whether the chosen spool's fresh device sample is past a busy ranking line. Busy still accepts the write.</param>
    /// <param name="ReservedBytes">Article bytes reserved against <paramref name="DeviceId"/> pending capacity.</param>
    internal readonly record struct SpoolSelection(string SpoolRoot, string DeviceId, bool Busy, long ReservedBytes = 0);

    /// <summary>
    /// Picks the eligible spool with the greatest available capacity in bytes.
    /// Unknown/stale space and used percent above CriticalPercent are excluded.
    /// Enumeration order does not change the result; path ordinal breaks exact ties.
    /// </summary>
    internal static class SpoolSelector
    {
        /// <summary>
        /// Selects the best writable candidate. Returns null when every candidate is blocked,
        /// at its in-flight cap, lacks a valid capacity sample, is above Critical, or cannot fit
        /// <paramref name="requiredBytes"/>.
        /// </summary>
        /// <param name="candidates">Current spool observations. The method does not mutate them.</param>
        /// <param name="requiredBytes">Rewritten article length. A fresh sample below this cannot fit the file.</param>
        /// <param name="criticalPercent">Used percent above which a spool is ineligible (strict greater-than).</param>
        /// <returns>The selected spool, or null.</returns>
        internal static SpoolSelection? Select(
            ReadOnlySpan<SpoolCandidate> candidates,
            long requiredBytes,
            int criticalPercent)
        {
            var found = false;
            var best = default(SpoolCandidate);
            foreach (var candidate in candidates)
            {
                if (!IsEligible(candidate, requiredBytes, criticalPercent))
                {
                    continue;
                }

                if (!found || Compare(candidate, best) < 0)
                {
                    best = candidate;
                    found = true;
                }
            }

            if (!found)
            {
                return null;
            }

            return new SpoolSelection(best.SpoolRoot, best.DeviceId, IsBusy(best));
        }

        /// <summary>
        /// Used-space percent from filesystem totals.
        /// The caller has already required a positive total.
        /// </summary>
        /// <param name="totalBytes">Filesystem capacity in bytes.</param>
        /// <param name="availableBytes">Bytes still available to the caller.</param>
        /// <returns>Truncated percent in the range 0–100 when the inputs are consistent.</returns>
        internal static int UsedPercent(long totalBytes, long availableBytes)
        {
            if (availableBytes <= 0)
            {
                return 100;
            }

            if (availableBytes >= totalBytes)
            {
                return 0;
            }

            if (totalBytes > long.MaxValue / 100)
            {
                return (int)Math.Min(100d, Math.Max(0d, 100d - (availableBytes * 100d / totalBytes)));
            }

            var freePercent = availableBytes % totalBytes * 100 / totalBytes;
            return (int)(100 - freePercent);
        }

        /// <summary>
        /// A spool may be written when it is initialized, accepts writes, under its in-flight cap,
        /// has a fresh consistent free-space sample that can hold <paramref name="requiredBytes"/>,
        /// and used percent is at or below <paramref name="criticalPercent"/>.
        /// </summary>
        internal static bool IsEligible(in SpoolCandidate candidate, long requiredBytes, int criticalPercent)
        {
            if (!candidate.Initialized || !candidate.AcceptsWrites)
            {
                return false;
            }

            if (candidate.InFlightWrites >= candidate.InFlightLimit || candidate.InFlightLimit < 1)
            {
                return false;
            }

            if (!candidate.SpaceKnown
                || candidate.TotalBytes <= 0
                || candidate.AvailableBytes < 0
                || candidate.AvailableBytes > candidate.TotalBytes)
            {
                return false;
            }

            var used = UsedPercent(candidate.TotalBytes, candidate.AvailableBytes);
            if (used > criticalPercent)
            {
                return false;
            }

            var needed = requiredBytes < 1 ? 1 : requiredBytes;
            return candidate.AvailableBytes >= needed;
        }

        /// <summary>
        /// Busy means a fresh sample measured active time, write latency, or queue depth past a ranking line.
        /// Busy does not make the spool ineligible.
        /// </summary>
        internal static bool IsBusy(in SpoolCandidate candidate)
        {
            if (!candidate.DeviceSampleFresh)
            {
                return false;
            }

            if (candidate.ActiveTimePercent is >= SpoolSelectionPolicy.BusyActiveTimePercent)
            {
                return true;
            }

            if (candidate.WriteLatencyMilliseconds is >= SpoolSelectionPolicy.BusyWriteLatencyMilliseconds)
            {
                return true;
            }

            if (candidate.CurrentQueueDepth is >= SpoolSelectionPolicy.BusyCurrentQueueDepth)
            {
                return true;
            }

            return candidate.AverageQueueDepth is >= SpoolSelectionPolicy.BusyAverageQueueDepth;
        }

        /// <summary>
        /// Compares two writable spools. A negative result means <paramref name="left"/> is better.
        /// Greatest available bytes wins first. Equal available bytes break by fewer in-flight writes,
        /// then lower write latency when both measured, then ordinal spool path.
        /// </summary>
        internal static int Compare(in SpoolCandidate left, in SpoolCandidate right)
        {
            var availableOrder = right.AvailableBytes.CompareTo(left.AvailableBytes);
            if (availableOrder != 0)
            {
                return availableOrder;
            }

            var flight = left.InFlightWrites - right.InFlightWrites;
            if (flight != 0)
            {
                return flight;
            }

            if (left.DeviceSampleFresh
                && right.DeviceSampleFresh
                && left.WriteLatencyMilliseconds is { } leftLatency
                && right.WriteLatencyMilliseconds is { } rightLatency)
            {
                var latencyOrder = leftLatency.CompareTo(rightLatency);
                if (latencyOrder != 0)
                {
                    return latencyOrder;
                }
            }

            return string.CompareOrdinal(left.SpoolRoot, right.SpoolRoot);
        }
    }
}
