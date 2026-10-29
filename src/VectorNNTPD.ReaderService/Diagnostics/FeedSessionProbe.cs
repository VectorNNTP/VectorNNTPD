using System.Diagnostics;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.History;

namespace VectorNNTPD.ReaderService.Diagnostics
{
    /// <summary>
    /// Per-session counters and wait totals for opt-in feed diagnostics.
    /// </summary>
    /// <remarks>
    /// Hot-path updates are Interlocked / Volatile only. No logging. No Message-IDs or payloads.
    /// </remarks>
    public sealed class FeedSessionProbe
    {
        private readonly long _startedTimestamp;
        private int _state;
        private long _commands;
        private long _checks;
        private long _articlesReceived;
        private long _articlesCompleted;
        private long _articleBytes;
        private long _receiveTicks;
        private long _historyTicks;
        private long _queueWaitTicks;
        private long _responseTicks;
        private long _maxReceiveTicks;
        private long _maxHistoryTicks;
        private long _maxQueueWaitTicks;
        private long _maxArticleIntervalTicks;
        private long _lastActivityTimestamp;
        private long _lastArticleCompleteTimestamp;
        private long _historyHits;
        private long _historyMisses;
        private long _historyErrors;
        private long _queueAccepted;
        private long _queueRejected;
        private long _queueUnavailable;
        private long _queueAdmittedBytes;
        private long _duplicates;

        /// <summary>Initializes a probe for one accepted session.</summary>
        public FeedSessionProbe(string remote, string peerName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(remote);
            ArgumentException.ThrowIfNullOrWhiteSpace(peerName);
            Remote = remote;
            PeerName = peerName;
            _startedTimestamp = Stopwatch.GetTimestamp();
            _lastActivityTimestamp = _startedTimestamp;
        }

        /// <summary>Gets the effective client endpoint (<c>ip:port</c>).</summary>
        public string Remote { get; }

        /// <summary>Gets the Transit identifier, or <c>none</c>.</summary>
        public string PeerName { get; }

        /// <summary>Gets the session age.</summary>
        public TimeSpan Age => Stopwatch.GetElapsedTime(_startedTimestamp);

        /// <summary>Gets the coarse activity state.</summary>
        public FeedSessionState State => (FeedSessionState)Volatile.Read(ref _state);

        /// <summary>Gets commands observed on this session.</summary>
        public long Commands => Volatile.Read(ref _commands);

        /// <summary>Gets CHECK commands observed on this session.</summary>
        public long Checks => Volatile.Read(ref _checks);

        /// <summary>Gets framed articles (receive completed).</summary>
        public long ArticlesReceived => Volatile.Read(ref _articlesReceived);

        /// <summary>Gets articles that reached a 239/439/400 completion.</summary>
        public long ArticlesCompleted => Volatile.Read(ref _articlesCompleted);

        /// <summary>Gets framed article payload bytes (terminator omitted).</summary>
        public long ArticleBytes => Volatile.Read(ref _articleBytes);

        /// <summary>Gets HistoryDB Seen outcomes.</summary>
        public long HistoryHits => Volatile.Read(ref _historyHits);

        /// <summary>Gets HistoryDB Unseen outcomes.</summary>
        public long HistoryMisses => Volatile.Read(ref _historyMisses);

        /// <summary>Gets HistoryDB Unavailable / exception outcomes.</summary>
        public long HistoryErrors => Volatile.Read(ref _historyErrors);

        /// <summary>Gets successful ingestion-queue admits.</summary>
        public long QueueAccepted => Volatile.Read(ref _queueAccepted);

        /// <summary>Gets payload bytes successfully admitted to the ingestion queue.</summary>
        public long QueueAdmittedBytes => Volatile.Read(ref _queueAdmittedBytes);

        /// <summary>Gets queue rejects (article larger than budget).</summary>
        public long QueueRejected => Volatile.Read(ref _queueRejected);

        /// <summary>Gets queue unavailable / shutdown outcomes.</summary>
        public long QueueUnavailable => Volatile.Read(ref _queueUnavailable);

        /// <summary>Gets TAKETHIS completions treated as already-seen.</summary>
        public long Duplicates => Volatile.Read(ref _duplicates);

        /// <summary>Gets total time spent in article receive.</summary>
        public TimeSpan ReceiveTime => TicksToTime(Volatile.Read(ref _receiveTicks));

        /// <summary>Gets total time spent awaiting HistoryDB.</summary>
        public TimeSpan HistoryWaitTime => TicksToTime(Volatile.Read(ref _historyTicks));

        /// <summary>Gets total time spent awaiting queue admission.</summary>
        public TimeSpan QueueWaitTime => TicksToTime(Volatile.Read(ref _queueWaitTicks));

        /// <summary>Gets total time spent enqueueing the protocol response.</summary>
        public TimeSpan ResponseTime => TicksToTime(Volatile.Read(ref _responseTicks));

        /// <summary>Gets the longest single article receive.</summary>
        public TimeSpan MaxReceiveTime => TicksToTime(Volatile.Read(ref _maxReceiveTicks));

        /// <summary>Gets the longest single HistoryDB wait.</summary>
        public TimeSpan MaxHistoryWaitTime => TicksToTime(Volatile.Read(ref _maxHistoryTicks));

        /// <summary>Gets the longest single queue-admission wait.</summary>
        public TimeSpan MaxQueueWaitTime => TicksToTime(Volatile.Read(ref _maxQueueWaitTicks));

        /// <summary>Gets the longest interval between completed articles.</summary>
        public TimeSpan MaxArticleInterval => TicksToTime(Volatile.Read(ref _maxArticleIntervalTicks));

        /// <summary>Gets time since the last recorded activity.</summary>
        public TimeSpan Idle => Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActivityTimestamp));

        /// <summary>Gets time since the previous completed article, or <see cref="TimeSpan.Zero"/>.</summary>
        public TimeSpan SinceLastArticle
        {
            get
            {
                var last = Volatile.Read(ref _lastArticleCompleteTimestamp);
                return last == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(last);
            }
        }

        /// <summary>Sets the coarse activity state.</summary>
        public void SetState(FeedSessionState state)
        {
            Volatile.Write(ref _state, (int)state);
            Touch();
        }

        /// <summary>Increments the command counter.</summary>
        public void RecordCommand()
        {
            Interlocked.Increment(ref _commands);
            Touch();
        }

        /// <summary>Increments the CHECK counter.</summary>
        public void RecordCheck()
        {
            Interlocked.Increment(ref _checks);
            Interlocked.Increment(ref _commands);
            Touch();
        }

        /// <summary>Records a completed article frame.</summary>
        public void RecordArticleReceived(int bytes, long receiveTicks)
        {
            Interlocked.Increment(ref _articlesReceived);
            Interlocked.Add(ref _articleBytes, bytes);
            AddTicks(ref _receiveTicks, ref _maxReceiveTicks, receiveTicks);
            Touch();
        }

        /// <summary>Records a HistoryDB Peek/Lookup outcome and wait.</summary>
        public void RecordHistory(HistoryLookupResult result, long waitTicks)
        {
            switch (result)
            {
                case HistoryLookupResult.Seen:
                    Interlocked.Increment(ref _historyHits);
                    break;
                case HistoryLookupResult.Unseen:
                    Interlocked.Increment(ref _historyMisses);
                    break;
                default:
                    Interlocked.Increment(ref _historyErrors);
                    break;
            }

            AddTicks(ref _historyTicks, ref _maxHistoryTicks, waitTicks);
            Touch();
        }

        /// <summary>Records ingestion-queue admission outcome and wait.</summary>
        public void RecordQueue(ArticleEnqueueResult result, long waitTicks, int articleBytes = 0)
        {
            switch (result)
            {
                case ArticleEnqueueResult.Accepted:
                    Interlocked.Increment(ref _queueAccepted);
                    if (articleBytes > 0)
                    {
                        Interlocked.Add(ref _queueAdmittedBytes, articleBytes);
                    }

                    break;
                case ArticleEnqueueResult.Rejected:
                    Interlocked.Increment(ref _queueRejected);
                    break;
                default:
                    Interlocked.Increment(ref _queueUnavailable);
                    break;
            }

            AddTicks(ref _queueWaitTicks, ref _maxQueueWaitTicks, waitTicks);
            Touch();
        }

        /// <summary>Records a completed TAKETHIS (or serial fallback) outcome.</summary>
        public void RecordArticleCompleted(bool duplicate, long responseTicks)
        {
            Interlocked.Increment(ref _articlesCompleted);
            if (duplicate)
            {
                Interlocked.Increment(ref _duplicates);
            }

            if (responseTicks > 0)
            {
                Interlocked.Add(ref _responseTicks, responseTicks);
            }

            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Exchange(ref _lastArticleCompleteTimestamp, now);
            if (previous != 0)
            {
                var interval = now - previous;
                if (interval > 0)
                {
                    UpdateMax(ref _maxArticleIntervalTicks, interval);
                }
            }

            Volatile.Write(ref _state, (int)FeedSessionState.Idle);
            Touch(now);
        }

        /// <summary>Adds response-enqueue wait without completing an article (CHECK).</summary>
        public void RecordResponseWait(long responseTicks)
        {
            if (responseTicks > 0)
            {
                Interlocked.Add(ref _responseTicks, responseTicks);
            }

            Touch();
        }

        private void Touch() => Touch(Stopwatch.GetTimestamp());

        private void Touch(long timestamp) => Volatile.Write(ref _lastActivityTimestamp, timestamp);

        private static TimeSpan TicksToTime(long ticks) =>
            ticks <= 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(0, ticks);

        private static void AddTicks(ref long total, ref long max, long ticks)
        {
            if (ticks <= 0)
            {
                return;
            }

            Interlocked.Add(ref total, ticks);
            UpdateMax(ref max, ticks);
        }

        private static void UpdateMax(ref long location, long candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref location);
                if (candidate <= current)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref location, candidate, current) == current)
                {
                    return;
                }
            }
        }
    }
}
