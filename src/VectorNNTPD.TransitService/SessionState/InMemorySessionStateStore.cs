namespace VectorNNTPD.TransitService.SessionState
{
    /// <summary>
    /// Process-local cluster fake for session + source-IP membership. Two trackers
    /// sharing one instance behave as two NNTP nodes. Ordinary unit tests must not
    /// require live Redis.
    /// </summary>
    internal sealed class InMemorySessionStateStore : ISessionStateStore
    {
        private readonly SessionStateEngine _engine = new();

        /// <summary>When set, admit/release/renew fail closed as if Redis were down.</summary>
        public bool Unavailable { get; set; }

        /// <summary>Optional gate that blocks the next admit until completed or cancelled.</summary>
        public TaskCompletionSource? BlockAdmit { get; set; }

        /// <summary>Optional gate that blocks the next release until completed or cancelled.</summary>
        public TaskCompletionSource? BlockRelease { get; set; }

        /// <summary>Optional gate that blocks the next renew until completed or cancelled.</summary>
        public TaskCompletionSource? BlockRenew { get; set; }

        /// <summary>Set when <see cref="RenewAsync"/> is entered, before <see cref="BlockRenew"/> waits.</summary>
        public TaskCompletionSource? NotifyRenewStarted { get; set; }

        /// <summary>When set, <see cref="RenewAsync"/> throws for this account so other accounts can still renew.</summary>
        public string? FaultRenewAccount { get; set; }

        /// <summary>Gets how many membership renew operations were attempted.</summary>
        public int RenewCalls { get; private set; }

        /// <summary>Gets the shared admission engine (tests).</summary>
        internal SessionStateEngine Engine => _engine;

        /// <summary>In-memory source hash identity for <paramref name="accountName"/>.</summary>
        internal static string SourceKey(string accountName) => "src:" + accountName;

        /// <summary>In-memory session hash identity for <paramref name="accountName"/>.</summary>
        internal static string SessionKey(string accountName) => "sess:" + accountName;

        /// <summary>Returns whether <paramref name="ownerId"/> currently holds <paramref name="normalizedSourceIp"/>.</summary>
        internal bool HasSourceOwner(string accountName, string normalizedSourceIp, string ownerId, long nowUnixMs) =>
            _engine.HasSourceOwner(SourceKey(accountName), normalizedSourceIp, ownerId, nowUnixMs);

        /// <summary>Returns the cluster-wide authenticated session count for <paramref name="accountName"/>.</summary>
        internal int ActiveSessionCount(string accountName, long nowUnixMs) =>
            _engine.ActiveSessionCount(SessionKey(accountName), nowUnixMs);

        /// <summary>Returns this owner's unexpired session count.</summary>
        internal int OwnerSessionCount(string accountName, string ownerId, long nowUnixMs) =>
            _engine.OwnerSessionCount(SessionKey(accountName), ownerId, nowUnixMs);

        /// <summary>Returns distinct unexpired source IPs for <paramref name="accountName"/>.</summary>
        internal IReadOnlyCollection<string> ActiveSourceIps(string accountName, long nowUnixMs) =>
            _engine.ActiveSourceIps(SourceKey(accountName), nowUnixMs);

        /// <summary>Returns the stored session-ownership expiry, or <see langword="null"/> when absent.</summary>
        internal long? SessionExpiry(string accountName, string ownerId) =>
            _engine.TryGetOwnership(SessionKey(accountName), ownerId, out var expiry, out _, out _)
                ? expiry
                : null;

        /// <summary>Returns the stored source-ownership expiry, or <see langword="null"/> when absent.</summary>
        internal long? SourceExpiry(string accountName, string normalizedSourceIp, string ownerId) =>
            _engine.TryGetOwnership(
                SourceKey(accountName),
                SessionStateKeys.SourceField(normalizedSourceIp, ownerId),
                out var expiry,
                out _,
                out _)
                ? expiry
                : null;

        /// <summary>Changes a source field's generation so a later renew is all-or-nothing rejected.</summary>
        internal void BreakSourceGeneration(string accountName, string normalizedSourceIp, string ownerId)
        {
            var field = SessionStateKeys.SourceField(normalizedSourceIp, ownerId);
            if (!_engine.TryGetOwnership(SourceKey(accountName), field, out var expiry, out var generation, out var count))
            {
                throw new InvalidOperationException("Source ownership is missing.");
            }

            _engine.WriteOwnership(SourceKey(accountName), field, expiry, generation + 1, count);
        }

        /// <inheritdoc />
        public async ValueTask<SessionStateAdmitResult> TryAdmitAsync(
            string accountName,
            string normalizedSourceIp,
            string ownerId,
            int sessionLimit,
            int srcIpLimit,
            long sessionGeneration,
            long sourceGeneration,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default,
            bool trackSessions = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            if (BlockAdmit is { } block)
            {
                await block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                return new SessionStateAdmitResult(SessionStateAdmitStatus.Unavailable);
            }

            var code = _engine.TryAdmit(
                SourceKey(accountName),
                SessionKey(accountName),
                normalizedSourceIp,
                ownerId,
                sessionLimit,
                srcIpLimit,
                now.ToUnixTimeMilliseconds(),
                (long)leaseTtl.TotalMilliseconds,
                sessionGeneration,
                sourceGeneration,
                trackSessions);
            var status = SessionStateEngine.UnpackAdmitStatus(code);
            var total = SessionStateEngine.UnpackAdmitSessionTotal(code);
            return status switch
            {
                SessionStateEngine.AcceptedExisting =>
                    new SessionStateAdmitResult(SessionStateAdmitStatus.AcceptedExisting, total),
                SessionStateEngine.AcceptedNew =>
                    new SessionStateAdmitResult(SessionStateAdmitStatus.AcceptedNew, total),
                SessionStateEngine.RejectedSessionLimit =>
                    new SessionStateAdmitResult(SessionStateAdmitStatus.RejectedSessionLimit),
                _ => new SessionStateAdmitResult(SessionStateAdmitStatus.RejectedSourceLimit),
            };
        }

        /// <inheritdoc />
        public async ValueTask<int> ReleaseAsync(
            string accountName,
            string normalizedSourceIp,
            string ownerId,
            long sessionGeneration,
            long sourceGeneration,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            if (BlockRelease is { } block)
            {
                await block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                return 0;
            }

            return (int)_engine.Release(
                SourceKey(accountName),
                SessionKey(accountName),
                normalizedSourceIp,
                ownerId,
                sessionGeneration,
                sourceGeneration);
        }

        /// <inheritdoc />
        public async ValueTask<SessionStateRenewResult> RenewAsync(
            string accountName,
            string ownerId,
            long sessionGeneration,
            IReadOnlyList<(string Ip, long Generation)> sources,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            ArgumentNullException.ThrowIfNull(sources);
            if (FaultRenewAccount is { } fault
                && string.Equals(fault, accountName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected renewal fault.");
            }

            NotifyRenewStarted?.TrySetResult();
            if (BlockRenew is { } block)
            {
                await block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RenewCalls++;
            if (Unavailable)
            {
                return new SessionStateRenewResult(SessionStateRenewStatus.Unavailable);
            }

            var result = _engine.Renew(
                SourceKey(accountName),
                SessionKey(accountName),
                ownerId,
                sessionGeneration,
                now.ToUnixTimeMilliseconds(),
                (long)leaseTtl.TotalMilliseconds,
                sources);
            return result == 0
                ? new SessionStateRenewResult(SessionStateRenewStatus.Lost)
                : new SessionStateRenewResult(SessionStateRenewStatus.Renewed, (int)result);
        }

        /// <inheritdoc />
        public async ValueTask<SessionStateRenewAndApplyResult> RenewAndApplyAsync(
            string accountName,
            string ownerId,
            long sessionGeneration,
            IReadOnlyList<(string Ip, long Generation)> sources,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            string batchId,
            long consumed,
            long mysqlRemainingAfter,
            CancellationToken cancellationToken = default)
        {
            _ = batchId;
            _ = consumed;
            _ = mysqlRemainingAfter;
            var status = await RenewAsync(
                accountName,
                ownerId,
                sessionGeneration,
                sources,
                now,
                leaseTtl,
                cancellationToken).ConfigureAwait(false);
            return new SessionStateRenewAndApplyResult(status.Status, remaining: null);
        }

        /// <inheritdoc />
        public ValueTask ReleaseOwnerAsync(
            string accountName,
            string ownerId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Unavailable)
            {
                _ = _engine.ReleaseOwner(SourceKey(accountName), SessionKey(accountName), ownerId);
            }

            return ValueTask.CompletedTask;
        }
    }
}
