using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Logging;
using Microsoft.Extensions.Options;

namespace VectorNNTPD.BackFillerService.Hosting.Systemd
{
    /// <summary>
    /// Publishes systemd readiness and status notifications from Generic Host lifetime events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ready is reported at most once after <see cref="IHostApplicationLifetime.ApplicationStarted"/> (all hosted
    /// services started successfully). Startup failure never reports ready. When watchdog health is not yet
    /// true at that moment, or when a handled transient notify failure occurs, readiness is retried until it
    /// succeeds, shutdown begins, or the notifier is disposed. Once shutdown begins, further readiness
    /// notifications are suppressed.
    /// </para>
    /// <para>
    /// <see cref="StartAsync"/> and <see cref="StopAsync"/> do not observe their cancellation tokens.
    /// Lifetime callbacks are registered with <see cref="CancellationToken.Register(Action)"/>,
    /// which runs the delegate inline when that token is already canceled. The returned registrations
    /// are not stored. <see cref="OnApplicationStarted"/> returns immediately unless <see cref="_subscribed"/>
    /// is already true, and that flag is set only after both callbacks are registered.
    /// </para>
    /// </remarks>
    internal sealed class SystemdLifecycleNotifier : IHostedService, IDisposable
    {
        /// <summary>
        /// Delay before the first readiness retry and between subsequent retries after a handled notify failure.
        /// </summary>
        internal static readonly TimeSpan ReadyRetryInterval = TimeSpan.FromSeconds(1);

        /// <summary>Host lifetime whose started and stopping tokens are observed.</summary>
        private readonly IHostApplicationLifetime _hostLifetime;

        /// <summary>Watchdog health marked started or stopping from the lifetime callbacks.</summary>
        private readonly BackFillerApplicationHealth _health;

        /// <summary>Bridge that receives <c>READY=1</c>, <c>STOPPING=1</c>, and <c>STATUS=</c>.</summary>
        private readonly ISystemdNotifyBridge _notify;

        /// <summary>Platform snapshot used only to decide whether activation is logged.</summary>
        private readonly ISystemdRuntime _runtime;

        /// <summary>Options that enable or suppress ready, stopping, and status notifications.</summary>
        private readonly IOptions<BackFillerOptions> _options;

        /// <summary>Clock used to schedule readiness retries after handled notify failures.</summary>
        private readonly TimeProvider _time;

        /// <summary>Logger for activation, readiness, stopping, status, and swallowed notify failures.</summary>
        private readonly ILogger<SystemdLifecycleNotifier> _logger;

        /// <summary>
        /// Non-recursive lock for duplicate <c>STATUS=</c> suppression and readiness-retry timer ownership.
        /// No caller enters it again on the same call stack.
        /// </summary>
        private readonly Lock _sync = new();

        /// <summary>Cached handler so subscribe and unsubscribe use the same delegate instance.</summary>
        private readonly Action _onWatchdogHealthMayHaveImproved;

        /// <summary>
        /// Non-zero after a ready notification succeeds or after stopping/disposal permanently suppresses readiness.
        /// </summary>
        private int _readyTerminal;

        /// <summary>Non-zero while a readiness attempt holds the send gate.</summary>
        private int _readyAttempt;

        /// <summary>Non-zero after the first handled readiness failure was logged at error level.</summary>
        private int _readyFailureLogged;

        /// <summary>Non-zero after <see cref="OnApplicationStarted"/> has armed readiness attempts.</summary>
        private int _applicationStartedSeen;

        /// <summary>Non-zero after <c>STOPPING=1</c> has been sent once.</summary>
        private int _stoppingSent;

        /// <summary>
        /// Last status text accepted for send, including when that send later throws. Compared ordinally.
        /// </summary>
        private string? _lastStatus;

        /// <summary>
        /// Set after both lifetime callbacks are registered. Gates <see cref="OnApplicationStarted"/> and readiness.
        /// </summary>
        private bool _subscribed;

        /// <summary>Periodic readiness retry timer, or <see langword="null"/> when no retry is armed.</summary>
        private ITimer? _readyRetryTimer;

        /// <summary>
        /// Retains the host lifetime, health flags, notify bridge, and systemd options.
        /// </summary>
        /// <param name="hostLifetime">Lifetime whose started and stopping callbacks are registered by <see cref="StartAsync"/>.</param>
        /// <param name="health">Health updated when the host starts and when it begins stopping.</param>
        /// <param name="notify">Bridge used for ready, stopping, and status notifications.</param>
        /// <param name="runtime">Platform snapshot. Used to decide whether activation is logged.</param>
        /// <param name="options">BackFiller options whose <see cref="BackFillerOptions.Systemd"/> flags gate each notification.</param>
        /// <param name="logger">Logger for lifecycle notification events.</param>
        /// <param name="time">Clock for readiness retries. Production uses the registered <see cref="TimeProvider"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when any argument is <see langword="null"/>.</exception>
        public SystemdLifecycleNotifier(
            IHostApplicationLifetime hostLifetime,
            BackFillerApplicationHealth health,
            ISystemdNotifyBridge notify,
            ISystemdRuntime runtime,
            IOptions<BackFillerOptions> options,
            ILogger<SystemdLifecycleNotifier> logger,
            TimeProvider time)
        {
            ArgumentNullException.ThrowIfNull(hostLifetime);
            ArgumentNullException.ThrowIfNull(health);
            ArgumentNullException.ThrowIfNull(notify);
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(time);

            _hostLifetime = hostLifetime;
            _health = health;
            _notify = notify;
            _runtime = runtime;
            _options = options;
            _logger = logger;
            _time = time;
            _onWatchdogHealthMayHaveImproved = OnWatchdogHealthMayHaveImproved;
        }

        /// <summary>
        /// Registers host started and stopping callbacks and publishes the starting status.
        /// </summary>
        /// <param name="cancellationToken">Not observed.</param>
        /// <returns>A completed task. Ready is not sent from this method.</returns>
        /// <remarks>
        /// Activation is logged only when <see cref="ISystemdRuntime.IsLinux"/> is true and either
        /// <see cref="ISystemdRuntime.IsSystemdService"/> or <see cref="ISystemdNotifyBridge.IsEnabled"/> is true.
        /// Callbacks are registered even when that log is skipped. If <see cref="IHostApplicationLifetime.ApplicationStopping"/>
        /// is already canceled, <see cref="OnApplicationStopping"/> runs inside registration, before the starting status is sent.
        /// </remarks>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_runtime.IsLinux && (_runtime.IsSystemdService || _notify.IsEnabled))
            {
                SystemdLogMessages.LifecycleNotificationsActivated(
                    _logger,
                    _notify.IsEnabled,
                    _runtime.IsSystemdService);
            }

            _health.WatchdogHealthMayHaveImproved += _onWatchdogHealthMayHaveImproved;
            _hostLifetime.ApplicationStarted.Register(OnApplicationStarted);
            _hostLifetime.ApplicationStopping.Register(OnApplicationStopping);
            _subscribed = true;

            TryNotifyStatus("Starting: initializing BackFiller hosted services");
            return Task.CompletedTask;
        }

        /// <summary>Sends stopping at most once, including when the stopping callback has not run yet.</summary>
        /// <param name="cancellationToken">Not observed.</param>
        /// <returns>A completed task.</returns>
        /// <remarks>
        /// Calls <see cref="OnApplicationStopping"/> directly. A second stop does not send another
        /// <c>STOPPING=1</c> or another copy of the same stopping status.
        /// </remarks>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            // STOPPING is emitted from ApplicationStopping. Ensure it was observed even if
            // registration raced with an already-stopping host.
            OnApplicationStopping();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Stops readiness retries and prevents a later <see cref="OnApplicationStarted"/> from marking health or sending ready.
        /// </summary>
        /// <remarks>
        /// Sets <see cref="_subscribed"/> to <see langword="false"/>, permanently suppresses readiness, and does not dispose
        /// the lifetime registrations. <see cref="OnApplicationStopping"/> does not consult that flag.
        /// </remarks>
        public void Dispose()
        {
            SuppressReadiness();
            _health.WatchdogHealthMayHaveImproved -= _onWatchdogHealthMayHaveImproved;
            _subscribed = false;
        }

        /// <summary>Marks health started, publishes the running status, then attempts <c>READY=1</c>.</summary>
        /// <remarks>Returns without those side effects when <see cref="Dispose"/> has cleared <see cref="_subscribed"/>.</remarks>
        private void OnApplicationStarted()
        {
            if (!_subscribed)
            {
                return;
            }

            _health.MarkStarted();
            TryNotifyStatus("Running: BackFiller hosted services started");
            Interlocked.Exchange(ref _applicationStartedSeen, 1);
            TryNotifyReady();
        }

        /// <summary>Marks health stopping, attempts <c>STOPPING=1</c>, then publishes the stopping status.</summary>
        /// <remarks>
        /// Does not consult <see cref="_subscribed"/>. Safe to call from both the lifetime callback and <see cref="StopAsync"/>.
        /// </remarks>
        private void OnApplicationStopping()
        {
            _health.MarkStopping();
            TryNotifyStopping();
            TryNotifyStatus("Stopping: graceful shutdown in progress");
        }

        /// <summary>Retries readiness after certificate health may have become healthy again.</summary>
        private void OnWatchdogHealthMayHaveImproved()
        {
            if (Volatile.Read(ref _applicationStartedSeen) == 0)
            {
                return;
            }

            TryNotifyReady();
        }

        /// <summary>
        /// Sends <c>READY=1</c> at most once when options, notify, and watchdog health allow it.
        /// </summary>
        /// <remarks>
        /// Returns without sending when <see cref="BackFillerSystemdOptions.NotifyReadyOnApplicationRunning"/>
        /// is false, <see cref="ISystemdNotifyBridge.IsEnabled"/> is false, readiness was already completed or
        /// suppressed, the notifier is no longer subscribed, or
        /// <see cref="IApplicationHealth.IsHealthyForWatchdog"/> is false. The once-only flag is set only after
        /// <see cref="ISystemdNotifyBridge.NotifyReady"/> returns. Handled notify exceptions schedule a delayed
        /// retry. Concurrent callers share one send gate so at most one successful <c>READY=1</c> is emitted.
        /// </remarks>
        private void TryNotifyReady()
        {
            if (!_subscribed)
            {
                return;
            }

            if (!_options.Value.Systemd.NotifyReadyOnApplicationRunning)
            {
                return;
            }

            if (!_notify.IsEnabled)
            {
                return;
            }

            if (Volatile.Read(ref _readyTerminal) != 0)
            {
                return;
            }

            if (!_health.IsHealthyForWatchdog)
            {
                StopReadyRetries();
                return;
            }

            if (Interlocked.CompareExchange(ref _readyAttempt, 1, 0) != 0)
            {
                return;
            }

            try
            {
                if (!_subscribed || Volatile.Read(ref _readyTerminal) != 0)
                {
                    return;
                }

                if (!_health.IsHealthyForWatchdog)
                {
                    StopReadyRetries();
                    return;
                }

                try
                {
                    _notify.NotifyReady();
                    Interlocked.Exchange(ref _readyTerminal, 1);
                    StopReadyRetries();
                    SystemdLogMessages.ReadinessReported(_logger);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
                {
                    if (Interlocked.Exchange(ref _readyFailureLogged, 1) == 0)
                    {
                        SystemdLogMessages.NotificationFailed(_logger, ex, "READY=1");
                    }
                    else
                    {
                        SystemdLogMessages.ReadinessRetryScheduled(_logger, ReadyRetryInterval);
                    }

                    ScheduleReadyRetry();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _readyAttempt, 0);
            }
        }

        /// <summary>Arms a periodic readiness retry when one is not already running.</summary>
        private void ScheduleReadyRetry()
        {
            if (!_subscribed || Volatile.Read(ref _readyTerminal) != 0)
            {
                return;
            }

            lock (_sync)
            {
                if (!_subscribed || Volatile.Read(ref _readyTerminal) != 0 || _readyRetryTimer is not null)
                {
                    return;
                }

                _readyRetryTimer = _time.CreateTimer(
                    static state => ((SystemdLifecycleNotifier)state!).TryNotifyReady(),
                    this,
                    ReadyRetryInterval,
                    ReadyRetryInterval);
            }
        }

        /// <summary>Disposes any armed readiness retry timer.</summary>
        private void StopReadyRetries()
        {
            ITimer? timer;
            lock (_sync)
            {
                timer = _readyRetryTimer;
                _readyRetryTimer = null;
            }

            timer?.Dispose();
        }

        /// <summary>Permanently suppresses readiness and cancels retries.</summary>
        private void SuppressReadiness()
        {
            Interlocked.Exchange(ref _readyTerminal, 1);
            StopReadyRetries();
        }

        /// <summary>Sends <c>STOPPING=1</c> at most once and suppresses any later ready notification.</summary>
        /// <remarks>
        /// Returns without sending when <see cref="BackFillerSystemdOptions.NotifyStoppingOnApplicationShutdown"/>
        /// is false or notify is disabled. After the once-only flag is claimed, readiness is suppressed
        /// so a later <see cref="TryNotifyReady"/> returns immediately. Exceptions from
        /// <see cref="ISystemdNotifyBridge.NotifyStopping"/> are logged and swallowed.
        /// </remarks>
        private void TryNotifyStopping()
        {
            if (!_options.Value.Systemd.NotifyStoppingOnApplicationShutdown)
            {
                SuppressReadiness();
                return;
            }

            if (!_notify.IsEnabled)
            {
                SuppressReadiness();
                return;
            }

            if (Interlocked.Exchange(ref _stoppingSent, 1) != 0)
            {
                return;
            }

            // Once stopping, readiness must not be reported later.
            SuppressReadiness();

            try
            {
                _notify.NotifyStopping();
                SystemdLogMessages.StoppingReported(_logger);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
            {
                SystemdLogMessages.NotificationFailed(_logger, ex, "STOPPING=1");
            }
        }

        /// <summary>Sends <paramref name="status"/> when lifecycle status is enabled and the text changed.</summary>
        /// <param name="status">Status text passed to <see cref="ISystemdNotifyBridge.NotifyStatus"/>.</param>
        /// <remarks>
        /// Returns without sending when <see cref="BackFillerSystemdOptions.ReportLifecycleStatus"/> is false
        /// or notify is disabled. An equal ordinal status is suppressed under <see cref="_sync"/>.
        /// The remembered text is updated before the send, so a thrown notification is not retried for the same text.
        /// Exceptions from <see cref="ISystemdNotifyBridge.NotifyStatus"/> are logged and swallowed.
        /// </remarks>
        private void TryNotifyStatus(string status)
        {
            if (!_options.Value.Systemd.ReportLifecycleStatus || !_notify.IsEnabled)
            {
                return;
            }

            lock (_sync)
            {
                if (string.Equals(_lastStatus, status, StringComparison.Ordinal))
                {
                    return;
                }

                _lastStatus = status;
            }

            try
            {
                _notify.NotifyStatus(status);
                SystemdLogMessages.StatusReported(_logger, status);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
            {
                SystemdLogMessages.NotificationFailed(_logger, ex, "STATUS");
            }
        }
    }
}
