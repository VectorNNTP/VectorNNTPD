namespace VectorNNTPD.StorageService.Storage.SegmentPools
{
    /// <summary>
    /// Immutable view of one configured spool's capacity and eviction posture.
    /// Percentages are never aggregated across unrelated filesystems.
    /// </summary>
    /// <param name="SpoolDirectory">Canonical spool root.</param>
    /// <param name="DeviceId">Physical device identity from validation (shared when directories share a volume).</param>
    /// <param name="TotalBytes">Filesystem capacity when <see cref="MeasurementValid"/>.</param>
    /// <param name="AvailableBytes">Filesystem available bytes when <see cref="MeasurementValid"/>.</param>
    /// <param name="UsedPercent">Derived used percent when <see cref="MeasurementValid"/>.</param>
    /// <param name="MeasurementValid">Whether the latest sample is present, consistent, and fresh enough for placement.</param>
    /// <param name="ProbeTimestamp">Monotonic probe time of the sample, or zero when unknown.</param>
    /// <param name="PlacementEligible">Valid measurement and used percent at or below Critical (strict greater-than blocks).</param>
    /// <param name="EvictionRequired">Valid measurement at or above HighPercent.</param>
    /// <param name="EvictionActive">Whether this spool's eviction monitor is currently reclaiming.</param>
    internal readonly record struct SpoolCapacityState(
        string SpoolDirectory,
        string DeviceId,
        long TotalBytes,
        long AvailableBytes,
        int UsedPercent,
        bool MeasurementValid,
        long ProbeTimestamp,
        bool PlacementEligible,
        bool EvictionRequired,
        bool EvictionActive);
}
