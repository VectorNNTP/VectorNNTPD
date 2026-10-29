namespace VectorNNTPD.TransitService.Newsgroups
{
    /// <summary>Source-generated newsgroup catalogue lifecycle log messages.</summary>
    internal static partial class NewsgroupCatalogueLogMessages
    {
        [LoggerMessage(
            EventId = 2500,
            Level = LogLevel.Information,
            Message = "Loading initial newsgroup catalogue")]
        public static partial void InitialLoadStarted(ILogger logger);

        [LoggerMessage(
            EventId = 2501,
            Level = LogLevel.Information,
            Message = "Published newsgroup catalogue snapshot with {GroupCount} groups")]
        public static partial void SnapshotPublished(ILogger logger, int GroupCount);

        [LoggerMessage(
            EventId = 2502,
            Level = LogLevel.Error,
            Message = "Initial newsgroup catalogue load failed; application will not start")]
        public static partial void InitialLoadFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2503,
            Level = LogLevel.Error,
            Message = "Newsgroup catalogue refresh failed; retaining the last known-good snapshot")]
        public static partial void RefreshFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2504,
            Level = LogLevel.Information,
            Message = "Newsgroup catalogue refresh loop started (interval {Interval})")]
        public static partial void RefreshLoopStarted(ILogger logger, TimeSpan Interval);

        [LoggerMessage(
            EventId = 2505,
            Level = LogLevel.Information,
            Message = "Newsgroup catalogue refresh loop stopped")]
        public static partial void RefreshLoopStopped(ILogger logger);
    }
}
