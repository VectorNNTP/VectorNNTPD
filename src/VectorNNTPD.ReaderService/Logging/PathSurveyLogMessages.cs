namespace VectorNNTPD.ReaderService.Logging
{
    /// <summary>Source-generated Path-survey lifecycle log messages.</summary>
    internal static partial class PathSurveyLogMessages
    {
        [LoggerMessage(
            EventId = 2010,
            Level = LogLevel.Error,
            Message = "Path-survey completed-file handler failed for {CompletedFile}; gzip archive will still run")]
        public static partial void CompletedFileHandlerFailed(ILogger logger, Exception exception, string CompletedFile);
    }
}
