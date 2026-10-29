namespace VectorNNTPD.ReaderService.Ninpaths
{
    /// <summary>Source-generated ninpaths processing log messages.</summary>
    internal static partial class NinpathsLogMessages
    {
        [LoggerMessage(
            EventId = 2900,
            Level = LogLevel.Debug,
            Message = "Ninpaths skipped completed Path-survey file {CompletedFile}; Nntpd:Top1000 has no recipients")]
        public static partial void Disabled(ILogger logger, string CompletedFile);

        [LoggerMessage(
            EventId = 2901,
            Level = LogLevel.Error,
            Message = "Ninpaths could not open completed Path-survey file {CompletedFile}")]
        public static partial void OpenFailed(ILogger logger, Exception exception, string CompletedFile);

        [LoggerMessage(
            EventId = 2902,
            Level = LogLevel.Debug,
            Message = "Ninpaths ignored duplicate completed Path-survey file {CompletedFile}")]
        public static partial void Duplicate(ILogger logger, string CompletedFile);

        [LoggerMessage(
            EventId = 2903,
            Level = LogLevel.Error,
            Message = "Ninpaths pending-file queue is full; dropping handoff for {CompletedFile}")]
        public static partial void QueueFull(ILogger logger, string CompletedFile);

        [LoggerMessage(
            EventId = 2904,
            Level = LogLevel.Information,
            Message = "Ninpaths processing completed Path-survey file {CompletedFile}")]
        public static partial void Processing(ILogger logger, string CompletedFile);

        [LoggerMessage(
            EventId = 2905,
            Level = LogLevel.Information,
            Message = "Ninpaths produced no dump for {CompletedFile}; total articles is zero")]
        public static partial void EmptyDump(ILogger logger, string CompletedFile);

        [LoggerMessage(
            EventId = 2906,
            Level = LogLevel.Error,
            Message = "Ninpaths cannot compose mail for {CompletedFile}; Email:DefaultFrom is missing or invalid")]
        public static partial void MissingFrom(ILogger logger, string CompletedFile);

        [LoggerMessage(
            EventId = 2907,
            Level = LogLevel.Information,
            Message = "Ninpaths report for {CompletedFile} not mailed; Email:Enabled is false")]
        public static partial void EmailDisabled(ILogger logger, string CompletedFile);

        [LoggerMessage(
            EventId = 2908,
            Level = LogLevel.Error,
            Message = "Ninpaths failed to spool report for {CompletedFile} status={Status} detail={Detail}")]
        public static partial void EmailFailed(ILogger logger, string CompletedFile, string Status, string Detail);

        [LoggerMessage(
            EventId = 2909,
            Level = LogLevel.Information,
            Message = "Ninpaths spooled report for {CompletedFile} recipients={RecipientCount} bytes={ReportBytes}")]
        public static partial void EmailAccepted(ILogger logger, string CompletedFile, int RecipientCount, int ReportBytes);

        [LoggerMessage(
            EventId = 2910,
            Level = LogLevel.Error,
            Message = "Ninpaths processing failed for {CompletedFile}")]
        public static partial void ProcessingFailed(ILogger logger, Exception exception, string CompletedFile);

        [LoggerMessage(
            EventId = 2911,
            Level = LogLevel.Error,
            Message = "Ninpaths background worker failed")]
        public static partial void WorkerFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2912,
            Level = LogLevel.Information,
            Message = "Ninpaths processing service started")]
        public static partial void Started(ILogger logger);

        [LoggerMessage(
            EventId = 2913,
            Level = LogLevel.Information,
            Message = "Ninpaths processing service stopped")]
        public static partial void Stopped(ILogger logger);
    }
}
