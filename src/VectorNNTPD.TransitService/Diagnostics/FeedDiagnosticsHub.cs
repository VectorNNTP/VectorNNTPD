using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Diagnostics
{
    /// <summary>
    /// Process-wide feed-diagnostics counters. Safe for concurrent session attach/release.
    /// </summary>
    public sealed class FeedDiagnosticsHub : IFeedDiagnostics
    {
        private readonly ConcurrentDictionary<FeedSessionProbe, byte> _sessions = new();
        private readonly ConcurrentDictionary<FeedSessionProbe, SessionPrior> _sessionPriors = new();
        private readonly ConcurrentDictionary<string, PeerCounters> _peers = new(StringComparer.Ordinal);
        private long _priorArticles;
        private long _priorArticlesReceived;
        private long _priorBytes;
        private long _priorCommands;
        private long _priorQueuedArticles;
        private long _priorQueuedBytes;
        private long _priorTcpBytes;
        private long _priorSpoolArticles;
        private long _priorSpoolBytes;
        private long _priorTimestamp;
        private long _historyHits;
        private long _historyMisses;
        private long _historyErrors;
        private long _queueFailures;
        private long _tcpBytes;
        private long _spoolArticles;
        private long _spoolBytes;
        private int _spoolBusy;

        /// <inheritdoc />
        public bool IsEnabled => true;

        /// <inheritdoc />
        public void OnRejected(string peerName, string remote)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(peerName);
            _ = remote;
            var peer = GetPeer(peerName);
            Interlocked.Increment(ref peer.Rejected);
        }

        /// <inheritdoc />
        public FeedSessionProbe? OnAccepted(NntpSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            var peerName = session.Authorization.TransitPeerName ?? "none";
            var remote = string.Create(
                CultureInfo.InvariantCulture,
                $"{session.ClientAddress}:{session.ClientPort}");
            var probe = new FeedSessionProbe(remote, peerName);
            var peer = GetPeer(peerName);
            Interlocked.Increment(ref peer.Accepted);
            while (true)
            {
                var current = Volatile.Read(ref peer.Active);
                var next = current + 1;
                if (Interlocked.CompareExchange(ref peer.Active, next, current) == current)
                {
                    UpdatePeak(ref peer.Peak, next);
                    break;
                }
            }

            _sessions[probe] = 0;
            return probe;
        }

        /// <inheritdoc />
        public void OnReleased(FeedSessionProbe? probe)
        {
            if (probe is null)
            {
                return;
            }

            if (!_sessions.TryRemove(probe, out _))
            {
                return;
            }

            _sessionPriors.TryRemove(probe, out _);
            AddLifetime(probe);
            var peer = GetPeer(probe.PeerName);
            while (true)
            {
                var current = Volatile.Read(ref peer.Active);
                if (current <= 0)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref peer.Active, current - 1, current) == current)
                {
                    return;
                }
            }
        }

        /// <inheritdoc />
        public void RecordTcpBytes(int bytes)
        {
            if (bytes > 0)
            {
                Interlocked.Add(ref _tcpBytes, bytes);
            }
        }

        /// <inheritdoc />
        public void BeginSpoolWork() => Interlocked.Increment(ref _spoolBusy);

        /// <inheritdoc />
        public void EndSpoolWork(int bytes, bool persisted)
        {
            if (persisted)
            {
                Interlocked.Increment(ref _spoolArticles);
                if (bytes > 0)
                {
                    Interlocked.Add(ref _spoolBytes, bytes);
                }
            }

            Interlocked.Decrement(ref _spoolBusy);
        }

        /// <inheritdoc />
        public FeedDiagnosticsSnapshot CaptureSnapshot(
            IArticleIngestionQueue queue,
            TransitConfigurationStore store)
        {
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(store);

            var now = Stopwatch.GetTimestamp();
            var priorTs = Interlocked.Exchange(ref _priorTimestamp, now);
            var sessions = _sessions.Keys.ToArray();
            long articles = 0;
            long articlesReceived = 0;
            long bytes = 0;
            long commands = 0;
            long queuedArticles = 0;
            long queuedBytes = 0;
            long liveHits = 0;
            long liveMisses = 0;
            long liveErrors = 0;
            long liveQueueFail = 0;
            var receiving = 0;
            var idle = 0;
            foreach (var session in sessions)
            {
                articles += session.ArticlesCompleted;
                articlesReceived += session.ArticlesReceived;
                bytes += session.ArticleBytes;
                commands += session.Commands;
                queuedArticles += session.QueueAccepted;
                queuedBytes += session.QueueAdmittedBytes;
                liveHits += session.HistoryHits;
                liveMisses += session.HistoryMisses;
                liveErrors += session.HistoryErrors;
                liveQueueFail += session.QueueRejected + session.QueueUnavailable;
                if (session.State == FeedSessionState.Receiving)
                {
                    receiving++;
                }
                else if (session.State == FeedSessionState.Idle)
                {
                    idle++;
                }
            }

            articles += SumPeer(static p => Volatile.Read(ref p.ReleasedArticles));
            articlesReceived += SumPeer(static p => Volatile.Read(ref p.ReleasedArticlesReceived));
            bytes += SumPeer(static p => Volatile.Read(ref p.ReleasedBytes));
            commands += SumPeer(static p => Volatile.Read(ref p.ReleasedCommands));
            queuedArticles += SumPeer(static p => Volatile.Read(ref p.ReleasedQueuedArticles));
            queuedBytes += SumPeer(static p => Volatile.Read(ref p.ReleasedQueuedBytes));

            var intervalArticles = FeedRateCalculator.NonNegativeDelta(
                articles,
                Interlocked.Exchange(ref _priorArticles, articles));
            var intervalArticlesReceived = FeedRateCalculator.NonNegativeDelta(
                articlesReceived,
                Interlocked.Exchange(ref _priorArticlesReceived, articlesReceived));
            var intervalBytes = FeedRateCalculator.NonNegativeDelta(
                bytes,
                Interlocked.Exchange(ref _priorBytes, bytes));
            var intervalCommands = FeedRateCalculator.NonNegativeDelta(
                commands,
                Interlocked.Exchange(ref _priorCommands, commands));
            var intervalQueuedArticles = FeedRateCalculator.NonNegativeDelta(
                queuedArticles,
                Interlocked.Exchange(ref _priorQueuedArticles, queuedArticles));
            var intervalQueuedBytes = FeedRateCalculator.NonNegativeDelta(
                queuedBytes,
                Interlocked.Exchange(ref _priorQueuedBytes, queuedBytes));
            var tcpBytes = Volatile.Read(ref _tcpBytes);
            var intervalTcpBytes = FeedRateCalculator.NonNegativeDelta(
                tcpBytes,
                Interlocked.Exchange(ref _priorTcpBytes, tcpBytes));
            var spoolArticles = Volatile.Read(ref _spoolArticles);
            var intervalSpoolArticles = FeedRateCalculator.NonNegativeDelta(
                spoolArticles,
                Interlocked.Exchange(ref _priorSpoolArticles, spoolArticles));
            var spoolBytes = Volatile.Read(ref _spoolBytes);
            var intervalSpoolBytes = FeedRateCalculator.NonNegativeDelta(
                spoolBytes,
                Interlocked.Exchange(ref _priorSpoolBytes, spoolBytes));

            var interval = priorTs == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(priorTs, now);
            var peers = new List<FeedPeerSnapshot>();
            foreach (var (name, counters) in _peers)
            {
                var max = 0;
                if (store.Current.Peers.TryGetValue(name, out var policy))
                {
                    max = policy.MaxIncomingConnections;
                }

                var liveArticles = 0L;
                var liveCompleted = 0L;
                var liveBytes = 0L;
                var liveChecks = 0L;
                foreach (var session in sessions)
                {
                    if (!string.Equals(session.PeerName, name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    liveArticles += session.ArticlesReceived;
                    liveCompleted += session.ArticlesCompleted;
                    liveBytes += session.ArticleBytes;
                    liveChecks += session.Checks;
                }

                peers.Add(new FeedPeerSnapshot
                {
                    PeerName = name,
                    ConfiguredMaxConnections = max,
                    ActiveConnections = Math.Max(0, Volatile.Read(ref counters.Active)),
                    PeakConnections = Volatile.Read(ref counters.Peak),
                    AcceptedConnections = Volatile.Read(ref counters.Accepted),
                    RejectedConnections = Volatile.Read(ref counters.Rejected),
                    ArticlesReceived = liveArticles + Volatile.Read(ref counters.ReleasedArticlesReceived),
                    ArticlesCompleted = liveCompleted + Volatile.Read(ref counters.ReleasedArticles),
                    ArticleBytes = liveBytes + Volatile.Read(ref counters.ReleasedBytes),
                    Checks = liveChecks + Volatile.Read(ref counters.ReleasedChecks),
                });
            }

            peers.Sort(static (a, b) => string.CompareOrdinal(a.PeerName, b.PeerName));

            var sessionRows = new List<FeedSessionSnapshot>(sessions.Length);
            foreach (var session in sessions)
            {
                _sessionPriors.TryGetValue(session, out var prior);
                var sessionCommands = session.Commands;
                var sessionReceived = session.ArticlesReceived;
                var sessionBytes = session.ArticleBytes;
                sessionRows.Add(new FeedSessionSnapshot
                {
                    Remote = session.Remote,
                    PeerName = session.PeerName,
                    Age = session.Age,
                    State = session.State,
                    Commands = sessionCommands,
                    Checks = session.Checks,
                    ArticlesReceived = sessionReceived,
                    ArticlesCompleted = session.ArticlesCompleted,
                    ArticleBytes = sessionBytes,
                    IntervalCommands = FeedRateCalculator.NonNegativeDelta(sessionCommands, prior.Commands),
                    IntervalArticlesReceived = FeedRateCalculator.NonNegativeDelta(sessionReceived, prior.ArticlesReceived),
                    IntervalBytes = FeedRateCalculator.NonNegativeDelta(sessionBytes, prior.Bytes),
                    Idle = session.Idle,
                    SinceLastArticle = session.SinceLastArticle,
                    ReceiveTime = session.ReceiveTime,
                    HistoryWaitTime = session.HistoryWaitTime,
                    QueueWaitTime = session.QueueWaitTime,
                    MaxReceiveTime = session.MaxReceiveTime,
                    MaxHistoryWaitTime = session.MaxHistoryWaitTime,
                    MaxQueueWaitTime = session.MaxQueueWaitTime,
                });
                _sessionPriors[session] = new SessionPrior(sessionCommands, sessionReceived, sessionBytes);
            }

            sessionRows.Sort(static (a, b) => string.CompareOrdinal(a.Remote, b.Remote));

            return new FeedDiagnosticsSnapshot
            {
                CapturedAt = DateTimeOffset.UtcNow,
                Interval = interval,
                Peers = peers,
                Sessions = sessionRows,
                IntervalArticles = intervalArticles,
                IntervalArticlesReceived = intervalArticlesReceived,
                IntervalBytes = intervalBytes,
                IntervalCommands = intervalCommands,
                IntervalTcpBytes = intervalTcpBytes,
                IntervalQueuedBytes = intervalQueuedBytes,
                IntervalQueuedArticles = intervalQueuedArticles,
                IntervalSpoolArticles = intervalSpoolArticles,
                IntervalSpoolBytes = intervalSpoolBytes,
                TcpBytes = tcpBytes,
                SpoolArticles = spoolArticles,
                SpoolBytes = spoolBytes,
                SpoolActiveWorkers = Math.Max(0, Volatile.Read(ref _spoolBusy)),
                ActiveSessions = sessions.Length,
                ReceivingSessions = receiving,
                IdleSessions = idle,
                QueueCount = queue.Count,
                QueueBytes = queue.QueuedBytes,
                QueuePeakCount = queue.PeakCount,
                QueuePeakBytes = queue.PeakQueuedBytes,
                QueueWaitingProducers = queue.WaitingProducerCount,
                QueueMemoryLimit = queue.MemoryLimitBytes,
                HistoryHits = Volatile.Read(ref _historyHits) + liveHits,
                HistoryMisses = Volatile.Read(ref _historyMisses) + liveMisses,
                HistoryErrors = Volatile.Read(ref _historyErrors) + liveErrors,
                QueueAdmissionFailures = Volatile.Read(ref _queueFailures) + liveQueueFail,
            };
        }

        private PeerCounters GetPeer(string peerName) => _peers.GetOrAdd(peerName, static _ => new PeerCounters());

        private long SumPeer(Func<PeerCounters, long> selector)
        {
            var total = 0L;
            foreach (var pair in _peers)
            {
                total += selector(pair.Value);
            }

            return total;
        }

        private void AddLifetime(FeedSessionProbe probe)
        {
            var peer = GetPeer(probe.PeerName);
            Interlocked.Add(ref peer.ReleasedArticles, probe.ArticlesCompleted);
            Interlocked.Add(ref peer.ReleasedArticlesReceived, probe.ArticlesReceived);
            Interlocked.Add(ref peer.ReleasedBytes, probe.ArticleBytes);
            Interlocked.Add(ref peer.ReleasedChecks, probe.Checks);
            Interlocked.Add(ref peer.ReleasedCommands, probe.Commands);
            Interlocked.Add(ref peer.ReleasedQueuedArticles, probe.QueueAccepted);
            Interlocked.Add(ref peer.ReleasedQueuedBytes, probe.QueueAdmittedBytes);
            Interlocked.Add(ref _historyHits, probe.HistoryHits);
            Interlocked.Add(ref _historyMisses, probe.HistoryMisses);
            Interlocked.Add(ref _historyErrors, probe.HistoryErrors);
            Interlocked.Add(ref _queueFailures, probe.QueueRejected + probe.QueueUnavailable);
        }

        private static void UpdatePeak(ref int location, int candidate)
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

        private sealed class PeerCounters
        {
            public int Active;
            public int Peak;
            public long Accepted;
            public long Rejected;
            public long ReleasedArticles;
            public long ReleasedArticlesReceived;
            public long ReleasedBytes;
            public long ReleasedChecks;
            public long ReleasedCommands;
            public long ReleasedQueuedArticles;
            public long ReleasedQueuedBytes;
        }

        private readonly struct SessionPrior(long commands, long articlesReceived, long bytes)
        {
            public long Commands { get; } = commands;

            public long ArticlesReceived { get; } = articlesReceived;

            public long Bytes { get; } = bytes;
        }
    }
}
