namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>Source-generated incoming spool writer log messages.</summary>
    internal static partial class SpoolLogMessages
    {
        [LoggerMessage(
            EventId = 2000,
            Level = LogLevel.Warning,
            Message = "Incoming spool writer stop canceled with {Queued} article(s) still buffered")]
        public static partial void StopCanceledWithBufferedArticles(ILogger logger, int Queued);

        [LoggerMessage(
            EventId = 2001,
            Level = LogLevel.Error,
            Message = "Incoming spool writer stopped with an error")]
        public static partial void WriterStoppedWithError(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2002,
            Level = LogLevel.Information,
            Message = "Incoming spool writer started (transit queue memory limit {MemoryLimit} bytes, max article {MaxBytes} bytes)")]
        public static partial void WriterStarted(ILogger logger, long MemoryLimit, int MaxBytes);

        [LoggerMessage(
            EventId = 2003,
            Level = LogLevel.Error,
            Message = "Incoming article {MessageId} ({Bytes} bytes) failed after OverviewDB handoff and was not requeued")]
        public static partial void FinishFailed(ILogger logger, Exception exception, string MessageId, int Bytes);

        [LoggerMessage(
            EventId = 2004,
            Level = LogLevel.Information,
            Message = "Incoming spool writer stopped")]
        public static partial void WriterStopped(ILogger logger);

        [LoggerMessage(
            EventId = 2005,
            Level = LogLevel.Error,
            Message = "Failed to write news log for incoming article {MessageId}; article remains accepted and is not reprocessed")]
        public static partial void NewsLogFailed(ILogger logger, Exception exception, string MessageId);

        [LoggerMessage(
            EventId = 2006,
            Level = LogLevel.Error,
            Message = "Failed to flush the news log during incoming spool writer stop")]
        public static partial void NewsLogFlushFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2007,
            Level = LogLevel.Error,
            Message = "Failed to write Path-survey log for incoming article {MessageId}; article remains accepted and is not reprocessed")]
        public static partial void PathSurveyFailed(ILogger logger, Exception exception, string MessageId);

        [LoggerMessage(
            EventId = 2008,
            Level = LogLevel.Error,
            Message = "Failed to flush the Path-survey log during incoming spool writer stop")]
        public static partial void PathSurveyFlushFailed(ILogger logger, Exception exception);
    }
}
