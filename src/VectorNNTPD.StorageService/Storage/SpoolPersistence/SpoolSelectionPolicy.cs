namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{

    /// <summary>
    /// Documented ranking and failure-retry constants for <see cref="SpoolSelector"/>.
    /// <c>CriticalPercent</c> is supplied by the caller and excludes spools above that used percent.
    /// <c>HighPercent</c>/<c>LowPercent</c> drive per-spool eviction monitors, not placement ranking.
    /// </summary>
    internal static class SpoolSelectionPolicy
    {
        /// <summary>
        /// A device sample or free-space probe older than this is not used as a measurement.
        /// A stale or missing free-space sample makes the spool ineligible for placement.
        /// </summary>
        internal static readonly TimeSpan SampleFreshness = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Historical free-space tie fraction. Placement now ranks strictly by available bytes;
        /// this constant remains for tests and documentation of prior tie-band behaviour.
        /// </summary>
        internal const double FreeSpaceTieFraction = 0.01;

        /// <summary>
        /// Historical free-space tie floor (one gibibyte). Placement now ranks strictly by available bytes.
        /// </summary>
        internal const long FreeSpaceTieMinimumBytes = 1L << 30;

        /// <summary>
        /// Active-time percent at or above which a fresh sample ranks as busy.
        /// This is device busy time, not used-space percent and not CPU I/O wait.
        /// Eighty-five means the device had outstanding I/O for most of the interval.
        /// It does not reject the spool.
        /// </summary>
        internal const double BusyActiveTimePercent = 85;

        /// <summary>
        /// Write response time at or above which a fresh sample ranks as busy.
        /// Twenty milliseconds is average OS service time, not seek time.
        /// It does not reject the spool.
        /// </summary>
        internal const double BusyWriteLatencyMilliseconds = 20;

        /// <summary>Current outstanding I/O at or above which a fresh sample ranks as busy. It does not reject the spool.</summary>
        internal const long BusyCurrentQueueDepth = 16;

        /// <summary>Average outstanding I/O at or above which a fresh Linux sample ranks as busy. It does not reject the spool.</summary>
        internal const double BusyAverageQueueDepth = 8;

        /// <summary>
        /// Consecutive access or I/O failures that close a spool until
        /// <see cref="FailureRetry"/> elapses. Disk-full closes on the first failure and
        /// does not use this count. Load does not increment it.
        /// </summary>
        internal const int OperationalFailureCloseCount = 3;

        /// <summary>
        /// How long an access or I/O close lasts before one trial write is allowed.
        /// A disk-full close ignores this and waits for a later probe with available bytes.
        /// </summary>
        internal static readonly TimeSpan FailureRetry = TimeSpan.FromSeconds(30);
    }
}
