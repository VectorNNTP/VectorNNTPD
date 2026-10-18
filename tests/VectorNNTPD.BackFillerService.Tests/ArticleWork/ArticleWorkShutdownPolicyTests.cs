using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;


namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Proves shutdown follows the captured drain and finish flags, and that a cancelled delivery is requeued instead of acknowledged.
    /// </summary>
    public sealed class ArticleWorkShutdownPolicyTests
    {
        /// <summary>
        /// With both flags true, a queued delivery starts during retirement and both deliveries are acknowledged after they finish.
        /// </summary>
        [Fact]
        public async Task Drain_true_finish_true_queued_work_starts_and_active_work_finishes()
        {
            var firstStarted = NewSource();
            var firstGate = NewSource();
            var secondStarted = NewSource();
            var handler = SuccessHandler();
            handler.Stages.Enqueue(new ArticleWorkControlStage(firstStarted, firstGate, ArticleWorkOutcome.Success, Fqdn: ArticleWorkTestDeliveries.CanonicalFqdn, ListenerPort: ArticleWorkTestDeliveries.CanonicalListenerPort, ArticleId: CanonicalArtId()));
            handler.Stages.Enqueue(new ArticleWorkControlStage(secondStarted, Gate: null, ArticleWorkOutcome.Success, Fqdn: ArticleWorkTestDeliveries.CanonicalFqdn, ListenerPort: ArticleWorkTestDeliveries.CanonicalListenerPort, ArticleId: CanonicalArtId()));
            await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: true, prefetch: 2);
            var first = context.Deliver(7);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = context.Deliver(8);
            await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);

            var retire = context.Session.RetireAsync();
            await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);
            firstGate.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await second.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, handler.HandleCount);
            Assert.Equal(2, context.Channel.Settlements.Count);
            Assert.All(context.Channel.Settlements, static settlement => Assert.True(settlement.Acknowledge));
            Assert.Equal(2, context.Publisher.Published.Count);
            Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
        }

        /// <summary>
        /// Drain allows the queued delivery to start, but finish-false cancels both and requeues them without publishing a response.
        /// </summary>
        [Fact]
        public async Task Drain_true_finish_false_queued_work_may_start_and_active_work_is_cancelled()
        {
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                Started = NewSource(),
                Gate = NewSource(),
            };
            await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: false, prefetch: 2);
            var first = context.Deliver(7);
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = context.Deliver(8);
            await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);

            var retire = context.Session.RetireAsync();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await second.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, handler.HandleCount);
            Assert.Equal(2, context.Channel.Settlements.Count);
            Assert.All(context.Channel.Settlements, static settlement =>
            {
                Assert.False(settlement.Acknowledge);
                Assert.True(settlement.Requeue);
            });
            Assert.Empty(context.Publisher.Published);
        }

        /// <summary>
        /// Drain-false requeues the delivery that has not entered processing, then acknowledges the one that was already active.
        /// </summary>
        [Fact]
        public async Task Drain_false_finish_true_queued_work_is_requeued_while_active_work_finishes()
        {
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                Started = NewSource(),
                Gate = NewSource(),
            };
            await using var context = await ShutdownContext.StartAsync(handler, drainQueued: false, finishActive: true, prefetch: 2);
            var first = context.Deliver(7);
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = context.Deliver(8);
            await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);

            var retire = context.Session.RetireAsync();
            await second.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, handler.HandleCount);
            var queued = Assert.Single(context.Channel.Settlements);
            Assert.Equal(8UL, queued.DeliveryTag);
            Assert.False(queued.Acknowledge);
            Assert.True(queued.Requeue);
            Assert.Empty(context.Publisher.Published);

            handler.Gate!.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, context.Channel.Settlements.Count);
            var active = context.Channel.Settlements.Single(static settlement => settlement.DeliveryTag == 7);
            Assert.True(active.Acknowledge);
            Assert.False(active.Requeue);
            Assert.Single(context.Publisher.Published);
            Assert.DoesNotContain(context.Channel.Settlements, static settlement => settlement.DeliveryTag == 8 && settlement.Acknowledge);
        }

        /// <summary>
        /// Both flags false: the queued delivery never starts, the active one is cancelled, and both are requeued.
        /// </summary>
        [Fact]
        public async Task Drain_false_finish_false_queued_work_does_not_start_and_active_work_is_cancelled()
        {
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                Started = NewSource(),
                Gate = NewSource(),
            };
            await using var context = await ShutdownContext.StartAsync(handler, drainQueued: false, finishActive: false, prefetch: 2);
            var first = context.Deliver(7);
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = context.Deliver(8);
            await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);

            var retire = context.Session.RetireAsync();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await second.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, handler.HandleCount);
            Assert.Equal(2, context.Channel.Settlements.Count);
            Assert.All(context.Channel.Settlements, static settlement =>
            {
                Assert.False(settlement.Acknowledge);
                Assert.True(settlement.Requeue);
            });
            Assert.Empty(context.Publisher.Published);
            Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
        }

        /// <summary>
        /// A queued NACK waits until the in-flight NACK leaves the broker operation, so the two settlements do not overlap.
        /// </summary>
        [Fact]
        public async Task Cancelled_queued_settlement_does_not_overlap_the_active_delivery_nack()
        {
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                Started = NewSource(),
                Gate = NewSource(),
            };
            await using var context = await ShutdownContext.StartAsync(handler, drainQueued: false, finishActive: false, prefetch: 2);
            var first = context.Deliver(7);
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = context.Deliver(8);
            await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);

            context.Channel.NackEntered = NewSource();
            context.Channel.NackGate = NewSource();
            var retire = context.Session.RetireAsync();
            try
            {
                await context.Channel.NackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await ArticleWorkTestDeliveries.WaitUntilAsync(
                    () => ArticleWorkSettlementLease.SettlementWaiters(context.Channel) >= 1 || context.Channel.MaxNackInFlight >= 2,
                    TimeSpan.FromSeconds(2));
                Assert.Equal(1, context.Channel.MaxNackInFlight);
                Assert.Equal(1, context.Channel.MaxBrokerOpsInFlight);
                Assert.Equal(1, ArticleWorkSettlementLease.SettlementWaiters(context.Channel));
                Assert.False(second.IsCompleted);
            }
            finally
            {
                context.Channel.NackGate.TrySetResult();
            }

            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await second.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, handler.HandleCount);
            Assert.Equal(1, context.Channel.MaxNackInFlight);
            Assert.Equal(2, context.Channel.Settlements.Count);
            Assert.All(context.Channel.Settlements, static settlement =>
            {
                Assert.False(settlement.Acknowledge);
                Assert.True(settlement.Requeue);
            });
            Assert.Empty(context.Publisher.Published);
            Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
        }

        /// <summary>
        /// Cancelling the retirement token stops finish-true work without acknowledging it or waiting for the handler gate.
        /// </summary>
        [Fact]
        public async Task Grace_expiry_cancels_finish_true_work_and_does_not_block_shutdown()
        {
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                Started = NewSource(),
                Gate = NewSource(),
            };
            await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: true);
            var processing = context.Deliver(7);
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var grace = new CancellationTokenSource();
            var retire = context.Session.RetireAsync(grace.Token);
            await grace.CancelAsync();
            await retire.WaitAsync(TimeSpan.FromSeconds(2));
            await processing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
            Assert.DoesNotContain(context.Channel.Settlements, static settlement => settlement.Acknowledge);
            Assert.Empty(context.Publisher.Published);
        }

        /// <summary>
        /// Retirement while dispatch is held still treats the delivery as queued, so drain-false requeues it before the handler runs.
        /// </summary>
        [Fact]
        public async Task Shutdown_during_queued_to_active_transition_uses_queued_policy()
        {
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
            };
            await using var context = await ShutdownContext.StartAsync(handler, drainQueued: false, finishActive: true);
            context.Session.DispatchAcquired = NewSource();
            context.Session.DispatchAcquireHold = NewSource();
            var processing = context.Deliver(7);
            await context.Session.DispatchAcquired.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, context.Session.ActiveCount);
            Assert.Equal(1, context.Session.QueuedCount);

            var retire = context.Session.RetireAsync();
            context.Session.DispatchAcquireHold.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, handler.HandleCount);
            var settlement = Assert.Single(context.Channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// Retirement during a blocked provider ARTICLE cancels the work and requeues it. The response is not published.
        /// </summary>
        [Fact]
        public async Task Shutdown_as_provider_retrieval_completes_does_not_ack()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            var block = NewSource();
            var server = harness.EnqueueArticle(ArticleWorkTestArticles.Valid(), block);
            await using var context = await ShutdownContext.StartAsync(
                harness.Handler,
                harness.Publisher,
                drainQueued: true,
                finishActive: false,
                connections: harness.Connections);
            var processing = context.Deliver(7);
            await server.ArticleStarted!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var retire = context.Session.RetireAsync();
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Session.WorkCancellationToken.IsCancellationRequested,
                TimeSpan.FromSeconds(2));
            block.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.DoesNotContain(context.Channel.Settlements, static settlement => settlement.Acknowledge);
            Assert.Empty(harness.PublishChannel.Publications);
        }

        /// <summary>
        /// Retirement after the article is retained but before publish requeues the delivery and leaves the retained article in place.
        /// </summary>
        [Fact]
        public async Task Shutdown_after_retention_before_publish_keeps_the_article_and_does_not_ack()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.EnqueueArticle(ArticleWorkTestArticles.Valid());
            var publisher = new GatedArticleWorkResponsePublisher { Gate = NewSource() };
            await using var context = await ShutdownContext.StartAsync(
                harness.Handler,
                publisher,
                drainQueued: true,
                finishActive: false,
                connections: harness.Connections);
            var processing = context.Deliver(7);
            await publisher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, harness.Retention.RetainedCount);

            var retire = context.Session.RetireAsync();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            var settlement = Assert.Single(context.Channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
            Assert.Empty(publisher.Published);
            Assert.Equal(1, harness.Retention.RetainedCount);
        }

        /// <summary>
        /// Retirement while the response publish is waiting for a confirm requeues the delivery instead of acknowledging it.
        /// </summary>
        [Fact]
        public async Task Shutdown_after_publish_before_confirm_does_not_ack()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync(FakePublishConfirmBehavior.Wait);
            harness.EnqueueArticle(ArticleWorkTestArticles.Valid());
            harness.PublishChannel.Enqueued = NewSource();
            await using var context = await ShutdownContext.StartAsync(
                harness.Handler,
                harness.Publisher,
                drainQueued: true,
                finishActive: false,
                connections: harness.Connections);
            var processing = context.Deliver(7);
            await harness.PublishChannel.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var retire = context.Session.RetireAsync();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(Assert.Single(context.Channel.Settlements).Acknowledge);
            Assert.True(context.Channel.Settlements[0].Requeue);
        }

        /// <summary>
        /// Once the response publish has been confirmed, retirement still acknowledges the current delivery.
        /// </summary>
        [Fact]
        public async Task Shutdown_after_confirm_before_ack_still_acks_when_the_context_is_current()
        {
            var handler = SuccessHandler();
            var publisher = new GatedArticleWorkResponsePublisher { AfterConfirmHold = NewSource() };
            await using var context = await ShutdownContext.StartAsync(handler, publisher, drainQueued: true, finishActive: false);
            var processing = context.Deliver(7);
            await publisher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Single(publisher.Published);

            var retire = context.Session.RetireAsync();
            publisher.AfterConfirmHold.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(Assert.Single(context.Channel.Settlements).Acknowledge);
        }

        /// <summary>
        /// Retirement that arrives after ACK has started does not cancel the work or record a second settlement.
        /// </summary>
        [Fact]
        public async Task Shutdown_during_ack_does_not_double_settle()
        {
            var handler = SuccessHandler();
            await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: true);
            context.Channel.AckStarted = NewSource();
            context.Channel.AckGate = NewSource();
            var processing = context.Deliver(7);
            await context.Channel.AckStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var retire = context.Session.RetireAsync();
            context.Channel.AckGate.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(context.Session.WorkCancellationToken.IsCancellationRequested);
            Assert.True(Assert.Single(context.Channel.Settlements).Acknowledge);
            Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
        }

        /// <summary>
        /// Grace cancellation while ACK is blocked does not dispose the channel until the ACK returns, then the delivery is still acknowledged.
        /// </summary>
        [Fact]
        public async Task Grace_expiry_during_blocked_ack_cancels_work_before_the_gate_is_released()
        {
            var handler = SuccessHandler();
            await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: true);
            context.Channel.AckStarted = NewSource();
            context.Channel.AckGate = NewSource();
            var processing = context.Deliver(7);
            await context.Channel.AckStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            using var grace = new CancellationTokenSource();
            var retire = context.Session.RetireAsync(grace.Token);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => ArticleWorkSettlementLease.SettlementWaiters(context.Channel) >= 1,
                TimeSpan.FromSeconds(2));
            Assert.Equal(0, context.Channel.CancelCount);
            Assert.Equal(0, context.Channel.DisposeCount);
            Assert.False(context.Session.WorkCancellationToken.IsCancellationRequested);

            await grace.CancelAsync();
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Session.WorkCancellationToken.IsCancellationRequested,
                TimeSpan.FromSeconds(2));
            Assert.Equal(0, context.Channel.CancelCount);
            Assert.Equal(0, context.Channel.DisposeCount);
            Assert.False(retire.IsCompleted);

            context.Channel.AckGate.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            var settlement = Assert.Single(context.Channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.Equal(1, context.Channel.CancelCount);
            Assert.True(context.Channel.DisposeCount >= 1);
            Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
        }

        /// <summary>
        /// Retirement while the provider connect is blocked requeues the delivery and does not publish a response.
        /// </summary>
        [Fact]
        public async Task Shutdown_during_provider_session_acquisition_does_not_ack()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            harness.Nntp.ConnectStarted = NewSource();
            harness.Nntp.BlockConnect = NewSource();
            await using var context = await ShutdownContext.StartAsync(
                harness.Handler,
                harness.Publisher,
                drainQueued: true,
                finishActive: false,
                connections: harness.Connections);
            var processing = context.Deliver(7);
            await harness.Nntp.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var retire = context.Session.RetireAsync();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(Assert.Single(context.Channel.Settlements).Acknowledge);
            Assert.True(context.Channel.Settlements[0].Requeue);
            Assert.Empty(harness.PublishChannel.Publications);
        }

        /// <summary>
        /// Stopping the consumer does not cancel an account refresh that is already inside its query. The refresh token still does.
        /// </summary>
        [Fact]
        public async Task Consumer_retirement_does_not_cancel_an_in_flight_mysql_refresh()
        {
            await using var harness = await BackFillerPipelineHarness.StartAsync();
            var runtime = RabbitMqServiceTests.CreateFastRuntime() with
            {
                Shutdown = new BackFillerShutdownRuntimeOptions(TimeSpan.FromSeconds(30), DrainQueuedWork: false, FinishActiveArticles: false),
            };
            var consumer = new ArticleWorkConsumerService(
                harness.Connections,
                runtime,
                harness.Handler,
                harness.Publisher,
                NullLogger<ArticleWorkConsumerService>.Instance);
            await consumer.StartAsync(CancellationToken.None);
            harness.Accounts.Block = NewSource();
            harness.Accounts.BlockAfterQueryCount = harness.Accounts.QueryCount + 1;
            using var queryCts = new CancellationTokenSource();
            var query = harness.AccountService.RefreshOnceAsync(queryCts.Token);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => harness.AccountService.RefreshInProgress,
                TimeSpan.FromSeconds(2));

            await consumer.StopAsync(CancellationToken.None);
            Assert.True(harness.AccountService.RefreshInProgress);
            Assert.False(query.IsCompleted);

            await queryCts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.WaitAsync(TimeSpan.FromSeconds(2)));
            harness.Accounts.Block.TrySetResult();
            await consumer.DisposeAsync();
        }

        /// <summary>
        /// Finish-false retirement records one requeue and no acknowledgement for the cancelled delivery.
        /// </summary>
        [Fact]
        public async Task Cancelled_shutdown_work_is_not_acked_and_is_requeued_once()
        {
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Started = NewSource(),
                Gate = NewSource(),
            };
            await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: false);
            var processing = context.Deliver(7);
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await context.Session.RetireAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await processing.WaitAsync(TimeSpan.FromSeconds(2));

            var settlement = Assert.Single(context.Channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
            Assert.False(settlement.Acknowledge);
        }

        /// <summary>
        /// A terminal not-found outcome that completes during retirement is still acknowledged and published once.
        /// </summary>
        [Fact]
        public async Task Terminal_shutdown_settlement_does_not_disappear()
        {
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "missing",
                Started = NewSource(),
                Gate = NewSource(),
            };
            await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: true);
            context.Publisher.CompletesSuccessPublication = true;
            var processing = context.Deliver(7);
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var retire = context.Session.RetireAsync();
            handler.Gate!.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            var settlement = Assert.Single(context.Channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
            Assert.Single(context.Publisher.Published);
        }

        /// <summary>
        /// Completing one active delivery acknowledges only that delivery tag. The second stays unsettled until its own gate opens.
        /// </summary>
        [Fact]
        public async Task One_active_completion_does_not_settle_another_delivery()
        {
            var firstStarted = NewSource();
            var firstGate = NewSource();
            var secondStarted = NewSource();
            var secondGate = NewSource();
            var handler = SuccessHandler();
            handler.Stages.Enqueue(new ArticleWorkControlStage(firstStarted, firstGate, ArticleWorkOutcome.Success, Fqdn: ArticleWorkTestDeliveries.CanonicalFqdn, ListenerPort: ArticleWorkTestDeliveries.CanonicalListenerPort, ArticleId: CanonicalArtId()));
            handler.Stages.Enqueue(new ArticleWorkControlStage(secondStarted, secondGate, ArticleWorkOutcome.Success, Fqdn: ArticleWorkTestDeliveries.CanonicalFqdn, ListenerPort: ArticleWorkTestDeliveries.CanonicalListenerPort, ArticleId: CanonicalArtId()));
            await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: true, prefetch: 2);
            var first = context.Deliver(7);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = context.Deliver(8);
            await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);

            firstGate.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Single(context.Channel.Settlements);
            Assert.Equal(7UL, context.Channel.Settlements[0].DeliveryTag);

            secondGate.TrySetResult();
            await second.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, context.Channel.Settlements.Count);
        }

        /// <summary>
        /// Retiring the Giganews session requeues only that delivery. The Eweka session keeps running and can still acknowledge.
        /// </summary>
        [Fact]
        public async Task Retiring_one_session_does_not_cancel_unrelated_active_work()
        {
            var giganewsHandler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                Started = NewSource(),
                Gate = NewSource(),
            };
            var ewekaHandler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                Started = NewSource(),
                Gate = NewSource(),
            };
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var giganewsPublisher = new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true };
            var ewekaPublisher = new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true };
            var shutdown = new BackFillerShutdownRuntimeOptions(TimeSpan.FromSeconds(30), DrainQueuedWork: true, FinishActiveArticles: false);
            var giganews = new ArticleWorkConsumerSession(
                "Giganews",
                1,
                new ArticleWorkDeliveryPipeline(giganewsHandler, giganewsPublisher, 1024),
                connections,
                NullLogger.Instance,
                shutdown);
            var eweka = new ArticleWorkConsumerSession(
                "Eweka",
                1,
                new ArticleWorkDeliveryPipeline(ewekaHandler, ewekaPublisher, 1024),
                connections,
                NullLogger.Instance,
                shutdown);
            await giganews.StartAsync(CancellationToken.None);
            await eweka.StartAsync(CancellationToken.None);
            var giganewsChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(giganews.Channel);
            var ewekaChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(eweka.Channel);
            var giganewsWork = giganewsChannel.DeliverAsync(ArticleWorkTestDeliveries.Canonical(7));
            var ewekaWork = ewekaChannel.DeliverAsync(
                ArticleWorkTestDeliveries.Create(
                    ArticleWorkTestDeliveries.ProtocolValidExampleJson,
                    requestIdHeader: "d0648b54-b1b8-4717-95e1-7b31bf7fd1bd",
                    deliveryTag: 8));
            await giganewsHandler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await ewekaHandler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await giganews.RetireAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await giganewsWork.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(Assert.Single(giganewsChannel.Settlements).Acknowledge);
            Assert.Empty(ewekaChannel.Settlements);

            ewekaHandler.Gate!.TrySetResult();
            await ewekaWork.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(Assert.Single(ewekaChannel.Settlements).Acknowledge);

            await giganews.DisposeAsync();
            await eweka.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Every delivery still queued when drain is false is requeued. The one already active is acknowledged after it finishes.
        /// </summary>
        [Fact]
        public async Task Multiple_queued_deliveries_obey_drain_queued_work_consistently()
        {
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                Started = NewSource(),
                Gate = NewSource(),
            };
            await using var context = await ShutdownContext.StartAsync(handler, drainQueued: false, finishActive: true, prefetch: 3);
            var first = context.Deliver(7);
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = context.Deliver(8);
            var third = context.Deliver(9);
            await WaitQueuedActiveAsync(context.Session, queued: 2, active: 1);

            var retire = context.Session.RetireAsync();
            await second.WaitAsync(TimeSpan.FromSeconds(2));
            await third.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, handler.HandleCount);
            Assert.Equal(2, context.Channel.Settlements.Count);
            Assert.All(context.Channel.Settlements, static settlement =>
            {
                Assert.False(settlement.Acknowledge);
                Assert.True(settlement.Requeue);
            });

            handler.Gate!.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(3, context.Channel.Settlements.Count);
            Assert.True(context.Channel.Settlements.Single(static settlement => settlement.DeliveryTag == 7).Acknowledge);
            Assert.All(
                context.Channel.Settlements.Where(static settlement => settlement.DeliveryTag != 7),
                static settlement => Assert.False(settlement.Acknowledge));
        }

        /// <summary>
        /// Mutating the bindable shutdown options after the session is built does not change the snapshot retirement already captured.
        /// </summary>
        [Fact]
        public async Task Runtime_shutdown_snapshot_is_used_and_does_not_mutate_during_retirement()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.Shutdown.DrainQueuedWork = false;
            options.Shutdown.FinishActiveArticles = false;
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Started = NewSource(),
                Gate = NewSource(),
            };
            await using var context = await ShutdownContext.StartAsync(
                handler,
                new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true },
                runtime.Shutdown,
                prefetch: 2);
            Assert.Same(runtime.Shutdown, context.Session.Shutdown);
            var first = context.Deliver(7);
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = context.Deliver(8);
            await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);

            options.Shutdown.DrainQueuedWork = true;
            options.Shutdown.FinishActiveArticles = true;
            var retire = context.Session.RetireAsync();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await second.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(context.Session.Shutdown.DrainQueuedWork);
            Assert.False(context.Session.Shutdown.FinishActiveArticles);
            Assert.Equal(1, handler.HandleCount);
            Assert.All(context.Channel.Settlements, static settlement => Assert.False(settlement.Acknowledge));
        }

        /// <summary>
        /// The hosted consumer stops with the runtime snapshot's finish-false policy, so the in-flight delivery is requeued.
        /// </summary>
        [Fact]
        public async Task Consumer_service_stop_uses_the_runtime_snapshot()
        {
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Started = NewSource(),
                Gate = NewSource(),
            };
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var runtime = RabbitMqServiceTests.CreateFastRuntime() with
            {
                Shutdown = new BackFillerShutdownRuntimeOptions(TimeSpan.FromSeconds(30), DrainQueuedWork: true, FinishActiveArticles: false),
            };
            var catalog = new StaticBackFillerProviderCatalog(
            [
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, "nntp-user", "p", 0, 1),
            ]);
            var capacity = new BackboneUsableCapacityState();
            capacity.PublishSnapshot(new Dictionary<string, int> { ["Giganews"] = 1 });
            var consumer = new ArticleWorkConsumerService(
                connections,
                runtime,
                handler,
                new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true },
                NullLogger<ArticleWorkConsumerService>.Instance,
                catalog,
                capacity);
            await consumer.StartAsync(CancellationToken.None);
            var session = consumer.Sessions.Single(static item => item.Backbone == "Giganews");
            Assert.False(session.Shutdown.FinishActiveArticles);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel);
            var processing = channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await consumer.StopAsync(CancellationToken.None);
            await processing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(Assert.Single(channel.Settlements).Acknowledge);
            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// The consumer session and service source must not read <c>IOptions</c> or block on <c>Wait</c> or <c>Result</c>.
        /// </summary>
        [Fact]
        public void Shutdown_policy_is_not_read_from_mutable_options_per_request()
        {
            var directory = FindArticleWorkSourceDirectory();
            foreach (var text in new[] { "ArticleWorkConsumerSession.cs", "ArticleWorkConsumerService.cs" }
                .Select(name => File.ReadAllText(RelativePath.Under(directory, name))))
            {
                Assert.DoesNotContain("IOptions<", text, StringComparison.Ordinal);
                Assert.DoesNotContain("GetRequiredService<BackFillerOptions>", text, StringComparison.Ordinal);
                Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
            }
        }

        /// <summary>Handler that reports success with the canonical endpoint and article id, with no gate.</summary>
        private static ControllableArticleWorkHandler SuccessHandler() =>
            new()
            {
                Outcome = ArticleWorkOutcome.Success,
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                ArticleId = CanonicalArtId(),
            };

        /// <summary>Article id parsed from the canonical test hex. Not a live retention key.</summary>
        private static VectorNNTPD.Common.Articles.ArticleId CanonicalArtId() =>
            VectorNNTPD.Common.Articles.ArticleId.ParseLowerHex(ArticleWorkTestDeliveries.CanonicalArticleIdHex);

        /// <summary>Completion source that runs continuations asynchronously so a test thread cannot deadlock on its own signal.</summary>
        private static TaskCompletionSource NewSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Waits until the session reports exactly these queued and active counts.</summary>
        /// <param name="session">Session under retirement.</param>
        /// <param name="queued">Expected deliveries admitted but not yet in the handler.</param>
        /// <param name="active">Expected deliveries inside the handler.</param>
        private static Task WaitQueuedActiveAsync(ArticleWorkConsumerSession session, int queued, int active) =>
            ArticleWorkTestDeliveries.WaitUntilAsync(
                () => session.QueuedCount == queued && session.ActiveCount == active,
                TimeSpan.FromSeconds(2));

        /// <summary>Walks parents of the test output directory until <c>src/VectorNNTPD.BackFillerService/ArticleWork</c> exists.</summary>
        /// <returns>That directory.</returns>
        /// <exception cref="DirectoryNotFoundException">The repository layout cannot be found.</exception>
        private static string FindArticleWorkSourceDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = RelativePath.Under(directory.FullName, "src", "VectorNNTPD.BackFillerService", "ArticleWork");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate src/VectorNNTPD.BackFillerService/ArticleWork.");
        }

        /// <summary>
        /// One consumer session on a fake RabbitMQ channel. It owns the connection only when the test did not supply one.
        /// </summary>
        private sealed class ShutdownContext : IAsyncDisposable
        {
            /// <summary>Stores the session, channel, and whether this context must dispose the connection.</summary>
            /// <param name="connections">Started service, or null when the caller owns it.</param>
            /// <param name="ownsConnections">Whether <see cref="DisposeAsync"/> disposes <paramref name="connections"/>.</param>
            /// <param name="session">Consumer session under test.</param>
            /// <param name="channel">Fake channel the session is consuming.</param>
            /// <param name="recordingPublisher">Publisher when it is the recording type; otherwise a stand-in that is not the live publisher.</param>
            private ShutdownContext(
                RabbitMqService? connections,
                bool ownsConnections,
                ArticleWorkConsumerSession session,
                FakeBackFillerRabbitMqChannel channel,
                RecordingArticleWorkResponsePublisher? recordingPublisher)
            {
                Connections = connections;
                OwnsConnections = ownsConnections;
                Session = session;
                Channel = channel;
                Publisher = recordingPublisher ?? new RecordingArticleWorkResponsePublisher();
            }

            /// <summary>Fake broker service. Null when this context does not own one.</summary>
            public RabbitMqService? Connections { get; }

            /// <summary>True when <see cref="DisposeAsync"/> must dispose <see cref="Connections"/>.</summary>
            public bool OwnsConnections { get; }

            /// <summary>Giganews consumer session started with the requested shutdown snapshot.</summary>
            public ArticleWorkConsumerSession Session { get; }

            /// <summary>Channel the session is consuming. Tests gate ACK and NACK on it.</summary>
            public FakeBackFillerRabbitMqChannel Channel { get; }

            /// <summary>
            /// Recording publisher when the session was given one. Otherwise an unused stand-in, not the publisher the pipeline calls.
            /// </summary>
            public RecordingArticleWorkResponsePublisher Publisher { get; }

            /// <summary>Delivers one canonical article-work payload on <see cref="Channel"/>.</summary>
            /// <param name="deliveryTag">Broker delivery tag the settlement assertions use.</param>
            /// <returns>The consume task.</returns>
            internal Task Deliver(ulong deliveryTag) =>
                Channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical(deliveryTag));

            /// <summary>Starts a session that records successful publishes, with a 30-second grace and the given flags.</summary>
            /// <param name="handler">Article-work handler.</param>
            /// <param name="drainQueued">Captured drain-queued flag.</param>
            /// <param name="finishActive">Captured finish-active flag.</param>
            /// <param name="prefetch">Session prefetch. Default 1.</param>
            internal static Task<ShutdownContext> StartAsync(
                IArticleWorkHandler handler,
                bool drainQueued,
                bool finishActive,
                ushort prefetch = 1) =>
                StartAsync(
                    handler,
                    new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true },
                    new BackFillerShutdownRuntimeOptions(TimeSpan.FromSeconds(30), drainQueued, finishActive),
                    prefetch);

            /// <summary>Starts a session with the caller's publisher and optional already-started connections. Prefetch is 1.</summary>
            /// <param name="handler">Article-work handler.</param>
            /// <param name="publisher">Response publisher the pipeline uses.</param>
            /// <param name="drainQueued">Captured drain-queued flag.</param>
            /// <param name="finishActive">Captured finish-active flag.</param>
            /// <param name="connections">Existing broker service. Null creates and owns one.</param>
            internal static Task<ShutdownContext> StartAsync(
                IArticleWorkHandler handler,
                IArticleWorkResponsePublisher publisher,
                bool drainQueued,
                bool finishActive,
                IRabbitMqService? connections = null) =>
                StartAsync(
                    handler,
                    publisher,
                    new BackFillerShutdownRuntimeOptions(TimeSpan.FromSeconds(30), drainQueued, finishActive),
                    prefetch: 1,
                    connections);

            /// <summary>Starts a Giganews session on a fake channel using the supplied shutdown snapshot.</summary>
            /// <param name="handler">Article-work handler.</param>
            /// <param name="publisher">Response publisher the pipeline uses.</param>
            /// <param name="shutdown">Immutable snapshot the session retires with. Later option mutations do not affect it.</param>
            /// <param name="prefetch">Session prefetch.</param>
            /// <param name="connections">Existing broker service. Null creates and owns one.</param>
            /// <returns>A started context.</returns>
            internal static async Task<ShutdownContext> StartAsync(
                IArticleWorkHandler handler,
                IArticleWorkResponsePublisher publisher,
                BackFillerShutdownRuntimeOptions shutdown,
                ushort prefetch = 1,
                IRabbitMqService? connections = null)
            {
                RabbitMqService? owned = null;
                IRabbitMqService service;
                if (connections is null)
                {
                    var factory = new FakeBackFillerRabbitMqConnectionFactory();
                    owned = RabbitMqServiceTests.CreateService(factory);
                    await owned.StartAsync(CancellationToken.None);
                    service = owned;
                }
                else
                {
                    service = connections;
                }

                var recording = publisher as RecordingArticleWorkResponsePublisher;
                var session = new ArticleWorkConsumerSession(
                    "Giganews",
                    prefetch,
                    new ArticleWorkDeliveryPipeline(handler, publisher, 1024),
                    service,
                    NullLogger.Instance,
                    shutdown);
                await session.StartAsync(CancellationToken.None);
                var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel);
                return new ShutdownContext(owned, owned is not null, session, channel, recording);
            }

            /// <summary>Disposes the session, then the owned connection. A caller-supplied connection is left running.</summary>
            public async ValueTask DisposeAsync()
            {
                await Session.DisposeAsync();
                if (OwnsConnections && Connections is not null)
                {
                    await Connections.DisposeAsync();
                }
            }
        }
    }
}
