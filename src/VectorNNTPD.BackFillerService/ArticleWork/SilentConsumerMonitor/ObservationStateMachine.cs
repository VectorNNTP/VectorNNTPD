namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Open/continue/resolve machine for one <see cref="ObservationKey"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ConditionSignal.Unknown"/> neither opens nor resolves: an already-open alert stays open
    /// so a Management outage is not treated as recovery. Unknown also <em>freezes</em> Active/Inactive
    /// persistence clocks (shifts clock origins by the Unknown gap) so outage wall time cannot complete an
    /// open or resolve window. Sustained <see cref="ConditionSignal.Active"/> past the configured open
    /// window opens; sustained <see cref="ConditionSignal.Inactive"/> past the resolve window resolves.
    /// Persistence clocks reset when the opposite conclusive signal arrives.
    /// </remarks>
    internal sealed class ObservationStateMachine
    {
        /// <summary>Stable observation identity owned by this machine.</summary>
        private readonly ObservationKey _key;

        /// <summary>Minimum sustained Active duration before opening.</summary>
        private readonly TimeSpan _openWindow;

        /// <summary>Minimum sustained Inactive duration before resolving while open.</summary>
        private readonly TimeSpan _resolveWindow;

        /// <summary>Clear until opened; Open until resolved.</summary>
        private ObservationPhase _phase;

        /// <summary>UTC start of the current Active persistence clock; null when not accumulating Active.</summary>
        private DateTimeOffset? _activeSinceUtc;

        /// <summary>UTC start of the current Inactive persistence clock while open; null otherwise.</summary>
        private DateTimeOffset? _inactiveSinceUtc;

        /// <summary>
        /// UTC of the last <see cref="Apply"/> used to freeze persistence across <see cref="ConditionSignal.Unknown"/> gaps.
        /// </summary>
        private DateTimeOffset? _lastApplyUtc;

        /// <summary>Creates a machine for one observation identity.</summary>
        /// <param name="key">Stable identity (instance session key, <c>local</c>, or shared queue name).</param>
        /// <param name="openWindow">Minimum sustained Active duration before <see cref="ObservationTransition.Opened"/>.</param>
        /// <param name="resolveWindow">Minimum sustained Inactive duration before <see cref="ObservationTransition.Resolved"/>.</param>
        public ObservationStateMachine(ObservationKey key, TimeSpan openWindow, TimeSpan resolveWindow)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(openWindow, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(resolveWindow, TimeSpan.Zero);
            _key = key;
            _openWindow = openWindow;
            _resolveWindow = resolveWindow;
        }

        /// <summary>Current phase after the last conclusive apply.</summary>
        public ObservationPhase Phase => _phase;

        /// <summary>
        /// Applies one evaluation signal at <paramref name="atUtc"/>.
        /// </summary>
        /// <param name="signal">Active, Inactive, or Unknown.</param>
        /// <param name="atUtc">Evaluation instant from the engine <see cref="TimeProvider"/>.</param>
        /// <param name="detail">Non-secret diagnostic text stamped on the result.</param>
        /// <returns>Transition for this tick; Unknown always yields <see cref="ObservationTransition.None"/>.</returns>
        public ObservationResult Apply(ConditionSignal signal, DateTimeOffset atUtc, string? detail = null)
        {
            switch (signal)
            {
                case ConditionSignal.Unknown:
                    FreezePersistenceClocks(atUtc);
                    _lastApplyUtc = atUtc;
                    return Result(signal, ObservationTransition.None, CurrentPersistence(atUtc), detail);

                case ConditionSignal.Active:
                    _inactiveSinceUtc = null;
                    _activeSinceUtc ??= atUtc;
                    _lastApplyUtc = atUtc;
                    var activeFor = atUtc - _activeSinceUtc.Value;
                    if (_phase == ObservationPhase.Clear)
                    {
                        if (activeFor >= _openWindow)
                        {
                            _phase = ObservationPhase.Open;
                            return Result(signal, ObservationTransition.Opened, activeFor, detail);
                        }

                        return Result(signal, ObservationTransition.None, activeFor, detail);
                    }

                    return Result(signal, ObservationTransition.Continued, activeFor, detail);

                case ConditionSignal.Inactive:
                    _activeSinceUtc = null;
                    if (_phase == ObservationPhase.Clear)
                    {
                        _inactiveSinceUtc = null;
                        _lastApplyUtc = atUtc;
                        return Result(signal, ObservationTransition.None, TimeSpan.Zero, detail);
                    }

                    _inactiveSinceUtc ??= atUtc;
                    _lastApplyUtc = atUtc;
                    var inactiveFor = atUtc - _inactiveSinceUtc.Value;
                    if (inactiveFor >= _resolveWindow)
                    {
                        _phase = ObservationPhase.Clear;
                        _inactiveSinceUtc = null;
                        return Result(signal, ObservationTransition.Resolved, inactiveFor, detail);
                    }

                    return Result(signal, ObservationTransition.Continued, inactiveFor, detail);

                default:
                    _lastApplyUtc = atUtc;
                    return Result(ConditionSignal.Unknown, ObservationTransition.None, TimeSpan.Zero, "unhandled");
            }
        }

        /// <summary>
        /// Shifts Active/Inactive clock origins by the gap since <see cref="_lastApplyUtc"/> so Unknown
        /// wall time does not advance open or resolve persistence.
        /// </summary>
        /// <param name="atUtc">Current evaluation instant.</param>
        private void FreezePersistenceClocks(DateTimeOffset atUtc)
        {
            if (_lastApplyUtc is not { } last)
            {
                return;
            }

            var gap = atUtc - last;
            if (gap <= TimeSpan.Zero)
            {
                return;
            }

            if (_activeSinceUtc is { } activeSince)
            {
                _activeSinceUtc = activeSince + gap;
            }

            if (_inactiveSinceUtc is { } inactiveSince)
            {
                _inactiveSinceUtc = inactiveSince + gap;
            }
        }

        /// <summary>Active or Inactive persistence relative to <paramref name="atUtc"/>, or zero when neither clock is set.</summary>
        /// <param name="atUtc">Evaluation instant.</param>
        /// <returns>Elapsed persistence for the active clock, inactive clock, or <see cref="TimeSpan.Zero"/>.</returns>
        private TimeSpan CurrentPersistence(DateTimeOffset atUtc)
        {
            if (_activeSinceUtc is { } a)
            {
                return atUtc - a;
            }

            if (_inactiveSinceUtc is { } i)
            {
                return atUtc - i;
            }

            return TimeSpan.Zero;
        }

        /// <summary>Builds an <see cref="ObservationResult"/> stamped with this machine's key and current phase.</summary>
        /// <param name="signal">Signal applied this tick.</param>
        /// <param name="transition">Transition produced this tick.</param>
        /// <param name="persistence">Persistence duration for diagnostics.</param>
        /// <param name="detail">Optional non-secret detail.</param>
        /// <returns>Immutable result for emit/tests.</returns>
        private ObservationResult Result(
            ConditionSignal signal,
            ObservationTransition transition,
            TimeSpan persistence,
            string? detail) =>
            new()
            {
                Key = _key,
                Signal = signal,
                Transition = transition,
                Phase = _phase,
                Persistence = persistence,
                Detail = detail,
            };
    }

    /// <summary>One evaluation result emitted to diagnostics and tests.</summary>
    internal sealed class ObservationResult
    {
        /// <summary>Observation identity that owns the state machine.</summary>
        public required ObservationKey Key { get; init; }

        /// <summary>Signal applied this tick.</summary>
        public required ConditionSignal Signal { get; init; }

        /// <summary>Opened, Continued, Resolved, or None.</summary>
        public required ObservationTransition Transition { get; init; }

        /// <summary>Phase after this apply (Clear or Open).</summary>
        public required ObservationPhase Phase { get; init; }

        /// <summary>How long the current Active or Inactive persistence clock has run.</summary>
        public required TimeSpan Persistence { get; init; }

        /// <summary>Non-secret evaluator or prune detail for logs.</summary>
        public string? Detail { get; init; }
    }
}
