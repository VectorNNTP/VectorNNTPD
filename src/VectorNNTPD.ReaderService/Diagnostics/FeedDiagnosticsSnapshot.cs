namespace VectorNNTPD.ReaderService.Diagnostics
{
    /// <summary>Point-in-time feed-diagnostics view (off the hot path).</summary>
    public sealed class FeedDiagnosticsSnapshot
    {
        /// <summary>Empty snapshot used when diagnostics are disabled.</summary>
        public static FeedDiagnosticsSnapshot Empty { get; } = new()
        {
            CapturedAt = DateTimeOffset.UnixEpoch,
            Interval = TimeSpan.Zero,
            Peers = [],
            Sessions = [],
        };

        /// <summary>Gets the capture time.</summary>
        public required DateTimeOffset CapturedAt { get; init; }

        /// <summary>Gets the interval since the previous snapshot (zero on the first).</summary>
        public required TimeSpan Interval { get; init; }

        /// <summary>Gets per-peer rows.</summary>
        public required IReadOnlyList<FeedPeerSnapshot> Peers { get; init; }

        /// <summary>Gets per-session rows.</summary>
        public required IReadOnlyList<FeedSessionSnapshot> Sessions { get; init; }

        /// <summary>Gets articles completed in this interval.</summary>
        public long IntervalArticles { get; init; }

        /// <summary>Gets framed articles received in this interval.</summary>
        public long IntervalArticlesReceived { get; init; }

        /// <summary>Gets article bytes framed in this interval.</summary>
        public long IntervalBytes { get; init; }

        /// <summary>Gets commands observed in this interval.</summary>
        public long IntervalCommands { get; init; }

        /// <summary>Gets application-layer upstream bytes read in this interval.</summary>
        public long IntervalTcpBytes { get; init; }

        /// <summary>Gets ingestion-queue admitted payload bytes in this interval.</summary>
        public long IntervalQueuedBytes { get; init; }

        /// <summary>Gets articles successfully admitted to the ingestion queue in this interval.</summary>
        public long IntervalQueuedArticles { get; init; }

        /// <summary>Gets spool articles persisted in this interval.</summary>
        public long IntervalSpoolArticles { get; init; }

        /// <summary>Gets spool payload bytes persisted in this interval.</summary>
        public long IntervalSpoolBytes { get; init; }

        /// <summary>Gets lifetime application-layer upstream bytes read.</summary>
        public long TcpBytes { get; init; }

        /// <summary>Gets lifetime spool articles persisted.</summary>
        public long SpoolArticles { get; init; }

        /// <summary>Gets lifetime spool payload bytes persisted.</summary>
        public long SpoolBytes { get; init; }

        /// <summary>Gets whether the spool drain loop is currently persisting an article.</summary>
        public int SpoolActiveWorkers { get; init; }

        /// <summary>Gets current admitted sessions (established and running).</summary>
        public int ActiveSessions { get; init; }

        /// <summary>Gets sessions in <see cref="FeedSessionState.Receiving"/>.</summary>
        public int ReceivingSessions { get; init; }

        /// <summary>Gets sessions in <see cref="FeedSessionState.Idle"/>.</summary>
        public int IdleSessions { get; init; }

        /// <summary>Gets current queue depth.</summary>
        public int QueueCount { get; init; }

        /// <summary>Gets current queued payload bytes.</summary>
        public long QueueBytes { get; init; }

        /// <summary>Gets queue peak article count.</summary>
        public int QueuePeakCount { get; init; }

        /// <summary>Gets queue peak payload bytes.</summary>
        public long QueuePeakBytes { get; init; }

        /// <summary>Gets producers waiting for queue byte budget.</summary>
        public int QueueWaitingProducers { get; init; }

        /// <summary>Gets queue memory limit.</summary>
        public long QueueMemoryLimit { get; init; }

        /// <summary>Gets lifetime HistoryDB Seen count (all sessions, including released).</summary>
        public long HistoryHits { get; init; }

        /// <summary>Gets lifetime HistoryDB Unseen count.</summary>
        public long HistoryMisses { get; init; }

        /// <summary>Gets lifetime HistoryDB error count.</summary>
        public long HistoryErrors { get; init; }

        /// <summary>Gets lifetime queue admission failures (rejected + unavailable + full).</summary>
        public long QueueAdmissionFailures { get; init; }

        /// <summary>Gets completed articles/sec over <see cref="Interval"/>.</summary>
        public double ArticlesPerSecond => FeedRateCalculator.PerSecond(IntervalArticles, Interval);

        /// <summary>Gets framed articles received/sec over <see cref="Interval"/>.</summary>
        public double ArticlesReceivedPerSecond =>
            FeedRateCalculator.PerSecond(IntervalArticlesReceived, Interval);

        /// <summary>Gets commands/sec over <see cref="Interval"/>.</summary>
        public double CommandsPerSecond => FeedRateCalculator.PerSecond(IntervalCommands, Interval);

        /// <summary>Gets bits/sec of framed article payload over <see cref="Interval"/>.</summary>
        public double BitsPerSecond => FeedRateCalculator.MegabitsPerSecond(IntervalBytes, Interval) * 1_000_000d;

        /// <summary>Gets framed-article Mbps over <see cref="Interval"/>.</summary>
        public double ArticleMegabitsPerSecond =>
            FeedRateCalculator.MegabitsPerSecond(IntervalBytes, Interval);

        /// <summary>Gets application-layer receive Mbps over <see cref="Interval"/>.</summary>
        public double TcpMegabitsPerSecond =>
            FeedRateCalculator.MegabitsPerSecond(IntervalTcpBytes, Interval);

        /// <summary>Gets ingestion-queue admit Mbps over <see cref="Interval"/>.</summary>
        public double IngestMegabitsPerSecond =>
            FeedRateCalculator.MegabitsPerSecond(IntervalQueuedBytes, Interval);

        /// <summary>Gets completed-article Mbps over <see cref="Interval"/>.</summary>
        public double ProcessMegabitsPerSecond =>
            FeedRateCalculator.MegabitsPerSecond(IntervalBytes, Interval);

        /// <summary>Gets spool persist Mbps over <see cref="Interval"/>.</summary>
        public double SpoolMegabitsPerSecond =>
            FeedRateCalculator.MegabitsPerSecond(IntervalSpoolBytes, Interval);
    }

    /// <summary>Per-peer connection and article totals.</summary>
    public sealed class FeedPeerSnapshot
    {
        /// <summary>Gets the Transit identifier.</summary>
        public required string PeerName { get; init; }

        /// <summary>Gets configured <c>MaxIncomingConnections</c>, or 0 when unknown.</summary>
        public int ConfiguredMaxConnections { get; init; }

        /// <summary>Gets current admitted sessions.</summary>
        public int ActiveConnections { get; init; }

        /// <summary>Gets peak admitted sessions.</summary>
        public int PeakConnections { get; init; }

        /// <summary>Gets lifetime admits.</summary>
        public long AcceptedConnections { get; init; }

        /// <summary>Gets lifetime admission rejects.</summary>
        public long RejectedConnections { get; init; }

        /// <summary>Gets lifetime framed articles.</summary>
        public long ArticlesReceived { get; init; }

        /// <summary>Gets lifetime completed articles.</summary>
        public long ArticlesCompleted { get; init; }

        /// <summary>Gets lifetime framed bytes.</summary>
        public long ArticleBytes { get; init; }

        /// <summary>Gets lifetime CHECK commands.</summary>
        public long Checks { get; init; }
    }

    /// <summary>Compact per-session row.</summary>
    public sealed class FeedSessionSnapshot
    {
        /// <summary>Gets <c>ip:port</c>.</summary>
        public required string Remote { get; init; }

        /// <summary>Gets the Transit identifier.</summary>
        public required string PeerName { get; init; }

        /// <summary>Gets session age.</summary>
        public TimeSpan Age { get; init; }

        /// <summary>Gets coarse activity.</summary>
        public FeedSessionState State { get; init; }

        /// <summary>Gets commands observed.</summary>
        public long Commands { get; init; }

        /// <summary>Gets CHECK commands.</summary>
        public long Checks { get; init; }

        /// <summary>Gets framed articles.</summary>
        public long ArticlesReceived { get; init; }

        /// <summary>Gets completed articles.</summary>
        public long ArticlesCompleted { get; init; }

        /// <summary>Gets framed bytes.</summary>
        public long ArticleBytes { get; init; }

        /// <summary>Gets commands observed in this interval.</summary>
        public long IntervalCommands { get; init; }

        /// <summary>Gets framed articles received in this interval.</summary>
        public long IntervalArticlesReceived { get; init; }

        /// <summary>Gets framed article bytes in this interval.</summary>
        public long IntervalBytes { get; init; }

        /// <summary>Gets idle time.</summary>
        public TimeSpan Idle { get; init; }

        /// <summary>Gets time since last completed article.</summary>
        public TimeSpan SinceLastArticle { get; init; }

        /// <summary>Gets total receive time.</summary>
        public TimeSpan ReceiveTime { get; init; }

        /// <summary>Gets total HistoryDB wait.</summary>
        public TimeSpan HistoryWaitTime { get; init; }

        /// <summary>Gets total queue wait.</summary>
        public TimeSpan QueueWaitTime { get; init; }

        /// <summary>Gets max receive time.</summary>
        public TimeSpan MaxReceiveTime { get; init; }

        /// <summary>Gets max HistoryDB wait.</summary>
        public TimeSpan MaxHistoryWaitTime { get; init; }

        /// <summary>Gets max queue wait.</summary>
        public TimeSpan MaxQueueWaitTime { get; init; }
    }
}
