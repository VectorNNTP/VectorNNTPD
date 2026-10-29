namespace VectorNNTPD.ReaderService.PostFilter
{
    /// <summary>Process-wide SPAMD transport counters. Not per-POST logging.</summary>
    internal sealed class SpamdTransportMetrics
    {
        private long _connectionsEstablished;
        private long _checkRequests;
        private long _connectionReuses;
        private long _reconnects;
        private long _evictions;
        private long _totalCheckTicks;

        /// <summary>Gets TCP connections opened to SPAMD.</summary>
        public long ConnectionsEstablished => Volatile.Read(ref _connectionsEstablished);

        /// <summary>Gets CHECK operations attempted after a connection was obtained.</summary>
        public long CheckRequests => Volatile.Read(ref _checkRequests);

        /// <summary>Gets CHECKs that reused an idle persistent connection.</summary>
        public long ConnectionReuses => Volatile.Read(ref _connectionReuses);

        /// <summary>Gets replacements opened after a broken or half-closed connection.</summary>
        public long Reconnects => Volatile.Read(ref _reconnects);

        /// <summary>Gets connections removed from the pool before another request could use them.</summary>
        public long Evictions => Volatile.Read(ref _evictions);

        /// <summary>Gets total CHECK duration in <see cref="TimeSpan.Ticks"/>.</summary>
        public long TotalCheckTicks => Volatile.Read(ref _totalCheckTicks);

        /// <summary>Gets average CHECK latency, or <see cref="TimeSpan.Zero"/> when none completed.</summary>
        public TimeSpan AverageCheckLatency =>
            CheckRequests == 0
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(TotalCheckTicks / CheckRequests);

        /// <summary>Records a newly established TCP connection.</summary>
        public void RecordConnect() => Interlocked.Increment(ref _connectionsEstablished);

        /// <summary>Records a CHECK that reused an idle connection.</summary>
        public void RecordReuse() => Interlocked.Increment(ref _connectionReuses);

        /// <summary>Records a reconnect after eviction or unexpected close.</summary>
        public void RecordReconnect() => Interlocked.Increment(ref _reconnects);

        /// <summary>Records removal of a broken connection.</summary>
        public void RecordEviction() => Interlocked.Increment(ref _evictions);

        /// <summary>Records one CHECK duration.</summary>
        public void RecordCheck(TimeSpan elapsed)
        {
            Interlocked.Increment(ref _checkRequests);
            Interlocked.Add(ref _totalCheckTicks, elapsed.Ticks);
        }
    }
}
