namespace VectorNNTPD.ReaderService.RabbitMq.ArticleWork
{
    /// <summary>
    /// Reusable article-work RPC entry point for ARTICLE, HEAD, BODY, and STAT message-id lookups.
    /// </summary>
    /// <remarks>
    /// This boundary converts command-line Message-ID bytes into the JSON application payload.
    /// It does not retrieve article bytes or write NNTP success responses.
    /// </remarks>
    public interface IArticleWorkRpcClient
    {
        /// <summary>
        /// Runs the sequential Backfill Scheduler: selects one eligible backbone at a time
        /// (weighted by active consumer count), publishes one ArticleWork request per attempt,
        /// and returns on Success, exhausted not-found, cancellation, or the 5-second lookup
        /// deadline. The scheduler never exceeds that 5-second budget from its start time.
        /// </summary>
        /// <param name="messageId">Command-line Message-ID bytes, including angle brackets.</param>
        /// <param name="cancellationToken">Caller or session cancellation.</param>
        /// <returns>The structured RPC result. Never includes article content.</returns>
        Task<ArticleWorkRpcResult> LookupByMessageIdAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken);
    }
}
