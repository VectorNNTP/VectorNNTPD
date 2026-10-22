using System.Net;
using System.Net.Sockets;
using VectorNNTPD.Common.Networking.Listeners;

namespace VectorNNTPD.BackFillerService.Listener
{
    /// <summary>
    /// Binds listen sockets from Common <see cref="ListenBinding"/> plans.
    /// </summary>
    /// <remarks>
    /// Each socket is a TCP stream socket. Nagle and buffer sizes are not set here: accepted sockets do not
    /// inherit those options, and <see cref="CacheListenerService"/> applies them to each accepted socket
    /// before TLS. <see cref="Socket.DualMode"/> is assigned only for IPv6 sockets. The listen backlog passed to
    /// <see cref="Socket.Listen(int)"/> is 512. Bind or listen failure disposes the socket and rethrows.
    /// </remarks>
    internal static class CacheListenerEndpoints
    {
        /// <summary>Creates, binds, and listens on <paramref name="binding"/>.</summary>
        /// <param name="binding">Address, port, and dual-mode flag from <see cref="ListenEndpointPlanner"/>.</param>
        /// <returns>A listening socket. The caller owns it.</returns>
        internal static Socket CreateBoundListenSocket(ListenBinding binding)
        {
            return CreateBoundListenSocket(binding.EndPoint, binding.DualMode);
        }

        /// <summary>Creates, binds, and listens on <paramref name="endpoint"/>.</summary>
        /// <param name="endpoint">Local TCP endpoint. IPv6 endpoints may also set dual mode.</param>
        /// <param name="dualMode">
        /// When <see langword="true"/> and <paramref name="endpoint"/> is IPv6, the socket accepts IPv4-mapped connections.
        /// Ignored for IPv4.
        /// </param>
        /// <returns>A listening socket. The caller owns it.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="endpoint"/> is null.</exception>
        /// <remarks>Any exception from bind or listen disposes the new socket and is rethrown.</remarks>
        private static Socket CreateBoundListenSocket(IPEndPoint endpoint, bool dualMode = false)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = dualMode;
            }

            Socket? pendingDispose = socket;
            try
            {
                socket.Bind(endpoint);
                socket.Listen(512);
                pendingDispose = null;
                return socket;
            }
            finally
            {
                pendingDispose?.Dispose();
            }
        }
    }
}
