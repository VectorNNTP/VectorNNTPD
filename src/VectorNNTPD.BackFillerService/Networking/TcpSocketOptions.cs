using System.Net.Sockets;

namespace VectorNNTPD.BackFillerService.Networking
{
    /// <summary>
    /// Applies <see cref="Socket.NoDelay"/> and the TCP send and receive buffer limits to one connected socket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nagle is disabled because both NNTP roles mix small writes with large article payloads.
    /// The upstream client sends short commands such as <c>ARTICLE</c>, <c>DATE</c>, and <c>AUTHINFO</c>.
    /// The cache listener sends a short status line and a short <c>.\r\n</c> around a large article image.
    /// With Nagle left on, a small write can wait for a delayed ACK. Disabling Nagle does not by itself
    /// produce a target throughput.
    /// </para>
    /// <para>
    /// Send and receive sizes are independent kernel buffer limits. They are not the effective TCP
    /// congestion window, and an operating system may round, double, or clamp them. Linux can disable
    /// receive autotuning once <see cref="Socket.ReceiveBufferSize"/> is set. The managed
    /// <c>NntpStreamReader</c> buffer is a separate allocation and is not taken from these sizes.
    /// </para>
    /// </remarks>
    internal static class TcpSocketOptions
    {
        /// <summary>Smallest accepted send or receive buffer, in bytes. Sixteen KiB.</summary>
        internal const int MinimumBufferBytes = 16 * 1024;

        /// <summary>
        /// Largest accepted send or receive buffer, in bytes. Sixteen MiB.
        /// </summary>
        /// <remarks>
        /// Sixteen MiB covers about 13 ms of 10 Gbps. The validator rejects larger values so a
        /// configuration mistake cannot request a multi-gigabyte kernel buffer on every session.
        /// </remarks>
        internal const int MaximumBufferBytes = 16 * 1024 * 1024;

        /// <summary>
        /// Sets <see cref="Socket.NoDelay"/>, then the send buffer, then the receive buffer.
        /// </summary>
        /// <param name="socket">Connected or not-yet-connected TCP socket that will carry NNTP bytes.</param>
        /// <param name="sendBufferBytes">Requested <see cref="Socket.SendBufferSize"/>. Must already be inside the validated range.</param>
        /// <param name="receiveBufferBytes">Requested <see cref="Socket.ReceiveBufferSize"/>. Must already be inside the validated range.</param>
        /// <exception cref="ArgumentNullException"><paramref name="socket"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Either size is outside <see cref="MinimumBufferBytes"/> through <see cref="MaximumBufferBytes"/>.</exception>
        /// <exception cref="SocketException">The operating system rejected one of the options.</exception>
        /// <exception cref="ObjectDisposedException"><paramref name="socket"/> is disposed.</exception>
        /// <remarks>
        /// Call this before the first application or TLS write on <paramref name="socket"/>.
        /// For an outbound socket, also call it before connect when that socket object is the one
        /// that will connect, so the handshake can advertise the receive window. A later call on the
        /// socket that connect actually used covers a runtime that replaced the socket during name resolution.
        /// </remarks>
        internal static void Apply(Socket socket, int sendBufferBytes, int receiveBufferBytes)
        {
            ArgumentNullException.ThrowIfNull(socket);
            ArgumentOutOfRangeException.ThrowIfLessThan(sendBufferBytes, MinimumBufferBytes);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(sendBufferBytes, MaximumBufferBytes);
            ArgumentOutOfRangeException.ThrowIfLessThan(receiveBufferBytes, MinimumBufferBytes);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(receiveBufferBytes, MaximumBufferBytes);

            socket.NoDelay = true;
            socket.SendBufferSize = sendBufferBytes;
            socket.ReceiveBufferSize = receiveBufferBytes;
        }
    }
}
