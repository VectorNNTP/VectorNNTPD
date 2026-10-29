using Microsoft.Extensions.Logging;

namespace VectorNNTPD.TransitService.SessionState.BytesAccounting
{
    /// <summary>Source-generated AccountBytes logs. EventIds 2700–2711.</summary>
    internal static partial class AccountByteLogMessages
    {
        [LoggerMessage(EventId = 2700, Level = LogLevel.Information, Message = "Account-byte reconciliation started interval={Interval}")]
        public static partial void ReconciliationStarted(ILogger logger, TimeSpan interval);

        [LoggerMessage(EventId = 2701, Level = LogLevel.Information, Message = "Account-byte reconciliation stopped")]
        public static partial void ReconciliationStopped(ILogger logger);

        [LoggerMessage(EventId = 2702, Level = LogLevel.Warning, Message = "Account-byte reconciliation pass failed")]
        public static partial void ReconciliationPassFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 2703, Level = LogLevel.Warning, Message = "Account-byte durable consume failed user={Username} bytes={Bytes}")]
        public static partial void DurableConsumeFailed(ILogger logger, Exception exception, string username, long bytes);

        [LoggerMessage(EventId = 2704, Level = LogLevel.Warning, Message = "Account-byte Redis apply unavailable user={Username} consumed={Consumed} mysqlRemaining={MysqlRemaining}")]
        public static partial void RedisApplyUnavailable(ILogger logger, string username, long consumed, long mysqlRemaining);

        [LoggerMessage(EventId = 2705, Level = LogLevel.Information, Message = "Account-byte quota exhausted user={Username} remaining={Remaining}")]
        public static partial void QuotaExhausted(ILogger logger, string username, long remaining);

        [LoggerMessage(EventId = 2706, Level = LogLevel.Debug, Message = "Account-byte batch reconciled user={Username} consumed={Consumed} remaining={Remaining}")]
        public static partial void BatchReconciled(ILogger logger, string username, long consumed, long remaining);

        [LoggerMessage(EventId = 2707, Level = LogLevel.Warning, Message = "Account-byte observe failed user={Username}")]
        public static partial void ObserveFailed(ILogger logger, Exception exception, string username);

        [LoggerMessage(EventId = 2708, Level = LogLevel.Warning, Message = "Account-byte durable remaining query failed user={Username}")]
        public static partial void DurableQueryFailed(ILogger logger, Exception exception, string username);

        [LoggerMessage(EventId = 2709, Level = LogLevel.Information, Message = "Account-byte final reconciliation started")]
        public static partial void FinalReconciliationStarted(ILogger logger);

        [LoggerMessage(EventId = 2710, Level = LogLevel.Warning, Message = "Account-byte final reconciliation failed")]
        public static partial void FinalReconciliationFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 2711, Level = LogLevel.Information, Message = "Account-byte Redis state deleted user={Username} existed={Existed}")]
        public static partial void ClusterStateDeleted(ILogger logger, string username, long existed);
    }
}
