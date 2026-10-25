using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Disposition planning and <see cref="ArticleWorkSettlementLease"/> invariants: single settlement, generation/channel gates, broker op serialization.
    /// </summary>
    public sealed class ArticleWorkSettlementTests
    {
        /// <summary>
        /// <see cref="ArticleWorkDispositionPlanner"/> maps each outcome to ack/requeue/publish flags for replyable, non-cancelled work.
        /// </summary>
        /// <param name="outcomeName">Outcome enum name.</param>
        /// <param name="acknowledge">Expected ack flag.</param>
        /// <param name="requeue">Expected requeue flag.</param>
        /// <param name="publish">Expected publish-response flag.</param>
        [Theory]
        [InlineData(nameof(ArticleWorkOutcome.Success), true, false, true)]
        [InlineData(nameof(ArticleWorkOutcome.ArticleNotFound), true, false, true)]
        [InlineData(nameof(ArticleWorkOutcome.InvalidArticle), true, false, true)]
        [InlineData(nameof(ArticleWorkOutcome.InvalidRequest), true, false, true)]
        [InlineData(nameof(ArticleWorkOutcome.ProviderFailure), true, false, true)]
        [InlineData(nameof(ArticleWorkOutcome.UnexpectedFailure), true, false, true)]
        [InlineData(nameof(ArticleWorkOutcome.RetentionRejected), true, false, true)]
        [InlineData(nameof(ArticleWorkOutcome.Cancelled), false, true, false)]
        public void Planner_maps_terminal_and_retryable_outcomes(
            string outcomeName,
            bool acknowledge,
            bool requeue,
            bool publish)
        {
            var outcome = Enum.Parse<ArticleWorkOutcome>(outcomeName);
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

        /// <summary>
        /// InvalidRequest still acks without requeue when reply coordinates are absent, but skips response publication.
        /// </summary>
        [Fact]
        public void InvalidRequest_without_reply_coordinates_does_not_publish()
        {
            var disposition = ArticleWorkDispositionPlanner.Create(
                ArticleWorkOutcome.InvalidRequest,
                replyable: false,
                cancellationRequested: false,
                deliveryCount: null,
                redelivered: false,
                deliveryLimit: 10);

            Assert.True(disposition.Acknowledge);
            Assert.False(disposition.Requeue);
            Assert.False(disposition.PublishResponse);
        }

        /// <summary>
        /// Cancellation requested converts even success disposition to nack/requeue without publishing.
        /// </summary>
        [Fact]
        public void Cancellation_overrides_a_terminal_outcome()
        {
            var disposition = ArticleWorkDispositionPlanner.Create(
                ArticleWorkOutcome.Success,
                replyable: true,
                cancellationRequested: true,
                deliveryCount: null,
                redelivered: false,
                deliveryLimit: 10);

            Assert.False(disposition.Acknowledge);
            Assert.True(disposition.Requeue);
            Assert.False(disposition.PublishResponse);
        }

        /// <summary>
        /// When broker delivery count reaches the configured limit, cancel/retryable settlements NACK without requeue.
        /// </summary>
        [Fact]
        public void Delivery_limit_exhaustion_nacks_without_requeue()
        {
            var disposition = ArticleWorkDispositionPlanner.Create(
                ArticleWorkOutcome.Cancelled,
                replyable: true,
                cancellationRequested: false,
                deliveryCount: 10,
                redelivered: true,
                deliveryLimit: 10);

            Assert.False(disposition.Acknowledge);
            Assert.False(disposition.Requeue);
            Assert.False(disposition.PublishResponse);
        }

        /// <summary>
        /// ProviderFailure is terminal regardless of delivery count (ACK + publish; no classic unbounded requeue).
        /// </summary>
        [Fact]
        public void ProviderFailure_is_terminal_even_when_delivery_count_is_high()
        {
            var disposition = ArticleWorkDispositionPlanner.Create(
                ArticleWorkOutcome.ProviderFailure,
                replyable: true,
                cancellationRequested: false,
                deliveryCount: 10,
                redelivered: true,
                deliveryLimit: 10);

            Assert.True(disposition.Acknowledge);
            Assert.False(disposition.Requeue);
            Assert.True(disposition.PublishResponse);
        }

        /// <summary>
        /// Cancellation that has already exhausted the delivery limit also discards without requeue.
        /// </summary>
        [Fact]
        public void Cancellation_with_exhausted_delivery_limit_does_not_requeue()
        {
            var disposition = ArticleWorkDispositionPlanner.Create(
                ArticleWorkOutcome.Success,
                replyable: true,
                cancellationRequested: true,
                deliveryCount: 10,
                redelivered: true,
                deliveryLimit: 10);

            Assert.False(disposition.Acknowledge);
            Assert.False(disposition.Requeue);
            Assert.False(disposition.PublishResponse);
        }

        /// <summary>
        /// Deferred handler surfaces provider failure as ACK + correlated failure reply (not Success, not requeue).
        /// </summary>
        [Fact]
        public async Task Valid_work_is_admitted_as_provider_failure_without_a_fake_success()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(
                new DeferredArticleWorkHandler(),
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
            Assert.False(string.IsNullOrWhiteSpace(intent.Error));
            Assert.NotEqual(ArticleWorkOutcome.Success, intent.Outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
            Assert.Equal(7UL, settlement.DeliveryTag);
        }

        /// <summary>
        /// Malformed request acks once, records InvalidRequest intent, no requeue.
        /// </summary>
        [Fact]
        public async Task InvalidRequest_acks_without_requeue_and_records_a_response_intent()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(
                new DeferredArticleWorkHandler(),
                publisher,
                1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Create("{"),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.InvalidRequest, outcome);
            var intent = Assert.Single(publisher.Published);
            Assert.Equal(ArticleWorkOutcome.InvalidRequest, intent.Outcome);
            Assert.Null(intent.RequestId);
            var settlement = Assert.Single(channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
        }

        /// <summary>
        /// Terminal handler outcomes publish once; duplicate <see cref="ArticleWorkSettlementLease.TrySettleAsync"/> returns false.
        /// </summary>
        /// <param name="handlerOutcomeName">Terminal outcome.</param>
        /// <param name="acknowledge">Expected ack on first settlement.</param>
        /// <param name="requeue">Expected requeue flag (false for terminal).</param>
        [Theory]
        [InlineData(nameof(ArticleWorkOutcome.Success), true, false)]
        [InlineData(nameof(ArticleWorkOutcome.ArticleNotFound), true, false)]
        [InlineData(nameof(ArticleWorkOutcome.InvalidArticle), true, false)]
        public async Task Terminal_handler_outcomes_publish_and_settle_exactly_once(
            string handlerOutcomeName,
            bool acknowledge,
            bool requeue)
        {
            var handlerOutcome = Enum.Parse<ArticleWorkOutcome>(handlerOutcomeName);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true };
            var handler = new ControllableArticleWorkHandler { Outcome = handlerOutcome, Error = "test" };
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);
            var delivery = ArticleWorkTestDeliveries.Canonical();

            var first = await pipeline.ProcessAsync(delivery, "Giganews", channel, static () => true, CancellationToken.None);
            var lease = handler.LastItem!.Settlement;
            var second = await lease.TrySettleAsync(
                ArticleWorkDispositionPlanner.Create(handlerOutcome, true, false, deliveryCount: null, redelivered: false, deliveryLimit: 10),
                channelStillCurrent: true,
                CancellationToken.None);

            Assert.Equal(handlerOutcome, first);
            Assert.False(second);
            Assert.Single(publisher.Published);
            var settlement = Assert.Single(channel.Settlements);
            Assert.Equal(acknowledge, settlement.Acknowledge);
            Assert.Equal(requeue, settlement.Requeue);
        }

        /// <summary>
        /// Handler exception maps to UnexpectedFailure with ACK, correlated failure reply, and prepared type+message error.
        /// </summary>
        [Fact]
        public async Task Unexpected_handler_failure_acks_with_terminal_failure_reply()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var handler = new ControllableArticleWorkHandler { Throw = new InvalidOperationException("boom") };
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Equal("InvalidOperationException: boom", pipeline.LastEscapedHandlerFailureError);
            var intent = Assert.Single(publisher.Published);
            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, intent.Outcome);
            Assert.Equal("InvalidOperationException: boom", intent.Error);
            Assert.True(Assert.Single(channel.Settlements).Acknowledge);
            Assert.False(Assert.Single(channel.Settlements).Requeue);
        }

        /// <summary>
        /// Cancelled token before processing yields Cancelled outcome with nack/requeue and no publication.
        /// </summary>
        [Fact]
        public async Task Cancellation_nacks_requeue_without_a_terminal_response()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(
                new DeferredArticleWorkHandler(),
                publisher,
                1024);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                cts.Token);

            Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
            Assert.Empty(publisher.Published);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// InvalidRequest NACK-requeues when the recording publisher throws before a reply is recorded.
        /// </summary>
        [Fact]
        public async Task InvalidRequest_publish_failure_nacks_with_requeue()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher
            {
                PublishException = new InvalidOperationException("confirm failed"),
            };
            var pipeline = new ArticleWorkDeliveryPipeline(
                new DeferredArticleWorkHandler(),
                publisher,
                1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Create("{"),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Empty(publisher.Published);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// Success handler with a publisher exception before a recorded reply NACK-requeues once and does not ACK.
        /// </summary>
        [Fact]
        public async Task Success_publish_failure_nacks_with_requeue_and_does_not_ack()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher
            {
                CompletesSuccessPublication = true,
                PublishException = new InvalidOperationException("confirm failed"),
            };
            var handler = new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Success };
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Empty(publisher.Published);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
            Assert.True(handler.LastItem!.Settlement.IsSettled);
            Assert.False(await handler.LastItem.Settlement.TrySettleAsync(
                new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: false),
                channelStillCurrent: true,
                CancellationToken.None));
        }

        /// <summary>
        /// A NACK that throws after a failed publish claims the lease once and does not try a second settlement.
        /// </summary>
        [Fact]
        public async Task Publish_failure_with_nack_exception_does_not_settle_twice()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1)
            {
                NackException = new InvalidOperationException("nack failed"),
            };
            var publisher = new RecordingArticleWorkResponsePublisher
            {
                CompletesSuccessPublication = true,
                PublishException = new InvalidOperationException("confirm failed"),
            };
            var handler = new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Success };
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.Empty(channel.Settlements);
            Assert.True(handler.LastItem!.Settlement.IsSettled);
            Assert.False(await handler.LastItem.Settlement.TrySettleAsync(
                new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false),
                channelStillCurrent: true,
                CancellationToken.None));
            Assert.Empty(channel.Settlements);
        }

        /// <summary>
        /// Lease settles only on the channel instance captured at admission; replacement channel is not original.
        /// </summary>
        [Fact]
        public async Task Settlement_uses_only_the_original_channel()
        {
            var original = new FakeBackFillerRabbitMqChannel(1);
            var replacement = new FakeBackFillerRabbitMqChannel(2);
            var lease = new ArticleWorkSettlementLease(original, 11, 1);
            var disposition = new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: false);

            Assert.True(lease.IsOriginalChannel(original));
            Assert.False(lease.IsOriginalChannel(replacement));
            Assert.True(await lease.TrySettleAsync(disposition, channelStillCurrent: true, CancellationToken.None));
            Assert.Single(original.Settlements);
            Assert.Empty(replacement.Settlements);
        }

        /// <summary>
        /// Closed original channel with wrong <c>channelStillCurrent</c> predicate does not settle on any channel.
        /// </summary>
        [Fact]
        public async Task Stale_channel_cannot_settle_through_a_replacement()
        {
            var original = new FakeBackFillerRabbitMqChannel(1);
            var replacement = new FakeBackFillerRabbitMqChannel(2);
            var lease = new ArticleWorkSettlementLease(original, 4, 1);
            original.IsOpen = false;

            var settled = await lease.TrySettleAsync(
                new ArticleWorkDisposition(false, true, false),
                channelStillCurrent: lease.IsOriginalChannel(replacement),
                CancellationToken.None);

            Assert.False(settled);
            Assert.False(lease.IsSettled);
            Assert.Empty(original.Settlements);
            Assert.Empty(replacement.Settlements);
        }

        /// <summary>
        /// <c>channelStillCurrent: false</c> suppresses settlement even if the channel object remains open.
        /// </summary>
        [Fact]
        public async Task Lost_generation_skips_settlement_even_when_the_old_channel_object_is_still_open()
        {
            var original = new FakeBackFillerRabbitMqChannel(1);
            var lease = new ArticleWorkSettlementLease(original, 5, 1);

            var settled = await lease.TrySettleAsync(
                new ArticleWorkDisposition(false, false, false),
                channelStillCurrent: false,
                CancellationToken.None);

            Assert.False(settled);
            Assert.False(lease.IsSettled);
            Assert.Empty(original.Settlements);
        }

        /// <summary>
        /// Second TrySettle after success returns false; first broker op wins.
        /// </summary>
        [Fact]
        public async Task Duplicate_settlement_is_prevented()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var lease = new ArticleWorkSettlementLease(channel, 3, 1);
            var nack = new ArticleWorkDisposition(false, false, false);

            Assert.True(await lease.TrySettleAsync(nack, true, CancellationToken.None));
            Assert.False(await lease.TrySettleAsync(new ArticleWorkDisposition(true, false, false), true, CancellationToken.None));
            Assert.True(lease.IsSettled);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
        }

        /// <summary>
        /// Handler completing after reconnect must not publish or ack on stale or replacement consume channels.
        /// </summary>
        [Fact]
        public async Task In_flight_delivery_does_not_settle_on_a_replaced_generation()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);
            var session = new ArticleWorkConsumerSession(
                "Giganews",
                1,
                pipeline,
                connections,
                NullLogger.Instance);
            await session.StartAsync(CancellationToken.None);
            var firstChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel);

            var processing = firstChannel.DeliverAsync(ArticleWorkTestDeliveries.Canonical(generation: 1));
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;
            factory.LastConnection!.SimulateLost();
            await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));

            handler.Gate!.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));

            var replacement = new ArticleWorkConsumerSession(
                "Giganews",
                1,
                pipeline,
                connections,
                NullLogger.Instance);
            await replacement.StartAsync(CancellationToken.None);
            var secondChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(replacement.Channel);

            Assert.Empty(firstChannel.Settlements);
            Assert.Empty(secondChannel.Settlements);
            Assert.Empty(publisher.Published);
            Assert.Equal(2, factory.ConnectCount);
            Assert.Equal(1, session.Generation);
            Assert.Equal(2, replacement.Generation);

            await session.DisposeAsync();
            await replacement.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Concurrent nacks on one channel serialize through the settlement gate (max one broker op in flight).
        /// </summary>
        [Fact]
        public async Task Two_nacks_on_one_channel_do_not_enter_together()
        {
            var channel = GatedChannel();
            var first = new ArticleWorkSettlementLease(channel, 7, channel.Generation);
            var second = new ArticleWorkSettlementLease(channel, 8, channel.Generation);
            var nack = new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false);

            var firstSettle = first.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None);
            await channel.NackEntered!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var secondSettle = second.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => ArticleWorkSettlementLease.SettlementWaiters(channel) >= 1 || channel.MaxBrokerOpsInFlight >= 2,
                TimeSpan.FromSeconds(2));

            Assert.Equal(1, channel.MaxBrokerOpsInFlight);
            Assert.Equal(1, ArticleWorkSettlementLease.SettlementWaiters(channel));
            Assert.False(secondSettle.IsCompleted);
            channel.NackGate!.TrySetResult();

            Assert.True(await firstSettle);
            Assert.True(await secondSettle);
            Assert.Equal(2, channel.Settlements.Count);
            Assert.Equal(1, channel.MaxBrokerOpsInFlight);
            Assert.True(first.IsSettled);
            Assert.True(second.IsSettled);
        }

        /// <summary>
        /// BasicCancel waits until an in-flight nack settlement releases the exclusive gate.
        /// </summary>
        [Fact]
        public async Task Basic_cancel_does_not_enter_while_a_nack_holds_the_channel()
        {
            var channel = GatedChannel();
            var holder = new ArticleWorkSettlementLease(channel, 7, channel.Generation);
            var nack = new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false);
            var holding = holder.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None);
            await channel.NackEntered!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var cancel = ArticleWorkSettlementLease.RunExclusiveAsync(
                channel,
                () => channel.BasicCancelAsync("ctag", CancellationToken.None));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => ArticleWorkSettlementLease.SettlementWaiters(channel) >= 1 || channel.CancelCount > 0,
                TimeSpan.FromSeconds(2));

            Assert.Equal(0, channel.CancelCount);
            Assert.Equal(1, ArticleWorkSettlementLease.SettlementWaiters(channel));
            channel.NackGate!.TrySetResult();
            Assert.True(await holding);
            await cancel.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, channel.CancelCount);
        }

        /// <summary>
        /// Ack and nack settlements on the same channel do not overlap broker operations.
        /// </summary>
        [Fact]
        public async Task Ack_and_nack_on_one_channel_do_not_enter_together()
        {
            var channel = GatedChannel();
            channel.AckStarted = NewSource();
            channel.AckGate = NewSource();
            var ackLease = new ArticleWorkSettlementLease(channel, 7, channel.Generation);
            var nackLease = new ArticleWorkSettlementLease(channel, 8, channel.Generation);
            var ack = new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: false);
            var nack = new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false);

            var ackSettle = ackLease.TrySettleAsync(ack, channelStillCurrent: true, CancellationToken.None);
            await channel.AckStarted!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var nackSettle = nackLease.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None);
            try
            {
                await ArticleWorkTestDeliveries.WaitUntilAsync(
                    () => ArticleWorkSettlementLease.SettlementWaiters(channel) >= 1 || channel.MaxBrokerOpsInFlight >= 2,
                    TimeSpan.FromSeconds(2));

                Assert.Equal(1, channel.MaxBrokerOpsInFlight);
                Assert.False(nackSettle.IsCompleted);
                Assert.False(channel.NackEntered!.Task.IsCompleted);
            }
            finally
            {
                channel.AckGate.TrySetResult();
                channel.NackGate!.TrySetResult();
            }

            Assert.True(await ackSettle);
            Assert.True(await nackSettle);
            Assert.Equal(2, channel.Settlements.Count);
            Assert.Equal(1, channel.MaxBrokerOpsInFlight);
        }

        /// <summary>
        /// Cancelled TrySettle while waiting does not mark settled; a later call completes the second delivery tag.
        /// </summary>
        [Fact]
        public async Task Cancelled_wait_does_not_claim_the_lease_and_a_later_call_settles_once()
        {
            var channel = GatedChannel();
            var holder = new ArticleWorkSettlementLease(channel, 7, channel.Generation);
            var waiting = new ArticleWorkSettlementLease(channel, 8, channel.Generation);
            var nack = new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            var holding = holder.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None);
            await channel.NackEntered!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var blocked = waiting.TrySettleAsync(nack, channelStillCurrent: true, cancelled.Token);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => ArticleWorkSettlementLease.SettlementWaiters(channel) >= 1,
                TimeSpan.FromSeconds(2));
            Assert.False(waiting.IsSettled);
            channel.NackGate!.TrySetResult();

            Assert.True(await holding);
            Assert.False(await blocked);
            Assert.False(waiting.IsSettled);
            Assert.True(await waiting.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None));
            Assert.Equal(2, channel.Settlements.Count);
            Assert.Equal(new ulong[] { 7, 8 }, channel.Settlements.Select(static settlement => settlement.DeliveryTag));
        }

        /// <summary>
        /// Failed broker nack marks lease settled once; waiter still completes settlement for the other tag.
        /// </summary>
        [Fact]
        public async Task Broker_failure_claims_the_lease_once_and_the_waiter_still_settles()
        {
            var channel = GatedChannel();
            var failed = new ArticleWorkSettlementLease(channel, 7, channel.Generation);
            var waiting = new ArticleWorkSettlementLease(channel, 8, channel.Generation);
            var nack = new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false);

            var failing = failed.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None);
            await channel.NackEntered!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var blocked = waiting.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => ArticleWorkSettlementLease.SettlementWaiters(channel) >= 1,
                TimeSpan.FromSeconds(2));
            channel.NackException = new InvalidOperationException("broker nack failed");
            channel.NackGate!.TrySetResult();

            Assert.False(await failing);
            Assert.True(failed.IsSettled);
            Assert.False(await failed.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None));
            Assert.True(await blocked);
            Assert.Equal(8UL, Assert.Single(channel.Settlements).DeliveryTag);
        }

        /// <summary>
        /// Channel closed while second waiter is queued prevents that waiter from settling afterward.
        /// </summary>
        [Fact]
        public async Task Closed_channel_does_not_claim_a_waiting_lease()
        {
            var channel = GatedChannel();
            var holder = new ArticleWorkSettlementLease(channel, 7, channel.Generation);
            var waiting = new ArticleWorkSettlementLease(channel, 8, channel.Generation);
            var nack = new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false);

            var holding = holder.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None);
            await channel.NackEntered!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var blocked = waiting.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => ArticleWorkSettlementLease.SettlementWaiters(channel) >= 1,
                TimeSpan.FromSeconds(2));
            channel.IsOpen = false;
            channel.NackGate!.TrySetResult();

            Assert.True(await holding);
            Assert.False(await blocked);
            Assert.False(waiting.IsSettled);
            Assert.False(await waiting.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None));
            Assert.Equal(7UL, Assert.Single(channel.Settlements).DeliveryTag);
        }

        /// <summary>
        /// Settlement gates are per-channel/generation; nacks on two channels may run concurrently.
        /// </summary>
        [Fact]
        public async Task Replacement_channel_uses_an_independent_gate()
        {
            var original = GatedChannel();
            var replacement = new FakeBackFillerRabbitMqChannel(original.Generation + 1)
            {
                NackEntered = NewSource(),
                NackGate = NewSource(),
            };
            var oldLease = new ArticleWorkSettlementLease(original, 7, original.Generation);
            var newLease = new ArticleWorkSettlementLease(replacement, 8, replacement.Generation);
            var nack = new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false);

            var oldSettle = oldLease.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None);
            var newSettle = newLease.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None);
            await original.NackEntered!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await replacement.NackEntered!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, original.MaxBrokerOpsInFlight);
            Assert.Equal(1, replacement.MaxBrokerOpsInFlight);
            original.NackGate!.TrySetResult();
            replacement.NackGate!.TrySetResult();
            Assert.True(await oldSettle);
            Assert.True(await newSettle);
        }

        /// <summary>
        /// Channel dispose waits for in-flight nack; concurrent dispose calls serialize after the settlement completes.
        /// </summary>
        [Fact]
        public async Task Dispose_does_not_enter_while_a_nack_holds_the_channel()
        {
            var channel = GatedChannel();
            var holder = new ArticleWorkSettlementLease(channel, 7, channel.Generation);
            var nack = new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false);
            var holding = holder.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None);
            await channel.NackEntered!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var firstDispose = channel.DisposeAsync();
            var secondDispose = channel.DisposeAsync();
            try
            {
                await ArticleWorkTestDeliveries.WaitUntilAsync(
                    () => ArticleWorkSettlementLease.SettlementWaiters(channel) >= 2 || channel.DisposeCount > 0,
                    TimeSpan.FromSeconds(2));

                Assert.Equal(0, channel.DisposeCount);
                Assert.Equal(2, ArticleWorkSettlementLease.SettlementWaiters(channel));
                Assert.True(channel.IsOpen);
                Assert.Equal(1, channel.MaxBrokerOpsInFlight);
            }
            finally
            {
                channel.NackGate!.TrySetResult();
            }

            Assert.True(await holding);
            await firstDispose;
            await secondDispose;
            Assert.Equal(2, channel.DisposeCount);
            Assert.False(channel.IsOpen);
            Assert.Equal(1, channel.MaxBrokerOpsInFlight);
            Assert.Equal(7UL, Assert.Single(channel.Settlements).DeliveryTag);
        }

        /// <summary>
        /// Lease generation mismatch with open channel suppresses settlement (stale delivery tag generation).
        /// </summary>
        [Fact]
        public async Task Stale_generation_check_does_not_settle_the_current_channel()
        {
            var channel = new FakeBackFillerRabbitMqChannel(2);
            var lease = new ArticleWorkSettlementLease(channel, 7, generation: 1);
            var nack = new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false);

            Assert.False(await lease.TrySettleAsync(nack, channelStillCurrent: true, CancellationToken.None));
            Assert.False(lease.IsSettled);
            Assert.Empty(channel.Settlements);
        }

        /// <summary>
        /// Fake consume channel with nack enter/complete gates for exclusive settlement tests.
        /// </summary>
        /// <returns>Channel with <see cref="FakeBackFillerRabbitMqChannel.NackEntered"/> and <see cref="FakeBackFillerRabbitMqChannel.NackGate"/> wired.</returns>
        private static FakeBackFillerRabbitMqChannel GatedChannel() =>
            new(1)
            {
                NackEntered = NewSource(),
                NackGate = NewSource(),
            };

        /// <summary>
        /// TaskCompletionSource configured for async test coordination without inline continuations.
        /// </summary>
        /// <returns>New completion source for gates and startup signals.</returns>
        private static TaskCompletionSource NewSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
