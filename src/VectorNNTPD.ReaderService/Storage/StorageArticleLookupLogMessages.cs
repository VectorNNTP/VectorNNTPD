namespace VectorNNTPD.ReaderService.Storage
{
    /// <summary>Source-generated StorageServer article-lookup log messages.</summary>
    internal static partial class StorageArticleLookupLogMessages
    {
        [LoggerMessage(
            EventId = 2870,
            Level = LogLevel.Information,
            Message = "Storage article lookup ready (replyTo={ReplyTo}, generation={Generation})")]
        public static partial void Ready(ILogger logger, string ReplyTo, long Generation);

        [LoggerMessage(
            EventId = 2871,
            Level = LogLevel.Information,
            Message = "Storage article lookup session replaced (replyTo={ReplyTo}, generation={Generation})")]
        public static partial void SessionReplaced(ILogger logger, string ReplyTo, long Generation);

        [LoggerMessage(
            EventId = 2872,
            Level = LogLevel.Error,
            Message = "Storage article lookup session attach failed (generation={Generation})")]
        public static partial void SessionAttachFailed(ILogger logger, Exception exception, long Generation);

        [LoggerMessage(
            EventId = 2873,
            Level = LogLevel.Error,
            Message = "RabbitMQ connection is not ready for storage article lookup")]
        public static partial void ConnectionNotReady(ILogger logger);

        [LoggerMessage(
            EventId = 2874,
            Level = LogLevel.Debug,
            Message = "Storage article lookup published (requestId={RequestId}, correlationId={CorrelationId}, articleId={ArticleId}, generation={Generation})")]
        public static partial void Published(
            ILogger logger,
            Guid RequestId,
            string CorrelationId,
            string ArticleId,
            long Generation);

        [LoggerMessage(
            EventId = 2875,
            Level = LogLevel.Debug,
            Message = "Storage article lookup response ignored (correlationId={CorrelationId}, reason={Reason})")]
        public static partial void ResponseIgnored(ILogger logger, string CorrelationId, string Reason);

        [LoggerMessage(
            EventId = 2876,
            Level = LogLevel.Error,
            Message = "Storage article lookup delivery handling failed")]
        public static partial void DeliveryFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2877,
            Level = LogLevel.Information,
            Message = "Storage article lookup stopped")]
        public static partial void Stopped(ILogger logger);

        [LoggerMessage(
            EventId = 2878,
            Level = LogLevel.Debug,
            Message = "Storage article lookup skipped because the registry has no StorageServers (articleId={ArticleId})")]
        public static partial void SkippedEmptyRegistry(ILogger logger, string ArticleId);
    }
}
