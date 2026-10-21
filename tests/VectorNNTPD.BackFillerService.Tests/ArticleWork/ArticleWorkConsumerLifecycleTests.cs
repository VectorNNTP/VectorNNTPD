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
    /// Consumer session and service lifecycle: generation binding, admission after retirement, connection loss rebuild, and shutdown interaction.
    /// </summary>
    public sealed class ArticleWorkConsumerLifecycleTests
    {
        /// <summary>
        /// Start binds the session to connection generation 1, declares consume on the provider queue, and leaves the shared connection open.
        /// </summary>
        [Fact]
        public async Task Session_starts_running_on_the_current_generation()
        {
            await using var context = await ConsumerContext.StartSessionAsync();

            Assert.Equal(ArticleWorkConsumerState.Running, context.Session.State);
            Assert.Equal(1, context.Session.Generation);
            Assert.Equal("backfiller.giganews", context.Session.Queue);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(context.Session.Channel);
            Assert.Equal(1, channel.ConsumeCount);
            Assert.Equal((ushort)1, channel.LastPrefetch);
            Assert.Equal("backfiller.giganews", channel.LastQueue);
            Assert.Equal(1, context.Factory.ConnectCount);
        }

        /// <summary>
        /// Start throws when RabbitMQ is not ready; session stays stopped and no connection attempt is made.
        /// </summary>
        [Fact]
        public async Task Startup_fails_when_the_connection_is_not_ready()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            var session = CreateSession(connections, new DeferredArticleWorkHandler());

            await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(CancellationToken.None));
            Assert.Equal(ArticleWorkConsumerState.Stopped, session.State);
            Assert.Equal(0, factory.ConnectCount);
        }

        /// <summary>
        /// Channel creation failure during start leaves the session stopped without a running consumer.
        /// </summary>
        [Fact]
        public async Task Startup_fails_when_consume_cannot_be_established()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            factory.LastConnection!.CreateChannelException = new InvalidOperationException("channel refused");
            var session = CreateSession(connections, new DeferredArticleWorkHandler());

            await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(CancellationToken.None));
            Assert.Equal(ArticleWorkConsumerState.Stopped, session.State);
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Retire cancels BasicConsume, disposes the manual-ack channel, clears the handle, and does not dispose the shared connection.
        /// </summary>
        [Fact]
        public async Task Retirement_cancels_the_consumer_disposes_the_channel_and_stops()
        {
            await using var context = await ConsumerContext.StartSessionAsync();
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(context.Session.Channel);

            await context.Session.RetireAsync();

            Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
            Assert.Equal(1, channel.CancelCount);
            Assert.Equal(1, channel.DisposeCount);
            Assert.False(channel.IsOpen);
            Assert.Null(context.Session.Channel);
            Assert.Equal(0, context.Factory.LastConnection!.DisposeCount);
        }

        /// <summary>
        /// Idempotent session dispose performs a single channel cancel/dispose pair.
        /// </summary>
        [Fact]
        public async Task Repeated_dispose_does_not_dispose_the_channel_again()
        {
            await using var context = await ConsumerContext.StartSessionAsync();
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(context.Session.Channel);

            await context.Session.DisposeAsync();
            await context.Session.DisposeAsync();

            Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
            Assert.Equal(1, channel.DisposeCount);
            Assert.Equal(1, channel.CancelCount);
            Assert.False(channel.IsOpen);
        }

        /// <summary>
        /// In-flight work started before retire completes on the original channel and settles once (terminal ack) before dispose.
        /// </summary>
        [Fact]
        public async Task Shutdown_drains_admitted_work_on_the_original_channel()
        {
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "missing",
                Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            await using var context = await ConsumerContext.StartSessionAsync(handler);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(context.Session.Channel);
            var processing = channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var retire = context.Session.RetireAsync();
            handler.Gate!.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));

            var settlement = Assert.Single(channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
            Assert.Equal(1, channel.DisposeCount);
            Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
        }

        /// <summary>
        /// Post-retire deliveries are ignored: no handler invocation, settlement, or response publication.
        /// </summary>
        [Fact]
        public async Task Delivery_arriving_after_retirement_is_not_admitted()
        {
            var handler = new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Success };
            await using var context = await ConsumerContext.StartSessionAsync(handler);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(context.Session.Channel);
            await context.Session.RetireAsync();

            await channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());

            Assert.Equal(0, handler.HandleCount);
            Assert.Empty(channel.Settlements);
            Assert.Empty(context.Publisher.Published);
        }

        /// <summary>
        /// Service opens a topology-only channel then one consume channel per desired slot (min of catalog max and usable capacity).
        /// </summary>
        [Fact]
        public async Task Consumer_service_starts_one_session_per_desired_nntp_slot_when_capacity_exists()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                catalog: CreateCatalog("Giganews", maxSessions: 2),
                capacity: CreateCapacity("Giganews", active: 1));

            await consumer.StartAsync(CancellationToken.None);

            Assert.Equal(2, consumer.Sessions.Count);
            Assert.Equal(new[] { 1, 2 }, consumer.Sessions.Select(static session => session.ConnectionNumber).OrderBy(static n => n));
            Assert.All(consumer.Sessions, static session => Assert.Equal(ArticleWorkConsumerState.Running, session.State));
            Assert.All(consumer.Sessions, static session => Assert.Equal("Giganews", session.Backbone));
            Assert.Equal(3, factory.LastConnection!.Channels.Count);
            var topologyChannel = factory.LastConnection.Channels[0];
            Assert.Equal("backfiller.giganews", Assert.Single(topologyChannel.ExchangeDeclarations).Name);
            Assert.Equal("backfiller.giganews", Assert.Single(topologyChannel.QueueDeclarations).Name);
            Assert.True(Assert.Single(topologyChannel.QueueDeclarations).Durable);
            Assert.True(Assert.Single(topologyChannel.QueueDeclarations).Arguments!.TryGetValue(
                BackFillerArticleWorkTopology.QueueTypeArgumentName,
                out var queueType));
            Assert.Equal(BackFillerArticleWorkTopology.ClassicQueueType, queueType);
            Assert.Equal(1, topologyChannel.DisposeCount);
            Assert.Equal(0, topologyChannel.ConsumeCount);
            Assert.Equal(1, factory.ConnectCount);
            Assert.All(
                factory.LastConnection.Channels.Skip(1),
                static channel => Assert.Equal("backfiller.giganews", channel.LastQueue));

            await consumer.DisposeAsync();
            Assert.All(factory.LastConnection.Channels, static channel => Assert.Equal(1, channel.DisposeCount));
            Assert.Equal(0, factory.LastConnection.DisposeCount);
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Empty usable-capacity snapshot yields no sessions and no RabbitMQ channels.
        /// </summary>
        [Fact]
        public async Task Consumer_service_does_not_start_topology_backbones_without_capacity()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var consumer = CreateConsumerService(connections, new DeferredArticleWorkHandler());

            await consumer.StartAsync(CancellationToken.None);

            Assert.Empty(consumer.Sessions);
            Assert.Empty(factory.LastConnection!.Channels);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Connection loss rebuilds sessions on generation 2 using one reconnect; prior consume channels are disposed.
        /// </summary>
        [Fact]
        public async Task Consumer_service_rebuilds_after_generation_loss_without_opening_a_competing_connection()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                catalog: CreateCatalog("Giganews", maxSessions: 1),
                capacity: CreateCapacity("Giganews", active: 1));
            await consumer.StartAsync(CancellationToken.None);
            var firstChannels = factory.LastConnection!.Channels.ToArray();

            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;
            factory.LastConnection.SimulateLost();
            await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.Sessions.Count == 1
                      && consumer.Sessions.All(static session => session.Generation == 2),
                TimeSpan.FromSeconds(2));

            Assert.Equal(2, factory.ConnectCount);
            Assert.Equal(2, connections.ConnectionGeneration);
            Assert.All(consumer.Sessions, static session => Assert.Equal(ArticleWorkConsumerState.Running, session.State));
            Assert.All(firstChannels, static channel => Assert.Equal(1, channel.DisposeCount));

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Stop with cancellation while generation replacement is in flight still disposes the old consume channel without double-handling the delivery.
        /// </summary>
        [Fact]
        public async Task Shutdown_cancels_active_replacement_retirement_started_before_stop()
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
            var consumer = CreateConsumerService(
                connections,
                handler,
                catalog: CreateCatalog("Giganews", maxSessions: 1),
                capacity: CreateCapacity("Giganews", active: 1));
            await consumer.StartAsync(CancellationToken.None);

            var oldSession = Assert.Single(consumer.Sessions);
            var oldChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(oldSession.Channel);
            var processing = oldChannel.DeliverAsync(ArticleWorkTestDeliveries.Canonical(generation: 1));
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;
            factory.LastConnection!.SimulateLost();
            await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => oldChannel.CancelCount == 1,
                TimeSpan.FromSeconds(2));

            using var shutdown = new CancellationTokenSource();
            shutdown.Cancel();
            await consumer.StopAsync(shutdown.Token).WaitAsync(TimeSpan.FromSeconds(2));
            await processing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, oldChannel.DisposeCount);
            Assert.Equal(1, handler.HandleCount);

            await connections.DisposeAsync();
        }

        /// <summary>
        /// Handler completion on a stale generation after reconnect must not ack/nack on old or new channels.
        /// </summary>
        [Fact]
        public async Task Stale_session_does_not_mutate_a_newer_generation()
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
            var consumer = CreateConsumerService(
                connections,
                handler,
                catalog: CreateCatalog("Giganews", maxSessions: 1),
                capacity: CreateCapacity("Giganews", active: 1));
            await consumer.StartAsync(CancellationToken.None);
            var giganews = consumer.Sessions.Single(static session => session.Backbone == "Giganews");
            var staleChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(giganews.Channel);
            var processing = staleChannel.DeliverAsync(ArticleWorkTestDeliveries.Canonical(generation: 1));
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;
            factory.LastConnection!.SimulateLost();
            await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            handler.Gate!.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.Sessions.Count == 1
                      && consumer.Sessions.All(static session => session.Generation == 2),
                TimeSpan.FromSeconds(2));

            var current = Assert.IsType<FakeBackFillerRabbitMqChannel>(
                consumer.Sessions.Single(static session => session.Backbone == "Giganews").Channel);
            Assert.Empty(staleChannel.Settlements);
            Assert.Empty(current.Settlements);
            Assert.Equal(2, factory.ConnectCount);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// An unexpected reconcile fault stays on the same loop, backs off, and does not open another consumer.
        /// </summary>
        [Fact]
        public async Task Reconcile_loop_fault_backs_off_without_a_second_loop_or_consumer()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                catalog: CreateCatalog("Giganews", maxSessions: 1),
                capacity: CreateCapacity("Giganews", active: 1),
                time: time,
                reconcileLoopBody: static _ => Task.FromException(new InvalidOperationException("reconcile fault")));
            await consumer.StartAsync(CancellationToken.None);
            var loop = consumer.ReconcileLoop;
            var sessions = consumer.Sessions.Count;
            var consumeCount = factory.LastConnection!.Channels.Sum(static channel => channel.ConsumeCount);

            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.ReconcileWaitsStarted == 1, TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromSeconds(15));
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.UnexpectedReconcileFaults == 1, TimeSpan.FromSeconds(2));
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.ReconcileWaitsStarted == 2, TimeSpan.FromSeconds(2));

            Assert.Equal(TimeSpan.FromSeconds(30), consumer.CurrentReconcileDelay);
            Assert.Same(loop, consumer.ReconcileLoop);
            Assert.False(loop!.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(15));
            Assert.Equal(1, consumer.UnexpectedReconcileFaults);
            Assert.Equal(2, consumer.ReconcileWaitsStarted);
            Assert.Equal(sessions, consumer.Sessions.Count);
            Assert.Equal(consumeCount, factory.LastConnection.Channels.Sum(static channel => channel.ConsumeCount));

            time.Advance(TimeSpan.FromSeconds(15));
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.UnexpectedReconcileFaults == 2, TimeSpan.FromSeconds(2));
            Assert.Same(loop, consumer.ReconcileLoop);
            Assert.False(loop.IsCompleted);
            Assert.Equal(sessions, consumer.Sessions.Count);
            Assert.Equal(consumeCount, factory.LastConnection.Channels.Sum(static channel => channel.ConsumeCount));

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// After a fault, the next successful pass resets the delay. Shutdown cancellation does not count as a fault or start another loop.
        /// </summary>
        [Fact]
        public async Task Reconcile_loop_resets_after_recovery_and_shutdown_does_not_restart_it()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var fail = true;
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                time: time,
                reconcileLoopBody: _ => fail
                    ? Task.FromException(new InvalidOperationException("reconcile fault"))
                    : Task.CompletedTask);
            await consumer.StartAsync(CancellationToken.None);
            var loop = consumer.ReconcileLoop;

            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.ReconcileWaitsStarted == 1, TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromSeconds(15));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.UnexpectedReconcileFaults == 1 && consumer.ReconcileWaitsStarted == 2,
                TimeSpan.FromSeconds(2));
            fail = false;
            time.Advance(TimeSpan.FromSeconds(30));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.ReconcileWaitsStarted == 3 && consumer.CurrentReconcileDelay == TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(2));
            Assert.Equal(1, consumer.UnexpectedReconcileFaults);
            Assert.Same(loop, consumer.ReconcileLoop);

            await consumer.StopAsync(CancellationToken.None);
            Assert.True(loop!.IsCompleted);
            Assert.Equal(1, consumer.UnexpectedReconcileFaults);
            time.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(1, consumer.UnexpectedReconcileFaults);
            Assert.Same(loop, consumer.ReconcileLoop);
            Assert.False(consumer.IsRunning);

            await connections.DisposeAsync();
        }

        /// <summary>
        /// A faulting periodic body does not stop capacity or connection-replacement reconciliation, and it does not duplicate sessions.
        /// </summary>
        [Fact]
        public async Task Reconcile_loop_fault_still_allows_capacity_and_connection_replacement()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var capacity = CreateCapacity("Giganews", active: 0);
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                catalog: CreateCatalog("Giganews", maxSessions: 1),
                capacity: capacity,
                time: time,
                reconcileLoopBody: static _ => Task.FromException(new InvalidOperationException("reconcile fault")));
            await consumer.StartAsync(CancellationToken.None);
            var loop = consumer.ReconcileLoop;
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.ReconcileWaitsStarted == 1, TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromSeconds(15));
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.UnexpectedReconcileFaults == 1, TimeSpan.FromSeconds(2));

            capacity.PublishSnapshot(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Giganews"] = 1 });
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.Sessions.Count == 1, TimeSpan.FromSeconds(2));
            var session = Assert.Single(consumer.Sessions);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel);
            Assert.Equal(1, channel.ConsumeCount);
            Assert.Equal(1, session.Generation);

            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;
            factory.LastConnection!.SimulateLost();
            await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.Sessions.Count == 1 && consumer.Sessions[0].Generation == 2,
                TimeSpan.FromSeconds(2));

            Assert.Same(loop, consumer.ReconcileLoop);
            Assert.False(loop!.IsCompleted);
            Assert.Equal(1, Assert.IsType<FakeBackFillerRabbitMqChannel>(consumer.Sessions[0].Channel).ConsumeCount);
            Assert.Equal(1, channel.DisposeCount);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// A broker that is not ready is not a successful reconcile, so the next wait doubles instead of staying at 15 seconds.
        /// </summary>
        [Fact]
        public async Task Broker_not_ready_reconcile_does_not_reset_backoff()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                catalog: CreateCatalog("Giganews", maxSessions: 1),
                capacity: CreateCapacity("Giganews", active: 1),
                time: time);
            await consumer.StartAsync(CancellationToken.None);
            var loop = consumer.ReconcileLoop;

            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.ReconcileWaitsStarted == 1, TimeSpan.FromSeconds(2));
            Assert.Equal(TimeSpan.FromSeconds(15), consumer.CurrentReconcileDelay);
            time.Advance(TimeSpan.FromSeconds(15));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.ReconcileWaitsStarted == 2 && consumer.CurrentReconcileDelay == TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(2));

            Assert.Equal(0, consumer.UnexpectedReconcileFaults);
            Assert.Empty(consumer.Sessions);
            Assert.Same(loop, consumer.ReconcileLoop);
            Assert.False(loop!.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(15));
            Assert.Equal(2, consumer.ReconcileWaitsStarted);
            Assert.Equal(TimeSpan.FromSeconds(30), consumer.CurrentReconcileDelay);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Repeated declaration failures wait 15, 30, 60, then 120 seconds, and further failures stay at the cap.
        /// </summary>
        [Fact]
        public async Task Repeated_topology_failures_back_off_to_the_two_minute_cap()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            factory.LastConnection!.OnManualAckChannelCreated = static channel =>
                channel.ExchangeDeclareException = new InvalidOperationException("declare failed");
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                catalog: CreateCatalog("Giganews", maxSessions: 1),
                capacity: CreateCapacity("Giganews", active: 1),
                time: time);
            await consumer.StartAsync(CancellationToken.None);
            var loop = consumer.ReconcileLoop;
            var waited = new List<TimeSpan>();
            for (var pass = 0; pass < 5; pass++)
            {
                await ArticleWorkTestDeliveries.WaitUntilAsync(
                    () => consumer.ReconcileWaitsStarted == pass + 1,
                    TimeSpan.FromSeconds(2));
                waited.Add(consumer.CurrentReconcileDelay);
                time.Advance(consumer.CurrentReconcileDelay);
            }

            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.ReconcileWaitsStarted == 6,
                TimeSpan.FromSeconds(2));
            Assert.Equal(
            [
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(2),
            ],
            waited);
            Assert.Equal(TimeSpan.FromMinutes(2), consumer.CurrentReconcileDelay);
            Assert.Equal(0, consumer.UnexpectedReconcileFaults);
            Assert.Empty(consumer.Sessions);
            Assert.Same(loop, consumer.ReconcileLoop);
            Assert.False(loop!.IsCompleted);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// A pass that starts the desired consumer resets backoff to 15 seconds.
        /// </summary>
        [Fact]
        public async Task Successful_reconcile_resets_backoff_to_fifteen_seconds()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            factory.LastConnection!.CreateChannelException = new InvalidOperationException("channel");
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                catalog: CreateCatalog("Giganews", maxSessions: 1),
                capacity: CreateCapacity("Giganews", active: 1),
                time: time);
            await consumer.StartAsync(CancellationToken.None);

            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.ReconcileWaitsStarted == 1, TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromSeconds(15));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.ReconcileWaitsStarted == 2 && consumer.CurrentReconcileDelay == TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(2));
            factory.LastConnection.CreateChannelException = null;
            time.Advance(TimeSpan.FromSeconds(30));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.Sessions.Count == 1 && consumer.CurrentReconcileDelay == TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(2));

            Assert.Equal(0, consumer.UnexpectedReconcileFaults);
            Assert.Equal(1, factory.LastConnection.Channels.Sum(static channel => channel.ConsumeCount));

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// No usable capacity is a completed pass, including while RabbitMQ is down, so the interval stays 15 seconds.
        /// </summary>
        [Fact]
        public async Task Empty_topology_reconcile_keeps_the_fifteen_second_interval()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                catalog: CreateCatalog("Giganews", maxSessions: 1),
                capacity: CreateCapacity("Giganews", active: 0),
                time: time);
            await consumer.StartAsync(CancellationToken.None);

            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.ReconcileWaitsStarted == 1, TimeSpan.FromSeconds(2));
            Assert.Equal(TimeSpan.FromSeconds(15), consumer.CurrentReconcileDelay);
            time.Advance(TimeSpan.FromSeconds(15));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.ReconcileWaitsStarted == 2,
                TimeSpan.FromSeconds(2));
            Assert.Equal(TimeSpan.FromSeconds(15), consumer.CurrentReconcileDelay);
            Assert.Empty(consumer.Sessions);
            Assert.Equal(0, consumer.UnexpectedReconcileFaults);
            time.Advance(TimeSpan.FromSeconds(15));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.ReconcileWaitsStarted == 3 && consumer.CurrentReconcileDelay == TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(2));
            Assert.Empty(consumer.Sessions);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Once RabbitMQ connects, the same loop starts one consumer and a later pass does not start a second one.
        /// </summary>
        [Fact]
        public async Task Broker_recovery_uses_the_same_reconcile_loop_without_a_duplicate_consumer()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                catalog: CreateCatalog("Giganews", maxSessions: 1),
                capacity: CreateCapacity("Giganews", active: 1),
                time: time);
            await consumer.StartAsync(CancellationToken.None);
            var loop = consumer.ReconcileLoop;

            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.ReconcileWaitsStarted == 1, TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromSeconds(15));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.ReconcileWaitsStarted == 2 && consumer.CurrentReconcileDelay == TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(2));
            await connections.StartAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(30));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.Sessions.Count == 1
                      && consumer.CurrentReconcileDelay == TimeSpan.FromSeconds(15)
                      && consumer.ReconcileWaitsStarted == 3,
                TimeSpan.FromSeconds(2));

            var session = Assert.Single(consumer.Sessions);
            Assert.Equal(1, session.Generation);
            Assert.Equal(1, Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel).ConsumeCount);
            Assert.Same(loop, consumer.ReconcileLoop);
            Assert.False(loop!.IsCompleted);
            Assert.Equal(0, consumer.UnexpectedReconcileFaults);

            time.Advance(TimeSpan.FromSeconds(15));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.ReconcileWaitsStarted == 4,
                TimeSpan.FromSeconds(2));
            Assert.Same(loop, consumer.ReconcileLoop);
            session = Assert.Single(consumer.Sessions);
            Assert.Equal(1, session.Generation);
            Assert.Equal(1, Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel).ConsumeCount);
            Assert.Equal(TimeSpan.FromSeconds(15), consumer.CurrentReconcileDelay);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Shutdown while the loop is waiting cancels that wait, counts no fault, and arms no later pass.
        /// </summary>
        [Fact]
        public async Task Shutdown_during_reconcile_backoff_does_not_schedule_another_pass()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                catalog: CreateCatalog("Giganews", maxSessions: 1),
                capacity: CreateCapacity("Giganews", active: 1),
                time: time);
            await consumer.StartAsync(CancellationToken.None);
            var loop = consumer.ReconcileLoop;

            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.ReconcileWaitsStarted == 1, TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromSeconds(15));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.ReconcileWaitsStarted == 2 && consumer.CurrentReconcileDelay == TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(2));
            await consumer.StopAsync(CancellationToken.None);

            Assert.True(loop!.IsCompleted);
            Assert.Equal(0, consumer.UnexpectedReconcileFaults);
            Assert.False(consumer.IsRunning);
            Assert.Empty(consumer.Sessions);
            time.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(2, consumer.ReconcileWaitsStarted);
            Assert.Same(loop, consumer.ReconcileLoop);
            Assert.Equal(0, consumer.UnexpectedReconcileFaults);

            await connections.DisposeAsync();
        }

        /// <summary>
        /// After a failed periodic pass, a capacity snapshot and a connection replacement still maintain one consumer on the same loop.
        /// </summary>
        [Fact]
        public async Task Failed_periodic_reconcile_still_accepts_capacity_and_connection_replacement()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            factory.LastConnection!.CreateChannelException = new InvalidOperationException("channel");
            var capacity = CreateCapacity("Giganews", active: 1);
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                catalog: CreateCatalog("Giganews", maxSessions: 1),
                capacity: capacity,
                time: time);
            await consumer.StartAsync(CancellationToken.None);
            var loop = consumer.ReconcileLoop;

            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.ReconcileWaitsStarted == 1, TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromSeconds(15));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.ReconcileWaitsStarted == 2 && consumer.CurrentReconcileDelay == TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(2));
            Assert.Empty(consumer.Sessions);
            Assert.Equal(0, consumer.UnexpectedReconcileFaults);

            factory.LastConnection.CreateChannelException = null;
            capacity.PublishSnapshot(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Giganews"] = 1 });
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.Sessions.Count == 1, TimeSpan.FromSeconds(2));
            var session = Assert.Single(consumer.Sessions);
            Assert.Equal(1, Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel).ConsumeCount);
            Assert.Equal(TimeSpan.FromSeconds(30), consumer.CurrentReconcileDelay);
            Assert.Same(loop, consumer.ReconcileLoop);

            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;
            factory.LastConnection.SimulateLost();
            await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.Sessions.Count == 1 && consumer.Sessions[0].Generation == 2,
                TimeSpan.FromSeconds(2));

            Assert.Same(loop, consumer.ReconcileLoop);
            Assert.False(loop!.IsCompleted);
            Assert.Equal(1, Assert.IsType<FakeBackFillerRabbitMqChannel>(consumer.Sessions[0].Channel).ConsumeCount);
            Assert.Equal(TimeSpan.FromSeconds(30), consumer.CurrentReconcileDelay);
            Assert.Equal(0, consumer.UnexpectedReconcileFaults);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// A connection replacement that cannot declare topology leaves backoff to the same periodic loop, which then starts one consumer.
        /// </summary>
        [Fact]
        public async Task Connection_replacement_failure_is_retried_by_the_same_reconcile_loop()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var factory = new FakeBackFillerRabbitMqConnectionFactory
            {
                CreateChannelException = new InvalidOperationException("channel"),
            };
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                catalog: CreateCatalog("Giganews", maxSessions: 1),
                capacity: CreateCapacity("Giganews", active: 1),
                time: time);
            await consumer.StartAsync(CancellationToken.None);
            var loop = consumer.ReconcileLoop;
            var first = factory.LastConnection;

            await ArticleWorkTestDeliveries.WaitUntilAsync(() => consumer.ReconcileWaitsStarted == 1, TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromSeconds(15));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.ReconcileWaitsStarted == 2 && consumer.CurrentReconcileDelay == TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(2));

            var reconnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = reconnected;
            first!.SimulateLost();
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => factory.LastConnection is { } current
                      && !ReferenceEquals(current, first)
                      && current.ManualAckChannelCreateAttempts >= 1
                      && consumer.Sessions.Count == 0,
                TimeSpan.FromSeconds(2));
            Assert.Equal(TimeSpan.FromSeconds(30), consumer.CurrentReconcileDelay);
            Assert.Same(loop, consumer.ReconcileLoop);
            Assert.False(loop!.IsCompleted);
            Assert.Equal(0, consumer.UnexpectedReconcileFaults);

            factory.CreateChannelException = null;
            factory.LastConnection!.CreateChannelException = null;
            time.Advance(TimeSpan.FromSeconds(30));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.Sessions.Count == 1
                      && consumer.CurrentReconcileDelay == TimeSpan.FromSeconds(15)
                      && consumer.ReconcileWaitsStarted == 3,
                TimeSpan.FromSeconds(2));

            var session = Assert.Single(consumer.Sessions);
            Assert.Equal(2, session.Generation);
            Assert.Equal(1, Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel).ConsumeCount);
            Assert.Same(loop, consumer.ReconcileLoop);
            Assert.False(loop.IsCompleted);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// One of two desired backbones failing declaration starts only the healthy consumer, backs off to the cap,
        /// and the same loop adds the recovered backbone without replacing the healthy session.
        /// </summary>
        [Fact]
        public async Task Partial_topology_failure_backs_off_and_keeps_the_healthy_consumer()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var failEweka = true;
            factory.LastConnection!.OnManualAckChannelCreated = channel =>
            {
                channel.ExchangeDeclareFailure = exchange =>
                    failEweka && string.Equals(exchange, "backfiller.eweka", StringComparison.Ordinal)
                        ? new InvalidOperationException("declare failed")
                        : null;
            };
            var capacity = new BackboneUsableCapacityState();
            capacity.PublishSnapshot(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Eweka"] = 1,
                ["Giganews"] = 1,
            });
            var consumer = CreateConsumerService(
                connections,
                new DeferredArticleWorkHandler(),
                catalog: new StaticBackFillerProviderCatalog(
                [
                    Provider("Eweka"),
                    Provider("Giganews"),
                ]),
                capacity: capacity,
                time: time);
            await consumer.StartAsync(CancellationToken.None);
            var loop = consumer.ReconcileLoop;

            var healthy = Assert.Single(consumer.Sessions);
            Assert.Equal("Giganews", healthy.Backbone);
            Assert.Equal(1, Assert.IsType<FakeBackFillerRabbitMqChannel>(healthy.Channel).ConsumeCount);
            Assert.Equal(TimeSpan.FromSeconds(15), consumer.CurrentReconcileDelay);
            Assert.Equal(0, consumer.UnexpectedReconcileFaults);

            var waited = new List<TimeSpan>();
            for (var pass = 0; pass < 5; pass++)
            {
                await ArticleWorkTestDeliveries.WaitUntilAsync(
                    () => consumer.ReconcileWaitsStarted == pass + 1,
                    TimeSpan.FromSeconds(2));
                waited.Add(consumer.CurrentReconcileDelay);
                time.Advance(consumer.CurrentReconcileDelay);
            }

            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.ReconcileWaitsStarted == 6,
                TimeSpan.FromSeconds(2));
            Assert.Equal(
            [
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(2),
            ],
            waited);
            Assert.Equal(TimeSpan.FromMinutes(2), consumer.CurrentReconcileDelay);
            Assert.Equal(0, consumer.UnexpectedReconcileFaults);
            Assert.Same(loop, consumer.ReconcileLoop);
            Assert.False(loop!.IsCompleted);
            var stillHealthy = Assert.Single(consumer.Sessions);
            Assert.Same(healthy, stillHealthy);
            Assert.Equal("Giganews", stillHealthy.Backbone);
            Assert.Equal(1, Assert.IsType<FakeBackFillerRabbitMqChannel>(stillHealthy.Channel).ConsumeCount);

            failEweka = false;
            time.Advance(TimeSpan.FromMinutes(2));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.Sessions.Count == 2
                      && consumer.CurrentReconcileDelay == TimeSpan.FromSeconds(15)
                      && consumer.ReconcileWaitsStarted == 7,
                TimeSpan.FromSeconds(2));

            var giganews = Assert.Single(consumer.Sessions, static session => session.Backbone == "Giganews");
            var eweka = Assert.Single(consumer.Sessions, static session => session.Backbone == "Eweka");
            Assert.Same(healthy, giganews);
            Assert.Equal(1, Assert.IsType<FakeBackFillerRabbitMqChannel>(giganews.Channel).ConsumeCount);
            Assert.Equal(1, Assert.IsType<FakeBackFillerRabbitMqChannel>(eweka.Channel).ConsumeCount);
            Assert.Same(loop, consumer.ReconcileLoop);
            Assert.False(loop.IsCompleted);
            Assert.Equal(0, consumer.UnexpectedReconcileFaults);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Static guard: production ArticleWork sources must not use blocking wait/sleep patterns on async paths.
        /// </summary>
        [Fact]
        public void Production_article_work_code_does_not_block_synchronously()
        {
            var directory = FindArticleWorkSourceDirectory();
            Assert.True(Directory.Exists(directory), directory);
            foreach (var text in Directory.GetFiles(directory, "*.cs").Select(static file => File.ReadAllText(file)))
            {
                Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Constructs a single-backbone consumer session wired to the fake connection service and delivery pipeline.
        /// </summary>
        /// <param name="connections">RabbitMQ service providing manual-ack channels.</param>
        /// <param name="handler">Work handler invoked for admitted deliveries.</param>
        /// <param name="publisher">Optional response publisher; defaults to a recording fake.</param>
        /// <returns>An unstarted <see cref="ArticleWorkConsumerSession"/>.</returns>
        private static ArticleWorkConsumerSession CreateSession(
            IRabbitMqService connections,
            IArticleWorkHandler handler,
            RecordingArticleWorkResponsePublisher? publisher = null)
        {
            return new ArticleWorkConsumerSession(
                "Giganews",
                1,
                new ArticleWorkDeliveryPipeline(handler, publisher ?? new RecordingArticleWorkResponsePublisher(), 1024),
                connections,
                NullLogger.Instance);
        }

        /// <summary>
        /// Builds the multi-session consumer service with optional catalog and capacity overrides for topology tests.
        /// </summary>
        /// <param name="connections">Shared RabbitMQ connection service.</param>
        /// <param name="handler">Handler used by each session pipeline.</param>
        /// <param name="catalog">Provider catalog; default defers to runtime wiring when null.</param>
        /// <param name="capacity">Usable capacity snapshots; default empty.</param>
        /// <returns>A new <see cref="ArticleWorkConsumerService"/>.</returns>
        private static ArticleWorkConsumerService CreateConsumerService(
            IRabbitMqService connections,
            IArticleWorkHandler handler,
            IBackFillerProviderCatalog? catalog = null,
            IBackboneUsableCapacityProvider? capacity = null,
            TimeProvider? time = null,
            Func<CancellationToken, Task>? reconcileLoopBody = null)
        {
            return new ArticleWorkConsumerService(
                connections,
                RabbitMqServiceTests.CreateFastRuntime(),
                handler,
                new RecordingArticleWorkResponsePublisher(),
                NullLogger<ArticleWorkConsumerService>.Instance,
                catalog,
                capacity,
                time,
                reconcileLoopBody);
        }

        /// <summary>
        /// Single-provider catalog entry for lifecycle tests.
        /// </summary>
        /// <param name="backbone">Provider backbone name.</param>
        /// <param name="maxSessions">Configured NNTP slot ceiling for that backbone.</param>
        /// <returns>A static catalog with one definition.</returns>
        private static StaticBackFillerProviderCatalog CreateCatalog(string backbone, int maxSessions) =>
            new([Provider(backbone, maxSessions)]);

        /// <summary>One provider definition with a single eager session unless <paramref name="maxSessions"/> says otherwise.</summary>
        /// <param name="backbone">Provider backbone label.</param>
        /// <param name="maxSessions">Configured NNTP slot ceiling.</param>
        /// <returns>A catalog entry that does not open a live NNTP connection.</returns>
        private static VectorNNTPD.BackFillerService.Nntp.BackFillerProviderDefinition Provider(string backbone, int maxSessions = 1) =>
            new(
                backbone,
                "127.0.0.1",
                119,
                false,
                "nntp-user",
                "p",
                0,
                maxSessions);

        /// <summary>
        /// Publishes an initial usable-capacity snapshot for one backbone (omit publish when <paramref name="active"/> is zero).
        /// </summary>
        /// <param name="backbone">Backbone key in the snapshot.</param>
        /// <param name="active">Active usable slots; values above zero publish the snapshot.</param>
        /// <returns>Mutable capacity state for driving consumer start/stop reactions.</returns>
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

        /// <summary>
        /// Locates <c>src/VectorNNTPD.BackFillerService/ArticleWork</c> by walking up from the test output directory.
        /// </summary>
        /// <returns>Absolute path to production ArticleWork sources.</returns>
        /// <exception cref="DirectoryNotFoundException">When the tree cannot be resolved.</exception>
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
        /// Started session plus fake factory and connection service; disposed by stopping the session then the connection service.
        /// </summary>
        private sealed class ConsumerContext : IAsyncDisposable
        {
            /// <summary>
            /// Captures factory, connection, started session, and publisher for lifecycle tests.
            /// </summary>
            /// <param name="factory">Fake RabbitMQ connection factory.</param>
            /// <param name="connections">Started connection service.</param>
            /// <param name="session">Running consumer session under test.</param>
            /// <param name="publisher">Publisher wired into the session pipeline.</param>
            private ConsumerContext(
                FakeBackFillerRabbitMqConnectionFactory factory,
                RabbitMqService connections,
                ArticleWorkConsumerSession session,
                RecordingArticleWorkResponsePublisher publisher)
            {
                Factory = factory;
                Connections = connections;
                Session = session;
                Publisher = publisher;
            }

            /// <summary>Fake factory recording connect and channel creation.</summary>
            public FakeBackFillerRabbitMqConnectionFactory Factory { get; }

            /// <summary>Shared RabbitMQ service used by the session.</summary>
            public RabbitMqService Connections { get; }

            /// <summary>Consumer session under test (started).</summary>
            public ArticleWorkConsumerSession Session { get; }

            /// <summary>Recording publisher installed in the session pipeline.</summary>
            public RecordingArticleWorkResponsePublisher Publisher { get; }

            /// <summary>
            /// Connects, starts a single Giganews session with optional handler, and returns the owning context.
            /// </summary>
            /// <param name="handler">Handler for admitted work; defaults to a deferred no-op handler.</param>
            /// <returns>Disposable context with a running session.</returns>
            internal static async Task<ConsumerContext> StartSessionAsync(IArticleWorkHandler? handler = null)
            {
                var factory = new FakeBackFillerRabbitMqConnectionFactory();
                var connections = RabbitMqServiceTests.CreateService(factory);
                await connections.StartAsync(CancellationToken.None);
                var publisher = new RecordingArticleWorkResponsePublisher();
                var session = CreateSession(connections, handler ?? new DeferredArticleWorkHandler(), publisher);
                await session.StartAsync(CancellationToken.None);
                return new ConsumerContext(factory, connections, session, publisher);
            }

            /// <summary>Retires the session then disposes the connection service.</summary>
            public async ValueTask DisposeAsync()
            {
                await Session.DisposeAsync();
                await Connections.DisposeAsync();
            }
        }
    }
}
