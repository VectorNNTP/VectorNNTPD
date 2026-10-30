using System.Net;
using System.Net.Sockets;

namespace VectorNNTPD.Common.Networking.Listeners
{
    /// <summary>
    /// Binds and listens on an already-created TCP socket.
    /// </summary>
    /// <remarks>
    /// This is the startup ownership seam for a listening endpoint. A successful
    /// <see cref="BindAndListen"/> means the socket accepted bind/listen under the
    /// application's socket options. Accept-loop failures after that point are not
    /// startup failures.
    /// </remarks>
    public interface IListenSocketBinder
    {
        /// <summary>
        /// Binds <paramref name="socket"/> to <paramref name="endpoint"/> and starts listening.
        /// </summary>
        /// <param name="socket">The listen socket created for <paramref name="endpoint"/>.</param>
        /// <param name="endpoint">The local endpoint that must be owned after this call.</param>
        /// <param name="backlog">The listen backlog.</param>
        void BindAndListen(Socket socket, IPEndPoint endpoint, int backlog);
    }

    /// <summary>Production <see cref="Socket.Bind(EndPoint)"/> / <see cref="Socket.Listen(int)"/> implementation.</summary>
    internal sealed class SocketListenBinder : IListenSocketBinder
    {
        /// <summary>Gets the shared production binder.</summary>
        public static SocketListenBinder Instance { get; } = new();

        /// <inheritdoc />
        public void BindAndListen(Socket socket, IPEndPoint endpoint, int backlog)
        {
            ArgumentNullException.ThrowIfNull(socket);
            ArgumentNullException.ThrowIfNull(endpoint);
            socket.Bind(endpoint);
            socket.Listen(backlog);
        }
    }
}
