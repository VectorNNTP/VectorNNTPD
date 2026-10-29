using System.Globalization;

namespace VectorNNTPD.ReaderService.SessionState.BytesAccounting
{
    /// <summary>
    /// In-process remaining-quota algorithm. Production Redis Lua is a faithful port.
    /// Remaining never increases and never goes negative.
    /// </summary>
    /// <remarks>
    /// APPLY is idempotent per batch id. The remaining update is
    /// <c>min(current, mysqlRemainingAfter)</c> (or initialize from MySQL when missing).
    /// <c>consumed</c> is not subtracted: MySQL already subtracted that batch, and
    /// another node's earlier APPLY may already have floored to a mysql_after that
    /// includes this consume. Decrementing would double-count and is not retry-safe.
    /// </remarks>
    internal sealed class AccountByteEngine
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, long> _remaining = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _applied = new(StringComparer.Ordinal);

        /// <summary>
        /// Applies one consumed batch identified by <paramref name="batchId"/>.
        /// Repeating the same id does not change remaining.
        /// </summary>
        public long Apply(string key, string batchId, long consumed, long mysqlRemainingAfter)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (!AccountByteBatchId.IsValid(batchId))
            {
                throw new ArgumentException("Batch id is required for idempotent APPLY.", nameof(batchId));
            }

            _ = ClampNonNegative(consumed);
            mysqlRemainingAfter = ClampNonNegative(mysqlRemainingAfter);
            lock (_gate)
            {
                if (IsApplied(key, batchId))
                {
                    return _remaining.TryGetValue(key, out var existing)
                        ? ClampNonNegative(existing)
                        : 0;
                }

                if (!_remaining.TryGetValue(key, out var current))
                {
                    _remaining[key] = mysqlRemainingAfter;
                    MarkApplied(key, batchId);
                    TrimApplied(key, batchId);
                    return mysqlRemainingAfter;
                }

                current = ClampNonNegative(current);
                var next = current < mysqlRemainingAfter ? current : mysqlRemainingAfter;
                _remaining[key] = next;
                MarkApplied(key, batchId);
                TrimApplied(key, batchId);
                return next;
            }
        }

        /// <summary>Reads remaining, or <see cref="AccountByteKeys.Missing"/> when absent.</summary>
        public long Observe(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            lock (_gate)
            {
                return _remaining.TryGetValue(key, out var current) ? ClampNonNegative(current) : AccountByteKeys.Missing;
            }
        }

        /// <summary>Overwrites remaining without consume semantics (tests).</summary>
        internal void Write(string key, long remaining)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            lock (_gate)
            {
                _remaining[key] = ClampNonNegative(remaining);
            }
        }

        /// <summary>Removes remaining and applied-batch marks. Returns 1 if the key existed.</summary>
        internal long Delete(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            lock (_gate)
            {
                var existed = _remaining.Remove(key);
                existed = _applied.Remove(key) || existed;
                return existed ? 1 : 0;
            }
        }

        /// <summary>Returns whether the remaining key exists (tests).</summary>
        internal bool Contains(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            lock (_gate)
            {
                return _remaining.ContainsKey(key);
            }
        }

        /// <summary>Returns whether <paramref name="batchId"/> was already applied (tests).</summary>
        internal bool WasApplied(string key, string batchId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
            lock (_gate)
            {
                return IsApplied(key, batchId);
            }
        }

        internal static long ClampNonNegative(long value) => value < 0 ? 0 : value;

        internal static string Format(long remaining) =>
            remaining.ToString(CultureInfo.InvariantCulture);

        private bool IsApplied(string key, string batchId) =>
            _applied.TryGetValue(key, out var batches) && batches.Contains(batchId);

        private void MarkApplied(string key, string batchId)
        {
            if (!_applied.TryGetValue(key, out var batches))
            {
                batches = new HashSet<string>(StringComparer.Ordinal);
                _applied[key] = batches;
            }

            batches.Add(batchId);
        }

        private void TrimApplied(string key, string keepBatchId)
        {
            if (!_applied.TryGetValue(key, out var batches)
                || batches.Count <= AccountByteBatchId.MaxRetainedMarks)
            {
                return;
            }

            var extra = batches.Count - AccountByteBatchId.MaxRetainedMarks;
            var doomed = new List<string>(extra);
            foreach (var id in batches)
            {
                if (id == keepBatchId)
                {
                    continue;
                }

                doomed.Add(id);
                if (doomed.Count == extra)
                {
                    break;
                }
            }

            foreach (var id in doomed)
            {
                batches.Remove(id);
            }
        }
    }
}
