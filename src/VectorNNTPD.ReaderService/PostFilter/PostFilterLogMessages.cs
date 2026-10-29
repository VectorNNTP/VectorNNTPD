using VectorNNTPD.ReaderService.PostFilter.Quota;

namespace VectorNNTPD.ReaderService.PostFilter
{
    /// <summary>Source-generated PostFilter logs. Reasons stay off the NNTP wire.</summary>
    internal static partial class PostFilterLogMessages
    {
        [LoggerMessage(EventId = 2800, Level = LogLevel.Information, Message = "PostFilter rejected stage={Stage} reason={Reason} account={Account} artId={ArtId}")]
        public static partial void Rejected(ILogger logger, PostFilterStage stage, string reason, string account, string artId);

        [LoggerMessage(EventId = 2801, Level = LogLevel.Debug, Message = "PostFilter accepted stage={Stage} account={Account} artId={ArtId} reserved={Reserved}")]
        public static partial void Accepted(ILogger logger, PostFilterStage stage, string account, string artId, bool reserved);

        [LoggerMessage(EventId = 2802, Level = LogLevel.Warning, Message = "PostFilter COMMIT NOOP account={Account} token={Token} artId={ArtId}")]
        public static partial void CommitNoop(ILogger logger, string account, string token, string artId);

        [LoggerMessage(EventId = 2803, Level = LogLevel.Warning, Message = "PostFilter COMMIT unavailable after admit account={Account} token={Token} artId={ArtId}")]
        public static partial void CommitUnavailable(ILogger logger, string account, string token, string artId);

        [LoggerMessage(EventId = 2804, Level = LogLevel.Debug, Message = "PostFilter RELEASE status={Status} account={Account} token={Token}")]
        public static partial void Released(ILogger logger, PostFilterQuotaReleaseStatus status, string account, string token);

        [LoggerMessage(EventId = 2805, Level = LogLevel.Warning, Message = "PostFilter policy refresh failed; retaining last-known-good snapshot")]
        public static partial void PolicyRefreshFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 2806, Level = LogLevel.Warning, Message = "PostFilter SpamAssassin failure action={Action} detail={Detail} account={Account} artId={ArtId}")]
        public static partial void SpamAssassinFailed(ILogger logger, string action, string detail, string account, string artId);

        [LoggerMessage(
            EventId = 2808,
            Level = LogLevel.Information,
            Message = "PostFilter policy published revision={Revision} gate={Gate} spamAssassin={SpamAssassin} maxArticleSize={MaxArticleSize} excludeArtTypes={ExcludeArtTypes} hosts={Hosts} maxConnections={MaxConnections}")]
        public static partial void PolicyPublished(
            ILogger logger,
            long revision,
            string gate,
            bool spamAssassin,
            int maxArticleSize,
            string excludeArtTypes,
            int hosts,
            int maxConnections);

        [LoggerMessage(EventId = 2809, Level = LogLevel.Error, Message = "PostFilter initial policy load failed")]
        public static partial void PolicyInitialLoadFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 2810, Level = LogLevel.Information, Message = "PostFilter policy revision changed from={FromRevision} to={ToRevision}")]
        public static partial void PolicyRevisionChanged(ILogger logger, long fromRevision, long toRevision);

        [LoggerMessage(EventId = 2811, Level = LogLevel.Error, Message = "PostFilter policy repository failed")]
        public static partial void PolicyRepositoryFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2840,
            Level = LogLevel.Information,
            Message = "PostFilter evidence writer started capacity={Capacity}")]
        public static partial void EvidenceWriterStarted(ILogger logger, int capacity);

        [LoggerMessage(
            EventId = 2841,
            Level = LogLevel.Information,
            Message = "PostFilter evidence writer stopped written={Written} dropped={Dropped} writeFailures={WriteFailures}")]
        public static partial void EvidenceWriterStopped(ILogger logger, long written, long dropped, long writeFailures);

        [LoggerMessage(
            EventId = 2842,
            Level = LogLevel.Warning,
            Message = "PostFilter evidence writer stopped with queued items remaining={Remaining}")]
        public static partial void EvidenceWriterStoppedWithQueued(ILogger logger, int remaining);

        [LoggerMessage(
            EventId = 2843,
            Level = LogLevel.Error,
            Message = "PostFilter evidence write failed stage={Stage} reason={Reason}")]
        public static partial void EvidenceWriteFailed(ILogger logger, Exception exception, PostFilterStage stage, string reason);

        [LoggerMessage(
            EventId = 2844,
            Level = LogLevel.Warning,
            Message = "PostFilter evidence queue full; rejection evidence dropped stage={Stage} reason={Reason}")]
        public static partial void EvidenceQueueFull(ILogger logger, PostFilterStage stage, string reason);

        [LoggerMessage(
            EventId = 2807,
            Level = LogLevel.Information,
            Message = "PostFilter SPAMD transport stopped connects={Connects} checks={Checks} reuses={Reuses} reconnects={Reconnects} evictions={Evictions}")]
        public static partial void SpamAssassinTransportStopped(
            ILogger logger,
            long connects,
            long checks,
            long reuses,
            long reconnects,
            long evictions);
    }
}
