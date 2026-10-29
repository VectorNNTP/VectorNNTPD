namespace VectorNNTPD.ReaderService.Redis
{
    /// <summary>Source-generated Redis infrastructure log messages.</summary>
    internal static partial class RedisLogMessages
    {
        [LoggerMessage(
            EventId = 2100,
            Level = LogLevel.Information,
            Message = "Connecting to Redis ({HostCount} endpoint(s), port {Port})")]
        public static partial void Connecting(ILogger logger, int HostCount, int Port);

        [LoggerMessage(
            EventId = 2101,
            Level = LogLevel.Information,
            Message = "Redis connection established (PING {ElapsedMs} ms)")]
        public static partial void Connected(ILogger logger, long ElapsedMs);

        [LoggerMessage(
            EventId = 2102,
            Level = LogLevel.Error,
            Message = "Redis connection failed during startup")]
        public static partial void StartupFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2103,
            Level = LogLevel.Information,
            Message = "Redis connection disposed")]
        public static partial void Disposed(ILogger logger);

        [LoggerMessage(
            EventId = 2104,
            Level = LogLevel.Error,
            Message = "Redis operation failed ({Operation})")]
        public static partial void OperationFailed(ILogger logger, Exception exception, string Operation);

        [LoggerMessage(
            EventId = 2105,
            Level = LogLevel.Warning,
            Message = "Redis unavailable; CHECK and writes will return failure until a recovery probe")]
        public static partial void BecameUnavailable(ILogger logger, Exception? exception);

        [LoggerMessage(
            EventId = 2106,
            Level = LogLevel.Information,
            Message = "Redis recovered")]
        public static partial void Recovered(ILogger logger);
    }
}
