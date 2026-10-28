namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>Aggregate counters for one <see cref="LocalArticleNntpPeer"/> lifetime.</summary>
    internal sealed class LocalArticleNntpPeerMetrics
    {
        private long _acceptedConnections;
        private long _rejectedConnections;
        private long _articleRequests;
        private long _articleSuccess;
        private long _articleMissing;
        private long _articleErrors;
        private long _logicalBytes;
        private long _wireBytes;
        private long _protocolErrors;
        private int _outstandingArticles;
        private int _maxOutstandingArticles;
        private int _activeSessions;
        private int _maxActiveSessions;

        internal long AcceptedConnections => Volatile.Read(ref _acceptedConnections);
        internal long RejectedConnections => Volatile.Read(ref _rejectedConnections);
        internal long ArticleRequests => Volatile.Read(ref _articleRequests);
        internal long ArticleSuccess => Volatile.Read(ref _articleSuccess);
        internal long ArticleMissing => Volatile.Read(ref _articleMissing);
        internal long ArticleErrors => Volatile.Read(ref _articleErrors);
        internal long LogicalBytesTransferred => Volatile.Read(ref _logicalBytes);
        internal long WireBytesTransferred => Volatile.Read(ref _wireBytes);
        internal long ProtocolErrors => Volatile.Read(ref _protocolErrors);
        internal int OutstandingArticles => Volatile.Read(ref _outstandingArticles);
        internal int MaxOutstandingArticles => Volatile.Read(ref _maxOutstandingArticles);
        internal int ActiveSessions => Volatile.Read(ref _activeSessions);
        internal int MaxActiveSessions => Volatile.Read(ref _maxActiveSessions);

        internal void ConnectionAccepted() => Interlocked.Increment(ref _acceptedConnections);

        internal void ConnectionRejected() => Interlocked.Increment(ref _rejectedConnections);

        internal void SessionStarted()
        {
            var n = Interlocked.Increment(ref _activeSessions);
            UpdateMax(ref _maxActiveSessions, n);
        }

        internal void SessionEnded() => Interlocked.Decrement(ref _activeSessions);

        internal void ArticleBegin()
        {
            Interlocked.Increment(ref _articleRequests);
            var n = Interlocked.Increment(ref _outstandingArticles);
            UpdateMax(ref _maxOutstandingArticles, n);
        }

        internal void RecordArticleSuccess(int logicalBytes, int wireBytes)
        {
            Interlocked.Increment(ref _articleSuccess);
            Interlocked.Add(ref _logicalBytes, logicalBytes);
            Interlocked.Add(ref _wireBytes, wireBytes);
            Interlocked.Decrement(ref _outstandingArticles);
        }

        internal void RecordArticleMissing()
        {
            Interlocked.Increment(ref _articleMissing);
            Interlocked.Decrement(ref _outstandingArticles);
        }

        internal void RecordArticleError()
        {
            Interlocked.Increment(ref _articleErrors);
            Interlocked.Decrement(ref _outstandingArticles);
        }

        internal void RecordProtocolError() => Interlocked.Increment(ref _protocolErrors);

        private static void UpdateMax(ref int location, int candidate)
        {
            var observed = Volatile.Read(ref location);
            while (candidate > observed)
            {
                var prev = Interlocked.CompareExchange(ref location, candidate, observed);
                if (prev == observed)
                {
                    return;
                }

                observed = prev;
            }
        }
    }
}
