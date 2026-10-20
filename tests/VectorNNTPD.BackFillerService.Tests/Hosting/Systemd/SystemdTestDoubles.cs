using Microsoft.Extensions.Hosting;
using VectorNNTPD.BackFillerService.Hosting.Systemd;

namespace VectorNNTPD.BackFillerService.Tests.Hosting.Systemd
{
    /// <summary>
    /// Records systemd notify payloads and counts.
    /// <see cref="ThrowOnNotify"/> fails every notify while set.
    /// <see cref="ReadyFailuresRemaining"/> fails that many <see cref="NotifyReady"/> attempts then succeeds.
    /// <see cref="IsEnabled"/> defaults to true.
    /// </summary>
    internal sealed class FakeSystemdNotifyBridge : ISystemdNotifyBridge
    {
        /// <summary>Protects the notification list.</summary>
        private readonly object _sync = new();
        /// <summary>Recorded notify payloads in order.</summary>
        private readonly List<string> _notifications = [];
        /// <summary>Successful READY recordings.</summary>
        private int _readyCount;
        /// <summary>Successful STOPPING recordings.</summary>
        private int _stoppingCount;
        /// <summary>Successful WATCHDOG recordings.</summary>
        private int _watchdogCount;
        /// <summary>Entries into <see cref="NotifyReady"/> regardless of success.</summary>
        private int _readyAttempts;
        /// <summary>Remaining READY attempts that throw before a successful record.</summary>
        private int _readyFailuresRemaining;

        /// <summary>When false, notify methods return without recording (mirrors disabled notify socket).</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// When set, every notify path that checks this throws before recording until cleared.
        /// Evaluated after <see cref="ReadyFailuresRemaining"/> on the READY path.
        /// </summary>
        public Exception? ThrowOnNotify { get; set; }

        /// <summary>
        /// Number of remaining <see cref="NotifyReady"/> attempts that throw before recording succeeds.
        /// Does not affect STATUS, STOPPING, or WATCHDOG paths.
        /// </summary>
        public int ReadyFailuresRemaining
        {
            get => Volatile.Read(ref _readyFailuresRemaining);
            set => Volatile.Write(ref _readyFailuresRemaining, value);
        }

        /// <summary>
        /// Optional gate held before a READY attempt records. Tests use this to hold concurrent callers.
        /// </summary>
        public ManualResetEventSlim? BlockReadyUntil { get; set; }

        /// <summary>Completes when any <see cref="NotifyReady"/> attempt begins, before recording or throwing.</summary>
        public TaskCompletionSource ReadyAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Snapshot of recorded notify lines (READY, STOPPING, WATCHDOG, STATUS).</summary>
        internal IReadOnlyList<string> Notifications
        {
            get
            {
                lock (_sync)
                {
                    return _notifications.ToArray();
                }
            }
        }

        /// <summary>Count of successful READY=1 recordings.</summary>
        internal int ReadyCount => Volatile.Read(ref _readyCount);

        /// <summary>Total entries into <see cref="NotifyReady"/>, including failures.</summary>
        internal int ReadyAttemptCount => Volatile.Read(ref _readyAttempts);

        /// <summary>Count of successful STOPPING=1 recordings.</summary>
        internal int StoppingCount => Volatile.Read(ref _stoppingCount);

        /// <summary>Count of successful WATCHDOG=1 recordings.</summary>
        internal int WatchdogCount => Volatile.Read(ref _watchdogCount);

        /// <summary>Completes after the first <see cref="NotifyReady"/> records.</summary>
        public TaskCompletionSource ReadySent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes after the first <see cref="NotifyStopping"/> records.</summary>
        public TaskCompletionSource StoppingSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes after the first <see cref="NotifyWatchdog"/> records.</summary>
        public TaskCompletionSource WatchdogSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when any <see cref="NotifyWatchdog"/> attempt begins.</summary>
        public TaskCompletionSource WatchdogAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Entries into <see cref="NotifyWatchdog"/> regardless of enablement.</summary>
        private int _watchdogAttempts;

        /// <summary>Completes on the second watchdog notify attempt.</summary>
        private readonly TaskCompletionSource _secondWatchdogAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Total entries into <see cref="NotifyWatchdog"/>.</summary>
        internal int WatchdogAttemptCount => Volatile.Read(ref _watchdogAttempts);

        /// <summary>Completes when <see cref="NotifyWatchdog"/> is entered for the second time, before that call records or throws.</summary>
        internal Task SecondWatchdogAttempt => _secondWatchdogAttempt.Task;

        /// <summary>
        /// Runs on the watchdog thread after a successful notify is recorded and before
        /// <see cref="NotifyWatchdog"/> returns. The loop cannot start another tick until it returns.
        /// </summary>
        public Action? AfterWatchdogRecorded { get; set; }

        /// <inheritdoc />
        public void NotifyReady()
        {
            Interlocked.Increment(ref _readyAttempts);
            ReadyAttempted.TrySetResult();
            BlockReadyUntil?.Wait();

            while (true)
            {
                var remaining = Volatile.Read(ref _readyFailuresRemaining);
                if (remaining <= 0)
                {
                    break;
                }

                if (Interlocked.CompareExchange(ref _readyFailuresRemaining, remaining - 1, remaining) == remaining)
                {
                    throw ThrowOnNotify ?? new InvalidOperationException("notify-failed");
                }
            }

            Record("READY=1", ref _readyCount);
            ReadySent.TrySetResult();
        }

        /// <inheritdoc />
        public void NotifyStopping()
        {
            Record("STOPPING=1", ref _stoppingCount);
            StoppingSent.TrySetResult();
        }

        /// <inheritdoc />
        public void NotifyStatus(string status)
        {
            if (ThrowOnNotify is not null)
            {
                throw ThrowOnNotify;
            }

            if (!IsEnabled)
            {
                return;
            }

            lock (_sync)
            {
                _notifications.Add("STATUS=" + status);
            }
        }

        /// <inheritdoc />
        public void NotifyWatchdog()
        {
            var attempt = Interlocked.Increment(ref _watchdogAttempts);
            WatchdogAttempted.TrySetResult();
            if (attempt >= 2)
            {
                _secondWatchdogAttempt.TrySetResult();
            }

            Record("WATCHDOG=1", ref _watchdogCount);
            WatchdogSent.TrySetResult();
            AfterWatchdogRecorded?.Invoke();
        }

        /// <summary>Records a notify line and increments <paramref name="counter"/> when enabled and not throwing.</summary>
        private void Record(string value, ref int counter)
        {
            if (ThrowOnNotify is not null)
            {
                throw ThrowOnNotify;
            }

            if (!IsEnabled)
            {
                return;
            }

            lock (_sync)
            {
                _notifications.Add(value);
            }

            Interlocked.Increment(ref counter);
        }
    }

    /// <summary>Settable systemd runtime. Watchdog is configured only when <see cref="WatchdogTimeout"/> is non-null.</summary>
    internal sealed class FakeSystemdRuntime : ISystemdRuntime
    {
        /// <inheritdoc />
        public bool IsLinux { get; set; }

        /// <inheritdoc />
        public bool IsSystemdService { get; set; }

        /// <inheritdoc />
        public bool IsNotifyEnabled { get; set; }

        /// <inheritdoc />
        public TimeSpan? WatchdogTimeout { get; set; }

        /// <inheritdoc />
        public bool IsWatchdogConfigured => WatchdogTimeout is not null;
    }

    /// <summary>Mutable watchdog health flag; signals when an unhealthy read is observed.</summary>
    internal sealed class FakeApplicationHealth : IApplicationHealth
    {
        /// <summary>Backing flag for <see cref="IsHealthyForWatchdog"/>.</summary>
        private bool _healthy;
        /// <summary>Signaled when health is read while unhealthy.</summary>
        private readonly TaskCompletionSource _unhealthyRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public bool IsHealthyForWatchdog
        {
            get
            {
                var healthy = Volatile.Read(ref _healthy);
                if (!healthy)
                {
                    _unhealthyRead.TrySetResult();
                }

                return healthy;
            }

            set => Volatile.Write(ref _healthy, value);
        }

        /// <summary>Completes when <see cref="IsHealthyForWatchdog"/> is read while false.</summary>
        private Task UnhealthyRead => _unhealthyRead.Task;
    }

    /// <summary>
    /// Signals each time <see cref="IApplicationHealth.IsHealthyForWatchdog"/> returns.
    /// </summary>
    internal sealed class ObservingApplicationHealth : IApplicationHealth
    {
        /// <summary>Health implementation whose reads are observed.</summary>
        private readonly IApplicationHealth _inner;
        /// <summary>Completes on each health read; replaced by <see cref="ExpectNextRead"/>.</summary>
        private TaskCompletionSource _readReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Wraps <paramref name="inner"/> and completes <see cref="ReadReturned"/> on each health read.</summary>
        /// <param name="inner">Health implementation under test.</param>
        internal ObservingApplicationHealth(IApplicationHealth inner)
        {
            _inner = inner;
        }

        /// <summary>Completes when the current expected read occurs; reset via <see cref="ExpectNextRead"/>.</summary>
        private Task ReadReturned => Volatile.Read(ref _readReturned).Task;

        /// <summary>Arms a fresh completion source for the next health read.</summary>
        private void ExpectNextRead() =>
            Volatile.Write(ref _readReturned, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        /// <inheritdoc />
        public bool IsHealthyForWatchdog
        {
            get
            {
                var healthy = _inner.IsHealthyForWatchdog;
                Volatile.Read(ref _readReturned).TrySetResult();
                return healthy;
            }
        }
    }

    /// <summary>Factory for common fake systemd runtime profiles used across lifecycle and watchdog tests.</summary>
    internal sealed class RecordingSystemdRuntimeFactory
    {
        /// <summary>Linux process managed by systemd with notify enabled and optional watchdog timeout.</summary>
        /// <param name="watchdog">Watchdog interval from systemd when non-null.</param>
        internal static FakeSystemdRuntime LinuxUnderSystemd(TimeSpan? watchdog = null) => new()
        {
            IsLinux = true,
            IsSystemdService = true,
            IsNotifyEnabled = true,
            WatchdogTimeout = watchdog,
        };

        /// <summary>Linux interactive console: not a systemd service, notify and watchdog off.</summary>
        internal static FakeSystemdRuntime LinuxConsole() => new()
        {
            IsLinux = true,
            IsSystemdService = false,
            IsNotifyEnabled = false,
            WatchdogTimeout = null,
        };

        /// <summary>Non-Linux host: all systemd features disabled.</summary>
        internal static FakeSystemdRuntime NonLinux() => new()
        {
            IsLinux = false,
            IsSystemdService = false,
            IsNotifyEnabled = false,
            WatchdogTimeout = null,
        };
    }

    /// <summary>Manual <see cref="IHostApplicationLifetime"/> for driving started/stopping tokens in unit tests.</summary>
    internal sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        /// <summary>Source for <see cref="ApplicationStarted"/>.</summary>
        private readonly CancellationTokenSource _started = new();
        /// <summary>Source for <see cref="ApplicationStopping"/>.</summary>
        private readonly CancellationTokenSource _stopping = new();
        /// <summary>Source for <see cref="ApplicationStopped"/>.</summary>
        private readonly CancellationTokenSource _stopped = new();

        /// <inheritdoc />
        public CancellationToken ApplicationStarted => _started.Token;

        /// <inheritdoc />
        public CancellationToken ApplicationStopping => _stopping.Token;

        /// <inheritdoc />
        public CancellationToken ApplicationStopped => _stopped.Token;

        /// <summary>True after <see cref="StopApplication"/>.</summary>
        public bool StopRequested { get; private set; }

        /// <summary>Completes when <see cref="StopApplication"/> is invoked.</summary>
        public TaskCompletionSource StoppingRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Cancels <see cref="ApplicationStarted"/>.</summary>
        internal void SignalStarted() => _started.Cancel();

        /// <summary>Cancels <see cref="ApplicationStopping"/>.</summary>
        internal void SignalStopping() => _stopping.Cancel();

        /// <summary>Cancels <see cref="ApplicationStopped"/>.</summary>
        private void SignalStopped() => _stopped.Cancel();

        /// <summary>Mimics host stop request: sets flags and cancels stopping if not already canceled.</summary>
        public void StopApplication()
        {
            StopRequested = true;
            StoppingRequested.TrySetResult();
            if (!_stopping.IsCancellationRequested)
            {
                _stopping.Cancel();
            }
        }
    }
}
