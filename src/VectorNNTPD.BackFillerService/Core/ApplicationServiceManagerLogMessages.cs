namespace VectorNNTPD.BackFillerService.Core
{
    /// <summary>Source-generated log messages for <see cref="ApplicationServiceManager"/>.</summary>
    /// <remarks>
    /// Each method writes at the level declared on its <see cref="LoggerMessageAttribute"/>.
    /// Host log filtering can suppress a call without changing runtime behavior.
    /// Exception parameters are recorded on the log event. These methods do not throw those exceptions.
    /// </remarks>
    internal static partial class ApplicationServiceManagerLogMessages
    {
        /// <summary>
        /// Written immediately before <see cref="ApplicationServiceManager"/> calls
        /// <see cref="VectorNNTPD.Common.Core.IApplicationService.StartAsync"/> on one registered service.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="serviceName">Name of the service about to start.</param>
        /// <param name="index">One-based position of the service in registration order.</param>
        /// <param name="count">Number of registered services.</param>
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting application service {ServiceName} ({Index}/{Count})")]
        internal static partial void StartingService(ILogger logger, string serviceName, int index, int count);

        /// <summary>Written after a service start returns and that service is recorded as started.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="serviceName">Name of the service that started.</param>
        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Started application service {ServiceName}")]
        internal static partial void ServiceStarted(ILogger logger, string serviceName);

        /// <summary>
        /// Written when a service start throws an exception that is not cancellation of the caller token.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">The start failure. Logged, not thrown by this method.</param>
        /// <param name="serviceName">Name of the service that failed to start.</param>
        [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Application service {ServiceName} failed to start")]
        internal static partial void ServiceStartupFailed(ILogger logger, Exception exception, string serviceName);

        /// <summary>Written when a service start is canceled by the caller token.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="serviceName">Name of the service whose start was canceled.</param>
        [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Application service {ServiceName} startup was canceled")]
        internal static partial void ServiceStartupCanceled(ILogger logger, string serviceName);

        /// <summary>Written immediately before a started service is stopped during coordinated shutdown.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="serviceName">Name of the service about to stop.</param>
        [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Stopping application service {ServiceName}")]
        internal static partial void StoppingService(ILogger logger, string serviceName);

        /// <summary>
        /// Written when a service stop throws an exception other than <see cref="OperationCanceledException"/>.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">The stop failure. Logged, not thrown by this method.</param>
        /// <param name="serviceName">Name of the service that failed to stop.</param>
        [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Application service {ServiceName} failed to stop")]
        internal static partial void ServiceStopFailed(ILogger logger, Exception exception, string serviceName);

        /// <summary>
        /// Written when a service stop throws <see cref="OperationCanceledException"/> because the caller token was canceled.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="serviceName">Name of the service whose stop was canceled.</param>
        [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Application service {ServiceName} stop was canceled")]
        internal static partial void ServiceStopCanceled(ILogger logger, string serviceName);

        /// <summary>
        /// Written when a service stop throws <see cref="OperationCanceledException"/> because the shutdown budget elapsed,
        /// and this is the first service to cross the budget.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="timeout">Configured shutdown grace period.</param>
        /// <param name="serviceName">Name of the service whose stop was canceled by the budget.</param>
        [LoggerMessage(
            EventId = 11,
            Level = LogLevel.Error,
            Message = "Graceful shutdown timed out after {Timeout} while stopping application service {ServiceName}")]
        internal static partial void GracefulShutdownTimedOut(ILogger logger, TimeSpan timeout, string serviceName);

        /// <summary>
        /// Written when a later service is offered an already-canceled stop token after the budget was exhausted,
        /// and that stop throws cancellation.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="serviceName">Name of the service whose stop was aborted.</param>
        [LoggerMessage(
            EventId = 12,
            Level = LogLevel.Warning,
            Message = "Application service {ServiceName} stop aborted after graceful shutdown budget was already exhausted")]
        internal static partial void ServiceStopAbortedAfterBudget(ILogger logger, string serviceName);

        /// <summary>
        /// Written when a service stop throws during startup rollback. Rollback swallows this exception.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">The rollback failure. Logged, not thrown by this method.</param>
        /// <param name="serviceName">Name of the service whose rollback stop failed.</param>
        [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Application service {ServiceName} rollback failed")]
        internal static partial void ServiceRollbackFailed(ILogger logger, Exception exception, string serviceName);

        /// <summary>Written when a watched service execution faults.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">
        /// The execution's base exception, or a stand-in when the faulted task exposes none. Logged, not thrown by this method.
        /// </param>
        /// <param name="serviceName">Name of the service whose execution faulted.</param>
        [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "Application service {ServiceName} execution faulted")]
        internal static partial void ServiceExecutionFaulted(ILogger logger, Exception exception, string serviceName);

        /// <summary>
        /// Written when a watched service execution runs to completion or is canceled without the watch token being canceled.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="serviceName">Name of the service whose execution ended.</param>
        [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Application service {ServiceName} execution completed unexpectedly")]
        internal static partial void ServiceExecutionEnded(ILogger logger, string serviceName);
    }
}
