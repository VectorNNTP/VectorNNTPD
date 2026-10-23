namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Converts two cumulative counter samples into interval rates.
    /// A measured zero stays zero. A missing or reset counter stays unavailable.
    /// </summary>
    internal static class BlockDeviceRates
    {
        /// <summary>One interval's measurements. A skipped interval is not a zero-activity sample.</summary>
        /// <param name="ReadBytesPerSecond">Read throughput, or <see langword="null"/> when that counter reset or the interval was skipped.</param>
        /// <param name="WriteBytesPerSecond">Write throughput, or <see langword="null"/> when that counter reset or the interval was skipped.</param>
        /// <param name="ReadIops">Completed reads per second, or <see langword="null"/> when that counter reset or the interval was skipped.</param>
        /// <param name="WriteIops">Completed writes per second, or <see langword="null"/> when that counter reset or the interval was skipped.</param>
        /// <param name="TotalIops">Sum of read and write IOPS when both are available.</param>
        /// <param name="ReadLatencyMilliseconds">Average OS read response time. <see langword="null"/> when unavailable, reset, or no read completed. Not seek time.</param>
        /// <param name="WriteLatencyMilliseconds">Average OS write response time. <see langword="null"/> when unavailable, reset, or no write completed. Not seek time.</param>
        /// <param name="ActiveTimePercent">Percent of the interval with outstanding I/O. Not clamped. <see langword="null"/> when the platform did not supply it.</param>
        /// <param name="AverageQueueDepth">Average outstanding I/O over the interval. Linux only. <see langword="null"/> on Windows.</param>
        /// <param name="CurrentQueueDepth">Outstanding requests at the current sample. Not an average.</param>
        /// <param name="Skipped">Whether the interval has no publishable measurement. Invalid elapsed time sets this.</param>
        /// <param name="SkipReason">Why <paramref name="Skipped"/> is true.</param>
        /// <param name="CounterResets">Comma-separated cumulative counters that decreased, or <c>none</c>.</param>
        internal readonly record struct Sample(
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
            bool Skipped,
            string? SkipReason,
            string CounterResets);

        /// <summary>
        /// Computes rates over <paramref name="elapsed"/>.
        /// A decreased cumulative counter makes only that measurement unavailable.
        /// A 32-bit operation-count wrap is applied only when the matching byte counter did not decrease
        /// and the previous count is in the top quarter of the 32-bit range.
        /// </summary>
        /// <param name="previous">Earlier cumulative sample.</param>
        /// <param name="current">Later cumulative sample.</param>
        /// <param name="elapsed">Monotonic time between the samples.</param>
        /// <returns>Rates, or a skipped sample when the interval is not positive.</returns>
        internal static Sample Compute(BlockDeviceCounters previous, BlockDeviceCounters current, TimeSpan elapsed)
        {
            if (elapsed <= TimeSpan.Zero
                || double.IsNaN(elapsed.TotalSeconds)
                || double.IsInfinity(elapsed.TotalSeconds)
                || elapsed.TotalSeconds <= 0)
            {
                return new Sample(null, null, null, null, null, null, null, null, null, null, true, "invalid interval", "none");
            }

            var resets = new List<string>();
            var seconds = elapsed.TotalSeconds;
            var readByteDelta = Delta(previous.ReadBytes, current.ReadBytes, "readBytes", resets);
            var writeByteDelta = Delta(previous.WriteBytes, current.WriteBytes, "writeBytes", resets);
            var readOps = OperationDelta(
                previous,
                current,
                previous.ReadOperations,
                current.ReadOperations,
                current.ReadBytes < previous.ReadBytes,
                "readOperations",
                resets);
            var writeOps = OperationDelta(
                previous,
                current,
                previous.WriteOperations,
                current.WriteOperations,
                current.WriteBytes < previous.WriteBytes,
                "writeOperations",
                resets);
            var readIops = PerSecond(readOps, seconds);
            var writeIops = PerSecond(writeOps, seconds);
            double? totalIops = readIops is { } read && writeIops is { } write ? read + write : null;
            var readLatency = Latency(
                previous.LatencyAvailable,
                current.LatencyAvailable,
                previous.ReadBusyMicroseconds,
                current.ReadBusyMicroseconds,
                readOps,
                "readTime",
                resets);
            var writeLatency = Latency(
                previous.LatencyAvailable,
                current.LatencyAvailable,
                previous.WriteBusyMicroseconds,
                current.WriteBusyMicroseconds,
                writeOps,
                "writeTime",
                resets);

            return new Sample(
                BytesPerSecond(readByteDelta, seconds),
                BytesPerSecond(writeByteDelta, seconds),
                readIops,
                writeIops,
                totalIops,
                readLatency,
                writeLatency,
                ActivePercent(previous, current, elapsed, resets),
                AverageQueue(previous, current, seconds, resets),
                current.CurrentQueueAvailable && current.CurrentQueueDepth <= long.MaxValue
                    ? (long)current.CurrentQueueDepth
                    : null,
                false,
                null,
                resets.Count == 0 ? "none" : string.Join(",", resets));
        }

        /// <summary>Unsigned delta, or <see langword="null"/> when the counter decreased.</summary>
        /// <param name="previous">Earlier cumulative value.</param>
        /// <param name="current">Later cumulative value.</param>
        /// <param name="name">Reset name recorded when the counter decreases.</param>
        /// <param name="resets">Reset names for this interval.</param>
        /// <returns>The increase, or <see langword="null"/> after recording <paramref name="name"/>.</returns>
        private static ulong? Delta(ulong previous, ulong current, string name, List<string> resets)
        {
            if (current >= previous)
            {
                return current - previous;
            }

            resets.Add(name);
            return null;
        }

        /// <summary>
        /// Operation-count delta. A decrease is a 32-bit wrap only for Windows counts whose byte counter
        /// kept moving and whose previous value is near <see cref="uint.MaxValue"/>.
        /// </summary>
        private static ulong? OperationDelta(
            BlockDeviceCounters previous,
            BlockDeviceCounters current,
            ulong previousOps,
            ulong currentOps,
            bool bytesReset,
            string name,
            List<string> resets)
        {
            if (currentOps >= previousOps)
            {
                return currentOps - previousOps;
            }

            if (!bytesReset
                && previous.OperationCountsAre32Bit
                && current.OperationCountsAre32Bit
                && previousOps <= uint.MaxValue
                && currentOps <= uint.MaxValue
                && previousOps >= 0xC000_0000UL
                && currentOps <= 0x3FFF_FFFFUL)
            {
                return (uint)currentOps - (uint)previousOps;
            }

            resets.Add(name);
            return null;
        }

        /// <summary>Average response time in milliseconds, or <see langword="null"/> when it cannot be formed.</summary>
        private static double? Latency(
            bool previousAvailable,
            bool currentAvailable,
            ulong previousMicroseconds,
            ulong currentMicroseconds,
            ulong? operations,
            string resetName,
            List<string> resets)
        {
            if (!previousAvailable || !currentAvailable || operations is not > 0)
            {
                return null;
            }

            var busy = Delta(previousMicroseconds, currentMicroseconds, resetName, resets);
            if (busy is not ulong microseconds)
            {
                return null;
            }

            var milliseconds = microseconds / (double)operations.Value / 1000d;
            return double.IsFinite(milliseconds) ? milliseconds : null;
        }

        /// <summary>
        /// Active-time percent. Windows uses idle time over the driver's query-time delta.
        /// Linux uses <c>io_ticks</c> over the monitor interval. Values outside 0–100 are preserved.
        /// </summary>
        private static double? ActivePercent(
            BlockDeviceCounters previous,
            BlockDeviceCounters current,
            TimeSpan elapsed,
            List<string> resets)
        {
            if (previous.IdleTimeAvailable && current.IdleTimeAvailable)
            {
                var idle = Delta(previous.IdleMicroseconds, current.IdleMicroseconds, "idleTime", resets);
                var query = Delta(previous.QueryTimeMicroseconds, current.QueryTimeMicroseconds, "queryTime", resets);
                if (idle is not ulong idleUs || query is not ulong queryUs || queryUs == 0)
                {
                    return null;
                }

                var percent = (queryUs - (double)idleUs) / queryUs * 100d;
                return double.IsFinite(percent) ? percent : null;
            }

            if (previous.DeviceBusyTimeAvailable && current.DeviceBusyTimeAvailable)
            {
                var busy = Delta(previous.DeviceBusyMicroseconds, current.DeviceBusyMicroseconds, "deviceBusyTime", resets);
                if (busy is not ulong busyUs || elapsed.TotalMilliseconds <= 0)
                {
                    return null;
                }

                var percent = busyUs / 1000d / elapsed.TotalMilliseconds * 100d;
                return double.IsFinite(percent) ? percent : null;
            }

            return null;
        }

        /// <summary>Linux average outstanding I/O. Windows has no weighted queue time in <c>DISK_PERFORMANCE</c>.</summary>
        private static double? AverageQueue(
            BlockDeviceCounters previous,
            BlockDeviceCounters current,
            double seconds,
            List<string> resets)
        {
            if (!previous.WeightedQueueAvailable || !current.WeightedQueueAvailable)
            {
                return null;
            }

            var weighted = Delta(previous.WeightedQueueMicroseconds, current.WeightedQueueMicroseconds, "weightedQueueTime", resets);
            if (weighted is not ulong microseconds)
            {
                return null;
            }

            var depth = microseconds / 1_000_000d / seconds;
            return double.IsFinite(depth) ? depth : null;
        }

        private static long? BytesPerSecond(ulong? delta, double seconds) =>
            delta is ulong value ? (long)(value / seconds) : null;

        private static double? PerSecond(ulong? delta, double seconds) =>
            delta is ulong value ? value / seconds : null;
    }
}
