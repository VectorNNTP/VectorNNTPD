using System.Globalization;

namespace VectorNNTPD.ReaderService.PostFilter.Quota
{
    /// <summary>
    /// Atomic accept-quota algorithm. Redis Lua is a faithful port of these operations.
    /// </summary>
    internal sealed class PostFilterQuotaEngine
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Dictionary<string, string>> _hashes = new(StringComparer.Ordinal);

        /// <summary>Atomically evaluates ceilings and creates one reservation, or writes nothing.</summary>
        public long Reserve(
            string quotaKey,
            string multipostKey,
            PostFilterReservationId reservation,
            long nowMs,
            long reservationTtlMs,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings,
            long messages,
            long bytes,
            int mpUnits,
            string bodyHex)
        {
            ValidateReserve(quotaKey, multipostKey, nowMs, reservationTtlMs, windows, ceilings, messages, bytes, mpUnits, ref bodyHex);

            lock (_gate)
            {
                var quota = GetOrCreateHash(quotaKey);
                var multipost = GetOrCreateHash(multipostKey);
                PruneQuota(quota, windows, nowMs);
                PruneMultipost(multipost, windows, nowMs);

                var field = reservation.Field;
                if (quota.TryGetValue(field, out var existing)
                    && PostFilterQuotaKeys.TryParseReservation(
                        existing, out var exp, out var storedGen, out var sm, out var sb, out var smp, out var sbody)
                    && exp > nowMs)
                {
                    if (storedGen == reservation.Generation
                        && sm == messages
                        && sb == bytes
                        && smp == mpUnits
                        && string.Equals(sbody, bodyHex, StringComparison.Ordinal))
                    {
                        return PostFilterQuotaReserveCodes.Accept;
                    }

                    return PostFilterQuotaReserveCodes.Conflict;
                }

                var deny = FirstDenial(quota, multipost, windows, ceilings, nowMs, messages, bytes, mpUnits, bodyHex);
                if (deny != PostFilterQuotaReserveCodes.Accept)
                {
                    RemoveIfEmpty(quotaKey, quota);
                    RemoveIfEmpty(multipostKey, multipost);
                    return deny;
                }

                quota[field] = PostFilterQuotaKeys.PackReservation(
                    nowMs + reservationTtlMs,
                    reservation.Generation,
                    messages,
                    bytes,
                    mpUnits,
                    bodyHex);
                return PostFilterQuotaReserveCodes.Accept;
            }
        }

        /// <summary>Transfers a live reservation into the current buckets, or no-ops.</summary>
        public long Commit(
            string quotaKey,
            string multipostKey,
            PostFilterReservationId reservation,
            long nowMs,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(quotaKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(multipostKey);
            windows.Validate();
            ceilings.Validate();

            lock (_gate)
            {
                var quota = GetOrCreateHash(quotaKey);
                var multipost = GetOrCreateHash(multipostKey);
                PruneQuota(quota, windows, nowMs);
                PruneMultipost(multipost, windows, nowMs);

                var field = reservation.Field;
                if (!quota.TryGetValue(field, out var raw)
                    || !PostFilterQuotaKeys.TryParseReservation(
                        raw, out var exp, out var storedGen, out var messages, out var bytes, out var mpUnits, out var bodyHex))
                {
                    if (quota.ContainsKey(field))
                    {
                        quota.Remove(field);
                    }

                    RemoveIfEmpty(quotaKey, quota);
                    RemoveIfEmpty(multipostKey, multipost);
                    return PostFilterQuotaCommitCodes.Noop;
                }

                if (storedGen != reservation.Generation)
                {
                    return PostFilterQuotaCommitCodes.Noop;
                }

                if (exp <= nowMs)
                {
                    quota.Remove(field);
                    RemoveIfEmpty(quotaKey, quota);
                    RemoveIfEmpty(multipostKey, multipost);
                    return PostFilterQuotaCommitCodes.Noop;
                }

                IncrementCurrent(quota, multipost, windows, ceilings, nowMs, messages, bytes, mpUnits, bodyHex);
                quota.Remove(field);
                RemoveIfEmpty(quotaKey, quota);
                RemoveIfEmpty(multipostKey, multipost);
                return PostFilterQuotaCommitCodes.Committed;
            }
        }

        /// <summary>Deletes a matching live reservation. Never decrements committed usage.</summary>
        public long Release(
            string quotaKey,
            string multipostKey,
            PostFilterReservationId reservation,
            long nowMs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(quotaKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(multipostKey);

            lock (_gate)
            {
                if (!_hashes.TryGetValue(quotaKey, out var quota))
                {
                    return PostFilterQuotaReleaseCodes.Noop;
                }

                var field = reservation.Field;
                if (!quota.TryGetValue(field, out var raw)
                    || !PostFilterQuotaKeys.TryParseReservation(
                        raw, out var exp, out var storedGen, out _, out _, out _, out _))
                {
                    quota.Remove(field);
                    RemoveIfEmpty(quotaKey, quota);
                    return PostFilterQuotaReleaseCodes.Noop;
                }

                if (storedGen != reservation.Generation)
                {
                    return PostFilterQuotaReleaseCodes.Noop;
                }

                if (exp <= nowMs)
                {
                    quota.Remove(field);
                    RemoveIfEmpty(quotaKey, quota);
                    return PostFilterQuotaReleaseCodes.Noop;
                }

                quota.Remove(field);
                RemoveIfEmpty(quotaKey, quota);
                return PostFilterQuotaReleaseCodes.Released;
            }
        }

        /// <summary>Returns committed messages in the current bucket, or zero.</summary>
        internal long CommittedMessages(string quotaKey, char window, long bucketId) =>
            ReadLong(quotaKey, PostFilterQuotaKeys.CommittedMessagesField(window, bucketId));

        /// <summary>Returns committed bytes in the current bucket, or zero.</summary>
        internal long CommittedBytes(string quotaKey, char window, long bucketId) =>
            ReadLong(quotaKey, PostFilterQuotaKeys.CommittedBytesField(window, bucketId));

        /// <summary>Returns committed identical count, or zero.</summary>
        internal long CommittedIdentical(string multipostKey, string bodyHex, char window, long bucketId) =>
            ReadLong(multipostKey, PostFilterQuotaKeys.MultipostField(bodyHex, window, bucketId));

        /// <summary>Returns whether a reservation field is present (including expired until pruned).</summary>
        internal bool HasReservationField(string quotaKey, PostFilterReservationId reservation)
        {
            lock (_gate)
            {
                return _hashes.TryGetValue(quotaKey, out var quota) && quota.ContainsKey(reservation.Field);
            }
        }

        /// <summary>Returns whether a live (unexpired) reservation exists.</summary>
        internal bool HasLiveReservation(string quotaKey, PostFilterReservationId reservation, long nowMs)
        {
            lock (_gate)
            {
                return _hashes.TryGetValue(quotaKey, out var quota)
                    && quota.TryGetValue(reservation.Field, out var raw)
                    && PostFilterQuotaKeys.TryParseReservation(raw, out var exp, out var gen, out _, out _, out _, out _)
                    && gen == reservation.Generation
                    && exp > nowMs;
            }
        }

        /// <summary>Returns the number of fields on the quota hash.</summary>
        internal int QuotaFieldCount(string quotaKey)
        {
            lock (_gate)
            {
                return _hashes.TryGetValue(quotaKey, out var quota) ? quota.Count : 0;
            }
        }

        /// <summary>Returns the number of fields on the multipost hash.</summary>
        internal int MultipostFieldCount(string multipostKey)
        {
            lock (_gate)
            {
                return _hashes.TryGetValue(multipostKey, out var multipost) ? multipost.Count : 0;
            }
        }

        /// <summary>Returns reservation field count (prefix <c>r:</c>).</summary>
        internal int ReservationFieldCount(string quotaKey)
        {
            lock (_gate)
            {
                if (!_hashes.TryGetValue(quotaKey, out var quota))
                {
                    return 0;
                }

                var count = 0;
                // codeql[cs/linq/missed-where] Lock-held count without LINQ allocation.
                foreach (var field in quota.Keys)
                {
                    if (field.StartsWith(PostFilterQuotaKeys.ReservationFieldPrefix, StringComparison.Ordinal))
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Writes a reservation field (tests: expiry / generation mismatch).</summary>
        internal void SeedReservation(
            string quotaKey,
            PostFilterReservationId reservation,
            long expiryMs,
            long storedGeneration,
            long messages,
            long bytes,
            int mpUnits,
            string bodyHex)
        {
            lock (_gate)
            {
                GetOrCreateHash(quotaKey)[reservation.Field] = PostFilterQuotaKeys.PackReservation(
                    expiryMs, storedGeneration, messages, bytes, mpUnits, bodyHex);
            }
        }

        /// <summary>Writes a committed counter field (tests).</summary>
        internal void SeedCommitted(string key, string field, long value)
        {
            lock (_gate)
            {
                GetOrCreateHash(key)[field] = value.ToString(CultureInfo.InvariantCulture);
            }
        }

        private long ReadLong(string key, string field)
        {
            lock (_gate)
            {
                if (!_hashes.TryGetValue(key, out var hash)
                    || !hash.TryGetValue(field, out var raw)
                    || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    return 0;
                }

                return value;
            }
        }

        private static long FirstDenial(
            Dictionary<string, string> quota,
            Dictionary<string, string> multipost,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings,
            long nowMs,
            long messages,
            long bytes,
            int mpUnits,
            string bodyHex)
        {
            SumLive(quota, nowMs, bodyHex, mpUnits, out var resMsg, out var resBytes, out var resIdentical);
            var bucketL = windows.BucketId(PostFilterQuotaKeys.LongWindow, nowMs);
            var bucketS = windows.BucketId(PostFilterQuotaKeys.ShortWindow, nowMs);
            var cml = ReadField(quota, PostFilterQuotaKeys.CommittedMessagesField(PostFilterQuotaKeys.LongWindow, bucketL));
            var cbl = ReadField(quota, PostFilterQuotaKeys.CommittedBytesField(PostFilterQuotaKeys.LongWindow, bucketL));
            var cms = ReadField(quota, PostFilterQuotaKeys.CommittedMessagesField(PostFilterQuotaKeys.ShortWindow, bucketS));
            var cbs = ReadField(quota, PostFilterQuotaKeys.CommittedBytesField(PostFilterQuotaKeys.ShortWindow, bucketS));
            var cil = mpUnits == 1
                ? ReadField(multipost, PostFilterQuotaKeys.MultipostField(bodyHex, PostFilterQuotaKeys.LongWindow, bucketL))
                : 0;
            var cis = mpUnits == 1
                ? ReadField(multipost, PostFilterQuotaKeys.MultipostField(bodyHex, PostFilterQuotaKeys.ShortWindow, bucketS))
                : 0;

            if (ceilings.MaxMessagesLong > 0 && cml + resMsg + messages > ceilings.MaxMessagesLong)
            {
                return PostFilterQuotaReserveCodes.DeniedMessagesLong;
            }

            if (ceilings.MaxBytesLong > 0 && cbl + resBytes + bytes > ceilings.MaxBytesLong)
            {
                return PostFilterQuotaReserveCodes.DeniedBytesLong;
            }

            if (mpUnits == 1
                && ceilings.MaxIdenticalLong > 0
                && cil + resIdentical + mpUnits > ceilings.MaxIdenticalLong)
            {
                return PostFilterQuotaReserveCodes.DeniedIdenticalLong;
            }

            if (ceilings.MaxMessagesShort > 0 && cms + resMsg + messages > ceilings.MaxMessagesShort)
            {
                return PostFilterQuotaReserveCodes.DeniedMessagesShort;
            }

            if (ceilings.MaxBytesShort > 0 && cbs + resBytes + bytes > ceilings.MaxBytesShort)
            {
                return PostFilterQuotaReserveCodes.DeniedBytesShort;
            }

            if (mpUnits == 1
                && ceilings.MaxIdenticalShort > 0
                && cis + resIdentical + mpUnits > ceilings.MaxIdenticalShort)
            {
                return PostFilterQuotaReserveCodes.DeniedIdenticalShort;
            }

            return PostFilterQuotaReserveCodes.Accept;
        }

        private static void SumLive(
            Dictionary<string, string> quota,
            long nowMs,
            string bodyHex,
            int requestMpUnits,
            out long messages,
            out long bytes,
            out long identical)
        {
            messages = 0;
            bytes = 0;
            identical = 0;
            foreach (var (field, value) in quota)
            {
                if (!field.StartsWith(PostFilterQuotaKeys.ReservationFieldPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!PostFilterQuotaKeys.TryParseReservation(value, out var exp, out _, out var m, out var b, out var mp, out var body)
                    || exp <= nowMs)
                {
                    continue;
                }

                messages += m;
                bytes += b;
                if (requestMpUnits == 1 && mp == 1 && string.Equals(body, bodyHex, StringComparison.Ordinal))
                {
                    identical += mp;
                }
            }
        }

        private static void IncrementCurrent(
            Dictionary<string, string> quota,
            Dictionary<string, string> multipost,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings,
            long nowMs,
            long messages,
            long bytes,
            int mpUnits,
            string bodyHex)
        {
            foreach (var window in new[] { PostFilterQuotaKeys.LongWindow, PostFilterQuotaKeys.ShortWindow })
            {
                var bucket = windows.BucketId(window, nowMs);
                if (ceilings.MaxMessages(window) > 0)
                {
                    Add(quota, PostFilterQuotaKeys.CommittedMessagesField(window, bucket), messages);
                }

                if (ceilings.MaxBytes(window) > 0)
                {
                    Add(quota, PostFilterQuotaKeys.CommittedBytesField(window, bucket), bytes);
                }

                if (mpUnits == 1 && bodyHex.Length > 0 && ceilings.MaxIdentical(window) > 0)
                {
                    Add(multipost, PostFilterQuotaKeys.MultipostField(bodyHex, window, bucket), mpUnits);
                }
            }
        }

        private static void Add(Dictionary<string, string> hash, string field, long delta)
        {
            var current = ReadField(hash, field);
            hash[field] = (current + delta).ToString(CultureInfo.InvariantCulture);
        }

        private static long ReadField(Dictionary<string, string> hash, string field) =>
            hash.TryGetValue(field, out var raw)
                && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;

        private static void PruneQuota(
            Dictionary<string, string> quota,
            PostFilterQuotaWindows windows,
            long nowMs)
        {
            List<string>? dead = null;
            foreach (var (field, value) in quota)
            {
                if (field.StartsWith(PostFilterQuotaKeys.ReservationFieldPrefix, StringComparison.Ordinal))
                {
                    if (!PostFilterQuotaKeys.TryParseReservation(value, out var exp, out _, out _, out _, out _, out _)
                        || exp <= nowMs)
                    {
                        (dead ??= []).Add(field);
                    }

                    continue;
                }

                if (PostFilterQuotaKeys.TryParseCommittedField(field, out _, out var window, out var bucket)
                    && windows.BucketEnded(window, bucket, nowMs))
                {
                    (dead ??= []).Add(field);
                }
            }

            DeleteDead(quota, dead);
        }

        private static void PruneMultipost(
            Dictionary<string, string> multipost,
            PostFilterQuotaWindows windows,
            long nowMs)
        {
            List<string>? dead = null;
            // codeql[cs/linq/missed-where] TryParse out-params; collect-then-remove without LINQ allocation.
            foreach (var field in multipost.Keys)
            {
                if (PostFilterQuotaKeys.TryParseMultipostField(field, out _, out var window, out var bucket)
                    && windows.BucketEnded(window, bucket, nowMs))
                {
                    (dead ??= []).Add(field);
                }
            }

            DeleteDead(multipost, dead);
        }

        private static void DeleteDead(Dictionary<string, string> hash, List<string>? dead)
        {
            if (dead is null)
            {
                return;
            }

            foreach (var field in dead)
            {
                hash.Remove(field);
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

        private static void ValidateReserve(
            string quotaKey,
            string multipostKey,
            long nowMs,
            long reservationTtlMs,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings,
            long messages,
            long bytes,
            int mpUnits,
            ref string bodyHex)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(quotaKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(multipostKey);
            ArgumentOutOfRangeException.ThrowIfNegative(nowMs);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reservationTtlMs, 0);
            windows.Validate();
            ceilings.Validate();
            ArgumentOutOfRangeException.ThrowIfNegative(messages);
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            if (mpUnits is not (0 or 1))
            {
                throw new ArgumentOutOfRangeException(nameof(mpUnits), mpUnits, "mpUnits must be 0 or 1.");
            }

            bodyHex ??= string.Empty;
            if (mpUnits == 0)
            {
                bodyHex = string.Empty;
                return;
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(bodyHex);
        }
    }
}
