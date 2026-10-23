namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>One filesystem capacity sample. Available bytes may have been reduced by commits since the probe.</summary>
    /// <param name="TotalBytes">Capacity reported by the probe.</param>
    /// <param name="AvailableBytes">Available bytes after later committed-write adjustments, never below zero.</param>
    /// <param name="ProbeTimestamp">Monotonic timestamp of the probe that produced this entry.</param>
    internal readonly record struct SpoolSpaceObservation(long TotalBytes, long AvailableBytes, long ProbeTimestamp);

    /// <summary>
    /// Immutable view of the latest device rates and free-space samples.
    /// Replacing the reference publishes a consistent set. A reader does not observe a partial update.
    /// </summary>
    /// <param name="Timestamp">Monotonic time when this view was published.</param>
    /// <param name="Devices">Latest rate sample per device. Absent devices were not measured.</param>
    /// <param name="UnavailableDevices">Devices whose latest counter read failed or that disappeared.</param>
    /// <param name="Space">Latest free-space sample per spool path.</param>
    internal sealed record SpoolObservationSnapshot(
        long Timestamp,
        IReadOnlyDictionary<string, BlockDeviceMonitorService.PublishedSample> Devices,
        IReadOnlySet<string> UnavailableDevices,
        IReadOnlyDictionary<string, SpoolSpaceObservation> Space)
    {
        /// <summary>Empty snapshot used before the first sample.</summary>
        internal static SpoolObservationSnapshot Empty { get; } = new(
            0,
            new Dictionary<string, BlockDeviceMonitorService.PublishedSample>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, SpoolSpaceObservation>(StringComparer.Ordinal));
    }
}
