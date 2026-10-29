using System.Net;
using System.Net.Sockets;

namespace VectorNNTPD.ReaderService.Networking.Proxy
{
    /// <summary>Result of resolving connection identity and optional PROXY leftover octets.</summary>
    public readonly struct ProxyPreambleResolution
    {
        /// <summary>Initializes a new instance of the <see cref="ProxyPreambleResolution"/> struct.</summary>
        public ProxyPreambleResolution(ConnectionClientIdentity identity, ReadOnlyMemory<byte> leftover)
        {
            Identity = identity;
            Leftover = leftover;
        }

        /// <summary>Gets the established client identity.</summary>
        public ConnectionClientIdentity Identity { get; }

        /// <summary>
        /// Gets octets that followed a consumed PROXY header and must be processed as application/TLS data.
        /// </summary>
        public ReadOnlyMemory<byte> Leftover { get; }
    }

    /// <summary>
    /// Resolves effective client identity from the TCP peer and, when trusted, a PROXY preamble.
    /// </summary>
    public static class ProxyPreambleResolver
    {
        /// <summary>
        /// Establishes connection identity for an accepted socket according to trusted PROXY hosts.
        /// </summary>
        /// <remarks>
        /// <list type="bullet">
        /// <item><description>Empty trusted set: PROXY disabled; client = TCP peer; no socket reads.</description></item>
        /// <item><description>Untrusted peer: client = TCP peer; PROXY bytes are not interpreted.</description></item>
        /// <item><description>Trusted peer: PROXY v1/v2 required; malformed/timeout fails the connection.</description></item>
        /// </list>
        /// </remarks>
        public static async ValueTask<ProxyPreambleResolution> ResolveAsync(
            Socket socket,
            IPEndPoint tcpPeer,
            ITrustedProxyHosts trustedProxyHosts,
            CancellationToken cancellationToken,
            TimeSpan? preambleTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(socket);
            ArgumentNullException.ThrowIfNull(tcpPeer);
            ArgumentNullException.ThrowIfNull(trustedProxyHosts);

            if (!trustedProxyHosts.IsEnabled || !trustedProxyHosts.IsTrusted(tcpPeer.Address))
            {
                return new ProxyPreambleResolution(ConnectionClientIdentity.Direct(tcpPeer), ReadOnlyMemory<byte>.Empty);
            }

            var timeout = preambleTimeout ?? ProxyProtocolParser.DefaultPreambleTimeout;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                return await ReadRequiredPreambleAsync(socket, tcpPeer, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProxyProtocolTimeoutException("PROXY preamble timed out");
            }
        }

        private static async ValueTask<ProxyPreambleResolution> ReadRequiredPreambleAsync(
            Socket socket,
            IPEndPoint tcpPeer,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[Math.Min(4096, ProxyProtocolParser.MaxV2HeaderOctets)];
            var gathered = new byte[ProxyProtocolParser.MaxV2HeaderOctets];
            var gatheredLength = 0;

            while (true)
            {
                var status = ProxyProtocolParser.TryParse(
                    gathered.AsSpan(0, gatheredLength),
                    tcpPeer,
                    out var parsed,
                    out var error);

                if (status == ProxyParseStatus.Ok)
                {
                    var leftoverLength = gatheredLength - parsed.HeaderLength;
                    ReadOnlyMemory<byte> leftover = leftoverLength > 0
                        ? gathered.AsMemory(parsed.HeaderLength, leftoverLength).ToArray()
                        : ReadOnlyMemory<byte>.Empty;
                    return new ProxyPreambleResolution(parsed.Identity, leftover);
                }

                if (status == ProxyParseStatus.Error)
                {
                    throw new ProxyProtocolMalformedException(error);
                }

                if (gatheredLength >= ProxyProtocolParser.MaxV2HeaderOctets)
                {
                    throw new ProxyProtocolMalformedException("PROXY preamble exceeds maximum size");
                }

                var room = Math.Min(buffer.Length, ProxyProtocolParser.MaxV2HeaderOctets - gatheredLength);
                var read = await socket
                    .ReceiveAsync(buffer.AsMemory(0, room), SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new ProxyProtocolMalformedException(
                        gatheredLength == 0 ? "PROXY header required" : "EOF during PROXY preamble");
                }

                buffer.AsSpan(0, read).CopyTo(gathered.AsSpan(gatheredLength));
                gatheredLength += read;
            }
        }

        /// <summary>Extracts an <see cref="IPEndPoint"/> from a socket remote endpoint.</summary>
        public static bool TryGetTcpPeer(Socket socket, out IPEndPoint tcpPeer)
        {
            ArgumentNullException.ThrowIfNull(socket);
            try
            {
                if (socket.RemoteEndPoint is IPEndPoint ep)
                {
                    tcpPeer = ep;
                    return true;
                }
            }
            catch (SocketException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
            catch (ObjectDisposedException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }

            tcpPeer = null!;
            return false;
        }
    }
}
