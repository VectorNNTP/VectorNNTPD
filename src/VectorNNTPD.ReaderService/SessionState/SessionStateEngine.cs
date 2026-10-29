namespace VectorNNTPD.ReaderService.SessionState
{
    /// <summary>
    /// Atomic cluster session + source-IP membership algorithm. The Redis Lua scripts
    /// are a faithful port of these operations.
    /// </summary>
    /// <remarks>
    /// Two hashes per account, mutated in one locked/EVAL step:
    /// source fields <c>{ip}\x1f{ownerId}</c> and session fields <c>{ownerId}</c>,
    /// values <c>{expiryUnixMs}|{generation}|{count}</c>. Session limit is checked
    /// before source-IP limit. Rejection makes no state changes. The session count is
    /// the sum of unexpired per-owner counts; the source count is the number of
    /// distinct unexpired IPs.
    /// </remarks>
    internal sealed class SessionStateEngine
    {
        /// <summary>Accepted; the source IP was already active.</summary>
        public const long AcceptedExisting = 3;

        /// <summary>Accepted; a new distinct source IP was established.</summary>
        public const long AcceptedNew = 2;

        /// <summary>Rejected: cluster session limit would be exceeded.</summary>
        public const long RejectedSessionLimit = 1;

        /// <summary>Rejected: cluster source-address limit would be exceeded.</summary>
        public const long RejectedSourceLimit = 0;

        /// <summary>Packs an accepted admit status with the cluster session total.</summary>
        public static long PackAdmit(long status, int sessionTotal) =>
            status <= RejectedSessionLimit
                ? status
                : status | ((long)(sessionTotal < 0 ? 0 : sessionTotal) << 2);

        /// <summary>Reads the raw admit status from a packed EVAL integer.</summary>
        public static long UnpackAdmitStatus(long packed) => packed <= RejectedSessionLimit ? packed : packed & 3;

        /// <summary>Reads the cluster session total from a packed accepted EVAL integer.</summary>
        public static int UnpackAdmitSessionTotal(long packed) =>
            packed <= RejectedSessionLimit ? 0 : (int)(packed >> 2);

        private readonly object _gate = new();
        private readonly Dictionary<string, Dictionary<string, string>> _hashes = new(StringComparer.Ordinal);

        /// <summary>Returns the unpacked admit status (tests that ignore the packed total).</summary>
        internal long TryAdmitStatus(
            string sourceKey,
            string sessionKey,
            string normalizedSourceIp,
            string ownerId,
            int sessionLimit,
            int srcIpLimit,
            long nowUnixMs,
            long leaseMs,
            long sessionGeneration,
            long sourceGeneration,
            bool trackSessions = false) =>
            UnpackAdmitStatus(
                TryAdmit(
                    sourceKey,
                    sessionKey,
                    normalizedSourceIp,
                    ownerId,
                    sessionLimit,
                    srcIpLimit,
                    nowUnixMs,
                    leaseMs,
                    sessionGeneration,
                    sourceGeneration,
                    trackSessions));

        /// <summary>
        /// Atomically admits one session and this owner's source-IP membership.
        /// </summary>
        public long TryAdmit(
            string sourceKey,
            string sessionKey,
            string normalizedSourceIp,
            string ownerId,
            int sessionLimit,
            int srcIpLimit,
            long nowUnixMs,
            long leaseMs,
            long sessionGeneration,
            long sourceGeneration,
            bool trackSessions = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(normalizedSourceIp);
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

            lock (_gate)
            {
                var sources = GetOrCreateHash(sourceKey);
                var sessions = GetOrCreateHash(sessionKey);
                PruneExpired(sources, nowUnixMs);
                PruneExpired(sessions, nowUnixMs);

                var sessionTotal = SumCounts(sessions);
                if (sessionLimit > 0 && sessionTotal >= sessionLimit)
                {
                    RemoveIfEmpty(sourceKey, sources);
                    RemoveIfEmpty(sessionKey, sessions);
                    return RejectedSessionLimit;
                }

                var activeIps = DistinctActiveIps(sources);
                if (srcIpLimit > 0 && !activeIps.Contains(normalizedSourceIp) && activeIps.Count >= srcIpLimit)
                {
                    RemoveIfEmpty(sourceKey, sources);
                    RemoveIfEmpty(sessionKey, sessions);
                    return RejectedSourceLimit;
                }

                var expiry = nowUnixMs + leaseMs;
                if (sessionLimit > 0 || trackSessions)
                {
                    IncrementOwned(sessions, ownerId, expiry, sessionGeneration);
                }

                if (srcIpLimit > 0)
                {
                    IncrementOwned(
                        sources,
                        SessionStateKeys.SourceField(normalizedSourceIp, ownerId),
                        expiry,
                        sourceGeneration);
                }

                var status = activeIps.Contains(normalizedSourceIp) ? AcceptedExisting : AcceptedNew;
                return PackAdmit(status, SumCounts(sessions));
            }
        }

        /// <summary>
        /// Decrements this owner's session count and source-IP count when generations match.
        /// </summary>
        public long Release(
            string sourceKey,
            string sessionKey,
            string normalizedSourceIp,
            string ownerId,
            long sessionGeneration,
            long sourceGeneration)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(normalizedSourceIp);
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

            lock (_gate)
            {
                if (_hashes.TryGetValue(sessionKey, out var sessions))
                {
                    DecrementOwned(sessions, ownerId, sessionGeneration);
                    RemoveIfEmpty(sessionKey, sessions);
                }

                if (_hashes.TryGetValue(sourceKey, out var sources))
                {
                    DecrementOwned(sources, SessionStateKeys.SourceField(normalizedSourceIp, ownerId), sourceGeneration);
                    RemoveIfEmpty(sourceKey, sources);
                }

                return _hashes.TryGetValue(sessionKey, out var remaining)
                    ? SumCounts(remaining)
                    : 0;
            }
        }

        /// <summary>Deletes every field owned by <paramref name="ownerId"/> (process shutdown).</summary>
        public long ReleaseOwner(string sourceKey, string sessionKey, string ownerId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

            lock (_gate)
            {
                if (_hashes.TryGetValue(sessionKey, out var sessions))
                {
                    sessions.Remove(ownerId);
                    RemoveIfEmpty(sessionKey, sessions);
                }

                if (_hashes.TryGetValue(sourceKey, out var sources))
                {
                    List<string>? owned = null;
                    // codeql[cs/linq/missed-where] Lock-held key collection without LINQ allocation.
                    foreach (var field in sources.Keys)
                    {
                        if (SessionStateKeys.TrySplitField(field, out _, out var fieldOwner)
                            && string.Equals(fieldOwner, ownerId, StringComparison.Ordinal))
                        {
                            owned ??= [];
                            owned.Add(field);
                        }
                    }

                    if (owned is not null)
                    {
                        foreach (var field in owned)
                        {
                            sources.Remove(field);
                        }
                    }

                    RemoveIfEmpty(sourceKey, sources);
                }

                return 1;
            }
        }

        /// <summary>
        /// Refreshes this owner's unexpired matching session and source leases.
        /// Returns 1 when every requested field was renewed; 0 when any was lost.
        /// </summary>
        public long Renew(
            string sourceKey,
            string sessionKey,
            string ownerId,
            long sessionGeneration,
            long nowUnixMs,
            long leaseMs,
            IReadOnlyList<(string Ip, long Generation)> sources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
            ArgumentNullException.ThrowIfNull(sources);

            lock (_gate)
            {
                if (sessionGeneration != 0
                    && !CanRenewField(sessionKey, ownerId, sessionGeneration, nowUnixMs, out _))
                {
                    return 0;
                }

                foreach (var (ip, generation) in sources)
                {
                    if (!CanRenewField(
                        sourceKey,
                        SessionStateKeys.SourceField(ip, ownerId),
                        generation,
                        nowUnixMs,
                        out _))
                    {
                        return 0;
                    }
                }

                if (sessionGeneration != 0)
                {
                    RenewField(sessionKey, ownerId, sessionGeneration, nowUnixMs, leaseMs);
                }

                foreach (var (ip, generation) in sources)
                {
                    RenewField(
                        sourceKey,
                        SessionStateKeys.SourceField(ip, ownerId),
                        generation,
                        nowUnixMs,
                        leaseMs);
                }

                if (_hashes.TryGetValue(sessionKey, out var sessions))
                {
                    PruneExpired(sessions, nowUnixMs);
                    RemoveIfEmpty(sessionKey, sessions);
                    var total = SumCounts(sessions);
                    return total < 1 ? 1 : total;
                }

                return 1;
            }
        }

        /// <summary>Returns the cluster-wide authenticated session count.</summary>
        internal int ActiveSessionCount(string sessionKey, long nowUnixMs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
            lock (_gate)
            {
                if (!_hashes.TryGetValue(sessionKey, out var sessions))
                {
                    return 0;
                }

                PruneExpired(sessions, nowUnixMs);
                RemoveIfEmpty(sessionKey, sessions);
                return SumCounts(sessions);
            }
        }

        /// <summary>Returns distinct unexpired source IPs.</summary>
        internal IReadOnlyCollection<string> ActiveSourceIps(string sourceKey, long nowUnixMs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
            lock (_gate)
            {
                if (!_hashes.TryGetValue(sourceKey, out var sources))
                {
                    return [];
                }

                PruneExpired(sources, nowUnixMs);
                RemoveIfEmpty(sourceKey, sources);
                return DistinctActiveIps(sources);
            }
        }

        /// <summary>Returns this owner's unexpired session count.</summary>
        internal int OwnerSessionCount(string sessionKey, string ownerId, long nowUnixMs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
            lock (_gate)
            {
                if (!_hashes.TryGetValue(sessionKey, out var sessions)
                    || !sessions.TryGetValue(ownerId, out var current)
                    || !SessionStateKeys.TrySplitValue(current, out var expiry, out _, out var count)
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
                    && SessionStateKeys.TrySplitValue(current, out expiryUnixMs, out generation, out count))
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
                GetOrCreateHash(key)[field] = SessionStateKeys.Value(expiryUnixMs, generation, count);
            }
        }

        /// <summary>Returns whether this owner currently holds <paramref name="normalizedSourceIp"/>.</summary>
        internal bool HasSourceOwner(string sourceKey, string normalizedSourceIp, string ownerId, long nowUnixMs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
            var field = SessionStateKeys.SourceField(normalizedSourceIp, ownerId);
            lock (_gate)
            {
                if (!_hashes.TryGetValue(sourceKey, out var sources)
                    || !sources.TryGetValue(field, out var current)
                    || !SessionStateKeys.TrySplitValue(current, out var expiry, out _, out var count))
                {
                    return false;
                }

                return expiry > nowUnixMs && count > 0;
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
                || !SessionStateKeys.TrySplitValue(current, out var expiry, out var storedGeneration, out count)
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

            hash[field] = SessionStateKeys.Value(nowUnixMs + leaseMs, generation, count);
        }

        private static void IncrementOwned(Dictionary<string, string> hash, string field, long expiry, long generation)
        {
            // Same generation: this owner still holds the field. A different generation
            // is a new local epoch (local state was wiped) and must replace stale count.
            if (hash.TryGetValue(field, out var current)
                && SessionStateKeys.TrySplitValue(current, out _, out var storedGeneration, out var count)
                && storedGeneration == generation)
            {
                hash[field] = SessionStateKeys.Value(expiry, storedGeneration, count + 1);
                return;
            }

            hash[field] = SessionStateKeys.Value(expiry, generation, 1);
        }

        private static void DecrementOwned(Dictionary<string, string> hash, string field, long generation)
        {
            if (!hash.TryGetValue(field, out var current)
                || !SessionStateKeys.TrySplitValue(current, out var expiry, out var storedGeneration, out var count)
                || storedGeneration != generation)
            {
                return;
            }

            if (count <= 1)
            {
                hash.Remove(field);
                return;
            }

            hash[field] = SessionStateKeys.Value(expiry, storedGeneration, count - 1);
        }

        private static void PruneExpired(Dictionary<string, string> hash, long nowUnixMs)
        {
            List<string>? expired = null;
            foreach (var (field, value) in hash)
            {
                if (!SessionStateKeys.TrySplitValue(value, out var expiry, out _, out _) || expiry <= nowUnixMs)
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
                if (SessionStateKeys.TrySplitValue(value, out _, out _, out var count))
                {
                    total += count;
                }
            }

            return total;
        }

        private static HashSet<string> DistinctActiveIps(Dictionary<string, string> hash)
        {
            var active = new HashSet<string>(StringComparer.Ordinal);
            // codeql[cs/linq/missed-where] TrySplitField out-params; lock-held scan without LINQ allocation.
            foreach (var field in hash.Keys)
            {
                if (SessionStateKeys.TrySplitField(field, out var ip, out _))
                {
                    active.Add(ip);
                }
            }

            return active;
        }
    }
}
