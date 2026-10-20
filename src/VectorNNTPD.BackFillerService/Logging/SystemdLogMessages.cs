namespace VectorNNTPD.BackFillerService.Logging
{
    /// <summary>
    /// Source-generated structured log messages for systemd notify and watchdog events.
    /// </summary>
    /// <remarks>
    /// Each method writes at the level declared on its <see cref="LoggerMessageAttribute"/>.
    /// Host log filtering can suppress a call without changing runtime behavior.
    /// An <see cref="Exception"/> parameter is stored on the event and is not thrown by the log method.
    /// Call sites live under <c>VectorNNTPD.BackFillerService.Hosting.Systemd</c>.
    /// </remarks>
    internal static partial class SystemdLogMessages
    {
        /// <summary>
        /// Written from lifecycle-notifier start on Linux when the process is a systemd service or notify is enabled.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="NotifyEnabled">Whether the notify bridge has an enabled <c>ISystemdNotifier</c>.</param>
        /// <param name="IsSystemdService">Whether systemd service detection reported this process as a service.</param>
        [LoggerMessage(
            EventId = 5100,
            Level = LogLevel.Information,
            Message = "systemd lifecycle notifications activated (notifyEnabled={NotifyEnabled}, isSystemdService={IsSystemdService}")]
        internal static partial void LifecycleNotificationsActivated(
            ILogger logger,
            bool NotifyEnabled,
            bool IsSystemdService);

        /// <summary>Written after <c>READY=1</c> is sent, when ready notification is configured, notify is enabled, the host is healthy, and ready has not already been sent.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 5101,
            Level = LogLevel.Information,
            Message = "Reported systemd readiness after successful BackFiller host start")]
        internal static partial void ReadinessReported(ILogger logger);

        /// <summary>
        /// Written on a repeated handled readiness failure after the first error was already logged.
        /// Avoids flooding the error stream while retries remain armed.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="RetryInterval">Delay until the next readiness attempt.</param>
        [LoggerMessage(
            EventId = 5119,
            Level = LogLevel.Debug,
            Message = "systemd READY=1 still failing; retrying in {RetryInterval}")]
        internal static partial void ReadinessRetryScheduled(ILogger logger, TimeSpan RetryInterval);

        /// <summary>Written after <c>STOPPING=1</c> is sent, when stopping notification is configured, notify is enabled, and stopping has not already been sent.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 5102,
            Level = LogLevel.Information,
            Message = "Reported systemd STOPPING notification for graceful shutdown")]
        internal static partial void StoppingReported(ILogger logger);

        /// <summary>Written after a lifecycle <c>STATUS=</c> notification is sent and the status text changed.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Status">Status text that was sent. Duplicate text is not logged again.</param>
        [LoggerMessage(
            EventId = 5103,
            Level = LogLevel.Information,
            Message = "Reported systemd status: {Status}")]
        internal static partial void StatusReported(ILogger logger, string Status);

        /// <summary>Written by the notify bridge after <c>ISystemdNotifier.Notify</c> returns.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Notification">Label passed by the bridge, such as <c>READY=1</c>, <c>STOPPING=1</c>, <c>STATUS</c>, or <c>WATCHDOG=1</c>.</param>
        [LoggerMessage(
            EventId = 5104,
            Level = LogLevel.Debug,
            Message = "Sent systemd notification {Notification}")]
        internal static partial void NotificationSent(ILogger logger, string Notification);

        /// <summary>Written when a notify-bridge or lifecycle notification throws. The bridge swallows the failure; lifecycle callers log it and do not rethrow.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Notification failure. Logged with the event and not thrown by this method.</param>
        /// <param name="Notification">Label of the notification that failed, such as <c>READY=1</c>, <c>STOPPING=1</c>, or <c>STATUS</c>.</param>
        [LoggerMessage(
            EventId = 5105,
            Level = LogLevel.Error,
            Message = "Failed to send systemd notification {Notification}")]
        internal static partial void NotificationFailed(ILogger logger, Exception exception, string Notification);

        /// <summary>Written from <c>SystemdRuntime</c> construction when the process is running on Linux.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="IsSystemdService">Whether systemd service detection reported this process as a service.</param>
        /// <param name="NotifyEnabled">Whether an enabled systemd notifier was registered.</param>
        /// <param name="WatchdogConfigured">Whether a watchdog timeout was accepted.</param>
        /// <param name="WatchdogTimeout">Accepted watchdog timeout, or null when watchdog configuration was not accepted.</param>
        /// <param name="Detail">Watchdog parse reason, such as a missing <c>WATCHDOG_USEC</c> or an accepted value.</param>
        [LoggerMessage(
            EventId = 5106,
            Level = LogLevel.Information,
            Message = "systemd runtime detection: isSystemdService={IsSystemdService}, notifyEnabled={NotifyEnabled}, watchdogConfigured={WatchdogConfigured}, watchdogTimeout={WatchdogTimeout}, detail={Detail}")]
        internal static partial void RuntimeDetection(
            ILogger logger,
            bool IsSystemdService,
            bool NotifyEnabled,
            bool WatchdogConfigured,
            TimeSpan? WatchdogTimeout,
            string Detail);

        /// <summary>Written when <c>WATCHDOG_USEC</c> is present but is not a positive integer.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 5107,
            Level = LogLevel.Warning,
            Message = "Ignoring malformed systemd watchdog configuration (WATCHDOG_USEC is not a positive integer)")]
        internal static partial void MalformedWatchdogUsec(ILogger logger);

        /// <summary>Written when <c>WATCHDOG_PID</c> is present but is not an integer.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 5108,
            Level = LogLevel.Warning,
            Message = "Ignoring systemd watchdog configuration because WATCHDOG_PID is malformed")]
        internal static partial void MalformedWatchdogPid(ILogger logger);

        /// <summary>Written when <c>WATCHDOG_PID</c> parses and is not the current process id. Watchdog configuration is then ignored.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 5109,
            Level = LogLevel.Information,
            Message = "systemd watchdog is configured for a different PID; watchdog keep-alives will remain disabled")]
        internal static partial void WatchdogConfiguredForDifferentPid(ILogger logger);

        /// <summary>Written when <c>WATCHDOG_USEC</c> cannot be represented as a <see cref="TimeSpan"/>.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 5110,
            Level = LogLevel.Warning,
            Message = "Ignoring systemd watchdog configuration because WATCHDOG_USEC is out of range")]
        internal static partial void WatchdogUsecOutOfRange(ILogger logger);

        /// <summary>Written when the watchdog service starts and activation conditions are not met.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Reason">
        /// Why the loop stayed inactive: watchdog disabled in options, non-Linux, <c>WATCHDOG_USEC</c> not configured for this process, or notify not enabled.
        /// </param>
        [LoggerMessage(
            EventId = 5111,
            Level = LogLevel.Information,
            Message = "systemd watchdog remain disabled ({Reason})")]
        internal static partial void WatchdogRemainDisabled(ILogger logger, string Reason);

        /// <summary>Written after the heartbeat interval is calculated and before the watchdog loop starts.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="WatchdogTimeout">Accepted <c>WATCHDOG_USEC</c> timeout.</param>
        /// <param name="HeartbeatInterval">Interval passed to the watchdog timer.</param>
        [LoggerMessage(
            EventId = 5112,
            Level = LogLevel.Information,
            Message = "systemd watchdog activated. Deadline={WatchdogTimeout}, heartbeatInterval={HeartbeatInterval}")]
        internal static partial void WatchdogActivated(
            ILogger logger,
            TimeSpan WatchdogTimeout,
            TimeSpan HeartbeatInterval);

        /// <summary>Written on a watchdog tick when the application is not healthy for watchdog. No keep-alive is sent for that tick.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 5113,
            Level = LogLevel.Debug,
            Message = "Skipping systemd watchdog keep-alive because the application is unhealthy")]
        internal static partial void WatchdogKeepAliveSkippedUnhealthy(ILogger logger);

        /// <summary>Written after a watchdog keep-alive notification returns.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 5114,
            Level = LogLevel.Trace,
            Message = "Sent systemd watchdog keep-alive")]
        internal static partial void WatchdogKeepAliveSent(ILogger logger);

        /// <summary>Written when a watchdog keep-alive notification throws. The loop logs the failure and continues.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Notify failure. Logged with the event and not thrown by this method.</param>
        [LoggerMessage(
            EventId = 5115,
            Level = LogLevel.Error,
            Message = "systemd watchdog notification failed")]
        internal static partial void WatchdogNotificationFailed(ILogger logger, Exception exception);

        /// <summary>Written when the watchdog loop observes cancellation of its linked stop token.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 5116,
            Level = LogLevel.Information,
            Message = "systemd watchdog deactivated due to shutdown")]
        internal static partial void WatchdogDeactivated(ILogger logger);

        /// <summary>Written when the watchdog loop fails for a reason other than that cancellation. The caller then clears the active flag, stops the host, and rethrows.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Loop failure. Logged with the event and not thrown by this method.</param>
        [LoggerMessage(
            EventId = 5117,
            Level = LogLevel.Critical,
            Message = "systemd watchdog loop failed unexpectedly; requesting host stop")]
        internal static partial void WatchdogLoopFailed(ILogger logger, Exception exception);

        /// <summary>Written at the start of watchdog <c>StopAsync</c>, including when the loop never became active.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 5118,
            Level = LogLevel.Information,
            Message = "systemd watchdog stopping")]
        internal static partial void WatchdogStopping(ILogger logger);
    }
}
