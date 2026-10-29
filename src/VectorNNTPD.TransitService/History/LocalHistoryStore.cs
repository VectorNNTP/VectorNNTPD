using System.Collections.Concurrent;

namespace VectorNNTPD.TransitService.History
{
    /// <summary>
    /// Hard-capped expiring in-memory HistoryDB.
    /// </summary>
    /// <remarks>
    /// CHECK never walks the dictionary. A local hit is one bucket lookup plus a tick
    /// comparison. Insert reserves a slot with an interlocked compare-exchange
    /// then <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> — never
    /// <see cref="ConcurrentDictionary{TKey,TValue}.Count"/>. Expired keys are removed
    /// only when the observed expiry still matches (compare/remove), on that key's
    /// lookup or by <see cref="Maintain"/> on <see cref="HistoryMaintenanceService"/>.
    /// When the cap is reached, new digests are not inserted (no false hits); Redis
    /// remains the authority for those IDs until maintenance frees a slot. Unexpired
    /// entries are not evicted to make room.
    /// </remarks>
    internal sealed class LocalHistoryStore
    {
        /// <summary>Hard cap on live local entries. Not a CHECK-path compaction trigger.</summary>
        internal const int MaxEntries = 1_048_576;

        private readonly ConcurrentDictionary<HistoryDigest, long> _entries = new();
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _retention;
        private readonly int _maxEntries;
        private int _reservedCount;

        /// <summary>Initializes a new instance of the <see cref="LocalHistoryStore"/> class.</summary>
        public LocalHistoryStore(TimeSpan retention, TimeProvider timeProvider, int maxEntries = MaxEntries)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
            _retention = retention;
            _timeProvider = timeProvider;
            _maxEntries = maxEntries;
        }

        /// <summary>Gets the reserved slot count (includes not-yet-scanned expired keys).</summary>
        public int ApproximateCount => Math.Max(0, Volatile.Read(ref _reservedCount));

        /// <summary>Gets the dictionary entry count (tests). Not used on the CHECK path.</summary>
        internal int EntryCountForTests => _entries.Count;

        /// <summary>Gets how many times <see cref="Maintain"/> has walked the dictionary.</summary>
        internal int MaintenanceScanCount { get; private set; }

        /// <summary>Returns whether <paramref name="digest"/> is present and unexpired.</summary>
        public bool Contains(in HistoryDigest digest)
        {
            if (!_entries.TryGetValue(digest, out var expiryTicks))
            {
                return false;
            }

            if (_timeProvider.GetUtcNow().UtcTicks <= expiryTicks)
            {
                return true;
            }

            TryRemoveObservedExpiry(digest, expiryTicks);
            return false;
        }

        /// <summary>
        /// Records <paramref name="digest"/> with the configured retention when a slot is available.
        /// </summary>
        public void Add(in HistoryDigest digest)
        {
            var expiry = _timeProvider.GetUtcNow().Add(_retention).UtcTicks;
            while (true)
            {
                if (_entries.TryGetValue(digest, out var observed)
                    && (_entries.TryUpdate(digest, expiry, observed) || _entries.ContainsKey(digest)))
                {
                    return;
                }

                if (!TryReserveSlot())
                {
                    return;
                }

                if (_entries.TryAdd(digest, expiry))
                {
                    return;
                }

                ReleaseSlot();
            }
        }

        /// <summary>
        /// Removes expired entries. Must run off the CHECK thread (maintenance worker).
        /// </summary>
        /// <returns>The number of entries removed.</returns>
        public int Maintain()
        {
            MaintenanceScanCount++;
            var now = _timeProvider.GetUtcNow().UtcTicks;
            var removed = 0;
            // codeql[cs/linq/missed-where] ConcurrentDictionary iteration with TryRemove side effects.
            foreach (var pair in _entries)
            {
                if (pair.Value < now && TryRemoveObservedExpiry(pair.Key, pair.Value))
                {
                    removed++;
                }
            }

            return removed;
        }

        /// <summary>
        /// Removes <paramref name="digest"/> only if its stored expiry still equals
        /// <paramref name="observedExpiryTicks"/>. Used by lookup, maintenance, and tests.
        /// </summary>
        internal bool TryRemoveObservedExpiry(in HistoryDigest digest, long observedExpiryTicks)
        {
            if (_entries.TryRemove(new KeyValuePair<HistoryDigest, long>(digest, observedExpiryTicks)))
            {
                ReleaseSlot();
                return true;
            }

            return false;
        }

        private bool TryReserveSlot()
        {
            while (true)
            {
                var current = Volatile.Read(ref _reservedCount);
                if (current >= _maxEntries)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _reservedCount, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        private void ReleaseSlot() => Interlocked.Decrement(ref _reservedCount);
    }
}
