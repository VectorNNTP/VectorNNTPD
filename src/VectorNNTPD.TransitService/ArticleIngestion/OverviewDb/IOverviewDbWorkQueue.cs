namespace VectorNNTPD.TransitService.ArticleIngestion.OverviewDb
{
    /// <summary>
    /// Bounded, byte-budgeted in-process queue between article workers and OverviewDB
    /// RabbitMQ publisher workers.
    /// </summary>
    /// <remarks>
    /// This queue is not durable. Enqueue success does not imply RabbitMQ confirmation.
    /// </remarks>
    internal interface IOverviewDbWorkQueue
    {
        /// <summary>Gets the configured byte budget.</summary>
        long MemoryLimitBytes { get; }

        /// <summary>Gets reserved queued payload bytes.</summary>
        long QueuedBytes { get; }

        /// <summary>Gets the approximate number of queued work items.</summary>
        int Count { get; }

        /// <summary>Gets producers waiting for byte-budget capacity.</summary>
        int WaitingProducerCount { get; }

        /// <summary>Gets whether the queue still accepts new work.</summary>
        bool IsAccepting { get; }

        /// <summary>
        /// Enqueues work, waiting for byte-budget capacity when full.
        /// </summary>
        ValueTask<ArticleEnqueueResult> EnqueueAsync(OverviewDbWorkItem item, CancellationToken cancellationToken);

        /// <summary>
        /// Dequeues the next item, or <see langword="null"/> when completed and empty.
        /// Releases the byte reservation on successful dequeue.
        /// </summary>
        ValueTask<OverviewDbWorkItem?> DequeueAsync(CancellationToken cancellationToken);

        /// <summary>Stops accepting new work; queued items remain readable until drained.</summary>
        void Complete();
    }
}
