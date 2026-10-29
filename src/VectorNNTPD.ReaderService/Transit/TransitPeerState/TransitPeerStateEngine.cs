namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// Atomic cluster Transit inbound-connection algorithm. The Redis Lua scripts
    /// are a faithful port of these operations.
    /// </summary>
    /// <remarks>
    /// One HASH per Transit <c>Identifier</c>. Field <c>{ownerId}</c>, value
    /// <c>{expiryUnixMs}|{generation}|{count}</c>. The cluster count is the sum of
    /// unexpired per-owner counts. <c>max &lt;= 0</c> rejects without incrementing.
    /// Rejection after prune writes no ownership. Source IP is not part of this state.
    /// </remarks>
    internal sealed class TransitPeerStateEngine
    {
        /// <summary>Accepted; ownership was created or incremented.</summary>
        public const long Accepted = 1;

        /// <summary>Rejected: closed (<c>max &lt;= 0</c>) or cluster count already at the limit.</summary>
        public const long Rejected = 0;

        private readonly object _gate = new();
        private readonly Dictionary<string, Dictionary<string, string>> _hashes = new(StringComparer.Ordinal);

        /// <summary>
        /// Atomically admits one inbound connection for this owner when the cluster
        /// count is below <paramref name="maxIncoming"/>.
        /// </summary>
        public long TryAdmit(
            string connectionKey,
            string ownerId,
            int maxIncoming,
            long nowUnixMs,
            long leaseMs,
            long generation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

            lock (_gate)
            {
                var owners = GetOrCreateHash(connectionKey);
                PruneExpired(owners, nowUnixMs);
                if (maxIncoming <= 0)
                {
                    RemoveIfEmpty(connectionKey, owners);
                    return Rejected;
                }

                if (SumCounts(owners) >= maxIncoming)
                {
                    RemoveIfEmpty(connectionKey, owners);
                    return Rejected;
                }

                IncrementOwned(owners, ownerId, nowUnixMs + leaseMs, generation);
                return Accepted;
            }
        }

        /// <summary>
        /// Decrements this owner's count when the generation matches. Deletes the
        /// field at zero and the hash when empty.
        /// </summary>
        public long Release(string connectionKey, string ownerId, long generation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

            lock (_gate)
            {
                if (_hashes.TryGetValue(connectionKey, out var owners))
                {
                    DecrementOwned(owners, ownerId, generation);
                    RemoveIfEmpty(connectionKey, owners);
                }

                return 1;
            }
        }

        /// <summary>
        /// Refreshes this owner's unexpired matching lease. Missing, expired, or
        /// generation-mismatched ownership is not recreated.
        /// </summary>
        public long Renew(
            string connectionKey,
            string ownerId,
            long generation,
            long nowUnixMs,
            long leaseMs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

            lock (_gate)
            {
                if (!CanRenewField(connectionKey, ownerId, generation, nowUnixMs, out _))
                {
                    return 0;
                }

                RenewField(connectionKey, ownerId, generation, nowUnixMs, leaseMs);
                return 1;
            }
        }

        /// <summary>Deletes only this owner's field. Other owners and peers are untouched.</summary>
        public long ReleaseOwner(string connectionKey, string ownerId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

            lock (_gate)
            {
                if (_hashes.TryGetValue(connectionKey, out var owners))
                {
                    owners.Remove(ownerId);
                    RemoveIfEmpty(connectionKey, owners);
                }

                return 1;
            }
        }

        /// <summary>Returns the cluster-wide unexpired inbound count for this peer.</summary>
        internal int ActiveCount(string connectionKey, long nowUnixMs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
            lock (_gate)
            {
                if (!_hashes.TryGetValue(connectionKey, out var owners))
                {
                    return 0;
                }

                PruneExpired(owners, nowUnixMs);
                RemoveIfEmpty(connectionKey, owners);
                return SumCounts(owners);
            }
        }

        /// <summary>Returns this owner's unexpired inbound count.</summary>
        internal int OwnerCount(string connectionKey, string ownerId, long nowUnixMs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
            lock (_gate)
            {
                if (!_hashes.TryGetValue(connectionKey, out var owners)
                    || !owners.TryGetValue(ownerId, out var current)
                    || !TransitPeerStateKeys.TrySplitValue(current, out var expiry, out _, out var count)
                    || expiry <= nowUnixMs)
                {
                    return 0;
                }

                return count;
            }
        }

        /// <summary>Reads a HASH field without mutating it (tests).</summary>
        internal bool TryGetOwnership(
            string key,
            string field,
            out long expiryUnixMs,
            out long generation,
            out int count)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentException.ThrowIfNullOrWhiteSpace(field);
            lock (_gate)
            {
                if (_hashes.TryGetValue(key, out var hash)
                    && hash.TryGetValue(field, out var current)
                    && TransitPeerStateKeys.TrySplitValue(current, out expiryUnixMs, out generation, out count))
                {
                    return true;
                }
            }

            expiryUnixMs = 0;
            generation = 0;
            count = 0;
            return false;
        }

        /// <summary>Overwrites a HASH field without admit/release semantics (tests).</summary>
        internal void WriteOwnership(string key, string field, long expiryUnixMs, long generation, int count)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentException.ThrowIfNullOrWhiteSpace(field);
            lock (_gate)
            {
                GetOrCreateHash(key)[field] = TransitPeerStateKeys.Value(expiryUnixMs, generation, count);
            }
        }

        /// <summary>Returns field names currently stored for <paramref name="connectionKey"/> (tests).</summary>
        internal IReadOnlyCollection<string> OwnerFields(string connectionKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
            lock (_gate)
            {
                return _hashes.TryGetValue(connectionKey, out var owners)
                    ? [.. owners.Keys]
                    : [];
            }
        }

        private Dictionary<string, string> GetOrCreateHash(string key)
        {
            if (!_hashes.TryGetValue(key, out var hash))
            {
                hash = new Dictionary<string, string>(StringComparer.Ordinal);
                _hashes[key] = hash;
            }

            return hash;
        }

        private void RemoveIfEmpty(string key, Dictionary<string, string> hash)
        {
            if (hash.Count == 0)
            {
                _hashes.Remove(key);
            }
        }

        private bool CanRenewField(string key, string field, long generation, long nowUnixMs, out int count)
        {
            count = 0;
            if (!_hashes.TryGetValue(key, out var hash)
                || !hash.TryGetValue(field, out var current)
                || !TransitPeerStateKeys.TrySplitValue(current, out var expiry, out var storedGeneration, out count)
                || storedGeneration != generation
                || expiry <= nowUnixMs)
            {
                return false;
            }

            return true;
        }

        private void RenewField(string key, string field, long generation, long nowUnixMs, long leaseMs)
        {
            if (!CanRenewField(key, field, generation, nowUnixMs, out var count)
                || !_hashes.TryGetValue(key, out var hash))
            {
                return;
            }

            hash[field] = TransitPeerStateKeys.Value(nowUnixMs + leaseMs, generation, count);
        }

        private static void IncrementOwned(Dictionary<string, string> hash, string field, long expiry, long generation)
        {
            // Same generation: this owner still holds the field. A different generation
            // is a new local epoch (local state was wiped) and must replace stale count.
            if (hash.TryGetValue(field, out var current)
                && TransitPeerStateKeys.TrySplitValue(current, out _, out var storedGeneration, out var count)
                && storedGeneration == generation)
            {
                hash[field] = TransitPeerStateKeys.Value(expiry, storedGeneration, count + 1);
                return;
            }

            hash[field] = TransitPeerStateKeys.Value(expiry, generation, 1);
        }

        private static void DecrementOwned(Dictionary<string, string> hash, string field, long generation)
        {
            if (!hash.TryGetValue(field, out var current)
                || !TransitPeerStateKeys.TrySplitValue(current, out var expiry, out var storedGeneration, out var count)
                || storedGeneration != generation)
            {
                return;
            }

            if (count <= 1)
            {
                hash.Remove(field);
                return;
            }

            hash[field] = TransitPeerStateKeys.Value(expiry, storedGeneration, count - 1);
        }

        private static void PruneExpired(Dictionary<string, string> hash, long nowUnixMs)
        {
            List<string>? expired = null;
            foreach (var (field, value) in hash)
            {
                if (!TransitPeerStateKeys.TrySplitValue(value, out var expiry, out _, out _) || expiry <= nowUnixMs)
                {
                    expired ??= [];
                    expired.Add(field);
                }
            }

            if (expired is null)
            {
                return;
            }

            foreach (var field in expired)
            {
                hash.Remove(field);
            }
        }

        private static int SumCounts(Dictionary<string, string> hash)
        {
            var total = 0;
            // codeql[cs/linq/missed-where] TrySplitValue out-params; lock-held sum without LINQ allocation.
            foreach (var value in hash.Values)
            {
                if (TransitPeerStateKeys.TrySplitValue(value, out _, out _, out var count))
                {
                    total += count;
                }
            }

            return total;
        }
    }
}
