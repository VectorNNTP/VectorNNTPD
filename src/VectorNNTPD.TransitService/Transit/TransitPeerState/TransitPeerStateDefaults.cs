namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>Lease timings for cluster-wide Transit inbound connection ownership.</summary>
    /// <remarks>
    /// These match the established SessionState operational values (30s lease, 10s
    /// renewal, 2s hot-path skew) without introducing Transit-specific configuration.
    /// Redis expiry is crash/failure recovery only. It is not a Transit idle timeout.
    /// A healthy node renews every 10 seconds while it still owns inbound connections,
    /// independently of NNTP traffic. Transit has no local admit hot path: every named
    /// peer requires distributed authorization. <c>MaxIncomingConnections == 0</c>
    /// means closed, not unlimited.
    /// </remarks>
    internal static class TransitPeerStateDefaults
    {
        /// <summary>Distributed ownership TTL written into Redis on admit and renew.</summary>
        public static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Interval between process-wide lease renewal passes.
        /// <see cref="TransitPeerStateService"/> uses the same 10-second period.
        /// </summary>
        public static readonly TimeSpan RenewalPeriod = TimeSpan.FromSeconds(10);

        /// <summary>
        /// SessionState hot-path skew. Transit does not admit without Redis; the
        /// constant is retained so operational timing stays aligned.
        /// </summary>
        public static readonly TimeSpan HotPathSkew = TimeSpan.FromSeconds(2);
    }
}
