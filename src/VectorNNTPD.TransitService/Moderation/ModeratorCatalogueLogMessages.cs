namespace VectorNNTPD.TransitService.Moderation
{
    /// <summary>Source-generated moderator catalogue lifecycle log messages.</summary>
    internal static partial class ModeratorCatalogueLogMessages
    {
        [LoggerMessage(
            EventId = 2510,
            Level = LogLevel.Information,
            Message = "Loading initial moderator catalogue")]
        public static partial void InitialLoadStarted(ILogger logger);

        [LoggerMessage(
            EventId = 2511,
            Level = LogLevel.Information,
            Message = "Published moderator catalogue snapshot with {RuleCount} rules")]
        public static partial void SnapshotPublished(ILogger logger, int RuleCount);

        [LoggerMessage(
            EventId = 2512,
            Level = LogLevel.Error,
            Message = "Initial moderator catalogue load failed; application will not start")]
        public static partial void InitialLoadFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2513,
            Level = LogLevel.Error,
            Message = "Moderator catalogue refresh failed; retaining the last known-good snapshot")]
        public static partial void RefreshFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2514,
            Level = LogLevel.Information,
            Message = "Moderator catalogue refresh loop started (interval {Interval})")]
        public static partial void RefreshLoopStarted(ILogger logger, TimeSpan Interval);

        [LoggerMessage(
            EventId = 2515,
            Level = LogLevel.Information,
            Message = "Moderator catalogue refresh loop stopped")]
        public static partial void RefreshLoopStopped(ILogger logger);

        [LoggerMessage(
            EventId = 2516,
            Level = LogLevel.Error,
            Message = "nntpmoderators lookup failed")]
        public static partial void RepositoryFailed(ILogger logger, Exception exception);
    }
}
