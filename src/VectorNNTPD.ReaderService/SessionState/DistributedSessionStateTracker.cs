using System.Collections.Concurrent;
using System.Net;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.Common.Networking.RateLimiting;
using VectorNNTPD.ReaderService.SessionState.RateLimiting;
namespace VectorNNTPD.ReaderService.SessionState
{
    /// <summary>
    /// Hybrid authenticated-session admission: Redis is the cluster authority for
    /// both <c>account_session_limit</c> and <c>account_srcip_limit</c>.
    /// </summary>
    /// <remarks>
    /// Both limits are cluster-wide and evaluated in one atomic membership operation.
    /// Limit <c>0</c> is unlimited. The source-IP local hot path is used only while
    /// <c>account_session_limit</c> is unlimited and this node already holds a live
    /// source-IP lease. A finite session limit requires every new session to take
    /// the distributed path so the global session count stays exact. New admissions
    /// that require Redis fail closed when membership is unavailable.
    /// Owner identity is <c>{nodeId}:{incarnation}</c>; incarnation is a process-lifetime
    /// identifier so a restart cannot mutate a previous incarnation's unexpired fields.
    /// Redis TTL is crash recovery only: while this process is alive and renewal
    /// succeeds, idle authenticated sessions keep consuming cluster capacity.
    /// </remarks>
    public sealed class DistributedSessionStateTracker : ISessionStateTracker, ISessionStateLeaseManager
    {
        private readonly ISessionStateStore _membership;
        private readonly IAccountByteAccountant? _bytes;
        private readonly IAccountRateAllocator _rates;
        private readonly ILogger<DistributedSessionStateTracker> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly string _nodeId;
        private readonly string _ownerId;
        private readonly TimeSpan _leaseTtl;
        private readonly TimeSpan _hotPathSkew;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountGates = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private readonly Dictionary<string, AccountAdmission> _accounts = new(StringComparer.Ordinal);
        private long _generations;

        /// <summary>Initializes a production tracker with default lease timings.</summary>
        public DistributedSessionStateTracker(
            ISessionStateStore membership,
            ILogger<DistributedSessionStateTracker> logger,
            string nodeId)
            : this(membership, logger, nodeId, TimeProvider.System, SessionStateDefaults.LeaseTtl, SessionStateDefaults.HotPathSkew, incarnation: null, bytes: null, rates: null)
        {
        }

        /// <summary>Initializes a tracker with explicit timing and incarnation (tests).</summary>
        internal DistributedSessionStateTracker(
            ISessionStateStore membership,
            ILogger<DistributedSessionStateTracker> logger,
            string nodeId,
            TimeProvider timeProvider,
            TimeSpan? leaseTtl = null,
            TimeSpan? hotPathSkew = null,
            string? incarnation = null,
            IAccountByteAccountant? bytes = null,
            IAccountRateAllocator? rates = null)
        {
            ArgumentNullException.ThrowIfNull(membership);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
            ArgumentNullException.ThrowIfNull(timeProvider);
            _membership = membership;
            _bytes = bytes;
            _rates = rates ?? NullAccountRateAllocator.Instance;
            _logger = logger;
            _nodeId = nodeId;
            _ownerId = string.Concat(nodeId, ":", string.IsNullOrWhiteSpace(incarnation) ? Guid.NewGuid().ToString("N") : incarnation);
            _timeProvider = timeProvider;
            _leaseTtl = leaseTtl ?? SessionStateDefaults.LeaseTtl;
            _hotPathSkew = hotPathSkew ?? SessionStateDefaults.HotPathSkew;
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_leaseTtl, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThan(_hotPathSkew, TimeSpan.Zero);
        }

        /// <summary>Gets the configured node identity.</summary>
        internal string NodeId => _nodeId;

        /// <summary>Gets <c>{nodeId}:{incarnation}</c> written into Redis ownership fields.</summary>
        internal string OwnerId => _ownerId;

        /// <summary>Gets how many admits were satisfied from a live local source-IP lease.</summary>
        internal int LocalHotPathAdmits { get; private set; }

        /// <summary>Gets how many admits required a distributed membership call.</summary>
        internal int DistributedAdmits { get; private set; }

        /// <summary>Gets how many admits were rejected by the distinct-IP limit.</summary>
        internal int DistributedSourceRejects { get; private set; }

        /// <summary>Gets how many admits were rejected by the cluster session limit.</summary>
        internal int DistributedSessionRejects { get; private set; }

        /// <summary>Gets how many admits were rejected by the distinct-IP limit.</summary>
        internal int DistributedRejects => DistributedSourceRejects;

        /// <summary>Gets how many admits failed closed because membership was unavailable.</summary>
        internal int UnavailableRejects { get; private set; }

        /// <summary>Gets how many admitted sessions this tracker actually released.</summary>
        internal int ReleaseCalls { get; private set; }

        /// <inheritdoc />
        public ValueTask<SessionAdmissionResult> TryAdmitAsync(
            string accountName,
            string sessionId,
            IPAddress sourceAddress,
            int sessionLimit,
            int srcIpLimit,
            CancellationToken cancellationToken = default) =>
            TryAdmitAsync(accountName, sessionId, sourceAddress, sessionLimit, srcIpLimit, rateLimitBps: 0, cancellationToken);

        /// <inheritdoc />
        public async ValueTask<SessionAdmissionResult> TryAdmitAsync(
            string accountName,
            string sessionId,
            IPAddress sourceAddress,
            int sessionLimit,
            int srcIpLimit,
            int rateLimitBps,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            ArgumentNullException.ThrowIfNull(sourceAddress);

            var ip = SourceAddressIdentity.Format(sourceAddress);
            var accountGate = _accountGates.GetOrAdd(accountName, static _ => new SemaphoreSlim(1, 1));
            await accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (TryAdmitLocalHotPath(accountName, sessionId, ip, sessionLimit, srcIpLimit, rateLimitBps, out var local))
                {
                    return local;
                }

                if (sessionLimit <= 0 && srcIpLimit <= 0 && rateLimitBps <= 0)
                {
                    return SessionAdmissionResult.Success;
                }

                var trackSessions = sessionLimit > 0 || rateLimitBps > 0;
                long sessionGeneration;
                long sourceGeneration;
                lock (_gate)
                {
                    _accounts.TryGetValue(accountName, out var account);
                    sessionGeneration = trackSessions
                        ? (account is { SessionGeneration: not 0 } ? account.SessionGeneration : NextGeneration())
                        : 0;
                    sourceGeneration = srcIpLimit > 0
                        && account is not null
                        && account.Ips.TryGetValue(ip, out var owner)
                        && owner is { Distributed: true, Generation: not 0 }
                        ? owner.Generation
                        : (srcIpLimit > 0 ? NextGeneration() : 0);
                }

                var now = _timeProvider.GetUtcNow();
                SessionStateAdmitResult membership;
                try
                {
                    membership = await _membership.TryAdmitAsync(
                        accountName,
                        ip,
                        _ownerId,
                        sessionLimit,
                        srcIpLimit,
                        sessionGeneration,
                        sourceGeneration,
                        now,
                        _leaseTtl,
                        cancellationToken,
                        trackSessions).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // TRY_ADMIT is atomic. Cancellation after a successful EVAL (or a
                    // store that throws OCE after writing) must still decrement, or
                    // this process has no local session that can later release.
                    await _membership.ReleaseAsync(
                            accountName,
                            ip,
                            _ownerId,
                            sessionGeneration,
                            sourceGeneration,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!membership.Accepted)
                {
                    if (membership.Status == SessionStateAdmitStatus.Unavailable)
                    {
                        UnavailableRejects++;
                        SessionStateLogMessages.SessionStateUnavailable(_logger, accountName, ip, srcIpLimit, _nodeId);
                        return SessionAdmissionResult.Unavailable;
                    }

                    if (membership.Status == SessionStateAdmitStatus.RejectedSessionLimit)
                    {
                        DistributedSessionRejects++;
                        SessionStateLogMessages.SessionAdmissionRejected(_logger, accountName, ip, sessionLimit, _nodeId);
                        return SessionAdmissionResult.SessionLimitExceeded;
                    }

                    DistributedSourceRejects++;
                    SessionStateLogMessages.SourceAddressRejected(_logger, accountName, ip, srcIpLimit, _nodeId);
                    return SessionAdmissionResult.SourceAddressLimitExceeded;
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CommitDistributedAdmit(
                        accountName,
                        sessionId,
                        ip,
                        sessionLimit,
                        srcIpLimit,
                        sessionGeneration,
                        sourceGeneration,
                        now,
                        trackSessions);
                }
                catch (OperationCanceledException)
                {
                    await _membership.ReleaseAsync(
                            accountName,
                            ip,
                            _ownerId,
                            sessionGeneration,
                            sourceGeneration,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }

                DistributedAdmits++;
                if (rateLimitBps > 0)
                {
                    _rates.ObserveClusterSessionCount(accountName, membership.SessionTotal);
                }

                SessionStateLogMessages.SessionAdmittedDistributed(
                    _logger,
                    accountName,
                    ip,
                    srcIpLimit,
                    _nodeId,
                    membership.Status.ToString());
                return SessionAdmissionResult.Success;
            }
            finally
            {
                accountGate.Release();
            }
        }

        /// <inheritdoc />
        public async ValueTask ReleaseAsync(
            string accountName,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

            var accountGate = _accountGates.GetOrAdd(accountName, static _ => new SemaphoreSlim(1, 1));
            await accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string? ip = null;
                long sessionGeneration = 0;
                long sourceGeneration = 0;
                var releaseMembership = false;
                lock (_gate)
                {
                    if (!_accounts.TryGetValue(accountName, out var account)
                        || !account.Sessions.Remove(sessionId, out var session))
                    {
                        return;
                    }

                    ReleaseCalls++;

                    ip = session.Ip;
                    sessionGeneration = account.SessionGeneration;
                    var distributedSession = account.DistributedSession;
                    if (account.Ips.TryGetValue(ip, out var owner))
                    {
                        owner.Count--;
                        sourceGeneration = owner.Generation;
                        if (owner.Count <= 0)
                        {
                            account.Ips.Remove(ip);
                            releaseMembership = owner.Distributed || distributedSession;
                        }
                        else
                        {
                            releaseMembership = distributedSession;
                        }
                    }
                    else
                    {
                        releaseMembership = distributedSession;
                    }

                    if (account.Sessions.Count == 0)
                    {
                        _accounts.Remove(accountName);
                    }
                }

                if (releaseMembership && ip is not null)
                {
                    var remaining = await _membership.ReleaseAsync(
                            accountName,
                            ip,
                            _ownerId,
                            sessionGeneration,
                            sourceGeneration,
                            cancellationToken)
                        .ConfigureAwait(false);
                    // Redis unavailable maps to 0, the same integer as "last cluster session
                    // released". Observing 0 while local R sessions remain treats remotes as
                    // gone and can raise joiners from blocked to the full account rate.
                    if (remaining > 0)
                    {
                        _rates.ObserveClusterSessionCount(accountName, remaining);
                    }

                    SessionStateLogMessages.SessionStateReleased(_logger, accountName, ip, _nodeId);
                }
            }
            finally
            {
                accountGate.Release();
            }
        }

        /// <inheritdoc />
        public async ValueTask RenewLeasesAsync(CancellationToken cancellationToken = default)
        {
            List<(string Account, long SessionGeneration, List<(string Ip, long Generation)> Sources)> owners;
            lock (_gate)
            {
                owners = [];
                foreach (var (accountName, account) in _accounts)
                {
                    List<(string Ip, long Generation)> sources = [];
                    foreach (var (ip, owner) in account.Ips)
                    {
                        if (owner is { Distributed: true, Count: > 0 })
                        {
                            sources.Add((ip, owner.Generation));
                        }
                    }

                    if (!account.DistributedSession && sources.Count == 0)
                    {
                        continue;
                    }

                    owners.Add((
                        accountName,
                        account.DistributedSession ? account.SessionGeneration : 0,
                        sources));
                }
            }

            var now = _timeProvider.GetUtcNow();
            foreach (var (accountName, sessionGeneration, sources) in owners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SessionStateRenewResult status;
                try
                {
                    status = await RenewAccountAsync(
                        accountName,
                        sessionGeneration,
                        sources,
                        now,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    SessionStateLogMessages.SessionStateLeaseRenewFailed(
                        _logger,
                        ex,
                        accountName,
                        sources.Count > 0 ? sources[0].Ip : "-",
                        _nodeId);
                    continue;
                }

                if (status.Status == SessionStateRenewStatus.Renewed)
                {
                    if (status.SessionTotal > 0)
                    {
                        _rates.ObserveClusterSessionCount(accountName, status.SessionTotal);
                    }

                    foreach (var (ip, _) in sources)
                    {
                        MarkLeaseRenewed(accountName, ip, now);
                        SessionStateLogMessages.SessionStateLeaseRenewed(_logger, accountName, ip, _nodeId);
                    }

                    continue;
                }

                if (status.Status == SessionStateRenewStatus.Unavailable)
                {
                    SessionStateLogMessages.SessionStateLeaseRenewUnavailable(
                        _logger,
                        accountName,
                        sources.Count > 0 ? sources[0].Ip : "-",
                        _nodeId);
                    continue;
                }

                foreach (var (ip, _) in sources)
                {
                    InvalidateHotPath(accountName, ip);
                    SessionStateLogMessages.SessionStateLeaseLost(_logger, accountName, ip, _nodeId);
                }
            }
        }

        private async ValueTask<SessionStateRenewResult> RenewAccountAsync(
            string accountName,
            long sessionGeneration,
            IReadOnlyList<(string Ip, long Generation)> sources,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            if (_bytes is not null
                && _bytes.TryGetCommittedBatch(accountName, out var batch))
            {
                var combined = await _membership.RenewAndApplyAsync(
                    accountName,
                    _ownerId,
                    sessionGeneration,
                    sources,
                    now,
                    _leaseTtl,
                    batch.BatchId,
                    batch.Consumed,
                    batch.MysqlRemaining,
                    cancellationToken).ConfigureAwait(false);
                if (combined.Remaining is { } remaining)
                {
                    _bytes.CompleteApply(accountName, remaining);
                }

                return new SessionStateRenewResult(combined.Renew);
            }

            return await _membership.RenewAsync(
                accountName,
                _ownerId,
                sessionGeneration,
                sources,
                now,
                _leaseTtl,
                cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask ReleaseAllOwnershipAsync(CancellationToken cancellationToken = default)
        {
            List<string> accounts;
            lock (_gate)
            {
                accounts = [];
                foreach (var (accountName, account) in _accounts)
                {
                    if (account.DistributedSession || account.Ips.Values.Any(static owner => owner.Distributed))
                    {
                        accounts.Add(accountName);
                    }
                }
            }

            foreach (var accountName in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _membership.ReleaseOwnerAsync(accountName, _ownerId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    SessionStateLogMessages.SessionStateReleaseFailed(_logger, ex, accountName, _ownerId);
                }
            }
        }

        /// <summary>Returns the local authenticated session count for <paramref name="accountName"/>.</summary>
        internal int GetLocalSessionCount(string accountName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            lock (_gate)
            {
                return _accounts.TryGetValue(accountName, out var account) ? account.Sessions.Count : 0;
            }
        }

        /// <summary>Returns the local reference count for an account/source pair.</summary>
        internal int GetLocalIpCount(string accountName, string normalizedSourceIp)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            ArgumentException.ThrowIfNullOrWhiteSpace(normalizedSourceIp);
            lock (_gate)
            {
                return _accounts.TryGetValue(accountName, out var account)
                    && account.Ips.TryGetValue(normalizedSourceIp, out var owner)
                        ? owner.Count
                        : 0;
            }
        }

        private bool TryAdmitLocalHotPath(
            string accountName,
            string sessionId,
            string ip,
            int sessionLimit,
            int srcIpLimit,
            int rateLimitBps,
            out SessionAdmissionResult result)
        {
            lock (_gate)
            {
                _accounts.TryGetValue(accountName, out var account);
                if (account is not null && account.Sessions.ContainsKey(sessionId))
                {
                    result = SessionAdmissionResult.Success;
                    return true;
                }

                if (sessionLimit <= 0 && srcIpLimit <= 0 && rateLimitBps <= 0)
                {
                    account ??= GetOrCreateAccount(accountName);
                    account.Sessions[sessionId] = new SessionAdmission(ip);
                    if (!account.Ips.TryGetValue(ip, out var unlimited))
                    {
                        unlimited = new IpOwner();
                        account.Ips[ip] = unlimited;
                    }

                    unlimited.Count++;
                    result = SessionAdmissionResult.Success;
                    return true;
                }

                // A finite session limit or positive rate share consumes a cluster-wide
                // slot; the source-IP hot path must not skip Redis.
                if (sessionLimit > 0 || rateLimitBps > 0)
                {
                    result = SessionAdmissionResult.Success;
                    return false;
                }

                var now = _timeProvider.GetUtcNow();
                if (account is not null
                    && account.Ips.TryGetValue(ip, out var owner)
                    && owner.Distributed
                    && owner.HotPathValid
                    && owner.LeaseExpiresAt - _hotPathSkew > now)
                {
                    owner.Count++;
                    owner.SrcIpLimit = srcIpLimit;
                    account.Sessions[sessionId] = new SessionAdmission(ip);
                    LocalHotPathAdmits++;
                    SessionStateLogMessages.SessionAdmittedLocal(_logger, accountName, ip, srcIpLimit, _nodeId);
                    result = SessionAdmissionResult.Success;
                    return true;
                }

                if (account is not null && account.Sessions.Count == 0 && account.Ips.Count == 0)
                {
                    _accounts.Remove(accountName);
                }

                result = SessionAdmissionResult.Success;
                return false;
            }
        }

        private void CommitDistributedAdmit(
            string accountName,
            string sessionId,
            string ip,
            int sessionLimit,
            int srcIpLimit,
            long sessionGeneration,
            long sourceGeneration,
            DateTimeOffset now,
            bool trackSessions)
        {
            lock (_gate)
            {
                var account = GetOrCreateAccount(accountName);
                if (account.Sessions.ContainsKey(sessionId))
                {
                    return;
                }

                if (sessionLimit > 0 || trackSessions)
                {
                    account.DistributedSession = true;
                    account.SessionGeneration = sessionGeneration;
                }

                if (!account.Ips.TryGetValue(ip, out var owner))
                {
                    owner = new IpOwner();
                    account.Ips[ip] = owner;
                }

                owner.Count++;
                if (srcIpLimit > 0)
                {
                    owner.Generation = sourceGeneration;
                    owner.SrcIpLimit = srcIpLimit;
                    owner.Distributed = true;
                    owner.HotPathValid = sessionLimit <= 0;
                    owner.LeaseExpiresAt = now + _leaseTtl;
                }

                account.Sessions[sessionId] = new SessionAdmission(ip);
            }
        }

        private AccountAdmission GetOrCreateAccount(string accountName)
        {
            if (!_accounts.TryGetValue(accountName, out var account))
            {
                account = new AccountAdmission();
                _accounts[accountName] = account;
            }

            return account;
        }

        private void MarkLeaseRenewed(string accountName, string ip, DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_accounts.TryGetValue(accountName, out var account)
                    && account.Ips.TryGetValue(ip, out var owner)
                    && owner.Distributed)
                {
                    owner.LeaseExpiresAt = now + _leaseTtl;
                    owner.HotPathValid = !account.DistributedSession;
                }
            }
        }

        private void InvalidateHotPath(string accountName, string ip)
        {
            lock (_gate)
            {
                if (_accounts.TryGetValue(accountName, out var account)
                    && account.Ips.TryGetValue(ip, out var owner))
                {
                    owner.HotPathValid = false;
                    owner.LeaseExpiresAt = DateTimeOffset.MinValue;
                }
            }
        }

        private long NextGeneration() => Interlocked.Increment(ref _generations);

        private sealed class AccountAdmission
        {
            public Dictionary<string, SessionAdmission> Sessions { get; } = new(StringComparer.Ordinal);

            public Dictionary<string, IpOwner> Ips { get; } = new(StringComparer.Ordinal);

            public bool DistributedSession { get; set; }

            public long SessionGeneration { get; set; }
        }

        private sealed class SessionAdmission
        {
            public SessionAdmission(string ip)
            {
                Ip = ip;
            }

            public string Ip { get; }
        }

        private sealed class IpOwner
        {
            public int Count { get; set; }

            public long Generation { get; set; }

            public int SrcIpLimit { get; set; }

            public bool Distributed { get; set; }

            public bool HotPathValid { get; set; }

            public DateTimeOffset LeaseExpiresAt { get; set; }
        }
    }
}
