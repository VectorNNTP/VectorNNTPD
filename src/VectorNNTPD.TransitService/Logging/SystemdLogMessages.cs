namespace VectorNNTPD.TransitService.Logging
{
    /// <summary>
    /// Source-generated structured log messages for systemd notify and watchdog events.
    /// </summary>
    internal static partial class SystemdLogMessages
    {
        [LoggerMessage(
            EventId = 1300,
            Level = LogLevel.Information,
            Message = "systemd lifecycle notifications activated (notifyEnabled={NotifyEnabled}, isSystemdService={IsSystemdService}")]
        public static partial void LifecycleNotificationsActivated(
            ILogger logger,
            bool NotifyEnabled,
            bool IsSystemdService);

        [LoggerMessage(
            EventId = 1301,
            Level = LogLevel.Information,
            Message = "Reported systemd readiness after successful application initialization")]
        public static partial void ReadinessReported(ILogger logger);

        [LoggerMessage(
            EventId = 1302,
            Level = LogLevel.Information,
            Message = "Reported systemd STOPPING notification for graceful shutdown")]
        public static partial void StoppingReported(ILogger logger);

        [LoggerMessage(
            EventId = 1303,
            Level = LogLevel.Information,
            Message = "Reported systemd status: {Status}")]
        public static partial void StatusReported(ILogger logger, string Status);

        [LoggerMessage(
            EventId = 1304,
            Level = LogLevel.Debug,
            Message = "Sent systemd notification {Notification}")]
        public static partial void NotificationSent(ILogger logger, string Notification);

        [LoggerMessage(
            EventId = 1305,
            Level = LogLevel.Error,
            Message = "Failed to send systemd notification {Notification}")]
        public static partial void NotificationFailed(ILogger logger, Exception exception, string Notification);

        [LoggerMessage(
            EventId = 1306,
            Level = LogLevel.Information,
            Message = "systemd runtime detection: isSystemdService={IsSystemdService}, notifyEnabled={NotifyEnabled}, watchdogConfigured={WatchdogConfigured}, watchdogTimeout={WatchdogTimeout}, detail={Detail}")]
        public static partial void RuntimeDetection(
            ILogger logger,
            bool IsSystemdService,
            bool NotifyEnabled,
            bool WatchdogConfigured,
            TimeSpan? WatchdogTimeout,
            string Detail);

        [LoggerMessage(
            EventId = 1307,
            Level = LogLevel.Warning,
            Message = "Ignoring malformed systemd watchdog configuration (WATCHDOG_USEC is not a positive integer)")]
        public static partial void MalformedWatchdogUsec(ILogger logger);

        [LoggerMessage(
            EventId = 1308,
            Level = LogLevel.Warning,
            Message = "Ignoring systemd watchdog configuration because WATCHDOG_PID is malformed")]
        public static partial void MalformedWatchdogPid(ILogger logger);

        [LoggerMessage(
            EventId = 1309,
            Level = LogLevel.Information,
            Message = "systemd watchdog is configured for a different PID; watchdog keep-alives will remain disabled")]
        public static partial void WatchdogConfiguredForDifferentPid(ILogger logger);

        [LoggerMessage(
            EventId = 1310,
            Level = LogLevel.Warning,
            Message = "Ignoring systemd watchdog configuration because WATCHDOG_USEC is out of range")]
        public static partial void WatchdogUsecOutOfRange(ILogger logger);

        [LoggerMessage(
            EventId = 1311,
            Level = LogLevel.Information,
            Message = "systemd watchdog remain disabled ({Reason})")]
        public static partial void WatchdogRemainDisabled(ILogger logger, string Reason);

        [LoggerMessage(
            EventId = 1312,
            Level = LogLevel.Information,
            Message = "systemd watchdog activated. Deadline={WatchdogTimeout}, heartbeatInterval={HeartbeatInterval}")]
        public static partial void WatchdogActivated(
            ILogger logger,
            TimeSpan WatchdogTimeout,
            TimeSpan HeartbeatInterval);

        [LoggerMessage(
            EventId = 1313,
            Level = LogLevel.Debug,
            Message = "Skipping systemd watchdog keep-alive because the application is unhealthy")]
        public static partial void WatchdogKeepAliveSkippedUnhealthy(ILogger logger);

        [LoggerMessage(
            EventId = 1314,
            Level = LogLevel.Trace,
            Message = "Sent systemd watchdog keep-alive")]
        public static partial void WatchdogKeepAliveSent(ILogger logger);

        [LoggerMessage(
            EventId = 1315,
            Level = LogLevel.Error,
            Message = "systemd watchdog notification failed")]
        public static partial void WatchdogNotificationFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 1316,
            Level = LogLevel.Information,
            Message = "systemd watchdog deactivated due to shutdown")]
        public static partial void WatchdogDeactivated(ILogger logger);

        [LoggerMessage(
            EventId = 1317,
            Level = LogLevel.Critical,
            Message = "systemd watchdog loop failed unexpectedly; requesting host stop")]
        public static partial void WatchdogLoopFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 1318,
            Level = LogLevel.Information,
            Message = "systemd watchdog stopping")]
        public static partial void WatchdogStopping(ILogger logger);
    }
}
