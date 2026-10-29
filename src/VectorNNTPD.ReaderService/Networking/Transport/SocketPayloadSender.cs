using System.Net.Sockets;

namespace VectorNNTPD.ReaderService.Networking.Transport
{
    /// <summary>
    /// Completes a TCP send of an entire <see cref="ReadOnlyMemory{T}"/> buffer, looping on partial sends.
    /// </summary>
    internal static class SocketPayloadSender
    {
        /// <summary>
        /// Sends <paramref name="buffer"/> on <paramref name="socket"/> until every byte is accepted or the
        /// operation fails / is cancelled.
        /// </summary>
        public static ValueTask SendAllAsync(
            Socket socket,
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken) =>
            SendAllAsync(
                static (memory, token, state) =>
                    ((Socket)state!).SendAsync(memory, SocketFlags.None, token),
                buffer,
                socket,
                cancellationToken);

        /// <summary>
        /// Testable send loop over an arbitrary send delegate (no heap copy of payload).
        /// </summary>
        /// <param name="sendAsync">
        /// Sends as many bytes as possible from the supplied memory; return value is the count accepted.
        /// </param>
        /// <param name="buffer">Payload to transmit completely.</param>
        /// <param name="state">Opaque state passed to <paramref name="sendAsync"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async ValueTask SendAllAsync(
            Func<ReadOnlyMemory<byte>, CancellationToken, object?, ValueTask<int>> sendAsync,
            ReadOnlyMemory<byte> buffer,
            object? state,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sendAsync);

            var remaining = buffer;
            while (!remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sent = await sendAsync(remaining, cancellationToken, state).ConfigureAwait(false);
                if (sent == 0)
                {
                    throw new IOException("Socket send transferred zero bytes.");
                }

                if ((uint)sent > (uint)remaining.Length)
                {
                    throw new IOException(
                        $"Socket send reported {sent} bytes but only {remaining.Length} were requested.");
                }

                remaining = remaining[sent..];
            }
        }
    }
}
