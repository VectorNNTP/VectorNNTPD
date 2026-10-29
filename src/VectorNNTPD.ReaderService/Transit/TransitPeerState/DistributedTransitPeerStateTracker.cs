using System.Collections.Concurrent;

namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// Hybrid Transit inbound admission: Redis is the cluster authority for
    /// <c>MaxIncomingConnections</c>. Local counts are bookkeeping only.
    /// </summary>
    /// <remarks>
    /// Identity is <see cref="TransitPeerPolicy.Identifier"/>. Source IP is never
    /// used as a Redis key. <c>MaxIncomingConnections == 0</c> is closed, not
    /// unlimited. Every named-peer admit requires a successful distributed TRY_ADMIT.
    /// Owner identity is <c>{nodeId}:{incarnation}</c>. Redis TTL is crash recovery
    /// only: while this process is alive and renewal succeeds, idle Transit
    /// connections keep consuming cluster capacity.
    /// </remarks>
    public sealed class DistributedTransitPeerStateTracker : ITransitPeerStateTracker, ITransitPeerStateLeaseManager
    {
        private readonly ITransitPeerStateStore _membership;
        private readonly ILogger<DistributedTransitPeerStateTracker> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly string _nodeId;
        private readonly string _ownerId;
        private readonly TimeSpan _leaseTtl;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _identifierGates = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private readonly Dictionary<string, PeerOwnership> _peers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ownedIdentifiers = new(StringComparer.Ordinal);
        private long _generations;
        private bool _accepting = true;

        /// <summary>Initializes a production tracker with default lease timings.</summary>
        public DistributedTransitPeerStateTracker(
            ITransitPeerStateStore membership,
            ILogger<DistributedTransitPeerStateTracker> logger,
            string nodeId)
            : this(membership, logger, nodeId, TimeProvider.System, TransitPeerStateDefaults.LeaseTtl)
        {
        }

        /// <summary>Initializes a tracker with explicit timing and incarnation (tests).</summary>
        internal DistributedTransitPeerStateTracker(
            ITransitPeerStateStore membership,
            ILogger<DistributedTransitPeerStateTracker> logger,
            string nodeId,
            TimeProvider timeProvider,
            TimeSpan? leaseTtl = null,
            string? incarnation = null)
        {
            ArgumentNullException.ThrowIfNull(membership);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
            ArgumentNullException.ThrowIfNull(timeProvider);
            _membership = membership;
            _logger = logger;
            _nodeId = nodeId;
            _ownerId = string.Concat(nodeId, ":", string.IsNullOrWhiteSpace(incarnation) ? Guid.NewGuid().ToString("N") : incarnation);
            _timeProvider = timeProvider;
            _leaseTtl = leaseTtl ?? TransitPeerStateDefaults.LeaseTtl;
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_leaseTtl, TimeSpan.Zero);
        }

        /// <summary>Gets the configured node identity.</summary>
        internal string NodeId => _nodeId;

        /// <summary>Gets <c>{nodeId}:{incarnation}</c> written into Redis ownership fields.</summary>
        internal string OwnerId => _ownerId;

        /// <summary>Gets how many admits required a distributed membership call that succeeded.</summary>
        internal int DistributedAdmits { get; private set; }

        /// <summary>Gets how many admits were rejected by the cluster inbound limit or a closed peer.</summary>
        internal int DistributedRejects { get; private set; }

        /// <summary>Gets how many admits failed closed because membership was unavailable.</summary>
        internal int UnavailableRejects { get; private set; }

        /// <summary>Gets how many admitted connections this tracker actually released.</summary>
        internal int ReleaseCalls { get; private set; }

        /// <inheritdoc />
        public async ValueTask<TransitPeerStateAdmitResult> TryAdmitAsync(
            string identifier,
            int maxIncoming,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

            var identifierGate = _identifierGates.GetOrAdd(identifier, static _ => new SemaphoreSlim(1, 1));
            await identifierGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                long generation;
                lock (_gate)
                {
                    if (!_accepting)
                    {
                        DistributedRejects++;
                        return new TransitPeerStateAdmitResult(TransitPeerStateAdmitStatus.Rejected);
                    }

                    generation = _peers.TryGetValue(identifier, out var peer) && peer.Generation != 0
                        ? peer.Generation
                        : NextGeneration();
                }

                var now = _timeProvider.GetUtcNow();
                TransitPeerStateAdmitResult membership;
                try
                {
                    membership = await _membership.TryAdmitAsync(
                        identifier,
                        _ownerId,
                        maxIncoming,
                        generation,
                        now,
                        _leaseTtl,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // TRY_ADMIT is atomic. Cancellation after a successful EVAL (or a
                    // store that throws OCE after writing) must still decrement, or
                    // this process has no local connection that can later release.
                    await _membership.ReleaseAsync(
                            identifier,
                            _ownerId,
                            generation,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!membership.Accepted)
                    {
                        if (membership.Status == TransitPeerStateAdmitStatus.Unavailable)
                        {
                            UnavailableRejects++;
                            TransitPeerStateLogMessages.TransitPeerUnavailable(_logger, identifier, maxIncoming, _nodeId);
                            return membership;
                        }

                        DistributedRejects++;
                        TransitPeerStateLogMessages.TransitPeerRejected(_logger, identifier, maxIncoming, _nodeId);
                        return membership;
                    }

                    CommitDistributedAdmit(identifier, generation, now);
                }
                catch (OperationCanceledException)
                {
                    if (membership.Accepted)
                    {
                        await _membership.ReleaseAsync(
                                identifier,
                                _ownerId,
                                generation,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    throw;
                }

                DistributedAdmits++;
                TransitPeerStateLogMessages.TransitPeerAdmitted(_logger, identifier, maxIncoming, _nodeId);
                return new TransitPeerStateAdmitResult(TransitPeerStateAdmitStatus.Accepted, generation);
            }
            finally
            {
                identifierGate.Release();
            }
        }

        /// <inheritdoc />
        public async ValueTask ReleaseAsync(
            string identifier,
            long generation,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

            var identifierGate = _identifierGates.GetOrAdd(identifier, static _ => new SemaphoreSlim(1, 1));
            await identifierGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var releaseMembership = false;
                lock (_gate)
                {
                    if (!_peers.TryGetValue(identifier, out var peer) || peer.LocalCount <= 0)
                    {
                        return;
                    }

                    ReleaseCalls++;
                    peer.LocalCount--;
                    releaseMembership = true;
                    if (peer.LocalCount <= 0)
                    {
                        _peers.Remove(identifier);
                    }
                }

                if (releaseMembership)
                {
                    await _membership.ReleaseAsync(
                            identifier,
                            _ownerId,
                            generation,
                            cancellationToken)
                        .ConfigureAwait(false);
                    TransitPeerStateLogMessages.TransitPeerReleased(_logger, identifier, _nodeId);
                }
            }
            finally
            {
                identifierGate.Release();
            }
        }

        /// <inheritdoc />
        public async ValueTask RenewLeasesAsync(CancellationToken cancellationToken = default)
        {
            List<(string Identifier, long Generation)> owners;
            lock (_gate)
            {
                owners = [];
                foreach (var (identifier, peer) in _peers)
                {
                    if (peer.LocalCount > 0 && peer.Generation != 0)
                    {
                        owners.Add((identifier, peer.Generation));
                    }
                }
            }

            var now = _timeProvider.GetUtcNow();
            foreach (var (identifier, generation) in owners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TransitPeerStateRenewStatus status;
                try
                {
                    status = await _membership.RenewAsync(
                        identifier,
                        _ownerId,
                        generation,
                        now,
                        _leaseTtl,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    TransitPeerStateLogMessages.TransitPeerLeaseRenewFailed(_logger, ex, identifier, _nodeId);
                    continue;
                }

                if (status == TransitPeerStateRenewStatus.Renewed)
                {
                    MarkLeaseRenewed(identifier, now);
                    TransitPeerStateLogMessages.TransitPeerLeaseRenewed(_logger, identifier, _nodeId);
                    continue;
                }

                if (status == TransitPeerStateRenewStatus.Unavailable)
                {
                    TransitPeerStateLogMessages.TransitPeerLeaseRenewUnavailable(_logger, identifier, _nodeId);
                    continue;
                }

                TransitPeerStateLogMessages.TransitPeerLeaseLost(_logger, identifier, _nodeId);
            }
        }

        /// <inheritdoc />
        public async ValueTask ReleaseAllOwnershipAsync(CancellationToken cancellationToken = default)
        {
            List<string> identifiers;
            lock (_gate)
            {
                _accepting = false;
                identifiers = [.. _ownedIdentifiers];
            }

            foreach (var identifier in identifiers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _membership.ReleaseOwnerAsync(identifier, _ownerId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    TransitPeerStateLogMessages.TransitPeerReleaseOwnerFailed(_logger, ex, identifier, _ownerId);
                }
            }
        }

        /// <inheritdoc />
        public int GetLocalCount(string identifier)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
            lock (_gate)
            {
                return _peers.TryGetValue(identifier, out var peer) ? peer.LocalCount : 0;
            }
        }

        /// <summary>Returns whether this tracker still accepts new admissions (tests).</summary>
        internal bool IsAccepting
        {
            get
            {
                lock (_gate)
                {
                    return _accepting;
                }
            }
        }

        private void CommitDistributedAdmit(string identifier, long generation, DateTimeOffset now)
        {
            lock (_gate)
            {
                if (!_peers.TryGetValue(identifier, out var peer))
                {
                    peer = new PeerOwnership();
                    _peers[identifier] = peer;
                }

                peer.Generation = generation;
                peer.LocalCount++;
                peer.LeaseExpiresAt = now + _leaseTtl;
                _ownedIdentifiers.Add(identifier);
            }
        }

        private void MarkLeaseRenewed(string identifier, DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_peers.TryGetValue(identifier, out var peer))
                {
                    peer.LeaseExpiresAt = now + _leaseTtl;
                }
            }
        }

        private long NextGeneration() => Interlocked.Increment(ref _generations);

        private sealed class PeerOwnership
        {
            public int LocalCount { get; set; }

            public long Generation { get; set; }

            public DateTimeOffset LeaseExpiresAt { get; set; }
        }
    }
}
