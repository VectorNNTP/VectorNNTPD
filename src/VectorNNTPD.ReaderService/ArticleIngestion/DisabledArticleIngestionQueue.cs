namespace VectorNNTPD.ReaderService.ArticleIngestion
{
    /// <summary>
    /// No-op ingestion queue used when no real queue is supplied (tests / early wiring).
    /// </summary>
    /// <remarks>Always reports unavailable so TAKETHIS follows the RFC 4644 <c>400</c> temporary path.</remarks>
    public sealed class DisabledArticleIngestionQueue : IArticleIngestionQueue
    {
        /// <summary>Shared disabled instance.</summary>
        public static DisabledArticleIngestionQueue Instance { get; } = new();

        private DisabledArticleIngestionQueue()
        {
        }

        /// <inheritdoc />
        public long MemoryLimitBytes => 0;

        /// <inheritdoc />
        public long QueuedBytes => 0;

        /// <inheritdoc />
        public long PeakQueuedBytes => 0;

        /// <inheritdoc />
        public int MaxArticleBytes => Configuration.ArticleIngestionOptions.DefaultMaxArticleBytes;

        /// <inheritdoc />
        public int Count => 0;

        /// <inheritdoc />
        public int PeakCount => 0;

        /// <inheritdoc />
        public bool IsAccepting => false;

        /// <inheritdoc />
        public ValueTask<ArticleEnqueueResult> EnqueueAsync(
            InboundArticle article,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(article);
            return ValueTask.FromResult(ArticleEnqueueResult.Unavailable);
        }

        /// <inheritdoc />
        public bool TryProbeCapacity() => false;

        /// <inheritdoc />
        public ArticleEnqueueResult TryAdmit(InboundArticle article)
        {
            ArgumentNullException.ThrowIfNull(article);
            return ArticleEnqueueResult.Unavailable;
        }

        /// <inheritdoc />
        public bool TryEnqueue(InboundArticle article)
        {
            ArgumentNullException.ThrowIfNull(article);
            return false;
        }

        /// <inheritdoc />
        public void Complete()
        {
        }

        /// <inheritdoc />
        public ValueTask<InboundArticle?> DequeueAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<InboundArticle?>(null);
    }
}
