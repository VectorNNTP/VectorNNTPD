namespace VectorNNTPD.TransitService.Tests.Fixtures;

/// <summary>
/// Test <see cref="TimeProvider"/> that advances monotonically and fires
/// <see cref="TimeProvider.CreateTimer"/> callbacks so <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// completes without real waits.
/// </summary>
internal sealed class ControllableTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<TimerEntry> _timers = [];
    private long _ticks;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        }
    }

    /// <summary>Gets whether any <see cref="CreateTimer"/> callback is currently scheduled.</summary>
    public bool HasScheduledTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets whether a timer is waiting for its due instant.
    /// A fired one-shot timer is not pending, so this becomes true again only when the next delay is registered.
    /// </summary>
    public bool HasPendingTimer
    {
        get
        {
            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (timer.IsPending)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>Advances virtual time and invokes timers that are now due.</summary>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        lock (_gate)
        {
            _ticks += delta.Ticks;
        }

        // Invoke due timers synchronously so tests do not depend on ThreadPool
        // scheduling. Callbacks may start a new Delay; only timers already due
        // at the current virtual time are collected on each pass.
        for (var pass = 0; pass < 32; pass++)
        {
            List<TimerEntry> due = [];
            lock (_gate)
            {
                var now = _ticks;
                // codeql[cs/linq/missed-where] CollectIfDue has side effects under lock; no LINQ filter.
                foreach (var timer in _timers)
                {
                    if (timer.CollectIfDue(now))
                    {
                        due.Add(timer);
                    }
                }
            }

            if (due.Count == 0)
            {
                return;
            }

            foreach (var timer in due)
            {
                timer.Invoke();
            }
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var entry = new TimerEntry(this, callback, state);
        lock (_gate)
        {
            _timers.Add(entry);
            entry.Schedule(_ticks, dueTime, period);
        }

        return entry;
    }

    private void Remove(TimerEntry entry)
    {
        lock (_gate)
        {
            _timers.Remove(entry);
        }
    }

    private sealed class TimerEntry : ITimer
    {
        private readonly ControllableTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private long? _dueTicks;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public TimerEntry(ControllableTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public void Schedule(long nowTicks, TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            _dueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : nowTicks + dueTime.Ticks;
        }

        public bool IsPending => _dueTicks is not null;

        public bool CollectIfDue(long nowTicks)
        {
            if (_dueTicks is not { } due || due > nowTicks)
            {
                return false;
            }

            _dueTicks = _period <= TimeSpan.Zero || _period == Timeout.InfiniteTimeSpan
                ? null
                : nowTicks + _period.Ticks;

            return true;
        }

        public void Invoke() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                Schedule(_owner._ticks, dueTime, period);
            }

            return true;
        }

        public void Dispose() => _owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
