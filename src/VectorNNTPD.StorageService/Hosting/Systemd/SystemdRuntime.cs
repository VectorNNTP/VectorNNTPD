using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting.Systemd;
using VectorNNTPD.StorageService.Logging;

namespace VectorNNTPD.StorageService.Hosting.Systemd
{

    /// <summary>
    /// Default systemd environment detection based on OS platform checks and systemd variables.
    /// </summary>
    public sealed class SystemdRuntime : ISystemdRuntime
    {
        /// <summary>Environment variable containing the watchdog timeout in microseconds.</summary>
        public const string WatchdogUsecVariable = "WATCHDOG_USEC";

        /// <summary>Environment variable containing the PID expected to send watchdog keep-alives.</summary>
        public const string WatchdogPidVariable = "WATCHDOG_PID";

        private readonly ILogger<SystemdRuntime> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="SystemdRuntime"/> class.
        /// </summary>
        public SystemdRuntime(
            IEnumerable<ISystemdNotifier> systemdNotifiers,
            ILogger<SystemdRuntime> logger)
        {
            ArgumentNullException.ThrowIfNull(systemdNotifiers);
            ArgumentNullException.ThrowIfNull(logger);

            _logger = logger;

            IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            IsSystemdService = IsLinux && SystemdHelpers.IsSystemdService();

            var notifier = systemdNotifiers.FirstOrDefault();
            IsNotifyEnabled = notifier?.IsEnabled == true;

            WatchdogTimeout = TryReadWatchdogTimeout(out var reason);
            IsWatchdogConfigured = WatchdogTimeout is not null;

            if (IsLinux)
            {
                SystemdLogMessages.RuntimeDetection(
                    _logger,
                    IsSystemdService,
                    IsNotifyEnabled,
                    IsWatchdogConfigured,
                    WatchdogTimeout,
                    reason);
            }
        }

        /// <inheritdoc />
        public bool IsLinux { get; }

        /// <inheritdoc />
        public bool IsSystemdService { get; }

        /// <inheritdoc />
        public bool IsNotifyEnabled { get; }

        /// <inheritdoc />
        public TimeSpan? WatchdogTimeout { get; }

        /// <inheritdoc />
        public bool IsWatchdogConfigured { get; }

        private TimeSpan? TryReadWatchdogTimeout(out string reason)
        {
            if (!IsLinux)
            {
                reason = "non-Linux platform";
                return null;
            }

            var usecText = Environment.GetEnvironmentVariable(WatchdogUsecVariable);
            if (string.IsNullOrWhiteSpace(usecText))
            {
                reason = "WATCHDOG_USEC not set";
                return null;
            }

            if (!ulong.TryParse(usecText, NumberStyles.None, CultureInfo.InvariantCulture, out var usec) || usec == 0)
            {
                reason = "WATCHDOG_USEC missing or invalid";
                SystemdLogMessages.MalformedWatchdogUsec(_logger);
                return null;
            }

            var watchdogPidText = Environment.GetEnvironmentVariable(WatchdogPidVariable);
            if (!string.IsNullOrWhiteSpace(watchdogPidText))
            {
                if (!int.TryParse(watchdogPidText, NumberStyles.None, CultureInfo.InvariantCulture, out var watchdogPid))
                {
                    reason = "WATCHDOG_PID malformed";
                    SystemdLogMessages.MalformedWatchdogPid(_logger);
                    return null;
                }

                if (watchdogPid != Environment.ProcessId)
                {
                    reason = "WATCHDOG_PID does not match current process";
                    SystemdLogMessages.WatchdogConfiguredForDifferentPid(_logger);
                    return null;
                }
            }

            if (usec > (ulong)(TimeSpan.MaxValue.Ticks / 10))
            {
                reason = "WATCHDOG_USEC out of range";
                SystemdLogMessages.WatchdogUsecOutOfRange(_logger);
                return null;
            }

            reason = "WATCHDOG_USEC accepted";
            return TimeSpan.FromTicks((long)usec * 10);
        }
    }
}
