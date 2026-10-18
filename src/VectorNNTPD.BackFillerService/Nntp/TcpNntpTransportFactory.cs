using System.Net.Sockets;
using VectorNNTPD.BackFillerService.Networking;

namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// Production transport: TCP plus optional implicit TLS.
    /// A null <see cref="NntpSessionOptions.ServerCertificateValidationCallback"/> uses platform certificate validation.
    /// </summary>
    /// <remarks>The factory does not read or write NNTP. The returned stream owns the <see cref="TcpClient"/>.</remarks>
    internal sealed class TcpNntpTransportFactory : INntpTransportFactory
    {
        /// <summary>
        /// Connects to <paramref name="provider"/> and returns a stream that owns the <see cref="TcpClient"/>.
        /// </summary>
        /// <param name="provider">Endpoint and implicit-TLS flag. Credentials are not read.</param>
        /// <param name="options">Connect timeout, TCP buffer limits, reader buffer, and optional certificate callback.</param>
        /// <param name="cancellationToken">Cancels the TCP connect and, when implicit TLS is used, the handshake.</param>
        /// <returns>
        /// A cleartext network stream when <see cref="BackFillerProviderDefinition.UseTls"/> is false.
        /// Otherwise the <see cref="System.Net.Security.SslStream"/> from <see cref="NntpTlsClient.AuthenticateAsClientAsync"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> or <paramref name="options"/> is null.</exception>
        /// <remarks>
        /// <see cref="TcpSocketOptions.Apply"/> runs on the socket before connect and again on the socket
        /// connect returned, before <see cref="TcpClient.GetStream"/> and before any TLS or NNTP write.
        /// Send and receive limits come from <see cref="NntpSessionOptions.TcpSendBufferBytes"/> and
        /// <see cref="NntpSessionOptions.TcpReceiveBufferBytes"/>. <see cref="NntpSessionOptions.ReceiveBufferBytes"/>
        /// is the managed reader size and is not copied onto the socket. Nagle is disabled.
        /// The TCP connect uses <see cref="NntpSessionOptions.ConnectTimeout"/> linked with <paramref name="cancellationToken"/>.
        /// Implicit TLS then gets another full <see cref="NntpSessionOptions.ConnectTimeout"/>, linked only with <paramref name="cancellationToken"/>.
        /// Any failure, including <see cref="System.Net.Sockets.SocketException"/>, disposes the client and rethrows.
        /// This factory does not classify the failure. <see cref="NntpProviderSession.ConnectAsync"/> turns a socket failure into a non-reusable provider-failure result.
        /// The caller disposes the returned stream.
        /// </remarks>
        public async Task<Stream> ConnectAsync(
            BackFillerProviderDefinition provider,
            NntpSessionOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(options);

            var client = new TcpClient();

            try
            {
                TcpSocketOptions.Apply(client.Client, options.TcpSendBufferBytes, options.TcpReceiveBufferBytes);
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(options.ConnectTimeout);
                await client.ConnectAsync(provider.Host, provider.Port, connectCts.Token).ConfigureAwait(false);
                // ConnectAsync(string) can replace the socket when the resolved address family differs.
                // Re-apply on the connected socket before the first TLS or NNTP byte.
                TcpSocketOptions.Apply(client.Client, options.TcpSendBufferBytes, options.TcpReceiveBufferBytes);
                Stream stream = client.GetStream();
                if (!provider.UseTls)
                {
                    return new TcpOwnedStream(client, stream);
                }

                var ssl = await NntpTlsClient.AuthenticateAsClientAsync(
                        stream,
                        provider.Host,
                        options.ConnectTimeout,
                        options.ServerCertificateValidationCallback,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new TcpOwnedStream(client, ssl);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Disposes both the inner stream and the <see cref="TcpClient"/> that owns the socket.
        /// Synchronous reads and writes are rejected. The stream is not seekable.
        /// </summary>
        /// <param name="client">Connected client. Disposed with this stream.</param>
        /// <param name="inner">Network or TLS stream over <paramref name="client"/>. Disposed with this stream.</param>
        private sealed class TcpOwnedStream(TcpClient client, Stream inner) : Stream
        {
            /// <summary>Gets whether the inner stream can be read.</summary>
            public override bool CanRead => inner.CanRead;

            /// <summary>Gets <see langword="false"/>. This wrapper does not support seeking.</summary>
            public override bool CanSeek => false;

            /// <summary>Gets whether the inner stream can be written.</summary>
            public override bool CanWrite => inner.CanWrite;

            /// <summary>Seeking is not supported.</summary>
            /// <exception cref="NotSupportedException">Always thrown.</exception>
            public override long Length => throw new NotSupportedException();

            /// <summary>Seeking is not supported.</summary>
            /// <exception cref="NotSupportedException">Getting or setting the position always throws.</exception>
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <summary>Flushes the inner stream.</summary>
            public override void Flush() => inner.Flush();

            /// <summary>Flushes the inner stream.</summary>
            /// <param name="cancellationToken">Forwarded to the inner flush.</param>
            /// <returns>The inner flush task.</returns>
            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

            /// <summary>Synchronous reads are rejected.</summary>
            /// <param name="buffer">Ignored.</param>
            /// <param name="offset">Ignored.</param>
            /// <param name="count">Ignored.</param>
            /// <returns>This method does not return.</returns>
            /// <exception cref="NotSupportedException">Always thrown. Callers must use <see cref="ReadAsync(Memory{byte}, CancellationToken)"/>.</exception>
            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException("Use ReadAsync.");

            /// <summary>Reads from the inner stream.</summary>
            /// <param name="buffer">Destination supplied to the inner stream.</param>
            /// <param name="cancellationToken">Forwarded to the inner read.</param>
            /// <returns>The inner read.</returns>
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(buffer, cancellationToken);

            /// <summary>Synchronous writes are rejected.</summary>
            /// <param name="buffer">Ignored.</param>
            /// <param name="offset">Ignored.</param>
            /// <param name="count">Ignored.</param>
            /// <exception cref="NotSupportedException">Always thrown. Callers must use <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.</exception>
            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException("Use WriteAsync.");

            /// <summary>Writes to the inner stream.</summary>
            /// <param name="buffer">Bytes supplied to the inner stream.</param>
            /// <param name="cancellationToken">Forwarded to the inner write.</param>
            /// <returns>The inner write.</returns>
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                inner.WriteAsync(buffer, cancellationToken);

            /// <summary>Seeking is not supported.</summary>
            /// <param name="offset">Ignored.</param>
            /// <param name="origin">Ignored.</param>
            /// <returns>This method does not return.</returns>
            /// <exception cref="NotSupportedException">Always thrown.</exception>
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <summary>Changing the length is not supported.</summary>
            /// <param name="value">Ignored.</param>
            /// <exception cref="NotSupportedException">Always thrown.</exception>
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <summary>Disposes the inner stream and then the <see cref="TcpClient"/> when <paramref name="disposing"/> is true.</summary>
            /// <param name="disposing"><see langword="true"/> when called from <see cref="IDisposable.Dispose"/>.</param>
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                    client.Dispose();
                }

                base.Dispose(disposing);
            }

            /// <summary>Disposes the inner stream asynchronously and then the <see cref="TcpClient"/>.</summary>
            /// <returns>A task that completes after the inner stream is disposed.</returns>
            /// <remarks>Does not call <see cref="Dispose(bool)"/>. A later <see cref="IDisposable.Dispose"/> disposes the same objects again.</remarks>
            public override async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                client.Dispose();
            }
        }
    }
}
