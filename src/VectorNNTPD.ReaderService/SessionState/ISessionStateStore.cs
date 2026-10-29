namespace VectorNNTPD.ReaderService.SessionState
{
    /// <summary>
    /// Cluster-wide account session and source-IP membership. Redis is the production
    /// authority; tests use an in-memory store that runs the same algorithm.
    /// Admit, release, and renew each use one atomic operation.
    /// </summary>
    public interface ISessionStateStore
    {
        /// <summary>
        /// Atomically admits one session and this owner's source-IP membership.
        /// </summary>
        ValueTask<SessionStateAdmitResult> TryAdmitAsync(
            string accountName,
            string normalizedSourceIp,
            string ownerId,
            int sessionLimit,
            int srcIpLimit,
            long sessionGeneration,
            long sourceGeneration,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default,
            bool trackSessions = false);

        /// <summary>
        /// Atomically decrements this owner's session count and source-IP count.
        /// Returns the remaining cluster session total. Unavailable membership
        /// returns <c>0</c>; callers must not treat that as a live total.
        /// </summary>
        ValueTask<int> ReleaseAsync(
            string accountName,
            string normalizedSourceIp,
            string ownerId,
            long sessionGeneration,
            long sourceGeneration,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically refreshes this owner's session and source-IP leases.
        /// All requested fields are extended or none are. Missing, expired, or
        /// generation-mismatched ownership is not recreated.
        /// </summary>
        ValueTask<SessionStateRenewResult> RenewAsync(
            string accountName,
            string ownerId,
            long sessionGeneration,
            IReadOnlyList<(string Ip, long Generation)> sources,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically refreshes this owner's session/source leases and APPLYs one
        /// MySQL-committed byte batch for the same account. APPLY runs even when
        /// renewal is lost. <see cref="SessionStateRenewAndApplyResult.Remaining"/>
        /// is <see langword="null"/> when this store cannot apply byte state.
        /// </summary>
        ValueTask<SessionStateRenewAndApplyResult> RenewAndApplyAsync(
            string accountName,
            string ownerId,
            long sessionGeneration,
            IReadOnlyList<(string Ip, long Generation)> sources,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            string batchId,
            long consumed,
            long mysqlRemainingAfter,
            CancellationToken cancellationToken = default);

        /// <summary>Deletes leftover ownership for this process incarnation.</summary>
        ValueTask ReleaseOwnerAsync(
            string accountName,
            string ownerId,
            CancellationToken cancellationToken = default);
    }
}
