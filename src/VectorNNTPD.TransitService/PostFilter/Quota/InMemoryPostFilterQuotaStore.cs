using System.Text;

namespace VectorNNTPD.TransitService.PostFilter.Quota
{
    /// <summary>
    /// Process-local cluster fake for accept-quota. Two stores sharing one engine
    /// behave as two NNTP nodes. Ordinary unit tests must not require live Redis.
    /// </summary>
    internal sealed class InMemoryPostFilterQuotaStore : IPostFilterQuotaStore
    {
        private readonly PostFilterQuotaEngine _engine;

        /// <summary>Initializes a store with a dedicated engine.</summary>
        public InMemoryPostFilterQuotaStore()
            : this(new PostFilterQuotaEngine())
        {
        }

        /// <summary>Initializes a store that shares <paramref name="engine"/> with other fakes.</summary>
        public InMemoryPostFilterQuotaStore(PostFilterQuotaEngine engine)
        {
            ArgumentNullException.ThrowIfNull(engine);
            _engine = engine;
        }

        /// <summary>When set, operations fail closed as if Redis were down.</summary>
        public bool Unavailable { get; set; }

        /// <summary>Gets the shared algorithm (tests).</summary>
        internal PostFilterQuotaEngine Engine => _engine;

        /// <inheritdoc />
        public ValueTask<PostFilterQuotaReserveStatus> ReserveAsync(
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
            long reservationTtlMs = 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                return ValueTask.FromResult(PostFilterQuotaReserveStatus.Unavailable);
            }

            var ttl = reservationTtlMs > 0
                ? reservationTtlMs
                : (long)PostFilterQuotaDefaults.ReservationTtl.TotalMilliseconds;
            var code = _engine.Reserve(
                QuotaKey(accountName),
                MultipostKey(accountName),
                reservation,
                now.ToUnixTimeMilliseconds(),
                ttl,
                windows,
                ceilings,
                messages,
                bytes,
                mpUnits,
                bodyHex ?? string.Empty);
            return ValueTask.FromResult((PostFilterQuotaReserveStatus)code);
        }

        /// <inheritdoc />
        public ValueTask<PostFilterQuotaCommitStatus> CommitAsync(
            string accountName,
            PostFilterReservationId reservation,
            DateTimeOffset now,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                return ValueTask.FromResult(PostFilterQuotaCommitStatus.Unavailable);
            }

            var code = _engine.Commit(
                QuotaKey(accountName),
                MultipostKey(accountName),
                reservation,
                now.ToUnixTimeMilliseconds(),
                windows,
                ceilings);
            return ValueTask.FromResult((PostFilterQuotaCommitStatus)code);
        }

        /// <inheritdoc />
        public ValueTask<PostFilterQuotaReleaseStatus> ReleaseAsync(
            string accountName,
            PostFilterReservationId reservation,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                return ValueTask.FromResult(PostFilterQuotaReleaseStatus.Unavailable);
            }

            var code = _engine.Release(
                QuotaKey(accountName),
                MultipostKey(accountName),
                reservation,
                now.ToUnixTimeMilliseconds());
            return ValueTask.FromResult((PostFilterQuotaReleaseStatus)code);
        }

        /// <summary>In-memory quota hash identity matching the Redis key bytes.</summary>
        internal static string QuotaKey(string accountName) =>
            Encoding.ASCII.GetString(PostFilterQuotaKeys.CreateQuota(accountName));

        /// <summary>In-memory multipost hash identity matching the Redis key bytes.</summary>
        internal static string MultipostKey(string accountName) =>
            Encoding.ASCII.GetString(PostFilterQuotaKeys.CreateMultipost(accountName));
    }
}
