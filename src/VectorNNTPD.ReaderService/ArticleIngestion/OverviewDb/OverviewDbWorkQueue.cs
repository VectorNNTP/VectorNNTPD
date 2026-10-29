using System.Threading.Channels;

namespace VectorNNTPD.ReaderService.ArticleIngestion.OverviewDb
{
    /// <summary>
    /// Byte-budgeted <see cref="Channel{T}"/>-backed OverviewDB handoff work queue.
    /// </summary>
    /// <remarks>
    /// In-process only. The admission bound is <see cref="MemoryLimitBytes"/>.
    /// Accounting uses each item's <see cref="OverviewDbWorkItem.ByteLength"/>.
    /// </remarks>
    internal sealed class OverviewDbWorkQueue : IOverviewDbWorkQueue
    {
        private readonly Channel<OverviewDbWorkItem> _channel;
        private readonly object _gate = new();
        private readonly Queue<ByteWaiter> _waiters = new();
        private readonly long _memoryLimit;
        private long _queuedBytes;
        private int _count;
        private int _completed;

        /// <summary>Initializes a new OverviewDB work queue with the given byte budget.</summary>
        public OverviewDbWorkQueue(long memoryLimitBytes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(memoryLimitBytes, 1);
            _memoryLimit = memoryLimitBytes;
            _channel = Channel.CreateUnbounded<OverviewDbWorkItem>(new UnboundedChannelOptions
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
        public int Count => Math.Max(0, Volatile.Read(ref _count));

        /// <inheritdoc />
        public bool IsAccepting => Volatile.Read(ref _completed) == 0;

        /// <inheritdoc />
        public int WaitingProducerCount
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
            OverviewDbWorkItem item,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            var bytes = item.ByteLength;
            if (bytes > _memoryLimit)
            {
                return ArticleEnqueueResult.Rejected;
            }

            if (!IsAccepting)
            {
                return ArticleEnqueueResult.Unavailable;
            }

            var reserved = await ReserveAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (reserved != ArticleEnqueueResult.Accepted)
            {
                return reserved;
            }

            if (!_channel.Writer.TryWrite(item))
            {
                Release(bytes);
                return ArticleEnqueueResult.Unavailable;
            }

            Interlocked.Increment(ref _count);
            return ArticleEnqueueResult.Accepted;
        }

        /// <inheritdoc />
        public async ValueTask<OverviewDbWorkItem?> DequeueAsync(CancellationToken cancellationToken)
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_channel.Reader.TryRead(out var item))
                {
                    Release(item.ByteLength);
                    Interlocked.Decrement(ref _count);
                    return item;
                }
            }

            return null;
        }

        /// <inheritdoc />
        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 1)
            {
                return;
            }

            _channel.Writer.TryComplete();
            List<ByteWaiter> waiters;
            lock (_gate)
            {
                waiters = [.. _waiters];
                _waiters.Clear();
            }

            foreach (var waiter in waiters)
            {
                waiter.TrySetUnavailable();
            }
        }

        private async ValueTask<ArticleEnqueueResult> ReserveAsync(int bytes, CancellationToken cancellationToken)
        {
            while (true)
            {
                ByteWaiter waiter;
                lock (_gate)
                {
                    if (_completed != 0)
                    {
                        return ArticleEnqueueResult.Unavailable;
                    }

                    if (_queuedBytes + bytes <= _memoryLimit)
                    {
                        _queuedBytes += bytes;
                        return ArticleEnqueueResult.Accepted;
                    }

                    waiter = new ByteWaiter(bytes);
                    _waiters.Enqueue(waiter);
                }

                try
                {
                    return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (_gate)
                    {
                        waiter.Cancel();
                    }

                    throw;
                }
            }
        }

        private void Release(int bytes)
        {
            lock (_gate)
            {
                _queuedBytes = Math.Max(0, _queuedBytes - bytes);
                while (_waiters.Count > 0)
                {
                    var next = _waiters.Peek();
                    if (!next.IsWaiting)
                    {
                        _waiters.Dequeue();
                        continue;
                    }

                    if (_queuedBytes + next.Bytes > _memoryLimit)
                    {
                        break;
                    }

                    _waiters.Dequeue();
                    _queuedBytes += next.Bytes;
                    next.TrySetAccepted();
                }
            }
        }

        private sealed class ByteWaiter
        {
            private readonly TaskCompletionSource<ArticleEnqueueResult> _tcs =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _state; // 0 waiting, 1 completed

            public ByteWaiter(int bytes) => Bytes = bytes;

            public int Bytes { get; }

            public bool IsWaiting => Volatile.Read(ref _state) == 0;

            public Task<ArticleEnqueueResult> Task => _tcs.Task;

            public void TrySetAccepted()
            {
                if (Interlocked.Exchange(ref _state, 1) == 0)
                {
                    _tcs.TrySetResult(ArticleEnqueueResult.Accepted);
                }
            }

            public void TrySetUnavailable()
            {
                if (Interlocked.Exchange(ref _state, 1) == 0)
                {
                    _tcs.TrySetResult(ArticleEnqueueResult.Unavailable);
                }
            }

            public void Cancel()
            {
                Interlocked.Exchange(ref _state, 1);
                _tcs.TrySetCanceled();
            }
        }
    }
}
