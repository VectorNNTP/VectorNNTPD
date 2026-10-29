using System.Globalization;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.PostFilter.Quota;

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>POST-only PostFilter pipeline. Does not call CreateQueued or History.</summary>
    internal sealed class PostFilterEvaluator : IPostFilter
    {
        private readonly IPostFilterPolicySource _policy;
        private readonly IPostFilterQuotaStore _quota;
        private readonly PostFilterReservationIdentity _identity;
        private readonly IPostFilterSpamAssassin _spamAssassin;
        private readonly ILogger<PostFilterEvaluator> _logger;

        /// <summary>Initializes the evaluator.</summary>
        public PostFilterEvaluator(
            IPostFilterPolicySource policy,
            IPostFilterQuotaStore quota,
            PostFilterReservationIdentity identity,
            IPostFilterSpamAssassin spamAssassin,
            ILogger<PostFilterEvaluator> logger)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(quota);
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(spamAssassin);
            ArgumentNullException.ThrowIfNull(logger);
            _policy = policy;
            _quota = quota;
            _identity = identity;
            _spamAssassin = spamAssassin;
            _logger = logger;
        }

        /// <inheritdoc />
        public async ValueTask<PostFilterResult> EvaluateAsync(
            PostFilterRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _policy.Current;
            var account = request.AccountName ?? string.Empty;
            var artId = FormatArtId(request.Article);

            if (snapshot.Gate == PostFilterGateState.Disabled)
            {
                PostFilterLogMessages.Accepted(_logger, PostFilterStage.Gate, account, artId, reserved: false);
                return PostFilterResult.AcceptedDisabled(snapshot.Revision);
            }

            if (snapshot.Gate == PostFilterGateState.Closed)
            {
                return Reject(snapshot, PostFilterStage.Gate, "closed", account, artId);
            }

            if (IsDenied(snapshot, request))
            {
                return Reject(snapshot, PostFilterStage.Deny, "denied", account, artId);
            }

            if (snapshot.RejectArtTypes != ArticleType.None
                && (request.Article.ArtType & snapshot.RejectArtTypes) != 0)
            {
                return Reject(snapshot, PostFilterStage.ArtType, "arttype", account, artId);
            }

            var allowlisted = IsAllowlisted(snapshot, request);
            if (string.IsNullOrWhiteSpace(request.AccountName))
            {
                return Reject(snapshot, PostFilterStage.Quota, "unauthenticated", account, artId);
            }

            var reservation = _identity.Next();
            var bodyHex = PostFilterBodyHash.TryCompute(request.Article);
            var mpUnits = bodyHex is null ? 0 : 1;
            PostFilterQuotaReserveStatus reserved;
            try
            {
                reserved = await _quota.ReserveAsync(
                        request.AccountName,
                        reservation,
                        request.Now,
                        snapshot.Windows,
                        snapshot.Ceilings,
                        messages: 1,
                        bytes: request.Article.ArtSize,
                        mpUnits,
                        bodyHex,
                        cancellationToken,
                        snapshot.ReservationTtlMs)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await _quota.ReleaseAsync(
                        request.AccountName,
                        reservation,
                        request.Now,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }

            if (reserved != PostFilterQuotaReserveStatus.Accepted)
            {
                return Reject(snapshot, PostFilterStage.Quota, reserved.ToString(), account, artId, reserved);
            }

            var lease = new PostFilterLease(
                _quota,
                request.AccountName,
                reservation,
                snapshot.Windows,
                snapshot.Ceilings);

            if (!allowlisted && ShouldScan(snapshot, request.Article))
            {
                PostFilterSpamAssassinResult scan;
                try
                {
                    scan = await _spamAssassin
                        .CheckAsync(
                            request.Article,
                            request.AccountName,
                            snapshot.SpamAssassinTarget,
                            new SpamdScanContext(
                                request.ClientIdentity.ClientAddress,
                                request.ServerFqdn,
                                request.Now),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await ReleaseQuietAsync(lease, request.Now).ConfigureAwait(false);
                    throw;
                }

                if (scan.Status == PostFilterSpamAssassinStatus.Spam)
                {
                    await ReleaseQuietAsync(lease, request.Now).ConfigureAwait(false);
                    PostFilterLogMessages.Rejected(_logger, PostFilterStage.SpamAssassin, "spam", account, artId);
                    return PostFilterResult.RejectedSpamAssassin("spam", snapshot.Revision, scan);
                }

                if (scan.Status == PostFilterSpamAssassinStatus.Failed)
                {
                    var action = snapshot.SpamAssassinOnFailure
                        ?? throw new InvalidOperationException(
                            "SpamAssassin is enabled but OnFailure is not configured.");
                    PostFilterLogMessages.SpamAssassinFailed(
                        _logger,
                        action.ToString(),
                        scan.Detail,
                        account,
                        artId);
                    if (action == PostFilterSpamOnFailure.Reject)
                    {
                        await ReleaseQuietAsync(lease, request.Now).ConfigureAwait(false);
                        PostFilterLogMessages.Rejected(
                            _logger,
                            PostFilterStage.SpamAssassin,
                            "scanner-failure",
                            account,
                            artId);
                        return PostFilterResult.RejectedSpamAssassin(
                            "scanner-failure",
                            snapshot.Revision,
                            scan);
                    }
                }
            }

            PostFilterLogMessages.Accepted(_logger, PostFilterStage.Complete, account, artId, reserved: true);
            return PostFilterResult.Accepted(lease, snapshot.Revision);
        }

        private PostFilterResult Reject(
            PostFilterPolicySnapshot snapshot,
            PostFilterStage stage,
            string reason,
            string account,
            string artId,
            PostFilterQuotaReserveStatus? quotaStatus = null)
        {
            PostFilterLogMessages.Rejected(_logger, stage, reason, account, artId);
            return PostFilterResult.Rejected(stage, reason, quotaStatus, snapshot.Revision);
        }

        private async ValueTask ReleaseQuietAsync(PostFilterLease lease, DateTimeOffset now)
        {
            var status = await lease.ReleaseAsync(now, CancellationToken.None).ConfigureAwait(false);
            PostFilterLogMessages.Released(_logger, status, lease.AccountName, lease.Reservation.Token);
        }

        private static bool ShouldScan(PostFilterPolicySnapshot snapshot, in ArticleRecord article)
        {
            if (!snapshot.SpamAssassinEnabled)
            {
                return false;
            }

            if (snapshot.SpamAssassinMaxArticleSize > 0
                && article.ArtSize >= snapshot.SpamAssassinMaxArticleSize)
            {
                return false;
            }

            return (article.ArtType & snapshot.SpamAssassinExcludeArtTypes) == 0;
        }

        private static bool IsDenied(PostFilterPolicySnapshot snapshot, in PostFilterRequest request)
        {
            var accountId = PostFilterAccountIdentity.TryFromRequest(request.AccountName);
            if (accountId is not null && snapshot.DeniedAccounts.Contains(accountId))
            {
                return true;
            }

            var address = request.ClientIdentity.ClientAddress;
            foreach (var network in snapshot.DeniedCidrs)
            {
                if (network.Contains(address))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAllowlisted(PostFilterPolicySnapshot snapshot, in PostFilterRequest request)
        {
            var accountId = PostFilterAccountIdentity.TryFromRequest(request.AccountName);
            if (accountId is not null && snapshot.AllowlistedAccounts.Contains(accountId))
            {
                return true;
            }

            var address = request.ClientIdentity.ClientAddress;
            foreach (var network in snapshot.AllowlistedCidrs)
            {
                if (network.Contains(address))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FormatArtId(in ArticleRecord article) =>
            article.ArtHash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
