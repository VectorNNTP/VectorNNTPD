using System.ComponentModel.DataAnnotations;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Optional systemd-specific hosting behaviour for Linux deployments.
    /// </summary>
    /// <remarks>
    /// These settings never override systemd-supplied watchdog deadlines
    /// (<c>WATCHDOG_USEC</c> / unit <c>WatchdogSec=</c>). Watchdog heartbeats activate only when
    /// systemd has configured a watchdog, the process is the intended watchdog target, and
    /// <see cref="EnableWatchdog"/> is <see langword="true"/>.
    /// </remarks>
    public sealed class SystemdOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether watchdog keep-alives may be sent when systemd
        /// has configured <c>WatchdogSec=</c> for this process.
        /// </summary>
        public bool EnableWatchdog { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether lifecycle <c>STATUS=</c> notifications are sent
        /// when systemd notify is enabled.
        /// </summary>
        public bool ReportLifecycleStatus { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the application sends an explicit <c>READY=1</c>
        /// when the application lifecycle reaches <see cref="ApplicationState.Running"/>.
        /// </summary>
        /// <remarks>
        /// The built-in <c>SystemdLifetime</c> also notifies ready after all hosted services start.
        /// Sending ready at the application lifecycle boundary ensures readiness is tied to
        /// successful application initialization rather than host construction alone.
        /// Duplicate <c>READY=1</c> notifications are harmless.
        /// </remarks>
        public bool NotifyReadyOnApplicationRunning { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the application sends an explicit <c>STOPPING=1</c>
        /// when graceful shutdown begins at the application lifecycle boundary.
        /// </summary>
        public bool NotifyStoppingOnApplicationShutdown { get; set; } = true;

        /// <summary>
        /// Gets or sets the fraction of the systemd watchdog deadline used as the heartbeat interval.
        /// </summary>
        /// <remarks>
        /// systemd recommends notifying at about half the watchdog timeout. Valid range is (0, 1).
        /// </remarks>
        [Range(0.05, 0.9)]
        public double WatchdogIntervalFraction { get; set; } = 0.5;
    }
}
