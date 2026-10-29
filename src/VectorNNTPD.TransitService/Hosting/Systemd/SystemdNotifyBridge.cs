using Microsoft.Extensions.Hosting.Systemd;
using VectorNNTPD.TransitService.Logging;

namespace VectorNNTPD.TransitService.Hosting.Systemd
{
    /// <summary>
    /// Forwards notifications to the official <see cref="ISystemdNotifier"/> when registered and enabled.
    /// </summary>
    public sealed class SystemdNotifyBridge : ISystemdNotifyBridge
    {
        private static readonly ServiceState Ready = ServiceState.Ready;
        private static readonly ServiceState Stopping = ServiceState.Stopping;
        private static readonly ServiceState Watchdog = new("WATCHDOG=1");

        private readonly ISystemdNotifier? _notifier;
        private readonly ILogger<SystemdNotifyBridge> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="SystemdNotifyBridge"/> class.
        /// </summary>
        public SystemdNotifyBridge(
            IEnumerable<ISystemdNotifier> systemdNotifiers,
            ILogger<SystemdNotifyBridge> logger)
        {
            ArgumentNullException.ThrowIfNull(systemdNotifiers);
            ArgumentNullException.ThrowIfNull(logger);

            _notifier = systemdNotifiers.FirstOrDefault();
            _logger = logger;
        }

        /// <inheritdoc />
        public bool IsEnabled => _notifier?.IsEnabled == true;

        /// <inheritdoc />
        public void NotifyReady() => Notify(Ready, "READY=1");

        /// <inheritdoc />
        public void NotifyStopping() => Notify(Stopping, "STOPPING=1");

        /// <inheritdoc />
        public void NotifyStatus(string status)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(status);
            if (status.Contains('\n', StringComparison.Ordinal) || status.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("Status must not contain newlines or NUL characters.", nameof(status));
            }

            Notify(new ServiceState("STATUS=" + status), "STATUS");
        }

        /// <inheritdoc />
        public void NotifyWatchdog() => Notify(Watchdog, "WATCHDOG=1");

        private void Notify(ServiceState state, string label)
        {
            if (_notifier is null || !_notifier.IsEnabled)
            {
                return;
            }

            try
            {
                _notifier.Notify(state);
                SystemdLogMessages.NotificationSent(_logger, label);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                SystemdLogMessages.NotificationFailed(_logger, ex, label);
                throw;
            }
        }
    }
}
