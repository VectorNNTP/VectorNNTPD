namespace VectorNNTPD.ReaderService.History
{
    /// <summary>Source-generated HistoryDB log messages.</summary>
    internal static partial class HistoryLogMessages
    {
        [LoggerMessage(
            EventId = 2200,
            Level = LogLevel.Warning,
            Message = "HistoryDB Redis write queue is full; local history was recorded and the Redis write was dropped")]
        public static partial void WriteQueueFull(ILogger logger);

        [LoggerMessage(
            EventId = 2201,
            Level = LogLevel.Error,
            Message = "HistoryDB Redis write failed")]
        public static partial void RedisWriteFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2202,
            Level = LogLevel.Warning,
            Message = "HistoryDB Redis lookup failed; returning 431")]
        public static partial void RedisLookupFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2203,
            Level = LogLevel.Information,
            Message = "HistoryDB write worker started (queue capacity {Capacity}, retention {Retention})")]
        public static partial void WriterStarted(ILogger logger, int Capacity, TimeSpan Retention);

        [LoggerMessage(
            EventId = 2204,
            Level = LogLevel.Information,
            Message = "HistoryDB write worker stopped")]
        public static partial void WriterStopped(ILogger logger);

        [LoggerMessage(
            EventId = 2205,
            Level = LogLevel.Warning,
            Message = "HistoryDB write worker stopped with {Queued} queued write(s) remaining")]
        public static partial void WriterStoppedWithQueued(ILogger logger, int Queued);

        [LoggerMessage(
            EventId = 2206,
            Level = LogLevel.Information,
            Message = "HistoryDB maintenance worker started (interval {Interval})")]
        public static partial void MaintenanceStarted(ILogger logger, TimeSpan Interval);

        [LoggerMessage(
            EventId = 2207,
            Level = LogLevel.Information,
            Message = "HistoryDB maintenance worker stopped")]
        public static partial void MaintenanceStopped(ILogger logger);
    }
}
