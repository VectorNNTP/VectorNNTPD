using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.Networking.Proxy
{
    /// <summary>
    /// Parses and caches <see cref="NntpdOptions.ProxyHosts"/> once for hot-path peer matching.
    /// </summary>
    public sealed class TrustedProxyHosts : ITrustedProxyHosts
    {
        private readonly HashSet<IPAddress> _hosts;

        /// <summary>Initializes a new instance of the <see cref="TrustedProxyHosts"/> class.</summary>
        public TrustedProxyHosts(IOptions<NntpdOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _hosts = BuildSet(options.Value.ProxyHosts);
        }

        /// <summary>Creates an instance from an already-validated address list (tests).</summary>
        internal TrustedProxyHosts(IEnumerable<IPAddress> hosts)
        {
            ArgumentNullException.ThrowIfNull(hosts);
            _hosts = new HashSet<IPAddress>(hosts.Select(Canonicalize), IPAddressEqualityComparer.Instance);
        }

        /// <inheritdoc />
        public bool IsEnabled => _hosts.Count > 0;

        /// <inheritdoc />
        public bool IsTrusted(IPAddress peerAddress)
        {
            ArgumentNullException.ThrowIfNull(peerAddress);
            if (_hosts.Count == 0)
            {
                return false;
            }

            return _hosts.Contains(Canonicalize(peerAddress));
        }

        /// <summary>Normalizes an address for PROXY peer trust comparison.</summary>
        public static IPAddress Canonicalize(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            return address is { AddressFamily: AddressFamily.InterNetworkV6, IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
        }

        private static HashSet<IPAddress> BuildSet(string[]? entries)
        {
            var set = new HashSet<IPAddress>(IPAddressEqualityComparer.Instance);
            if (entries is null)
            {
                return set;
            }

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                if (!IPAddress.TryParse(entry.Trim(), out var address))
                {
                    continue;
                }

                set.Add(Canonicalize(address));
            }

            return set;
        }

        private sealed class IPAddressEqualityComparer : IEqualityComparer<IPAddress>
        {
            public static readonly IPAddressEqualityComparer Instance = new();

            public bool Equals(IPAddress? x, IPAddress? y)
            {
                if (ReferenceEquals(x, y))
                {
                    return true;
                }

                if (x is null || y is null)
                {
                    return false;
                }

                return x.Equals(y);
            }

            public int GetHashCode(IPAddress obj) => obj.GetHashCode();
        }
    }
}
