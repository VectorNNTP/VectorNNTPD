// Copyright © Chris Knipe cknipe@opticnetworks.net

using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Work-request size limits: oversized bodies are rejected before handler execution with bounded diagnostics.
    /// </summary>
    public sealed class ArticleWorkOversizedSecurityTests
    {
        /// <summary>
        /// Configured <c>WorkRequestMaxPayloadBytes</c> ceiling used in parser and pipeline tests.
        /// </summary>
        private const int ConfiguredMaxPayload = 1024;

        /// <summary>
        /// Parser rejects bodies above the byte limit and surfaces a reason referencing <c>WorkRequestMaxPayloadBytes</c> without parsing JSON identities.
        /// </summary>
        [Fact]
        public void Parser_rejects_oversized_payload_and_returns_reason()
        {
            var validJson = """{" + "\"version\":1,\"requestId\":\"7c1cb8a0-95f9-4c13-8e53-339773e3afaa\",\"messageId\":\"<test@example.invalid>\",\"backbone\":\"TestBone\"}""";
            var oversized = validJson + new string('X', ConfiguredMaxPayload);
            var delivery = ArticleWorkTestDeliveries.Create(oversized);

            var parsed = ArticleWorkRequestParser.Parse(delivery, "TestBone", ConfiguredMaxPayload);

            Assert.False(parsed.IsValid);
            Assert.NotNull(parsed.Failure);
            Assert.Contains("WorkRequestMaxPayloadBytes", parsed.Failure.Reason, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Pipeline returns <see cref="ArticleWorkOutcome.InvalidRequest"/> for pre-parsed oversized deliveries; handler is not invoked.
        /// </summary>
        [Fact]
        public async Task Pipeline_ProcessAsync_returns_diagnostic_reason_for_oversized_payload()
        {
            var handler = new TestNullHandler();
            var publisher = new TestPublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, ConfiguredMaxPayload);

            var validJson = """{" + "\"version\":1,\"requestId\":\"7c1cb8a0-95f9-4c13-8e53-339773e3afaa\",\"messageId\":\"<test@example.invalid>\",\"backbone\":\"TestBone\"}""";
            var oversized = validJson + new string('X', ConfiguredMaxPayload);
            var delivery = ArticleWorkTestDeliveries.Create(oversized);

            var channel = new TestManualAckChannel();
            var backbone = "TestBone";
            var channelStillCurrent = () => true;

            var parsed = ArticleWorkRequestParser.Parse(delivery, backbone, ConfiguredMaxPayload);
            var outcome = await pipeline.ProcessAsync(
                delivery,
                backbone,
                channel,
                channelStillCurrent,
                CancellationToken.None,
                parsed);

            Assert.Equal(ArticleWorkOutcome.InvalidRequest, outcome);
            Assert.False(parsed.IsValid);
            Assert.NotNull(parsed.Failure);
            Assert.Contains("WorkRequestMaxPayloadBytes", parsed.Failure.Reason, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Handler stub that must not run for oversized requests; returns cancelled if invoked.
        /// </summary>
        private sealed class TestNullHandler : IArticleWorkHandler
        {
            /// <inheritdoc />
            public ValueTask<ArticleWorkHandlerResult> HandleAsync(ArticleWorkItem item, CancellationToken cancellationToken)
            {
                return ValueTask.FromResult(new ArticleWorkHandlerResult(ArticleWorkOutcome.Cancelled, null, null, null, null));
            }
        }

        /// <summary>
        /// No-op publisher; does not record intents or simulate confirm. Not used to assert publication in these tests.
        /// </summary>
        private sealed class TestPublisher : IArticleWorkResponsePublisher
        {
            /// <inheritdoc />
            public bool CompletesSuccessPublication => false;

            /// <inheritdoc />
            public Task PublishAsync(ArticleWorkResponseIntent intent, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        /// <summary>
        /// Minimal open manual-ack channel stub: settlement methods complete without recording ops or enforcing generation semantics.
        /// </summary>
        private sealed class TestManualAckChannel : IRabbitMqManualAckChannel
        {
            /// <inheritdoc />
            public long Generation => 1;

            /// <inheritdoc />
            public bool IsOpen => true;

            /// <inheritdoc />
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            /// <inheritdoc />
            public Task ExchangeDeclareAsync(string exchange, string type, bool durable, bool autoDelete, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken) => Task.CompletedTask;

            /// <inheritdoc />
            public Task QueueDeclareAsync(string queue, bool durable, bool exclusive, bool autoDelete, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken) => Task.CompletedTask;

            /// <inheritdoc />
            public Task QueueBindAsync(string queue, string exchange, string routingKey, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<string> BasicConsumeAsync(string queue, ushort prefetch, Func<RabbitMqManualAckDelivery, Task> onDeliveryAsync, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

            /// <inheritdoc />
            public Task BasicCancelAsync(string consumerTag, CancellationToken cancellationToken) => Task.CompletedTask;

            /// <inheritdoc />
            public Task BasicAckAsync(ulong deliveryTag, CancellationToken cancellationToken) => Task.CompletedTask;

            /// <inheritdoc />
            public Task BasicNackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
