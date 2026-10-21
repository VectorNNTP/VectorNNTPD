using System.Net.Sockets;

namespace VectorNNTPD.BackFillerService.Networking
{
    /// <summary>
    /// Applies <see cref="Socket.NoDelay"/>, TCP send and receive buffer limits, and TCP keepalive
    /// to one connected or not-yet-connected socket.
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
    /// <para>
    /// TCP keepalive is enabled independently of application-level NNTP <c>DATE</c> probes.
    /// <see cref="SocketOptionName.KeepAlive"/> must succeed. Probe timings use
    /// <see cref="SocketOptionName.TcpKeepAliveTime"/>, <see cref="SocketOptionName.TcpKeepAliveInterval"/>,
    /// and <see cref="SocketOptionName.TcpKeepAliveRetryCount"/> when the platform supports them;
    /// an unsupported probe option leaves OS defaults in place and does not undo keepalive.
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
        /// Sets <see cref="Socket.NoDelay"/>, the send and receive buffers, and TCP keepalive.
        /// </summary>
        /// <param name="socket">Connected or not-yet-connected TCP socket that will carry NNTP bytes.</param>
        /// <param name="sendBufferBytes">Requested <see cref="Socket.SendBufferSize"/>. Must already be inside the validated range.</param>
        /// <param name="receiveBufferBytes">Requested <see cref="Socket.ReceiveBufferSize"/>. Must already be inside the validated range.</param>
        /// <param name="keepAlive">Idle, interval, and retry settings. Validated before any option is written.</param>
        /// <exception cref="ArgumentNullException"><paramref name="socket"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// A buffer size is outside <see cref="MinimumBufferBytes"/> through <see cref="MaximumBufferBytes"/>,
        /// or <paramref name="keepAlive"/> fails <see cref="TcpKeepAliveSettings.Validate"/>.
        /// </exception>
        /// <exception cref="SocketException">The operating system rejected a required option.</exception>
        /// <exception cref="ObjectDisposedException"><paramref name="socket"/> is disposed.</exception>
        /// <remarks>
        /// Call this before the first application or TLS write on <paramref name="socket"/>.
        /// For an outbound socket, also call it before connect when that socket object is the one
        /// that will connect, so the handshake can advertise the receive window. A later call on the
        /// socket that connect actually used covers a runtime that replaced the socket during name resolution.
        /// </remarks>
        internal static void Apply(
            Socket socket,
            int sendBufferBytes,
            int receiveBufferBytes,
            TcpKeepAliveSettings keepAlive)
        {
            ArgumentNullException.ThrowIfNull(socket);
            ArgumentOutOfRangeException.ThrowIfLessThan(sendBufferBytes, MinimumBufferBytes);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(sendBufferBytes, MaximumBufferBytes);
            ArgumentOutOfRangeException.ThrowIfLessThan(receiveBufferBytes, MinimumBufferBytes);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(receiveBufferBytes, MaximumBufferBytes);
            keepAlive.Validate();

            socket.NoDelay = true;
            socket.SendBufferSize = sendBufferBytes;
            socket.ReceiveBufferSize = receiveBufferBytes;
            ApplyKeepAlive(socket, keepAlive);
        }

        /// <summary>
        /// Returns whether <see cref="SocketOptionName.KeepAlive"/> is enabled on <paramref name="socket"/>.
        /// </summary>
        /// <param name="socket">Socket to inspect.</param>
        /// <returns><see langword="true"/> when the option is non-zero.</returns>
        internal static bool IsKeepAliveEnabled(Socket socket)
        {
            ArgumentNullException.ThrowIfNull(socket);
            var value = socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive);
            return value switch
            {
                bool enabled => enabled,
                int flag => flag != 0,
                _ => Convert.ToBoolean(value),
            };
        }

        /// <summary>
        /// Reads a TCP keepalive probe option when the platform exposes it.
        /// </summary>
        /// <param name="socket">Configured socket.</param>
        /// <param name="name">
        /// One of <see cref="SocketOptionName.TcpKeepAliveTime"/>,
        /// <see cref="SocketOptionName.TcpKeepAliveInterval"/>, or
        /// <see cref="SocketOptionName.TcpKeepAliveRetryCount"/>.
        /// </param>
        /// <param name="value">Option value in seconds or retry count when the read succeeds.</param>
        /// <returns>
        /// <see langword="true"/> when the option was read; <see langword="false"/> when the platform
        /// does not support reading that option (distinct from a failed required keepalive enable).
        /// </returns>
        /// <exception cref="SocketException">
        /// Thrown for unexpected socket errors that are not classified as unsupported options.
        /// </exception>
        internal static bool TryGetTcpKeepAliveProbeOption(Socket socket, SocketOptionName name, out int value)
        {
            ArgumentNullException.ThrowIfNull(socket);
            try
            {
                var raw = socket.GetSocketOption(SocketOptionLevel.Tcp, name);
                value = Convert.ToInt32(raw);
                return true;
            }
            catch (SocketException ex) when (IsUnsupportedKeepAliveProbeError(ex))
            {
                value = 0;
                return false;
            }
        }

        /// <summary>Enables <see cref="SocketOptionName.KeepAlive"/> and best-effort probe timings.</summary>
        /// <param name="socket">Socket that already passed buffer and range checks.</param>
        /// <param name="keepAlive">Validated probe timings.</param>
        private static void ApplyKeepAlive(Socket socket, TcpKeepAliveSettings keepAlive)
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            TrySetTcpKeepAliveProbeOption(socket, SocketOptionName.TcpKeepAliveTime, keepAlive.TimeSeconds);
            TrySetTcpKeepAliveProbeOption(socket, SocketOptionName.TcpKeepAliveInterval, keepAlive.IntervalSeconds);
            TrySetTcpKeepAliveProbeOption(socket, SocketOptionName.TcpKeepAliveRetryCount, keepAlive.RetryCount);
        }

        /// <summary>
        /// Sets one TCP keepalive probe option, treating platform non-support as a skipped option.
        /// </summary>
        /// <param name="socket">Socket with keepalive already enabled.</param>
        /// <param name="name">Probe option name.</param>
        /// <param name="value">Seconds or retry count.</param>
        /// <exception cref="SocketException">Thrown when the failure is not classified as unsupported.</exception>
        private static void TrySetTcpKeepAliveProbeOption(Socket socket, SocketOptionName name, int value)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Tcp, name, value);
            }
            catch (SocketException ex) when (IsUnsupportedKeepAliveProbeError(ex))
            {
                // SO_KEEPALIVE remains enabled; the stack keeps its probe defaults for this option.
            }
        }

        /// <summary>
        /// True when <paramref name="ex"/> indicates the TCP keepalive probe option is unavailable
        /// on this platform, not that the socket is broken or the value is out of OS range after validation.
        /// </summary>
        /// <param name="ex">Socket failure from set or get of a probe option.</param>
        private static bool IsUnsupportedKeepAliveProbeError(SocketException ex) =>
            ex.SocketErrorCode is SocketError.ProtocolOption
                or SocketError.OperationNotSupported
                or SocketError.InvalidArgument;
    }
}
