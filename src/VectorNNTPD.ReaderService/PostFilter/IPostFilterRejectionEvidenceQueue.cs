namespace VectorNNTPD.ReaderService.PostFilter
{
    /// <summary>
    /// Bounded asynchronous queue for PostFilter rejection evidence.
    /// Enqueue never changes the PostFilter or NNTP decision.
    /// </summary>
    public interface IPostFilterRejectionEvidenceQueue
    {
        /// <summary>Gets the configured capacity.</summary>
        int Capacity { get; }

        /// <summary>Gets the approximate queued count.</summary>
        int Count { get; }

        /// <summary>Gets dropped-enqueue count (queue full or completed).</summary>
        long Dropped { get; }

        /// <summary>Gets successfully written rows.</summary>
        long Written { get; }

        /// <summary>Gets failed database writes.</summary>
        long WriteFailures { get; }

        /// <summary>
        /// Attempts to enqueue evidence. Returns <see langword="false"/> when the
        /// queue is full or completed. The caller must still return the original
        /// NNTP rejection.
        /// </summary>
        bool TryEnqueue(PostFilterRejectionEvidence evidence);

        /// <summary>Completes the writer so the consumer can drain and exit.</summary>
        void Complete();

        /// <summary>Dequeues the next item, or <see langword="null"/> when completed and empty.</summary>
        ValueTask<PostFilterRejectionEvidence?> DequeueAsync(CancellationToken cancellationToken);
    }
}
