namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    public sealed class LocalPeerSmokeAccountingTests
    {
        [Fact]
        public void Accepts_successful_end_to_end_completion()
        {
            LocalPeerSmokeAccounting.AssertSuccessfulSmokeOrThrow(Clean(delivered: 5, workSuccess: 5, peerOk: 5));
        }

        [Fact]
        public void Rejects_zero_downstream_deliveries_despite_peer_success()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalPeerSmokeAccounting.AssertSuccessfulSmokeOrThrow(
                    Clean(delivered: 0, workSuccess: 5, peerOk: 5)));
            Assert.Contains("ArticlesDelivered=0", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_missing_rpc_success()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalPeerSmokeAccounting.AssertSuccessfulSmokeOrThrow(
                    Clean(delivered: 3, workSuccess: 0, peerOk: 3)));
            Assert.Contains("WorkSuccess=0", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_failed_or_timeout_work()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalPeerSmokeAccounting.AssertSuccessfulSmokeOrThrow(
                    Clean(delivered: 2, workSuccess: 2, peerOk: 2) with { Timeouts = 1 }));
            Assert.Contains("Timeouts=", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_outstanding_work_at_deadline()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalPeerSmokeAccounting.AssertSuccessfulSmokeOrThrow(
                    Clean(delivered: 2, workSuccess: 2, peerOk: 2) with { OutstandingAtEnd = 1 }));
            Assert.Contains("OutstandingAtEnd=", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_mismatched_delivery_vs_work_success()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalPeerSmokeAccounting.AssertSuccessfulSmokeOrThrow(
                    Clean(delivered: 5, workSuccess: 2, peerOk: 5)));
            Assert.Contains("accounting mismatch", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_unresolved_cleanup()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalPeerSmokeAccounting.AssertSuccessfulSmokeOrThrow(
                    Clean(delivered: 1, workSuccess: 1, peerOk: 1) with { UnresolvedAtDispose = 2 }));
            Assert.Contains("UnresolvedAtDispose=", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_no_work_started()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalPeerSmokeAccounting.AssertSuccessfulSmokeOrThrow(
                    Clean(delivered: 0, workSuccess: 0, peerOk: 0) with { RequestsStarted = 0 }));
            Assert.Contains("RequestsStarted=0", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_peer_missing_or_protocol_errors()
        {
            Assert.Throws<InvalidOperationException>(() =>
                LocalPeerSmokeAccounting.AssertSuccessfulSmokeOrThrow(
                    Clean(delivered: 1, workSuccess: 1, peerOk: 1) with { PeerArticleMissing = 1 }));
            Assert.Throws<InvalidOperationException>(() =>
                LocalPeerSmokeAccounting.AssertSuccessfulSmokeOrThrow(
                    Clean(delivered: 1, workSuccess: 1, peerOk: 1) with { PeerProtocolErrors = 1 }));
        }

        private static LocalPeerSmokeAccounting.Evidence Clean(long delivered, long workSuccess, long peerOk) =>
            new(
                RequestsStarted: Math.Max(delivered, 1),
                WorkSuccess: workSuccess,
                WorkNotFound: 0,
                WorkOtherFailure: 0,
                ArticlesDelivered: delivered,
                OutstandingAtEnd: 0,
                Timeouts: 0,
                UnresolvedAtDispose: 0,
                LateRepliesAfterTimeout: 0,
                PeerArticleRequests: peerOk,
                PeerArticleSuccess: peerOk,
                PeerArticleMissing: 0,
                PeerProtocolErrors: 0);
    }
}
