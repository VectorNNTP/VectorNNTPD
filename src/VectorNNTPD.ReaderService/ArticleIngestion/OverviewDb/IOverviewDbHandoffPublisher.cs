namespace VectorNNTPD.ReaderService.ArticleIngestion.OverviewDb
{
    /// <summary>
    /// One-way OverviewDB RabbitMQ handoff with asynchronous publisher confirms.
    /// </summary>
    /// <remarks>
    /// <see cref="PublishAsync"/> returns after the message is written into the outstanding
    /// confirmation window. Broker confirmation is not awaited on the publish path.
    /// Crash loss of outstanding unconfirmed work is acceptable for OverviewDB.
    /// </remarks>
    public interface IOverviewDbHandoffPublisher
    {
        /// <summary>
        /// Publishes a compact OverviewDB payload to <c>overviewdb.queue</c> without waiting
        /// for the broker confirmation.
        /// </summary>
        /// <param name="item">Owned OverviewDB work item (already-encoded payload).</param>
        /// <param name="cancellationToken">Token used to cancel waiting for outstanding capacity or the write.</param>
        Task PublishAsync(OverviewDbWorkItem item, CancellationToken cancellationToken);

        /// <summary>Gets the number of publishes awaiting broker confirmation.</summary>
        int OutstandingCount { get; }

        /// <summary>
        /// Tries to dequeue one nack/return failure for in-process requeue handling.
        /// </summary>
        /// <remarks>Must not be called from inside a RabbitMQ ack/nack/return callback.</remarks>
        bool TryDequeuePublishFailure(out OverviewDbWorkItem item);

        /// <summary>
        /// Abandons all outstanding unconfirmed work (bounded shutdown / crash-loss path).
        /// </summary>
        void AbandonOutstanding();
    }
}
