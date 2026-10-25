namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Deterministic lifecycle contract for exclusive ReplyTo drain (waiter vs broker-outstanding).
    /// </summary>
    public sealed class ArticleWorkRpcOutstandingTrackerTests
    {
        [Fact]
        public void Timeout_clears_waiter_but_keeps_broker_outstanding_until_late_reply()
        {
            var tracker = new ArticleWorkRpcOutstandingTracker();
            tracker.OnPublished();
            Assert.Equal(1, tracker.WaitingCount);
            Assert.Equal(1, tracker.BrokerOutstandingCount);

            tracker.OnCallerTimeout();
            Assert.Equal(0, tracker.WaitingCount);
            Assert.Equal(1, tracker.BrokerOutstandingCount);

            tracker.OnReply(callerStillWaiting: false);
            Assert.Equal(0, tracker.WaitingCount);
            Assert.Equal(0, tracker.BrokerOutstandingCount);
            Assert.Equal(1, tracker.LateRepliesAfterTimeout);
        }

        [Fact]
        public void Normal_reply_clears_waiter_and_broker_outstanding()
        {
            var tracker = new ArticleWorkRpcOutstandingTracker();
            tracker.OnPublished();
            tracker.OnReply(callerStillWaiting: true);
            Assert.Equal(0, tracker.WaitingCount);
            Assert.Equal(0, tracker.BrokerOutstandingCount);
            Assert.Equal(0, tracker.LateRepliesAfterTimeout);
        }

        [Fact]
        public void Concurrent_requests_are_independent()
        {
            var tracker = new ArticleWorkRpcOutstandingTracker();
            tracker.OnPublished();
            tracker.OnPublished();
            Assert.Equal(2, tracker.WaitingCount);
            Assert.Equal(2, tracker.BrokerOutstandingCount);

            tracker.OnCallerTimeout();
            Assert.Equal(1, tracker.WaitingCount);
            Assert.Equal(2, tracker.BrokerOutstandingCount);

            tracker.OnReply(callerStillWaiting: true);
            Assert.Equal(0, tracker.WaitingCount);
            Assert.Equal(1, tracker.BrokerOutstandingCount);

            tracker.OnReply(callerStillWaiting: false);
            Assert.Equal(0, tracker.BrokerOutstandingCount);
            Assert.Equal(1, tracker.LateRepliesAfterTimeout);
        }

        [Fact]
        public void Publish_failure_clears_both_counters()
        {
            var tracker = new ArticleWorkRpcOutstandingTracker();
            tracker.OnPublished();
            tracker.OnPublishFailed();
            Assert.Equal(0, tracker.WaitingCount);
            Assert.Equal(0, tracker.BrokerOutstandingCount);
        }

        [Fact]
        public void Dispose_is_safe_when_broker_outstanding_already_zero()
        {
            var tracker = new ArticleWorkRpcOutstandingTracker();
            Assert.Equal(0, tracker.BrokerOutstandingCount);
            Assert.Equal(0, tracker.WaitingCount);
        }
    }
}
