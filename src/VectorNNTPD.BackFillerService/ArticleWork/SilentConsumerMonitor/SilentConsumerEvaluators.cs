namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Condition predicates. Instance ProcessingStall defers remediation to existing stall recovery;
    /// shared metrics never claim a specific consumer identity and must not mask a local stall.
    /// </summary>
    internal static class SilentConsumerEvaluators
    {
        /// <summary>
        /// Instance ProcessingStall for one session: active pipeline stalled past threshold
        /// (same predicate as reconcile). Observation only — does not call <c>RetireForRecoveryAsync</c>.
        /// </summary>
        /// <param name="session">Projected session for this tick.</param>
        /// <param name="detail">Non-secret diagnostic text for logs/tests.</param>
        /// <returns><see cref="ConditionSignal.Active"/> when Running and stalled; otherwise Inactive.</returns>
        public static ConditionSignal EvaluateInstanceProcessingStall(
            SilentConsumerSessionObservation session,
            out string detail)
        {
            if (session.State != ArticleWorkConsumerState.Running || !session.IsActiveProcessingStalled)
            {
                detail = $"session={session.SessionKey} no active-processing stall";
                return ConditionSignal.Inactive;
            }

            detail =
                $"session={session.SessionKey} active={session.ActiveCount}; "
                + "remediation owned by ArticleWork reconcile (Event 5328); "
                + "sibling instance aggregate ACK progress does not clear this";
            return ConditionSignal.Active;
        }

        /// <summary>
        /// Instance DispatchStall for one session: admitted/queued work holding dispatch before
        /// <c>_active</c> increments. Legitimate idle sessions have <c>QueuedCount == 0</c> and are inactive.
        /// </summary>
        /// <param name="session">Projected session for this tick.</param>
        /// <param name="detail">Non-secret diagnostic text for logs/tests.</param>
        /// <returns>
        /// Active when Running with <c>QueuedCount &gt; 0</c> and <c>ActiveCount == 0</c>; otherwise Inactive.
        /// </returns>
        public static ConditionSignal EvaluateInstanceDispatchStall(
            SilentConsumerSessionObservation session,
            out string detail)
        {
            if (session.State != ArticleWorkConsumerState.Running)
            {
                detail = $"session={session.SessionKey} not running";
                return ConditionSignal.Inactive;
            }

            if (session.QueuedCount <= 0 || session.ActiveCount != 0)
            {
                detail =
                    $"session={session.SessionKey} queued={session.QueuedCount} active={session.ActiveCount}; "
                    + "idle (queued=0) is not a dispatch stall";
                return ConditionSignal.Inactive;
            }

            detail =
                $"session={session.SessionKey} queued={session.QueuedCount} active=0; "
                + "dispatch hold before active pipeline (DispatchAcquireHold class)";
            return ConditionSignal.Active;
        }

        /// <summary>
        /// Instance ConsumerLoss: this process has zero running sessions while usable capacity desires consumers.
        /// Process-scoped (not per-backbone): desired slots vs open Running channels on this instance.
        /// </summary>
        /// <param name="sessions">All projected sessions for this process.</param>
        /// <param name="desiredRunningSessions">Desired Running slot count for this instance.</param>
        /// <param name="detail">Non-secret diagnostic text.</param>
        /// <returns>
        /// Inactive when desired is ≤ 0 or any Running session has an open channel; Active when desired &gt; 0 and none are Running with open channels.
        /// </returns>
        public static ConditionSignal EvaluateInstanceConsumerLoss(
            IReadOnlyList<SilentConsumerSessionObservation> sessions,
            int desiredRunningSessions,
            out string detail)
        {
            if (desiredRunningSessions <= 0)
            {
                detail = "no desired sessions for this instance";
                return ConditionSignal.Inactive;
            }

            var running = sessions.Count(
                static session => session.State == ArticleWorkConsumerState.Running && session.HasOpenChannel);

            if (running > 0)
            {
                detail = $"runningSessions={running} desired={desiredRunningSessions}";
                return ConditionSignal.Inactive;
            }

            detail =
                $"runningSessions=0 desired={desiredRunningSessions}; "
                + "local connection/consumer loss — not inferred from sibling aggregate rates";
            return ConditionSignal.Active;
        }

        /// <summary>
        /// Shared ProcessingStall: consumers≥1, unacked floor, near-zero ack (aggregate).
        /// Active only when the fleet-wide ack path looks stalled; a healthy sibling suppresses this signal.
        /// </summary>
        /// <param name="history">Oldest-to-newest Management samples for the queue.</param>
        /// <param name="options">Floors and ack-rate epsilon for this condition.</param>
        /// <param name="detail">Non-secret diagnostic text.</param>
        /// <returns>Active/Inactive when gauges and ack rate are known; Unknown when the latest sample is invalid or ack rate is unavailable.</returns>
        public static ConditionSignal EvaluateSharedProcessingStall(
            IReadOnlyList<QueueSampleEntry> history,
            SilentConsumerConditionThresholds options,
            out string detail)
        {
            if (!TryLatest(history, out var snap, out detail))
            {
                return ConditionSignal.Unknown;
            }

            if (snap.Consumers < 1)
            {
                detail = "consumers < 1";
                return ConditionSignal.Inactive;
            }

            if (snap.MessagesUnacknowledged < options.UnackedFloor)
            {
                detail = $"unacked {snap.MessagesUnacknowledged} < {options.UnackedFloor}";
                return ConditionSignal.Inactive;
            }

            if (!TryAckRate(history, snap, out var ackRate, out detail))
            {
                return ConditionSignal.Unknown;
            }

            if (ackRate > options.AckRateEpsilon)
            {
                detail =
                    $"ackRate={ackRate:0.###}; aggregate progress present — "
                    + "does not prove every instance is healthy";
                return ConditionSignal.Inactive;
            }

            detail =
                $"SHARED aggregate consumers={snap.Consumers} unacked={snap.MessagesUnacknowledged} ackRate={ackRate:0.###}; "
                + "does not identify a specific instance/consumer";
            return ConditionSignal.Active;
        }

        /// <summary>
        /// Shared DispatchStall: ready backlog + near-zero deliver/ack (aggregate; may be masked by siblings).
        /// </summary>
        /// <param name="history">Oldest-to-newest Management samples for the queue.</param>
        /// <param name="options">Floors and rate epsilons for this condition.</param>
        /// <param name="detail">Non-secret diagnostic text.</param>
        /// <returns>Active/Inactive when metrics are known; Unknown when samples or rates are unavailable.</returns>
        public static ConditionSignal EvaluateSharedDispatchStall(
            IReadOnlyList<QueueSampleEntry> history,
            SilentConsumerConditionThresholds options,
            out string detail)
        {
            if (!TryLatest(history, out var snap, out detail))
            {
                return ConditionSignal.Unknown;
            }

            if (snap.Consumers < 1)
            {
                detail = "consumers < 1";
                return ConditionSignal.Inactive;
            }

            if (snap.MessagesReady < options.ReadyFloor)
            {
                detail = $"ready {snap.MessagesReady} < {options.ReadyFloor}";
                return ConditionSignal.Inactive;
            }

            if (options.UnackedFloor > 0 && snap.MessagesUnacknowledged < options.UnackedFloor)
            {
                detail = $"unacked {snap.MessagesUnacknowledged} < {options.UnackedFloor}";
                return ConditionSignal.Inactive;
            }

            if (!TryDeliverRate(history, snap, out var deliverRate, out detail)
                || !TryAckRate(history, snap, out var ackRate, out detail))
            {
                return ConditionSignal.Unknown;
            }

            if (deliverRate > options.DeliverRateEpsilon || ackRate > options.AckRateEpsilon)
            {
                detail =
                    $"deliver={deliverRate:0.###} ack={ackRate:0.###}; "
                    + "aggregate progress — cannot clear a local DispatchStall on another instance";
                return ConditionSignal.Inactive;
            }

            detail =
                $"SHARED aggregate ready={snap.MessagesReady} unacked={snap.MessagesUnacknowledged}; "
                + "healthy siblings may mask a single stalled consumer";
            return ConditionSignal.Active;
        }

        /// <summary>
        /// Shared ConsumerLoss: consumers==0 and ready floor while this instance expects the queue to be active.
        /// One remaining consumer on another instance keeps consumers≥1 and keeps this inactive.
        /// </summary>
        /// <param name="history">Oldest-to-newest Management samples for the queue.</param>
        /// <param name="options">Ready floor for this condition.</param>
        /// <param name="expectedActive">False suppresses the condition (no usable capacity / desired slots).</param>
        /// <param name="detail">Non-secret diagnostic text.</param>
        /// <returns>Inactive when not expected-active or consumers≠0 or ready below floor; Active on fleet-wide zero consumers with backlog; Unknown without a valid latest sample.</returns>
        public static ConditionSignal EvaluateSharedConsumerLoss(
            IReadOnlyList<QueueSampleEntry> history,
            SilentConsumerConditionThresholds options,
            bool expectedActive,
            out string detail)
        {
            if (!expectedActive)
            {
                detail = "queue not expected-active for this instance (no usable capacity / desired slots)";
                return ConditionSignal.Inactive;
            }

            if (!TryLatest(history, out var snap, out detail))
            {
                return ConditionSignal.Unknown;
            }

            if (snap.Consumers != 0)
            {
                detail =
                    $"consumers={snap.Consumers}; not fleet-wide consumer loss "
                    + "(one healthy consumer keeps this inactive)";
                return ConditionSignal.Inactive;
            }

            if (snap.MessagesReady < options.ReadyFloor)
            {
                detail = $"ready {snap.MessagesReady} < {options.ReadyFloor}";
                return ConditionSignal.Inactive;
            }

            detail =
                $"SHARED consumers=0 ready={snap.MessagesReady}; every polling instance may log this independently";
            return ConditionSignal.Active;
        }

        /// <summary>Reads the newest valid snapshot from <paramref name="history"/>.</summary>
        /// <param name="history">Sample ring snapshot.</param>
        /// <param name="snap">Latest valid snapshot when returning true.</param>
        /// <param name="detail">Failure reason when returning false.</param>
        /// <returns><see langword="true"/> when the latest entry is valid and has a snapshot.</returns>
        private static bool TryLatest(
            IReadOnlyList<QueueSampleEntry> history,
            out ManagementQueueSnapshot snap,
            out string detail)
        {
            snap = null!;
            if (history.Count == 0)
            {
                detail = "no samples";
                return false;
            }

            var latest = history[^1];
            if (!latest.IsValid || latest.Snapshot is null)
            {
                detail = latest.FailureReason ?? "invalid sample";
                return false;
            }

            snap = latest.Snapshot;
            detail = string.Empty;
            return true;
        }

        /// <summary>
        /// Resolves ack rate from broker <see cref="ManagementQueueSnapshot.AckRate"/> or derived counters.
        /// </summary>
        /// <param name="history">Samples used for counter derivation when broker rate is absent.</param>
        /// <param name="snap">Latest snapshot.</param>
        /// <param name="rate">Resolved rate when returning true; 0 when false.</param>
        /// <param name="detail">Failure reason when returning false.</param>
        /// <returns><see langword="true"/> when a usable ack rate is available.</returns>
        private static bool TryAckRate(
            IReadOnlyList<QueueSampleEntry> history,
            ManagementQueueSnapshot snap,
            out double rate,
            out string detail)
        {
            if (snap.AckRate is { } broker)
            {
                rate = broker;
                detail = string.Empty;
                return true;
            }

            if (history.Count >= 2)
            {
                var derived = QueueSampleHistory.TryComputeCounterRate(
                    history[^2],
                    history[^1],
                    static s => s.AckTotal);
                if (derived is { } value)
                {
                    rate = value;
                    detail = string.Empty;
                    return true;
                }
            }

            rate = 0;
            detail = "ack rate unavailable";
            return false;
        }

        /// <summary>
        /// Resolves deliver/get rate from broker <see cref="ManagementQueueSnapshot.DeliverGetRate"/> or derived counters.
        /// </summary>
        /// <param name="history">Samples used for counter derivation when broker rate is absent.</param>
        /// <param name="snap">Latest snapshot.</param>
        /// <param name="rate">Resolved rate when returning true; 0 when false.</param>
        /// <param name="detail">Failure reason when returning false.</param>
        /// <returns><see langword="true"/> when a usable deliver rate is available.</returns>
        private static bool TryDeliverRate(
            IReadOnlyList<QueueSampleEntry> history,
            ManagementQueueSnapshot snap,
            out double rate,
            out string detail)
        {
            if (snap.DeliverGetRate is { } broker)
            {
                rate = broker;
                detail = string.Empty;
                return true;
            }

            if (history.Count >= 2)
            {
                var derived = QueueSampleHistory.TryComputeCounterRate(
                    history[^2],
                    history[^1],
                    static s => s.DeliverGetTotal);
                if (derived is { } value)
                {
                    rate = value;
                    detail = string.Empty;
                    return true;
                }
            }

            rate = 0;
            detail = "deliver rate unavailable";
            return false;
        }
    }
}
