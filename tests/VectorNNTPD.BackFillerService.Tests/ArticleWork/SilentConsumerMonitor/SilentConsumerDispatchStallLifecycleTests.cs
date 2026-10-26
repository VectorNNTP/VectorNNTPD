using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// DispatchStall / ProcessingStall / local ConsumerLoss through the real session dispatch path
    /// (<see cref="ArticleWorkConsumerSession.DispatchAcquireHold"/> → QueuedCount/ActiveCount →
    /// <see cref="SilentConsumerSessionObservation.FromSession"/> → engine), not synthetic observation structs.
    /// Fake RabbitMQ channels; blocked tasks are always released in each test.
    /// </summary>
    public sealed class SilentConsumerDispatchStallLifecycleTests
    {
        /// <summary>
        /// Holding <see cref="ArticleWorkConsumerSession.DispatchAcquireHold"/> after dispatch acquire opens instance dispatch stall;
        /// healthy shared-queue poll does not open shared dispatch stall for the same scenario.
        /// </summary>
        /// <returns>A task that completes when assertions pass and blocked delivery is released.</returns>
        [Fact]
        public async Task Dispatch_blocked_before_active_opens_session_dispatch_stall()
        {
            var monitorTime = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, monitorTime);
            await using var harness = await SessionHarness.StartAsync(connectionNumber: 1, connectionLimit: 1);

            harness.Session.DispatchAcquired = NewSource();
            harness.Session.DispatchAcquireHold = NewSource();
            var processing = harness.Deliver(7);
            await harness.Session.DispatchAcquired.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(ArticleWorkConsumerState.Running, harness.Session.State);
            Assert.True(harness.Session.Channel is { IsOpen: true });
            Assert.Equal(1, harness.Session.QueuedCount);
            Assert.Equal(0, harness.Session.ActiveCount);
            Assert.False(harness.Session.IsActiveProcessingStalled(ArticleWorkConsumerService.ProcessingStallThreshold));

            var observation = SilentConsumerSessionObservation.FromSession(
                harness.Session,
                ArticleWorkConsumerService.ProcessingStallThreshold);
            Assert.Equal(1, observation.QueuedCount);
            Assert.Equal(0, observation.ActiveCount);
            Assert.Equal(harness.Session.SessionKey, observation.SessionKey);

            _ = engine.ObserveInstance([observation], desiredRunningSessions: 1);
            monitorTime.Advance(SilentConsumerMonitorDefaults.DispatchStall.OpenWindow);
            var opened = engine
                .ObserveInstance(
                    [
                        SilentConsumerSessionObservation.FromSession(
                            harness.Session,
                            ArticleWorkConsumerService.ProcessingStallThreshold),
                    ],
                    1)
                .Single(r => r.Key.Condition == SilentConsumerCondition.DispatchStall);

            Assert.Equal(ObservationTransition.Opened, opened.Transition);
            Assert.Equal(ObservationScope.Instance, opened.Key.Scope);
            Assert.Equal(harness.Session.SessionKey, opened.Key.QueueOrBackbone);
            Assert.Equal("a.example", engine.InstanceId);
            Assert.Contains("DispatchAcquireHold", opened.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                engine.ObserveSharedQueue(
                        "backfiller.giganews",
                        SharedSuccess(consumers: 1, ready: 0, unacked: 1, ackRate: 5),
                        expectedActive: true),
                r => r.Key.Condition == SilentConsumerCondition.DispatchStall
                     && r.Transition == ObservationTransition.Opened);

            harness.Session.DispatchAcquireHold.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// A running session with no queued or active work stays inactive for dispatch stall through the open window.
        /// </summary>
        /// <returns>A task that completes when assertions pass.</returns>
        [Fact]
        public async Task Idle_running_session_does_not_open_dispatch_stall()
        {
            var monitorTime = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, monitorTime);
            await using var harness = await SessionHarness.StartAsync(connectionNumber: 1, connectionLimit: 1);

            Assert.Equal(0, harness.Session.QueuedCount);
            Assert.Equal(0, harness.Session.ActiveCount);

            var observation = SilentConsumerSessionObservation.FromSession(
                harness.Session,
                ArticleWorkConsumerService.ProcessingStallThreshold);
            _ = engine.ObserveInstance([observation], 1);
            monitorTime.Advance(SilentConsumerMonitorDefaults.DispatchStall.OpenWindow);
            var result = engine
                .ObserveInstance(
                    [
                        SilentConsumerSessionObservation.FromSession(
                            harness.Session,
                            ArticleWorkConsumerService.ProcessingStallThreshold),
                    ],
                    1)
                .Single(r => r.Key.Condition == SilentConsumerCondition.DispatchStall);

            Assert.Equal(ConditionSignal.Inactive, result.Signal);
            Assert.Equal(ObservationPhase.Clear, result.Phase);
            Assert.Contains("idle", result.Detail, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Dispatch stall opens only on the blocked session when a healthy sibling shares the same backbone and connection pool.
        /// </summary>
        /// <returns>A task that completes when assertions pass and the blocked delivery is released.</returns>
        [Fact]
        public async Task Blocked_session_dispatch_stall_independent_of_healthy_sibling_same_backbone()
        {
            var monitorTime = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, monitorTime);
            await using var shared = await SharedConnection.StartAsync();
            await using var stalled = await SessionHarness.StartAsync(
                shared.Connections,
                connectionNumber: 1,
                connectionLimit: 2);
            await using var healthy = await SessionHarness.StartAsync(
                shared.Connections,
                connectionNumber: 2,
                connectionLimit: 2);

            stalled.Session.DispatchAcquired = NewSource();
            stalled.Session.DispatchAcquireHold = NewSource();
            var processing = stalled.Deliver(7);
            await stalled.Session.DispatchAcquired.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, stalled.Session.QueuedCount);
            Assert.Equal(0, stalled.Session.ActiveCount);
            Assert.Equal(0, healthy.Session.QueuedCount);

            SilentConsumerSessionObservation[] Project() =>
            [
                SilentConsumerSessionObservation.FromSession(
                    stalled.Session,
                    ArticleWorkConsumerService.ProcessingStallThreshold),
                SilentConsumerSessionObservation.FromSession(
                    healthy.Session,
                    ArticleWorkConsumerService.ProcessingStallThreshold),
            ];

            _ = engine.ObserveInstance(Project(), desiredRunningSessions: 2);
            monitorTime.Advance(SilentConsumerMonitorDefaults.DispatchStall.OpenWindow);
            var results = engine.ObserveInstance(Project(), 2);

            var stalledKey = stalled.Session.SessionKey;
            var healthyKey = healthy.Session.SessionKey;
            Assert.Equal(
                ObservationTransition.Opened,
                results.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.DispatchStall
                    && r.Key.QueueOrBackbone == stalledKey).Transition);
            Assert.Equal(
                ConditionSignal.Inactive,
                results.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.DispatchStall
                    && r.Key.QueueOrBackbone == healthyKey).Signal);

            monitorTime.Advance(SilentConsumerMonitorDefaults.DispatchStall.ResolveWindow);
            Assert.Equal(
                ObservationPhase.Open,
                engine.ObserveInstance(Project(), 2)
                    .Single(r =>
                        r.Key.Condition == SilentConsumerCondition.DispatchStall
                        && r.Key.QueueOrBackbone == stalledKey)
                    .Phase);

            stalled.Session.DispatchAcquireHold.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// Releasing dispatch hold on one session resolves its dispatch stall after the resolve window while the other stays open.
        /// </summary>
        /// <returns>A task that completes when assertions pass and both deliveries finish.</returns>
        [Fact]
        public async Task Releasing_dispatch_hold_resolves_dispatch_stall_only_for_that_session()
        {
            var monitorTime = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, monitorTime);
            await using var shared = await SharedConnection.StartAsync();
            await using var session1 = await SessionHarness.StartAsync(
                shared.Connections,
                connectionNumber: 1,
                connectionLimit: 2);
            await using var session2 = await SessionHarness.StartAsync(
                shared.Connections,
                connectionNumber: 2,
                connectionLimit: 2);

            session1.Session.DispatchAcquired = NewSource();
            session1.Session.DispatchAcquireHold = NewSource();
            session2.Session.DispatchAcquired = NewSource();
            session2.Session.DispatchAcquireHold = NewSource();

            var p1 = session1.Deliver(1);
            var p2 = session2.Deliver(2);
            await session1.Session.DispatchAcquired.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await session2.Session.DispatchAcquired.Task.WaitAsync(TimeSpan.FromSeconds(2));

            SilentConsumerSessionObservation[] Project() =>
            [
                SilentConsumerSessionObservation.FromSession(
                    session1.Session,
                    ArticleWorkConsumerService.ProcessingStallThreshold),
                SilentConsumerSessionObservation.FromSession(
                    session2.Session,
                    ArticleWorkConsumerService.ProcessingStallThreshold),
            ];

            _ = engine.ObserveInstance(Project(), 2);
            monitorTime.Advance(SilentConsumerMonitorDefaults.DispatchStall.OpenWindow);
            var opened = engine.ObserveInstance(Project(), 2);
            Assert.All(
                opened.Where(r => r.Key.Condition == SilentConsumerCondition.DispatchStall),
                r => Assert.Equal(ObservationTransition.Opened, r.Transition));

            // Session 1 recovers; session 2 remains blocked.
            session1.Session.DispatchAcquireHold.TrySetResult();
            await p1.WaitAsync(TimeSpan.FromSeconds(2));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => session1.Session.QueuedCount == 0 && session1.Session.ActiveCount == 0,
                TimeSpan.FromSeconds(2));

            _ = engine.ObserveInstance(Project(), 2);
            monitorTime.Advance(SilentConsumerMonitorDefaults.DispatchStall.ResolveWindow);
            var after = engine.ObserveInstance(Project(), 2);

            Assert.Equal(
                ObservationTransition.Resolved,
                after.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.DispatchStall
                    && r.Key.QueueOrBackbone == session1.Session.SessionKey).Transition);
            Assert.Equal(
                ObservationPhase.Open,
                after.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.DispatchStall
                    && r.Key.QueueOrBackbone == session2.Session.SessionKey).Phase);

            session2.Session.DispatchAcquireHold.TrySetResult();
            await p2.WaitAsync(TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// Active processing held past <see cref="ArticleWorkConsumerService.ProcessingStallThreshold"/> opens instance processing stall from real session metrics.
        /// </summary>
        /// <returns>A task that completes when assertions pass and the handler gate is released.</returns>
        [Fact]
        public async Task Active_processing_stall_projects_from_real_session_into_instance_alert()
        {
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, clock);
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "held",
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                Started = NewSource(),
                Gate = NewSource(),
            };
            await using var harness = await SessionHarness.StartAsync(
                connectionNumber: 1,
                connectionLimit: 1,
                handler: handler,
                time: clock);

            var processing = harness.Deliver(9);
            await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, harness.Session.ActiveCount);
            Assert.True(harness.Session.HasActiveProcessing);

            clock.Advance(ArticleWorkConsumerService.ProcessingStallThreshold);
            Assert.True(
                harness.Session.IsActiveProcessingStalled(ArticleWorkConsumerService.ProcessingStallThreshold));

            var observation = SilentConsumerSessionObservation.FromSession(
                harness.Session,
                ArticleWorkConsumerService.ProcessingStallThreshold);
            Assert.True(observation.IsActiveProcessingStalled);

            _ = engine.ObserveInstance([observation], 1);
            clock.Advance(SilentConsumerMonitorDefaults.ProcessingStall.OpenWindow);
            var opened = engine
                .ObserveInstance(
                    [
                        SilentConsumerSessionObservation.FromSession(
                            harness.Session,
                            ArticleWorkConsumerService.ProcessingStallThreshold),
                    ],
                    1)
                .Single(r => r.Key.Condition == SilentConsumerCondition.ProcessingStall);

            Assert.Equal(ObservationTransition.Opened, opened.Transition);
            Assert.Equal(ObservationScope.Instance, opened.Key.Scope);
            Assert.Equal(harness.Session.SessionKey, opened.Key.QueueOrBackbone);
            Assert.Contains("5328", opened.Detail, StringComparison.Ordinal);

            // Monitor does not retire; release handler ourselves.
            handler.Gate!.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// After session retire, instance consumer loss is active when desired running sessions exceed observed running channels.
        /// </summary>
        /// <returns>A task that completes when assertions pass.</returns>
        [Fact]
        public async Task Local_consumer_loss_projects_when_desired_sessions_exist_but_none_running()
        {
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, TimeProvider.System);
            await using var harness = await SessionHarness.StartAsync(connectionNumber: 1, connectionLimit: 1);
            await harness.Session.RetireAsync();
            Assert.Equal(ArticleWorkConsumerState.Stopped, harness.Session.State);

            var observation = SilentConsumerSessionObservation.FromSession(
                harness.Session,
                ArticleWorkConsumerService.ProcessingStallThreshold);
            Assert.False(observation.HasOpenChannel || observation.State == ArticleWorkConsumerState.Running);

            var loss = engine.ObserveInstance([observation], desiredRunningSessions: 2)
                .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss);
            Assert.Equal(ObservationScope.Instance, loss.Key.Scope);
            Assert.Equal("local", loss.Key.QueueOrBackbone);
            Assert.Equal(ConditionSignal.Active, loss.Signal);
        }

        /// <summary>Task completion source with continuations off the completing thread (test synchronization).</summary>
        /// <returns>A new unset <see cref="TaskCompletionSource"/>.</returns>
        private static TaskCompletionSource NewSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Successful shared-queue management poll for cross-scope assertions in dispatch tests.</summary>
        /// <param name="consumers">Consumer gauge.</param>
        /// <param name="ready">Messages ready.</param>
        /// <param name="unacked">Unacknowledged messages.</param>
        /// <param name="ackRate">Ack rate.</param>
        /// <returns><see cref="ManagementPollKind.Success"/> with a Giganews queue snapshot.</returns>
        private static ManagementPollResult SharedSuccess(
            long consumers,
            long ready,
            long unacked,
            double ackRate) =>
            new()
            {
                Kind = ManagementPollKind.Success,
                Reason = "ok",
                Snapshot = new ManagementQueueSnapshot
                {
                    QueueName = "backfiller.giganews",
                    VirtualHost = "/",
                    SampledAtUtc = DateTimeOffset.Parse("2026-10-25T20:00:00Z"),
                    Consumers = consumers,
                    MessagesReady = ready,
                    MessagesUnacknowledged = unacked,
                    AckRate = ackRate,
                    DeliverGetRate = 0,
                },
            };

        /// <summary>
        /// Owns one started fake <see cref="RabbitMqService"/> shared by multiple session harnesses on the same connection generation.
        /// </summary>
        private sealed class SharedConnection : IAsyncDisposable
        {
            /// <summary>Wraps an already-started broker service.</summary>
            /// <param name="connections">Started RabbitMQ service from test factory helpers.</param>
            private SharedConnection(RabbitMqService connections) => Connections = connections;

            /// <summary>Broker service used to create multiple consumer sessions without separate connection factories.</summary>
            public RabbitMqService Connections { get; }

            /// <summary>Starts fake RabbitMQ via <see cref="RabbitMqServiceTests.CreateService"/>.</summary>
            /// <returns>Shared connection holder; dispose to tear down the service.</returns>
            public static async Task<SharedConnection> StartAsync()
            {
                var factory = new FakeBackFillerRabbitMqConnectionFactory();
                var connections = RabbitMqServiceTests.CreateService(factory);
                await connections.StartAsync(CancellationToken.None);
                return new SharedConnection(connections);
            }

            /// <inheritdoc />
            public async ValueTask DisposeAsync() => await Connections.DisposeAsync();
        }

        /// <summary>
        /// Starts a real <see cref="ArticleWorkConsumerSession"/> against fake RabbitMQ for end-to-end dispatch and stall projection.
        /// </summary>
        private sealed class SessionHarness : IAsyncDisposable
        {
            /// <summary>Creates a harness around an already-started session and channel.</summary>
            /// <param name="ownedConnections">Non-null when this harness started its own <see cref="RabbitMqService"/>.</param>
            /// <param name="session">Running consumer session under test.</param>
            /// <param name="channel">Fake channel cast from the session.</param>
            private SessionHarness(
                RabbitMqService? ownedConnections,
                ArticleWorkConsumerSession session,
                FakeBackFillerRabbitMqChannel channel)
            {
                OwnedConnections = ownedConnections;
                Session = session;
                Channel = channel;
            }

            /// <summary>Broker service disposed only when created by this harness (not when injected via shared connection).</summary>
            private RabbitMqService? OwnedConnections { get; }

            /// <summary>Consumer session exercising the production dispatch pipeline.</summary>
            public ArticleWorkConsumerSession Session { get; }

            /// <summary>Fake AMQP channel used to inject deliveries.</summary>
            public FakeBackFillerRabbitMqChannel Channel { get; }

            /// <summary>Delivers a canonical test message with the given delivery tag.</summary>
            /// <param name="deliveryTag">Broker delivery tag.</param>
            /// <returns>The channel deliver task (handler processing).</returns>
            public Task Deliver(ulong deliveryTag) =>
                Channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical(deliveryTag));

            /// <summary>Starts a session with a dedicated fake broker connection factory.</summary>
            /// <param name="connectionNumber">Session slot within the backbone limit.</param>
            /// <param name="connectionLimit">Max concurrent sessions for the backbone.</param>
            /// <param name="handler">Optional article handler; default auto-completing success handler.</param>
            /// <param name="time">Clock for stall timing; defaults to system time when null.</param>
            /// <returns>Started harness owning its broker service when connections are not shared.</returns>
            public static Task<SessionHarness> StartAsync(
                int connectionNumber,
                int connectionLimit,
                IArticleWorkHandler? handler = null,
                TimeProvider? time = null) =>
                StartAsync(connections: null, connectionNumber, connectionLimit, handler, time);

            /// <summary>
            /// Starts a session on optional shared <see cref="IRabbitMqService"/>; uses fake RabbitMQ, not a live broker.
            /// </summary>
            /// <param name="connections">Shared broker service, or null to create and own one.</param>
            /// <param name="connectionNumber">Session slot within the backbone limit.</param>
            /// <param name="connectionLimit">Max concurrent sessions for the backbone.</param>
            /// <param name="handler">Optional article handler.</param>
            /// <param name="time">Clock for stall timing.</param>
            /// <returns>Started harness with session channel exposed as <see cref="FakeBackFillerRabbitMqChannel"/>.</returns>
            public static async Task<SessionHarness> StartAsync(
                IRabbitMqService? connections,
                int connectionNumber,
                int connectionLimit,
                IArticleWorkHandler? handler = null,
                TimeProvider? time = null)
            {
                // Only own the broker service when this harness created it.
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

                handler ??= new ControllableArticleWorkHandler
                {
                    Outcome = ArticleWorkOutcome.Success,
                    Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                    ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                    Gate = NewSource(),
                };
                // Auto-complete fast handlers that are not intentionally held.
                if (handler is ControllableArticleWorkHandler controllable
                    && controllable.Gate is not null
                    && controllable.Started is null
                    && controllable.Outcome == ArticleWorkOutcome.Success)
                {
                    controllable.Gate.TrySetResult();
                }

                var session = new ArticleWorkConsumerSession(
                    "Giganews",
                    prefetch: 1,
                    new ArticleWorkDeliveryPipeline(
                        handler,
                        new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true },
                        1024),
                    service,
                    NullLogger.Instance,
                    new BackFillerShutdownRuntimeOptions(TimeSpan.FromSeconds(30), DrainQueuedWork: true, FinishActiveArticles: true),
                    connectionNumber,
                    connectionLimit,
                    time);
                await session.StartAsync(CancellationToken.None);
                var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel);
                return new SessionHarness(owned, session, channel);
            }

            /// <summary>Disposes the session and owned broker service when applicable.</summary>
            /// <returns>ValueTask completing when disposal finishes.</returns>
            public async ValueTask DisposeAsync()
            {
                await Session.DisposeAsync();
                if (OwnedConnections is not null)
                {
                    await OwnedConnections.DisposeAsync();
                }
            }
        }
    }
}
