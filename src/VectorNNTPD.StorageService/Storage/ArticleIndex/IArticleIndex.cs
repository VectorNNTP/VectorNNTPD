using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>
    /// Local article index: one SQLite database per spool plus one in-memory membership set.
    /// </summary>
    /// <remarks>
    /// Membership is published after the index transaction commits. The set is not checked against the filesystem.
    /// <see cref="TryGetRetrievableArticle"/> also requires the final spool file to exist.
    /// A stale membership entry can remain when the payload is missing. That is accepted until eviction removes the row and the file together.
    /// </remarks>
    internal interface IArticleIndex
    {
        /// <summary>Gets a value indicating that startup membership load has finished.</summary>
        bool IsReady { get; }

        /// <summary>Gets the resolved database directory.</summary>
        string DatabaseDirectory { get; }

        /// <summary>Gets the SQLite database file path.</summary>
        string DatabasePath { get; }

        /// <summary>Gets the hashes currently published in memory.</summary>
        long PresenceCount { get; }

        /// <summary>Gets the membership backing-store estimate, in bytes.</summary>
        long PresenceEstimatedBytes { get; }

        /// <summary>Gets process counters.</summary>
        ArticleIndexCounters Counters { get; }

        /// <summary>Gets the startup membership task. It completes when the databases have been loaded. Startup does not walk spool files.</summary>
        Task<ArticleIndexReconciliationReport> Reconciliation { get; }

        /// <summary>Returns whether the hash is in the committed membership set.</summary>
        /// <param name="articleId">Canonical article identity.</param>
        /// <returns><see langword="true"/> when the hash was published after a commit.</returns>
        /// <exception cref="InvalidOperationException">Thrown when startup load has not finished.</exception>
        bool Contains(ArticleId articleId);

        /// <summary>
        /// Inserts or reaffirms a spool-published article.
        /// The first insert stores <paramref name="receivedAtUnixMilliseconds"/> in both timestamps.
        /// A later insert for the same hash in that database keeps both timestamps and does not add a second row.
        /// </summary>
        /// <param name="articleId">Canonical article identity.</param>
        /// <param name="spoolDirectory">Validated spool root that holds the final file.</param>
        /// <param name="receivedAtUnixMilliseconds">One UTC Unix epoch millisecond sample.</param>
        /// <param name="cancellationToken">Cancels the wait. A queued command can still commit.</param>
        /// <returns>The commit result. <see cref="ArticleIndexInsertResult.Inserted"/> is false when the original timestamps were kept.</returns>
        ValueTask<ArticleIndexInsertResult> InsertStoredAsync(
            ArticleId articleId,
            string spoolDirectory,
            long receivedAtUnixMilliseconds,
            CancellationToken cancellationToken);

        /// <summary>
        /// Waits until this caller owns the in-flight reservation for <paramref name="articleId"/>.
        /// Membership is not an ingestion gate. Release the reservation when ingestion finishes or fails.
        /// </summary>
        /// <param name="articleId">Canonical article identity.</param>
        /// <param name="cancellationToken">Cancels the wait. Cancellation does not leave this caller holding the reservation.</param>
        /// <returns><see cref="ArticleIngestionHold.Acquired"/> when this caller must publish and index the article.</returns>
        ValueTask<ArticleIngestionHold> ReserveIngestionAsync(ArticleId articleId, CancellationToken cancellationToken);

        /// <summary>Releases a reservation acquired by <see cref="ReserveIngestionAsync"/> without changing membership.</summary>
        /// <param name="articleId">Identity that this caller reserved.</param>
        void ReleaseIngestion(ArticleId articleId);

        /// <summary>Moves <c>LastAccessedAt</c> forward in <paramref name="spoolDirectory"/> when that row exists and the timestamp is newer.</summary>
        /// <param name="articleId">Canonical article identity.</param>
        /// <param name="spoolDirectory">Spool whose database owns the article.</param>
        /// <param name="accessedAtUnixMilliseconds">UTC Unix epoch milliseconds.</param>
        /// <param name="cancellationToken">Cancels the wait. A queued command can still commit.</param>
        /// <returns>The commit result. A missing row is not inserted.</returns>
        ValueTask<ArticleIndexTouchResult> TouchAsync(
            ArticleId articleId,
            string spoolDirectory,
            long accessedAtUnixMilliseconds,
            CancellationToken cancellationToken);

        /// <summary>Reads the committed location without checking that the file still exists.</summary>
        /// <param name="articleId">Canonical article identity.</param>
        /// <param name="record">The row when the method returns <see langword="true"/>.</param>
        /// <returns><see langword="false"/> when the hash is not committed.</returns>
        bool TryGetIndexedRecord(ArticleId articleId, out ArticleIndexRecord record);

        /// <summary>
        /// Returns the final article path when the index row exists and that file exists.
        /// A missing file returns false and does not remove the index row or the membership hash.
        /// </summary>
        /// <param name="articleId">Canonical article identity.</param>
        /// <param name="articlePath">Absolute final path when the method returns <see langword="true"/>.</param>
        /// <returns><see langword="false"/> when the article cannot be retrieved from the indexed location.</returns>
        bool TryGetRetrievableArticle(ArticleId articleId, out string articlePath);

        /// <summary>Copies the oldest committed rows in one spool. Pending newer touches are skipped.</summary>
        /// <param name="spoolDirectory">Spool whose database is queried. Other spools are not scanned.</param>
        /// <param name="destination">Buffer. At most 10000 rows are accepted.</param>
        /// <returns>The number of rows written.</returns>
        int CopyOldest(string spoolDirectory, Span<ArticleIndexRecord> destination);

        /// <summary>Copies rows in one spool whose access time is at or before <paramref name="accessedAtOrBeforeUnixMilliseconds"/>.</summary>
        /// <param name="spoolDirectory">Spool whose database is queried.</param>
        /// <param name="accessedAtOrBeforeUnixMilliseconds">Inclusive UTC Unix epoch millisecond bound.</param>
        /// <param name="destination">Buffer filled oldest first. Rows with a newer pending touch are omitted.</param>
        /// <returns>The number of rows written.</returns>
        int CopyAccessedAtOrBefore(string spoolDirectory, long accessedAtOrBeforeUnixMilliseconds, Span<ArticleIndexRecord> destination);

        /// <summary>Gets the SQLite path for a registered spool.</summary>
        /// <param name="spoolDirectory">Registered spool root.</param>
        /// <returns>The database file under <c>DbDir</c>.</returns>
        string DatabasePathFor(string spoolDirectory);
    }
}
