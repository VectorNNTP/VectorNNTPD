namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Deterministic completion gates for <c>LocalPeerSmoke</c>. Peer ARTICLE success alone is never enough.
    /// </summary>
    internal static class LocalPeerSmokeAccounting
    {
        /// <summary>Harness-visible counters used by smoke success criteria.</summary>
        internal readonly record struct Evidence(
            long RequestsStarted,
            long WorkSuccess,
            long WorkNotFound,
            long WorkOtherFailure,
            long ArticlesDelivered,
            long OutstandingAtEnd,
            long Timeouts,
            long UnresolvedAtDispose,
            long LateRepliesAfterTimeout,
            long? PeerArticleRequests,
            long? PeerArticleSuccess,
            long? PeerArticleMissing,
            long? PeerProtocolErrors);

        /// <summary>
        /// Throws when smoke completion cannot be established. Does not invent counters for
        /// stages the stress harness does not expose (e.g. per-message RMQ consume ACKs).
        /// </summary>
        internal static void AssertSuccessfulSmokeOrThrow(Evidence evidence)
        {
            if (evidence.RequestsStarted <= 0)
            {
                throw new InvalidOperationException(
                    "Smoke failed: no work items were submitted (RequestsStarted=0).");
            }

            if (evidence.ArticlesDelivered <= 0)
            {
                throw new InvalidOperationException(
                    "Smoke failed: ArticlesDelivered=0. Peer ARTICLE responses alone do not satisfy "
                    + "end-to-end delivery. Zero downstream TLS listener deliveries is a hard failure.");
            }

            if (evidence.WorkSuccess <= 0)
            {
                throw new InvalidOperationException(
                    "Smoke failed: WorkSuccess=0 (missing RPC success completion) while deliveries were claimed.");
            }

            if (evidence.ArticlesDelivered > evidence.WorkSuccess)
            {
                throw new InvalidOperationException(
                    $"Smoke failed: accounting mismatch ArticlesDelivered={evidence.ArticlesDelivered} "
                    + $"> WorkSuccess={evidence.WorkSuccess}.");
            }

            if (evidence.OutstandingAtEnd != 0)
            {
                throw new InvalidOperationException(
                    $"Smoke failed: OutstandingAtEnd={evidence.OutstandingAtEnd} at completion deadline.");
            }

            if (evidence.UnresolvedAtDispose != 0)
            {
                throw new InvalidOperationException(
                    $"Smoke failed: UnresolvedAtDispose={evidence.UnresolvedAtDispose}.");
            }

            // Timeouts with zero deliveries already failed above; timeouts with some deliveries
            // still fail smoke — a bounded smoke must be clean.
            if (evidence.Timeouts > 0)
            {
                throw new InvalidOperationException(
                    $"Smoke failed: Timeouts={evidence.Timeouts}. Bounded smoke requires a clean run.");
            }

            if (evidence.LateRepliesAfterTimeout > 0)
            {
                throw new InvalidOperationException(
                    $"Smoke failed: LateRepliesAfterTimeout={evidence.LateRepliesAfterTimeout}.");
            }

            if (evidence.PeerArticleSuccess is { } peerOk)
            {
                if (peerOk < evidence.ArticlesDelivered)
                {
                    throw new InvalidOperationException(
                        $"Smoke failed: peer ArticleSuccess={peerOk} < ArticlesDelivered={evidence.ArticlesDelivered}.");
                }

                // Peer responses without downstream delivery: already covered by ArticlesDelivered<=0,
                // but explicit when peerOk>0 and deliveries somehow zero is handled above.
                if (peerOk > 0 && evidence.ArticlesDelivered == 0)
                {
                    throw new InvalidOperationException(
                        "Smoke failed: peer returned ARTICLE success but ArticlesDelivered=0.");
                }
            }

            if (evidence.PeerArticleRequests is { } peerReq
                && evidence.PeerArticleSuccess is { } peerSuccess
                && peerReq < peerSuccess)
            {
                throw new InvalidOperationException(
                    $"Smoke failed: peer ArticleRequests={peerReq} < ArticleSuccess={peerSuccess}.");
            }

            if (evidence.PeerArticleMissing is > 0)
            {
                throw new InvalidOperationException(
                    $"Smoke failed: peer ArticleMissing={evidence.PeerArticleMissing} for a corpus-backed smoke.");
            }

            if (evidence.PeerProtocolErrors is > 0)
            {
                throw new InvalidOperationException(
                    $"Smoke failed: peer ProtocolErrors={evidence.PeerProtocolErrors}.");
            }
        }
    }
}
