namespace VectorNNTPD.StorageService.Ingress
{

    /// <summary>
    /// Result of waiting for one queued article.
    /// </summary>
    /// <param name="HasArticle"><see langword="false"/> when the queue is completed and empty.</param>
    /// <param name="Article">The dequeued article when <paramref name="HasArticle"/> is true.</param>
    public readonly record struct AcceptedArticleDelivery(bool HasArticle, AcceptedArticle Article);

    /// <summary>
    /// In-memory handoff of accepted feed articles. Multiple sessions may enqueue,
    /// and multiple consumers may dequeue. The queue does not persist articles.
    /// </summary>
    public interface IAcceptedArticleQueue
    {
        /// <summary>
        /// Enqueues <paramref name="article"/> if the queue is still accepting.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when the article is visible to consumers.
        /// <see langword="false"/> after <see cref="Complete"/> or when the queued-byte budget
        /// would be exceeded. A false result does not queue the article.
        /// </returns>
        bool TryEnqueue(AcceptedArticle article);

        /// <summary>Maximum bytes queued plus reserved.</summary>
        long MaxQueuedBytes { get; }

        /// <summary>Bytes occupied by articles already in the queue.</summary>
        long QueuedBytes { get; }

        /// <summary>Bytes reserved for admitted transfers that are not yet queued.</summary>
        long ReservedBytes { get; }

        /// <summary>
        /// Queued bytes plus reserved bytes plus in-flight (dequeued, not yet released) bytes.
        /// All three count toward <see cref="MaxQueuedBytes"/>.
        /// </summary>
        long AccountedBytes { get; }

        /// <summary>
        /// Bytes for articles dequeued but not yet finished by a worker
        /// (including capacity-blocked holds). Counts toward <see cref="MaxQueuedBytes"/>.
        /// </summary>
        long InFlightBytes { get; }

        /// <summary>Admission requests waiting for reserved bytes. They do not hold a reservation yet.</summary>
        int AdmissionWaiters { get; }

        /// <summary>
        /// Releases bytes retained after dequeue once processing finishes (completed, failed, or abandoned).
        /// Do not call while the article is still held for <c>WaitingForCapacity</c>.
        /// A second release of the same bytes is a no-op only when the caller never over-releases the total.
        /// </summary>
        /// <param name="bytes">Article size previously moved into in-flight on dequeue.</param>
        void ReleaseProcessed(int bytes);

        /// <summary>
        /// Reserves <paramref name="bytes"/> when no admission is waiting and the reservation fits.
        /// A false result reserves nothing. Callers that must wait use <see cref="ReserveAsync"/>.
        /// </summary>
        /// <param name="bytes">Article body length. Must be at least 1.</param>
        /// <param name="reservation">The reservation when the method returns <see langword="true"/>.</param>
        bool TryReserve(int bytes, out QueueReservation reservation);

        /// <summary>
        /// Reserves <paramref name="bytes"/> immediately or in FIFO order when capacity is short.
        /// Returns null when the queue is completed or <paramref name="bytes"/> can never fit.
        /// Cancellation removes the waiter and does not leave a reservation.
        /// </summary>
        /// <param name="bytes">Article body length. Must be at least 1.</param>
        /// <param name="cancellationToken">Cancels the wait. Does not cancel a reservation already returned.</param>
        ValueTask<QueueReservation?> ReserveAsync(int bytes, CancellationToken cancellationToken);

        /// <summary>
        /// Moves <paramref name="reservation"/> into the queue as <paramref name="article"/>.
        /// The byte total does not increase. A false result releases the reservation.
        /// </summary>
        /// <param name="reservation">An outstanding reservation.</param>
        /// <param name="article">The received article. Its size must match the reservation.</param>
        bool TryCommit(QueueReservation reservation, AcceptedArticle article);

        /// <summary>Releases a reservation once. A second release is a no-op.</summary>
        /// <param name="reservation">The reservation to drop.</param>
        void Release(QueueReservation reservation);

        /// <summary>Articles currently in the queue.</summary>
        int QueuedCount { get; }

        /// <summary>Whether <see cref="Complete"/> has been called. Queued articles can still be read.</summary>
        bool IsCompleted { get; }

        /// <summary>
        /// Age of the oldest queued article. This is the enqueue time of the item that is still
        /// at the head of the timestamp queue. An empty queue returns <see cref="TimeSpan.Zero"/>.
        /// </summary>
        /// <param name="time">Clock that understands the timestamps recorded at enqueue.</param>
        TimeSpan OldestAge(TimeProvider time);

        /// <summary>
        /// Removes one article when one is already queued.
        /// Does not wait and does not allocate a cancellation source.
        /// A false result leaves the queue unchanged. A true result moves that article's bytes
        /// into <see cref="InFlightBytes"/> (still counted in <see cref="AccountedBytes"/>) and
        /// transfers ownership of the article to the caller, who must call
        /// <see cref="ReleaseProcessed"/> when finished.
        /// An empty completed queue returns <see langword="false"/>; it does not signal completion.
        /// Call <see cref="DequeueAsync"/> to wait or to observe completion.
        /// </summary>
        /// <param name="delivery">The article when the method returns <see langword="true"/>.</param>
        /// <returns><see langword="false"/> when the queue is empty, including after <see cref="Complete"/>.</returns>
        bool TryDequeue(out AcceptedArticleDelivery delivery);

        /// <summary>
        /// Waits for the next article. <see cref="AcceptedArticleDelivery.HasArticle"/> is
        /// <see langword="false"/> when the queue is completed and empty. Cancellation throws
        /// and does not remove an article.
        /// </summary>
        ValueTask<AcceptedArticleDelivery> DequeueAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Stops further enqueues and unblocks dequeue waiters once the queue is empty.
        /// Articles already queued remain available. Repeated calls are safe.
        /// </summary>
        void Complete();
    }
}
