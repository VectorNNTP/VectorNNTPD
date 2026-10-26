using Microsoft.Extensions.Logging;

namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Per-process silent-consumer evaluation. In-memory state is not shared across BackFiller instances;
    /// shared-queue observations may therefore be duplicated in logs. Queue and backbone identities are
    /// registered lazily from the live catalog/sessions so database-driven changes apply without restart.
    /// </summary>
    internal sealed class SilentConsumerMonitorEngine
    {
        /// <summary>Supplier for the instance id stamped on diagnostic events (typically runtime FQDN).</summary>
        private readonly Func<string> _instanceId;

        /// <summary>Logger for structured silent-consumer events (9100+).</summary>
        private readonly ILogger _logger;

        /// <summary>Clock for evaluation timestamps, persistence, and rate-limit windows.</summary>
        private readonly TimeProvider _time;

        /// <summary>Per-queue Management sample rings (shared scope only).</summary>
        private readonly Dictionary<string, QueueSampleHistory> _histories = new(StringComparer.Ordinal);

        /// <summary>One state machine per observation identity (instance session key, <c>local</c>, or queue).</summary>
        private readonly Dictionary<ObservationKey, ObservationStateMachine> _machines = new();

        /// <summary>
        /// Per-key last emit time for Continued (9111) and Unavailable-while-open (9113).
        /// Interval is <see cref="SilentConsumerMonitorDefaults.ProcessingStall"/> open window.
        /// </summary>
        private readonly Dictionary<ObservationKey, DateTimeOffset> _lastContinuationUtc = new();

        /// <summary>Consecutive non-success Management poll samples since the last success.</summary>
        private int _consecutiveManagementFailures;

        /// <summary>True after consecutive failures reach <see cref="SilentConsumerMonitorDefaults.DegradedAfterConsecutiveFailures"/> until a success clears it.</summary>
        private bool _managementDegraded;

        /// <summary>
        /// Creates the in-process evaluation engine and seeds the process-scoped instance ConsumerLoss machine.
        /// </summary>
        /// <param name="instanceId">
        /// Delegate invoked when stamping diagnostics (and <see cref="InstanceId"/>); typically
        /// <c>() =&gt; runtime.Fqdn</c> so refreshes are visible without rebuilding the engine.
        /// </param>
        /// <param name="logger">Structured logger for Events 9100+.</param>
        /// <param name="time">Time provider for evaluation and rate limiting.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="instanceId"/>, <paramref name="logger"/>, or <paramref name="time"/> is null.
        /// </exception>
        public SilentConsumerMonitorEngine(
            Func<string> instanceId,
            ILogger logger,
            TimeProvider time)
        {
            ArgumentNullException.ThrowIfNull(instanceId);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(time);
            _instanceId = instanceId;
            _logger = logger;
            _time = time;
            EnsureInstanceTarget(
                "local",
                SilentConsumerCondition.ConsumerLoss,
                SilentConsumerMonitorDefaults.ConsumerLoss);
        }

        /// <summary>Instance identifier stamped on diagnostic events (typically current runtime FQDN).</summary>
        public string InstanceId => _instanceId();

        /// <summary>True when Management polling is degraded.</summary>
        public bool IsManagementDegraded => _managementDegraded;

        /// <summary>
        /// Evaluates instance-local session health (no Management call).
        /// ProcessingStall and DispatchStall are keyed by
        /// <see cref="ArticleWorkConsumerSession.ComposeSessionKey"/> so multiple MaxSessions
        /// slots on the same backbone (and distinct backbones) keep independent state machines.
        /// </summary>
        /// <param name="sessions">Projected live sessions for this process (null not allowed).</param>
        /// <param name="desiredRunningSessions">
        /// Count of desired Running consume slots for this instance (usable capacity × MaxSessions).
        /// Used only by process-scoped ConsumerLoss (<c>local</c>).
        /// </param>
        /// <returns>Per-condition results for seen sessions, removed session keys, and ConsumerLoss; also emits diagnostics.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="sessions"/> is null.</exception>
        public IReadOnlyList<ObservationResult> ObserveInstance(
            IReadOnlyList<SilentConsumerSessionObservation> sessions,
            int desiredRunningSessions)
        {
            ArgumentNullException.ThrowIfNull(sessions);
            var now = _time.GetUtcNow();
            var results = new List<ObservationResult>();
            var seenSessionKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var session in sessions)
            {
                if (string.IsNullOrWhiteSpace(session.Backbone) || session.ConnectionNumber < 1)
                {
                    continue;
                }

                var sessionKey = session.SessionKey;
                seenSessionKeys.Add(sessionKey);
                EnsureInstanceTarget(
                    sessionKey,
                    SilentConsumerCondition.ProcessingStall,
                    SilentConsumerMonitorDefaults.ProcessingStall);
                EnsureInstanceTarget(
                    sessionKey,
                    SilentConsumerCondition.DispatchStall,
                    SilentConsumerMonitorDefaults.DispatchStall);

                results.Add(
                    Apply(
                        new ObservationKey(
                            ObservationScope.Instance,
                            SilentConsumerCondition.ProcessingStall,
                            sessionKey),
                        SilentConsumerEvaluators.EvaluateInstanceProcessingStall(session, out var stallDetail),
                        stallDetail,
                        now));
                results.Add(
                    Apply(
                        new ObservationKey(
                            ObservationScope.Instance,
                            SilentConsumerCondition.DispatchStall,
                            sessionKey),
                        SilentConsumerEvaluators.EvaluateInstanceDispatchStall(session, out var dispatchDetail),
                        dispatchDetail,
                        now));
            }

            // Drive Inactive for session keys that left the live session set (sibling keys untouched).
            foreach (var key in _machines.Keys.ToArray())
            {
                if (key.Scope != ObservationScope.Instance
                    || key.Condition == SilentConsumerCondition.ConsumerLoss
                    || string.Equals(key.QueueOrBackbone, "local", StringComparison.Ordinal))
                {
                    continue;
                }

                if (seenSessionKeys.Contains(key.QueueOrBackbone))
                {
                    continue;
                }

                results.Add(Apply(key, ConditionSignal.Inactive, "session removed", now));
            }

            results.Add(
                Apply(
                    new ObservationKey(ObservationScope.Instance, SilentConsumerCondition.ConsumerLoss, "local"),
                    SilentConsumerEvaluators.EvaluateInstanceConsumerLoss(
                        sessions,
                        desiredRunningSessions,
                        out var lossDetail),
                    lossDetail,
                    now));

            Emit(results);
            return results;
        }

        /// <summary>
        /// Ingests one Management poll and evaluates shared conditions for that queue.
        /// </summary>
        /// <param name="queueName">Article Work queue entity name (shared observation identity).</param>
        /// <param name="poll">Classified Management poll for this cycle.</param>
        /// <param name="expectedActive">
        /// True when this instance currently desires consume slots for the queue's backbone
        /// (usable capacity); gates shared ConsumerLoss.
        /// </param>
        /// <returns>Three shared-condition results (ProcessingStall, DispatchStall, ConsumerLoss); also emits diagnostics.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="queueName"/> is null/empty/whitespace.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="poll"/> is null.</exception>
        /// <remarks>
        /// Non-success polls append an invalid sample so shared evaluators return <see cref="ConditionSignal.Unknown"/>.
        /// Unknown neither opens nor resolves; an already-open shared alert remains open until a conclusive
        /// Inactive signal, catalog prune (target gone — not broker recovery), or process restart.
        /// </remarks>
        public IReadOnlyList<ObservationResult> ObserveSharedQueue(
            string queueName,
            ManagementPollResult poll,
            bool expectedActive)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
            ArgumentNullException.ThrowIfNull(poll);
            EnsureShared(queueName);

            var history = _histories[queueName];
            var now = _time.GetUtcNow();
            if (poll.IsSuccess && poll.Snapshot is { } snap)
            {
                history.AddSuccess(snap);
                NoteManagementSuccess();
            }
            else
            {
                history.AddFailure(now, poll.Kind, poll.Reason);
                SilentConsumerMonitorLogMessages.ManagementPollFailed(
                    _logger,
                    InstanceId,
                    queueName,
                    poll.Kind.ToString(),
                    poll.Reason);
                NoteManagementFailure(poll.Reason);
            }

            var samples = history.Snapshot();
            var results = new List<ObservationResult>(3)
            {
                Apply(
                    new ObservationKey(ObservationScope.SharedQueue, SilentConsumerCondition.ProcessingStall, queueName),
                    SilentConsumerEvaluators.EvaluateSharedProcessingStall(
                        samples,
                        SilentConsumerMonitorDefaults.ProcessingStall,
                        out var dDetail),
                    dDetail,
                    now),
                Apply(
                    new ObservationKey(ObservationScope.SharedQueue, SilentConsumerCondition.DispatchStall, queueName),
                    SilentConsumerEvaluators.EvaluateSharedDispatchStall(
                        samples,
                        SilentConsumerMonitorDefaults.DispatchStall,
                        out var d2Detail),
                    d2Detail,
                    now),
                Apply(
                    new ObservationKey(ObservationScope.SharedQueue, SilentConsumerCondition.ConsumerLoss, queueName),
                    SilentConsumerEvaluators.EvaluateSharedConsumerLoss(
                        samples,
                        SilentConsumerMonitorDefaults.ConsumerLoss,
                        expectedActive,
                        out var eDetail),
                    eDetail,
                    now),
            };
            Emit(results);
            return results;
        }

        /// <summary>
        /// Removes sample history for queues no longer in the live catalog.
        /// Open shared observations emit an explicit Resolved event (target removed), then machines are dropped.
        /// This is not a claim that the underlying queue condition cleared on the broker.
        /// </summary>
        /// <param name="activeQueues">Queue names still present in the resolved catalog targets this cycle.</param>
        /// <returns>Resolved results for pruned open shared observations (may be empty).</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="activeQueues"/> is null.</exception>
        public IReadOnlyList<ObservationResult> PruneQueues(IReadOnlyCollection<string> activeQueues)
        {
            ArgumentNullException.ThrowIfNull(activeQueues);
            var active = activeQueues as HashSet<string>
                ?? new HashSet<string>(activeQueues, StringComparer.Ordinal);
            var stale = _histories.Keys.Where(q => !active.Contains(q)).ToArray();
            if (stale.Length == 0)
            {
                return Array.Empty<ObservationResult>();
            }

            var results = new List<ObservationResult>();
            foreach (var queue in stale)
            {
                foreach (var condition in new[]
                         {
                             SilentConsumerCondition.ProcessingStall,
                             SilentConsumerCondition.DispatchStall,
                             SilentConsumerCondition.ConsumerLoss,
                         })
                {
                    var key = new ObservationKey(ObservationScope.SharedQueue, condition, queue);
                    if (!_machines.TryGetValue(key, out var machine))
                    {
                        continue;
                    }

                    if (machine.Phase == ObservationPhase.Open)
                    {
                        results.Add(new ObservationResult
                        {
                            Key = key,
                            Signal = ConditionSignal.Inactive,
                            Transition = ObservationTransition.Resolved,
                            Phase = ObservationPhase.Clear,
                            Persistence = TimeSpan.Zero,
                            Detail = "provider/queue removed from catalog (observation target gone; not broker recovery)",
                        });
                    }
                }

                _histories.Remove(queue);
                RemoveSharedMachines(queue);
            }

            Emit(results);
            return results;
        }

        /// <summary>Applies <paramref name="signal"/> to the machine for <paramref name="key"/> (must already exist).</summary>
        /// <param name="key">Observation identity whose machine receives the signal.</param>
        /// <param name="signal">Evaluator signal for this tick.</param>
        /// <param name="detail">Non-secret detail text.</param>
        /// <param name="atUtc">Evaluation instant.</param>
        /// <returns>State-machine result for this tick.</returns>
        private ObservationResult Apply(
            ObservationKey key,
            ConditionSignal signal,
            string detail,
            DateTimeOffset atUtc) =>
            _machines[key].Apply(signal, atUtc, detail);

        /// <summary>
        /// Emits structured logs for Opened/Resolved immediately and rate-limits Continued / Unavailable-while-open.
        /// </summary>
        /// <param name="results">Results produced by the current evaluation pass.</param>
        private void Emit(IReadOnlyList<ObservationResult> results)
        {
            foreach (var result in results)
            {
                switch (result.Transition)
                {
                    case ObservationTransition.Opened:
                        SilentConsumerMonitorLogMessages.ObservationOpened(
                            _logger,
                            result.Key.Scope,
                            result.Key.Condition,
                            result.Key.QueueOrBackbone,
                            InstanceId,
                            result.Persistence.TotalSeconds,
                            result.Detail);
                        break;
                    case ObservationTransition.Resolved:
                        SilentConsumerMonitorLogMessages.ObservationResolved(
                            _logger,
                            result.Key.Scope,
                            result.Key.Condition,
                            result.Key.QueueOrBackbone,
                            InstanceId,
                            result.Detail);
                        break;
                    case ObservationTransition.Continued when result.Phase == ObservationPhase.Open:
                        EmitRateLimited(
                            result.Key,
                            () => SilentConsumerMonitorLogMessages.ObservationContinued(
                                _logger,
                                result.Key.Scope,
                                result.Key.Condition,
                                result.Key.QueueOrBackbone,
                                InstanceId,
                                result.Persistence.TotalSeconds,
                                result.Detail));
                        break;
                    case ObservationTransition.None
                        when result.Signal == ConditionSignal.Unknown && result.Phase == ObservationPhase.Open:
                        EmitRateLimited(
                            result.Key,
                            () => SilentConsumerMonitorLogMessages.ObservationUnavailableWhileOpen(
                                _logger,
                                result.Key.Scope,
                                result.Key.Condition,
                                result.Key.QueueOrBackbone,
                                InstanceId,
                                result.Detail));
                        break;
                }
            }
        }

        /// <summary>
        /// Emits at most once per observation key per ProcessingStall open window.
        /// Continued and Unavailable-while-open share the same timestamp for a given key.
        /// </summary>
        /// <param name="key">Observation identity used as the rate-limit key.</param>
        /// <param name="emit">Log emission to run when the interval has elapsed.</param>
        private void EmitRateLimited(ObservationKey key, Action emit)
        {
            var now = _time.GetUtcNow();
            if (!_lastContinuationUtc.TryGetValue(key, out var last)
                || now - last >= SilentConsumerMonitorDefaults.ProcessingStall.OpenWindow)
            {
                _lastContinuationUtc[key] = now;
                emit();
            }
        }

        /// <summary>Clears Management-degraded latch after a successful poll sample.</summary>
        private void NoteManagementSuccess()
        {
            if (_managementDegraded)
            {
                _managementDegraded = false;
                SilentConsumerMonitorLogMessages.ManagementRecovered(_logger, InstanceId);
            }

            _consecutiveManagementFailures = 0;
        }

        /// <summary>Counts consecutive poll failures and emits Event 9120 when the degraded threshold is crossed.</summary>
        /// <param name="reason">Non-secret failure reason stamped on the degraded event.</param>
        private void NoteManagementFailure(string reason)
        {
            _consecutiveManagementFailures++;
            if (!_managementDegraded
                && _consecutiveManagementFailures >= SilentConsumerMonitorDefaults.DegradedAfterConsecutiveFailures)
            {
                _managementDegraded = true;
                SilentConsumerMonitorLogMessages.ManagementDegraded(
                    _logger,
                    InstanceId,
                    _consecutiveManagementFailures,
                    reason);
            }
        }

        /// <summary>Lazily creates shared history and state machines for a catalog queue name.</summary>
        /// <param name="queue">Queue entity name.</param>
        private void EnsureShared(string queue)
        {
            if (_histories.ContainsKey(queue))
            {
                return;
            }

            _histories[queue] = new QueueSampleHistory(SilentConsumerMonitorDefaults.SampleHistoryCapacity);
            EnsureInstanceTarget(
                queue,
                SilentConsumerCondition.ProcessingStall,
                SilentConsumerMonitorDefaults.ProcessingStall,
                ObservationScope.SharedQueue);
            EnsureInstanceTarget(
                queue,
                SilentConsumerCondition.DispatchStall,
                SilentConsumerMonitorDefaults.DispatchStall,
                ObservationScope.SharedQueue);
            EnsureInstanceTarget(
                queue,
                SilentConsumerCondition.ConsumerLoss,
                SilentConsumerMonitorDefaults.ConsumerLoss,
                ObservationScope.SharedQueue);
        }

        /// <summary>Creates a state machine for <paramref name="target"/> when absent (instance session key or shared queue).</summary>
        /// <param name="target">Session key, <c>local</c>, or shared queue name.</param>
        /// <param name="condition">Condition the machine tracks.</param>
        /// <param name="thresholds">Open/resolve windows for the machine.</param>
        /// <param name="scope">Instance (default) or SharedQueue.</param>
        private void EnsureInstanceTarget(
            string target,
            SilentConsumerCondition condition,
            SilentConsumerConditionThresholds thresholds,
            ObservationScope scope = ObservationScope.Instance)
        {
            var key = new ObservationKey(scope, condition, target);
            if (_machines.ContainsKey(key))
            {
                return;
            }

            _machines[key] = new ObservationStateMachine(key, thresholds.OpenWindow, thresholds.ResolveWindow);
        }

        /// <summary>Drops all shared machines for a pruned queue name.</summary>
        /// <param name="queue">Queue entity name being removed from catalog targets.</param>
        private void RemoveSharedMachines(string queue)
        {
            RemoveMachine(new ObservationKey(ObservationScope.SharedQueue, SilentConsumerCondition.ProcessingStall, queue));
            RemoveMachine(new ObservationKey(ObservationScope.SharedQueue, SilentConsumerCondition.DispatchStall, queue));
            RemoveMachine(new ObservationKey(ObservationScope.SharedQueue, SilentConsumerCondition.ConsumerLoss, queue));
        }

        /// <summary>Removes the machine and its rate-limit timestamp so a reintroduced target starts fresh.</summary>
        /// <param name="key">Observation identity to drop.</param>
        private void RemoveMachine(ObservationKey key)
        {
            _machines.Remove(key);
            _lastContinuationUtc.Remove(key);
        }
    }
}
