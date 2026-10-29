using System.Net;

namespace VectorNNTPD.ReaderService.Networking.Proxy
{
    /// <summary>
    /// Canonical set of trusted HAProxy PROXY-protocol peer addresses from configuration.
    /// </summary>
    public interface ITrustedProxyHosts
    {
        /// <summary>Gets whether any trusted PROXY peers are configured.</summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Returns whether <paramref name="peerAddress"/> matches a configured trusted proxy host.
        /// </summary>
        /// <remarks>
        /// Comparison uses canonical forms: IPv4-mapped IPv6 addresses match their IPv4 equivalents.
        /// </remarks>
        bool IsTrusted(IPAddress peerAddress);
    }
}
