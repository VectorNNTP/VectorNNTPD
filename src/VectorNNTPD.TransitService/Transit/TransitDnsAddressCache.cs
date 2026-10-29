using System.Collections.Concurrent;
using System.Net;
using VectorNNTPD.TransitService.Networking.Proxy;

namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>
    /// Per-hostname DNS cache with TTL-based refresh and a 60-second minimum interval.
    /// </summary>
    public sealed class TransitDnsAddressCache : ITransitDnsAddressCache
    {
        private static readonly IReadOnlySet<IPAddress> EmptySet = new HashSet<IPAddress>();

        private readonly ITransitDnsResolver _resolver;
        private readonly ILogger<TransitDnsAddressCache> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _minimumRefreshInterval;
        private readonly ConcurrentDictionary<string, HostState> _hosts =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Initializes the cache with the production 60-second minimum refresh interval.</summary>
        public TransitDnsAddressCache(
            ITransitDnsResolver resolver,
            ILogger<TransitDnsAddressCache> logger,
            TimeProvider? timeProvider = null)
            : this(resolver, logger, ITransitDnsAddressCache.MinimumRefreshInterval, timeProvider)
        {
        }

        /// <summary>Test constructor with an injectable minimum refresh interval.</summary>
        internal TransitDnsAddressCache(
            ITransitDnsResolver resolver,
            ILogger<TransitDnsAddressCache> logger,
            TimeSpan minimumRefreshInterval,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            ArgumentNullException.ThrowIfNull(logger);
            _resolver = resolver;
            _logger = logger;
            _minimumRefreshInterval = minimumRefreshInterval;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Dictionary lookup only. Does not call <see cref="ITransitDnsResolver"/>,
        /// <c>Dns.GetHostEntry</c>, <c>Dns.GetHostAddresses</c>, or perform network I/O.
        /// </remarks>
        public IReadOnlySet<IPAddress> GetResolved(string hostname)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
            return _hosts.TryGetValue(hostname, out var state) ? state.Addresses : EmptySet;
        }

        /// <inheritdoc />
        public Task RefreshAllAsync(TransitConfigurationSnapshot snapshot, CancellationToken cancellationToken) =>
            RefreshCoreAsync(snapshot, dueOnly: false, cancellationToken);

        /// <inheritdoc />
        public Task RefreshDueAsync(TransitConfigurationSnapshot snapshot, CancellationToken cancellationToken) =>
            RefreshCoreAsync(snapshot, dueOnly: true, cancellationToken);

        /// <inheritdoc />
        public TimeSpan GetDelayUntilNextRefresh(TransitConfigurationSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var names = CollectHostnames(snapshot);
            if (names.Count == 0)
            {
                return Timeout.InfiniteTimeSpan;
            }

            var now = _timeProvider.GetUtcNow();
            var next = DateTimeOffset.MaxValue;
            foreach (var name in names.Keys)
            {
                if (!_hosts.TryGetValue(name, out var state))
                {
                    return TimeSpan.Zero;
                }

                if (state.NextRefreshUtc < next)
                {
                    next = state.NextRefreshUtc;
                }
            }

            var delay = next - now;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        private async Task RefreshCoreAsync(
            TransitConfigurationSnapshot snapshot,
            bool dueOnly,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var hosts = CollectHostnames(snapshot);
            PruneUnused(hosts.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
            var now = _timeProvider.GetUtcNow();
            foreach (var (name, peerNames) in hosts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (dueOnly
                    && _hosts.TryGetValue(name, out var existing)
                    && existing.NextRefreshUtc > now)
                {
                    continue;
                }

                await RefreshHostnameAsync(snapshot, name, peerNames, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task RefreshHostnameAsync(
            TransitConfigurationSnapshot snapshot,
            string hostname,
            IReadOnlyList<string> peerNames,
            CancellationToken cancellationToken)
        {
            TransitDnsResolveResult result;
            string? failureReason = null;
            try
            {
                result = await _resolver.ResolveAsync(hostname, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                failureReason = ex.Message;
                result = TransitDnsResolveResult.TransientFailure();
            }

            var now = _timeProvider.GetUtcNow();
            var next = now + RefreshInterval(result.TimeToLive);
            switch (result.Outcome)
            {
                case TransitDnsOutcome.Success:
                    var set = Deduplicate(result.Addresses);
                    _hosts[hostname] = new HostState(set, next);
                    TransitLogMessages.DnsResolved(_logger, hostname, set.Count, next);
                    break;
                case TransitDnsOutcome.Empty:
                    _hosts[hostname] = new HostState(new HashSet<IPAddress>(), next);
                    LogDnsFailure(
                        snapshot,
                        peerNames,
                        hostname,
                        failureReason ?? "NXDOMAIN or NOERROR with no A/AAAA records");
                    break;
                default:
                    _hosts[hostname] = _hosts.TryGetValue(hostname, out var previous)
                        ? new HostState(previous.Addresses, next)
                        : new HostState(new HashSet<IPAddress>(), next);

                    LogDnsFailure(snapshot, peerNames, hostname, failureReason ?? "transient DNS failure");
                    break;
            }
        }

        private void LogDnsFailure(
            TransitConfigurationSnapshot snapshot,
            IReadOnlyList<string> peerIds,
            string hostname,
            string reason)
        {
            foreach (var peerId in peerIds)
            {
                var display = snapshot.Peers.TryGetValue(peerId, out var peer) ? peer.PeerName : peerId;
                TransitLogMessages.DnsResolutionFailed(_logger, peerId, display, hostname, reason);
            }
        }

        private TimeSpan RefreshInterval(TimeSpan? ttl)
        {
            if (ttl is not { } value || value <= TimeSpan.Zero)
            {
                return _minimumRefreshInterval;
            }

            return value > _minimumRefreshInterval ? value : _minimumRefreshInterval;
        }

        private static Dictionary<string, List<string>> CollectHostnames(TransitConfigurationSnapshot snapshot)
        {
            var names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var peer in snapshot.Peers.Values)
            {
                foreach (var hostname in peer.DnsHostnames)
                {
                    if (!names.TryGetValue(hostname, out var peers))
                    {
                        peers = [];
                        names[hostname] = peers;
                    }

                    if (!peers.Contains(peer.Identifier, StringComparer.Ordinal))
                    {
                        peers.Add(peer.Identifier);
                    }
                }
            }

            return names;
        }

        private void PruneUnused(HashSet<string> live)
        {
            // codeql[cs/linq/missed-where] ConcurrentDictionary Keys with TryRemove side effects.
            foreach (var key in _hosts.Keys)
            {
                if (!live.Contains(key))
                {
                    _hosts.TryRemove(key, out _);
                }
            }
        }

        private static IReadOnlySet<IPAddress> Deduplicate(IReadOnlyList<IPAddress> addresses)
        {
            var set = new HashSet<IPAddress>();
            foreach (var address in addresses)
            {
                set.Add(TrustedProxyHosts.Canonicalize(address));
            }

            return set;
        }

        private sealed class HostState(IReadOnlySet<IPAddress> addresses, DateTimeOffset nextRefreshUtc)
        {
            public IReadOnlySet<IPAddress> Addresses { get; } = addresses;

            public DateTimeOffset NextRefreshUtc { get; } = nextRefreshUtc;
        }
    }
}
