using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Predicate-level <see cref="SilentConsumerEvaluators"/> contracts plus <see cref="SilentConsumerMonitorEngine"/>
    /// open/resolve timing, Unknown freeze while shared alerts stay open, and event 9113 observation-unavailable rate limiting.
    /// </summary>
    public sealed class SilentConsumerEvaluatorAndEngineTests
    {
        /// <summary>
        /// Shared processing stall is inactive when sample history shows positive ack rate with consumers and unacked work present.
        /// </summary>
        [Fact]
        public void Shared_processing_stall_healthy_ack_is_inactive()
        {
            var history = History(Snap(consumers: 1, ready: 0, unacked: 1, ackRate: 1.0));
            Assert.Equal(
                ConditionSignal.Inactive,
                SilentConsumerEvaluators.EvaluateSharedProcessingStall(
                    history,
                    SilentConsumerMonitorDefaults.ProcessingStall,
                    out _));
        }

        /// <summary>
        /// Zero ack rate with consumers and unacked messages yields an active shared processing-stall signal.
        /// </summary>
        [Fact]
        public void Shared_processing_stall_no_progress_is_active()
        {
            var history = History(Snap(consumers: 1, ready: 0, unacked: 1, ackRate: 0.0));
            Assert.Equal(
                ConditionSignal.Active,
                SilentConsumerEvaluators.EvaluateSharedProcessingStall(
                    history,
                    SilentConsumerMonitorDefaults.ProcessingStall,
                    out _));
        }

        /// <summary>
        /// Instance processing stall follows the session stall flag only; healthy active work is inactive and detail references the stall threshold.
        /// </summary>
        [Fact]
        public void Instance_processing_stall_uses_existing_stall_predicate_only()
        {
            Assert.Equal(
                ConditionSignal.Active,
                SilentConsumerEvaluators.EvaluateInstanceProcessingStall(
                    Running("giganews", stalled: true, queued: 0, active: 1),
                    out var detail));
            Assert.Contains("5328", detail, StringComparison.Ordinal);
            Assert.Equal(
                ConditionSignal.Inactive,
                SilentConsumerEvaluators.EvaluateInstanceProcessingStall(
                    Running("giganews", stalled: false, queued: 0, active: 1),
                    out _));
        }

        /// <summary>
        /// Dispatch stall is active when queued work exists before active processing; idle running sessions are inactive.
        /// </summary>
        [Fact]
        public void Instance_dispatch_stall_detects_queued_before_active()
        {
            Assert.Equal(
                ConditionSignal.Active,
                SilentConsumerEvaluators.EvaluateInstanceDispatchStall(
                    Running("giganews", stalled: false, queued: 2, active: 0),
                    out _));
            Assert.Equal(
                ConditionSignal.Inactive,
                SilentConsumerEvaluators.EvaluateInstanceDispatchStall(
                    Running("giganews", stalled: false, queued: 0, active: 0),
                    out var idleDetail));
            Assert.Contains("idle", idleDetail, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Engine opens dispatch stall per session key after the dispatch open window with queued-before-active observation.
        /// </summary>
        [Fact]
        public void Engine_opens_per_session_dispatch_stall()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            _ = engine.ObserveInstance([Running("Giganews", stalled: false, queued: 3, active: 0, connectionNumber: 1)], 1);
            time.Advance(SilentConsumerMonitorDefaults.DispatchStall.OpenWindow);
            var opened = engine.ObserveInstance([Running("Giganews", stalled: false, queued: 3, active: 0, connectionNumber: 1)], 1)
                .Single(r => r.Key.Condition == SilentConsumerCondition.DispatchStall);
            Assert.Equal(ObservationTransition.Opened, opened.Transition);
            Assert.Equal(
                ArticleWorkConsumerSession.ComposeSessionKey("Giganews", 1),
                opened.Key.QueueOrBackbone);
        }

        /// <summary>
        /// Shared processing stall opens once after the open window, continues while active, and resolves after recovery and the resolve window.
        /// </summary>
        [Fact]
        public void Engine_opens_shared_processing_stall_once_and_resolves_on_recovery()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);

            ObservationResult LastStall(IReadOnlyList<ObservationResult> results) =>
                results.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.ProcessingStall
                    && r.Key.Scope == ObservationScope.SharedQueue);

            Assert.Equal(
                ObservationTransition.None,
                LastStall(engine.ObserveSharedQueue("backfiller.giganews", Success(1, 0, 1, 0), true)).Transition);
            time.Advance(SilentConsumerMonitorDefaults.ProcessingStall.OpenWindow);
            Assert.Equal(
                ObservationTransition.Opened,
                LastStall(engine.ObserveSharedQueue("backfiller.giganews", Success(1, 0, 1, 0), true)).Transition);
            Assert.Equal(
                ObservationTransition.Continued,
                LastStall(engine.ObserveSharedQueue("backfiller.giganews", Success(1, 0, 0, 1), true)).Transition);
            time.Advance(SilentConsumerMonitorDefaults.ProcessingStall.ResolveWindow);
            Assert.Equal(
                ObservationTransition.Resolved,
                LastStall(engine.ObserveSharedQueue("backfiller.giganews", Success(1, 0, 0, 1), true)).Transition);
        }

        /// <summary>
        /// Open shared consumer-loss stays frozen on Unknown polls; local dispatch stall can still open; resumed valid samples continue then resolve normally.
        /// </summary>
        [Fact]
        public void Open_shared_alert_stays_open_on_unknown_then_reevaluates_when_management_resumes()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);

            ObservationResult LastConsumerLoss(IReadOnlyList<ObservationResult> results) =>
                results.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.ConsumerLoss
                    && r.Key.Scope == ObservationScope.SharedQueue);

            _ = engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), expectedActive: true);
            time.Advance(SilentConsumerMonitorDefaults.ConsumerLoss.OpenWindow);
            Assert.Equal(
                ObservationTransition.Opened,
                LastConsumerLoss(engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), true)).Transition);

            var unknownPoll = new ManagementPollResult
            {
                Kind = ManagementPollKind.TransientFailure,
                Reason = "timeout",
            };
            var duringOutage = LastConsumerLoss(engine.ObserveSharedQueue("backfiller.giganews", unknownPoll, true));
            Assert.Equal(ConditionSignal.Unknown, duringOutage.Signal);
            Assert.Equal(ObservationTransition.None, duringOutage.Transition);
            Assert.Equal(ObservationPhase.Open, duringOutage.Phase);

            // Local monitoring must still be able to open while shared stays frozen Open.
            _ = engine.ObserveInstance(
                [
                    new SilentConsumerSessionObservation(
                        "Giganews",
                        1,
                        ArticleWorkConsumerState.Running,
                        QueuedCount: 2,
                        ActiveCount: 0,
                        HasOpenChannel: true,
                        IsActiveProcessingStalled: false),
                ],
                desiredRunningSessions: 1);
            time.Advance(SilentConsumerMonitorDefaults.DispatchStall.OpenWindow);
            Assert.Equal(
                ObservationTransition.Opened,
                engine.ObserveInstance(
                    [
                        new SilentConsumerSessionObservation(
                            "Giganews",
                            1,
                            ArticleWorkConsumerState.Running,
                            QueuedCount: 2,
                            ActiveCount: 0,
                            HasOpenChannel: true,
                            IsActiveProcessingStalled: false),
                    ],
                    1)
                    .Single(r => r.Key.Condition == SilentConsumerCondition.DispatchStall)
                    .Transition);
            Assert.Equal(
                ObservationPhase.Open,
                LastConsumerLoss(engine.ObserveSharedQueue("backfiller.giganews", unknownPoll, true)).Phase);

            // Valid samples resume: still Active → Continued (not Resolved by the outage).
            Assert.Equal(
                ObservationTransition.Continued,
                LastConsumerLoss(engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), true)).Transition);
            Assert.Equal(ObservationPhase.Open, LastConsumerLoss(
                engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), true)).Phase);

            // Conclusive clear after resume uses the normal resolve window — not Unknown-as-recovery.
            _ = engine.ObserveSharedQueue("backfiller.giganews", Success(1, 5, 0, 0), true);
            time.Advance(SilentConsumerMonitorDefaults.ConsumerLoss.ResolveWindow);
            var resolved = LastConsumerLoss(engine.ObserveSharedQueue("backfiller.giganews", Success(1, 5, 0, 0), true));
            Assert.Equal(ObservationTransition.Resolved, resolved.Transition);
            Assert.DoesNotContain("removed from catalog", resolved.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Failed Management polls (Unknown) during Clear-phase accumulation must not complete the shared open window.
        /// </summary>
        [Fact]
        public void Unknown_management_gap_does_not_complete_shared_open_window()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-26T15:00:00Z"));
            var engine = new SilentConsumerMonitorEngine(() => "a.example", NullLogger.Instance, time);
            var unknownPoll = new ManagementPollResult
            {
                Kind = ManagementPollKind.TransientFailure,
                Reason = "timeout",
            };

            ObservationResult LastLoss(IReadOnlyList<ObservationResult> results) =>
                results.Single(r =>
                    r.Key.Condition == SilentConsumerCondition.ConsumerLoss
                    && r.Key.Scope == ObservationScope.SharedQueue);

            Assert.Equal(
                ObservationTransition.None,
                LastLoss(engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), true)).Transition);

            time.Advance(TimeSpan.FromSeconds(60));
            Assert.Equal(
                ObservationTransition.None,
                LastLoss(engine.ObserveSharedQueue("backfiller.giganews", unknownPoll, true)).Transition);
            Assert.Equal(ObservationPhase.Clear, LastLoss(
                engine.ObserveSharedQueue("backfiller.giganews", unknownPoll, true)).Phase);

            // Wall time since first Active would meet ConsumerLoss open (45s); freeze keeps Clear.
            time.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(
                ObservationTransition.None,
                LastLoss(engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), true)).Transition);

            time.Advance(SilentConsumerMonitorDefaults.ConsumerLoss.OpenWindow);
            Assert.Equal(
                ObservationTransition.Opened,
                LastLoss(engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), true)).Transition);
        }

        /// <summary>
        /// Event 9113 logs once per observation key per open-window interval; distinct queue keys and advanced time emit additional entries.
        /// </summary>
        [Fact]
        public void Observation_unavailable_while_open_is_rate_limited_per_observation_key()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T20:00:00Z"));
            var logger = new CollectingLogger<SilentConsumerMonitorEngine>();
            var engine = new SilentConsumerMonitorEngine(() => "a.example", logger, time);
            var unknownPoll = new ManagementPollResult
            {
                Kind = ManagementPollKind.TransientFailure,
                Reason = "timeout",
            };

            _ = engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), expectedActive: true);
            time.Advance(SilentConsumerMonitorDefaults.ConsumerLoss.OpenWindow);
            _ = engine.ObserveSharedQueue("backfiller.giganews", Success(0, 5, 0, 0), true);

            _ = engine.ObserveSharedQueue("backfiller.giganews", unknownPoll, true);
            Assert.Equal(1, logger.Entries.Count(e => e.EventId.Id == 9113));

            // Same observation key within the open-window interval: suppressed.
            _ = engine.ObserveSharedQueue("backfiller.giganews", unknownPoll, true);
            Assert.Equal(1, logger.Entries.Count(e => e.EventId.Id == 9113));

            time.Advance(SilentConsumerMonitorDefaults.ProcessingStall.OpenWindow);
            _ = engine.ObserveSharedQueue("backfiller.giganews", unknownPoll, true);
            Assert.Equal(2, logger.Entries.Count(e => e.EventId.Id == 9113));

            // Distinct queue key is not suppressed by the first queue's limiter.
            _ = engine.ObserveSharedQueue("backfiller.eweka", Success(0, 5, 0, 0), true);
            time.Advance(SilentConsumerMonitorDefaults.ConsumerLoss.OpenWindow);
            _ = engine.ObserveSharedQueue("backfiller.eweka", Success(0, 5, 0, 0), true);
            _ = engine.ObserveSharedQueue("backfiller.eweka", unknownPoll, true);
            Assert.Contains(
                logger.Entries,
                e => e.EventId.Id == 9113 && e.Message.Contains("backfiller.eweka", StringComparison.Ordinal));
        }

        /// <summary>
        /// Invalid sample history yields <see cref="ConditionSignal.Unknown"/> for shared processing stall evaluation.
        /// </summary>
        [Fact]
        public void Missing_metrics_are_unknown()
        {
            var invalid = new List<QueueSampleEntry>
            {
                new(
                    DateTimeOffset.UtcNow,
                    IsValid: false,
                    Snapshot: null,
                    ManagementPollKind.TransientFailure,
                    "timeout"),
            };
            Assert.Equal(
                ConditionSignal.Unknown,
                SilentConsumerEvaluators.EvaluateSharedProcessingStall(
                    invalid,
                    SilentConsumerMonitorDefaults.ProcessingStall,
                    out _));
        }

        /// <summary>Synthetic running session observation for evaluator and engine tests.</summary>
        /// <param name="backbone">Provider backbone name.</param>
        /// <param name="stalled">Whether active processing exceeds the stall threshold.</param>
        /// <param name="queued">Queued delivery count before active processing.</param>
        /// <param name="active">In-flight active article count.</param>
        /// <param name="connectionNumber">Connection slot within the backbone.</param>
        /// <returns>Observation with an open channel and running state.</returns>
        private static SilentConsumerSessionObservation Running(
            string backbone,
            bool stalled,
            int queued,
            int active,
            int connectionNumber = 1) =>
            new(
                backbone,
                connectionNumber,
                ArticleWorkConsumerState.Running,
                queued,
                active,
                HasOpenChannel: true,
                IsActiveProcessingStalled: stalled);

        /// <summary>Successful management poll wrapping a synthetic snapshot.</summary>
        /// <param name="consumers">Consumer count gauge.</param>
        /// <param name="ready">Messages ready gauge.</param>
        /// <param name="unacked">Unacknowledged message gauge.</param>
        /// <param name="ackRate">Instant ack rate from management API.</param>
        /// <returns><see cref="ManagementPollKind.Success"/> result for engine shared-queue tests.</returns>
        private static ManagementPollResult Success(long consumers, long ready, long unacked, double ackRate) =>
            new()
            {
                Kind = ManagementPollKind.Success,
                Reason = "ok",
                Snapshot = Snap(consumers, ready, unacked, ackRate),
            };

        /// <summary>Single-entry history list for shared evaluator tests.</summary>
        /// <param name="snap">Valid snapshot to wrap.</param>
        /// <returns>One valid <see cref="QueueSampleEntry"/> at the snapshot timestamp.</returns>
        private static IReadOnlyList<QueueSampleEntry> History(ManagementQueueSnapshot snap) =>
        [
            new(snap.SampledAtUtc, true, snap, null, null),
        ];

        /// <summary>Minimal queue snapshot for evaluator inputs.</summary>
        /// <param name="consumers">Consumer count.</param>
        /// <param name="ready">Messages ready.</param>
        /// <param name="unacked">Unacknowledged messages.</param>
        /// <param name="ackRate">Optional ack rate.</param>
        /// <returns>Snapshot with fixed queue name and sample time.</returns>
        private static ManagementQueueSnapshot Snap(
            long consumers,
            long ready,
            long unacked,
            double? ackRate) =>
            new()
            {
                QueueName = "q",
                VirtualHost = "/",
                SampledAtUtc = DateTimeOffset.Parse("2026-10-25T20:00:00Z"),
                Consumers = consumers,
                MessagesReady = ready,
                MessagesUnacknowledged = unacked,
                AckRate = ackRate,
                DeliverGetRate = 0,
            };
    }
}
