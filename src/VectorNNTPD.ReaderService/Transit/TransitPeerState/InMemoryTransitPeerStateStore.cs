namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// Process-local cluster fake for Transit inbound membership. Two trackers
    /// sharing one instance behave as two NNTP nodes. Ordinary unit tests must not
    /// require live Redis.
    /// </summary>
    internal sealed class InMemoryTransitPeerStateStore : ITransitPeerStateStore
    {
        private readonly TransitPeerStateEngine _engine = new();

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

        /// <summary>When set, <see cref="TryAdmitAsync"/> throws after a successful engine write.</summary>
        public Exception? ThrowAfterAdmit { get; set; }

        /// <summary>When set, <see cref="RenewAsync"/> throws for this identifier so others can still renew.</summary>
        public string? FaultRenewIdentifier { get; set; }

        /// <summary>Gets how many membership renew operations were attempted.</summary>
        /// <remarks>
        /// After <see cref="RenewAsync"/> reaches the engine, this increments only once that
        /// renew has returned, so a non-zero count means the renewed expiry is already visible.
        /// The unavailable path increments without an engine write.
        /// </remarks>
        public int RenewCalls { get; private set; }

        /// <summary>Gets how many admit operations reached the engine.</summary>
        public int AdmitCalls { get; private set; }

        /// <summary>Gets how many release operations reached the engine.</summary>
        public int ReleaseCalls { get; private set; }

        /// <summary>Gets the shared admission engine (tests).</summary>
        internal TransitPeerStateEngine Engine => _engine;

        /// <summary>In-memory hash identity for <paramref name="identifier"/>.</summary>
        internal static string ConnectionKey(string identifier) => "tconn:" + identifier;

        /// <summary>Returns the cluster-wide unexpired inbound count.</summary>
        internal int ActiveCount(string identifier, long nowUnixMs) =>
            _engine.ActiveCount(ConnectionKey(identifier), nowUnixMs);

        /// <summary>Returns this owner's unexpired inbound count.</summary>
        internal int OwnerCount(string identifier, string ownerId, long nowUnixMs) =>
            _engine.OwnerCount(ConnectionKey(identifier), ownerId, nowUnixMs);

        /// <summary>Returns the stored ownership expiry, or <see langword="null"/> when absent.</summary>
        internal long? Expiry(string identifier, string ownerId) =>
            _engine.TryGetOwnership(ConnectionKey(identifier), ownerId, out var expiry, out _, out _)
                ? expiry
                : null;

        /// <inheritdoc />
        public async ValueTask<TransitPeerStateAdmitResult> TryAdmitAsync(
            string identifier,
            string ownerId,
            int maxIncoming,
            long generation,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
            if (BlockAdmit is { } block)
            {
                await block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                return new TransitPeerStateAdmitResult(TransitPeerStateAdmitStatus.Unavailable);
            }

            AdmitCalls++;
            var code = _engine.TryAdmit(
                ConnectionKey(identifier),
                ownerId,
                maxIncoming,
                now.ToUnixTimeMilliseconds(),
                (long)leaseTtl.TotalMilliseconds,
                generation);
            if (ThrowAfterAdmit is { } fault)
            {
                throw fault;
            }

            return code == TransitPeerStateEngine.Accepted
                ? new TransitPeerStateAdmitResult(TransitPeerStateAdmitStatus.Accepted, generation)
                : new TransitPeerStateAdmitResult(TransitPeerStateAdmitStatus.Rejected);
        }

        /// <inheritdoc />
        public async ValueTask ReleaseAsync(
            string identifier,
            string ownerId,
            long generation,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
            if (BlockRelease is { } block)
            {
                await block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                return;
            }

            ReleaseCalls++;
            _ = _engine.Release(ConnectionKey(identifier), ownerId, generation);
        }

        /// <inheritdoc />
        public async ValueTask<TransitPeerStateRenewStatus> RenewAsync(
            string identifier,
            string ownerId,
            long generation,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
            if (FaultRenewIdentifier is { } fault
                && string.Equals(fault, identifier, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected renewal fault.");
            }

            NotifyRenewStarted?.TrySetResult();
            if (BlockRenew is { } block)
            {
                await block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                RenewCalls++;
                return TransitPeerStateRenewStatus.Unavailable;
            }

            var renewed = _engine.Renew(
                ConnectionKey(identifier),
                ownerId,
                generation,
                now.ToUnixTimeMilliseconds(),
                (long)leaseTtl.TotalMilliseconds) == 1;
            RenewCalls++;
            return renewed
                ? TransitPeerStateRenewStatus.Renewed
                : TransitPeerStateRenewStatus.Lost;
        }

        /// <inheritdoc />
        public ValueTask ReleaseOwnerAsync(
            string identifier,
            string ownerId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Unavailable)
            {
                _ = _engine.ReleaseOwner(ConnectionKey(identifier), ownerId);
            }

            return ValueTask.CompletedTask;
        }
    }
}
