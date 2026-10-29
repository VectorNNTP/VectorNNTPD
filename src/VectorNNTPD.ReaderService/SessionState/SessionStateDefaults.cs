namespace VectorNNTPD.ReaderService.SessionState
{
    /// <summary>Lease and hot-path constants for cluster-wide session and source-IP admission.</summary>
    /// <remarks>
    /// The Redis TTL is crash/failure recovery only. It is not an idle-session
    /// timeout. A healthy node renews every 10 seconds while authenticated sessions
    /// exist, so ownership stays alive indefinitely even when clients issue no
    /// commands. A 30-second lease recovers crashed-node ownership quickly without
    /// treating a brief Redis blip as membership loss. Two missed heartbeats expire
    /// the lease. The 2-second hot-path skew refuses a local fast-path decision that
    /// would outlive the distributed lease.
    /// </remarks>
    internal static class SessionStateDefaults
    {
        /// <summary>Distributed ownership TTL written into Redis on admit and renew.</summary>
        public static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Interval between process-wide lease renewal passes.
        /// <see cref="SessionStateService"/> uses the same 10-second period.
        /// </summary>
        public static readonly TimeSpan RenewalPeriod = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Local hot-path requires the known lease to remain valid for at least this long.
        /// </summary>
        public static readonly TimeSpan HotPathSkew = TimeSpan.FromSeconds(2);
    }
}
