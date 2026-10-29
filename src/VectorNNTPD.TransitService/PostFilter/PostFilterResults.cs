using VectorNNTPD.TransitService.PostFilter.Quota;

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>Client-visible PostFilter decision. Moderate is not implemented.</summary>
    public enum PostFilterDecision
    {
        /// <summary>Continue to CreateQueued.</summary>
        Accept = 0,

        /// <summary>Do not queue. Client receives <c>441</c>.</summary>
        Reject = 1,
    }

    /// <summary>Stage that produced the decision.</summary>
    public enum PostFilterStage
    {
        /// <summary>No remaining stage ran (disabled gate).</summary>
        Gate = 0,

        /// <summary>Account or client-IP deny.</summary>
        Deny = 1,

        /// <summary>ArtType policy mask.</summary>
        ArtType = 2,

        /// <summary>Redis accept-quota reservation.</summary>
        Quota = 3,

        /// <summary>SpamAssassin CHECK.</summary>
        SpamAssassin = 4,

        /// <summary>Accepted after all enabled stages.</summary>
        Complete = 5,

        /// <summary>Queue admission after an Accept decision (<c>TryAdmit</c>).</summary>
        Admission = 6,
    }

    /// <summary>Lease held after a successful RESERVE. POST owns commit/release.</summary>
    public sealed class PostFilterLease
    {
        private readonly IPostFilterQuotaStore _store;

        /// <summary>Initializes a live reservation lease.</summary>
        internal PostFilterLease(
            IPostFilterQuotaStore store,
            string accountName,
            PostFilterReservationId reservation,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            _store = store;
            AccountName = accountName;
            Reservation = reservation;
            Windows = windows;
            Ceilings = ceilings;
        }

        /// <summary>Gets the authenticated account key.</summary>
        public string AccountName { get; }

        /// <summary>Gets the reservation token for logs.</summary>
        public string Token => Reservation.Token;

        /// <summary>Gets the reservation identity.</summary>
        internal PostFilterReservationId Reservation { get; }

        /// <summary>Gets the windows used at reserve time.</summary>
        internal PostFilterQuotaWindows Windows { get; }

        /// <summary>Gets the ceilings used at reserve time.</summary>
        internal PostFilterQuotaCeilings Ceilings { get; }

        /// <summary>COMMITs reserved units into the current buckets.</summary>
        internal ValueTask<PostFilterQuotaCommitStatus> CommitAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            _store.CommitAsync(AccountName, Reservation, now, Windows, Ceilings, cancellationToken);

        /// <summary>RELEASEs the reservation. Never decrements committed usage.</summary>
        internal ValueTask<PostFilterQuotaReleaseStatus> ReleaseAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            _store.ReleaseAsync(AccountName, Reservation, now, cancellationToken);
    }

    /// <summary>PostFilter evaluation outcome.</summary>
    public readonly struct PostFilterResult
    {
        /// <summary>Initializes a result.</summary>
        public PostFilterResult(
            PostFilterDecision decision,
            PostFilterStage stage,
            string reason,
            PostFilterLease? lease)
            : this(decision, stage, reason, lease, quotaStatus: null)
        {
        }

        /// <summary>Initializes a result with a quota reserve status.</summary>
        internal PostFilterResult(
            PostFilterDecision decision,
            PostFilterStage stage,
            string reason,
            PostFilterLease? lease,
            PostFilterQuotaReserveStatus? quotaStatus)
            : this(
                decision,
                stage,
                reason,
                lease,
                quotaStatus,
                policyRevision: 0,
                spamAssassinStatus: null,
                spamAssassinScore: null,
                spamAssassinThreshold: null)
        {
        }

        /// <summary>Initializes a result with policy revision and optional SA metadata.</summary>
        internal PostFilterResult(
            PostFilterDecision decision,
            PostFilterStage stage,
            string reason,
            PostFilterLease? lease,
            PostFilterQuotaReserveStatus? quotaStatus,
            long policyRevision,
            PostFilterSpamAssassinStatus? spamAssassinStatus,
            decimal? spamAssassinScore,
            decimal? spamAssassinThreshold)
        {
            Decision = decision;
            Stage = stage;
            Reason = reason;
            Lease = lease;
            QuotaStatus = quotaStatus;
            PolicyRevision = policyRevision;
            SpamAssassinStatus = spamAssassinStatus;
            SpamAssassinScore = spamAssassinScore;
            SpamAssassinThreshold = spamAssassinThreshold;
        }

        /// <summary>Gets Accept or Reject.</summary>
        public PostFilterDecision Decision { get; }

        /// <summary>Gets the decisive stage.</summary>
        public PostFilterStage Stage { get; }

        /// <summary>Gets an internal reason (never sent on the wire).</summary>
        public string Reason { get; }

        /// <summary>Gets the reservation lease when Accept reserved quota.</summary>
        public PostFilterLease? Lease { get; }

        /// <summary>Gets the RESERVE status when the quota stage ran.</summary>
        internal PostFilterQuotaReserveStatus? QuotaStatus { get; }

        /// <summary>Gets the published policy revision observed for this decision.</summary>
        public long PolicyRevision { get; }

        /// <summary>Gets the SA status when the SpamAssassin stage produced the decision.</summary>
        internal PostFilterSpamAssassinStatus? SpamAssassinStatus { get; }

        /// <summary>Gets the SA score when the Spam header included one.</summary>
        public decimal? SpamAssassinScore { get; }

        /// <summary>Gets the SA threshold when the Spam header included one.</summary>
        public decimal? SpamAssassinThreshold { get; }

        /// <summary>Accept with no reservation (disabled gate).</summary>
        public static PostFilterResult AcceptedDisabled(long policyRevision = 0) =>
            new(
                PostFilterDecision.Accept,
                PostFilterStage.Gate,
                "disabled",
                lease: null,
                quotaStatus: null,
                policyRevision,
                spamAssassinStatus: null,
                spamAssassinScore: null,
                spamAssassinThreshold: null);

        /// <summary>Accept after enabled stages.</summary>
        public static PostFilterResult Accepted(PostFilterLease? lease, long policyRevision = 0) =>
            new(
                PostFilterDecision.Accept,
                PostFilterStage.Complete,
                "accepted",
                lease,
                quotaStatus: null,
                policyRevision,
                spamAssassinStatus: null,
                spamAssassinScore: null,
                spamAssassinThreshold: null);

        /// <summary>Reject.</summary>
        public static PostFilterResult Rejected(PostFilterStage stage, string reason, long policyRevision = 0) =>
            new(
                PostFilterDecision.Reject,
                stage,
                reason,
                lease: null,
                quotaStatus: null,
                policyRevision,
                spamAssassinStatus: null,
                spamAssassinScore: null,
                spamAssassinThreshold: null);

        /// <summary>Reject with a quota reserve status.</summary>
        internal static PostFilterResult Rejected(
            PostFilterStage stage,
            string reason,
            PostFilterQuotaReserveStatus? quotaStatus,
            long policyRevision = 0) =>
            new(
                PostFilterDecision.Reject,
                stage,
                reason,
                lease: null,
                quotaStatus,
                policyRevision,
                spamAssassinStatus: null,
                spamAssassinScore: null,
                spamAssassinThreshold: null);

        /// <summary>Reject after SpamAssassin CHECK.</summary>
        internal static PostFilterResult RejectedSpamAssassin(
            string reason,
            long policyRevision,
            PostFilterSpamAssassinResult scan) =>
            new(
                PostFilterDecision.Reject,
                PostFilterStage.SpamAssassin,
                reason,
                lease: null,
                quotaStatus: null,
                policyRevision,
                scan.Status,
                scan.Score,
                scan.Threshold);
    }
}
