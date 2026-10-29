using System.Threading.Channels;

namespace VectorNNTPD.TransitService.History
{
    /// <summary>Bounded queue of HistoryDB Redis writes. Full writes are dropped, not unbounded.</summary>
    internal sealed class HistoryWriteQueue
    {
        /// <summary>Default bound for pending Redis history writes.</summary>
        public const int DefaultCapacity = 4096;

        private readonly Channel<HistoryDigest> _channel;
        private int _count;

        /// <summary>Initializes a new instance of the <see cref="HistoryWriteQueue"/> class.</summary>
        public HistoryWriteQueue(int capacity = DefaultCapacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            Capacity = capacity;
            _channel = Channel.CreateBounded<HistoryDigest>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        }

        /// <summary>Gets the queue capacity.</summary>
        public int Capacity { get; }

        /// <summary>Gets the approximate number of queued writes.</summary>
        public int Count => Math.Max(0, Volatile.Read(ref _count));

        /// <summary>Attempts to enqueue a Redis write. Returns <see langword="false"/> when full or completed.</summary>
        public bool TryEnqueue(in HistoryDigest digest)
        {
            if (!_channel.Writer.TryWrite(digest))
            {
                return false;
            }

            Interlocked.Increment(ref _count);
            return true;
        }

        /// <summary>Completes the writer so the consumer can drain and exit.</summary>
        public void Complete() => _channel.Writer.TryComplete();

        /// <summary>Dequeues the next write, or <see langword="null"/> when completed and empty.</summary>
        public async ValueTask<HistoryDigest?> DequeueAsync(CancellationToken cancellationToken)
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_channel.Reader.TryRead(out var digest))
                {
                    Interlocked.Decrement(ref _count);
                    return digest;
                }
            }

            return null;
        }
    }
}
