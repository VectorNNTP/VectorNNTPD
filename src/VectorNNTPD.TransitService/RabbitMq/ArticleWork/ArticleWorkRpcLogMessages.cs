using Microsoft.Extensions.Logging;

namespace VectorNNTPD.TransitService.RabbitMq.ArticleWork
{
    /// <summary>Source-generated article-work RPC log messages. Never includes payloads or credentials.</summary>
    internal static partial class ArticleWorkRpcLogMessages
    {
        [LoggerMessage(
            EventId = 2830,
            Level = LogLevel.Information,
            Message = "Article-work RPC ready (replyTo={ReplyTo}, generation={Generation})")]
        public static partial void Ready(ILogger logger, string ReplyTo, long Generation);

        [LoggerMessage(
            EventId = 2831,
            Level = LogLevel.Information,
            Message = "Article-work RPC session replaced (replyTo={ReplyTo}, generation={Generation})")]
        public static partial void SessionReplaced(ILogger logger, string ReplyTo, long Generation);

        [LoggerMessage(
            EventId = 2832,
            Level = LogLevel.Error,
            Message = "Article-work RPC is not ready")]
        public static partial void NotReady(ILogger logger);

        [LoggerMessage(
            EventId = 2833,
            Level = LogLevel.Debug,
            Message = "Article-work request published (requestId={RequestId}, correlationId={CorrelationId}, messageId={MessageId}, exchange={Exchange}, generation={Generation})")]
        public static partial void Published(
            ILogger logger,
            Guid RequestId,
            string CorrelationId,
            string MessageId,
            string Exchange,
            long Generation);

        [LoggerMessage(
            EventId = 2834,
            Level = LogLevel.Warning,
            Message = "Article-work publication failed (requestId={RequestId}, correlationId={CorrelationId}, messageId={MessageId}, exchange={Exchange}, generation={Generation})")]
        public static partial void PublishFailed(
            ILogger logger,
            Exception exception,
            Guid RequestId,
            string CorrelationId,
            string MessageId,
            string Exchange,
            long Generation);

        [LoggerMessage(
            EventId = 2835,
            Level = LogLevel.Information,
            Message = "Article-work RPC completed (requestId={RequestId}, messageId={MessageId}, outcome={Outcome}, exchange={Exchange}, elapsedMs={ElapsedMs})")]
        public static partial void Completed(
            ILogger logger,
            Guid RequestId,
            string MessageId,
            string Outcome,
            string? Exchange,
            long ElapsedMs);

        [LoggerMessage(
            EventId = 2836,
            Level = LogLevel.Debug,
            Message = "Article-work RPC response ignored (correlationId={CorrelationId}, reason={Reason})")]
        public static partial void ResponseIgnored(ILogger logger, string CorrelationId, string Reason);

        [LoggerMessage(
            EventId = 2837,
            Level = LogLevel.Warning,
            Message = "Article-work RPC consumer callback failed")]
        public static partial void ConsumerCallbackFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2838,
            Level = LogLevel.Error,
            Message = "Article-work RPC session attach failed (generation={Generation})")]
        public static partial void SessionAttachFailed(ILogger logger, Exception exception, long Generation);

        [LoggerMessage(
            EventId = 2839,
            Level = LogLevel.Warning,
            Message = "ARTICLE message-id RPC lookup failed (messageId={MessageId})")]
        public static partial void ArticleLookupFailed(ILogger logger, Exception exception, string MessageId);

        [LoggerMessage(
            EventId = 2840,
            Level = LogLevel.Warning,
            Message = "Article-work Management API availability refresh failed; retaining last successful snapshot")]
        public static partial void ManagementAvailabilityRefreshFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2841,
            Level = LogLevel.Debug,
            Message = "Article-work scheduler attempt (requestId={RequestId}, messageId={MessageId}, backbone={Backbone}, consumers={Consumers}, attempt={Attempt})")]
        public static partial void SchedulerAttempt(
            ILogger logger,
            Guid RequestId,
            string MessageId,
            string Backbone,
            int Consumers,
            int Attempt);

        [LoggerMessage(
            EventId = 2842,
            Level = LogLevel.Debug,
            Message = "Article-work availability snapshot refreshed (eligibleCount={EligibleCount})")]
        public static partial void AvailabilitySnapshotRefreshed(ILogger logger, int EligibleCount);
    }
}
