namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>Outcome of attempting to accept an article into the ingestion queue.</summary>
    public enum ArticleEnqueueResult
    {
        /// <summary>Article was accepted into the bounded ingestion queue (239 path).</summary>
        Accepted = 0,

        /// <summary>
        /// Queue is unavailable (completed/shutting down or otherwise not accepting).
        /// Maps to RFC 4644 temporary failure (<c>400</c> + close) for TAKETHIS.
        /// </summary>
        Unavailable = 1,

        /// <summary>
        /// Article cannot be admitted because its payload exceeds
        /// <c>Nntpd:TransitQueueMemoryLimit</c>. Waiting cannot free enough budget.
        /// Maps to IHAVE <c>437</c> and TAKETHIS <c>439</c>.
        /// </summary>
        Rejected = 2,

        /// <summary>
        /// Article cannot be reserved immediately without waiting for capacity.
        /// IHAVE maps this to <c>436</c> and must not wait. TAKETHIS still uses
        /// <see cref="IArticleIngestionQueue.EnqueueAsync"/> (waits) and does not
        /// observe this result on the production path.
        /// </summary>
        Full = 3,
    }
}
