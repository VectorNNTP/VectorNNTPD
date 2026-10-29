using System.Threading.Channels;

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>
    /// Bounded rejection-evidence queue. Full enqueues are dropped and counted;
    /// they never invert a 441/240 decision.
    /// </summary>
    public sealed class PostFilterRejectionEvidenceQueue : IPostFilterRejectionEvidenceQueue
    {
        /// <summary>Default bound. Evidence rows can include full article bytes.</summary>
        public const int DefaultCapacity = 128;

        private readonly Channel<PostFilterRejectionEvidence> _channel;
        private int _count;
        private long _dropped;
        private long _written;
        private long _writeFailures;

        /// <summary>Initializes a bounded queue.</summary>
        public PostFilterRejectionEvidenceQueue(int capacity = DefaultCapacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            Capacity = capacity;
            _channel = Channel.CreateBounded<PostFilterRejectionEvidence>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        }

        /// <inheritdoc />
        public int Capacity { get; }

        /// <inheritdoc />
        public int Count => Math.Max(0, Volatile.Read(ref _count));

        /// <inheritdoc />
        public long Dropped => Volatile.Read(ref _dropped);

        /// <inheritdoc />
        public long Written => Volatile.Read(ref _written);

        /// <inheritdoc />
        public long WriteFailures => Volatile.Read(ref _writeFailures);

        /// <inheritdoc />
        public bool TryEnqueue(PostFilterRejectionEvidence evidence)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            if (!_channel.Writer.TryWrite(evidence))
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            Interlocked.Increment(ref _count);
            return true;
        }

        /// <inheritdoc />
        public void Complete() => _channel.Writer.TryComplete();

        /// <inheritdoc />
        public async ValueTask<PostFilterRejectionEvidence?> DequeueAsync(CancellationToken cancellationToken)
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_channel.Reader.TryRead(out var evidence))
                {
                    Interlocked.Decrement(ref _count);
                    return evidence;
                }
            }

            return null;
        }

        /// <summary>Records a successful database insert.</summary>
        internal void RecordWritten() => Interlocked.Increment(ref _written);

        /// <summary>Records a failed database insert.</summary>
        internal void RecordWriteFailure() => Interlocked.Increment(ref _writeFailures);
    }
}
