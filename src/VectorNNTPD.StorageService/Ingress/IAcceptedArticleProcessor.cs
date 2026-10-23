namespace VectorNNTPD.StorageService.Ingress
{

    /// <summary>What a worker should do with the article it just processed.</summary>
    internal enum AcceptedArticleProcessResult
    {
        /// <summary>The article was published, matched an existing copy, or was permanently rejected.</summary>
        Completed,

        /// <summary>The article cannot be published and must not be retried.</summary>
        Failed,

        /// <summary>No eligible spool or write slot is available. The worker keeps this article and retries.</summary>
        WaitingForCapacity,
    }

    /// <summary>
    /// Processes one dequeued article. Production rewrites Path and publishes the bytes to a spool.
    /// </summary>
    internal interface IAcceptedArticleProcessor
    {
        /// <summary>
        /// Captures the server Path identity. Called once when the worker starts, after shared configuration is published.
        /// </summary>
        void EnsureReady();

        /// <summary>
        /// Processes <paramref name="article"/> once.
        /// </summary>
        /// <param name="article">Dequeued article. The caller keeps it when the result is <see cref="AcceptedArticleProcessResult.WaitingForCapacity"/>.</param>
        /// <param name="cancellationToken">Cancels an in-flight publish. Cancellation does not remove a queued article that has not been dequeued.</param>
        /// <returns>Whether the worker is done with the article or must hold it.</returns>
        ValueTask<AcceptedArticleProcessResult> ProcessAsync(AcceptedArticle article, CancellationToken cancellationToken);
    }
}
