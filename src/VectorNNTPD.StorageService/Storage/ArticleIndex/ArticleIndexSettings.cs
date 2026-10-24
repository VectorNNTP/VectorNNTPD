namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>
    /// SQLite article-index tuning. Production uses the defaults. Tests replace batching and queue bounds.
    /// </summary>
    /// <remarks>
    /// These values are not configuration keys. <c>StorageServer:DbDir</c> is the only operator-facing
    /// article-index setting. The durability and batching rationale is in the StorageServer configuration docs.
    /// </remarks>
    internal sealed class ArticleIndexSettings
    {
        /// <summary>File name of a development-era single database. Per-spool databases do not use this name.</summary>
        internal const string DatabaseFileName = "article-index.sqlite";

        /// <summary>Identity file stored in a spool root. The database file name is derived from its contents.</summary>
        internal const string SpoolIdentityFileName = "vectornntp-spool.id";

        /// <summary>Registry of spool identities stored under <c>DbDir</c>. Disabling a spool does not delete it.</summary>
        internal const string SpoolRegistryFileName = "article-index-spools.registry";

        /// <summary>Schema version written to a per-spool <c>ArticleIndexMeta</c>. Version 1 is the retired single-database shape.</summary>
        internal const int SchemaVersion = 3;

        /// <summary>Maximum eviction attempts per capacity-eviction pass.</summary>
        internal const int MaxEvictionAttempts = 256;

        /// <summary>Page size applied before the first schema write. Existing databases keep their page size.</summary>
        internal const int PageSizeBytes = 8192;

        /// <summary>Aggregate writer page-cache budget, in kibibytes, shared across every open spool database.</summary>
        internal const int AggregateWriterCacheKibibytes = 262144;

        /// <summary>Aggregate reader page-cache budget, in kibibytes, shared across every open read connection.</summary>
        internal const int AggregateReaderCacheKibibytes = 65536;

        /// <summary>Smallest writer cache given to one spool, in kibibytes.</summary>
        internal const int MinWriterCacheKibibytes = 2048;

        /// <summary>Smallest reader cache given to one connection, in kibibytes.</summary>
        internal const int MinReaderCacheKibibytes = 1024;

        /// <summary>Read connections used when this process has one spool database.</summary>
        internal const int ReaderCount = 4;

        /// <summary>Read connections used for each spool when several spool databases are open.</summary>
        internal const int SharedSpoolReaderCount = 2;

        /// <summary>SQLite busy wait before <c>SQLITE_BUSY</c>, in milliseconds.</summary>
        internal const int DefaultBusyTimeoutMilliseconds = 5000;

        /// <summary>WAL pages between automatic checkpoints. At 8 KiB pages this is about 32 MiB.</summary>
        internal const int WalAutoCheckpointPages = 4000;

        /// <summary>
        /// WAL size SQLite may keep after a checkpoint shrinks the file.
        /// It is not a hard cap on WAL growth while frames are still uncheckpointed.
        /// </summary>
        internal const long JournalSizeLimitBytes = 64L * 1024 * 1024;

        /// <summary>Writer mmap cap. This is a mapping limit, not a second copy of the database.</summary>
        internal const long MmapSizeBytes = 256L * 1024 * 1024;

        /// <summary>Maximum mutations in one committed transaction.</summary>
        internal const int DefaultBatchSize = 512;

        /// <summary>
        /// Maximum wait, in milliseconds, before a non-empty batch that is still under <see cref="DefaultBatchSize"/> commits.
        /// The caller waits for that commit, so further commands from the same caller cannot arrive during the wait.
        /// One millisecond gathers commands already queued by other in-flight callers.
        /// </summary>
        internal const int DefaultFlushIntervalMilliseconds = 1;

        /// <summary>Bounded command queue capacity. Producers wait when it is full.</summary>
        internal const int DefaultQueueCapacity = 8192;

        /// <summary>Commit attempts for one batch, including the first attempt.</summary>
        internal const int MaxCommitAttempts = 4;

        /// <summary>BLAKE3 digest length required for every index key.</summary>
        internal const int HashLength = 32;

        /// <summary>Maximum stored spool-directory length.</summary>
        internal const int MaxSpoolDirectoryChars = 1024;

        /// <summary>Passive checkpoint after this many committed batches.</summary>
        internal const int CheckpointEveryBatches = 32;

        /// <summary>Passive checkpoint when the WAL file is at least this large.</summary>
        internal const long WalCheckpointThresholdBytes = 64L * 1024 * 1024;

        /// <summary>How often the writer emits an information summary, in milliseconds.</summary>
        internal const int DefaultSummaryIntervalMilliseconds = 5000;

        /// <summary>Distinct in-flight touch keys held by the coalescer.</summary>
        internal const int MaxCoalescedKeys = 8192;

        /// <summary>Extra waiters attached to one coalesced touch key.</summary>
        internal const int MaxCoalescedWaitersPerKey = 64;

        /// <summary>Gets the maximum mutations committed together.</summary>
        internal int BatchSize { get; init; } = DefaultBatchSize;

        /// <summary>Gets the flush interval for a non-empty batch that has not reached <see cref="BatchSize"/>.</summary>
        internal int FlushIntervalMilliseconds { get; init; } = DefaultFlushIntervalMilliseconds;

        /// <summary>Gets the bounded queue capacity.</summary>
        internal int QueueCapacity { get; init; } = DefaultQueueCapacity;

        /// <summary>Gets the number of commit attempts for one batch.</summary>
        internal int CommitAttempts { get; init; } = MaxCommitAttempts;

        /// <summary>Gets the SQLite busy timeout in milliseconds.</summary>
        internal int BusyTimeoutMilliseconds { get; init; } = DefaultBusyTimeoutMilliseconds;

        /// <summary>Gets the summary interval in milliseconds.</summary>
        internal int SummaryIntervalMilliseconds { get; init; } = DefaultSummaryIntervalMilliseconds;

        /// <summary>
        /// Gets a test hook invoked on the writer thread before it waits for commands.
        /// Production leaves this null. The token is the writer cancellation token.
        /// </summary>
        internal Func<CancellationToken, ValueTask>? BeforeReadAsync { get; init; }

        /// <summary>
        /// Gets a test hook invoked inside the open transaction before commit.
        /// Production leaves this null. A throw rolls the batch back.
        /// </summary>
        internal Action? BeforeCommit { get; init; }

        /// <summary>
        /// Gets the spool root whose commits invoke <see cref="BeforeCommit"/>.
        /// Null invokes the hook for every spool.
        /// </summary>
        internal string? BeforeCommitSpool { get; init; }
    }

    /// <summary>SQLite cache sizes for one spool database. The aggregate budget is split across open spools.</summary>
    /// <param name="WriterCacheKibibytes">Writer <c>cache_size</c> in kibibytes.</param>
    /// <param name="ReaderCacheKibibytes">Per-reader <c>cache_size</c> in kibibytes.</param>
    /// <param name="ReaderCount">Read connections for this database.</param>
    internal readonly record struct ArticleIndexCacheBudget(int WriterCacheKibibytes, int ReaderCacheKibibytes, int ReaderCount)
    {
        /// <summary>Splits the aggregate cache across <paramref name="spoolCount"/> databases.</summary>
        /// <param name="spoolCount">Open spool databases. A value below one is treated as one.</param>
        /// <returns>The budget for each database. One spool keeps a 256 MiB writer cache and four 16 MiB readers.</returns>
        internal static ArticleIndexCacheBudget ForSpoolCount(int spoolCount)
        {
            var count = Math.Max(1, spoolCount);
            var readers = count == 1 ? ArticleIndexSettings.ReaderCount : ArticleIndexSettings.SharedSpoolReaderCount;
            var writer = Math.Max(ArticleIndexSettings.MinWriterCacheKibibytes, ArticleIndexSettings.AggregateWriterCacheKibibytes / count);
            var reader = Math.Max(ArticleIndexSettings.MinReaderCacheKibibytes, ArticleIndexSettings.AggregateReaderCacheKibibytes / (count * readers));
            return new ArticleIndexCacheBudget(writer, reader, readers);
        }
    }
}
