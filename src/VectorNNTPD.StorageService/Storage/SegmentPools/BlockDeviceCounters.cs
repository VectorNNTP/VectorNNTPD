namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Cumulative block-device counters plus gauges that are meaningful only at the sample instant.
    /// Throughput, IOPS, latency, active time, and average queue depth are derived from successive samples.
    /// A flag that is false means that measurement was not supplied. A zero counter is not a substitute.
    /// </summary>
    /// <param name="ReadBytes">Cumulative bytes read.</param>
    /// <param name="WriteBytes">Cumulative bytes written.</param>
    /// <param name="ReadOperations">Cumulative completed read operations.</param>
    /// <param name="WriteOperations">Cumulative completed write operations.</param>
    /// <param name="ReadBusyMicroseconds">Cumulative read response time in microseconds. Ignored when <paramref name="LatencyAvailable"/> is false.</param>
    /// <param name="WriteBusyMicroseconds">Cumulative write response time in microseconds. Ignored when <paramref name="LatencyAvailable"/> is false.</param>
    /// <param name="LatencyAvailable">Whether the read and write response-time fields were supplied. False is not zero latency.</param>
    /// <param name="DeviceBusyTimeAvailable">Whether <paramref name="DeviceBusyMicroseconds"/> is Linux <c>io_ticks</c>. False is not zero active time.</param>
    /// <param name="DeviceBusyMicroseconds">Linux time with at least one I/O in flight, in microseconds.</param>
    /// <param name="IdleTimeAvailable">Whether Windows idle time and query time were supplied.</param>
    /// <param name="IdleMicroseconds">Windows <c>DISK_PERFORMANCE.IdleTime</c> converted from 100-nanosecond units to microseconds.</param>
    /// <param name="QueryTimeMicroseconds">Windows <c>DISK_PERFORMANCE.QueryTime</c> converted from 100-nanosecond units to microseconds.</param>
    /// <param name="WeightedQueueAvailable">Whether <paramref name="WeightedQueueMicroseconds"/> is Linux <c>time_in_queue</c>.</param>
    /// <param name="WeightedQueueMicroseconds">Linux weighted I/O time in microseconds. Dividing the interval delta by the interval yields average outstanding I/O.</param>
    /// <param name="CurrentQueueAvailable">Whether <paramref name="CurrentQueueDepth"/> was supplied for this sample.</param>
    /// <param name="CurrentQueueDepth">Requests outstanding at the sample, not an average. Linux <c>in_flight</c> or Windows <c>QueueDepth</c>.</param>
    /// <param name="OperationCountsAre32Bit">Whether operation counts can wrap at 2^32. Windows <c>ReadCount</c> and <c>WriteCount</c> are 32-bit. Linux diskstats counts are not.</param>
    internal readonly record struct BlockDeviceCounters(
        ulong ReadBytes,
        ulong WriteBytes,
        ulong ReadOperations,
        ulong WriteOperations,
        ulong ReadBusyMicroseconds,
        ulong WriteBusyMicroseconds,
        bool LatencyAvailable,
        bool DeviceBusyTimeAvailable = false,
        ulong DeviceBusyMicroseconds = 0,
        bool IdleTimeAvailable = false,
        ulong IdleMicroseconds = 0,
        ulong QueryTimeMicroseconds = 0,
        bool WeightedQueueAvailable = false,
        ulong WeightedQueueMicroseconds = 0,
        bool CurrentQueueAvailable = false,
        ulong CurrentQueueDepth = 0,
        bool OperationCountsAre32Bit = false);
}
