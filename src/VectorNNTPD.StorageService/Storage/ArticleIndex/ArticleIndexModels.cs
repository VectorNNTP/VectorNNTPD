using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>Kind of one queued article-index mutation.</summary>
    internal enum ArticleIndexCommandKind : byte
    {
        /// <summary>Insert a spool-published article. Duplicate keys keep the original receipt time.</summary>
        Insert = 1,

        /// <summary>Move <c>LastAccessedAt</c> forward. A missing row is not created.</summary>
        Touch = 2,
    }

    /// <summary>Outcome of one committed mutation after the transaction commits.</summary>
    internal enum ArticleIndexWriteDisposition : byte
    {
        /// <summary>The hash was not in the index and the insert wrote it.</summary>
        Inserted = 1,

        /// <summary>The hash was already committed. <c>ReceivedAt</c> and the spool directory were left unchanged.</summary>
        AlreadyPresent = 2,

        /// <summary><c>LastAccessedAt</c> moved forward. <c>ReceivedAt</c> was not written.</summary>
        Updated = 3,

        /// <summary>The row exists and the supplied access time is not newer.</summary>
        Unchanged = 4,

        /// <summary>No row exists for the hash. The touch did not insert one.</summary>
        Missing = 5,
    }

    /// <summary>Result of a committed insert.</summary>
    /// <param name="Committed">True when the transaction that observed this insert committed.</param>
    /// <param name="Inserted">False when an older row was kept.</param>
    internal readonly record struct ArticleIndexInsertResult(bool Committed, bool Inserted);

    /// <summary>How a committed touch finished.</summary>
    internal enum ArticleIndexTouchDisposition : byte
    {
        /// <summary><c>LastAccessedAt</c> changed.</summary>
        Updated = 1,

        /// <summary>The row exists and the timestamp was not newer.</summary>
        Unchanged = 2,

        /// <summary>No index row exists.</summary>
        Missing = 3,
    }

    /// <summary>Result of a committed touch.</summary>
    /// <param name="Committed">True when the transaction that observed this touch committed.</param>
    /// <param name="Disposition">Whether the row changed, stayed, or was absent.</param>
    internal readonly record struct ArticleIndexTouchResult(bool Committed, ArticleIndexTouchDisposition Disposition);

    /// <summary>One committed article-index row.</summary>
    /// <param name="ArticleId">Canonical 32-byte identity.</param>
    /// <param name="ReceivedAtUnixMilliseconds">UTC Unix epoch milliseconds of first index insert.</param>
    /// <param name="LastAccessedAtUnixMilliseconds">UTC Unix epoch milliseconds of the newest committed access.</param>
    /// <param name="SpoolDirectory">Validated spool root. Not the article file path.</param>
    internal readonly record struct ArticleIndexRecord(
        ArticleId ArticleId,
        long ReceivedAtUnixMilliseconds,
        long LastAccessedAtUnixMilliseconds,
        string SpoolDirectory);

    /// <summary>One <c>wal_checkpoint</c> result row.</summary>
    /// <param name="Busy">Non-zero when SQLite could not checkpoint every frame.</param>
    /// <param name="LogFrames">Frames in the WAL.</param>
    /// <param name="CheckpointedFrames">Frames copied back to the database.</param>
    internal readonly record struct ArticleIndexCheckpoint(int Busy, int LogFrames, int CheckpointedFrames);

    /// <summary>Startup membership counters. Startup does not walk spool files, so every count stays zero.</summary>
    /// <param name="FilesSeen">Always zero. Startup does not visit article file names.</param>
    /// <param name="MissingQueued">Always zero. Startup does not insert rows from file names.</param>
    /// <param name="AlreadyPresent">Always zero.</param>
    /// <param name="RowsProbed">Always zero. Startup does not stat indexed payloads.</param>
    /// <param name="StaleRows">Always zero. A row whose file is missing is left in place and is not counted here.</param>
    /// <param name="ReceiptTimeFromClock">Always zero. Startup does not invent a receipt time from a file.</param>
    /// <param name="CrossSpoolDuplicates">Always zero. Cross-spool hash copies found while loading membership are logged separately.</param>
    internal readonly record struct ArticleIndexReconciliationReport(
        long FilesSeen,
        long MissingQueued,
        long AlreadyPresent,
        long RowsProbed,
        long StaleRows,
        long ReceiptTimeFromClock,
        long CrossSpoolDuplicates);

    /// <summary>One row read from the retired single-database index.</summary>
    /// <param name="Id">Canonical article identity.</param>
    /// <param name="ReceivedAtUnixMilliseconds">Original receipt time.</param>
    /// <param name="LastAccessedAtUnixMilliseconds">Original access time.</param>
    /// <param name="SpoolDirectory">Spool root stored by the retired schema.</param>
    internal readonly record struct ArticleIndexLegacyRow(
        ArticleId Id,
        long ReceivedAtUnixMilliseconds,
        long LastAccessedAtUnixMilliseconds,
        string SpoolDirectory);

    /// <summary>
    /// One queued command. The completion is set only after the batch commits, or with the failure that rolled it back.
    /// </summary>
    internal sealed class ArticleIndexRequest
    {
        /// <summary>Initializes a command and observes a later fault so a cancelled caller cannot leave it unobserved.</summary>
        /// <param name="kind">Insert or touch.</param>
        /// <param name="id">Canonical article identity.</param>
        /// <param name="unixMilliseconds">UTC Unix epoch milliseconds supplied by the caller.</param>
        /// <param name="spoolDirectory">Spool root for an insert. Null for a touch.</param>
        internal ArticleIndexRequest(ArticleIndexCommandKind kind, ArticleId id, long unixMilliseconds, string? spoolDirectory)
        {
            Kind = kind;
            Id = id;
            UnixMilliseconds = unixMilliseconds;
            SpoolDirectory = spoolDirectory;
            Completion = new TaskCompletionSource<ArticleIndexWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Completion.Task.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>Gets the command kind.</summary>
        internal ArticleIndexCommandKind Kind { get; }

        /// <summary>Gets the article identity.</summary>
        internal ArticleId Id { get; }

        /// <summary>Gets the caller-supplied UTC Unix millisecond timestamp.</summary>
        internal long UnixMilliseconds { get; }

        /// <summary>Gets the spool root for an insert.</summary>
        internal string? SpoolDirectory { get; }

        /// <summary>Gets the commit completion. Set by the writer after the transaction outcome is known.</summary>
        internal TaskCompletionSource<ArticleIndexWriteResult> Completion { get; }
    }

    /// <summary>Shared commit result for insert and touch waiters.</summary>
    /// <param name="Disposition">What the committed statement did.</param>
    internal readonly record struct ArticleIndexWriteResult(ArticleIndexWriteDisposition Disposition);

    /// <summary>
    /// Distinct article identities accumulated for one transaction.
    /// Insert waiters and touch waiters can share one row.
    /// </summary>
    internal sealed class ArticleIndexMutation
    {
        /// <summary>Initializes a mutation for <paramref name="id"/>.</summary>
        /// <param name="id">Canonical article identity.</param>
        internal ArticleIndexMutation(ArticleId id)
        {
            Id = id;
        }

        /// <summary>Gets the article identity.</summary>
        internal ArticleId Id { get; }

        /// <summary>Gets a value indicating whether this batch inserts the row.</summary>
        internal bool HasInsert { get; set; }

        /// <summary>Gets or sets the original receipt timestamp. Later inserts in the batch do not replace it.</summary>
        internal long ReceivedAt { get; set; }

        /// <summary>Gets or sets the newest access timestamp in the batch.</summary>
        internal long LastAccessedAt { get; set; }

        /// <summary>Gets or sets the spool root from the first insert.</summary>
        internal string? SpoolDirectory { get; set; }

        /// <summary>Gets a value indicating whether a touch should run after the insert.</summary>
        internal bool HasTouch { get; set; }

        /// <summary>Gets or sets the insert statement outcome. Meaningful after a successful commit.</summary>
        internal ArticleIndexWriteDisposition InsertDisposition { get; set; }

        /// <summary>Gets or sets the touch statement outcome. Meaningful after a successful commit.</summary>
        internal ArticleIndexWriteDisposition TouchDisposition { get; set; }

        /// <summary>Gets insert commands completed from this mutation.</summary>
        internal List<ArticleIndexRequest> InsertWaiters { get; } = new(1);

        /// <summary>Gets touch commands completed from this mutation.</summary>
        internal List<ArticleIndexRequest> TouchWaiters { get; } = new(1);

        /// <summary>Gets coalesced touch completions that were not queued as their own commands.</summary>
        internal List<TaskCompletionSource<ArticleIndexWriteResult>>? CoalescedTouches { get; set; }
    }
}
