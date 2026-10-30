using System.Collections.Concurrent;

using VectorNNTPD.Common.Networking.RateLimiting;

namespace VectorNNTPD.TransitService.SessionState.RateLimiting
{
    /// <summary>
    /// Process-local map of account sessions to outbound caps. Cluster session
    /// counts come from SessionState admit/release/renew; the write path never
    /// consults this type.
    /// </summary>
    /// <remarks>
    /// A cross-node join would overshoot if the new session took an equal share
    /// while remotes still held <c>floor(rate / previousCount)</c>. The unused
    /// remainder <c>accountBytes % remotes</c> is safe only when every remote is
    /// that previous equal-share group. A second joining node sees remotes that
    /// include the first joiner and would take another remainder while the
    /// established node still holds nearly the whole aggregate. While remotes
    /// exist, new sessions are blocked. Established local sessions may only drop
    /// to a budget that assumes previously split remotes still hold this node's
    /// last per-session cap and newly observed remotes hold 0. Equal shares
    /// resume only when this node owns every remaining session.
    /// </remarks>
    internal sealed class AccountRateAllocator : IAccountRateAllocator
    {
        private readonly ConcurrentDictionary<string, int> _clusterCounts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AccountRates> _accounts = new(StringComparer.Ordinal);

        /// <summary>Initializes a process-local allocator.</summary>
        public AccountRateAllocator(TimeProvider? timeProvider = null, TimeSpan? equalSplitHold = null)
        {
            _ = timeProvider;
            _ = equalSplitHold;
        }

        /// <inheritdoc />
        public void Register(string accountName, string sessionId, IOutboundRateCap cap, int rateBps)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            ArgumentNullException.ThrowIfNull(cap);
            if (rateBps <= 0)
            {
                cap.UpdateMaxSendBytesPerSecond(0);
                return;
            }

            var account = _accounts.GetOrAdd(accountName, static _ => new AccountRates());
            lock (account.Gate)
            {
                account.RateBps = rateBps;
                account.Sessions[sessionId] = cap;
                if (_clusterCounts.TryGetValue(accountName, out var observed) && observed > 0)
                {
                    account.ClusterCount = observed;
                }

                Apply(account);
            }
        }

        /// <inheritdoc />
        public void Unregister(string accountName, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(accountName)
                || string.IsNullOrWhiteSpace(sessionId)
                || !_accounts.TryGetValue(accountName, out var account))
            {
                return;
            }

            lock (account.Gate)
            {
                account.Sessions.TryRemove(sessionId, out _);
                if (account.Sessions.IsEmpty)
                {
                    _accounts.TryRemove(accountName, out _);
                    return;
                }

                Apply(account);
            }
        }

        /// <inheritdoc />
        public void ObserveClusterSessionCount(string accountName, int clusterSessionCount)
        {
            if (string.IsNullOrWhiteSpace(accountName))
            {
                return;
            }

            var count = clusterSessionCount < 0 ? 0 : clusterSessionCount;
            _clusterCounts[accountName] = count;
            if (!_accounts.TryGetValue(accountName, out var account))
            {
                return;
            }

            lock (account.Gate)
            {
                account.ClusterCount = count;
                Apply(account);
            }
        }

        /// <inheritdoc />
        public long? GetSessionCap(string accountName, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(accountName)
                || string.IsNullOrWhiteSpace(sessionId)
                || !_accounts.TryGetValue(accountName, out var account))
            {
                return null;
            }

            lock (account.Gate)
            {
                return account.Sessions.TryGetValue(sessionId, out var cap)
                    ? cap.MaxSendBytesPerSecond
                    : null;
            }
        }

        /// <summary>Returns the last observed cluster count (tests).</summary>
        internal int ClusterCount(string accountName) =>
            _clusterCounts.TryGetValue(accountName, out var count) ? count : 0;

        /// <summary>Returns how many local sessions are registered (tests).</summary>
        internal int LocalSessionCount(string accountName) =>
            _accounts.TryGetValue(accountName, out var account) ? account.Sessions.Count : 0;

        private void Apply(AccountRates account)
        {
            var rate = account.RateBps;
            if (rate <= 0)
            {
                return;
            }

            var count = account.ClusterCount;
            if (count <= 0)
            {
                count = account.Sessions.Count;
            }

            var local = account.Sessions.Count;
            if (local == 0)
            {
                return;
            }

            var remotes = count - local;
            var target = AccountRateFormula.EqualShareCap(rate, count);
            if (remotes <= 0)
            {
                foreach (var pair in account.Sessions)
                {
                    pair.Value.UpdateMaxSendBytesPerSecond(target);
                }

                account.LastCount = count;
                account.LastLocal = local;
                return;
            }

            var accountBytes = AccountRateFormula.AccountBytesPerSecond(rate);
            long remoteHold;
            if (account.LastCount <= 0)
            {
                var remoteShare = AccountRateFormula.PerSessionBytesPerSecond(rate, remotes);
                remoteHold = checked(remoteShare * remotes);
            }
            else
            {
                var oldRemotes = account.LastCount - account.LastLocal;
                if (oldRemotes < 0)
                {
                    oldRemotes = 0;
                }

                var oldShare = 0L;
                // codeql[cs/linq/missed-select] Allocation-free max scan; Select would materialize caps.
                foreach (var pair in account.Sessions)
                {
                    var cap = pair.Value.MaxSendBytesPerSecond;
                    if (cap > oldShare)
                    {
                        oldShare = cap;
                    }
                }

                if (oldShare <= 0)
                {
                    oldShare = AccountRateFormula.PerSessionBytesPerSecond(rate, account.LastCount);
                }

                remoteHold = checked(oldShare * oldRemotes);
            }

            var localBudget = accountBytes - remoteHold;
            var perLocal = localBudget > 0 ? localBudget / local : 0;
            foreach (var pair in account.Sessions)
            {
                var current = pair.Value.MaxSendBytesPerSecond;
                if (current <= 0)
                {
                    pair.Value.UpdateMaxSendBytesPerSecond(AccountRateFormula.BlockedBytesPerSecond);
                    continue;
                }

                var safe = target;
                if (perLocal < safe)
                {
                    safe = perLocal;
                }

                if (current < safe)
                {
                    safe = current;
                }

                pair.Value.UpdateMaxSendBytesPerSecond(
                    safe > 0 ? safe : AccountRateFormula.BlockedBytesPerSecond);
            }

            account.LastCount = count;
            account.LastLocal = local;
        }

        private sealed class AccountRates
        {
            public object Gate { get; } = new();
            public int RateBps;
            public int ClusterCount;
            public int LastCount;
            public int LastLocal;
            public ConcurrentDictionary<string, IOutboundRateCap> Sessions { get; } = new(StringComparer.Ordinal);
        }
    }
}
