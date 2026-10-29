using System.Net;
using System.Net.Sockets;
using VectorNNTPD.TransitService.Networking.Proxy;

namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// An IPv4 or IPv6 network prefix used by Transit <c>AllowFrom</c> matching.
    /// </summary>
    /// <remarks>
    /// Does not apply routability tests such as <c>IPAddress.IsGlobal</c>. Private, ULA,
    /// documentation, and lab addresses are valid.
    /// </remarks>
    public readonly struct IpPrefix : IEquatable<IpPrefix>
    {
        /// <summary>Initializes a prefix from a network address and length.</summary>
        public IpPrefix(IPAddress network, int prefixLength)
        {
            ArgumentNullException.ThrowIfNull(network);
            network = TrustedProxyHosts.Canonicalize(network);
            var max = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (prefixLength < 0 || prefixLength > max)
            {
                throw new ArgumentOutOfRangeException(nameof(prefixLength));
            }

            Network = Mask(network, prefixLength);
            PrefixLength = prefixLength;
            AddressFamily = Network.AddressFamily;
        }

        /// <summary>Gets the masked network address.</summary>
        public IPAddress Network { get; }

        /// <summary>Gets the prefix length in bits.</summary>
        public int PrefixLength { get; }

        /// <summary>Gets the address family of this prefix.</summary>
        public AddressFamily AddressFamily { get; }

        /// <summary>Creates a host prefix (<c>/32</c> or <c>/128</c>) for <paramref name="address"/>.</summary>
        public static IpPrefix Host(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            address = TrustedProxyHosts.Canonicalize(address);
            var length = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            return new IpPrefix(address, length);
        }

        /// <summary>
        /// Parses a literal IPv4/IPv6 address or CIDR prefix.
        /// </summary>
        public static bool TryParse(string text, out IpPrefix prefix)
        {
            prefix = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text.Trim();
            var slash = trimmed.LastIndexOf('/');
            if (slash < 0)
            {
                if (!IPAddress.TryParse(trimmed, out var address))
                {
                    return false;
                }

                prefix = Host(address);
                return true;
            }

            if (slash == 0 || slash == trimmed.Length - 1)
            {
                return false;
            }

            if (!IPAddress.TryParse(trimmed[..slash], out var network))
            {
                return false;
            }

            if (!int.TryParse(trimmed[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length))
            {
                return false;
            }

            var max = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (network.AddressFamily == AddressFamily.InterNetworkV6 && network.IsIPv4MappedToIPv6)
            {
                max = 32;
            }

            if (length < 0 || length > max)
            {
                return false;
            }

            prefix = new IpPrefix(network, length);
            return true;
        }

        /// <summary>Returns whether <paramref name="address"/> is contained by this prefix.</summary>
        public bool Contains(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            address = TrustedProxyHosts.Canonicalize(address);
            if (address.AddressFamily != AddressFamily)
            {
                return false;
            }

            return PrefixBitsEqual(Network, address, PrefixLength);
        }

        /// <summary>
        /// Returns whether this prefix and <paramref name="other"/> share any address.
        /// </summary>
        public bool Overlaps(IpPrefix other)
        {
            if (AddressFamily != other.AddressFamily)
            {
                return false;
            }

            var length = Math.Min(PrefixLength, other.PrefixLength);
            return PrefixBitsEqual(Network, other.Network, length);
        }

        /// <inheritdoc />
        public bool Equals(IpPrefix other) =>
            AddressFamily == other.AddressFamily
            && PrefixLength == other.PrefixLength
            && Network.Equals(other.Network);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is IpPrefix other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(AddressFamily, PrefixLength, Network);

        /// <inheritdoc />
        public override string ToString() =>
            $"{Network}/{PrefixLength}";

        private static IPAddress Mask(IPAddress address, int prefixLength)
        {
            var bytes = address.GetAddressBytes();
            ApplyPrefixMask(bytes, prefixLength);
            return new IPAddress(bytes);
        }

        private static bool PrefixBitsEqual(IPAddress left, IPAddress right, int prefixLength)
        {
            if (prefixLength == 0)
            {
                return true;
            }

            var leftBytes = left.GetAddressBytes();
            var rightBytes = right.GetAddressBytes();
            if (leftBytes.Length != rightBytes.Length)
            {
                return false;
            }

            var fullBytes = prefixLength / 8;
            for (var i = 0; i < fullBytes; i++)
            {
                if (leftBytes[i] != rightBytes[i])
                {
                    return false;
                }
            }

            var remaining = prefixLength % 8;
            if (remaining == 0)
            {
                return true;
            }

            var mask = (byte)(0xFF << (8 - remaining));
            return (leftBytes[fullBytes] & mask) == (rightBytes[fullBytes] & mask);
        }

        private static void ApplyPrefixMask(byte[] bytes, int prefixLength)
        {
            var fullBytes = prefixLength / 8;
            var remaining = prefixLength % 8;
            if (remaining != 0)
            {
                bytes[fullBytes] &= (byte)(0xFF << (8 - remaining));
                fullBytes++;
            }

            for (var i = fullBytes; i < bytes.Length; i++)
            {
                bytes[i] = 0;
            }
        }
    }
}
