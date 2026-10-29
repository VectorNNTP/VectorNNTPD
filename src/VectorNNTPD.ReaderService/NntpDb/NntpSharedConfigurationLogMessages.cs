namespace VectorNNTPD.ReaderService.NntpDb
{
    /// <summary>Source-generated shared-configuration lifecycle log messages.</summary>
    internal static partial class NntpSharedConfigurationLogMessages
    {
        [LoggerMessage(
            EventId = 2260,
            Level = LogLevel.Information,
            Message = "Loading initial nntpsharedconfig")]
        public static partial void InitialLoadStarted(ILogger logger);

        [LoggerMessage(
            EventId = 2261,
            Level = LogLevel.Information,
            Message = "Published nntpsharedconfig maxArticleBytes={MaxArticleBytes} siteName={SiteName}")]
        public static partial void SnapshotPublished(ILogger logger, int MaxArticleBytes, string SiteName);

        [LoggerMessage(
            EventId = 2262,
            Level = LogLevel.Error,
            Message = "Initial nntpsharedconfig load failed; application will not start")]
        public static partial void InitialLoadFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2263,
            Level = LogLevel.Error,
            Message = "nntpsharedconfig refresh failed; retaining the last known-good snapshot")]
        public static partial void RefreshFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2264,
            Level = LogLevel.Information,
            Message = "nntpsharedconfig refresh loop started (interval {Interval})")]
        public static partial void RefreshLoopStarted(ILogger logger, TimeSpan Interval);

        [LoggerMessage(
            EventId = 2265,
            Level = LogLevel.Information,
            Message = "nntpsharedconfig refresh loop stopped")]
        public static partial void RefreshLoopStopped(ILogger logger);
    }
}
