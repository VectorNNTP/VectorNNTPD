using System.Threading.Channels;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Diagnostics;

namespace VectorNNTPD.ReaderService.ArticleIngestion
{
    /// <summary>
    /// Byte-budgeted <see cref="Channel{T}"/>-backed Transit article ingestion queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The admission bound is <see cref="MemoryLimitBytes"/>
    /// (<c>Nntpd:TransitQueueMemoryLimit</c>, default 1 GiB). Accounting uses each
    /// article's owned payload length (<see cref="InboundArticle.Payload"/>.Length):
    /// canonical <see cref="InboundArticle.Payload"/> / ArtData length. Every queued
    /// item is a CanonicalV1 <c>ArticleRecord</c>. Object overhead is not counted.
    /// This is not a process-wide memory cap.
    /// </para>
    /// <para>
    /// Producers reserve payload bytes atomically before the article is visible
    /// to consumers. Multiple producers therefore cannot oversubscribe the
    /// budget. An article larger than the configured budget is rejected immediately
    /// so admission cannot deadlock waiting for capacity. The historical
    /// 256-article count bound is not an admission limit; the channel is unbounded
    /// and the byte reservation is the resource bound.
    /// </para>
    /// <para>
    /// Every successful reservation has exactly one release: consumer dequeue,
    /// failed write after reserve, or (for waiters) cancel/shutdown before admit.
    /// </para>
    /// <para>
    /// The channel is multi-reader. Concurrent <see cref="DequeueAsync"/> callers are
    /// supported. Items leave the channel in FIFO order; worker processing/completion
    /// order may differ. Byte-budget accounting still serializes on the existing
    /// reservation gate. A successful dequeue transfers ownership and releases the
    /// reservation inside <see cref="DequeueAsync"/>; consumers must not release again.
    /// </para>
    /// </remarks>
    public sealed class ArticleIngestionQueue : IArticleIngestionQueue
    {
        private readonly Channel<InboundArticle> _channel;
        private readonly object _gate = new();
        private readonly Queue<ByteWaiter> _waiters = new();
        private readonly long _memoryLimit;
        private readonly int _maxArticleBytes;
        private long _queuedBytes;
        private long _peakQueuedBytes;
        private int _count;
        private int _peakCount;
        private int _completed;
        private long _admissionFailures;
        private long _admissionWaitTicks;

        /// <summary>Initializes a new instance of the <see cref="ArticleIngestionQueue"/> class.</summary>
        public ArticleIngestionQueue(IOptions<NntpdOptions> options)
            : this(GetIngestionOptions(options), GetMemoryLimit(options))
        {
        }

        /// <summary>
        /// Initializes a new instance with explicit ingestion options and the default 1 GiB budget (tests).
        /// </summary>
        public ArticleIngestionQueue(ArticleIngestionOptions options)
            : this(options, NntpdOptions.DefaultTransitQueueMemoryLimit)
        {
        }

        /// <summary>Initializes a new instance with an explicit Transit queue memory budget (tests).</summary>
        public ArticleIngestionQueue(ArticleIngestionOptions options, long transitQueueMemoryLimit)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxArticleBytes, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(transitQueueMemoryLimit, 1);

            _memoryLimit = transitQueueMemoryLimit;
            _maxArticleBytes = options.MaxArticleBytes;
            _channel = Channel.CreateUnbounded<InboundArticle>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        }

        /// <inheritdoc />
        public long MemoryLimitBytes => _memoryLimit;

        /// <inheritdoc />
        public long QueuedBytes => Volatile.Read(ref _queuedBytes);

        /// <inheritdoc />
        public long PeakQueuedBytes => Volatile.Read(ref _peakQueuedBytes);

        /// <inheritdoc />
        public int MaxArticleBytes => _maxArticleBytes;

        /// <inheritdoc />
        public int Count => Math.Max(0, Volatile.Read(ref _count));

        /// <inheritdoc />
        public int PeakCount => Math.Max(0, Volatile.Read(ref _peakCount));

        /// <inheritdoc />
        public bool IsAccepting => Volatile.Read(ref _completed) == 0;

        /// <inheritdoc />
        public int WaitingProducerCount => DebugWaiterCount;

        /// <inheritdoc />
        public long AdmissionFailureCount => Volatile.Read(ref _admissionFailures);

        /// <inheritdoc />
        public long AdmissionWaitTicks => Volatile.Read(ref _admissionWaitTicks);

        /// <summary>Gets the number of producers waiting for byte-budget capacity (tests).</summary>
        internal int DebugWaiterCount
        {
            get
            {
                lock (_gate)
                {
                    var n = 0;
                    // codeql[cs/linq/missed-where] Lock-held count without LINQ allocation.
                    foreach (var waiter in _waiters)
                    {
                        if (waiter.IsWaiting)
                        {
                            n++;
                        }
                    }

                    return n;
                }
            }
        }

        /// <inheritdoc />
        public async ValueTask<ArticleEnqueueResult> EnqueueAsync(
            InboundArticle article,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(article);
            EnsureCanonical(article);

            var bytes = article.Payload.Length;
            if (bytes > _memoryLimit)
            {
                RecordAdmission(ArticleEnqueueResult.Rejected, waitTicks: 0);
                return ArticleEnqueueResult.Rejected;
            }

            if (!IsAccepting)
            {
                RecordAdmission(ArticleEnqueueResult.Unavailable, waitTicks: 0);
                return ArticleEnqueueResult.Unavailable;
            }

            var waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var reserved = await ReserveAsync(bytes, cancellationToken).ConfigureAwait(false);
            var waitTicks = System.Diagnostics.Stopwatch.GetTimestamp() - waitStarted;
            IngestionPipelineMetrics.Shared.RecordEnqueueWaitTicks(waitTicks);
            if (reserved.WaitedForBudget)
            {
                IngestionPipelineMetrics.Shared.RecordBudgetWaitTicks(waitTicks);
            }

            if (reserved.Result != ArticleEnqueueResult.Accepted)
            {
                RecordAdmission(reserved.Result, waitTicks);
                return reserved.Result;
            }

            var written = TryWriteReserved(article, bytes)
                ? ArticleEnqueueResult.Accepted
                : ArticleEnqueueResult.Unavailable;
            RecordAdmission(written, waitTicks);
            return written;
        }

        /// <inheritdoc />
        public bool TryProbeCapacity()
        {
            lock (_gate)
            {
                return _completed == 0 && _queuedBytes < _memoryLimit;
            }
        }

        /// <inheritdoc />
        public ArticleEnqueueResult TryAdmit(InboundArticle article)
        {
            ArgumentNullException.ThrowIfNull(article);
            EnsureCanonical(article);

            var bytes = article.Payload.Length;
            if (bytes > _memoryLimit)
            {
                RecordAdmission(ArticleEnqueueResult.Rejected, waitTicks: 0);
                return ArticleEnqueueResult.Rejected;
            }

            if (!IsAccepting)
            {
                RecordAdmission(ArticleEnqueueResult.Unavailable, waitTicks: 0);
                return ArticleEnqueueResult.Unavailable;
            }

            if (!TryReserveImmediate(bytes))
            {
                RecordAdmission(ArticleEnqueueResult.Full, waitTicks: 0);
                return ArticleEnqueueResult.Full;
            }

            var written = TryWriteReserved(article, bytes)
                ? ArticleEnqueueResult.Accepted
                : ArticleEnqueueResult.Unavailable;
            RecordAdmission(written, waitTicks: 0);
            return written;
        }

        /// <inheritdoc />
        public bool TryEnqueue(InboundArticle article) => TryAdmit(article) == ArticleEnqueueResult.Accepted;

        /// <inheritdoc />
        public void Complete()
        {
            List<ByteWaiter>? pending = null;
            lock (_gate)
            {
                Interlocked.Exchange(ref _completed, 1);
                _channel.Writer.TryComplete();
                if (_waiters.Count > 0)
                {
                    pending = new List<ByteWaiter>(_waiters.Count);
                    while (_waiters.TryDequeue(out var waiter))
                    {
                        if (waiter.TryCancel())
                        {
                            pending.Add(waiter);
                        }
                    }
                }
            }

            if (pending is null)
            {
                return;
            }

            foreach (var waiter in pending)
            {
                waiter.Tcs.TrySetResult(false);
            }
        }

        /// <inheritdoc />
        public async ValueTask<InboundArticle?> DequeueAsync(CancellationToken cancellationToken)
        {
            // WaitToReadAsync + TryRead is the Channel multi-reader contract: another
            // consumer may take the item between the wait and the read, so retry.
            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_channel.Reader.TryRead(out var article) || article is null)
                {
                    continue;
                }

                Interlocked.Decrement(ref _count);
                Release(article.Payload.Length);
                return article;
            }

            return null;
        }

        /// <summary>
        /// Completes the channel writer without marking the queue unavailable (tests).
        /// The next reserve-then-write fails and must release the reservation.
        /// </summary>
        internal void CompleteChannelWithoutMarkingUnavailableForTests() => _channel.Writer.TryComplete();

        private async ValueTask<(ArticleEnqueueResult Result, bool WaitedForBudget)> ReserveAsync(
            int bytes,
            CancellationToken cancellationToken)
        {
            ByteWaiter? waiter;
            lock (_gate)
            {
                if (_completed != 0)
                {
                    return (ArticleEnqueueResult.Unavailable, false);
                }

                if (bytes <= _memoryLimit - _queuedBytes)
                {
                    AddReservation(bytes);
                    return (ArticleEnqueueResult.Accepted, false);
                }

                waiter = new ByteWaiter(bytes);
                _waiters.Enqueue(waiter);
            }

            using var registration = cancellationToken.Register(
                static state => ((ByteWaiter)state!).CancelFromToken(),
                waiter);

            try
            {
                var admitted = await waiter.Tcs.Task.ConfigureAwait(false);
                return (
                    admitted ? ArticleEnqueueResult.Accepted : ArticleEnqueueResult.Unavailable,
                    true);
            }
            catch (OperationCanceledException)
            {
                waiter.CancelFromToken();
                return (ArticleEnqueueResult.Unavailable, true);
            }
        }

        private void RecordAdmission(ArticleEnqueueResult result, long waitTicks)
        {
            if (result is ArticleEnqueueResult.Rejected
                or ArticleEnqueueResult.Unavailable
                or ArticleEnqueueResult.Full)
            {
                Interlocked.Increment(ref _admissionFailures);
            }

            if (waitTicks > 0)
            {
                Interlocked.Add(ref _admissionWaitTicks, waitTicks);
            }
        }

        private bool TryReserveImmediate(int bytes)
        {
            lock (_gate)
            {
                if (_completed != 0 || bytes > _memoryLimit - _queuedBytes)
                {
                    return false;
                }

                AddReservation(bytes);
                return true;
            }
        }

        private void AddReservation(int bytes)
        {
            _queuedBytes += bytes;
            if (_queuedBytes > _peakQueuedBytes)
            {
                _peakQueuedBytes = _queuedBytes;
            }
        }

        private static void EnsureCanonical(InboundArticle article)
        {
            if (article.Record.ParseStatus != ArticleParseStatus.CanonicalV1)
            {
                throw new ArgumentException(
                    "The ingress queue accepts CanonicalV1 ArticleRecord items only.",
                    nameof(article));
            }
        }

        private bool TryWriteReserved(InboundArticle article, int bytes)
        {
            if (_channel.Writer.TryWrite(article))
            {
                var now = Interlocked.Increment(ref _count);
                UpdatePeakCount(now);
                return true;
            }

            Release(bytes);
            return false;
        }

        private void Release(int bytes)
        {
            List<ByteWaiter>? admitted;
            lock (_gate)
            {
                if (bytes > 0)
                {
                    var next = _queuedBytes - bytes;
                    _queuedBytes = next < 0 ? 0 : next;
                }

                admitted = AdmitWaiters();
            }

            if (admitted is null)
            {
                return;
            }

            foreach (var waiter in admitted)
            {
                waiter.Tcs.TrySetResult(true);
            }
        }

        private List<ByteWaiter>? AdmitWaiters()
        {
            List<ByteWaiter>? admitted = null;
            while (_waiters.TryPeek(out var waiter))
            {
                if (!waiter.IsWaiting)
                {
                    _waiters.Dequeue();
                    continue;
                }

                if (_completed != 0)
                {
                    break;
                }

                if (waiter.Bytes > _memoryLimit - _queuedBytes)
                {
                    break;
                }

                if (!waiter.TryAdmit())
                {
                    _waiters.Dequeue();
                    continue;
                }

                _waiters.Dequeue();
                AddReservation(waiter.Bytes);
                admitted ??= [];
                admitted.Add(waiter);
            }

            return admitted;
        }

        private void UpdatePeakCount(int now)
        {
            var peak = Volatile.Read(ref _peakCount);
            while (now > peak)
            {
                var previous = Interlocked.CompareExchange(ref _peakCount, now, peak);
                if (previous == peak)
                {
                    break;
                }

                peak = previous;
            }
        }

        private static ArticleIngestionOptions GetIngestionOptions(IOptions<NntpdOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return options.Value.ArticleIngestion ?? new ArticleIngestionOptions();
        }

        private static long GetMemoryLimit(IOptions<NntpdOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return options.Value.TransitQueueMemoryLimit;
        }

        private sealed class ByteWaiter
        {
            private const int Waiting = 0;
            private const int Admitted = 1;
            private const int Cancelled = 2;

            private int _state;

            public ByteWaiter(int bytes)
            {
                Bytes = bytes;
            }

            public int Bytes { get; }

            public TaskCompletionSource<bool> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool IsWaiting => Volatile.Read(ref _state) == Waiting;

            public bool TryAdmit() => Interlocked.CompareExchange(ref _state, Admitted, Waiting) == Waiting;

            public bool TryCancel() => Interlocked.CompareExchange(ref _state, Cancelled, Waiting) == Waiting;

            public void CancelFromToken()
            {
                if (TryCancel())
                {
                    Tcs.TrySetResult(false);
                }
            }
        }
    }
}
