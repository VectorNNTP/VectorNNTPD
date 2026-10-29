namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>
    /// Admits and releases inbound Transit connections against cluster-wide
    /// <c>MaxIncomingConnections</c> for a <see cref="TransitPeerPolicy.Identifier"/>.
    /// </summary>
    /// <remarks>
    /// Redis is authoritative. A local owned-count is diagnostics and lifecycle
    /// bookkeeping only; it never permits admission without distributed authorization.
    /// Source IP is not an identity here. <c>MaxIncomingConnections == 0</c> is closed.
    /// </remarks>
    public interface ITransitPeerStateTracker
    {
        /// <summary>
        /// Attempts to admit one inbound connection for <paramref name="identifier"/>.
        /// </summary>
        ValueTask<TransitPeerStateAdmitResult> TryAdmitAsync(
            string identifier,
            int maxIncoming,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Releases one previously admitted inbound connection for this identifier
        /// and generation. Idempotent when this process has no local ownership.
        /// </summary>
        ValueTask ReleaseAsync(
            string identifier,
            long generation,
            CancellationToken cancellationToken = default);

        /// <summary>Returns the process-local owned count for <paramref name="identifier"/>.</summary>
        int GetLocalCount(string identifier);
    }
}
