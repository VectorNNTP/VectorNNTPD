using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Channel-scoped recovery for Article Work consumers and the response publisher without reconnecting a healthy AMQP connection.
    /// </summary>
    public sealed class ArticleWorkChannelFailureRecoveryTests
    {
        /// <summary>
        /// Unexpected consumer-channel shutdown retires the failed session and starts a replacement on the same connection generation.
        /// </summary>
        [Fact]
        public async Task Consumer_channel_shutdown_retires_session_and_starts_replacement_without_reconnect()
        {
            await using var context = await StartConsumerAsync();
            var first = Assert.Single(context.Consumer.Sessions);
            var firstChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(first.Channel);
            Assert.Equal(1, context.Factory.ConnectCount);

            firstChannel.SimulateChannelShutdown();

            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Consumer.Sessions.Count == 1
                      && !ReferenceEquals(context.Consumer.Sessions[0], first)
                      && context.Consumer.Sessions[0].State == ArticleWorkConsumerState.Running
                      && context.Consumer.Sessions[0].Channel is { IsOpen: true },
                TimeSpan.FromSeconds(2));

            Assert.Equal(ArticleWorkConsumerState.Stopped, first.State);
            Assert.Equal(1, firstChannel.DisposeCount);
            Assert.Equal(1, context.Factory.ConnectCount);
            Assert.Equal(1, context.Connections.ConnectionGeneration);
            Assert.True(context.Factory.LastConnection!.Channels.Count > 1);
            Assert.Equal(1, Assert.IsType<FakeBackFillerRabbitMqChannel>(context.Consumer.Sessions[0].Channel).ConsumeCount);
        }

        /// <summary>
        /// Broker-side consumer cancellation recovers while the AMQP connection remains open.
        /// </summary>
        [Fact]
        public async Task Broker_consumer_cancellation_recovers_while_connection_stays_open()
        {
            await using var context = await StartConsumerAsync();
            var first = Assert.Single(context.Consumer.Sessions);
            var firstChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(first.Channel);

            firstChannel.SimulateConsumerCancelled();

            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Consumer.Sessions.Count == 1
                      && !ReferenceEquals(context.Consumer.Sessions[0], first)
                      && context.Consumer.Sessions[0].Channel is { IsOpen: true },
                TimeSpan.FromSeconds(2));

            Assert.Equal(1, context.Factory.ConnectCount);
            Assert.True(context.Factory.LastConnection!.IsOpen);
            Assert.Equal(ArticleWorkConsumerState.Stopped, first.State);
        }

        /// <summary>
        /// Reconciliation does not keep a session merely because its state is Running when the channel is closed.
        /// </summary>
        [Fact]
        public async Task Reconcile_retires_running_session_with_closed_channel()
        {
            await using var context = await StartConsumerAsync();
            var first = Assert.Single(context.Consumer.Sessions);
            var firstChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(first.Channel);
            firstChannel.IsOpen = false;

            await context.Consumer.ReconcileAsync(CancellationToken.None);

            var replacement = Assert.Single(context.Consumer.Sessions);
            Assert.NotSame(first, replacement);
            Assert.Equal(ArticleWorkConsumerState.Running, replacement.State);
            Assert.True(replacement.Channel!.IsOpen);
            Assert.Equal(ArticleWorkConsumerState.Stopped, first.State);
            Assert.Equal(1, firstChannel.DisposeCount);
            Assert.Equal(1, context.Factory.ConnectCount);
        }

        /// <summary>
        /// A late fault callback from a retired channel does not retire its replacement.
        /// </summary>
        [Fact]
        public async Task Late_fault_from_old_session_does_not_retire_replacement()
        {
            await using var context = await StartConsumerAsync();
            var first = Assert.Single(context.Consumer.Sessions);
            var firstChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(first.Channel);
            firstChannel.SimulateChannelShutdown();

            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Consumer.Sessions.Count == 1 && !ReferenceEquals(context.Consumer.Sessions[0], first),
                TimeSpan.FromSeconds(2));

            var replacement = Assert.Single(context.Consumer.Sessions);
            var replacementChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(replacement.Channel);

            firstChannel.SimulateChannelShutdown("stale late callback");

            await Task.Yield();
            Assert.Same(replacement, Assert.Single(context.Consumer.Sessions));
            Assert.Equal(ArticleWorkConsumerState.Running, replacement.State);
            Assert.Same(replacementChannel, replacement.Channel);
            Assert.Equal(0, replacementChannel.DisposeCount);
            Assert.Equal(1, context.Factory.ConnectCount);
        }

        /// <summary>
        /// Repeated fault notifications for one session retire it once and leave a single replacement.
        /// </summary>
        [Fact]
        public async Task Repeated_channel_faults_do_not_duplicate_active_consumers()
        {
            await using var context = await StartConsumerAsync();
            var first = Assert.Single(context.Consumer.Sessions);
            var firstChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(first.Channel);

            firstChannel.SimulateChannelShutdown("first");
            firstChannel.SimulateConsumerCancelled("second");
            firstChannel.SimulateChannelShutdown("third");

            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Consumer.Sessions.Count == 1
                      && !ReferenceEquals(context.Consumer.Sessions[0], first)
                      && context.Consumer.Sessions[0].State == ArticleWorkConsumerState.Running,
                TimeSpan.FromSeconds(2));

            Assert.Single(context.Consumer.Sessions);
            Assert.Equal(1, firstChannel.DisposeCount);
            Assert.Equal(1, context.Factory.ConnectCount);
            Assert.True(context.Factory.LastConnection!.Channels.Count > 1);
        }

        /// <summary>
        /// Expected retirement cancel does not raise a fault-driven reconnect of the AMQP connection.
        /// </summary>
        [Fact]
        public async Task Expected_retirement_does_not_open_a_new_connection()
        {
            await using var context = await StartConsumerAsync();
            var session = Assert.Single(context.Consumer.Sessions);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel);

            await session.RetireAsync();

            Assert.Equal(ArticleWorkConsumerState.Stopped, session.State);
            Assert.Equal(1, channel.CancelCount);
            Assert.Equal(1, context.Factory.ConnectCount);
        }

        /// <summary>
        /// Application shutdown retires sessions without starting replacements or reconnecting.
        /// </summary>
        [Fact]
        public async Task Application_shutdown_does_not_start_channel_fault_replacements()
        {
            await using var context = await StartConsumerAsync();
            var session = Assert.Single(context.Consumer.Sessions);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel);

            await context.Consumer.DisposeAsync();

            Assert.Empty(context.Consumer.Sessions);
            Assert.Equal(ArticleWorkConsumerState.Stopped, session.State);
            Assert.Equal(1, channel.DisposeCount);
            Assert.Equal(1, context.Factory.ConnectCount);

            channel.SimulateChannelShutdown("after shutdown");
            await Task.Yield();
            Assert.Empty(context.Consumer.Sessions);
            Assert.Equal(1, context.Factory.ConnectCount);
        }

        /// <summary>
        /// In-flight delivery admitted before an unexpected channel fault completes without ACK/NACK on the dead channel
        /// (broker redelivery) and without a second settlement attempt after retirement.
        /// </summary>
        [Fact]
        public async Task In_flight_delivery_during_channel_fault_is_left_unsettled_for_redelivery()
        {
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "missing",
                Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            await using var context = await StartConsumerAsync(handler);
            var session = Assert.Single(context.Consumer.Sessions);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel);
            var processing = channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            channel.SimulateChannelShutdown();
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => session.State == ArticleWorkConsumerState.Retiring
                      || session.State == ArticleWorkConsumerState.Stopped,
                TimeSpan.FromSeconds(2));

            handler.Gate!.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => session.State == ArticleWorkConsumerState.Stopped
                      && context.Consumer.Sessions.Count == 1
                      && !ReferenceEquals(context.Consumer.Sessions[0], session),
                TimeSpan.FromSeconds(2));

            Assert.Empty(channel.Settlements);
            Assert.Equal(1, channel.DisposeCount);
            Assert.Equal(1, context.Factory.ConnectCount);
        }

        /// <summary>
        /// Active pipeline work with no progress beyond the stall threshold is retired and replaced without reconnecting.
        /// Recovery cancels the hung handler; the gate is never released (proves no FinishActiveArticles deadlock).
        /// </summary>
        [Fact]
        public async Task Active_processing_stall_retires_session_and_starts_replacement_without_reconnect()
        {
            var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "held",
                Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            await using var context = await StartConsumerAsync(handler, clock);
            var first = Assert.Single(context.Consumer.Sessions);
            var firstChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(first.Channel);
            var processing = firstChannel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(first.HasActiveProcessing);

            clock.Advance(ArticleWorkConsumerService.ProcessingStallThreshold);
            await context.Consumer.ReconcileAsync(CancellationToken.None);

            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Consumer.Sessions.Count == 1
                      && !ReferenceEquals(context.Consumer.Sessions[0], first)
                      && context.Consumer.Sessions[0].Channel is { IsOpen: true },
                TimeSpan.FromSeconds(2));

            Assert.Equal(ArticleWorkConsumerState.Stopped, first.State);
            Assert.Equal(1, context.Factory.ConnectCount);
            Assert.False(handler.Gate!.Task.IsCompleted);

            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, firstChannel.DisposeCount);
            var interrupted = Assert.Single(firstChannel.Settlements);
            Assert.False(interrupted.Acknowledge);
            Assert.True(interrupted.Requeue);

            // Replacement must accept new work without depending on the hung first gate.
            handler.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.Gate.TrySetResult();
            var replacement = Assert.IsType<FakeBackFillerRabbitMqChannel>(context.Consumer.Sessions[0].Channel);
            await replacement.DeliverAsync(ArticleWorkTestDeliveries.Canonical(deliveryTag: 2));
            Assert.NotEmpty(replacement.Settlements);
        }

        /// <summary>
        /// An idle running consumer is not retired merely because wall time advances past the stall threshold.
        /// </summary>
        [Fact]
        public async Task Idle_consumer_is_not_retired_when_stall_threshold_elapses()
        {
            var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
            await using var context = await StartConsumerAsync(time: clock);
            var first = Assert.Single(context.Consumer.Sessions);

            clock.Advance(ArticleWorkConsumerService.ProcessingStallThreshold + TimeSpan.FromMinutes(1));
            await context.Consumer.ReconcileAsync(CancellationToken.None);

            Assert.Same(first, Assert.Single(context.Consumer.Sessions));
            Assert.Equal(ArticleWorkConsumerState.Running, first.State);
            Assert.True(first.Channel!.IsOpen);
        }

        /// <summary>
        /// Active work younger than the stall threshold is left running; reconcile does not retire it.
        /// </summary>
        [Fact]
        public async Task Active_work_below_stall_threshold_is_not_retired()
        {
            var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "held",
                Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            await using var context = await StartConsumerAsync(handler, clock);
            var first = Assert.Single(context.Consumer.Sessions);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(first.Channel);
            var processing = channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            clock.Advance(ArticleWorkConsumerService.ProcessingStallThreshold - TimeSpan.FromSeconds(1));
            await context.Consumer.ReconcileAsync(CancellationToken.None);

            Assert.Same(first, Assert.Single(context.Consumer.Sessions));
            Assert.Equal(ArticleWorkConsumerState.Running, first.State);
            Assert.True(first.HasActiveProcessing);

            handler.Gate!.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotEmpty(channel.Settlements);
        }

        /// <summary>
        /// When a normal retire wins the race, recovery still force-cancels hung work so rebuild cannot deadlock.
        /// </summary>
        [Fact]
        public async Task RetireForRecovery_force_cancels_after_non_recovery_retire_already_started()
        {
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "held",
                Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            await using var context = await StartConsumerAsync(handler);
            var first = Assert.Single(context.Consumer.Sessions);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(first.Channel);
            var processing = channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var normalRetire = first.RetireAsync(CancellationToken.None);
            var recoveryRetire = first.RetireForRecoveryAsync(CancellationToken.None);
            Assert.Same(normalRetire, recoveryRetire);

            await recoveryRetire.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ArticleWorkConsumerState.Stopped, first.State);
            Assert.False(handler.Gate!.Task.IsCompleted);
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// Dispose overlapping an active stall recovery completes without waiting on the hung gate.
        /// </summary>
        [Fact]
        public async Task Dispose_during_stalled_active_work_completes_without_gate_release()
        {
            var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "held",
                Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var consumer = new ArticleWorkConsumerService(
                connections,
                RabbitMqServiceTests.CreateFastRuntime(),
                handler,
                new RecordingArticleWorkResponsePublisher(),
                NullLogger<ArticleWorkConsumerService>.Instance,
                catalog: new StaticBackFillerProviderCatalog(
                [
                    new BackFillerProviderDefinition(
                        "Giganews",
                        "127.0.0.1",
                        119,
                        false,
                        "nntp-user",
                        "p",
                        0,
                        1),
                ]),
                capacity: CreateCapacity("Giganews", 1),
                time: clock);
            await consumer.StartAsync(CancellationToken.None);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.Sessions.Count == 1 && consumer.Sessions[0].State == ArticleWorkConsumerState.Running,
                TimeSpan.FromSeconds(2));

            var first = Assert.Single(consumer.Sessions);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(first.Channel);
            var processing = channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            clock.Advance(ArticleWorkConsumerService.ProcessingStallThreshold);
            var reconcile = consumer.ReconcileAsync(CancellationToken.None);
            await consumer.DisposeAsync();
            await reconcile.WaitAsync(TimeSpan.FromSeconds(2));
            await processing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(ArticleWorkConsumerState.Stopped, first.State);
            Assert.False(handler.Gate!.Task.IsCompleted);
            await connections.DisposeAsync();
        }

        /// <summary>
        /// A handler that completes normally under the stall window settles and is not recovery-retired.
        /// </summary>
        [Fact]
        public async Task Normal_handler_completion_settles_without_stall_recovery()
        {
            var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "missing",
            };
            await using var context = await StartConsumerAsync(handler, clock);
            var session = Assert.Single(context.Consumer.Sessions);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel);

            await channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());
            Assert.NotEmpty(channel.Settlements);

            clock.Advance(ArticleWorkConsumerService.ProcessingStallThreshold);
            await context.Consumer.ReconcileAsync(CancellationToken.None);

            Assert.Same(session, Assert.Single(context.Consumer.Sessions));
            Assert.Equal(ArticleWorkConsumerState.Running, session.State);
        }

        /// <summary>
        /// Completing work then starting new work refreshes progress so wall time from the prior article
        /// does not falsely stall the next active delivery.
        /// </summary>
        [Fact]
        public async Task Progress_refresh_on_new_active_work_avoids_false_stall()
        {
            var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "missing",
            };
            await using var context = await StartConsumerAsync(handler, clock);
            var session = Assert.Single(context.Consumer.Sessions);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel);

            await channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical(deliveryTag: 1));
            clock.Advance(ArticleWorkConsumerService.ProcessingStallThreshold - TimeSpan.FromSeconds(5));

            handler.Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical(deliveryTag: 2));
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            clock.Advance(TimeSpan.FromSeconds(10));
            await context.Consumer.ReconcileAsync(CancellationToken.None);

            Assert.Same(session, Assert.Single(context.Consumer.Sessions));
            Assert.True(session.HasActiveProcessing);

            handler.Gate.TrySetResult();
            await second.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, channel.Settlements.Count);
        }

        /// <summary>
        /// A second reconcile while stall recovery is in flight shares serialization and still finishes
        /// without releasing the hung gate.
        /// </summary>
        [Fact]
        public async Task Concurrent_reconcile_during_stall_recovery_completes_once()
        {
            var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "held",
                Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            await using var context = await StartConsumerAsync(handler, clock);
            var first = Assert.Single(context.Consumer.Sessions);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(first.Channel);
            var processing = channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            clock.Advance(ArticleWorkConsumerService.ProcessingStallThreshold);
            var firstReconcile = context.Consumer.ReconcileAsync(CancellationToken.None);
            var secondReconcile = context.Consumer.ReconcileAsync(CancellationToken.None);
            await Task.WhenAll(firstReconcile, secondReconcile).WaitAsync(TimeSpan.FromSeconds(2));
            await processing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(ArticleWorkConsumerState.Stopped, first.State);
            Assert.False(handler.Gate!.Task.IsCompleted);
            Assert.Single(context.Consumer.Sessions);
            Assert.Equal(1, context.Factory.ConnectCount);
        }

        /// <summary>
        /// A handler that observes cancel then delays exit still drains under recovery without waiting on the gate.
        /// </summary>
        [Fact]
        public async Task Stall_recovery_waits_for_delayed_cancel_cleanup_without_gate_release()
        {
            var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
            var cancelSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new DelayedCancelArticleWorkHandler(cancelSeen, allowExit);
            await using var context = await StartConsumerAsync(handler, clock);
            var first = Assert.Single(context.Consumer.Sessions);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(first.Channel);
            var processing = channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            clock.Advance(ArticleWorkConsumerService.ProcessingStallThreshold);
            var reconcile = context.Consumer.ReconcileAsync(CancellationToken.None);
            await cancelSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(reconcile.IsCompleted);

            allowExit.TrySetResult();
            await reconcile.WaitAsync(TimeSpan.FromSeconds(2));
            await processing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(ArticleWorkConsumerState.Stopped, first.State);
            Assert.Single(context.Consumer.Sessions);
        }

        /// <summary>
        /// A handler that throws after observing cancel still allows recovery to complete and replace the session.
        /// </summary>
        [Fact]
        public async Task Stall_recovery_survives_handler_throw_during_cancel_cleanup()
        {
            var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
            var handler = new ThrowingOnCancelArticleWorkHandler();
            await using var context = await StartConsumerAsync(handler, clock);
            var first = Assert.Single(context.Consumer.Sessions);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(first.Channel);
            var processing = channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            clock.Advance(ArticleWorkConsumerService.ProcessingStallThreshold);
            await context.Consumer.ReconcileAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await processing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(ArticleWorkConsumerState.Stopped, first.State);
            Assert.Single(context.Consumer.Sessions);
            Assert.NotSame(first, context.Consumer.Sessions[0]);
        }

        /// <summary>
        /// Two capacity slots with active hung work are both recovery-retired and replaced.
        /// </summary>
        [Fact]
        public async Task Multiple_stalled_sessions_are_retired_together()
        {
            var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "held",
                Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var started1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.Stages.Enqueue(new ArticleWorkControlStage(
                started1,
                handler.Gate,
                ArticleWorkOutcome.ArticleNotFound,
                Error: "held-1"));
            handler.Stages.Enqueue(new ArticleWorkControlStage(
                started2,
                handler.Gate,
                ArticleWorkOutcome.ArticleNotFound,
                Error: "held-2"));

            await using var context = await StartConsumerAsync(handler, clock, capacitySlots: 2);
            Assert.Equal(2, context.Consumer.Sessions.Count);
            var first = context.Consumer.Sessions[0];
            var second = context.Consumer.Sessions[1];
            var channel1 = Assert.IsType<FakeBackFillerRabbitMqChannel>(first.Channel);
            var channel2 = Assert.IsType<FakeBackFillerRabbitMqChannel>(second.Channel);
            var processing1 = channel1.DeliverAsync(ArticleWorkTestDeliveries.Canonical(deliveryTag: 1));
            var processing2 = channel2.DeliverAsync(ArticleWorkTestDeliveries.Canonical(deliveryTag: 2));
            await Task.WhenAll(started1.Task, started2.Task).WaitAsync(TimeSpan.FromSeconds(2));

            clock.Advance(ArticleWorkConsumerService.ProcessingStallThreshold);
            await context.Consumer.ReconcileAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await Task.WhenAll(processing1, processing2).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(ArticleWorkConsumerState.Stopped, first.State);
            Assert.Equal(ArticleWorkConsumerState.Stopped, second.State);
            Assert.Equal(2, context.Consumer.Sessions.Count);
            Assert.All(context.Consumer.Sessions, s => Assert.NotEqual(ArticleWorkConsumerState.Stopped, s.State));
            Assert.False(handler.Gate.Task.IsCompleted);
        }

        /// <summary>
        /// Publish-channel shutdown installs a replacement on the same connection generation.
        /// </summary>
        [Fact]
        public async Task Publisher_channel_shutdown_replaces_channel_without_reconnect()
        {
            await using var context = await StartPublisherAsync();
            var first = context.PublishChannel;
            Assert.Equal(1, context.Factory.ConnectCount);

            first.SimulateChannelShutdown();

            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Publisher.Channel is not null
                      && !ReferenceEquals(context.Publisher.Channel, first)
                      && context.Publisher.Channel.IsOpen,
                TimeSpan.FromSeconds(2));

            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(1, context.Factory.ConnectCount);
            Assert.Equal(1, context.Publisher.Generation);
            Assert.Equal(2, context.Factory.LastConnection!.PublishChannels.Count);
        }

        /// <summary>
        /// After publish-channel replacement, the next publish succeeds through the new channel.
        /// </summary>
        [Fact]
        public async Task Publish_after_channel_replacement_uses_the_new_channel()
        {
            await using var context = await StartPublisherAsync();
            var first = context.PublishChannel;
            first.SimulateChannelShutdown();
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Publisher.Channel is not null && !ReferenceEquals(context.Publisher.Channel, first),
                TimeSpan.FromSeconds(2));

            var replacement = context.PublishChannel;
            await context.Publisher.PublishAsync(SuccessIntent(), CancellationToken.None);

            Assert.Empty(first.Publications);
            Assert.Single(replacement.Publications);
            Assert.Equal(1, context.Factory.ConnectCount);
        }

        /// <summary>
        /// A late fault from an obsolete publish channel does not dispose the installed replacement.
        /// </summary>
        [Fact]
        public async Task Late_publish_channel_fault_does_not_invalidate_replacement()
        {
            await using var context = await StartPublisherAsync();
            var first = context.PublishChannel;
            first.SimulateChannelShutdown();
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Publisher.Channel is not null && !ReferenceEquals(context.Publisher.Channel, first),
                TimeSpan.FromSeconds(2));

            var replacement = context.PublishChannel;
            first.SimulateChannelShutdown("stale");

            await Task.Yield();
            Assert.Same(replacement, context.Publisher.Channel);
            Assert.True(replacement.IsOpen);
            Assert.Equal(0, replacement.DisposeCount);
            Assert.Equal(1, context.Factory.ConnectCount);
        }

        /// <summary>
        /// Concurrent publishes wait for channel rebuild rather than racing duplicate replacements unsafely.
        /// </summary>
        [Fact]
        public async Task Concurrent_publishes_after_channel_fault_share_one_replacement()
        {
            await using var context = await StartPublisherAsync();
            var first = context.PublishChannel;
            first.SimulateChannelShutdown();

            var publish1 = context.Publisher.PublishAsync(SuccessIntent(), CancellationToken.None);
            var publish2 = context.Publisher.PublishAsync(SuccessIntent(), CancellationToken.None);
            await Task.WhenAll(publish1, publish2).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(2, context.Factory.LastConnection!.PublishChannels.Count);
            Assert.Equal(2, context.PublishChannel.Publications.Count);
            Assert.Equal(1, context.Factory.ConnectCount);
        }

        /// <summary>
        /// Failed channel replacement does not report publish success.
        /// </summary>
        [Fact]
        public async Task Failed_publish_channel_replacement_does_not_report_success()
        {
            await using var context = await StartPublisherAsync();
            var first = context.PublishChannel;
            context.Factory.LastConnection!.CreatePublishChannelException =
                new InvalidOperationException("no publish channel");
            first.SimulateChannelShutdown();

            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Publisher.Channel is null || !context.Publisher.Channel.IsOpen,
                TimeSpan.FromSeconds(2));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.Publisher.PublishAsync(SuccessIntent(), CancellationToken.None));
            Assert.Empty(first.Publications);
            Assert.Equal(1, context.Factory.ConnectCount);
        }

        /// <summary>
        /// Caller cancellation during publish wait does not leave a permanently closed installed channel when rebuild already completed.
        /// </summary>
        [Fact]
        public async Task Caller_cancellation_does_not_trigger_unwanted_connection_recovery()
        {
            await using var context = await StartPublisherAsync();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => context.Publisher.PublishAsync(SuccessIntent(), cts.Token));

            Assert.Equal(1, context.Factory.ConnectCount);
            Assert.True(context.PublishChannel.IsOpen);
        }

        /// <summary>
        /// Publisher disposal prevents channel-fault rebuild work after stop.
        /// </summary>
        [Fact]
        public async Task Publisher_shutdown_ignores_late_channel_faults()
        {
            await using var context = await StartPublisherAsync();
            var channel = context.PublishChannel;
            await context.Publisher.DisposeAsync();

            channel.SimulateChannelShutdown();
            await Task.Yield();

            Assert.Equal(ArticleWorkResponsePublisherState.Stopped, context.Publisher.State);
            Assert.Null(context.Publisher.Channel);
            Assert.Equal(1, context.Factory.ConnectCount);
            Assert.Single(context.Factory.LastConnection!.PublishChannels);
        }

        /// <summary>
        /// Dispose while a channel-fault rebuild is blocked, then connection replacement is chained, completes without
        /// deadlock or an unobserved replace-gate waiter after the publisher disposes its semaphores.
        /// </summary>
        [Fact]
        public async Task Publisher_shutdown_during_chained_channel_and_connection_rebuild_does_not_deadlock()
        {
            await using var context = await StartPublisherAsync();
            var first = context.PublishChannel;
            var firstConnection = context.Factory.LastConnection!;
            firstConnection.CreatePublishChannelStarted =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            firstConnection.BlockCreatePublishChannel =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            first.SimulateChannelShutdown();
            await firstConnection.CreatePublishChannelStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Factory.Connected = secondConnected;
            firstConnection.SimulateLost();
            await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var disposing = context.Publisher.DisposeAsync().AsTask();
            firstConnection.BlockCreatePublishChannel.TrySetResult();
            await disposing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(ArticleWorkResponsePublisherState.Stopped, context.Publisher.State);
            Assert.Null(context.Publisher.Channel);
        }

        /// <summary>
        /// Connection replacement that races with channel-only recovery still installs a channel on the new generation.
        /// </summary>
        [Fact]
        public async Task Connection_replacement_chained_after_channel_fault_installs_new_generation()
        {
            await using var context = await StartPublisherAsync();
            var first = context.PublishChannel;
            var firstConnection = context.Factory.LastConnection!;
            firstConnection.CreatePublishChannelStarted =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            firstConnection.BlockCreatePublishChannel =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            first.SimulateChannelShutdown();
            await firstConnection.CreatePublishChannelStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Factory.Connected = secondConnected;
            firstConnection.SimulateLost();
            await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));

            firstConnection.BlockCreatePublishChannel.TrySetResult();

            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Publisher.Generation == 2
                      && context.Publisher.Channel is { IsOpen: true },
                TimeSpan.FromSeconds(2));

            Assert.NotSame(first, context.Publisher.Channel);
            Assert.Equal(2, context.Factory.ConnectCount);
            await context.Publisher.PublishAsync(SuccessIntent(), CancellationToken.None);
            Assert.Single(context.PublishChannel.Publications);
        }

        /// <summary>Canonical success response intent used when exercising publish recovery paths.</summary>
        private static ArticleWorkResponseIntent SuccessIntent() =>
            new(
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

        /// <summary>Starts an Article Work consumer against a fake RabbitMQ factory.</summary>
        /// <param name="handler">Handler wired into the consumer; defaults to <see cref="DeferredArticleWorkHandler"/>.</param>
        /// <param name="time">Optional clock for reconcile delay and processing-stall detection.</param>
        /// <param name="capacitySlots">Usable NNTP capacity slots published for Giganews (session count).</param>
        /// <returns>Harness holding factory, connections, and running consumer.</returns>
        private static async Task<ConsumerHarness> StartConsumerAsync(
            IArticleWorkHandler? handler = null,
            TimeProvider? time = null,
            int capacitySlots = 1)
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var consumer = new ArticleWorkConsumerService(
                connections,
                RabbitMqServiceTests.CreateFastRuntime(),
                handler ?? new DeferredArticleWorkHandler(),
                new RecordingArticleWorkResponsePublisher(),
                NullLogger<ArticleWorkConsumerService>.Instance,
                catalog: new StaticBackFillerProviderCatalog(
                [
                    new BackFillerProviderDefinition(
                        "Giganews",
                        "127.0.0.1",
                        119,
                        false,
                        "nntp-user",
                        "p",
                        0,
                        capacitySlots),
                ]),
                capacity: CreateCapacity("Giganews", capacitySlots),
                time: time);
            await consumer.StartAsync(CancellationToken.None);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.Sessions.Count == capacitySlots
                      && consumer.Sessions.All(static s => s.State == ArticleWorkConsumerState.Running),
                TimeSpan.FromSeconds(2));
            return new ConsumerHarness(factory, connections, consumer);
        }

        /// <summary>
        /// Handler that waits until cancelled, signals cancel observation, then blocks until the test releases exit.
        /// </summary>
        private sealed class DelayedCancelArticleWorkHandler(
            TaskCompletionSource cancelSeen,
            TaskCompletionSource allowExit) : IArticleWorkHandler
        {
            /// <summary>Signalled when <see cref="HandleAsync"/> begins waiting.</summary>
            public TaskCompletionSource Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <inheritdoc />
            public async ValueTask<ArticleWorkHandlerResult> HandleAsync(
                ArticleWorkItem item,
                CancellationToken cancellationToken)
            {
                Started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelSeen.TrySetResult();
                    await allowExit.Task.ConfigureAwait(false);
                    throw;
                }

                return new ArticleWorkHandlerResult(ArticleWorkOutcome.ArticleNotFound, "unreachable");
            }
        }

        /// <summary>
        /// Handler that waits until cancelled, then throws a non-cancel exception during cleanup.
        /// </summary>
        private sealed class ThrowingOnCancelArticleWorkHandler : IArticleWorkHandler
        {
            /// <summary>Signalled when <see cref="HandleAsync"/> begins waiting.</summary>
            public TaskCompletionSource Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <inheritdoc />
            public async ValueTask<ArticleWorkHandlerResult> HandleAsync(
                ArticleWorkItem item,
                CancellationToken cancellationToken)
            {
                Started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException("cleanup failed after cancel");
                }

                return new ArticleWorkHandlerResult(ArticleWorkOutcome.ArticleNotFound, "unreachable");
            }
        }

        /// <summary>Starts a running <see cref="ArticleWorkResponsePublisher"/> with publish confirms enabled.</summary>
        private static async Task<PublisherHarness> StartPublisherAsync()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory
            {
                DefaultPublishConfirmBehavior = FakePublishConfirmBehavior.Confirm,
            };
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            factory.LastConnection!.DefaultPublishConfirmBehavior = FakePublishConfirmBehavior.Confirm;
            var publisher = new ArticleWorkResponsePublisher(
                connections,
                RabbitMqServiceTests.CreateFastRuntime(),
                NullLogger<ArticleWorkResponsePublisher>.Instance);
            await publisher.StartAsync(CancellationToken.None);
            Assert.Equal(ArticleWorkResponsePublisherState.Running, publisher.State);
            return new PublisherHarness(factory, connections, publisher);
        }

        /// <summary>Publishes a single-backbone usable-capacity snapshot when <paramref name="active"/> is positive.</summary>
        /// <param name="backbone">Backbone name key in the snapshot.</param>
        /// <param name="active">Active session count; zero omits the backbone from the snapshot.</param>
        private static BackboneUsableCapacityState CreateCapacity(string backbone, int active)
        {
            var capacity = new BackboneUsableCapacityState();
            if (active > 0)
            {
                capacity.PublishSnapshot(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [backbone] = active,
                });
            }

            return capacity;
        }

        /// <summary>Owns a started consumer and its RabbitMQ connections for channel-failure tests.</summary>
        private sealed class ConsumerHarness(
            FakeBackFillerRabbitMqConnectionFactory factory,
            RabbitMqService connections,
            ArticleWorkConsumerService consumer) : IAsyncDisposable
        {
            /// <summary>Fake factory that created the test connection.</summary>
            public FakeBackFillerRabbitMqConnectionFactory Factory { get; } = factory;

            /// <summary>Started <see cref="RabbitMqService"/> instance.</summary>
            public RabbitMqService Connections { get; } = connections;

            /// <summary>Running Article Work consumer under test.</summary>
            public ArticleWorkConsumerService Consumer { get; } = consumer;

            /// <summary>Stops the consumer, then disposes connections.</summary>
            public async ValueTask DisposeAsync()
            {
                await Consumer.DisposeAsync();
                await Connections.DisposeAsync();
            }
        }

        /// <summary>Owns a started response publisher and exposes its fake publish channel.</summary>
        private sealed class PublisherHarness(
            FakeBackFillerRabbitMqConnectionFactory factory,
            RabbitMqService connections,
            ArticleWorkResponsePublisher publisher) : IAsyncDisposable
        {
            /// <summary>Fake factory that created the test connection.</summary>
            public FakeBackFillerRabbitMqConnectionFactory Factory { get; } = factory;

            /// <summary>Started <see cref="RabbitMqService"/> instance.</summary>
            public RabbitMqService Connections { get; } = connections;

            /// <summary>Running response publisher under test.</summary>
            public ArticleWorkResponsePublisher Publisher { get; } = publisher;

            /// <summary>Fake publish channel backing <see cref="Publisher"/>.</summary>
            public FakeBackFillerRabbitMqPublishChannel PublishChannel =>
                Assert.IsType<FakeBackFillerRabbitMqPublishChannel>(Publisher.Channel);

            /// <summary>Stops the publisher, then disposes connections.</summary>
            public async ValueTask DisposeAsync()
            {
                await Publisher.DisposeAsync();
                await Connections.DisposeAsync();
            }
        }
    }
}
