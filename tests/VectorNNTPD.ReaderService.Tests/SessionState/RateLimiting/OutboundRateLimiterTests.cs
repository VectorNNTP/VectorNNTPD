using VectorNNTPD.Common.Networking.RateLimiting;
using VectorNNTPD.ReaderService.SessionState.RateLimiting;
namespace VectorNNTPD.ReaderService.Tests.SessionState.RateLimiting;

public sealed class OutboundRateLimiterTests
{
    [Fact]
    public async Task ZeroCap_IsPassthrough()
    {
        await using var inner = new MemoryStream();
        await using var limiter = new OutboundRateLimiter(inner, 0, leaveInnerOpen: true);
        var payload = "hello"u8.ToArray();
        await limiter.WriteAsync(payload);
        Assert.Equal(payload, inner.ToArray());
        Assert.Equal(0, limiter.MaxSendBytesPerSecond);
    }

    [Fact]
    public void Update_IsVisibleImmediately()
    {
        using var inner = new MemoryStream();
        using var limiter = new OutboundRateLimiter(inner, 100, leaveInnerOpen: true);
        limiter.UpdateMaxSendBytesPerSecond(50);
        Assert.Equal(50, limiter.MaxSendBytesPerSecond);
        limiter.UpdateMaxSendBytesPerSecond(0);
        Assert.Equal(0, limiter.MaxSendBytesPerSecond);
    }

    [Fact]
    public async Task HighRate_WritesLargeBufferWithoutDelay()
    {
        await using var inner = new MemoryStream();
        await using var limiter = new OutboundRateLimiter(inner, int.MaxValue, leaveInnerOpen: true);
        var payload = new byte[64_000];
        Random.Shared.NextBytes(payload);
        await limiter.WriteAsync(payload);
        Assert.Equal(payload, inner.ToArray());
    }

    [Fact]
    public async Task SequentialSmallWrites_StayUnderFixedCap()
    {
        var clock = new ManualTimeProvider();
        await using var inner = new MemoryStream();
        await using var limiter = new OutboundRateLimiter(inner, 4, leaveInnerOpen: true, clock);
        await limiter.WriteAsync(new byte[2]);
        await limiter.WriteAsync(new byte[2]);
        Assert.Equal(4, inner.Length);

        var blocked = limiter.WriteAsync(new byte[1]).AsTask();
        await WaitForPendingTimerAsync(clock, blocked);
        Assert.False(blocked.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(blocked.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(25));
        await blocked.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(5, inner.Length);
    }

    [Fact]
    public async Task NegativeCap_BlocksUntilUpdated()
    {
        var clock = new ManualTimeProvider();
        await using var inner = new MemoryStream();
        await using var limiter = new OutboundRateLimiter(
            inner,
            AccountRateFormula.BlockedBytesPerSecond,
            leaveInnerOpen: true,
            clock);
        var write = limiter.WriteAsync(new byte[4]).AsTask();
        await WaitForPendingTimerAsync(clock, write);
        Assert.False(write.IsCompleted);
        Assert.Equal(0, inner.Length);
        limiter.UpdateMaxSendBytesPerSecond(1_000_000);
        clock.Advance(TimeSpan.FromMilliseconds(25));
        await write.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, inner.Length);
    }

    [Fact]
    public async Task DynamicIncrease_UnblocksWaitingWrite()
    {
        await using var inner = new MemoryStream();
        await using var limiter = new OutboundRateLimiter(inner, 1, leaveInnerOpen: true);
        await limiter.WriteAsync(new byte[1]);
        var write = limiter.WriteAsync(new byte[8]).AsTask();
        Assert.False(write.IsCompleted);
        limiter.UpdateMaxSendBytesPerSecond(1_000_000);
        await write.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(9, inner.Length);
    }

    [Fact]
    public async Task DynamicDecrease_IsObservedOnNextSlice()
    {
        var clock = new ManualTimeProvider();
        await using var inner = new MemoryStream();
        await using var limiter = new OutboundRateLimiter(inner, 1_000_000, leaveInnerOpen: true, clock);
        await limiter.WriteAsync(new byte[4]);
        limiter.UpdateMaxSendBytesPerSecond(4);
        var blocked = limiter.WriteAsync(new byte[1]).AsTask();
        Assert.False(blocked.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await blocked.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(5, inner.Length);
    }

    [Fact]
    public async Task Cancellation_AbortsBackoff()
    {
        var clock = new ManualTimeProvider();
        await using var inner = new MemoryStream();
        await using var limiter = new OutboundRateLimiter(inner, 1, leaveInnerOpen: true, clock);
        await limiter.WriteAsync(new byte[1]);
        using var cts = new CancellationTokenSource();
        var write = limiter.WriteAsync(new byte[8], cts.Token).AsTask();
        await WaitForPendingTimerAsync(clock, write);
        Assert.False(write.IsCompleted);
        cts.Cancel();
        clock.Advance(TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
    }

    [Fact]
    public async Task Dispose_RejectsNewWrites()
    {
        var inner = new MemoryStream();
        var limiter = new OutboundRateLimiter(inner, 0, leaveInnerOpen: true);
        await limiter.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => limiter.WriteAsync(new byte[1]).AsTask());
    }

    [Fact]
    public async Task Dispose_UnblocksWaitingWrite()
    {
        var clock = new ManualTimeProvider();
        var inner = new MemoryStream();
        var limiter = new OutboundRateLimiter(inner, 1, leaveInnerOpen: true, clock);
        await limiter.WriteAsync(new byte[1]);
        var write = limiter.WriteAsync(new byte[8]).AsTask();
        await WaitForPendingTimerAsync(clock, write);
        Assert.False(write.IsCompleted);
        await limiter.DisposeAsync();
        clock.Advance(TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAnyAsync<Exception>(() => write);
    }

    [Fact]
    public async Task ConcurrentWrites_AreSerialized()
    {
        await using var inner = new MemoryStream();
        await using var limiter = new OutboundRateLimiter(inner, 0, leaveInnerOpen: true);
        var first = Enumerable.Repeat((byte)1, 32).ToArray();
        var second = Enumerable.Repeat((byte)2, 32).ToArray();
        await Task.WhenAll(limiter.WriteAsync(first).AsTask(), limiter.WriteAsync(second).AsTask());
        var bytes = inner.ToArray();
        Assert.Equal(64, bytes.Length);
        Assert.Equal(32, bytes.Count(static b => b == 1));
        Assert.Equal(32, bytes.Count(static b => b == 2));
    }

    [Fact]
    public async Task LeaveInnerOpen_DoesNotDisposeSink()
    {
        var inner = new MemoryStream();
        var limiter = new OutboundRateLimiter(inner, 0, leaveInnerOpen: true);
        await limiter.WriteAsync("x"u8.ToArray());
        await limiter.DisposeAsync();
        inner.WriteByte(2);
        Assert.Equal(2, inner.ToArray()[^1]);
        await inner.DisposeAsync();
    }

    private static async Task WaitForPendingTimerAsync(ManualTimeProvider clock, Task write)
    {
        for (var i = 0; i < 50 && !write.IsCompleted && !clock.HasPendingTimer; i++)
        {
            await Task.Yield();
        }

        Assert.True(clock.HasPendingTimer || write.IsCompleted);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public bool HasPendingTimer
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Exists(static timer => timer.NextDue is not null);
                }
            }
        }

        public void Advance(TimeSpan delta)
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
                    // codeql[cs/linq/missed-where] Lock-held earliest-due selection; no LINQ allocation.
                    foreach (var timer in _timers)
                    {
                        if (timer.NextDue is { } next && next <= target && (due is null || next < dueAt))
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

        internal void Register(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Add(timer);
            }
        }

        internal void Unregister(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        internal DateTimeOffset Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return _utcNow;
                }
            }
        }

        internal sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;

            public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                owner.Register(this);
            }

            public DateTimeOffset? NextDue { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                NextDue = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : _owner.Snapshot + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                return true;
            }

            public void Fire()
            {
                NextDue = _period == Timeout.InfiniteTimeSpan ? null : _owner.Snapshot + _period;
                _callback(_state);
            }

            public void Dispose()
            {
                NextDue = null;
                _owner.Unregister(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
