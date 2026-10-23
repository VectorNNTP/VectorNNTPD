using VectorNNTPD.StorageService.Ingress;

namespace VectorNNTPD.StorageService.Listener
{

    /// <summary>
    /// Exactly-once ownership of a queue reservation bound to a STORE stream.
    /// </summary>
    /// <remarks>
    /// Bind, release, and commit-take share an atomic ownership flag so concurrent
    /// <c>RemoveStore</c>, authorization failure, and <c>TryCommit</c> paths cannot both
    /// release or both consume the same slot. The hold must be taken before
    /// <see cref="IAcceptedArticleQueue.TryCommit"/> so teardown cannot observe a still-bound
    /// reservation after the queue has already consumed it.
    /// </remarks>
    internal sealed class StoreReservationHold
    {
        private IAcceptedArticleQueue? _queue;
        private QueueReservation _reservation;
        private int _bound;

        /// <summary>Gets whether a reservation is currently bound to this hold.</summary>
        public bool IsBound => Volatile.Read(ref _bound) != 0;

        /// <summary>Gets the bound reservation. Meaningful only while <see cref="IsBound"/> is true.</summary>
        public QueueReservation Reservation => _reservation;

        /// <summary>Gets the queue that owns the bound reservation.</summary>
        public IAcceptedArticleQueue? Queue => _queue;

        /// <summary>
        /// Records a reserved queue slot and the queue that must release it.
        /// </summary>
        /// <remarks>
        /// Callers must serialize this method with <see cref="TryRelease"/> and
        /// <see cref="TryTakeForCommit"/> (the session <c>_storeGate</c>) so a concurrent
        /// teardown cannot observe <see cref="IsBound"/> as false while
        /// <paramref name="queue"/> and <paramref name="reservation"/> are only partially published.
        /// </remarks>
        public void Bind(IAcceptedArticleQueue? queue, QueueReservation reservation)
        {
            _queue = queue;
            _reservation = reservation;
            // Publish ownership last; paired take/release uses Interlocked.Exchange.
            Volatile.Write(ref _bound, queue is not null ? 1 : 0);
        }

        /// <summary>
        /// Releases the bound reservation if this caller wins ownership.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when this call performed the release;
        /// <see langword="false"/> when there was no bound reservation or another caller already took it.
        /// </returns>
        public bool TryRelease()
        {
            if (!TryTake(out var queue, out var reservation))
            {
                return false;
            }

            queue?.Release(reservation);
            return true;
        }

        /// <summary>
        /// Takes exclusive ownership for a commit attempt without releasing the reservation.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when this caller now owns <paramref name="queue"/> and
        /// <paramref name="reservation"/> and must either <see cref="IAcceptedArticleQueue.TryCommit"/>
        /// or <see cref="IAcceptedArticleQueue.Release"/> them;
        /// <see langword="false"/> when another path already released or took the reservation.
        /// </returns>
        public bool TryTakeForCommit(out IAcceptedArticleQueue? queue, out QueueReservation reservation)
        {
            return TryTake(out queue, out reservation);
        }

        /// <summary>
        /// Records whether a taken reservation was committed into the queue.
        /// Does not call <see cref="IAcceptedArticleQueue.Release"/>.
        /// </summary>
        /// <param name="committed">
        /// <see langword="true"/> when <see cref="IAcceptedArticleQueue.TryCommit"/> returned true.
        /// </param>
        public void MarkCommitOutcome(bool committed)
        {
            Committed = committed;
        }

        /// <summary>Gets whether the reservation was committed into the queue.</summary>
        public bool Committed { get; private set; }

        private bool TryTake(out IAcceptedArticleQueue? queue, out QueueReservation reservation)
        {
            if (Interlocked.Exchange(ref _bound, 0) == 0)
            {
                queue = null;
                reservation = default;
                return false;
            }

            queue = _queue;
            reservation = _reservation;
            _queue = null;
            return true;
        }
    }
}
