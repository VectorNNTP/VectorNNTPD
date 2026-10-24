using Microsoft.Extensions.Logging;

namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>Source-generated article-index logs. Per-article success is not logged.</summary>
    internal static partial class ArticleIndexLogMessages
    {
        /// <summary>Logs the open database after schema validation and membership load.</summary>
        /// <param name="logger">Article-index logger.</param>
        /// <param name="path">SQLite database file path.</param>
        /// <param name="schemaVersion">Committed schema version.</param>
        /// <param name="pageSize">SQLite page size in bytes.</param>
        /// <param name="rows">Hashes loaded into memory.</param>
        /// <param name="elapsedMilliseconds">Membership load time.</param>
        /// <param name="estimatedBytes">Backing-store estimate for the membership index.</param>
        [LoggerMessage(
            EventId = 5800,
            Level = LogLevel.Information,
            Message = "Article index ready path={Path} schemaVersion={SchemaVersion} pageSize={PageSize} rows={Rows} loadMilliseconds={ElapsedMilliseconds} membershipBytes={EstimatedBytes}")]
        public static partial void Ready(
            ILogger logger,
            string path,
            int schemaVersion,
            int pageSize,
            long rows,
            long elapsedMilliseconds,
            long estimatedBytes);

        /// <summary>Logs a periodic writer summary.</summary>
        /// <param name="logger">Article-index logger.</param>
        /// <param name="inserts">Rows inserted in this process.</param>
        /// <param name="batches">Committed transactions.</param>
        /// <param name="queueDepth">Commands waiting.</param>
        /// <param name="queueCapacity">Queue capacity.</param>
        /// <param name="lastBatchSize">Mutations in the last batch.</param>
        /// <param name="lastBatchMilliseconds">Last batch duration.</param>
        /// <param name="failures">Failed commit attempts.</param>
        /// <param name="retries">Busy retries.</param>
        /// <param name="walBytes">Current WAL file length, or zero when the file is absent.</param>
        /// <param name="membership">Hashes in the membership index.</param>
        [LoggerMessage(
            EventId = 5801,
            Level = LogLevel.Information,
            Message = "Article index inserts={Inserts} batches={Batches} queueDepth={QueueDepth} queueCapacity={QueueCapacity} lastBatchSize={LastBatchSize} lastBatchMilliseconds={LastBatchMilliseconds} failures={Failures} retries={Retries} walBytes={WalBytes} membership={Membership}")]
        public static partial void Summary(
            ILogger logger,
            long inserts,
            long batches,
            long queueDepth,
            int queueCapacity,
            long lastBatchSize,
            long lastBatchMilliseconds,
            long failures,
            long retries,
            long walBytes,
            long membership);

        /// <summary>Logs a batch that exhausted retries or failed for a non-busy reason.</summary>
        /// <param name="logger">Article-index logger.</param>
        /// <param name="exception">The failure.</param>
        /// <param name="commands">Commands in the failed batch.</param>
        /// <param name="attempt">Attempt number, starting at 1.</param>
        [LoggerMessage(EventId = 5802, Level = LogLevel.Error, Message = "Article index batch failed commands={Commands} attempt={Attempt}")]
        public static partial void BatchFailed(ILogger logger, Exception exception, int commands, int attempt);

        /// <summary>Logs a busy or locked result that will be retried.</summary>
        /// <param name="logger">Article-index logger.</param>
        /// <param name="attempt">Attempt that observed the lock.</param>
        /// <param name="sqliteError">SQLite primary error code.</param>
        [LoggerMessage(EventId = 5803, Level = LogLevel.Warning, Message = "Article index busy attempt={Attempt} sqliteError={SqliteError}")]
        public static partial void Busy(ILogger logger, int attempt, int sqliteError);

        /// <summary>Logs a checkpoint.</summary>
        /// <param name="logger">Article-index logger.</param>
        /// <param name="mode">PASSIVE or TRUNCATE.</param>
        /// <param name="busy">SQLite busy flag.</param>
        /// <param name="logFrames">WAL frames.</param>
        /// <param name="checkpointedFrames">Frames copied.</param>
        /// <param name="walBytes">WAL length after the checkpoint.</param>
        [LoggerMessage(
            EventId = 5804,
            Level = LogLevel.Information,
            Message = "Article index checkpoint mode={Mode} busy={Busy} logFrames={LogFrames} checkpointedFrames={CheckpointedFrames} walBytes={WalBytes}")]
        public static partial void Checkpoint(ILogger logger, string mode, int busy, int logFrames, int checkpointedFrames, long walBytes);

        /// <summary>Logs a membership publication failure after the row committed.</summary>
        /// <param name="logger">Article-index logger.</param>
        /// <param name="exception">The publication failure.</param>
        [LoggerMessage(EventId = 5805, Level = LogLevel.Error, Message = "Article index committed a row and failed to publish it to the membership index")]
        public static partial void CachePublishFailed(ILogger logger, Exception exception);

        /// <summary>Logs an index failure after the spool file was already published.</summary>
        /// <param name="logger">Persistence logger.</param>
        /// <param name="exception">The index failure.</param>
        /// <param name="spool">Spool root that holds the file.</param>
        [LoggerMessage(EventId = 5807, Level = LogLevel.Error, Message = "Article index update failed after the spool file was published spool={Spool}")]
        public static partial void IndexFailedAfterStore(ILogger logger, Exception exception, string spool);

        /// <summary>Logs that the configured database directory could not be created or opened.</summary>
        /// <param name="logger">Article-index logger.</param>
        /// <param name="exception">The I/O or SQLite failure.</param>
        /// <param name="path">Resolved database directory. This is not replaced with a spool path.</param>
        [LoggerMessage(EventId = 5808, Level = LogLevel.Error, Message = "Article index failed to start path={Path}")]
        public static partial void DirectoryFailed(ILogger logger, Exception exception, string path);

        /// <summary>Logs writer shutdown after the queue drain.</summary>
        /// <param name="logger">Article-index logger.</param>
        /// <param name="batches">Transactions committed in this process.</param>
        [LoggerMessage(EventId = 5809, Level = LogLevel.Information, Message = "Article index writer stopped batches={Batches}")]
        public static partial void Stopped(ILogger logger, long batches);

        /// <summary>Logs one open spool database.</summary>
        /// <param name="logger">Article-index logger.</param>
        /// <param name="spoolId">Stable spool identity.</param>
        /// <param name="path">SQLite file under <c>DbDir</c>.</param>
        /// <param name="schemaVersion">Schema version.</param>
        /// <param name="pageSize">Page size in bytes.</param>
        /// <param name="rows">Rows recorded in metadata after initialization and legacy import.</param>
        [LoggerMessage(
            EventId = 5810,
            Level = LogLevel.Information,
            Message = "Article index spool ready id={SpoolId} path={Path} schemaVersion={SchemaVersion} pageSize={PageSize} rows={Rows}")]
        public static partial void SpoolReady(ILogger logger, string spoolId, string path, int schemaVersion, int pageSize, long rows);

        /// <summary>Logs cross-spool hash duplicates found while loading membership. Rows were not deleted.</summary>
        /// <param name="logger">Article-index logger.</param>
        /// <param name="duplicates">Hashes already published by an earlier spool.</param>
        /// <param name="sample">Up to eight <c>spoolId:hash</c> pairs.</param>
        [LoggerMessage(EventId = 5811, Level = LogLevel.Warning, Message = "Article index cross-spool duplicates={Duplicates} sample={Sample}")]
        public static partial void CrossSpoolDuplicates(ILogger logger, long duplicates, string sample);

        /// <summary>Logs a completed legacy single-database migration.</summary>
        /// <param name="logger">Article-index logger.</param>
        /// <param name="rows">Rows inserted into per-spool databases.</param>
        /// <param name="databases">Spool databases that were open.</param>
        /// <param name="directory">Database directory. The legacy file was renamed, not deleted.</param>
        [LoggerMessage(EventId = 5812, Level = LogLevel.Warning, Message = "Article index migrated legacy rows={Rows} databases={Databases} directory={Directory}")]
        public static partial void LegacyMigrated(ILogger logger, int rows, int databases, string directory);

        /// <summary>Logs a spool file delete failure during eviction.</summary>
        [LoggerMessage(EventId = 5813, Level = LogLevel.Warning, Message = "Article index eviction file delete failed articleId={ArticleId} error={Error}")]
        public static partial void EvictionFileDeleteFailed(ILogger logger, string ArticleId, string Error);

        /// <summary>Logs an index delete failure after the spool file was already removed.</summary>
        [LoggerMessage(EventId = 5814, Level = LogLevel.Error, Message = "Article index eviction row delete failed articleId={ArticleId} spool={Spool}")]
        public static partial void EvictionIndexDeleteFailed(ILogger logger, string articleId, string spool);
    }
}
