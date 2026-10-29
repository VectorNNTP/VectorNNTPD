namespace VectorNNTPD.TransitService.Authentication
{
    /// <summary>Source-generated account-cache diagnostics. Never includes secrets or payloads.</summary>
    internal static partial class AccountCacheLogMessages
    {
        [LoggerMessage(EventId = 2340, Level = LogLevel.Debug, Message = "Account cache HIT accountHash={AccountHash}")]
        public static partial void Hit(ILogger logger, string accountHash);

        [LoggerMessage(EventId = 2341, Level = LogLevel.Debug, Message = "Account cache MISS accountHash={AccountHash}")]
        public static partial void Miss(ILogger logger, string accountHash);

        [LoggerMessage(EventId = 2342, Level = LogLevel.Debug, Message = "Account cache unavailable; falling back to MySQL accountHash={AccountHash}")]
        public static partial void Unavailable(ILogger logger, string accountHash);

        [LoggerMessage(EventId = 2343, Level = LogLevel.Debug, Message = "Account cache populate skipped accountHash={AccountHash}")]
        public static partial void PopulateSkipped(ILogger logger, string accountHash);

        [LoggerMessage(EventId = 2344, Level = LogLevel.Debug, Message = "Account cache payload rejected accountHash={AccountHash}")]
        public static partial void PayloadRejected(ILogger logger, string accountHash);
    }
}
