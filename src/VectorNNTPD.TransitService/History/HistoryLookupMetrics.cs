namespace VectorNNTPD.TransitService.History
{
    /// <summary>Lifetime HistoryDB lookup counters (hits, misses, errors, wait).</summary>
    public readonly struct HistoryLookupMetricsSnapshot
    {
        /// <summary>Initializes a snapshot.</summary>
        public HistoryLookupMetricsSnapshot(long lookups, long hits, long misses, long errors, long waitTicks)
        {
            Lookups = lookups;
            Hits = hits;
            Misses = misses;
            Errors = errors;
            WaitTicks = waitTicks;
        }

        /// <summary>Gets completed lookups.</summary>
        public long Lookups { get; }

        /// <summary>Gets <see cref="HistoryLookupResult.Seen"/> outcomes.</summary>
        public long Hits { get; }

        /// <summary>Gets <see cref="HistoryLookupResult.Unseen"/> outcomes.</summary>
        public long Misses { get; }

        /// <summary>Gets <see cref="HistoryLookupResult.Unavailable"/> outcomes.</summary>
        public long Errors { get; }

        /// <summary>Gets accumulated lookup wait ticks (<see cref="System.Diagnostics.Stopwatch"/>).</summary>
        public long WaitTicks { get; }
    }

    /// <summary>Authoritative process-wide HistoryDB lookup metrics.</summary>
    public interface IHistoryLookupMetrics
    {
        /// <summary>Captures lifetime lookup counters.</summary>
        HistoryLookupMetricsSnapshot Capture();
    }
}
