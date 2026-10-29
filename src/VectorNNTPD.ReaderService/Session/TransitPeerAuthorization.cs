using System.Net;
using Microsoft.Extensions.DependencyInjection;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.Session
{
    /// <summary>
    /// Resolves connection-time transit/streaming peer identity from the active Transit snapshot.
    /// </summary>
    public interface ITransitPeerAuthorization
    {
        /// <summary>Gets whether any Transit peers are configured in the current snapshot.</summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Returns the initial <see cref="NntpAuthorization"/> for <paramref name="effectiveClientAddress"/>.
        /// </summary>
        /// <remarks>
        /// Matching uses the precomputed IP-only ACL (literal prefixes plus already-resolved
        /// AllowFrom addresses). This method never performs DNS, reverse DNS, or network I/O.
        /// A unique match retains the named peer policy and grants streaming.
        /// Feed commands are granted immediately only when that peer has no receive credentials.
        /// When both username and password are set, feed commands wait for AUTHINFO.
        /// Ambiguous multi-peer matches grant nothing.
        /// Other addresses receive <see cref="NntpAuthorization.Unauthenticated"/>.
        /// </remarks>
        NntpAuthorization Resolve(IPAddress effectiveClientAddress);
    }

    /// <summary>
    /// Snapshot-backed implementation of <see cref="ITransitPeerAuthorization"/>.
    /// </summary>
    public sealed class TransitPeerAuthorization : ITransitPeerAuthorization
    {
        private readonly TransitConfigurationStore _store;
        private readonly ITransitDnsAddressCache _dns;
        private readonly ILogger<TransitPeerAuthorization>? _logger;

        /// <summary>Disabled shared instance (empty snapshot).</summary>
        public static TransitPeerAuthorization Disabled { get; } = CreateStatic(TransitConfigurationSnapshot.Empty);

        /// <summary>Creates an instance from the live snapshot store (DI).</summary>
        [ActivatorUtilitiesConstructor]
        public TransitPeerAuthorization(
            TransitConfigurationStore store,
            ITransitDnsAddressCache dns,
            TransitConfigurationHotReload hotReload,
            ILogger<TransitPeerAuthorization> logger)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(dns);
            ArgumentNullException.ThrowIfNull(hotReload);
            ArgumentNullException.ThrowIfNull(logger);
            _store = store;
            _dns = dns;
            _logger = logger;
            if (!store.Current.IsEmpty)
            {
                SessionLogMessages.TransitPeersConfigured(
                    logger,
                    store.Current.Peers.Count,
                    string.Join(", ", store.Current.Peers.Values.Select(static p => p.Identifier + " (" + p.PeerName + ")")));
            }
        }

        private TransitPeerAuthorization(
            TransitConfigurationStore store,
            ITransitDnsAddressCache dns,
            ILogger<TransitPeerAuthorization>? logger = null)
        {
            _store = store;
            _dns = dns;
            _logger = logger;
        }

        /// <summary>Creates an instance bound to an existing snapshot store (tests).</summary>
        public static TransitPeerAuthorization CreateForStore(
            TransitConfigurationStore store,
            ITransitDnsAddressCache? dns = null,
            ILogger<TransitPeerAuthorization>? logger = null)
        {
            ArgumentNullException.ThrowIfNull(store);
            return new TransitPeerAuthorization(store, dns ?? new EmptyDnsCache(), logger);
        }

        /// <summary>Creates a static instance from a complete snapshot (tests).</summary>
        public static TransitPeerAuthorization CreateStatic(
            TransitConfigurationSnapshot snapshot,
            ITransitDnsAddressCache? dns = null,
            ILogger<TransitPeerAuthorization>? logger = null)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var store = new TransitConfigurationStore();
            store.Replace(snapshot);
            return new TransitPeerAuthorization(store, dns ?? new EmptyDnsCache(), logger);
        }

        /// <inheritdoc />
        public bool IsEnabled => !_store.Current.IsEmpty;

        /// <inheritdoc />
        public NntpAuthorization Resolve(IPAddress effectiveClientAddress)
        {
            ArgumentNullException.ThrowIfNull(effectiveClientAddress);
            var address = TrustedProxyHosts.Canonicalize(effectiveClientAddress);
            var snapshot = _store.Current;
            if (snapshot.IsEmpty)
            {
                return NntpAuthorization.Unauthenticated;
            }

            List<string>? matches = null;
            TransitPeerPolicy? matched = null;
            foreach (var peer in snapshot.Peers.Values)
            {
                if (!MatchesIpAcl(peer, address))
                {
                    continue;
                }

                matches ??= [];
                matches.Add(peer.Identifier + " (" + peer.PeerName + ")");
                matched = peer;
            }

            if (matches is { Count: > 1 })
            {
                if (_logger is not null)
                {
                    SessionLogMessages.TransitPeerAmbiguous(_logger, address, string.Join(", ", matches));
                }
                return NntpAuthorization.Unauthenticated;
            }

            return matched is null
                ? NntpAuthorization.Unauthenticated
                : NntpAuthorization.ForTransitPeer(matched);
        }

        /// <summary>
        /// Matches <paramref name="address"/> against the peer's already-materialized IP ACL.
        /// Does not call <see cref="ITransitDnsResolver"/>.
        /// </summary>
        private bool MatchesIpAcl(TransitPeerPolicy peer, IPAddress address)
        {
            foreach (var prefix in peer.LiteralPrefixes)
            {
                if (prefix.Contains(address))
                {
                    return true;
                }
            }

            foreach (var hostname in peer.DnsHostnames)
            {
                if (_dns.GetResolved(hostname).Contains(address))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class EmptyDnsCache : ITransitDnsAddressCache
        {
            public IReadOnlySet<IPAddress> GetResolved(string hostname) => new HashSet<IPAddress>();

            public Task RefreshAllAsync(TransitConfigurationSnapshot snapshot, CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task RefreshDueAsync(TransitConfigurationSnapshot snapshot, CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public TimeSpan GetDelayUntilNextRefresh(TransitConfigurationSnapshot snapshot) => Timeout.InfiniteTimeSpan;
        }
    }
}
