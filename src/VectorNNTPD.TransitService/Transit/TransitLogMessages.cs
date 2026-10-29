namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>Source-generated Transit configuration and DNS log messages.</summary>
    internal static partial class TransitLogMessages
    {
        [LoggerMessage(
            EventId = 1702,
            Level = LogLevel.Error,
            Message = "Ignoring invalid Transit configuration reload; last valid snapshot remains active. {Failure}")]
        public static partial void InvalidReloadIgnored(ILogger logger, string Failure);

        [LoggerMessage(
            EventId = 1703,
            Level = LogLevel.Information,
            Message = "Transit peer configuration reloaded ({Count} peer(s))")]
        public static partial void ConfigurationReloaded(ILogger logger, int Count);

        [LoggerMessage(
            EventId = 1704,
            Level = LogLevel.Information,
            Message = "Transit AllowFrom DNS {Hostname} resolved to {Count} address(es); next refresh at {NextRefreshUtc}")]
        public static partial void DnsResolved(ILogger logger, string Hostname, int Count, DateTimeOffset NextRefreshUtc);

        [LoggerMessage(
            EventId = 1705,
            Level = LogLevel.Warning,
            Message = "Transit peer {PeerId} ({PeerName}) AllowFrom DNS resolution failed for {Hostname}: {Reason}")]
        public static partial void DnsResolutionFailed(
            ILogger logger,
            string PeerId,
            string PeerName,
            string Hostname,
            string Reason);

        [LoggerMessage(
            EventId = 1706,
            Level = LogLevel.Error,
            Message = "Transit AllowFrom DNS refresh loop terminated unexpectedly")]
        public static partial void RefreshLoopTerminated(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2270,
            Level = LogLevel.Error,
            Message = "Transit catalogue initial load failed")]
        public static partial void CatalogueInitialLoadFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2271,
            Level = LogLevel.Warning,
            Message = "Transit catalogue refresh failed; last published snapshot remains active")]
        public static partial void CatalogueRefreshFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2272,
            Level = LogLevel.Information,
            Message = "Transit catalogue published {PublicationId} ({PeerCount} peer(s))")]
        public static partial void CataloguePublished(ILogger logger, long PublicationId, int PeerCount);
    }
}
