using System.Collections.Concurrent;

namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>
    /// Always-on Transit peer counters keyed by configured identifier.
    /// </summary>
    public sealed class TransitPeerMetrics : ITransitPeerMetrics
    {
        private readonly ConcurrentDictionary<string, Counters> _peers = new(StringComparer.Ordinal);

        /// <inheritdoc />
        public void RecordAccepted(string peerId) => Get(peerId).IncrementAccepted();

        /// <inheritdoc />
        public void RetractAccepted(string peerId) => Get(peerId).DecrementAccepted();

        /// <inheritdoc />
        public void RecordRejected(string peerId) => Get(peerId).IncrementRejected();

        /// <inheritdoc />
        public void RecordSessionRegistered(string peerId) => Get(peerId).RegisterSession();

        /// <inheritdoc />
        public void RecordSessionUnregistered(string peerId) => Get(peerId).UnregisterSession();

        /// <inheritdoc />
        public void RecordArticleReceived(string peerId, int bytes) => Get(peerId).AddArticle(bytes);

        /// <inheritdoc />
        public void RecordCheck(string peerId) => Get(peerId).IncrementChecks();

        /// <inheritdoc />
        public void RecordTransmitted(string peerId, long articles = 1) => Get(peerId).AddTransmitted(articles);

        /// <inheritdoc />
        public TransitPeerMetricsSnapshot Capture(string peerId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
            return _peers.TryGetValue(peerId, out var counters)
                ? counters.Capture(peerId)
                : new TransitPeerMetricsSnapshot(peerId, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private Counters Get(string peerId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
            return _peers.GetOrAdd(peerId, static _ => new Counters());
        }

        private sealed class Counters
        {
            private int _active;
            private int _peak;
            private long _accepted;
            private long _rejected;
            private long _articlesReceived;
            private long _articleBytes;
            private long _checks;
            private long _transmitted;

            public void IncrementAccepted() => Interlocked.Increment(ref _accepted);

            public void DecrementAccepted()
            {
                while (true)
                {
                    var current = Volatile.Read(ref _accepted);
                    if (current <= 0)
                    {
                        return;
                    }

                    if (Interlocked.CompareExchange(ref _accepted, current - 1, current) == current)
                    {
                        return;
                    }
                }
            }

            public void IncrementRejected() => Interlocked.Increment(ref _rejected);

            public void IncrementChecks() => Interlocked.Increment(ref _checks);

            public void RegisterSession()
            {
                var next = Interlocked.Increment(ref _active);
                UpdatePeak(next);
            }

            public void UnregisterSession()
            {
                while (true)
                {
                    var current = Volatile.Read(ref _active);
                    if (current <= 0)
                    {
                        return;
                    }

                    if (Interlocked.CompareExchange(ref _active, current - 1, current) == current)
                    {
                        return;
                    }
                }
            }

            public void AddArticle(int bytes)
            {
                Interlocked.Increment(ref _articlesReceived);
                if (bytes > 0)
                {
                    Interlocked.Add(ref _articleBytes, bytes);
                }
            }

            public void AddTransmitted(long articles)
            {
                if (articles > 0)
                {
                    Interlocked.Add(ref _transmitted, articles);
                }
            }

            public TransitPeerMetricsSnapshot Capture(string peerId) =>
                new(
                    peerId,
                    Math.Max(0, Volatile.Read(ref _active)),
                    Volatile.Read(ref _peak),
                    Volatile.Read(ref _accepted),
                    Volatile.Read(ref _rejected),
                    Volatile.Read(ref _articlesReceived),
                    Volatile.Read(ref _articleBytes),
                    Volatile.Read(ref _checks),
                    Volatile.Read(ref _transmitted));

            private void UpdatePeak(int candidate)
            {
                while (true)
                {
                    var current = Volatile.Read(ref _peak);
                    if (candidate <= current)
                    {
                        return;
                    }

                    if (Interlocked.CompareExchange(ref _peak, candidate, current) == current)
                    {
                        return;
                    }
                }
            }
        }
    }
}
