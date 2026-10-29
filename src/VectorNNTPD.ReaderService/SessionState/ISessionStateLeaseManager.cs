namespace VectorNNTPD.ReaderService.SessionState
{
    /// <summary>
    /// Process-local lease renewal and shutdown release for cluster session-state ownership.
    /// </summary>
    /// <remarks>
    /// Renewal proves this node is still responsible for its authenticated sessions.
    /// It is independent of NNTP traffic. One renew operation per account extends
    /// session and source-IP ownership together, or extends none of them.
    /// The background <see cref="SessionStateService"/> is the sole scheduler; this
    /// abstraction owns the actual ownership snapshot and Redis operations. When a
    /// MySQL-committed byte batch exists for an owned account, renewal and APPLY
    /// share one Redis EVAL. A successful renew also returns the cluster session
    /// total so local rate caps can be updated without a second scheduler.
    /// </remarks>
    public interface ISessionStateLeaseManager
    {
        /// <summary>
        /// Refreshes this incarnation's session and source-IP leases for accounts that
        /// still have local authenticated sessions. Does not recreate missing ownership.
        /// </summary>
        ValueTask RenewLeasesAsync(CancellationToken cancellationToken = default);

        /// <summary>Releases leftover node/incarnation ownership without touching the database.</summary>
        ValueTask ReleaseAllOwnershipAsync(CancellationToken cancellationToken = default);
    }
}
