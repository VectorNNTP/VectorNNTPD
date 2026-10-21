using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.RabbitMq
{
    /// <summary>
    /// Locks broker-blocked duration timeout retirement onto the existing multi-host reconnect path.
    /// </summary>
    public sealed class RabbitMqBlockedConnectionTimeoutTests
    {
        /// <summary>
        /// A connection that stays blocked through the configured timeout is retired and replaced.
        /// </summary>
        [Fact]
        public async Task Persistent_block_retires_generation_and_reconnects()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var logger = new CollectingLogger<RabbitMqService>();
            await using var service = CreateService(
                factory,
                logger,
                new ImmediateDelayTimeProvider(),
                blockedTimeoutSeconds: 5);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;

            first.SimulateBlocked("disk alarm");

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await secondConnected.Task.WaitAsync(safety.Token);

            Assert.Equal(2, service.ConnectionGeneration);
            Assert.Equal(1, first.DisposeCount);
            Assert.True(service.IsReady);
            Assert.Contains(
                logger.Entries,
                static entry => entry.EventId.Id == 2807
                    && entry.Message.Contains("generation 1", StringComparison.Ordinal)
                    && entry.Message.Contains("disk alarm", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Entries, static entry => entry.EventId.Id == 2803);
        }

        /// <summary>
        /// Unblocking before the timeout cancels recovery for that episode.
        /// </summary>
        [Fact]
        public async Task Unblock_before_timeout_cancels_recovery()
        {
            var clock = new ControllableTimeProvider();
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            await using var service = CreateService(factory, timeProvider: clock, blockedTimeoutSeconds: 5);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;

            first.SimulateBlocked();
            clock.Advance(TimeSpan.FromSeconds(4));
            first.SimulateUnblocked();
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();

            Assert.Equal(1, factory.ConnectCount);
            Assert.Equal(1, service.ConnectionGeneration);
            Assert.Equal(0, first.DisposeCount);
            Assert.Same(first, factory.LastConnection);
        }

        /// <summary>
        /// After the timeout claims recovery (event 2807), a later unblock cannot prevent reconnect.
        /// </summary>
        [Fact]
        public async Task Timeout_claim_then_unblock_still_recovers()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var logger = new CollectingLogger<RabbitMqService>();
            await using var service = CreateService(
                factory,
                logger,
                new ImmediateDelayTimeProvider(),
                blockedTimeoutSeconds: 5);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;

            first.SimulateBlocked("claimed-then-unblock");
            await WaitForLogEventAsync(logger, eventId: 2807);

            first.SimulateUnblocked();

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await secondConnected.Task.WaitAsync(safety.Token);

            Assert.Equal(2, service.ConnectionGeneration);
            Assert.Equal(1, first.DisposeCount);
            Assert.Contains(
                logger.Entries,
                static entry => entry.EventId.Id == 2807
                    && entry.Message.Contains("claimed-then-unblock", StringComparison.Ordinal));
        }

        /// <summary>
        /// An unblock from a retired connection cannot cancel a blocked-timeout timer on the replacement.
        /// </summary>
        [Fact]
        public async Task Stale_unblock_does_not_cancel_replacement_blocked_timer()
        {
            var clock = new ControllableTimeProvider();
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            await using var service = CreateService(factory, timeProvider: clock, blockedTimeoutSeconds: 5);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;

            first.SimulateLost();
            await WaitForTaskWhileAdvancingAsync(secondConnected.Task, clock);
            var second = factory.LastConnection!;
            Assert.Equal(2, service.ConnectionGeneration);

            var thirdConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = thirdConnected;
            second.SimulateBlocked("replacement-block");
            first.SimulateUnblocked();
            clock.Advance(TimeSpan.FromSeconds(4));
            await Task.Yield();
            Assert.Equal(2, factory.ConnectCount);

            await WaitForTaskWhileAdvancingAsync(thirdConnected.Task, clock);
            Assert.Equal(3, service.ConnectionGeneration);
            Assert.Equal(1, second.DisposeCount);
        }

        /// <summary>
        /// A new blocked episode after unblock gets a fresh timeout window.
        /// </summary>
        [Fact]
        public async Task New_block_episode_starts_a_fresh_timeout()
        {
            var clock = new ControllableTimeProvider();
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            await using var service = CreateService(factory, timeProvider: clock, blockedTimeoutSeconds: 5);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            first.SimulateBlocked("episode-1");
            clock.Advance(TimeSpan.FromSeconds(4));
            first.SimulateUnblocked();
            first.SimulateBlocked("episode-2");
            clock.Advance(TimeSpan.FromSeconds(4));
            await Task.Yield();
            Assert.Equal(1, factory.ConnectCount);

            factory.Connected = secondConnected;
            await WaitForTaskWhileAdvancingAsync(secondConnected.Task, clock);

            Assert.Equal(2, service.ConnectionGeneration);
        }

        /// <summary>
        /// Duplicate blocked notifications for one episode schedule a single timeout.
        /// </summary>
        [Fact]
        public async Task Duplicate_blocked_notifications_do_not_stack_timers()
        {
            var clock = new ControllableTimeProvider();
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            await using var service = CreateService(factory, timeProvider: clock, blockedTimeoutSeconds: 5);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;

            first.SimulateBlocked("first");
            first.SimulateBlocked("duplicate");
            clock.Advance(TimeSpan.FromSeconds(4));
            await Task.Yield();
            Assert.Equal(1, factory.ConnectCount);

            await WaitForTaskWhileAdvancingAsync(secondConnected.Task, clock);
            Assert.Equal(2, factory.ConnectCount);
        }

        /// <summary>
        /// Ordinary shutdown racing a blocked timeout still produces one recovery transition.
        /// </summary>
        [Fact]
        public async Task Shutdown_racing_blocked_timeout_yields_one_recovery()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            await using var service = CreateService(
                factory,
                timeProvider: new ImmediateDelayTimeProvider(),
                blockedTimeoutSeconds: 5);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;

            first.SimulateBlocked();
            first.SimulateLost();

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await secondConnected.Task.WaitAsync(safety.Token);

            Assert.Equal(2, factory.ConnectCount);
            Assert.Equal(2, service.ConnectionGeneration);
            Assert.Equal(1, first.DisposeCount);
        }

        /// <summary>
        /// A stale blocked timeout from a retired generation cannot retire its replacement.
        /// </summary>
        [Fact]
        public async Task Stale_generation_blocked_timeout_does_not_retire_replacement()
        {
            var clock = new ControllableTimeProvider();
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            await using var service = CreateService(factory, timeProvider: clock, blockedTimeoutSeconds: 5);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;

            first.SimulateBlocked("stale-episode");
            first.SimulateLost();
            await WaitForTaskWhileAdvancingAsync(secondConnected.Task, clock);
            var second = factory.LastConnection!;
            Assert.Equal(2, service.ConnectionGeneration);

            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();

            Assert.Equal(2, factory.ConnectCount);
            Assert.Equal(2, service.ConnectionGeneration);
            Assert.Same(second, factory.LastConnection);
            Assert.Equal(0, second.DisposeCount);
        }

        /// <summary>
        /// Stopping the service cancels a pending blocked timeout without starting reconnect.
        /// </summary>
        [Fact]
        public async Task Service_stop_cancels_pending_blocked_timeout()
        {
            var clock = new ControllableTimeProvider();
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory, timeProvider: clock, blockedTimeoutSeconds: 5);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;

            first.SimulateBlocked();
            await service.DisposeAsync();
            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Yield();

            Assert.Equal(1, factory.ConnectCount);
            Assert.Equal(1, first.DisposeCount);
        }

        /// <summary>
        /// Blocked-timeout recovery uses ConnectAsync with the configured multi-host options list.
        /// </summary>
        [Fact]
        public async Task Timeout_recovery_reconnects_with_configured_hosts()
        {
            var factory = new RecordingHostsConnectionFactory();
            var options = BackFillerTestOptions.CreateValidRabbitMq();
            options.Hosts = ["broker-a.example.test", "broker-b.example.test"];
            options.PoolReconnectBaseDelayMs = 1;
            options.PoolReconnectMaxDelayMs = 1;
            options.ConnectionBlockedTimeoutSeconds = 5;
            await using var service = new RabbitMqService(
                factory,
                Options.Create(options),
                new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.BackFillerService:blocked-hosts"),
                NullLogger<RabbitMqService>.Instance,
                new ImmediateDelayTimeProvider());

            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;

            first.SimulateBlocked();

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await secondConnected.Task.WaitAsync(safety.Token);

            Assert.Equal(2, factory.ConnectCount);
            Assert.Equal(2, factory.ConnectOptions.Count);
            Assert.Equal(
                ["broker-a.example.test", "broker-b.example.test"],
                factory.ConnectOptions[1].Hosts!);
        }

        /// <summary>
        /// A delayed blocked callback from a retired connection is ignored by the active generation.
        /// </summary>
        [Fact]
        public async Task Stale_blocked_callback_does_not_start_timer_on_replacement()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            await using var service = CreateService(
                factory,
                timeProvider: new ImmediateDelayTimeProvider(),
                blockedTimeoutSeconds: 5);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;

            first.SimulateLost();
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await secondConnected.Task.WaitAsync(safety.Token);
            var second = factory.LastConnection!;

            first.SimulateBlocked("from-retired");
            await Task.Yield();

            Assert.Equal(2, factory.ConnectCount);
            Assert.Same(second, factory.LastConnection);
            Assert.Equal(0, second.DisposeCount);
        }

        /// <summary>
        /// Advances a controllable clock until <paramref name="task"/> completes or the safety budget expires.
        /// </summary>
        private static async Task WaitForTaskWhileAdvancingAsync(Task task, ControllableTimeProvider clock)
        {
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!task.IsCompleted)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Yield();
                safety.Token.ThrowIfCancellationRequested();
            }

            await task.WaitAsync(safety.Token);
        }

        /// <summary>
        /// Waits until <paramref name="logger"/> records <paramref name="eventId"/> or the safety budget expires.
        /// </summary>
        private static async Task WaitForLogEventAsync(CollectingLogger<RabbitMqService> logger, int eventId)
        {
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!logger.Entries.Any(entry => entry.EventId.Id == eventId))
            {
                await Task.Yield();
                safety.Token.ThrowIfCancellationRequested();
            }
        }

        private static RabbitMqService CreateService(
            IRabbitMqConnectionFactory factory,
            ILogger<RabbitMqService>? logger = null,
            TimeProvider? timeProvider = null,
            int blockedTimeoutSeconds = 30)
        {
            var options = BackFillerTestOptions.CreateValidRabbitMq();
            options.PoolReconnectBaseDelayMs = 1;
            options.PoolReconnectMaxDelayMs = 1;
            options.ConnectionBlockedTimeoutSeconds = blockedTimeoutSeconds;
            return new RabbitMqService(
                factory,
                Options.Create(options),
                new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.BackFillerService:blocked-timeout"),
                logger ?? NullLogger<RabbitMqService>.Instance,
                timeProvider ?? TimeProvider.System);
        }

        /// <summary>
        /// Records options passed to each connect so multi-host failover inputs stay observable.
        /// </summary>
        private sealed class RecordingHostsConnectionFactory : IRabbitMqConnectionFactory
        {
            private readonly FakeBackFillerRabbitMqConnectionFactory _inner = new();

            internal int ConnectCount => _inner.ConnectCount;

            internal FakeBackFillerRabbitMqConnection? LastConnection => _inner.LastConnection;

            internal TaskCompletionSource<int>? Connected
            {
                get => _inner.Connected;
                set => _inner.Connected = value;
            }

            internal List<RabbitMqOptions> ConnectOptions { get; } = [];

            public Task<IRabbitMqConnection> ConnectAsync(
                RabbitMqOptions options,
                string connectionName,
                CancellationToken cancellationToken)
            {
                ConnectOptions.Add(options);
                return _inner.ConnectAsync(options, connectionName, cancellationToken);
            }
        }

        /// <summary>
        /// Completes every <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> immediately.
        /// </summary>
        private sealed class ImmediateDelayTimeProvider : TimeProvider
        {
            /// <inheritdoc />
            public override ITimer CreateTimer(
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    callback(state);
                }

                return new NoOpTimer();
            }

            private sealed class NoOpTimer : ITimer
            {
                public bool Change(TimeSpan dueTime, TimeSpan period) => true;

                public void Dispose()
                {
                }

                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
        }

        /// <summary>
        /// Virtual clock that queues <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> timers until <see cref="Advance"/>.
        /// </summary>
        private sealed class ControllableTimeProvider : TimeProvider
        {
            private readonly Lock _gate = new();
            private TimeSpan _elapsed;
            private readonly List<PendingTimer> _timers = [];

            /// <inheritdoc />
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed;

            /// <inheritdoc />
            public override long GetTimestamp() => _elapsed.Ticks;

            /// <inheritdoc />
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;

            /// <summary>Advances the virtual clock and fires due one-shot timers.</summary>
            /// <param name="delta">Non-negative duration to add to the virtual clock.</param>
            public void Advance(TimeSpan delta)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);

                List<(TimerCallback Callback, object? State)> due = [];
                lock (_gate)
                {
                    _elapsed += delta;
                    for (var i = _timers.Count - 1; i >= 0; i--)
                    {
                        var timer = _timers[i];
                        if (timer.Cancelled || timer.DueAt > _elapsed)
                        {
                            continue;
                        }

                        due.Add((timer.Callback, timer.State));
                        _timers.RemoveAt(i);
                    }
                }

                foreach (var (callback, state) in due)
                {
                    callback(state);
                }
            }

            /// <inheritdoc />
            public override ITimer CreateTimer(
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                ArgumentNullException.ThrowIfNull(callback);
                var timer = new PendingTimer(this, callback, state);
                timer.Change(dueTime, period);
                return timer;
            }

            private void Schedule(PendingTimer timer, TimeSpan dueTime)
            {
                lock (_gate)
                {
                    _timers.Remove(timer);
                    if (dueTime == Timeout.InfiniteTimeSpan)
                    {
                        timer.Cancelled = true;
                        return;
                    }

                    timer.Cancelled = false;
                    timer.DueAt = dueTime <= TimeSpan.Zero ? _elapsed : _elapsed + dueTime;
                    if (timer.DueAt <= _elapsed)
                    {
                        timer.FireImmediately = true;
                        return;
                    }

                    _timers.Add(timer);
                }
            }

            private void Cancel(PendingTimer timer)
            {
                lock (_gate)
                {
                    timer.Cancelled = true;
                    _timers.Remove(timer);
                }
            }

            private sealed class PendingTimer : ITimer
            {
                private readonly ControllableTimeProvider _owner;

                internal PendingTimer(ControllableTimeProvider owner, TimerCallback callback, object? state)
                {
                    _owner = owner;
                    Callback = callback;
                    State = state;
                }

                internal TimerCallback Callback { get; }

                internal object? State { get; }

                internal TimeSpan DueAt { get; set; }

                internal bool Cancelled { get; set; }

                internal bool FireImmediately { get; set; }

                public bool Change(TimeSpan dueTime, TimeSpan period)
                {
                    _ = period;
                    FireImmediately = false;
                    _owner.Schedule(this, dueTime);
                    if (FireImmediately && !Cancelled)
                    {
                        FireImmediately = false;
                        Callback(State);
                    }

                    return true;
                }

                public void Dispose() => _owner.Cancel(this);

                public ValueTask DisposeAsync()
                {
                    Dispose();
                    return ValueTask.CompletedTask;
                }
            }
        }
    }
}
