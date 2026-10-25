using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;
using System.Text;


namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Response publication and confirm ordering relative to RabbitMQ settlement: terminal ack after confirm, generation guards, publish-only channel.
    /// </summary>
    public sealed class ArticleWorkResponsePublicationTests
    {
        /// <summary>
        /// Success path publishes confirmed v1 JSON on the reply route then acks the original delivery exactly once on the consume channel.
        /// </summary>
        [Fact]
        public async Task Success_publishes_confirmed_json_then_acks_exactly_once()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
            var handler = SuccessHandler();
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(handler, context.Publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            var publication = Assert.Single(context.PublishChannel.Publications);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalReplyTo, publication.RoutingKey);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalCorrelationId, publication.CorrelationId);
            Assert.Equal(ArticleWorkResponseWireProtocol.JsonContentType, publication.ContentType);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalRequestId, publication.RequestIdHeader);
            Assert.Equal(ArticleWorkResponseWireProtocol.ExpirationMilliseconds, publication.ExpirationMilliseconds);
            Assert.True(Guid.TryParse(publication.MessageId, out var amqpMessageId));
            Assert.NotEqual(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), amqpMessageId);
            Assert.NotEqual(ArticleWorkTestDeliveries.CanonicalCorrelationId, publication.MessageId);
            Assert.Equal(
                ArticleWorkTestDeliveries.CanonicalSuccessResponseJson,
                Encoding.UTF8.GetString(publication.Body.Span));
            Assert.DoesNotContain("From:", Encoding.UTF8.GetString(publication.Body.Span), StringComparison.Ordinal);
            var settlement = Assert.Single(channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.Equal(7UL, settlement.DeliveryTag);
            Assert.DoesNotContain(
                context.PublishChannel.GetType().GetMethods(),
                static method => method.Name is "BasicAckAsync" or "BasicNackAsync");
        }

        /// <summary>
        /// Distinct delivery tags each get one ack and may each publish; handler runs per delivery.
        /// </summary>
        [Fact]
        public async Task Redelivered_success_may_publish_again_but_each_delivery_settles_once()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
            var handler = SuccessHandler();
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(handler, context.Publisher, 1024);

            await pipeline.ProcessAsync(ArticleWorkTestDeliveries.Canonical(deliveryTag: 7), "Giganews", channel, static () => true, CancellationToken.None);
            await pipeline.ProcessAsync(ArticleWorkTestDeliveries.Canonical(deliveryTag: 8), "Giganews", channel, static () => true, CancellationToken.None);

            Assert.Equal(2, context.PublishChannel.Publications.Count);
            Assert.Equal(2, channel.Settlements.Count);
            Assert.All(channel.Settlements, static settlement => Assert.True(settlement.Acknowledge));
            Assert.Equal(new ulong[] { 7, 8 }, channel.Settlements.Select(static settlement => settlement.DeliveryTag));
            Assert.Equal(2, handler.HandleCount);
        }

        /// <summary>
        /// Transient/ambiguous publish failures NACK-requeue once and do not ACK (no local retry loop).
        /// Confirmed unroutable is covered separately as terminal without requeue.
        /// </summary>
        [Fact]
        public async Task Success_publication_failures_nack_requeue_and_do_not_ack()
        {
            FakePublishConfirmBehavior[] behaviors =
            [
                FakePublishConfirmBehavior.ThrowOnPublish,
                FakePublishConfirmBehavior.Nack,
                FakePublishConfirmBehavior.CloseChannel,
            ];

            foreach (var behavior in behaviors)
            {
                await using var context = await PublicationContext.StartAsync(behavior);
                var handler = SuccessHandler();
                var channel = new FakeBackFillerRabbitMqChannel(1);
                var pipeline = new ArticleWorkDeliveryPipeline(handler, context.Publisher, 1024);

                var outcome = await pipeline.ProcessAsync(
                    ArticleWorkTestDeliveries.Canonical(),
                    "Giganews",
                    channel,
                    static () => true,
                    CancellationToken.None);

                Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
                Assert.Equal(1, handler.HandleCount);
                var settlement = Assert.Single(channel.Settlements);
                Assert.False(settlement.Acknowledge);
                Assert.True(settlement.Requeue);
            }
        }

        /// <summary>
        /// Confirm timeout yields retryable outcome: nack with requeue, no ack.
        /// </summary>
        [Fact]
        public async Task Confirm_timeout_remains_retryable()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Timeout);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// Connection loss during publish fails the reply and suppresses settlement. The delivery is not ACKed.
        /// </summary>
        [Fact]
        public async Task Generation_disappears_during_publication_does_not_ack()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
            context.PublishChannel.AfterEnqueue = () => context.Factory.LastConnection!.SimulateLost();
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                () => context.Connections.TryGetCurrent(out var handle) && handle.Generation == 1,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Empty(channel.Settlements);
        }

        /// <summary>
        /// <c>channelStillCurrent</c> false after confirm prevents ack on a lost generation.
        /// </summary>
        [Fact]
        public async Task Generation_change_after_confirm_before_ack_does_not_settle()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);
            var seen = 0;

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                () => Interlocked.Increment(ref seen) == 1,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Single(context.PublishChannel.Publications);
            Assert.Empty(channel.Settlements);
        }

        /// <summary>
        /// Closed consume channel cannot be settled; replacement channel receives no stray ack/nack.
        /// </summary>
        [Fact]
        public async Task Closed_original_channel_is_not_settled_by_a_replacement()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
            var original = new FakeBackFillerRabbitMqChannel(1);
            var replacement = new FakeBackFillerRabbitMqChannel(2);
            var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);
            original.IsOpen = false;

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                original,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Empty(original.Settlements);
            Assert.Empty(replacement.Settlements);
        }

        /// <summary>
        /// A failed publish on a closed consume channel does not NACK that tag, and it does not settle a replacement channel.
        /// </summary>
        [Fact]
        public async Task Closed_channel_publish_failure_does_not_settle_a_replacement()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.ThrowOnPublish);
            var original = new FakeBackFillerRabbitMqChannel(1);
            var replacement = new FakeBackFillerRabbitMqChannel(2);
            original.IsOpen = false;
            var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                original,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Empty(original.Settlements);
            Assert.Empty(replacement.Settlements);
        }

        /// <summary>
        /// Cancelling while the publisher is waiting for a confirm NACK-requeues and does not ACK.
        /// </summary>
        [Fact]
        public async Task Cancellation_during_publication_nacks_with_requeue()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Wait);
            var enqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.PublishChannel.Enqueued = enqueued;
            context.PublishChannel.ConfirmGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);
            using var cts = new CancellationTokenSource();

            var processing = pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                cts.Token);
            await enqueued.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await cts.CancelAsync();

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, await processing.WaitAsync(TimeSpan.FromSeconds(2)));
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalCorrelationId, Assert.Single(context.PublishChannel.Publications).CorrelationId);
        }

        /// <summary>
        /// Terminal handler failures publish failure JSON without success endpoint fields, then ack without requeue.
        /// </summary>
        /// <param name="handlerOutcomeName">Terminal outcome under test.</param>
        /// <param name="acknowledge">Expected ack disposition on the consume channel.</param>
        [Theory]
        [InlineData(nameof(ArticleWorkOutcome.ArticleNotFound), true)]
        [InlineData(nameof(ArticleWorkOutcome.InvalidArticle), true)]
        public async Task Terminal_failure_confirms_then_acks_without_requeue(
            string handlerOutcomeName,
            bool acknowledge)
        {
            var handlerOutcome = Enum.Parse<ArticleWorkOutcome>(handlerOutcomeName);
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
            var handler = new ControllableArticleWorkHandler { Outcome = handlerOutcome, Error = "missing" };
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(handler, context.Publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(handlerOutcome, outcome);
            using var document = JsonDocument.Parse(Assert.Single(context.PublishChannel.Publications).Body);
            Assert.Equal(handlerOutcome.ToString(), document.RootElement.GetProperty("outcome").GetString());
            Assert.False(document.RootElement.TryGetProperty("uri", out _));
            var settlement = Assert.Single(channel.Settlements);
            Assert.Equal(acknowledge, settlement.Acknowledge);
            Assert.False(settlement.Requeue);
        }

        /// <summary>
        /// Parse failure publishes InvalidRequest response then acks without requeue.
        /// </summary>
        [Fact]
        public async Task InvalidRequest_confirms_then_acks_without_requeue()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Create("{"),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.InvalidRequest, outcome);
            using var document = JsonDocument.Parse(Assert.Single(context.PublishChannel.Publications).Body);
            Assert.Equal("InvalidRequest", document.RootElement.GetProperty("outcome").GetString());
            Assert.True(Assert.Single(channel.Settlements).Acknowledge);
            Assert.False(Assert.Single(channel.Settlements).Requeue);
        }

        /// <summary>
        /// A publisher nack after the reply is recorded is an ambiguous confirm. The delivery NACK-requeues and is not ACKed.
        /// </summary>
        [Fact]
        public async Task Terminal_response_confirm_failure_nacks_with_requeue()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Nack);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.ArticleNotFound, Error = "missing" },
                context.Publisher,
                1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            var publication = Assert.Single(context.PublishChannel.Publications);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalCorrelationId, publication.CorrelationId);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// InvalidRequest path NACK-requeues when publish throws before the reply is recorded.
        /// </summary>
        [Fact]
        public async Task InvalidRequest_publish_failure_nacks_with_requeue()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.ThrowOnPublish);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Create("{"),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Empty(context.PublishChannel.Publications);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// Cancelled (including cancel override) nacks with requeue and emits no response JSON.
        /// </summary>
        [Fact]
        public async Task Cancelled_outcomes_do_not_publish()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Cancelled,
                Error = "retry",
            };
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(handler, context.Publisher, 1024);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                cts.Token);

            Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
            Assert.Empty(context.PublishChannel.Publications);
            Assert.True(Assert.Single(channel.Settlements).Requeue);
            Assert.False(Assert.Single(channel.Settlements).Acknowledge);
        }

        /// <summary>
        /// Installing a publish channel from an older generation is rejected; current channel stays live.
        /// </summary>
        [Fact]
        public async Task Stale_publisher_channel_cannot_replace_or_dispose_the_current_channel()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
            var current = context.PublishChannel;
            var stale = new FakeBackFillerRabbitMqPublishChannel(0);

            await context.Publisher.InstallPublishChannelAsync(stale);

            Assert.Same(current, context.Publisher.Channel);
            Assert.Equal(0, current.DisposeCount);
            Assert.Equal(1, stale.DisposeCount);
        }

        /// <summary>
        /// Simulated connection loss rebuilds publish channel on the new generation and disposes the prior publish channel once.
        /// </summary>
        [Fact]
        public async Task Publisher_rebuilds_on_generation_replacement()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
            var first = context.PublishChannel;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Factory.Connected = secondConnected;
            context.Factory.LastConnection!.SimulateLost();
            await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Publisher.Generation == 2 && context.Publisher.Channel is not null,
                TimeSpan.FromSeconds(2));

            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(2, context.Publisher.Generation);
            Assert.NotSame(first, context.Publisher.Channel);
        }

        /// <summary>
        /// Publisher start propagates channel creation failure and remains stopped.
        /// </summary>
        [Fact]
        public async Task Startup_fails_when_a_publish_channel_cannot_be_created()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            factory.LastConnection!.CreatePublishChannelException = new InvalidOperationException("no channel");
            var publisher = new ArticleWorkResponsePublisher(
                connections,
                RabbitMqServiceTests.CreateFastRuntime(),
                NullLogger<ArticleWorkResponsePublisher>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.StartAsync(CancellationToken.None));
            Assert.Equal(ArticleWorkResponsePublisherState.Stopped, publisher.State);
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Dispose cancels an in-progress publish-channel rebuild blocked on the factory.
        /// </summary>
        [Fact]
        public async Task Shutdown_cancels_a_blocked_publish_channel_rebuild()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
            context.Factory.CreatePublishChannelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Factory.BlockCreatePublishChannel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Factory.Connected = secondConnected;
            context.Factory.LastConnection!.SimulateLost();
            await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await context.Factory.CreatePublishChannelStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var disposing = context.Publisher.DisposeAsync().AsTask();
            await disposing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(ArticleWorkResponsePublisherState.Stopped, context.Publisher.State);
        }

        /// <summary>
        /// Publisher dispose during blocked confirm yields retryable nack, not ack.
        /// </summary>
        [Fact]
        public async Task Shutdown_during_confirm_does_not_ack()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Wait);
            context.PublishChannel.Enqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);
            var processing = pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);
            await context.PublishChannel.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await context.Publisher.DisposeAsync();
            var outcome = await processing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.True(Assert.Single(channel.Settlements).Requeue);
            Assert.False(Assert.Single(channel.Settlements).Acknowledge);
            Assert.Equal(ArticleWorkResponsePublisherState.Stopped, context.Publisher.State);
        }

        /// <summary>
        /// Stale channelStillCurrent after successful confirm suppresses ack (same invariant as generation change).
        /// </summary>
        [Fact]
        public async Task Shutdown_after_confirm_before_ack_does_not_ack_on_a_stale_context()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);
            var seen = 0;

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                () => Interlocked.Increment(ref seen) == 1,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Single(context.PublishChannel.Publications);
            Assert.Empty(channel.Settlements);
        }

        /// <summary>
        /// Direct <see cref="ArticleWorkResponsePublisher.PublishAsync"/> never ack/nacks the consumer delivery channel.
        /// </summary>
        [Fact]
        public async Task Publisher_never_settles_the_original_delivery()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
            var intent = new ArticleWorkResponseIntent(
                ArticleWorkOutcome.Success,
                Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId),
                ArticleWorkTestDeliveries.CanonicalMessageId,
                "Giganews",
                ArticleWorkTestDeliveries.CanonicalCorrelationId,
                ArticleWorkTestDeliveries.CanonicalReplyTo,
                Error: null,
                ArticleWorkTestDeliveries.CanonicalFqdn,
                ArticleWorkTestDeliveries.CanonicalListenerPort,
                ArticleWorkTestDeliveries.CanonicalArticleIdHex);
            var consumer = new FakeBackFillerRabbitMqChannel(1);

            await context.Publisher.PublishAsync(intent, CancellationToken.None);

            Assert.Empty(consumer.Settlements);
            Assert.Single(context.PublishChannel.Publications);
        }

        /// <summary>
        /// Single pipeline pass after publish nack: one handler invocation, one publication attempt, one NACK.
        /// </summary>
        [Fact]
        public async Task One_delivery_is_not_retried_locally_after_publish_failure()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Nack);
            var handler = SuccessHandler();
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(handler, context.Publisher, 1024);

            await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(1, handler.HandleCount);
            Assert.Single(context.PublishChannel.Publications);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// Confirmed mandatory unroutable Success is terminal: NACK without requeue, one handler invocation.
        /// </summary>
        [Fact]
        public async Task Unroutable_success_response_nacks_without_requeue_and_without_a_local_execution_loop()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Unroutable);
            var handler = SuccessHandler();
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(handler, context.Publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Equal(1, handler.HandleCount);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
        }

        /// <summary>
        /// Unroutable success publication logs request id, correlation, ReplyTo, and outcome, then terminal NACK.
        /// </summary>
        [Fact]
        public async Task Unroutable_success_publication_is_logged_with_request_id_and_outcome()
        {
            var logger = new CollectingLogger<ArticleWorkResponsePublisher>();
            await using var context = await PublicationContext.StartAsync(
                FakePublishConfirmBehavior.Unroutable,
                logger);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
            Assert.Contains(
                logger.Messages,
                static message =>
                    message.Contains(ArticleWorkTestDeliveries.CanonicalRequestId, StringComparison.Ordinal)
                    && message.Contains(ArticleWorkTestDeliveries.CanonicalMessageId, StringComparison.Ordinal)
                    && message.Contains("Giganews", StringComparison.Ordinal)
                    && message.Contains("Success", StringComparison.Ordinal)
                    && message.Contains("unroutable", StringComparison.OrdinalIgnoreCase)
                    && message.Contains("not be requeued", StringComparison.OrdinalIgnoreCase)
                    && message.Contains("dead-letter", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Terminal unroutable settlement discards without requeue and does not claim a dead-letter destination.
        /// </summary>
        [Fact]
        public async Task Unroutable_terminal_settlement_is_discard_without_requeue_not_dead_letter_claim()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Unroutable);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);

            _ = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
            // Architecture: no DLX on Article Work queues; this is broker discard, not dead-letter.
            Assert.False(
                BackFillerArticleWorkTopology.BuildQueueArguments(
                        new BackFillerRabbitMqRuntimeOptions(
                            WorkRequestMaxPayloadBytes: 1024,
                            PublishConfirmTimeoutSeconds: 10,
                            ConsumerPrefetchCount: 1,
                            QueueTopology: new ArticleWorkQueueTopology(
                                ArticleWorkQueueKind.Classic,
                                ConfiguredHostCount: 1,
                                QuorumInitialGroupSize: null),
                            DeliveryLimit: 10))
                    .ContainsKey("x-dead-letter-exchange"));
        }

        /// <summary>
        /// Ambiguous/non-unroutable publish exceptions remain retryable (NACK requeue).
        /// </summary>
        [Fact]
        public async Task Ambiguous_publish_exception_is_not_classified_as_unroutable()
        {
            var publisher = new RecordingArticleWorkResponsePublisher
            {
                CompletesSuccessPublication = true,
                PublishException = new InvalidOperationException("some other publish failure"),
            };
            var handler = SuccessHandler();
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// Typed <see cref="RabbitMqPublicationUnroutableException"/> is terminal even when thrown by a test double.
        /// </summary>
        [Fact]
        public async Task Typed_unroutable_exception_from_publisher_nacks_without_requeue()
        {
            var publisher = new RecordingArticleWorkResponsePublisher
            {
                CompletesSuccessPublication = true,
                PublishException = new RabbitMqPublicationUnroutableException(),
            };
            var handler = SuccessHandler();
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Equal(1, handler.HandleCount);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
        }

        /// <summary>
        /// Publisher-confirm NACK (non-return) remains retryable and must not be treated as unroutable.
        /// </summary>
        [Fact]
        public async Task Publisher_confirm_nack_remains_retryable()
        {
            await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Nack);
            var handler = SuccessHandler();
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(handler, context.Publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Equal(1, handler.HandleCount);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// Static guard against blocking patterns in ArticleWork and RabbitMq production sources touched by publication.
        /// </summary>
        [Fact]
        public void Production_response_code_does_not_block_synchronously()
        {
            var directories = new[]
            {
                FindSourceDirectory("ArticleWork"),
                FindSourceDirectory("RabbitMq"),
            };
            foreach (var text in directories.SelectMany(static directory => Directory.GetFiles(directory, "*.cs")).Select(static file => File.ReadAllText(file)))
            {
                Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Handler returning canonical success endpoint fields for publication tests.
        /// </summary>
        /// <returns>Controllable handler configured for success JSON.</returns>
        private static ControllableArticleWorkHandler SuccessHandler() =>
            new()
            {
                Outcome = ArticleWorkOutcome.Success,
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                ArticleId = VectorNNTPD.Common.Articles.ArticleId.ParseLowerHex(
                    ArticleWorkTestDeliveries.CanonicalArticleIdHex),
            };

        /// <summary>
        /// Resolves <c>src/VectorNNTPD.BackFillerService/{folder}</c> from the test output directory.
        /// </summary>
        /// <param name="folder">Production subfolder name (for example <c>ArticleWork</c>).</param>
        /// <returns>Absolute source path.</returns>
        /// <exception cref="DirectoryNotFoundException">When the folder cannot be found.</exception>
        private static string FindSourceDirectory(string folder)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = RelativePath.Under(directory.FullName, "src", "VectorNNTPD.BackFillerService", folder);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException($"Could not locate src/VectorNNTPD.BackFillerService/{folder}.");
        }

        /// <summary>
        /// Started <see cref="ArticleWorkResponsePublisher"/> with fake connection factory for confirm behavior control.
        /// </summary>
        private sealed class PublicationContext : IAsyncDisposable
        {
            /// <summary>
            /// Binds factory, connection service, and running publisher for publication/settlement tests.
            /// </summary>
            /// <param name="factory">Fake factory supplying publish and consume channels.</param>
            /// <param name="connections">Started RabbitMQ service.</param>
            /// <param name="publisher">Running response publisher under test.</param>
            private PublicationContext(
                FakeBackFillerRabbitMqConnectionFactory factory,
                RabbitMqService connections,
                ArticleWorkResponsePublisher publisher)
            {
                Factory = factory;
                Connections = connections;
                Publisher = publisher;
            }

            /// <summary>Fake RabbitMQ factory recording channels and confirm hooks.</summary>
            public FakeBackFillerRabbitMqConnectionFactory Factory { get; }

            /// <summary>Connection service wired to the publisher.</summary>
            public RabbitMqService Connections { get; }

            /// <summary>Production publisher instance under test.</summary>
            public ArticleWorkResponsePublisher Publisher { get; }

            /// <summary>Current fake publish channel (throws if publisher channel is not the fake).</summary>
            internal FakeBackFillerRabbitMqPublishChannel PublishChannel =>
                Assert.IsType<FakeBackFillerRabbitMqPublishChannel>(Publisher.Channel);

            /// <summary>
            /// Connects, starts publisher with the given confirm behavior, and asserts running state.
            /// </summary>
            /// <param name="behavior">Simulated publisher confirm outcome.</param>
            /// <param name="logger">Optional logger for publication diagnostics.</param>
            /// <returns>Disposable publication test context.</returns>
            internal static async Task<PublicationContext> StartAsync(
                FakePublishConfirmBehavior behavior,
                ILogger<ArticleWorkResponsePublisher>? logger = null)
            {
                var factory = new FakeBackFillerRabbitMqConnectionFactory();
                var connections = RabbitMqServiceTests.CreateService(factory);
                await connections.StartAsync(CancellationToken.None);
                factory.LastConnection!.DefaultPublishConfirmBehavior = behavior;
                var publisher = new ArticleWorkResponsePublisher(
                    connections,
                    RabbitMqServiceTests.CreateFastRuntime(),
                    logger ?? NullLogger<ArticleWorkResponsePublisher>.Instance);
                await publisher.StartAsync(CancellationToken.None);
                Assert.Equal(ArticleWorkResponsePublisherState.Running, publisher.State);
                return new PublicationContext(factory, connections, publisher);
            }

            /// <summary>Stops publisher then disposes the connection service.</summary>
            public async ValueTask DisposeAsync()
            {
                await Publisher.DisposeAsync();
                await Connections.DisposeAsync();
            }
        }
    }
}
