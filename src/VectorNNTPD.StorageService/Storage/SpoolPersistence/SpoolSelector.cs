namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{

    /// <summary>The spool chosen for one article.</summary>
    /// <param name="SpoolRoot">Canonical directory that will receive the file.</param>
    /// <param name="DeviceId">Device that backs <paramref name="SpoolRoot"/>.</param>
    /// <param name="Busy">Whether the chosen spool's fresh device sample is past a busy ranking line. Busy still accepts the write.</param>
    internal readonly record struct SpoolSelection(string SpoolRoot, string DeviceId, bool Busy);

    /// <summary>
    /// Picks one spool that can accept a filesystem write.
    /// Free space wins when the gap is material. Load, in-flight work, and path break a capacity tie.
    /// Busy time, stale samples, and used-space percent do not reject a spool.
    /// </summary>
    internal static class SpoolSelector
    {
        /// <summary>
        /// Selects the best writable candidate. Returns null when every candidate is blocked,
        /// at its in-flight cap, or known not to fit <paramref name="requiredBytes"/>.
        /// Enumeration order does not change the result.
        /// </summary>
        /// <param name="candidates">Current spool observations. The method does not mutate them.</param>
        /// <param name="requiredBytes">Rewritten article length. A fresh sample below this cannot fit the file.</param>
        /// <returns>The selected spool, or null.</returns>
        internal static SpoolSelection? Select(ReadOnlySpan<SpoolCandidate> candidates, long requiredBytes)
        {
            var found = false;
            var best = default(SpoolCandidate);
            foreach (var candidate in candidates)
            {
                if (!IsEligible(candidate, requiredBytes))
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
        /// Used-space percent from filesystem totals. Not a write gate.
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

            // available * 100 overflows long when the volume is larger than long.MaxValue / 100.
            if (totalBytes > long.MaxValue / 100)
            {
                return (int)Math.Min(100d, Math.Max(0d, 100d - (availableBytes * 100d / totalBytes)));
            }

            var freePercent = availableBytes % totalBytes * 100 / totalBytes;
            return (int)(100 - freePercent);
        }

        /// <summary>
        /// A spool may be written when it is initialized, not closed for an operational failure,
        /// under its in-flight cap, and a fresh free-space sample can hold <paramref name="requiredBytes"/>.
        /// Missing or stale free space does not reject it. Used-space percent does not reject it.
        /// Device load does not reject it.
        /// </summary>
        /// <param name="candidate">One spool observation.</param>
        /// <param name="requiredBytes">Rewritten article length. Zero or negative is treated as one byte.</param>
        /// <returns><see langword="true"/> when the spool may receive this article.</returns>
        internal static bool IsEligible(in SpoolCandidate candidate, long requiredBytes)
        {
            if (!candidate.Initialized || !candidate.AcceptsWrites)
            {
                return false;
            }

            if (candidate.InFlightWrites >= candidate.InFlightLimit || candidate.InFlightLimit < 1)
            {
                return false;
            }

            if (!candidate.SpaceKnown)
            {
                return true;
            }

            if (candidate.TotalBytes <= 0
                || candidate.AvailableBytes < 0
                || candidate.AvailableBytes > candidate.TotalBytes)
            {
                return true;
            }

            var needed = requiredBytes < 1 ? 1 : requiredBytes;
            return candidate.AvailableBytes >= needed;
        }

        /// <summary>
        /// Busy means a fresh sample measured active time, write latency, or queue depth past a ranking line.
        /// A null measurement is not busy and is not zero. A stale sample is not busy.
        /// Busy does not make the spool ineligible.
        /// </summary>
        /// <param name="candidate">One spool observation.</param>
        /// <returns><see langword="true"/> when a fresh measurement is past a busy ranking line.</returns>
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
        /// A material free-space gap wins first. Inside that band, or when free space is unknown,
        /// measured-light beats unknown load, and unknown load beats measured-busy.
        /// Then fewer in-flight writes, then lower write latency when both fresh samples measured it,
        /// then the ordinal path. A spool with fresh free space ranks ahead of one without it.
        /// </summary>
        /// <param name="left">First writable spool.</param>
        /// <param name="right">Second writable spool.</param>
        /// <returns>Negative when <paramref name="left"/> should be chosen.</returns>
        internal static int Compare(in SpoolCandidate left, in SpoolCandidate right)
        {
            var leftKnown = HasUsableSpace(left);
            var rightKnown = HasUsableSpace(right);
            if (leftKnown != rightKnown)
            {
                return leftKnown ? -1 : 1;
            }

            if (leftKnown)
            {
                var threshold = FreeSpaceTieBytes(left.AvailableBytes, right.AvailableBytes);
                var gap = left.AvailableBytes - right.AvailableBytes;
                if (gap > threshold)
                {
                    return -1;
                }

                if (gap < -threshold)
                {
                    return 1;
                }
            }

            var load = LoadRank(left) - LoadRank(right);
            if (load != 0)
            {
                return load;
            }

            var flight = left.InFlightWrites - right.InFlightWrites;
            if (flight != 0)
            {
                return flight;
            }

            if (left.DeviceSampleFresh
                && right.DeviceSampleFresh
                && left.WriteLatencyMilliseconds is { } leftLatency
                && right.WriteLatencyMilliseconds is { } rightLatency
                && leftLatency != rightLatency)
            {
                return leftLatency < rightLatency ? -1 : 1;
            }

            // Exact latency equality (or a missing sample) falls through to a deterministic path order.
            return string.CompareOrdinal(left.SpoolRoot, right.SpoolRoot);
        }

        /// <summary>Bytes that must separate two free-space figures before free space decides the rank.</summary>
        /// <param name="leftAvailable">Available bytes on the first spool.</param>
        /// <param name="rightAvailable">Available bytes on the second spool.</param>
        /// <returns>The larger of one gibibyte and one percent of the larger available value.</returns>
        internal static long FreeSpaceTieBytes(long leftAvailable, long rightAvailable)
        {
            var larger = Math.Max(0, Math.Max(leftAvailable, rightAvailable));
            var fraction = (long)(larger * SpoolSelectionPolicy.FreeSpaceTieFraction);
            return Math.Max(SpoolSelectionPolicy.FreeSpaceTieMinimumBytes, fraction);
        }

        /// <summary>Fresh, consistent free space that ranking may compare. Inconsistent numbers are unknown.</summary>
        /// <param name="candidate">One spool observation.</param>
        /// <returns><see langword="true"/> when available bytes are a fresh sample inside the reported total.</returns>
        private static bool HasUsableSpace(in SpoolCandidate candidate) =>
            candidate.SpaceKnown
            && candidate.TotalBytes > 0
            && candidate.AvailableBytes >= 0
            && candidate.AvailableBytes <= candidate.TotalBytes;

        /// <summary>Zero for a fresh light sample, one for unknown load, two for a fresh busy sample.</summary>
        /// <param name="candidate">One spool observation.</param>
        /// <returns>The load class. Lower is better. Unknown is not zero.</returns>
        private static int LoadRank(in SpoolCandidate candidate)
        {
            if (!candidate.DeviceSampleFresh)
            {
                return 1;
            }

            return IsBusy(candidate) ? 2 : 0;
        }
    }
}
