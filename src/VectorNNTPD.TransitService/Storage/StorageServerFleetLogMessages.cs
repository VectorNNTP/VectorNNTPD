namespace VectorNNTPD.TransitService.Storage
{
    /// <summary>Source-generated StorageServer fleet consumer log messages.</summary>
    internal static partial class StorageServerFleetLogMessages
    {
        [LoggerMessage(
            EventId = 2860,
            Level = LogLevel.Information,
            Message = "StorageServer fleet consumer ready (queue={Queue}, generation={Generation})")]
        public static partial void Ready(ILogger logger, string Queue, long Generation);

        [LoggerMessage(
            EventId = 2861,
            Level = LogLevel.Information,
            Message = "StorageServer fleet consumer session replaced (queue={Queue}, generation={Generation})")]
        public static partial void SessionReplaced(ILogger logger, string Queue, long Generation);

        [LoggerMessage(
            EventId = 2862,
            Level = LogLevel.Error,
            Message = "StorageServer fleet consumer session attach failed (generation={Generation})")]
        public static partial void SessionAttachFailed(ILogger logger, Exception exception, long Generation);

        [LoggerMessage(
            EventId = 2863,
            Level = LogLevel.Error,
            Message = "RabbitMQ connection is not ready for StorageServer fleet consume")]
        public static partial void ConnectionNotReady(ILogger logger);

        [LoggerMessage(
            EventId = 2864,
            Level = LogLevel.Warning,
            Message = "Rejected StorageServer advertisement payload ({Reason})")]
        public static partial void PayloadRejected(ILogger logger, string Reason);

        [LoggerMessage(
            EventId = 2865,
            Level = LogLevel.Error,
            Message = "StorageServer fleet delivery handling failed")]
        public static partial void DeliveryFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2866,
            Level = LogLevel.Information,
            Message = "StorageServer fleet consumer stopped")]
        public static partial void Stopped(ILogger logger);
    }
}
