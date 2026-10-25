using RabbitMQ.Client;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.RabbitMq;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Deterministic contract for Article Work RPC request AMQP properties used by the stress client.
    /// </summary>
    public sealed class ArticleWorkStressClientRequestPropertiesTests
    {
        [Fact]
        public void CreateRequestProperties_omits_expiration_so_ready_work_can_buffer()
        {
            var requestId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var props = ArticleWorkStressClient.CreateRequestProperties(
                correlationId: "corr-1",
                replyTo: "amq.gen-reply-test",
                requestId);

            Assert.True(string.IsNullOrEmpty(props.Expiration));
        }

        [Fact]
        public void CreateRequestProperties_sets_correlation_reply_to_content_type_and_request_id()
        {
            var requestId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var props = ArticleWorkStressClient.CreateRequestProperties(
                correlationId: "corr-abc",
                replyTo: "exclusive.reply.queue",
                requestId);

            Assert.Equal("corr-abc", props.CorrelationId);
            Assert.Equal("exclusive.reply.queue", props.ReplyTo);
            Assert.Equal("application/json", props.ContentType);
            Assert.Equal(DeliveryModes.Persistent, props.DeliveryMode);
            Assert.NotNull(props.Headers);
            Assert.True(props.Headers.TryGetValue("RequestId", out var header));
            Assert.Equal(requestId.ToString("D"), header);
            Assert.True(string.IsNullOrEmpty(props.Expiration));
        }

        [Fact]
        public void Response_ttl_remains_one_second_and_is_not_applied_to_stress_requests()
        {
            Assert.Equal("1000", ArticleWorkResponseWireProtocol.ExpirationMilliseconds);
            var props = ArticleWorkStressClient.CreateRequestProperties(
                "corr",
                "reply",
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
            Assert.NotEqual(
                ArticleWorkResponseWireProtocol.ExpirationMilliseconds,
                props.Expiration ?? string.Empty);
        }

        [Fact]
        public void Provider_work_queue_arguments_do_not_declare_x_message_ttl()
        {
            var classic = BackFillerArticleWorkTopology.BuildQueueArguments(
                new BackFillerRabbitMqRuntimeOptions(
                    WorkRequestMaxPayloadBytes: 65536,
                    PublishConfirmTimeoutSeconds: 30,
                    ConsumerPrefetchCount: 1,
                    QueueTopology: new ArticleWorkQueueTopology(
                        ArticleWorkQueueKind.Classic,
                        ConfiguredHostCount: 1,
                        QuorumInitialGroupSize: null),
                    DeliveryLimit: 10));
            Assert.False(classic.ContainsKey("x-message-ttl"));

            var quorum = BackFillerArticleWorkTopology.BuildQueueArguments(
                new BackFillerRabbitMqRuntimeOptions(
                    WorkRequestMaxPayloadBytes: 65536,
                    PublishConfirmTimeoutSeconds: 30,
                    ConsumerPrefetchCount: 1,
                    QueueTopology: new ArticleWorkQueueTopology(
                        ArticleWorkQueueKind.Quorum,
                        ConfiguredHostCount: 3,
                        QuorumInitialGroupSize: 3),
                    DeliveryLimit: 10));
            Assert.False(quorum.ContainsKey("x-message-ttl"));
            Assert.True(quorum.ContainsKey(BackFillerArticleWorkTopology.DeliveryLimitArgumentName));
        }

        [Fact]
        public void CreateRequestProperties_rejects_missing_correlation_or_reply_to()
        {
            var requestId = Guid.NewGuid();
            Assert.Throws<ArgumentException>(() =>
                ArticleWorkStressClient.CreateRequestProperties(" ", "reply", requestId));
            Assert.Throws<ArgumentException>(() =>
                ArticleWorkStressClient.CreateRequestProperties("corr", " ", requestId));
        }
    }
}
