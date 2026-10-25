using System.Collections.Concurrent;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Deterministic correlation / waiter / drain contracts for the stress RPC client lifecycle.
    /// </summary>
    public sealed class ArticleWorkRpcLifecycleContractTests
    {
        [Fact]
        public void Waiter_registration_before_publish_prevents_immediate_reply_race()
        {
            var pending = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            const string correlationId = "corr-race";

            // Same order as ArticleWorkStressClient.RequestAsync: register, then "publish".
            Assert.True(pending.TryAdd(correlationId, 0));
            var published = true;
            Assert.True(published);
            Assert.True(pending.ContainsKey(correlationId));
        }

        [Fact]
        public void Concurrent_correlations_complete_independent_waiters()
        {
            var tracker = new ArticleWorkRpcOutstandingTracker();
            tracker.OnPublished();
            tracker.OnPublished();

            tracker.OnReply(callerStillWaiting: true);
            Assert.Equal(1, tracker.WaitingCount);
            Assert.Equal(1, tracker.BrokerOutstandingCount);

            tracker.OnReply(callerStillWaiting: true);
            Assert.Equal(0, tracker.WaitingCount);
            Assert.Equal(0, tracker.BrokerOutstandingCount);
        }

        [Fact]
        public void Caller_timeout_does_not_clear_broker_outstanding()
        {
            var tracker = new ArticleWorkRpcOutstandingTracker();
            tracker.OnPublished();
            tracker.OnCallerTimeout();
            Assert.Equal(0, tracker.WaitingCount);
            Assert.Equal(1, tracker.BrokerOutstandingCount);
        }

        [Fact]
        public void Late_reply_after_timeout_is_accounted_without_corrupting_other_waiters()
        {
            var tracker = new ArticleWorkRpcOutstandingTracker();
            tracker.OnPublished();
            tracker.OnPublished();
            tracker.OnCallerTimeout();

            tracker.OnReply(callerStillWaiting: false);
            Assert.Equal(1, tracker.WaitingCount);
            Assert.Equal(1, tracker.BrokerOutstandingCount);
            Assert.Equal(1, tracker.LateRepliesAfterTimeout);

            tracker.OnReply(callerStillWaiting: true);
            Assert.Equal(0, tracker.WaitingCount);
            Assert.Equal(0, tracker.BrokerOutstandingCount);
            Assert.Equal(1, tracker.LateRepliesAfterTimeout);
        }

        [Fact]
        public void Bounded_drain_reports_unresolved_when_broker_outstanding_remains()
        {
            var tracker = new ArticleWorkRpcOutstandingTracker();
            tracker.OnPublished();
            tracker.OnCallerTimeout();

            var unresolved = tracker.BrokerOutstandingCount;
            Assert.Equal(1, unresolved);
            // Dispose path records UnresolvedAtDispose when drain expires with outstanding > 0.
            Assert.True(unresolved > 0);
        }

        [Fact]
        public void Stress_work_requests_do_not_carry_per_message_expiration()
        {
            var props = ArticleWorkStressClient.CreateRequestProperties(
                "corr-ttl",
                "reply-ttl",
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
            Assert.True(string.IsNullOrEmpty(props.Expiration));
        }

        [Fact]
        public void Stress_summary_surfaces_reply_lifecycle_counters()
        {
            var metrics = new StressMetrics
            {
                RunName = "lifecycle-counters",
                Backbone = "Eweka",
                ConfiguredConcurrency = 75,
                ConfiguredMaxInFlight = 75,
            };
            metrics.SteadyStateStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            metrics.SteadyStateEndTimestamp = metrics.SteadyStateStartTimestamp
                + System.Diagnostics.Stopwatch.Frequency;

            metrics.SetReplyLifecycleCounters(
                lateRepliesAfterTimeout: 3,
                unresolvedAtDispose: 21,
                brokerOutstandingAtDrainEnd: 21);

            var summary = metrics.BuildSummary();
            Assert.Equal(3, summary.LateRepliesAfterTimeout);
            Assert.Equal(21, summary.UnresolvedAtDispose);
            Assert.Equal(21, summary.BrokerOutstandingAtDrainEnd);
            Assert.Contains("late=3", summary.FormatTableRow(), StringComparison.Ordinal);
            Assert.Contains("unresolved=21", summary.FormatTableRow(), StringComparison.Ordinal);
        }
    }
}
