namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>
    /// Admits inbound connections associated with a named Transit peer.
    /// </summary>
    /// <remarks>
    /// Listeners remain Transit-facing. Redis is the admission authority for named
    /// peers. A process-local count is diagnostics only.
    /// </remarks>
    public interface ITransitInboundConnectionLimiter
    {
        /// <summary>
        /// Attempts to consume one inbound slot for <paramref name="peerName"/> using the
        /// current snapshot's <c>MaxIncomingConnections</c>.
        /// </summary>
        /// <returns>
        /// An outcome that is admitted when a cluster slot was taken (or no slot is
        /// required). Rejected when the peer is unknown, closed, at its cluster
        /// limit, or Redis is unavailable.
        /// </returns>
        ValueTask<TransitInboundAdmitResult> TryAcquireAsync(
            string peerName,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Outcome of a Transit inbound slot acquire.</summary>
    public readonly struct TransitInboundAdmitResult
    {
        /// <summary>Initializes an inbound admit outcome.</summary>
        public TransitInboundAdmitResult(bool admitted, TransitInboundConnectionLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);
            Admitted = admitted;
            Lease = lease;
        }

        /// <summary>Gets whether the connection may proceed.</summary>
        public bool Admitted { get; }

        /// <summary>Gets the lease to dispose when the connection ends. Never null.</summary>
        public TransitInboundConnectionLease Lease { get; }

        /// <summary>Rejected without a held slot.</summary>
        public static TransitInboundAdmitResult Rejected { get; } =
            new(false, TransitInboundConnectionLease.None);

        /// <summary>Admitted without a held slot (non-transit or disabled limiter).</summary>
        public static TransitInboundAdmitResult Uncounted { get; } =
            new(true, TransitInboundConnectionLease.None);

        /// <summary>Admitted with a slot that must be released exactly once.</summary>
        public static TransitInboundAdmitResult Accept(TransitInboundConnectionLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);
            return new TransitInboundAdmitResult(true, lease);
        }
    }

    /// <summary>Releases one inbound Transit peer slot when disposed. One-shot.</summary>
    public sealed class TransitInboundConnectionLease : IAsyncDisposable, IDisposable
    {
        private readonly Func<CancellationToken, ValueTask>? _release;
        private int _released;

        internal TransitInboundConnectionLease(Func<CancellationToken, ValueTask> release)
        {
            ArgumentNullException.ThrowIfNull(release);
            _release = release;
        }

        private TransitInboundConnectionLease()
        {
        }

        /// <summary>Empty lease (no slot was taken).</summary>
        public static TransitInboundConnectionLease None { get; } = new();

        /// <summary>Gets whether this lease holds a slot that has not yet been released.</summary>
        public bool IsHeld => _release is not null && Volatile.Read(ref _released) == 0;

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_release is null)
            {
                return;
            }

            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            await _release(CancellationToken.None).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
