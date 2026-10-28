using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Multi-instance / multi-session isolation, shared-queue fleet semantics, prune/reintroduction,
    /// and credential-projection helpers. Uses engine fakes (no live broker).
    /// </summary>
    public sealed class SilentConsumerMultiInstanceTests
    {
        /// <summary>
        /// Case A: one instance opens local processing stall while a sibling instance and shared aggregate ack progress stay healthy.
        /// </summary>
        [Fact]
        public void CaseA_local_processing_stall_opens_while_sibling_aggregate_ack_continues()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var stalled = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            var healthy = new SilentConsumerMonitorEngine(() => "b.example", NullLogger.Instance, time);

            _ = stalled.ObserveInstance([Session("Giganews", 1, stalled: true, queued: 0, active: 1)], desiredRunningSessions: 1);
            time.Advance(SilentConsumerMonitorDefaults.ProcessingStall.OpenWindow);
            var local = stalled.ObserveInstance([Session("Giganews", 1, stalled: true, queued: 0, active: 1)], 1)
                .Single(r => r.Key is { Scope: ObservationScope.Instance, Condition: SilentConsumerCondition.ProcessingStall });
            Assert.Equal(ObservationTransition.Opened, local.Transition);
            Assert.Equal(ArticleWorkConsumerSession.ComposeSessionKey("Giganews", 1), local.Key.QueueOrBackbone);
            Assert.Contains("sibling", local.Detail, StringComparison.OrdinalIgnoreCase);

            // Sibling sees healthy local state and aggregate ACK progress — must not open ProcessingStall.
            Assert.Equal(
                ConditionSignal.Inactive,
                healthy.ObserveInstance([Session("Giganews", 1, stalled: false, queued: 0, active: 1)], 1)
                    .Single(r => r.Key.Condition == SilentConsumerCondition.ProcessingStall)
                    .Signal);
            Assert.Equal(
                ConditionSignal.Inactive,
                healthy.ObserveSharedQueue("backfiller.giganews", Success(consumers: 2, ready: 0, unacked: 1, ackRate: 5), expectedActive: true)
                    .Single(r => r.Key.Condition == SilentConsumerCondition.ProcessingStall)
                    .Signal);

            // Aggregate progress must not clear the stalled instance's local observation.
            Assert.Equal(
                ObservationPhase.Open,
                stalled.ObserveInstance([Session("Giganews", 1, stalled: true, queued: 0, active: 1)], 1)
                    .Single(r => r.Key.Condition == SilentConsumerCondition.ProcessingStall)
                    .Phase);
        }

        /// <summary>
        /// Case B: a running session with zero queued and zero active work is not classified as dispatch stall.
        /// </summary>
        [Fact]
        public void CaseB_idle_instance_with_zero_queued_is_not_dispatch_stall()
        {
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, TimeProvider.System);
            var result = engine.ObserveInstance([Session("Giganews", 1, stalled: false, queued: 0, active: 0)], desiredRunningSessions: 1)
                .Single(r => r.Key.Condition == SilentConsumerCondition.DispatchStall);
            Assert.Equal(ConditionSignal.Inactive, result.Signal);
            Assert.Contains("idle", result.Detail, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Case C: queued work before active count opens dispatch stall after the open window.
        /// </summary>
        [Fact]
        public void CaseC_dispatch_stall_before_active_count_increments()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            _ = engine.ObserveInstance([Session("Giganews", 1, stalled: false, queued: 2, active: 0)], 1);
            time.Advance(SilentConsumerMonitorDefaults.DispatchStall.OpenWindow);
            var opened = engine.ObserveInstance([Session("Giganews", 1, stalled: false, queued: 2, active: 0)], 1)
                .Single(r => r.Key.Condition == SilentConsumerCondition.DispatchStall);
            Assert.Equal(ObservationTransition.Opened, opened.Transition);
            Assert.Contains("DispatchAcquireHold", opened.Detail, StringComparison.Ordinal);
        }

        /// <summary>
        /// Case D: zero consumers with ready backlog opens shared consumer loss when the queue is expected active.
        /// </summary>
        [Fact]
        public void CaseD_shared_consumer_loss_when_all_consumers_gone_with_backlog()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            _ = engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), expectedActive: true);
            time.Advance(SilentConsumerMonitorDefaults.ConsumerLoss.OpenWindow);
            Assert.Equal(
                ObservationTransition.Opened,
                engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), expectedActive: true)
                    .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss)
                    .Transition);
        }

        /// <summary>
        /// Case D variant: one remaining consumer with backlog is inactive fleet consumer loss.
        /// </summary>
        [Fact]
        public void CaseD_one_remaining_consumer_is_not_fleet_consumer_loss()
        {
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, TimeProvider.System);
            var shared = engine.ObserveSharedQueue("backfiller.giganews", Success(1, 5, 0, 0), expectedActive: true)
                .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss);
            Assert.Equal(ConditionSignal.Inactive, shared.Signal);
            Assert.Contains("not fleet-wide", shared.Detail, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Case E: local consumer loss activates with desired sessions but no running observations; healthy shared metrics stay inactive.
        /// </summary>
        [Fact]
        public void CaseE_local_consumer_loss_independent_of_aggregate_rates()
        {
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, TimeProvider.System);
            var local = engine.ObserveInstance([], desiredRunningSessions: 2)
                .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss);
            Assert.Equal(ObservationScope.Instance, local.Key.Scope);
            Assert.Equal(ConditionSignal.Active, local.Signal);

            var shared = engine.ObserveSharedQueue("backfiller.giganews", Success(2, 0, 0, ackRate: 10), expectedActive: true)
                .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss);
            Assert.Equal(ConditionSignal.Inactive, shared.Signal);
        }

        /// <summary>
        /// Case F: two monitor engines on distinct instance ids both open the same shared consumer-loss condition independently.
        /// </summary>
        [Fact]
        public void CaseF_two_monitors_duplicate_shared_observation_without_attributing_to_local_consumer()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var a = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            var b = new SilentConsumerMonitorEngine(() => "b.example", NullLogger.Instance, time);
            _ = a.ObserveSharedQueue("backfiller.giganews", Success(0, 4, 0, 0), true);
            _ = b.ObserveSharedQueue("backfiller.giganews", Success(0, 4, 0, 0), true);
            time.Advance(SilentConsumerMonitorDefaults.ConsumerLoss.OpenWindow);
            var aOpen = a.ObserveSharedQueue("backfiller.giganews", Success(0, 4, 0, 0), true)
                .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss);
            var bOpen = b.ObserveSharedQueue("backfiller.giganews", Success(0, 4, 0, 0), true)
                .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss);
            Assert.Equal(ObservationTransition.Opened, aOpen.Transition);
            Assert.Equal(ObservationTransition.Opened, bOpen.Transition);
            Assert.Equal(ObservationScope.SharedQueue, aOpen.Key.Scope);
            Assert.NotEqual(a.InstanceId, b.InstanceId);
        }

        /// <summary>
        /// A healthy sibling session on the same backbone does not suppress or resolve another session's processing stall.
        /// </summary>
        [Fact]
        public void Healthy_sibling_session_same_backbone_does_not_suppress_processing_stall()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            var key1 = ArticleWorkConsumerSession.ComposeSessionKey("Giganews", 1);
            var key2 = ArticleWorkConsumerSession.ComposeSessionKey("Giganews", 2);
            var sessions = new[]
            {
                Session("Giganews", 1, stalled: true, queued: 0, active: 1),
                Session("Giganews", 2, stalled: false, queued: 0, active: 1),
            };

            _ = engine.ObserveInstance(sessions, desiredRunningSessions: 2);
            time.Advance(SilentConsumerMonitorDefaults.ProcessingStall.OpenWindow);
            var results = engine.ObserveInstance(sessions, 2);

            var stalled = results.Single(r =>
                r.Key.Condition == SilentConsumerCondition.ProcessingStall
                && r.Key.QueueOrBackbone == key1);
            var healthy = results.Single(r =>
                r.Key.Condition == SilentConsumerCondition.ProcessingStall
                && r.Key.QueueOrBackbone == key2);

            Assert.Equal(ObservationTransition.Opened, stalled.Transition);
            Assert.Equal(ObservationPhase.Open, stalled.Phase);
            Assert.Equal(ConditionSignal.Inactive, healthy.Signal);
            Assert.Equal(ObservationPhase.Clear, healthy.Phase);

            // Same-cycle healthy sibling must not begin resolving the stalled session.
            time.Advance(SilentConsumerMonitorDefaults.ProcessingStall.ResolveWindow);
            var continued = engine.ObserveInstance(sessions, 2)
                .Single(r =>
                    r.Key.Condition == SilentConsumerCondition.ProcessingStall
                    && r.Key.QueueOrBackbone == key1);
            Assert.Equal(ObservationPhase.Open, continued.Phase);
            Assert.Equal(ObservationTransition.Continued, continued.Transition);
        }

        /// <summary>
        /// A healthy idle sibling does not clear dispatch stall on a blocked session on the same backbone.
        /// </summary>
        [Fact]
        public void Healthy_sibling_session_same_backbone_does_not_suppress_dispatch_stall()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            var key1 = ArticleWorkConsumerSession.ComposeSessionKey("Giganews", 1);
            var key2 = ArticleWorkConsumerSession.ComposeSessionKey("Giganews", 2);
            var sessions = new[]
            {
                Session("Giganews", 1, stalled: false, queued: 2, active: 0),
                Session("Giganews", 2, stalled: false, queued: 0, active: 0),
            };

            _ = engine.ObserveInstance(sessions, 2);
            time.Advance(SilentConsumerMonitorDefaults.DispatchStall.OpenWindow);
            var results = engine.ObserveInstance(sessions, 2);

            Assert.Equal(
                ObservationTransition.Opened,
                results.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.DispatchStall
                    && r.Key.QueueOrBackbone == key1).Transition);
            Assert.Equal(
                ConditionSignal.Inactive,
                results.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.DispatchStall
                    && r.Key.QueueOrBackbone == key2).Signal);

            time.Advance(SilentConsumerMonitorDefaults.DispatchStall.ResolveWindow);
            Assert.Equal(
                ObservationPhase.Open,
                engine.ObserveInstance(sessions, 2)
                    .Single(r =>
                        r.Key.Condition == SilentConsumerCondition.DispatchStall
                        && r.Key.QueueOrBackbone == key1)
                    .Phase);
        }

        /// <summary>
        /// When one of two stalled sessions recovers, only that session's processing stall resolves after the resolve window.
        /// </summary>
        [Fact]
        public void Independent_recovery_resolves_only_recovered_session()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            var key1 = ArticleWorkConsumerSession.ComposeSessionKey("Giganews", 1);
            var key2 = ArticleWorkConsumerSession.ComposeSessionKey("Giganews", 2);

            var bothStalled = new[]
            {
                Session("Giganews", 1, stalled: true, queued: 0, active: 1),
                Session("Giganews", 2, stalled: true, queued: 0, active: 1),
            };
            _ = engine.ObserveInstance(bothStalled, 2);
            time.Advance(SilentConsumerMonitorDefaults.ProcessingStall.OpenWindow);
            var opened = engine.ObserveInstance(bothStalled, 2);
            Assert.Equal(
                ObservationTransition.Opened,
                opened.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.ProcessingStall
                    && r.Key.QueueOrBackbone == key1).Transition);
            Assert.Equal(
                ObservationTransition.Opened,
                opened.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.ProcessingStall
                    && r.Key.QueueOrBackbone == key2).Transition);

            // Session 1 recovers; session 2 remains stalled.
            var mixed = new[]
            {
                Session("Giganews", 1, stalled: false, queued: 0, active: 1),
                Session("Giganews", 2, stalled: true, queued: 0, active: 1),
            };
            _ = engine.ObserveInstance(mixed, 2);
            time.Advance(SilentConsumerMonitorDefaults.ProcessingStall.ResolveWindow);
            var after = engine.ObserveInstance(mixed, 2);

            var recovered = after.Single(r =>
                r.Key.Condition == SilentConsumerCondition.ProcessingStall
                && r.Key.QueueOrBackbone == key1);
            var stillOpen = after.Single(r =>
                r.Key.Condition == SilentConsumerCondition.ProcessingStall
                && r.Key.QueueOrBackbone == key2);

            Assert.Equal(ObservationTransition.Resolved, recovered.Transition);
            Assert.Equal(ObservationPhase.Clear, recovered.Phase);
            Assert.Equal(ObservationPhase.Open, stillOpen.Phase);
            Assert.Equal(ObservationTransition.Continued, stillOpen.Transition);
        }

        /// <summary>
        /// Removing a session from observations inactivates its stall and resolves it after the window while the remaining session stays open.
        /// </summary>
        [Fact]
        public void Session_removal_prunes_only_removed_session_identity()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            var key1 = ArticleWorkConsumerSession.ComposeSessionKey("Giganews", 1);
            var key2 = ArticleWorkConsumerSession.ComposeSessionKey("Giganews", 2);

            var both = new[]
            {
                Session("Giganews", 1, stalled: true, queued: 0, active: 1),
                Session("Giganews", 2, stalled: true, queued: 0, active: 1),
            };
            _ = engine.ObserveInstance(both, 2);
            time.Advance(SilentConsumerMonitorDefaults.ProcessingStall.OpenWindow);
            _ = engine.ObserveInstance(both, 2);

            // Session 1 disappears from the desired set; session 2 remains stalled.
            var remaining = new[] { Session("Giganews", 2, stalled: true, queued: 0, active: 1) };
            var afterRemoval = engine.ObserveInstance(remaining, 1);
            var removed = afterRemoval.Single(r =>
                r.Key.Condition == SilentConsumerCondition.ProcessingStall
                && r.Key.QueueOrBackbone == key1);
            var kept = afterRemoval.Single(r =>
                r.Key.Condition == SilentConsumerCondition.ProcessingStall
                && r.Key.QueueOrBackbone == key2);

            Assert.Equal(ConditionSignal.Inactive, removed.Signal);
            Assert.Contains("session removed", removed.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ObservationPhase.Open, kept.Phase);
            Assert.Equal(ConditionSignal.Active, kept.Signal);

            time.Advance(SilentConsumerMonitorDefaults.ProcessingStall.ResolveWindow);
            var afterResolve = engine.ObserveInstance(remaining, 1);
            Assert.Equal(
                ObservationTransition.Resolved,
                afterResolve.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.ProcessingStall
                    && r.Key.QueueOrBackbone == key1).Transition);
            Assert.Equal(
                ObservationPhase.Open,
                afterResolve.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.ProcessingStall
                    && r.Key.QueueOrBackbone == key2).Phase);

            // Resolved session-removed machines are dropped; a later cycle must not re-emit that identity.
            var afterPrune = engine.ObserveInstance(remaining, 1);
            Assert.DoesNotContain(
                afterPrune,
                r => r.Key.Condition == SilentConsumerCondition.ProcessingStall
                     && r.Key.QueueOrBackbone == key1);
            Assert.DoesNotContain(
                afterPrune,
                r => r.Key.Condition == SilentConsumerCondition.DispatchStall
                     && r.Key.QueueOrBackbone == key1);
            Assert.Contains(
                afterPrune,
                r => r.Key.Condition == SilentConsumerCondition.ProcessingStall
                     && r.Key.QueueOrBackbone == key2);
        }

        /// <summary>
        /// A never-opened session machine is dropped immediately on session removal (Clear + Inactive).
        /// </summary>
        [Fact]
        public void Session_removal_drops_clear_machines_without_waiting_for_resolve_window()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T21:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            var key1 = ArticleWorkConsumerSession.ComposeSessionKey("Eweka", 1);

            var both = new[]
            {
                Session("Eweka", 1, stalled: false, queued: 0, active: 0),
                Session("Eweka", 2, stalled: false, queued: 0, active: 0),
            };
            _ = engine.ObserveInstance(both, 2);

            var remaining = new[] { Session("Eweka", 2, stalled: false, queued: 0, active: 0) };
            var afterRemoval = engine.ObserveInstance(remaining, 1);
            Assert.Contains(
                afterRemoval,
                r => r.Key.QueueOrBackbone == key1
                     && (r.Detail?.Contains("session removed", StringComparison.OrdinalIgnoreCase) ?? false));

            var afterPrune = engine.ObserveInstance(remaining, 1);
            Assert.DoesNotContain(afterPrune, r => r.Key.QueueOrBackbone == key1);
        }

        /// <summary>
        /// After <see cref="SilentConsumerMonitorEngine.PruneQueues"/>, re-observing a queue gets fresh state (None then Opened, not Continued).
        /// </summary>
        [Fact]
        public void Provider_reintroduction_recreates_shared_observation_state()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);

            _ = engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), expectedActive: true);
            time.Advance(SilentConsumerMonitorDefaults.ConsumerLoss.OpenWindow);
            Assert.Equal(
                ObservationTransition.Opened,
                engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), true)
                    .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss)
                    .Transition);

            var pruned = engine.PruneQueues([]);
            Assert.Contains(
                pruned,
                r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss
                     && r.Transition == ObservationTransition.Resolved
                     && r.Detail is not null
                     && r.Detail.Contains("removed from catalog", StringComparison.OrdinalIgnoreCase));

            // Reintroduce: history/machines must be fresh — first Active tick is None, then Opened (not Continued).
            Assert.Equal(
                ObservationTransition.None,
                engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), true)
                    .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss)
                    .Transition);
            time.Advance(SilentConsumerMonitorDefaults.ConsumerLoss.OpenWindow);
            var reopened = engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), true)
                .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss);
            Assert.Equal(ObservationTransition.Opened, reopened.Transition);
            Assert.Equal(ObservationPhase.Open, reopened.Phase);
            Assert.Equal(ObservationScope.SharedQueue, reopened.Key.Scope);
        }

        /// <summary>
        /// A healthy session on one backbone does not suppress processing stall on another backbone on the same monitor instance.
        /// </summary>
        [Fact]
        public void Healthy_backbone_does_not_clear_stalled_backbone_on_same_instance()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            var sessions = new[]
            {
                Session("Giganews", 1, stalled: true, queued: 0, active: 1),
                Session("Eweka", 1, stalled: false, queued: 0, active: 1),
            };
            _ = engine.ObserveInstance(sessions, 2);
            time.Advance(SilentConsumerMonitorDefaults.ProcessingStall.OpenWindow);
            var results = engine.ObserveInstance(sessions, 2);
            Assert.Equal(
                ObservationTransition.Opened,
                results.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.ProcessingStall
                    && r.Key.QueueOrBackbone == ArticleWorkConsumerSession.ComposeSessionKey("Giganews", 1)).Transition);
            Assert.Equal(
                ConditionSignal.Inactive,
                results.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.ProcessingStall
                    && r.Key.QueueOrBackbone == ArticleWorkConsumerSession.ComposeSessionKey("Eweka", 1)).Signal);
        }

        /// <summary>
        /// Shared consumer loss is inactive when <c>expectedActive</c> is false even with zero consumers and backlog.
        /// </summary>
        [Fact]
        public void No_expected_capacity_suppresses_shared_consumer_loss()
        {
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, TimeProvider.System);
            Assert.Equal(
                ConditionSignal.Inactive,
                engine.ObserveSharedQueue("backfiller.giganews", Success(0, 9, 0, 0), expectedActive: false)
                    .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss)
                    .Signal);
        }

        /// <summary>
        /// Pruning all queues resolves open shared consumer loss with catalog-removal detail.
        /// </summary>
        [Fact]
        public void Provider_removal_drives_shared_observation_toward_resolve()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            _ = engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), true);
            time.Advance(SilentConsumerMonitorDefaults.ConsumerLoss.OpenWindow);
            Assert.Equal(
                ObservationTransition.Opened,
                engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), true)
                    .Single(r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss)
                    .Transition);

            var pruned = engine.PruneQueues([]);
            Assert.Contains(
                pruned,
                r => r.Key.Condition == SilentConsumerCondition.ConsumerLoss
                     && r.Transition == ObservationTransition.Resolved
                     && r.Detail is not null
                     && r.Detail.Contains("removed from catalog", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// <see cref="NntpSharedConfiguration.CopyRabbitMqCredentialsTo"/> mutates live <see cref="RabbitMqOptions"/> without changing management URL.
        /// </summary>
        [Fact]
        public void Shared_config_projects_credentials_onto_live_rabbit_options_used_by_management_client()
        {
            var rabbit = new RabbitMqOptions
            {
                Username = null,
                Password = null,
                Management = new RabbitMqManagementOptions { BaseUrl = "http://198.18.0.3:15672" },
            };
            var shared = new NntpSharedConfiguration(
                MaxArticleBytes: 1,
                SiteName: "site",
                PrometheusUrl: "http://p",
                AcmeDirectoryUrl: "https://acme",
                AcmeRenewalThresholdDays: 30,
                CloudFlareZoneId: "zone",
                DnsSuffix: "example.net",
                AcmeAccount: "a@example.net",
                AcmeCertificatePassword: "x",
                CloudFlareApiKey: "k",
                RabbitMqPassword: "secret-pass",
                RabbitMqUsername: "nntparticles");
            shared.CopyRabbitMqCredentialsTo(rabbit);
            Assert.Equal("nntparticles", rabbit.Username);
            Assert.Equal("secret-pass", rabbit.Password);
            Assert.Equal("http://198.18.0.3:15672", rabbit.Management.BaseUrl);
        }

        /// <summary>
        /// Resolved monitor targets use the same queue naming convention as provider entity composition.
        /// </summary>
        [Fact]
        public void Catalog_targets_match_compose_provider_entity_queue_names()
        {
            var catalog = new ProviderConfigurationCatalog();
            catalog.Publish(
            [
                new BackFillerProviderDefinition("Giganews", "h", 563, true, "u", "p", 0, 2),
            ]);
            var capacity = new BackboneUsableCapacityState();
            capacity.PublishSnapshot(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Giganews"] = 2 });
            var target = Assert.Single(SilentConsumerMonitorTargets.Resolve(catalog, capacity));
            Assert.Equal("backfiller.giganews", target.QueueName);
            Assert.True(target.ExpectedActive);
        }

        /// <summary>Synthetic session observation for multi-instance engine scenarios.</summary>
        /// <param name="backbone">Provider backbone.</param>
        /// <param name="connectionNumber">Connection index within the backbone.</param>
        /// <param name="stalled">Active processing stall flag.</param>
        /// <param name="queued">Queued-before-active count.</param>
        /// <param name="active">Active in-flight count.</param>
        /// <returns>Running observation with open channel.</returns>
        private static SilentConsumerSessionObservation Session(
            string backbone,
            int connectionNumber,
            bool stalled,
            int queued,
            int active) =>
            new(
                backbone,
                connectionNumber,
                ArticleWorkConsumerState.Running,
                queued,
                active,
                HasOpenChannel: true,
                IsActiveProcessingStalled: stalled);

        /// <summary>Successful shared-queue poll with synthetic snapshot for fleet semantics tests.</summary>
        /// <param name="consumers">Management consumer gauge.</param>
        /// <param name="ready">Messages ready.</param>
        /// <param name="unacked">Unacknowledged messages.</param>
        /// <param name="ackRate">Ack rate from management API.</param>
        /// <returns><see cref="ManagementPollKind.Success"/> poll result.</returns>
        private static ManagementPollResult Success(long consumers, long ready, long unacked, double ackRate) =>
            new()
            {
                Kind = ManagementPollKind.Success,
                Reason = "ok",
                Snapshot = new ManagementQueueSnapshot
                {
                    QueueName = "q",
                    VirtualHost = "/",
                    SampledAtUtc = DateTimeOffset.Parse("2026-10-25T20:00:00Z"),
                    Consumers = consumers,
                    MessagesReady = ready,
                    MessagesUnacknowledged = unacked,
                    AckRate = ackRate,
                    DeliverGetRate = 0,
                },
            };
    }
}
