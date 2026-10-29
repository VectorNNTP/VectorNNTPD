namespace VectorNNTPD.TransitService.Session.Commands
{
    /// <summary>Source-generated log messages for ARTICLE/HEAD/BODY/STAT BackFiller retrieval.</summary>
    internal static partial class ArticleRetrievalLogMessages
    {
        [LoggerMessage(
            EventId = 2610,
            Level = LogLevel.Warning,
            Message = "Article retrieval transfer unavailable ({Outcome}): {Detail}")]
        public static partial void TransferUnavailable(ILogger logger, string Outcome, string? Detail);

        [LoggerMessage(
            EventId = 2611,
            Level = LogLevel.Warning,
            Message = "VATP article fetch failed for {Fqdn}:{VatpPort} request {RequestId}")]
        public static partial void VatpFetchFailed(ILogger logger, Exception ex, string Fqdn, int VatpPort, Guid RequestId);

        [LoggerMessage(
            EventId = 2612,
            Level = LogLevel.Warning,
            Message = "VATP article fetch unsuccessful ({Kind}): {Detail} ({ErrorCode})")]
        public static partial void VatpFetchUnsuccessful(ILogger logger, string Kind, string? Detail, string? ErrorCode);

        [LoggerMessage(
            EventId = 2613,
            Level = LogLevel.Warning,
            Message = "Fetched ArticleRecord identity mismatch: {Detail}")]
        public static partial void IdentityMismatch(ILogger logger, string? Detail);

        [LoggerMessage(
            EventId = 2614,
            Level = LogLevel.Debug,
            Message = "BackFiller article ingest not admitted ({Result}) for {MessageId}")]
        public static partial void IngestNotAdmitted(ILogger logger, string Result, string MessageId);

        [LoggerMessage(
            EventId = 2615,
            Level = LogLevel.Warning,
            Message = "BackFiller article ingest failed after successful retrieval")]
        public static partial void IngestFailed(ILogger logger, Exception ex);

        [LoggerMessage(
            EventId = 2616,
            Level = LogLevel.Debug,
            Message = "Storage article lookup {Outcome} articleId={ArticleId} requestId={RequestId}")]
        public static partial void StorageLookupCompleted(
            ILogger logger,
            string Outcome,
            string ArticleId,
            Guid RequestId);

        [LoggerMessage(
            EventId = 2617,
            Level = LogLevel.Debug,
            Message = "Storage candidate selected articleId={ArticleId} requestId={RequestId} serverId={ServerId} attempt={Attempt}")]
        public static partial void StorageCandidateSelected(
            ILogger logger,
            string ArticleId,
            Guid RequestId,
            int ServerId,
            int Attempt);

        [LoggerMessage(
            EventId = 2618,
            Level = LogLevel.Debug,
            Message = "Storage candidate failed articleId={ArticleId} requestId={RequestId} serverId={ServerId} attempt={Attempt} kind={Kind} acceptedDataBytes={AcceptedDataBytes} failoverEligible={FailoverEligible}")]
        public static partial void StorageCandidateFailed(
            ILogger logger,
            string ArticleId,
            Guid RequestId,
            int ServerId,
            int Attempt,
            string Kind,
            int AcceptedDataBytes,
            bool FailoverEligible);

        [LoggerMessage(
            EventId = 2619,
            Level = LogLevel.Debug,
            Message = "Storage candidate exhausted articleId={ArticleId} requestId={RequestId} attempt={Attempt}")]
        public static partial void StorageCandidateExhausted(
            ILogger logger,
            string ArticleId,
            Guid RequestId,
            int Attempt);
    }
}
