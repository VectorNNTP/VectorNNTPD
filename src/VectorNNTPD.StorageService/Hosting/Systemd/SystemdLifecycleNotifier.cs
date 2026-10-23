using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Logging;

namespace VectorNNTPD.StorageService.Hosting.Systemd
{

    /// <summary>
    /// Publishes systemd readiness and status notifications from application lifecycle transitions.
    /// </summary>
    /// <remarks>
    /// Readiness is reported at most once, and only after a successful transition to
    /// <see cref="ApplicationState.Running"/>. Startup failure never reports ready.
    /// </remarks>
    public sealed class SystemdLifecycleNotifier : IHostedService, IDisposable
    {
        private readonly ApplicationLifecycle _lifecycle;
        private readonly ISystemdNotifyBridge _notify;
        private readonly ISystemdRuntime _runtime;
        private readonly IOptions<StorageServerOptions> _options;
        private readonly ILogger<SystemdLifecycleNotifier> _logger;
        private readonly object _sync = new();
        private int _readySent;
        private int _stoppingSent;
        private string? _lastStatus;
        private bool _subscribed;

        /// <summary>
        /// Initializes a new instance of the <see cref="SystemdLifecycleNotifier"/> class.
        /// </summary>
        public SystemdLifecycleNotifier(
            ApplicationLifecycle lifecycle,
            ISystemdNotifyBridge notify,
            ISystemdRuntime runtime,
            IOptions<StorageServerOptions> options,
            ILogger<SystemdLifecycleNotifier> logger)
        {
            ArgumentNullException.ThrowIfNull(lifecycle);
            ArgumentNullException.ThrowIfNull(notify);
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);

            _lifecycle = lifecycle;
            _notify = notify;
            _runtime = runtime;
            _options = options;
            _logger = logger;

            _lifecycle.StateChanged += OnStateChanged;
            _subscribed = true;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_runtime.IsLinux && (_runtime.IsSystemdService || _notify.IsEnabled))
            {
                SystemdLogMessages.LifecycleNotificationsActivated(
                    _logger,
                    _notify.IsEnabled,
                    _runtime.IsSystemdService);
            }

            var current = _lifecycle.State;
            OnStateChanged(_lifecycle, new ApplicationStateChangedEventArgs(current, current));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Unsubscribe();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void Dispose() => Unsubscribe();

        private void Unsubscribe()
        {
            if (!_subscribed)
            {
                return;
            }

            _lifecycle.StateChanged -= OnStateChanged;
            _subscribed = false;
        }

        private void OnStateChanged(object? sender, ApplicationStateChangedEventArgs e)
        {
            var state = e.ToState;
            switch (state)
            {
                case ApplicationState.Starting:
                    TryNotifyStatus("Starting: initializing application services");
                    break;

                case ApplicationState.Running:
                    TryNotifyStatus("Running: application initialization completed");
                    TryNotifyReady();
                    break;

                case ApplicationState.Stopping:
                    TryNotifyStopping();
                    TryNotifyStatus("Stopping: graceful shutdown in progress");
                    break;

                case ApplicationState.Stopped:
                    TryNotifyStopping();
                    TryNotifyStatus("Stopped");
                    break;

                case ApplicationState.Created:
                    break;

                default:
                    TryNotifyStatus($"State={state}");
                    break;
            }
        }

        private void TryNotifyReady()
        {
            if (!_options.Value.Systemd.NotifyReadyOnApplicationRunning)
            {
                return;
            }

            if (!_notify.IsEnabled)
            {
                return;
            }

            if (_lifecycle.ShutdownRequested || _lifecycle.State != ApplicationState.Running)
            {
                return;
            }

            if (Interlocked.Exchange(ref _readySent, 1) != 0)
            {
                return;
            }

            _notify.NotifyReady();
            SystemdLogMessages.ReadinessReported(_logger);
        }

        private void TryNotifyStopping()
        {
            if (!_options.Value.Systemd.NotifyStoppingOnApplicationShutdown)
            {
                return;
            }

            if (!_notify.IsEnabled)
            {
                return;
            }

            if (Interlocked.Exchange(ref _stoppingSent, 1) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _readySent, 1);

            _notify.NotifyStopping();
            SystemdLogMessages.StoppingReported(_logger);
        }

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

            _notify.NotifyStatus(status);
            SystemdLogMessages.StatusReported(_logger, status);
        }
    }
}
