using System.Net;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Networking.Proxy;

namespace VectorNNTPD.Common.Networking.Listeners
{
    /// <summary>Formats connection-acceptance Information logs (TCP peer vs PROXY effective client).</summary>
    internal static class ConnectionAcceptanceLogging
    {
        /// <summary>Logs a successful plain (or plain+PROXY) connection acceptance.</summary>
        public static void LogPlainAccepted(ILogger logger, ConnectionClientIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(identity);

            if (identity.UsedProxyHeader)
            {
                NetworkingLogMessages.ProxyConnectionAccepted(
                    logger,
                    FormatEndpoint(identity.TcpPeer),
                    FormatEndpoint(identity.Client));
            }
            else
            {
                NetworkingLogMessages.PlainConnectionAccepted(
                    logger,
                    FormatEndpoint(identity.TcpPeer));
            }
        }

        /// <summary>Logs a successful TLS (or TLS+PROXY) connection acceptance after handshake.</summary>
        public static void LogTlsAccepted(
            ILogger logger,
            ConnectionClientIdentity identity,
            string tlsVersion,
            string cipher)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentException.ThrowIfNullOrWhiteSpace(tlsVersion);
            ArgumentException.ThrowIfNullOrWhiteSpace(cipher);

            if (identity.UsedProxyHeader)
            {
                NetworkingLogMessages.TlsProxyConnectionAccepted(
                    logger,
                    FormatEndpoint(identity.TcpPeer),
                    FormatEndpoint(identity.Client),
                    tlsVersion,
                    cipher);
            }
            else
            {
                NetworkingLogMessages.TlsConnectionAccepted(
                    logger,
                    FormatEndpoint(identity.TcpPeer),
                    tlsVersion,
                    cipher);
            }
        }

        /// <summary>Formats an endpoint as <c>ip:port</c> for connection acceptance logs.</summary>
        public static string FormatEndpoint(IPEndPoint endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            return $"{endpoint.Address}:{endpoint.Port}";
        }

        /// <summary>Formats a TCP endpoint for accept/disconnect logs, or <c>unknown</c>.</summary>
        public static string FormatEndpoint(EndPoint? endpoint) =>
            endpoint switch
            {
                IPEndPoint ip => FormatEndpoint(ip),
                not null => endpoint.ToString() ?? "unknown",
                _ => "unknown",
            };

        /// <summary>Logs the single Information disconnect summary for an established TCP connection.</summary>
        public static void LogDisconnected(
            ILogger logger,
            EndPoint? remote,
            EndPoint? local,
            string reason)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            NetworkingLogMessages.TcpConnectionDisconnected(
                logger,
                FormatEndpoint(remote),
                FormatEndpoint(local),
                reason);
        }
    }
}
