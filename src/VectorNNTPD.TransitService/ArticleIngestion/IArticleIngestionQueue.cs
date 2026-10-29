namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// Byte-budgeted in-memory ingestion boundary between NNTP receive handlers and the spool writer.
    /// </summary>
    /// <remarks>
    /// Enqueue completes when payload-byte capacity is available (or fails when the queue is
    /// completed, or when a single article exceeds the configured budget). Callers must not
    /// perform disk I/O; persistence is owned by the background spool writer.
    /// </remarks>
    public interface IArticleIngestionQueue
    {
        /// <summary>
        /// Gets the configured Transit article-queue memory budget in bytes
        /// (<c>Nntpd:TransitQueueMemoryLimit</c>).
        /// </summary>
        long MemoryLimitBytes { get; }

        /// <summary>
        /// Gets reserved queued article payload bytes
        /// (sum of owned <see cref="InboundArticle.Payload"/> lengths).
        /// </summary>
        long QueuedBytes { get; }

        /// <summary>Gets the high-water mark of <see cref="QueuedBytes"/> since construction.</summary>
        long PeakQueuedBytes { get; }

        /// <summary>Gets the maximum accepted article payload size in bytes.</summary>
        int MaxArticleBytes { get; }

        /// <summary>Gets the approximate number of articles currently queued.</summary>
        int Count { get; }

        /// <summary>Gets the high-water mark of <see cref="Count"/> since construction.</summary>
        int PeakCount { get; }

        /// <summary>
        /// Gets the number of producers waiting for byte-budget capacity.
        /// </summary>
        int WaitingProducerCount => 0;

        /// <summary>
        /// Gets lifetime admission failures (rejected, full, or unavailable).
        /// </summary>
        long AdmissionFailureCount => 0;

        /// <summary>
        /// Gets accumulated producer wait ticks while blocked on byte-budget capacity.
        /// </summary>
        long AdmissionWaitTicks => 0;

        /// <summary>Gets a value indicating whether the queue is still accepting articles.</summary>
        bool IsAccepting { get; }

        /// <summary>
        /// Enqueues an accepted article, waiting for byte-budget capacity when the queue is full.
        /// </summary>
        /// <returns>
        /// <see cref="ArticleEnqueueResult.Accepted"/> when queued;
        /// <see cref="ArticleEnqueueResult.Rejected"/> when the article is larger than the budget;
        /// <see cref="ArticleEnqueueResult.Unavailable"/> when the queue is completed/shutting down
        /// or the wait is cancelled.
        /// </returns>
        ValueTask<ArticleEnqueueResult> EnqueueAsync(InboundArticle article, CancellationToken cancellationToken);

        /// <summary>
        /// Returns whether the queue is accepting and has at least one free payload byte.
        /// </summary>
        /// <remarks>
        /// Does not reserve bytes. IHAVE uses this before <c>335</c> because the article
        /// size is unknown. A later <see cref="TryAdmit"/> still performs the atomic
        /// reservation against the actual payload length.
        /// </remarks>
        bool TryProbeCapacity();

        /// <summary>
        /// Attempts to reserve the article's payload bytes and enqueue it without waiting.
        /// </summary>
        /// <returns>
        /// <see cref="ArticleEnqueueResult.Accepted"/> when queued;
        /// <see cref="ArticleEnqueueResult.Rejected"/> when the article is larger than the budget;
        /// <see cref="ArticleEnqueueResult.Full"/> when the remaining budget cannot admit it now;
        /// <see cref="ArticleEnqueueResult.Unavailable"/> when the queue is completed/shutting down.
        /// </returns>
        ArticleEnqueueResult TryAdmit(InboundArticle article);

        /// <summary>
        /// Attempts to enqueue without waiting. Returns <see langword="false"/> when the budget
        /// cannot admit the article immediately, the article exceeds the budget, or the queue
        /// is unavailable.
        /// </summary>
        bool TryEnqueue(InboundArticle article);

        /// <summary>
        /// Completes the writer side so no further articles are accepted.
        /// Already-queued articles remain readable until drained.
        /// </summary>
        void Complete();

        /// <summary>
        /// Reads the next queued article, or <see langword="null"/> when the queue is completed and empty.
        /// </summary>
        /// <remarks>
        /// Safe for concurrent callers. A successful read transfers consumer ownership and
        /// releases reserved payload bytes exactly once. Callers must not release again.
        /// Items leave the queue in FIFO order; concurrent processing order may differ.
        /// </remarks>
        ValueTask<InboundArticle?> DequeueAsync(CancellationToken cancellationToken);
    }
}
