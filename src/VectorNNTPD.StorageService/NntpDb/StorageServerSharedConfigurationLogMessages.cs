namespace VectorNNTPD.StorageService.NntpDb
{

    /// <summary>Source-generated logs for the StorageServer <c>nntpsharedconfig</c> load.</summary>
    internal static partial class StorageServerSharedConfigurationLogMessages
    {
        [LoggerMessage(
            EventId = 2270,
            Level = LogLevel.Information,
            Message = "Loading initial nntpsharedconfig")]
        public static partial void InitialLoadStarted(ILogger logger);

        [LoggerMessage(
            EventId = 2271,
            Level = LogLevel.Information,
            Message = "Published nntpsharedconfig fqdn={Fqdn}")]
        public static partial void SnapshotPublished(ILogger logger, string fqdn);

        [LoggerMessage(
            EventId = 2272,
            Level = LogLevel.Error,
            Message = "Initial nntpsharedconfig load failed; application will not start")]
        public static partial void InitialLoadFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2273,
            Level = LogLevel.Error,
            Message = "nntpsharedconfig refresh failed; retaining the last known-good snapshot")]
        public static partial void RefreshFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2274,
            Level = LogLevel.Information,
            Message = "nntpsharedconfig refresh loop started (interval {Interval})")]
        public static partial void RefreshLoopStarted(ILogger logger, TimeSpan interval);

        [LoggerMessage(
            EventId = 2275,
            Level = LogLevel.Information,
            Message = "nntpsharedconfig refresh loop stopped")]
        public static partial void RefreshLoopStopped(ILogger logger);
    }
}
