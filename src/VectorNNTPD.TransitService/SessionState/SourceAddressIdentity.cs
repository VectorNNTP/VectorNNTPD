using System.Net;

namespace VectorNNTPD.TransitService.SessionState
{
    /// <summary>
    /// Canonical source-IP identity used by session and cluster source-IP admission.
    /// </summary>
    /// <remarks>
    /// IPv4-mapped IPv6 addresses are converted to IPv4 so <c>::ffff:192.0.2.10</c>
    /// and <c>192.0.2.10</c> are one identity. Other IPv4 and IPv6 addresses are
    /// independent; equivalent textual forms of the same address normalize through
    /// <see cref="IPAddress.ToString"/>.
    /// </remarks>
    internal static class SourceAddressIdentity
    {
        /// <summary>Returns the canonical textual identity for <paramref name="address"/>.</summary>
        public static string Format(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            return Normalize(address).ToString();
        }

        /// <summary>
        /// Network-order address bytes after the same IPv4-mapped normalization as
        /// <see cref="Format"/>. IPv4 is 4 bytes; IPv6 is 16 bytes.
        /// </summary>
        public static byte[] ToNetworkBytes(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            return Normalize(address).GetAddressBytes();
        }

        private static IPAddress Normalize(IPAddress address) =>
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
