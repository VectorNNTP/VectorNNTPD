using Microsoft.Extensions.Logging;

namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>Structured silent-consumer monitor events (9100+).</summary>
    internal static partial class SilentConsumerMonitorLogMessages
    {
        /// <summary>Event 9100: monitor loop starting (or exiting immediately when disabled).</summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="instanceId">Instance identity stamped on the event (typically FQDN).</param>
        /// <param name="enabled">Whether the monitor will run cycles.</param>
        /// <param name="queueCount">Shared queue target count at first start log (0 when disabled).</param>
        [LoggerMessage(
            EventId = 9100,
            Level = LogLevel.Information,
            Message = "Silent-consumer monitor starting instanceId={InstanceId} enabled={Enabled} sharedQueueTargets={QueueCount}")]
        public static partial void Starting(ILogger logger, string instanceId, bool enabled, int queueCount);

        /// <summary>Event 9101: monitor loop stopped (finally after cancel/disable/exit).</summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="instanceId">Instance identity stamped on the event.</param>
        [LoggerMessage(
            EventId = 9101,
            Level = LogLevel.Information,
            Message = "Silent-consumer monitor stopped instanceId={InstanceId}")]
        public static partial void Stopped(ILogger logger, string instanceId);

        /// <summary>Event 9110: observation transitioned to Open.</summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="scope">Instance vs shared queue.</param>
        /// <param name="condition">Condition that opened.</param>
        /// <param name="target">Session key, <c>local</c>, or queue name.</param>
        /// <param name="instanceId">Instance identity.</param>
        /// <param name="persistenceSeconds">Sustained Active duration at open, in seconds.</param>
        /// <param name="detail">Non-secret evaluator detail.</param>
        [LoggerMessage(
            EventId = 9110,
            Level = LogLevel.Warning,
            Message = "Silent-consumer observation opened scope={Scope} condition={Condition} target={Target} instanceId={InstanceId} persistenceSeconds={PersistenceSeconds} detail={Detail}")]
        public static partial void ObservationOpened(
            ILogger logger,
            ObservationScope scope,
            SilentConsumerCondition condition,
            string target,
            string instanceId,
            double persistenceSeconds,
            string? detail);

        /// <summary>Event 9111: open observation continued (rate-limited per key).</summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="scope">Instance vs shared queue.</param>
        /// <param name="condition">Condition still open.</param>
        /// <param name="target">Session key, <c>local</c>, or queue name.</param>
        /// <param name="instanceId">Instance identity.</param>
        /// <param name="persistenceSeconds">Current persistence seconds.</param>
        /// <param name="detail">Non-secret evaluator detail.</param>
        [LoggerMessage(
            EventId = 9111,
            Level = LogLevel.Information,
            Message = "Silent-consumer observation continued scope={Scope} condition={Condition} target={Target} instanceId={InstanceId} persistenceSeconds={PersistenceSeconds} detail={Detail}")]
        public static partial void ObservationContinued(
            ILogger logger,
            ObservationScope scope,
            SilentConsumerCondition condition,
            string target,
            string instanceId,
            double persistenceSeconds,
            string? detail);

        /// <summary>Event 9112: observation resolved (Inactive window or catalog prune).</summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="scope">Instance vs shared queue.</param>
        /// <param name="condition">Condition that resolved.</param>
        /// <param name="target">Session key, <c>local</c>, or queue name.</param>
        /// <param name="instanceId">Instance identity.</param>
        /// <param name="detail">Non-secret resolve detail.</param>
        [LoggerMessage(
            EventId = 9112,
            Level = LogLevel.Information,
            Message = "Silent-consumer observation resolved scope={Scope} condition={Condition} target={Target} instanceId={InstanceId} detail={Detail}")]
        public static partial void ObservationResolved(
            ILogger logger,
            ObservationScope scope,
            SilentConsumerCondition condition,
            string target,
            string instanceId,
            string? detail);

        /// <summary>
        /// Event 9113: rate-limited when an already-open observation receives <see cref="ConditionSignal.Unknown"/>.
        /// Unknown must not be read as recovery; the open phase is intentionally retained.
        /// </summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="scope">Instance vs shared queue.</param>
        /// <param name="condition">Condition that remains open.</param>
        /// <param name="target">Session key, <c>local</c>, or queue name.</param>
        /// <param name="instanceId">Instance identity.</param>
        /// <param name="detail">Non-secret unavailability detail.</param>
        [LoggerMessage(
            EventId = 9113,
            Level = LogLevel.Information,
            Message = "Silent-consumer observation unavailable scope={Scope} condition={Condition} target={Target} instanceId={InstanceId} detail={Detail}; open alert retained (Unknown is not recovery)")]
        public static partial void ObservationUnavailableWhileOpen(
            ILogger logger,
            ObservationScope scope,
            SilentConsumerCondition condition,
            string target,
            string instanceId,
            string? detail);

        /// <summary>Event 9120: Management polling crossed the consecutive-failure degraded threshold.</summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="instanceId">Instance identity.</param>
        /// <param name="consecutiveFailures">Failure count at the moment degraded latched.</param>
        /// <param name="reason">Non-secret failure reason.</param>
        [LoggerMessage(
            EventId = 9120,
            Level = LogLevel.Warning,
            Message = "Silent-consumer monitor Management degraded instanceId={InstanceId} consecutiveFailures={ConsecutiveFailures} reason={Reason}")]
        public static partial void ManagementDegraded(
            ILogger logger,
            string instanceId,
            int consecutiveFailures,
            string reason);

        /// <summary>Event 9121: Management recovered after a degraded latch or after Event 9123 readiness returned.</summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="instanceId">Instance identity.</param>
        [LoggerMessage(
            EventId = 9121,
            Level = LogLevel.Information,
            Message = "Silent-consumer monitor Management recovered instanceId={InstanceId}")]
        public static partial void ManagementRecovered(ILogger logger, string instanceId);

        /// <summary>Event 9122: one Management poll failed (debug); does not by itself mean degraded.</summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="instanceId">Instance identity.</param>
        /// <param name="queueName">Queue that was polled.</param>
        /// <param name="kind">Failure kind or exception type name.</param>
        /// <param name="reason">Non-secret reason.</param>
        [LoggerMessage(
            EventId = 9122,
            Level = LogLevel.Debug,
            Message = "Silent-consumer Management poll failed instanceId={InstanceId} queue={QueueName} kind={Kind} reason={Reason}")]
        public static partial void ManagementPollFailed(
            ILogger logger,
            string instanceId,
            string queueName,
            string kind,
            string reason);

        /// <summary>
        /// Event 9123: emitted once when Management is not ready to poll (missing BaseUrl/credentials).
        /// Does not mean a shared alert received <see cref="ConditionSignal.Unknown"/>; polling did not run.
        /// Any already-open shared alerts are left unchanged until polling resumes or the catalog target is removed.
        /// </summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="instanceId">Instance identity.</param>
        /// <param name="reason">Non-secret readiness failure reason.</param>
        [LoggerMessage(
            EventId = 9123,
            Level = LogLevel.Warning,
            Message = "Silent-consumer Management unavailable instanceId={InstanceId} reason={Reason}; instance-local monitoring continues; Management is not being polled, so shared alerts are neither refreshed nor auto-resolved (unavailable is not recovery)")]
        public static partial void ManagementUnavailable(ILogger logger, string instanceId, string reason);
    }
}
