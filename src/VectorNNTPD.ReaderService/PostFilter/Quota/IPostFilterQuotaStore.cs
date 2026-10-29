namespace VectorNNTPD.ReaderService.PostFilter.Quota
{
    /// <summary>
    /// Cluster-wide PostFilter accept-quota reservations. Redis is the production
    /// authority; tests use an in-memory store that runs the same algorithm.
    /// Reserve, commit, and release each use one atomic operation.
    /// </summary>
    internal interface IPostFilterQuotaStore
    {
        /// <summary>
        /// Atomically evaluates enabled ceilings and creates one reservation, or writes nothing.
        /// <c>reservationTtlMs</c> of <c>0</c> uses <see cref="PostFilterQuotaDefaults.ReservationTtl"/>.
        /// When SpamAssassin CHECK can run, callers pass
        /// <see cref="PostFilterQuotaDefaults.HoldMilliseconds"/>.
        /// </summary>
        ValueTask<PostFilterQuotaReserveStatus> ReserveAsync(
            string accountName,
            PostFilterReservationId reservation,
            DateTimeOffset now,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings,
            long messages,
            long bytes,
            int mpUnits,
            string? bodyHex,
            CancellationToken cancellationToken = default,
            long reservationTtlMs = 0);

        /// <summary>
        /// Transfers units from the live reservation into the current buckets.
        /// The caller does not supply units.
        /// </summary>
        ValueTask<PostFilterQuotaCommitStatus> CommitAsync(
            string accountName,
            PostFilterReservationId reservation,
            DateTimeOffset now,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings,
            CancellationToken cancellationToken = default);

        /// <summary>Deletes a matching live reservation. Never decrements committed usage.</summary>
        ValueTask<PostFilterQuotaReleaseStatus> ReleaseAsync(
            string accountName,
            PostFilterReservationId reservation,
            DateTimeOffset now,
            CancellationToken cancellationToken = default);
    }
}
