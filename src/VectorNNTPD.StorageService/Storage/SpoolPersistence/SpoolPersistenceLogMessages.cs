using Microsoft.Extensions.Logging;

namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{

    /// <summary>Source-generated logs for spool persistence. Success stays at debug so the hot path does not format an id string.</summary>
    internal static partial class SpoolPersistenceLogMessages
    {
        /// <summary>Logs a completed rename.</summary>
        /// <param name="logger">Persistence logger.</param>
        /// <param name="spool">Spool root.</param>
        /// <param name="bytes">Published length.</param>
        [LoggerMessage(EventId = 5700, Level = LogLevel.Debug, Message = "Spool article stored spool={Spool} bytes={Bytes}")]
        public static partial void Stored(ILogger logger, string spool, int bytes);

        /// <summary>Logs a byte-identical duplicate.</summary>
        /// <param name="logger">Persistence logger.</param>
        /// <param name="spool">Spool root.</param>
        [LoggerMessage(EventId = 5701, Level = LogLevel.Debug, Message = "Spool article duplicate spool={Spool}")]
        public static partial void Duplicate(ILogger logger, string spool);

        /// <summary>Logs a conflicting file that was left in place.</summary>
        /// <param name="logger">Persistence logger.</param>
        /// <param name="articleId">Canonical article id hex.</param>
        /// <param name="path">Final path.</param>
        [LoggerMessage(EventId = 5702, Level = LogLevel.Error, Message = "Spool article conflict articleId={ArticleId} path={Path}")]
        public static partial void Conflict(ILogger logger, string articleId, string path);

        /// <summary>Logs a publish failure that can be retried on another spool.</summary>
        /// <param name="logger">Persistence logger.</param>
        /// <param name="articleId">Canonical article id hex.</param>
        /// <param name="spool">Spool root.</param>
        /// <param name="kind">Failure kind.</param>
        /// <param name="detail">Short filesystem detail.</param>
        [LoggerMessage(EventId = 5703, Level = LogLevel.Warning, Message = "Spool article publish failed articleId={ArticleId} spool={Spool} kind={Kind} detail={Detail}")]
        public static partial void PublishFailed(ILogger logger, string articleId, string spool, string kind, string detail);

        /// <summary>Logs that no spool could take the article.</summary>
        /// <param name="logger">Persistence logger.</param>
        /// <param name="articleId">Canonical article id hex.</param>
        [LoggerMessage(EventId = 5704, Level = LogLevel.Warning, Message = "Spool article deferred articleId={ArticleId} reason=no-eligible-spool")]
        public static partial void Deferred(ILogger logger, string articleId);

        /// <summary>Logs a worker-count change.</summary>
        /// <param name="logger">Worker logger.</param>
        /// <param name="active">Workers running after the change.</param>
        /// <param name="desired">Target worker count.</param>
        /// <param name="queued">Articles still queued.</param>
        [LoggerMessage(EventId = 5705, Level = LogLevel.Information, Message = "Spool workers active={Active} desired={Desired} queued={Queued}")]
        public static partial void WorkersChanged(ILogger logger, int active, int desired, int queued);

        /// <summary>Logs an article that was dequeued and not published because the process is stopping.</summary>
        /// <param name="logger">Worker logger.</param>
        /// <param name="articleId">Canonical article id hex.</param>
        [LoggerMessage(EventId = 5706, Level = LogLevel.Warning, Message = "Accepted article not persisted on shutdown articleId={ArticleId}")]
        public static partial void DroppedOnShutdown(ILogger logger, string articleId);

        /// <summary>Logs a periodic persistence summary.</summary>
        /// <param name="logger">Worker logger.</param>
        /// <param name="stored">Renames completed in the process.</param>
        /// <param name="storedBytes">Bytes renamed.</param>
        /// <param name="deferred">Deferred selection attempts.</param>
        /// <param name="active">Running workers.</param>
        /// <param name="queued">Queued articles.</param>
        /// <param name="queuedBytes">Bytes in the queue.</param>
        /// <param name="reservedBytes">Bytes reserved for admitted bodies that are not queued yet.</param>
        [LoggerMessage(EventId = 5707, Level = LogLevel.Information, Message = "Spool persistence stored={Stored} storedBytes={StoredBytes} deferred={Deferred} activeWorkers={Active} queued={Queued} queuedBytes={QueuedBytes} reservedBytes={ReservedBytes}")]
        public static partial void Summary(ILogger logger, long stored, long storedBytes, long deferred, int active, int queued, long queuedBytes, long reservedBytes);
    }
}
