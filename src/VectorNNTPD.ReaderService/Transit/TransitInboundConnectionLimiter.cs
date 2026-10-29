namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// Transit-facing inbound limiter. Redis is authoritative for named peers;
    /// the process-local count is diagnostics and lifecycle bookkeeping only.
    /// </summary>
    /// <remarks>
    /// Lowering <c>MaxIncomingConnections</c> on reload does not disconnect existing holders.
    /// Removing a peer rejects new acquires for that name; existing leases still release.
    /// <c>0</c> admits no new inbound connections. Redis unavailability fails closed.
    /// </remarks>
    public sealed class TransitInboundConnectionLimiter : ITransitInboundConnectionLimiter
    {
        private readonly TransitConfigurationStore _store;
        private readonly ITransitPeerStateTracker _tracker;

        /// <summary>Shared no-op limiter for tests that do not exercise inbound limits.</summary>
        public static ITransitInboundConnectionLimiter Disabled { get; } = new DisabledLimiter();

        /// <summary>Initializes a limiter bound to the active Transit snapshot and cluster tracker.</summary>
        public TransitInboundConnectionLimiter(TransitConfigurationStore store, ITransitPeerStateTracker tracker)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(tracker);
            _store = store;
            _tracker = tracker;
        }

        /// <summary>Gets the current local owned count for <paramref name="peerName"/> (tests/diagnostics).</summary>
        public int GetCount(string peerName) => _tracker.GetLocalCount(peerName);

        /// <inheritdoc />
        public async ValueTask<TransitInboundAdmitResult> TryAcquireAsync(
            string peerName,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(peerName);
            if (!_store.Current.Peers.TryGetValue(peerName, out var peer))
            {
                return TransitInboundAdmitResult.Rejected;
            }

            var max = peer.MaxIncomingConnections;
            var admitted = await _tracker
                .TryAdmitAsync(peerName, max, cancellationToken)
                .ConfigureAwait(false);
            if (!admitted.Accepted)
            {
                return TransitInboundAdmitResult.Rejected;
            }

            var generation = admitted.Generation;
            return TransitInboundAdmitResult.Accept(
                new TransitInboundConnectionLease(ct => _tracker.ReleaseAsync(peerName, generation, ct)));
        }

        private sealed class DisabledLimiter : ITransitInboundConnectionLimiter
        {
            public ValueTask<TransitInboundAdmitResult> TryAcquireAsync(
                string peerName,
                CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(TransitInboundAdmitResult.Uncounted);
        }
    }
}
