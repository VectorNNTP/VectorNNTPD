namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// Process-local lease renewal and shutdown release for Transit inbound ownership.
    /// </summary>
    /// <remarks>
    /// Renewal proves this node is still responsible for its admitted Transit
    /// connections. It is independent of NNTP traffic, article transfers, CHECK,
    /// TAKETHIS, commands, bytes transferred, and idle state.
    /// <see cref="TransitPeerStateService"/> is the scheduler; this abstraction
    /// owns the ownership snapshot and Redis operations.
    /// </remarks>
    public interface ITransitPeerStateLeaseManager
    {
        /// <summary>
        /// Refreshes this incarnation's inbound leases for peers that still have
        /// local admitted connections. Does not recreate missing ownership.
        /// </summary>
        ValueTask RenewLeasesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Releases leftover node/incarnation ownership. After this call, new
        /// admissions on this tracker fail closed so shutdown cannot race ReleaseOwner
        /// with a later TRY_ADMIT.
        /// </summary>
        ValueTask ReleaseAllOwnershipAsync(CancellationToken cancellationToken = default);
    }
}
