namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{

    /// <summary>
    /// One spool as observed for a single selection. Null measurements were not supplied.
    /// A zero measurement was measured and was zero. Load is not a write-eligibility flag.
    /// </summary>
    /// <param name="SpoolRoot">Canonical spool directory.</param>
    /// <param name="DeviceId">Physical device identity from validation.</param>
    /// <param name="TotalBytes">Filesystem capacity. Meaningful when <paramref name="SpaceKnown"/> is true.</param>
    /// <param name="AvailableBytes">Filesystem bytes available to the caller. Meaningful when <paramref name="SpaceKnown"/> is true.</param>
    /// <param name="SpaceKnown">Whether total and available bytes are a fresh, consistent probe. False is not zero free space.</param>
    /// <param name="Initialized">Whether shard initialization completed for this spool.</param>
    /// <param name="AcceptsWrites">Whether a genuine operational failure or a missing mount is not currently blocking writes.</param>
    /// <param name="DeviceSampleFresh">Whether device rates in this value come from a fresh monitor sample.</param>
    /// <param name="ActiveTimePercent">Percent of the interval with outstanding I/O. Not CPU I/O wait.</param>
    /// <param name="WriteLatencyMilliseconds">Average OS write response time. Not seek time.</param>
    /// <param name="AverageQueueDepth">Average outstanding I/O. Unavailable on Windows stays null.</param>
    /// <param name="CurrentQueueDepth">Outstanding requests at the sample. Not an average.</param>
    /// <param name="InFlightWrites">Publishes this process has started on the spool and not finished.</param>
    /// <param name="InFlightLimit">Per-spool publish cap. A spool at the cap is not selected for another publish.</param>
    internal readonly record struct SpoolCandidate(
        string SpoolRoot,
        string DeviceId,
        long TotalBytes,
        long AvailableBytes,
        bool SpaceKnown,
        bool Initialized,
        bool AcceptsWrites,
        bool DeviceSampleFresh,
        double? ActiveTimePercent,
        double? WriteLatencyMilliseconds,
        double? AverageQueueDepth,
        long? CurrentQueueDepth,
        int InFlightWrites,
        int InFlightLimit);
}
