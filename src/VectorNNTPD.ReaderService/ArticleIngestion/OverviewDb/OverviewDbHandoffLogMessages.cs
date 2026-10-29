using Microsoft.Extensions.Logging;

namespace VectorNNTPD.ReaderService.ArticleIngestion.OverviewDb
{
    /// <summary>Source-generated OverviewDB handoff log messages.</summary>
    internal static partial class OverviewDbHandoffLogMessages
    {
        [LoggerMessage(
            EventId = 2100,
            Level = LogLevel.Error,
            Message = "OverviewDB RabbitMQ handoff failed for {MessageId} ({Bytes} payload bytes)")]
        public static partial void PublishFailed(ILogger logger, Exception exception, string MessageId, int Bytes);

        [LoggerMessage(
            EventId = 2101,
            Level = LogLevel.Warning,
            Message = "OverviewDB handoff was requeued for {MessageId}")]
        public static partial void Requeued(ILogger logger, string MessageId);

        [LoggerMessage(
            EventId = 2102,
            Level = LogLevel.Error,
            Message = "OverviewDB handoff could not be requeued for {MessageId}; queue is unavailable")]
        public static partial void RequeueUnavailable(ILogger logger, string MessageId);
    }
}
