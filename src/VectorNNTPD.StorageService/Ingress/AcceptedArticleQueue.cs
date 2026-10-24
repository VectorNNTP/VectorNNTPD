using System.Threading.Channels;
using VectorNNTPD.StorageService.Configuration;

namespace VectorNNTPD.StorageService.Ingress
{

    /// <summary>
    /// Multi-writer, multi-reader queue of accepted articles, bounded by accounted bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryEnqueue"/> returns false when the byte budget would be exceeded or the
    /// queue has been completed. The channel itself stays unbounded so a reserved article is
    /// not dropped after the budget check. Enqueue does not copy <see cref="AcceptedArticle.ArtData"/>.
    /// </para>
    /// <para>
    /// The budget covers queued payloads, admission reservations, and dequeued in-flight articles
    /// (including capacity-blocked holds) until <see cref="ReleaseProcessed"/>. Dequeue alone does
    /// not free budget. The default budget is one gibibyte. At 768,000-byte articles that is about
    /// 1,398 articles. TAKETHIS 239 is sent only after a true enqueue. A false enqueue is the
    /// overload signal and is not a durable-storage acknowledgement.
    /// </para>
    /// </remarks>
    public sealed class AcceptedArticleQueue : IAcceptedArticleQueue
    {
        private readonly long _maxQueuedBytes;
        private readonly TimeProvider _time;
        private readonly Channel<AcceptedArticle> _channel = Channel.CreateUnbounded<AcceptedArticle>(
            new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        private readonly object _gate = new();
        private readonly Queue<long> _stamps = new();
        private readonly Dictionary<long, int> _reservations = new();
        private readonly Queue<ReservationWaiter> _waiters = new();
        private long _enqueued;
        private long _dequeued;
        private long _queuedBytes;
        private long _reservedBytes;
        private long _inFlightBytes;
        private long _nextReservationId;
        private bool _completed;

        /// <summary>Initializes the queue with the default one-gibibyte budget.</summary>
        public AcceptedArticleQueue()
            : this(SpoolPersistenceOptions.DefaultMaxQueuedBytes, TimeProvider.System)
        {
        }

        /// <summary>Initializes the queue with an explicit byte budget and clock.</summary>
        /// <param name="maxQueuedBytes">Maximum queued article bytes. Must be at least 1.</param>
        /// <param name="time">Clock used for oldest-item age. The queue stores timestamps, not the articles' clocks.</param>
        public AcceptedArticleQueue(long maxQueuedBytes, TimeProvider time)
        {
            ArgumentNullException.ThrowIfNull(time);
            if (maxQueuedBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes));
            }

            _maxQueuedBytes = maxQueuedBytes;
            _time = time;
        }

        /// <summary>Maximum bytes <see cref="TryEnqueue"/> will hold.</summary>
        public long MaxQueuedBytes => _maxQueuedBytes;

        /// <summary>Articles successfully written to the channel.</summary>
        public long EnqueuedCount
        {
            get
            {
                lock (_gate)
                {
                    return _enqueued;
                }
            }
        }

        /// <summary>Articles removed by <see cref="TryDequeue"/> or <see cref="DequeueAsync"/>.</summary>
        public long DequeuedCount
        {
            get
            {
                lock (_gate)
                {
                    return _dequeued;
                }
            }
        }

        /// <inheritdoc />
        public long QueuedBytes
        {
            get
            {
                lock (_gate)
                {
                    return _queuedBytes;
                }
            }
        }

        /// <inheritdoc />
        public long ReservedBytes
        {
            get
            {
                lock (_gate)
                {
                    return _reservedBytes;
                }
            }
        }

        /// <inheritdoc />
        public long AccountedBytes
        {
            get
            {
                lock (_gate)
                {
                    return _queuedBytes + _reservedBytes + _inFlightBytes;
                }
            }
        }

        /// <inheritdoc />
        public long InFlightBytes
        {
            get
            {
                lock (_gate)
                {
                    return _inFlightBytes;
                }
            }
        }

        /// <inheritdoc />
        public int AdmissionWaiters
        {
            get
            {
                lock (_gate)
                {
                    return _waiters.Count;
                }
            }
        }

        /// <inheritdoc />
        public int QueuedCount
        {
            get
            {
                lock (_gate)
                {
                    return _stamps.Count;
                }
            }
        }

        /// <inheritdoc />
        public bool IsCompleted
        {
            get
            {
                lock (_gate)
                {
                    return _completed;
                }
            }
        }

        /// <inheritdoc />
        public TimeSpan OldestAge(TimeProvider time)
        {
            ArgumentNullException.ThrowIfNull(time);
            long stamp;
            lock (_gate)
            {
                if (_stamps.Count == 0)
                {
                    return TimeSpan.Zero;
                }

                stamp = _stamps.Peek();
            }

            var age = time.GetElapsedTime(stamp);
            return age < TimeSpan.Zero ? TimeSpan.Zero : age;
        }

        /// <inheritdoc />
        public bool TryEnqueue(AcceptedArticle article)
        {
            if (article.ArtSize < 1 || article.ArtData.Length != article.ArtSize)
            {
                throw new ArgumentException("ArtSize must match ArtData.Length and be at least 1.", nameof(article));
            }

            lock (_gate)
            {
                var accounted = _queuedBytes + _reservedBytes + _inFlightBytes;
                if (_completed || article.ArtSize > _maxQueuedBytes || accounted > _maxQueuedBytes - article.ArtSize)
                {
                    return false;
                }

                if (!_channel.Writer.TryWrite(article))
                {
                    return false;
                }

                _enqueued++;
                _queuedBytes += article.ArtSize;
                _stamps.Enqueue(_time.GetTimestamp());
                return true;
            }
        }

        /// <inheritdoc />
        public bool TryDequeue(out AcceptedArticleDelivery delivery)
        {
            if (!_channel.Reader.TryRead(out var article))
            {
                delivery = default;
                return false;
            }

            List<ReservationWaiter>? granted;
            lock (_gate)
            {
                granted = NoteDequeued(article.ArtSize);
            }

            DisposeRegistrations(granted);
            delivery = new AcceptedArticleDelivery(true, article);
            return true;
        }

        /// <inheritdoc />
        public async ValueTask<AcceptedArticleDelivery> DequeueAsync(CancellationToken cancellationToken)
        {
            try
            {
                var article = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                List<ReservationWaiter>? granted;
                lock (_gate)
                {
                    granted = NoteDequeued(article.ArtSize);
                }

                DisposeRegistrations(granted);
                return new AcceptedArticleDelivery(true, article);
            }
            catch (ChannelClosedException)
            {
                return default;
            }
        }

        /// <inheritdoc />
        public void Complete()
        {
            ReservationWaiter[] pending;
            lock (_gate)
            {
                _completed = true;
                _channel.Writer.TryComplete();
                pending = _waiters.ToArray();
                _waiters.Clear();
            }

            foreach (var waiter in pending)
            {
                waiter.DisposeRegistration();
                waiter.Completion.TrySetResult(null);
            }
        }

        /// <inheritdoc />
        public bool TryReserve(int bytes, out QueueReservation reservation)
        {
            if (bytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes));
            }

            lock (_gate)
            {
                if (!TryTakeReservation(bytes, out reservation))
                {
                    return false;
                }
            }

            return true;
        }

        /// <inheritdoc />
        public ValueTask<QueueReservation?> ReserveAsync(int bytes, CancellationToken cancellationToken)
        {
            if (bytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes));
            }

            ReservationWaiter waiter;
            List<ReservationWaiter>? granted = null;
            lock (_gate)
            {
                if (TryTakeReservation(bytes, out var reservation))
                {
                    return new ValueTask<QueueReservation?>(reservation);
                }

                if (_completed || bytes > _maxQueuedBytes)
                {
                    return new ValueTask<QueueReservation?>((QueueReservation?)null);
                }

                waiter = new ReservationWaiter(bytes, this);
                _waiters.Enqueue(waiter);
                granted = PumpWaiters();
            }

            DisposeRegistrations(granted);
            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(static state =>
                {
                    var self = (ReservationWaiter)state!;
                    self.Owner.CancelWaiter(self);
                }, waiter);
                if (waiter.Completion.Task.IsCompleted)
                {
                    waiter.DisposeRegistration();
                }
            }

            return new ValueTask<QueueReservation?>(waiter.Completion.Task);
        }

        /// <inheritdoc />
        public bool TryCommit(QueueReservation reservation, AcceptedArticle article)
        {
            if (article.ArtSize < 1 || article.ArtData.Length != article.ArtSize)
            {
                throw new ArgumentException("ArtSize must match ArtData.Length and be at least 1.", nameof(article));
            }

            bool committed;
            List<ReservationWaiter>? granted;
            lock (_gate)
            {
                if (!_reservations.Remove(reservation.Id, out var reserved))
                {
                    return false;
                }

                _reservedBytes -= reserved;
                if (article.ArtSize == reserved && _channel.Writer.TryWrite(article))
                {
                    _enqueued++;
                    _queuedBytes += article.ArtSize;
                    _stamps.Enqueue(_time.GetTimestamp());
                    committed = true;
                    granted = null;
                }
                else
                {
                    committed = false;
                    granted = PumpWaiters();
                }
            }

            DisposeRegistrations(granted);
            return committed;
        }

        /// <inheritdoc />
        public void Release(QueueReservation reservation)
        {
            List<ReservationWaiter>? granted;
            lock (_gate)
            {
                if (!_reservations.Remove(reservation.Id, out var reserved))
                {
                    return;
                }

                _reservedBytes -= reserved;
                granted = PumpWaiters();
            }

            DisposeRegistrations(granted);
        }

        /// <inheritdoc />
        public void ReleaseProcessed(int bytes)
        {
            if (bytes < 1)
            {
                return;
            }

            List<ReservationWaiter>? granted;
            lock (_gate)
            {
                if (_inFlightBytes <= 0)
                {
                    return;
                }

                _inFlightBytes = _inFlightBytes > bytes ? _inFlightBytes - bytes : 0;
                granted = PumpWaiters();
            }

            DisposeRegistrations(granted);
        }

        /// <summary>
        /// Moves dequeued bytes from the queue into in-flight so capacity-blocked holds still
        /// consume the admission budget. Does not grant waiters (accounted total is unchanged).
        /// </summary>
        /// <param name="bytes">Bytes removed from the queue channel.</param>
        /// <returns>Always null; dequeue does not free budget for new admissions.</returns>
        private List<ReservationWaiter>? NoteDequeued(int bytes)
        {
            _dequeued++;
            _queuedBytes -= bytes;
            _inFlightBytes += bytes;
            if (_stamps.Count > 0)
            {
                _stamps.Dequeue();
            }

            return null;
        }

        /// <summary>
        /// Reserves <paramref name="bytes"/> when the queue is open, nobody is waiting, and the bytes fit.
        /// </summary>
        /// <param name="bytes">Positive article length.</param>
        /// <param name="reservation">Set when the method returns <see langword="true"/>.</param>
        private bool TryTakeReservation(int bytes, out QueueReservation reservation)
        {
            reservation = default;
            if (_completed || _waiters.Count > 0 || bytes > _maxQueuedBytes)
            {
                return false;
            }

            var accounted = _queuedBytes + _reservedBytes + _inFlightBytes;
            if (accounted > _maxQueuedBytes - bytes)
            {
                return false;
            }

            reservation = AddReservation(bytes);
            return true;
        }

        /// <summary>Records a reservation. Caller holds <see cref="_gate"/>.</summary>
        /// <param name="bytes">Reserved article length.</param>
        private QueueReservation AddReservation(int bytes)
        {
            var id = ++_nextReservationId;
            _reservations.Add(id, bytes);
            _reservedBytes += bytes;
            return new QueueReservation(id, bytes);
        }

        /// <summary>
        /// Grants reservations to the oldest waiters while the head of the line fits.
        /// A head that does not fit blocks later waiters, so one session cannot skip the line.
        /// Completions are published under the lock. Registrations are disposed by the caller
        /// after the lock is released, so a cancellation callback cannot deadlock on this gate.
        /// </summary>
        /// <returns>Granted waiters whose registrations are still outstanding.</returns>
        private List<ReservationWaiter>? PumpWaiters()
        {
            List<ReservationWaiter>? granted = null;
            while (_waiters.Count > 0)
            {
                var head = _waiters.Peek();
                var accounted = _queuedBytes + _reservedBytes + _inFlightBytes;
                if (head.Bytes > _maxQueuedBytes || accounted > _maxQueuedBytes - head.Bytes)
                {
                    return granted;
                }

                _waiters.Dequeue();
                var reservation = AddReservation(head.Bytes);
                head.Completion.TrySetResult(reservation);
                granted ??= new List<ReservationWaiter>(1);
                granted.Add(head);
            }

            return granted;
        }

        /// <summary>Disposes cancellation registrations for waiters that already have a reservation.</summary>
        /// <param name="granted">Waiters granted by <see cref="PumpWaiters"/>.</param>
        private static void DisposeRegistrations(List<ReservationWaiter>? granted)
        {
            if (granted is null)
            {
                return;
            }

            foreach (var waiter in granted)
            {
                waiter.DisposeRegistration();
            }
        }

        /// <summary>Removes a cancelled waiter. A reservation already granted stays with the caller.</summary>
        /// <param name="waiter">The waiter to drop.</param>
        private void CancelWaiter(ReservationWaiter waiter)
        {
            List<ReservationWaiter>? granted = null;
            lock (_gate)
            {
                if (waiter.Completion.Task.IsCompleted)
                {
                    return;
                }

                var removed = false;
                var kept = new Queue<ReservationWaiter>(_waiters.Count);
                while (_waiters.Count > 0)
                {
                    var current = _waiters.Dequeue();
                    if (ReferenceEquals(current, waiter))
                    {
                        removed = true;
                        continue;
                    }

                    kept.Enqueue(current);
                }

                while (kept.Count > 0)
                {
                    _waiters.Enqueue(kept.Dequeue());
                }

                if (removed)
                {
                    granted = PumpWaiters();
                }
            }

            waiter.DisposeRegistration();
            waiter.Completion.TrySetCanceled();
            DisposeRegistrations(granted);
        }

        /// <summary>One FIFO admission waiter. The completion runs asynchronously.</summary>
        private sealed class ReservationWaiter
        {
            public ReservationWaiter(int bytes, AcceptedArticleQueue owner)
            {
                Bytes = bytes;
                Owner = owner;
                Completion = new TaskCompletionSource<QueueReservation?>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public int Bytes { get; }

            public AcceptedArticleQueue Owner { get; }

            public TaskCompletionSource<QueueReservation?> Completion { get; }

            public CancellationTokenRegistration Registration { get; set; }

            public void DisposeRegistration()
            {
                Registration.Dispose();
            }
        }
    }
}
