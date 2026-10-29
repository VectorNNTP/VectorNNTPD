using System.Net.Sockets;
using VectorNNTPD.TransitService.Networking.Transport;

namespace VectorNNTPD.TransitService.Session.CommandProcessor
{
    /// <summary>
    /// Classifies transport failures that indicate the peer has already gone away.
    /// </summary>
    /// <remarks>
    /// Used by QUIT and by the session command-loop catch. Classification stays
    /// reset/EOF-shaped so unrelated I/O failures are not treated as normal disconnects.
    /// Authenticated-session release is guaranteed by session finalization, not by this classifier.
    /// </remarks>
    internal static class NntpPeerDisconnect
    {
        /// <summary>
        /// Returns whether <paramref name="exception"/> indicates the peer disconnected or the
        /// connection became terminal in a way expected during QUIT response/close.
        /// </summary>
        public static bool IsPeerDisconnect(Exception exception, INntpConnection connection)
        {
            ArgumentNullException.ThrowIfNull(exception);
            ArgumentNullException.ThrowIfNull(connection);

            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                switch (current)
                {
                    case SocketException socket when IsResetLike(socket.SocketErrorCode):
                        return true;
                    case IOException { InnerException: SocketException nested } when IsResetLike(nested.SocketErrorCode):
                        return true;
                    case IOException { Message: not null } io when
                        io.Message.Contains("zero bytes", StringComparison.OrdinalIgnoreCase):
                        return true;
                    case ObjectDisposedException when connection.IsCompleted
                        || connection.ConnectionClosed.IsCancellationRequested:
                        return true;
                    case InvalidOperationException { Message: not null } ioe when
                        ioe.Message.Contains("writer was completed", StringComparison.OrdinalIgnoreCase)
                        || ioe.Message.Contains("pipe completed", StringComparison.OrdinalIgnoreCase):
                        // Output PipeWriter completed (peer gone / CompleteAsync raced with QUIT flush).
                        return true;
                }
            }

            return false;
        }

        private static bool IsResetLike(SocketError error) =>
            error is SocketError.ConnectionReset
                or SocketError.ConnectionAborted
                or SocketError.Shutdown
                or SocketError.NotConnected
                or SocketError.NetworkReset
                // EPIPE (32) — not always present as SocketError.BrokenPipe on Windows TFMs.
                or (SocketError)32;
    }
}
