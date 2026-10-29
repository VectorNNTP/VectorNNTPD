namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>
    /// Cluster-wide Transit inbound-connection membership. Redis is the production
    /// authority; tests use an in-memory store that runs the same algorithm.
    /// Admit, release, and renew each use one atomic operation.
    /// </summary>
    public interface ITransitPeerStateStore
    {
        /// <summary>
        /// Atomically admits one inbound connection for this owner when the cluster
        /// count is below <paramref name="maxIncoming"/>.
        /// </summary>
        ValueTask<TransitPeerStateAdmitResult> TryAdmitAsync(
            string identifier,
            string ownerId,
            int maxIncoming,
            long generation,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically decrements this owner's inbound count when the generation matches.
        /// </summary>
        ValueTask ReleaseAsync(
            string identifier,
            string ownerId,
            long generation,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically refreshes this owner's inbound lease. Missing, expired, or
        /// generation-mismatched ownership is not recreated.
        /// </summary>
        ValueTask<TransitPeerStateRenewStatus> RenewAsync(
            string identifier,
            string ownerId,
            long generation,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default);

        /// <summary>Deletes leftover ownership for this process incarnation on this peer.</summary>
        ValueTask ReleaseOwnerAsync(
            string identifier,
            string ownerId,
            CancellationToken cancellationToken = default);
    }
}
