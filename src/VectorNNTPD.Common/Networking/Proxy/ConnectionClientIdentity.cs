using System.Net;

namespace VectorNNTPD.Common.Networking.Proxy
{
    /// <summary>
    /// Immutable connection-level client identity established once at accept time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TcpPeer"/> is always the accepted socket's remote endpoint.
    /// <see cref="Client"/> is the effective client endpoint exposed to the session layer
    /// (equal to <see cref="TcpPeer"/> for direct and untrusted peers; the PROXY-reported
    /// source for trusted HAProxy peers that supplied a proxied address).
    /// </para>
    /// <para>
    /// Trust and PROXY parsing are completed before this value is published; the session layer
    /// must not reparse PROXY headers or inspect the socket to obtain the client endpoint.
    /// </para>
    /// </remarks>
    public sealed class ConnectionClientIdentity
    {
        private ConnectionClientIdentity(
            IPEndPoint tcpPeer,
            IPEndPoint client,
            bool isTrustedProxy,
            bool usedProxyHeader,
            int? proxyProtocolVersion)
        {
            TcpPeer = tcpPeer;
            Client = client;
            IsTrustedProxy = isTrustedProxy;
            UsedProxyHeader = usedProxyHeader;
            ProxyProtocolVersion = proxyProtocolVersion;
        }

        /// <summary>Gets the actual TCP peer endpoint of the accepted socket.</summary>
        public IPEndPoint TcpPeer { get; }

        /// <summary>Gets the effective client endpoint for the session layer.</summary>
        public IPEndPoint Client { get; }

        /// <summary>Gets the effective client IP address.</summary>
        public IPAddress ClientAddress => Client.Address;

        /// <summary>Gets the effective client TCP source port.</summary>
        public int ClientPort => Client.Port;

        /// <summary>Gets whether the TCP peer matched a configured trusted PROXY host.</summary>
        public bool IsTrustedProxy { get; }

        /// <summary>Gets whether a PROXY protocol header was consumed for this connection.</summary>
        public bool UsedProxyHeader { get; }

        /// <summary>Gets the PROXY protocol version when a header was consumed; otherwise <see langword="null"/>.</summary>
        public int? ProxyProtocolVersion { get; }

        /// <summary>Creates a direct (non-proxy) identity where client equals TCP peer.</summary>
        /// <remarks>
        /// Publishes independent <see cref="IPEndPoint"/> instances so later mutation of the
        /// caller's endpoint (or of one published property) cannot alias into the other.
        /// </remarks>
        public static ConnectionClientIdentity Direct(IPEndPoint tcpPeer)
        {
            ArgumentNullException.ThrowIfNull(tcpPeer);
            var peer = CloneEndpoint(tcpPeer);
            var client = CloneEndpoint(tcpPeer);
            return new ConnectionClientIdentity(
                peer,
                client,
                isTrustedProxy: false,
                usedProxyHeader: false,
                proxyProtocolVersion: null);
        }

        /// <summary>Creates an identity for a trusted proxy peer with an optional PROXY-reported client.</summary>
        public static ConnectionClientIdentity FromTrustedProxy(
            IPEndPoint tcpPeer,
            IPEndPoint client,
            int proxyProtocolVersion)
        {
            ArgumentNullException.ThrowIfNull(tcpPeer);
            ArgumentNullException.ThrowIfNull(client);
            return new ConnectionClientIdentity(
                CloneEndpoint(tcpPeer),
                CloneEndpoint(client),
                isTrustedProxy: true,
                usedProxyHeader: true,
                proxyProtocolVersion);
        }

        private static IPEndPoint CloneEndpoint(IPEndPoint endpoint) =>
            new(endpoint.Address, endpoint.Port);
    }
}
