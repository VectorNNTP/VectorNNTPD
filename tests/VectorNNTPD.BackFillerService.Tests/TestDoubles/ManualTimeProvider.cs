namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>
    /// <see cref="TimeProvider"/> that moves only when a test calls <see cref="Advance"/>.
    /// Timers created here fire when the advanced clock reaches their due time.
    /// </summary>
    internal sealed class ManualTimeProvider : TimeProvider
    {
        /// <summary>Serializes clock reads, timer registration, and advances.</summary>
        private readonly object _gate = new();
        /// <summary>Current UTC instant returned from <see cref="GetUtcNow"/>.</summary>
        private DateTimeOffset _utcNow;
        /// <summary>Active manual timers evaluated during <see cref="Advance"/>.</summary>
        private readonly List<ManualTimer> _timers = [];

        /// <summary>Initial UTC clock for the provider.</summary>
        /// <param name="utcNow">Starting instant.</param>
        internal ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Moves the clock forward and fires due timers in due-time order until the target is reached.</summary>
        /// <param name="delta">Non-negative advance amount.</param>
        internal void Advance(TimeSpan delta)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(delta.Ticks);
            DateTimeOffset target;
            lock (_gate)
            {
                target = _utcNow + delta;
            }

            while (true)
            {
                ManualTimer? due = null;
                DateTimeOffset dueAt = default;
                lock (_gate)
                {
                    foreach (var timer in _timers.Where(timer => timer.NextDue is { } next && next <= target))
                    {
                        var next = timer.NextDue!.Value;
                        if (due is null || next < dueAt)
                        {
                            due = timer;
                            dueAt = next;
                        }
                    }

                    if (due is null)
                    {
                        _utcNow = target;
                        return;
                    }

                    _utcNow = dueAt;
                }

                due.Fire();
            }
        }

        /// <summary>
        /// Sets the clock to an absolute UTC instant without firing timers.
        /// Used to simulate NTP or operator steps, including a backward adjustment.
        /// </summary>
        /// <param name="utcNow">New instant returned from <see cref="GetUtcNow"/>.</param>
        internal void SetUtcNow(DateTimeOffset utcNow)
        {
            lock (_gate)
            {
                _utcNow = utcNow;
            }
        }

        /// <summary>Adds a timer created by <see cref="CreateTimer"/> to the set evaluated during <see cref="Advance"/>.</summary>
        /// <param name="timer">Timer instance to track; must belong to this provider.</param>
        private void Register(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Add(timer);
            }
        }

        /// <summary>Removes a disposed or disabled timer so <see cref="Advance"/> no longer considers it.</summary>
        /// <param name="timer">Timer to drop from the active list.</param>
        private void Unregister(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        /// <summary>Current UTC under the provider lock (for timer scheduling).</summary>
        private DateTimeOffset Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return _utcNow;
                }
            }
        }

        /// <summary>ITimer implementation driven by the owning provider's manual clock advances.</summary>
        private sealed class ManualTimer : ITimer
        {
            /// <summary>Clock that supplies <see cref="Snapshot"/> for scheduling.</summary>
            private readonly ManualTimeProvider _owner;
            /// <summary>Invoked when the manual clock reaches <see cref="NextDue"/>.</summary>
            private readonly TimerCallback _callback;
            /// <summary>Opaque state passed to the timer callback.</summary>
            private readonly object? _state;
            /// <summary>Repeat interval after each fire; infinite disables periodic scheduling.</summary>
            private TimeSpan _period = Timeout.InfiniteTimeSpan;

            /// <summary>Registers with <paramref name="owner"/> for manual scheduling.</summary>
            /// <param name="owner">Clock that drives due times.</param>
            /// <param name="callback">Invoked when due.</param>
            /// <param name="state">Opaque callback state.</param>
            internal ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                owner.Register(this);
            }

            /// <summary>UTC instant when the callback should run next; null when the timer is disabled.</summary>
            public DateTimeOffset? NextDue { get; private set; }

            /// <summary>
            /// Reschedules relative to the provider snapshot; infinite due time clears <see cref="NextDue"/>.
            /// Does not fire the callback until the owner clock advances.
            /// </summary>
            /// <param name="dueTime">Delay from the current snapshot, or infinite to disable.</param>
            /// <param name="period">Repeat interval after each fire, or infinite for one-shot.</param>
            /// <returns>Always true; failures are not simulated.</returns>
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                NextDue = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : _owner.Snapshot + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                return true;
            }

            /// <summary>Invokes the callback and schedules the next due time when periodic; called only from <see cref="ManualTimeProvider.Advance"/>.</summary>
            internal void Fire()
            {
                NextDue = _period == Timeout.InfiniteTimeSpan ? null : _owner.Snapshot + _period;
                _callback(_state);
            }

            /// <summary>Clears the due time and unregisters from the owning provider.</summary>
            public void Dispose()
            {
                NextDue = null;
                _owner.Unregister(this);
            }

            /// <summary>Async dispose wrapper that delegates to synchronous <see cref="Dispose"/>.</summary>
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
