using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Opt-in lab runtime validation for SilentConsumerMonitor against the shared lab RabbitMQ broker.
    /// Disabled unless <c>VECTORNNTPD_SILENT_CONSUMER_LAB=1</c>. Does not start OS BackFiller processes;
    /// uses production session/consumer/monitor types with <see cref="ControllableArticleWorkHandler"/> /
    /// <see cref="ArticleWorkConsumerSession.DispatchAcquireHold"/> seams (no production code changes).
    /// </summary>
    public sealed class SilentConsumerLabRuntimeTests
    {
        /// <summary>
        /// Scenario C: DispatchAcquireHold opens instance DispatchStall; release resolves; Event 5328 path stays idle.
        /// </summary>
        [Fact]
        public async Task ScenarioC_dispatch_stall_detects_and_resolves_on_lab_broker()
        {
            if (!SilentConsumerLabOptions.IsEnabled())
            {
                return;
            }

            var stress = SilentConsumerLabOptions.CreateStressBrokerOptions();
            var runId = $"scm-lab-c-{Guid.NewGuid():N}";
            var rabbitOptions = SilentConsumerLabOptions.CreateRabbitMqOptions(stress);
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-26T15:00:00Z"));
            var logger = new CollectingLogger<SilentConsumerMonitorEngine>();
            var engine = new SilentConsumerMonitorEngine(() => "lab-bf-c.example", logger, clock);
            await using var connections = await SilentConsumerLabHarness.StartLiveRabbitAsync(
                rabbitOptions,
                "VectorNNTPD.BackFillerService:silent-consumer-lab-c",
                CancellationToken.None);
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "lab-dispatch",
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                Gate = NewSource(),
            };
            handler.Gate!.TrySetResult();
            await using var consumer = SilentConsumerLabHarness.CreateConsumer(connections, handler, clock);
            await consumer.StartAsync(CancellationToken.None);
            var session = await SilentConsumerLabHarness.WaitForRunningSessionAsync(
                consumer,
                TimeSpan.FromSeconds(60));

            var before = await SilentConsumerLabHarness.SampleQueueAsync(stress, CancellationToken.None);
            Console.WriteLine(
                $"ScenarioC start utc={DateTimeOffset.UtcNow:o} ready={before.Ready} unacked={before.Unacked} consumers={before.Consumers}");

            session.DispatchAcquired = NewSource();
            session.DispatchAcquireHold = NewSource();
            await SilentConsumerLabHarness.PublishOwnedWorkAsync(stress, runId, CancellationToken.None);
            await session.DispatchAcquired.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, session.QueuedCount);
            Assert.Equal(0, session.ActiveCount);
            Assert.False(session.IsActiveProcessingStalled(ArticleWorkConsumerService.ProcessingStallThreshold));

            var mid = await SilentConsumerLabHarness.SampleQueueAsync(stress, CancellationToken.None);
            Console.WriteLine(
                $"ScenarioC held utc={DateTimeOffset.UtcNow:o} ready={mid.Ready} unacked={mid.Unacked} consumers={mid.Consumers} queued={session.QueuedCount} active={session.ActiveCount}");

            var opened = SilentConsumerLabHarness
                .ObserveInstanceAfterOpenWindow(
                    engine,
                    [session],
                    desired: 1,
                    clock,
                    SilentConsumerMonitorDefaults.DispatchStall.OpenWindow)
                .Single(r => r.Key.Condition == SilentConsumerCondition.DispatchStall);
            Assert.Equal(ObservationTransition.Opened, opened.Transition);
            Assert.Equal(ObservationScope.Instance, opened.Key.Scope);
            Assert.Contains("DispatchAcquireHold", opened.Detail, StringComparison.Ordinal);
            Assert.Contains(
                logger.Entries,
                e => e.EventId.Id == 9110 && e.Message.Contains("DispatchStall", StringComparison.Ordinal));

            session.DispatchAcquireHold.TrySetResult();
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => session.QueuedCount == 0 && session.ActiveCount == 0,
                TimeSpan.FromSeconds(30));

            var resolved = SilentConsumerLabHarness.ResolveInstanceCondition(
                engine,
                [session],
                desired: 1,
                clock,
                SilentConsumerCondition.DispatchStall,
                SilentConsumerMonitorDefaults.DispatchStall.ResolveWindow);
            Assert.Equal(ObservationTransition.Resolved, resolved.Transition);
            Assert.Contains(logger.Entries, e => e.EventId.Id == 9112);

            var discarded = await SilentConsumerLabHarness.CleanupOwnedAsync(
                stress,
                runId,
                CancellationToken.None);
            var after = await SilentConsumerLabHarness.SampleQueueAsync(stress, CancellationToken.None);
            Console.WriteLine(
                $"ScenarioC end utc={DateTimeOffset.UtcNow:o} discarded={discarded} ready={after.Ready} unacked={after.Unacked} consumers={after.Consumers}");
            await consumer.StopAsync(CancellationToken.None);
        }

        /// <summary>
        /// Scenario B: handler Gate opens ProcessingStall on one instance; healthy sibling stays clear; reconcile retires.
        /// </summary>
        [Fact]
        public async Task ScenarioB_processing_stall_sibling_healthy_and_reconcile_recovers()
        {
            if (!SilentConsumerLabOptions.IsEnabled())
            {
                return;
            }

            var stress = SilentConsumerLabOptions.CreateStressBrokerOptions();
            var runId = $"scm-lab-b-{Guid.NewGuid():N}";
            var rabbitOptions = SilentConsumerLabOptions.CreateRabbitMqOptions(stress);
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-26T15:10:00Z"));
            var stalledLogger = new CollectingLogger<SilentConsumerMonitorEngine>();
            var healthyLogger = new CollectingLogger<SilentConsumerMonitorEngine>();
            var stalledEngine = new SilentConsumerMonitorEngine(() => "lab-bf-b-stalled.example", stalledLogger, clock);
            var healthyEngine = new SilentConsumerMonitorEngine(() => "lab-bf-b-healthy.example", healthyLogger, clock);

            await using var stalledConnections = await SilentConsumerLabHarness.StartLiveRabbitAsync(
                rabbitOptions,
                "VectorNNTPD.BackFillerService:silent-consumer-lab-b-stalled",
                CancellationToken.None);
            await using var healthyConnections = await SilentConsumerLabHarness.StartLiveRabbitAsync(
                rabbitOptions,
                "VectorNNTPD.BackFillerService:silent-consumer-lab-b-healthy",
                CancellationToken.None);

            var stalledHandler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "lab-held",
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                Started = NewSource(),
                Gate = NewSource(),
            };
            var healthyHandler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "lab-ok",
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                Gate = NewSource(),
            };
            healthyHandler.Gate!.TrySetResult();

            await using var stalledConsumer = SilentConsumerLabHarness.CreateConsumer(
                stalledConnections,
                stalledHandler,
                clock);
            await using var healthyConsumer = SilentConsumerLabHarness.CreateConsumer(
                healthyConnections,
                healthyHandler,
                clock);
            // Start stalled consumer first so the held delivery is routed to it (StartAsync is one-shot).
            await stalledConsumer.StartAsync(CancellationToken.None);
            var stalledSession = await SilentConsumerLabHarness.WaitForRunningSessionAsync(
                stalledConsumer,
                TimeSpan.FromSeconds(60));

            var before = await SilentConsumerLabHarness.SampleQueueAsync(stress, CancellationToken.None);
            Console.WriteLine(
                $"ScenarioB start utc={DateTimeOffset.UtcNow:o} ready={before.Ready} unacked={before.Unacked} consumers={before.Consumers}");

            await SilentConsumerLabHarness.PublishOwnedWorkAsync(stress, runId, CancellationToken.None);
            await stalledHandler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, stalledSession.ActiveCount);
            Assert.True(stalledSession.HasActiveProcessing);

            await healthyConsumer.StartAsync(CancellationToken.None);
            var healthySession = await SilentConsumerLabHarness.WaitForRunningSessionAsync(
                healthyConsumer,
                TimeSpan.FromSeconds(60));
            await SilentConsumerLabHarness.PublishOwnedWorkAsync(stress, runId, CancellationToken.None);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => healthyHandler.HandleCount >= 1,
                TimeSpan.FromSeconds(30));

            clock.Advance(ArticleWorkConsumerService.ProcessingStallThreshold);
            Assert.True(stalledSession.IsActiveProcessingStalled(ArticleWorkConsumerService.ProcessingStallThreshold));
            Assert.False(healthySession.IsActiveProcessingStalled(ArticleWorkConsumerService.ProcessingStallThreshold));

            var mid = await SilentConsumerLabHarness.SampleQueueAsync(stress, CancellationToken.None);
            Console.WriteLine(
                $"ScenarioB stalled utc={DateTimeOffset.UtcNow:o} ready={mid.Ready} unacked={mid.Unacked} consumers={mid.Consumers}");

            var stalledOpened = SilentConsumerLabHarness
                .ObserveInstanceAfterOpenWindow(
                    stalledEngine,
                    [stalledSession],
                    desired: 1,
                    clock,
                    SilentConsumerMonitorDefaults.ProcessingStall.OpenWindow)
                .Single(r => r.Key.Condition == SilentConsumerCondition.ProcessingStall);
            Assert.Equal(ObservationTransition.Opened, stalledOpened.Transition);
            Assert.Contains("5328", stalledOpened.Detail, StringComparison.Ordinal);
            Assert.Contains(stalledLogger.Entries, e => e.EventId.Id == 9110);

            var healthyProcessing = healthyEngine
                .ObserveInstance(
                    [
                        SilentConsumerSessionObservation.FromSession(
                            healthySession,
                            ArticleWorkConsumerService.ProcessingStallThreshold),
                    ],
                    desiredRunningSessions: 1)
                .Single(r => r.Key.Condition == SilentConsumerCondition.ProcessingStall);
            Assert.Equal(ConditionSignal.Inactive, healthyProcessing.Signal);
            Assert.DoesNotContain(healthyLogger.Entries, e => e.EventId.Id == 9110);

            // Monitor remains observational: reconcile owns recovery.
            await stalledConsumer.ReconcileAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(ArticleWorkConsumerState.Stopped, stalledSession.State);
            stalledHandler.Gate!.TrySetResult();

            var discarded = await SilentConsumerLabHarness.CleanupOwnedAsync(
                stress,
                runId,
                CancellationToken.None);
            var after = await SilentConsumerLabHarness.SampleQueueAsync(stress, CancellationToken.None);
            Console.WriteLine(
                $"ScenarioB end utc={DateTimeOffset.UtcNow:o} discarded={discarded} ready={after.Ready} unacked={after.Unacked} consumers={after.Consumers}");
            await healthyConsumer.StopAsync(CancellationToken.None);
            await stalledConsumer.StopAsync(CancellationToken.None);
        }

        /// <summary>
        /// Scenario E: consumers drop with ready backlog → shared ConsumerLoss opens; restore consumers → resolves.
        /// </summary>
        [Fact]
        public async Task ScenarioE_consumer_loss_shared_alert_and_restore()
        {
            if (!SilentConsumerLabOptions.IsEnabled())
            {
                return;
            }

            var stress = SilentConsumerLabOptions.CreateStressBrokerOptions();
            var runId = $"scm-lab-e-{Guid.NewGuid():N}";
            var rabbitOptions = SilentConsumerLabOptions.CreateRabbitMqOptions(stress);
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-26T15:20:00Z"));
            var logger = new CollectingLogger<SilentConsumerMonitorEngine>();
            var engine = new SilentConsumerMonitorEngine(() => "lab-bf-e.example", logger, clock);

            // Seed ready backlog with no consumer (StartAsync is one-shot — restore uses a fresh consumer).
            var before = await SilentConsumerLabHarness.SampleQueueAsync(stress, CancellationToken.None);
            Console.WriteLine(
                $"ScenarioE start utc={DateTimeOffset.UtcNow:o} ready={before.Ready} unacked={before.Unacked} consumers={before.Consumers}");
            await SilentConsumerLabHarness.PublishOwnedWorkAsync(stress, runId, CancellationToken.None);
            await SilentConsumerLabHarness.PublishOwnedWorkAsync(stress, runId, CancellationToken.None);

            var lossShape = await WaitForQueueAsync(
                stress,
                static s => s.Consumers == 0 && s.Ready >= 1,
                TimeSpan.FromSeconds(30));
            Console.WriteLine(
                $"ScenarioE loss utc={DateTimeOffset.UtcNow:o} ready={lossShape.Ready} unacked={lossShape.Unacked} consumers={lossShape.Consumers}");

            using var http = SilentConsumerLabHarness.CreateHttpServices();
            var management = new ManagementQueueClient(
                http.GetRequiredService<IHttpClientFactory>(),
                Options.Create(rabbitOptions),
                clock);
            var poll = await management.GetQueueAsync("/", SilentConsumerLabHarness.GiganewsQueue, CancellationToken.None);
            Assert.Equal(ManagementPollKind.Success, poll.Kind);
            Assert.NotNull(poll.Snapshot);

            _ = engine.ObserveSharedQueue(
                SilentConsumerLabHarness.GiganewsQueue,
                poll,
                expectedActive: true);
            clock.Advance(SilentConsumerMonitorDefaults.ConsumerLoss.OpenWindow);
            poll = await management.GetQueueAsync("/", SilentConsumerLabHarness.GiganewsQueue, CancellationToken.None);
            var opened = engine
                .ObserveSharedQueue(SilentConsumerLabHarness.GiganewsQueue, poll, expectedActive: true)
                .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss);
            Assert.Equal(ObservationTransition.Opened, opened.Transition);
            Assert.Equal(ObservationScope.SharedQueue, opened.Key.Scope);
            Assert.Contains(logger.Entries, e => e.EventId.Id == 9110);

            await using var connections = await SilentConsumerLabHarness.StartLiveRabbitAsync(
                rabbitOptions,
                "VectorNNTPD.BackFillerService:silent-consumer-lab-e",
                CancellationToken.None);
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "lab-e",
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                Gate = NewSource(),
            };
            handler.Gate!.TrySetResult();
            await using var consumer = SilentConsumerLabHarness.CreateConsumer(connections, handler, clock);
            await consumer.StartAsync(CancellationToken.None);
            _ = await SilentConsumerLabHarness.WaitForRunningSessionAsync(consumer, TimeSpan.FromSeconds(60));
            var restored = await WaitForQueueAsync(
                stress,
                static s => s.Consumers >= 1 && s.Ready == 0 && s.Unacked == 0,
                TimeSpan.FromSeconds(60));
            Console.WriteLine(
                $"ScenarioE restored utc={DateTimeOffset.UtcNow:o} ready={restored.Ready} unacked={restored.Unacked} consumers={restored.Consumers}");

            poll = await management.GetQueueAsync("/", SilentConsumerLabHarness.GiganewsQueue, CancellationToken.None);
            var resolveStart = engine
                .ObserveSharedQueue(SilentConsumerLabHarness.GiganewsQueue, poll, expectedActive: true)
                .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss);
            Assert.Equal(ConditionSignal.Inactive, resolveStart.Signal);
            clock.Advance(SilentConsumerMonitorDefaults.ConsumerLoss.ResolveWindow);
            poll = await management.GetQueueAsync("/", SilentConsumerLabHarness.GiganewsQueue, CancellationToken.None);
            var resolved = engine
                .ObserveSharedQueue(SilentConsumerLabHarness.GiganewsQueue, poll, expectedActive: true)
                .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss);
            Assert.Equal(ObservationTransition.Resolved, resolved.Transition);
            Assert.Contains(logger.Entries, e => e.EventId.Id == 9112);

            var discarded = await SilentConsumerLabHarness.CleanupOwnedAsync(
                stress,
                runId,
                CancellationToken.None);
            Console.WriteLine($"ScenarioE cleanup discarded={discarded}");
            await consumer.StopAsync(CancellationToken.None);
        }

        /// <summary>
        /// Scenario D: clear Management BaseUrl on the options instance (process-local) → Event 9123; no shared Management poll.
        /// Does not take down the lab Management HTTP service.
        /// </summary>
        [Fact]
        public async Task ScenarioD_management_baseurl_cleared_emits_9123_without_shared_outage()
        {
            if (!SilentConsumerLabOptions.IsEnabled())
            {
                return;
            }

            var stress = SilentConsumerLabOptions.CreateStressBrokerOptions();
            var rabbitOptions = SilentConsumerLabOptions.CreateRabbitMqOptions(stress);
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-26T15:30:00Z"));
            var engineLogger = new CollectingLogger<SilentConsumerMonitorEngine>();
            var hostedLogger = new CollectingLogger<SilentConsumerMonitorHostedService>();
            var engine = new SilentConsumerMonitorEngine(() => "lab-bf-d.example", engineLogger, clock);

            await using var connections = await SilentConsumerLabHarness.StartLiveRabbitAsync(
                rabbitOptions,
                "VectorNNTPD.BackFillerService:silent-consumer-lab-d",
                CancellationToken.None);
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.ArticleNotFound,
                Error = "lab-d",
                Gate = NewSource(),
            };
            handler.Gate!.TrySetResult();
            await using var consumer = SilentConsumerLabHarness.CreateConsumer(connections, handler, clock);
            await consumer.StartAsync(CancellationToken.None);
            _ = await SilentConsumerLabHarness.WaitForRunningSessionAsync(consumer, TimeSpan.FromSeconds(60));

            using var http = SilentConsumerLabHarness.CreateHttpServices();
            var rabbitOpts = Options.Create(rabbitOptions);
            var management = new ManagementQueueClient(
                http.GetRequiredService<IHttpClientFactory>(),
                rabbitOpts,
                clock);
            var catalog = new StaticBackFillerProviderCatalog(
            [
                new BackFillerProviderDefinition(
                    SilentConsumerLabHarness.Backbone,
                    "127.0.0.1",
                    119,
                    false,
                    "u",
                    "p",
                    MinSessions: 0,
                    MaxSessions: 1),
            ]);
            var capacity = new BackboneUsableCapacityState();
            capacity.PublishSnapshot(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [SilentConsumerLabHarness.Backbone] = 1,
                });
            var runtime = SilentConsumerLabHarness.CreateMonitorRuntime();
            var hosted = new SilentConsumerMonitorHostedService(
                engine,
                management,
                consumer,
                capacity,
                catalog,
                Options.Create(new SilentConsumerMonitorOptions { Enabled = true }),
                rabbitOpts,
                runtime,
                hostedLogger,
                clock);

            // Isolated inject: clear BaseUrl on the shared options object (not broker Management).
            rabbitOptions.Management!.BaseUrl = string.Empty;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await hosted.StartAsync(cts.Token);
            try
            {
                await ArticleWorkTestDeliveries.WaitUntilAsync(
                    () => hostedLogger.Entries.Any(e => e.EventId.Id == 9123),
                    TimeSpan.FromSeconds(5));
            }
            finally
            {
                await hosted.StopAsync(CancellationToken.None);
            }

            Assert.Contains(hostedLogger.Entries, e => e.EventId.Id == 9123);
            Console.WriteLine($"ScenarioD 9123 utc={DateTimeOffset.UtcNow:o} detail={hostedLogger.Entries.First(e => e.EventId.Id == 9123).Message}");

            // Lab Management remains reachable for operators / NNTPD.
            rabbitOptions.Management.BaseUrl = $"http://{stress.RabbitMqHost}:15672";
            var sample = await SilentConsumerLabHarness.SampleQueueAsync(stress, CancellationToken.None);
            Assert.True(sample.Consumers >= 0);
            await consumer.StopAsync(CancellationToken.None);
        }

        /// <summary>Creates an unset TCS with async continuations.</summary>
        private static TaskCompletionSource NewSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Polls Management until <paramref name="predicate"/> holds or timeout.</summary>
        private static async Task<(long Ready, long Unacked, long Consumers)> WaitForQueueAsync(
            Stress.StressHarnessOptions stress,
            Func<(long Ready, long Unacked, long Consumers), bool> predicate,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            (long Ready, long Unacked, long Consumers) last = default;
            while (DateTime.UtcNow < deadline)
            {
                last = await SilentConsumerLabHarness.SampleQueueAsync(stress, CancellationToken.None);
                if (predicate(last))
                {
                    return last;
                }

                await Task.Delay(250);
            }

            throw new TimeoutException(
                $"Queue predicate not met (ready={last.Ready} unacked={last.Unacked} consumers={last.Consumers}).");
        }
    }
}
