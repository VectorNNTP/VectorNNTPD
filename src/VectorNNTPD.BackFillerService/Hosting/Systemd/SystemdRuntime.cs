using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting.Systemd;
using VectorNNTPD.BackFillerService.Logging;
using System.Globalization;

namespace VectorNNTPD.BackFillerService.Hosting.Systemd
{
    /// <summary>
    /// Default systemd environment detection based on OS platform checks and systemd variables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Detection distinguishes Linux from other OSes, and systemd-managed execution from ordinary
    /// Linux console sessions. <see cref="SystemdHelpers.IsSystemdService"/> is preferred for
    /// service detection; notify enablement uses the first registered <see cref="ISystemdNotifier"/>
    /// when one is present (the hosting package clears <c>NOTIFY_SOCKET</c> after constructing the notifier).
    /// Property values are fixed in the constructor and are not reread from the environment.
    /// </para>
    /// <para>
    /// Watchdog configuration is read from <c>WATCHDOG_USEC</c> and optionally <c>WATCHDOG_PID</c>.
    /// Missing or malformed values disable watchdog behaviour rather than inventing an interval.
    /// </para>
    /// </remarks>
    internal sealed class SystemdRuntime : ISystemdRuntime
    {
        /// <summary>Environment variable containing the watchdog timeout in microseconds.</summary>
        internal const string WatchdogUsecVariable = "WATCHDOG_USEC";

        /// <summary>Environment variable containing the PID expected to send watchdog keep-alives.</summary>
        internal const string WatchdogPidVariable = "WATCHDOG_PID";

        /// <summary>Logger for watchdog-configuration warnings and the Linux detection event.</summary>
        private readonly ILogger<SystemdRuntime> _logger;

        /// <summary>
        /// Reads the OS, systemd service, notifier, and watchdog environment once.
        /// </summary>
        /// <param name="systemdNotifiers">
        /// Registered notifiers. The first one, if any, supplies <see cref="IsNotifyEnabled"/>.
        /// </param>
        /// <param name="logger">Logger for detection and malformed watchdog configuration.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="systemdNotifiers"/> or <paramref name="logger"/> is <see langword="null"/>.
        /// </exception>
        /// <remarks>
        /// The Linux detection event is written only when <see cref="IsLinux"/> is true.
        /// A missing <c>WATCHDOG_USEC</c> disables the watchdog without a malformed-value warning.
        /// </remarks>
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

        /// <summary>Reads <c>WATCHDOG_USEC</c> and optional <c>WATCHDOG_PID</c> for this process.</summary>
        /// <param name="reason">
        /// Why the deadline was accepted or ignored. Always assigned, including
        /// <c>non-Linux platform</c>, <c>WATCHDOG_USEC not set</c>, <c>WATCHDOG_USEC missing or invalid</c>,
        /// <c>WATCHDOG_PID malformed</c>, <c>WATCHDOG_PID does not match current process</c>,
        /// <c>WATCHDOG_USEC out of range</c>, and <c>WATCHDOG_USEC accepted</c>.
        /// </param>
        /// <returns>
        /// The watchdog deadline, or <see langword="null"/> when keep-alives must stay disabled.
        /// </returns>
        /// <remarks>
        /// A non-positive or non-integer <c>WATCHDOG_USEC</c> is logged and ignored. A present
        /// <c>WATCHDOG_PID</c> must be an integer equal to <see cref="Environment.ProcessId"/>;
        /// an absent PID does not disable the deadline. Values that do not fit in a
        /// <see cref="TimeSpan"/> are logged and ignored. The accepted value is that many microseconds.
        /// </remarks>
        private TimeSpan? TryReadWatchdogTimeout(out string reason)
        {
            if (!IsLinux)
            {
                reason = "non-Linux platform";
                return null;
            }

            return TryParseWatchdogTimeout(
                Environment.GetEnvironmentVariable(WatchdogUsecVariable),
                Environment.GetEnvironmentVariable(WatchdogPidVariable),
                Environment.ProcessId,
                _logger,
                out reason);
        }

        /// <summary>
        /// Parses watchdog environment text the way a Linux <see cref="SystemdRuntime"/> does after the platform check.
        /// </summary>
        /// <param name="usecText">Raw <c>WATCHDOG_USEC</c> value, or null when the variable is absent.</param>
        /// <param name="pidText">Raw <c>WATCHDOG_PID</c> value, or null when the variable is absent.</param>
        /// <param name="currentProcessId">Process id that a present PID must match.</param>
        /// <param name="logger">Receives the malformed, mismatched, and out-of-range warnings.</param>
        /// <param name="reason">
        /// <c>WATCHDOG_USEC not set</c>, <c>WATCHDOG_USEC missing or invalid</c>, <c>WATCHDOG_PID malformed</c>,
        /// <c>WATCHDOG_PID does not match current process</c>, <c>WATCHDOG_USEC out of range</c>,
        /// or <c>WATCHDOG_USEC accepted</c>.
        /// </param>
        /// <returns>The deadline in microseconds, or null when keep-alives must stay disabled.</returns>
        /// <remarks>
        /// This method does not consult the operating system. <see cref="SystemdRuntime"/> returns
        /// <c>non-Linux platform</c> before calling it. A non-positive or non-integer usec is logged and ignored.
        /// An absent PID does not disable an otherwise accepted deadline.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
        internal static TimeSpan? TryParseWatchdogTimeout(
            string? usecText,
            string? pidText,
            int currentProcessId,
            ILogger logger,
            out string reason)
        {
            ArgumentNullException.ThrowIfNull(logger);

            if (string.IsNullOrWhiteSpace(usecText))
            {
                reason = "WATCHDOG_USEC not set";
                return null;
            }

            if (!ulong.TryParse(usecText, NumberStyles.None, CultureInfo.InvariantCulture, out var usec) || usec == 0)
            {
                reason = "WATCHDOG_USEC missing or invalid";
                SystemdLogMessages.MalformedWatchdogUsec(logger);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(pidText))
            {
                if (!int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var watchdogPid))
                {
                    reason = "WATCHDOG_PID malformed";
                    SystemdLogMessages.MalformedWatchdogPid(logger);
                    return null;
                }

                if (watchdogPid != currentProcessId)
                {
                    reason = "WATCHDOG_PID does not match current process";
                    SystemdLogMessages.WatchdogConfiguredForDifferentPid(logger);
                    return null;
                }
            }

            // TimeSpan ticks are 100ns; microseconds * 10 = ticks.
            if (usec > (ulong)(TimeSpan.MaxValue.Ticks / 10))
            {
                reason = "WATCHDOG_USEC out of range";
                SystemdLogMessages.WatchdogUsecOutOfRange(logger);
                return null;
            }

            reason = "WATCHDOG_USEC accepted";
            return TimeSpan.FromTicks(checked((long)usec * 10));
        }
    }
}
