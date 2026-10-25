using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Focused regression: ProviderFailure / peer terminal failures ACK with a correlated RPC reply and never requeue.
    /// </summary>
    public sealed class ProviderFailureTerminalSettlementTests
    {
        /// <summary>
        /// Planner disposition for ProviderFailure is backbone-agnostic (same ACK/publish contract).
        /// </summary>
        [Fact]
        public void ProviderFailure_planner_disposition_is_identical_for_any_caller()
        {
            var first = ArticleWorkDispositionPlanner.Create(
                ArticleWorkOutcome.ProviderFailure,
                replyable: true,
                cancellationRequested: false,
                deliveryCount: null,
                redelivered: false,
                deliveryLimit: 10);
            var second = ArticleWorkDispositionPlanner.Create(
                ArticleWorkOutcome.ProviderFailure,
                replyable: true,
                cancellationRequested: false,
                deliveryCount: null,
                redelivered: false,
                deliveryLimit: 10);
            Assert.Equal(first, second);
            Assert.True(first.Acknowledge);
            Assert.False(first.Requeue);
            Assert.True(first.PublishResponse);
        }

        /// <summary>
        /// Pipeline publishes ProviderFailure with the handler error and ACKs without requeue.
        /// </summary>
        [Fact]
        public async Task ProviderFailure_pipeline_acks_and_publishes_failure_reply()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler
                {
                    Outcome = ArticleWorkOutcome.ProviderFailure,
                    Error = "IOException: connection reset",
                },
                publisher,
                1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ProviderFailure, outcome);
            var intent = Assert.Single(publisher.Published);
            Assert.Equal(ArticleWorkOutcome.ProviderFailure, intent.Outcome);
            Assert.Equal("IOException: connection reset", intent.Error);
            var settlement = Assert.Single(channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
        }

        /// <summary>
        /// Repeated ProviderFailure deliveries ACK each time (no broker requeue feedback loop).
        /// </summary>
        [Fact]
        public async Task ProviderFailure_does_not_requeue_across_repeated_deliveries()
        {
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler
                {
                    Outcome = ArticleWorkOutcome.ProviderFailure,
                    Error = "fault",
                },
                publisher,
                1024);

            for (var i = 0; i < 5; i++)
            {
                var channel = new FakeBackFillerRabbitMqChannel(1);
                var outcome = await pipeline.ProcessAsync(
                    ArticleWorkTestDeliveries.Canonical(),
                    "Giganews",
                    channel,
                    static () => true,
                    CancellationToken.None);
                Assert.Equal(ArticleWorkOutcome.ProviderFailure, outcome);
                Assert.True(Assert.Single(channel.Settlements).Acknowledge);
                Assert.False(Assert.Single(channel.Settlements).Requeue);
            }

            Assert.Equal(5, publisher.Published.Count);
        }

        /// <summary>
        /// Found / NotFound / InvalidArticle remain ACK + publish; Cancelled remains NACK-requeue without publish.
        /// </summary>
        [Fact]
        public void Established_terminal_and_cancel_dispositions_unchanged()
        {
            AssertDisposition(ArticleWorkOutcome.Success, acknowledge: true, requeue: false, publish: true);
            AssertDisposition(ArticleWorkOutcome.ArticleNotFound, acknowledge: true, requeue: false, publish: true);
            AssertDisposition(ArticleWorkOutcome.InvalidArticle, acknowledge: true, requeue: false, publish: true);
            AssertDisposition(ArticleWorkOutcome.Cancelled, acknowledge: false, requeue: true, publish: false);
        }

        /// <summary>Asserts ACK/requeue/publish flags from <see cref="ArticleWorkDispositionPlanner"/> for one terminal outcome.</summary>
        private static void AssertDisposition(
            ArticleWorkOutcome outcome,
            bool acknowledge,
            bool requeue,
            bool publish)
        {
            var disposition = ArticleWorkDispositionPlanner.Create(
                outcome,
                replyable: true,
                cancellationRequested: false,
                deliveryCount: null,
                redelivered: false,
                deliveryLimit: 10);
            Assert.Equal(acknowledge, disposition.Acknowledge);
            Assert.Equal(requeue, disposition.Requeue);
            Assert.Equal(publish, disposition.PublishResponse);
        }
    }
}
