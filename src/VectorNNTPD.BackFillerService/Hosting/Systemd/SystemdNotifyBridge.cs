using Microsoft.Extensions.Hosting.Systemd;
using Serilog.Debugging;
using VectorNNTPD.BackFillerService.Logging;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.BackFillerService.Hosting.Systemd
{
    /// <summary>
    /// Forwards notifications to the official <see cref="ISystemdNotifier"/> when registered and enabled.
    /// </summary>
    /// <remarks>
    /// Notification failures are logged and swallowed so a broken notify socket cannot crash
    /// BackFiller. Watchdog keep-alives remain best-effort; missing keep-alives still trip systemd
    /// when <c>WatchdogSec=</c> is configured. When several notifiers are registered, only the first is used.
    /// </remarks>
    internal sealed class SystemdNotifyBridge : ISystemdNotifyBridge
    {
        /// <summary>Cached <c>READY=1</c> state.</summary>
        private static readonly ServiceState Ready = ServiceState.Ready;

        /// <summary>Cached <c>STOPPING=1</c> state.</summary>
        private static readonly ServiceState Stopping = ServiceState.Stopping;

        /// <summary>Cached <c>WATCHDOG=1</c> state.</summary>
        private static readonly ServiceState Watchdog = new("WATCHDOG=1");

        /// <summary>First registered notifier, or <see langword="null"/> when none was supplied.</summary>
        private readonly ISystemdNotifier? _notifier;

        /// <summary>Logger for notification success and swallowed notification failures.</summary>
        private readonly ILogger<SystemdNotifyBridge> _logger;

        /// <summary>
        /// Retains the first registered systemd notifier.
        /// </summary>
        /// <param name="systemdNotifiers">Registered notifiers. Only the first is used.</param>
        /// <param name="logger">Logger for notification success and failure.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="systemdNotifiers"/> or <paramref name="logger"/> is <see langword="null"/>.
        /// </exception>
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

        /// <summary>Sends <c>READY=1</c> when <see cref="IsEnabled"/> is <see langword="true"/>.</summary>
        /// <remarks>
        /// Does nothing when no notifier is registered or the notifier is disabled.
        /// Exceptions from <see cref="ISystemdNotifier.Notify"/> are logged and swallowed.
        /// </remarks>
        public void NotifyReady() => Notify(Ready, "READY=1");

        /// <summary>Sends <c>STOPPING=1</c> when <see cref="IsEnabled"/> is <see langword="true"/>.</summary>
        /// <remarks>
        /// Does nothing when no notifier is registered or the notifier is disabled.
        /// Exceptions from <see cref="ISystemdNotifier.Notify"/> are logged and swallowed.
        /// </remarks>
        public void NotifyStopping() => Notify(Stopping, "STOPPING=1");

        /// <summary>Sends a <c>STATUS=</c> message when <see cref="IsEnabled"/> is <see langword="true"/>.</summary>
        /// <param name="status">Human-readable status without newlines or NUL.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="status"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="status"/> is empty, whitespace, or contains a newline or a NUL character.
        /// </exception>
        /// <remarks>
        /// Validation runs before the enabled check. When notify is disabled, a valid status is not sent.
        /// Exceptions from <see cref="ISystemdNotifier.Notify"/> are logged and swallowed.
        /// </remarks>
        public void NotifyStatus(string status)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(status);
            if (status.Contains('\n', StringComparison.Ordinal) || status.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("Status must not contain newlines or NUL characters.", nameof(status));
            }

            Notify(new ServiceState("STATUS=" + status), "STATUS");
        }

        /// <summary>Sends <c>WATCHDOG=1</c> when <see cref="IsEnabled"/> is <see langword="true"/>.</summary>
        /// <remarks>
        /// Does nothing when no notifier is registered or the notifier is disabled.
        /// Exceptions from <see cref="ISystemdNotifier.Notify"/> are logged and swallowed.
        /// </remarks>
        public void NotifyWatchdog() => Notify(Watchdog, "WATCHDOG=1");

        /// <summary>Sends <paramref name="state"/> when a notifier is registered and enabled.</summary>
        /// <param name="state">State passed to <see cref="ISystemdNotifier.Notify"/>.</param>
        /// <param name="label">Text written on the success or failure log. Not sent as the state payload.</param>
        /// <remarks>
        /// Returns without sending when <see cref="_notifier"/> is <see langword="null"/> or disabled.
        /// Any exception from <see cref="ISystemdNotifier.Notify"/> is logged and swallowed.
        /// </remarks>
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
            // codeql[cs/catch-of-all-exceptions] A notification or diagnostic failure must not escape the notifier.
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                try
                {
                    SystemdLogMessages.NotificationFailed(_logger, ex, label);
                }
                // codeql[cs/catch-of-all-exceptions] A notification or diagnostic failure must not escape the notifier.
                catch (Exception logFailure) when (!ExceptionHelpers.IsFatal(logFailure))
                {
                    try
                    {
                        SelfLog.WriteLine("systemd notification failure log failed: {0}", logFailure);
                    }
                    // codeql[cs/catch-of-all-exceptions] A notification or diagnostic failure must not escape the notifier.
                    catch (Exception selfLogFailure) when (!ExceptionHelpers.IsFatal(selfLogFailure))
                    {
                        // Notification and its diagnostic log are both best-effort.
                    }
                }
            }
        }
    }
}
