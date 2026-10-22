namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Parses Linux <c>/proc/diskstats</c>. Sector counts use the kernel's 512-byte sector.
    /// Read and write time fields are milliseconds spent completing those requests, not CPU I/O wait and not seek time.
    /// <c>io_ticks</c> is milliseconds during which at least one I/O was in flight.
    /// <c>time_in_queue</c> is weighted so its interval delta divided by the interval is average outstanding I/O.
    /// <c>in_flight</c> is the current outstanding count, not an average.
    /// A controller identity such as <c>nvme0</c> sums whole-namespace rows. Partition rows are not added to a disk that already has its own row.
    /// Active time is left unavailable when more than one namespace matches, because adding <c>io_ticks</c> would count parallel time twice.
    /// </summary>
    internal static class LinuxDiskstats
    {
        /// <summary>
        /// Finds the row or rows for <paramref name="deviceName"/> and converts their counters.
        /// </summary>
        /// <param name="diskstats">Raw diskstats text.</param>
        /// <param name="deviceName">Physical identity without the <c>block:</c> prefix, such as <c>sda</c> or <c>nvme0</c>.</param>
        /// <param name="counters">Parsed counters when the method returns <see langword="true"/>.</param>
        /// <param name="error">Diagnostic when the device is absent or a matching line is malformed.</param>
        /// <returns><see langword="true"/> when at least one matching row was parsed.</returns>
        internal static bool TryParse(string diskstats, string deviceName, out BlockDeviceCounters counters, out string? error)
        {
            counters = default;
            error = null;
            ulong readBytes = 0;
            ulong writeBytes = 0;
            ulong reads = 0;
            ulong writes = 0;
            ulong readUs = 0;
            ulong writeUs = 0;
            ulong busyUs = 0;
            ulong weightedUs = 0;
            ulong inFlight = 0;
            var matches = 0;
            var busyKnown = true;
            var weightedKnown = true;
            var inFlightKnown = true;
            foreach (var line in diskstats.Split('\n'))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3 || !Matches(fields[2], deviceName))
                {
                    continue;
                }

                if (fields.Length < 11
                    || !ulong.TryParse(fields[3], out var readOps)
                    || !ulong.TryParse(fields[5], out var sectorsRead)
                    || !ulong.TryParse(fields[6], out var readMs)
                    || !ulong.TryParse(fields[7], out var writeOps)
                    || !ulong.TryParse(fields[9], out var sectorsWritten)
                    || !ulong.TryParse(fields[10], out var writeMs))
                {
                    error = "diskstats line for '" + fields[2] + "' is malformed.";
                    return false;
                }

                matches++;
                readBytes += sectorsRead * 512;
                writeBytes += sectorsWritten * 512;
                reads += readOps;
                writes += writeOps;
                readUs += readMs * 1000;
                writeUs += writeMs * 1000;
                if (fields.Length < 12 || !ulong.TryParse(fields[11], out var flying))
                {
                    inFlightKnown = false;
                }
                else
                {
                    inFlight += flying;
                }

                if (fields.Length < 13 || !ulong.TryParse(fields[12], out var ioTicks))
                {
                    busyKnown = false;
                }
                else
                {
                    busyUs += ioTicks * 1000;
                }

                if (fields.Length < 14 || !ulong.TryParse(fields[13], out var weighted))
                {
                    weightedKnown = false;
                }
                else
                {
                    weightedUs += weighted * 1000;
                }
            }

            if (matches == 0)
            {
                error = "diskstats has no device '" + deviceName + "'.";
                return false;
            }

            var activeKnown = busyKnown && matches == 1;
            counters = new BlockDeviceCounters(
                ReadBytes: readBytes,
                WriteBytes: writeBytes,
                ReadOperations: reads,
                WriteOperations: writes,
                ReadBusyMicroseconds: readUs,
                WriteBusyMicroseconds: writeUs,
                LatencyAvailable: true,
                DeviceBusyTimeAvailable: activeKnown,
                DeviceBusyMicroseconds: activeKnown ? busyUs : 0,
                WeightedQueueAvailable: weightedKnown,
                WeightedQueueMicroseconds: weightedKnown ? weightedUs : 0,
                CurrentQueueAvailable: inFlightKnown,
                CurrentQueueDepth: inFlightKnown ? inFlight : 0);
            return true;
        }

        /// <summary>
        /// A diskstats name belongs to the physical identity when it is that name, or when it is a whole NVMe namespace of that controller.
        /// Partition names such as <c>sda1</c> and <c>nvme0n1p1</c> do not match.
        /// </summary>
        /// <param name="diskstatsName">Name in field 3 of a diskstats row.</param>
        /// <param name="deviceName">Physical identity name.</param>
        /// <returns><see langword="true"/> when the row should be included.</returns>
        private static bool Matches(string diskstatsName, string deviceName)
        {
            if (string.Equals(diskstatsName, deviceName, StringComparison.Ordinal))
            {
                return true;
            }

            return LinuxBlockDeviceIdentity.TryNvmeController(diskstatsName, out var controller)
                && string.Equals(controller, deviceName, StringComparison.Ordinal);
        }
    }
}
