namespace VectorNNTPD.StorageService.Ingress
{

    /// <summary>Source-generated logs for the single accepted-article worker.</summary>
    internal static partial class AcceptedArticleWorkerLogMessages
    {
        [LoggerMessage(
            EventId = 2280,
            Level = LogLevel.Information,
            Message = "Accepted article worker started")]
        public static partial void Started(ILogger logger);

        [LoggerMessage(
            EventId = 2281,
            Level = LogLevel.Information,
            Message = "Accepted article worker stopped")]
        public static partial void Stopped(ILogger logger);

        [LoggerMessage(
            EventId = 2282,
            Level = LogLevel.Error,
            Message = "Accepted article path manipulation failed articleId={ArticleId} parseFailure={ParseFailure} materializeFailure={MaterializeFailure}")]
        public static partial void PathRejected(ILogger logger, string articleId, string parseFailure, string materializeFailure);

        [LoggerMessage(
            EventId = 2283,
            Level = LogLevel.Error,
            Message = "Accepted article path manipulation changed the article id from {ArticleId}")]
        public static partial void PathIdentityChanged(ILogger logger, string articleId);

        [LoggerMessage(
            EventId = 2284,
            Level = LogLevel.Error,
            Message = "Accepted article processing failed articleId={ArticleId}")]
        public static partial void ProcessingFailed(ILogger logger, Exception exception, string articleId);
    }
}
