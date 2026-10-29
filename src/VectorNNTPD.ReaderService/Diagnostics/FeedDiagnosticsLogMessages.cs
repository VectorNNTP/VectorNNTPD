namespace VectorNNTPD.ReaderService.Diagnostics
{
    /// <summary>Source-generated feed-diagnostics reporter messages.</summary>
    internal static partial class FeedDiagnosticsLogMessages
    {
        [LoggerMessage(
            EventId = 2300,
            Level = LogLevel.Information,
            Message = "Feed diagnostics enabled (interval {IntervalSeconds}s, sessions={IncludeSessions})")]
        public static partial void Enabled(ILogger logger, int IntervalSeconds, bool IncludeSessions);

        [LoggerMessage(
            EventId = 2301,
            Level = LogLevel.Information,
            Message = "{Snapshot}")]
        public static partial void Snapshot(ILogger logger, string Snapshot);

        [LoggerMessage(
            EventId = 2302,
            Level = LogLevel.Information,
            Message = "Feed diagnostics stopped")]
        public static partial void Stopped(ILogger logger);
    }
}
