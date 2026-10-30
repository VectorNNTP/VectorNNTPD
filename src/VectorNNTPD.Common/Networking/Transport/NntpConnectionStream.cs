using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace VectorNNTPD.Common.Networking.Transport
{
    /// <summary>
    /// <see cref="Stream"/> adapter over an <see cref="INntpConnection"/> duplex pipe pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads consume <see cref="INntpConnection.Input"/>; writes produce to
    /// <see cref="INntpConnection.Output"/> and flush. Disposal completes and disposes the
    /// underlying connection when <see cref="_ownsConnection"/> is true.
    /// Used by Storage and BackFiller session layers that speak stream APIs over the shared
    /// pipeline transport.
    /// </para>
    /// <para>
    /// <b>Write cancellation contract:</b> each chunk is copied into the <see cref="PipeWriter"/>
    /// and <c>Advance</c>d before <c>FlushAsync</c>. The pipe API cannot roll back advanced bytes.
    /// If cancellation is observed during or after a flush, those committed bytes may still be
    /// delivered to the peer (or remain readable from the outbound pipe). A caller that retries the
    /// same buffer after <see cref="OperationCanceledException"/> can therefore duplicate data.
    /// <see cref="WriteAsync(System.ReadOnlyMemory{byte},System.Threading.CancellationToken)"/> does
    /// not report how many bytes were committed before cancellation.
    /// </para>
    /// </remarks>
    public sealed class NntpConnectionStream : Stream
    {
        private const int WriteChunkBytes = 64 * 1024;

        private readonly INntpConnection _connection;
        private readonly bool _ownsConnection;
        private int _disposed;

        /// <summary>Initializes a stream over <paramref name="connection"/>.</summary>
        /// <param name="connection">Live duplex pipe connection with pumps running.</param>
        /// <param name="ownsConnection">
        /// When <see langword="true"/>, disposing this stream completes and disposes
        /// <paramref name="connection"/>.
        /// </param>
        public NntpConnectionStream(INntpConnection connection, bool ownsConnection = true)
        {
            ArgumentNullException.ThrowIfNull(connection);
            _connection = connection;
            _ownsConnection = ownsConnection;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Synchronous reads are not supported on NntpConnectionStream.");

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Synchronous writes are not supported on NntpConnectionStream.");

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (buffer.IsEmpty)
            {
                return 0;
            }

            while (true)
            {
                var result = await _connection.Input.ReadAsync(cancellationToken).ConfigureAwait(false);
                var sequence = result.Buffer;
                // Exactly one AdvanceTo per ReadAsync. Default keeps unread bytes and examines nothing
                // so a failure before copy progress re-presents the same buffer.
                var consumed = sequence.Start;
                var examined = sequence.Start;
                try
                {
                    if (result.IsCanceled)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (sequence.IsEmpty)
                    {
                        examined = sequence.End;
                        if (result.IsCompleted)
                        {
                            return 0;
                        }

                        continue;
                    }

                    var copied = CopyToBuffer(sequence, buffer.Span);
                    consumed = sequence.GetPosition(copied);
                    // Examine only through consumed. Advancing examined to sequence.End while
                    // leaving unread bytes would make the next ReadAsync wait for more writes
                    // instead of returning the remainder.
                    examined = consumed;
                    return copied;
                }
                finally
                {
                    _connection.Input.AdvanceTo(consumed, examined);
                }
            }
        }

        /// <inheritdoc />
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty)
            {
                return;
            }

            // Every GetMemory/GetSpan is paired with Advance for that slice. Large payloads are
            // chunked and flushed per chunk so pause-writer backpressure can apply.
            var remaining = buffer;
            FlushResult flush;
            do
            {
                try
                {
                    var memory = _connection.Output.GetMemory(Math.Min(remaining.Length, WriteChunkBytes));
                    var toCopy = Math.Min(memory.Length, remaining.Length);
                    remaining.Span[..toCopy].CopyTo(memory.Span);
                    _connection.Output.Advance(toCopy);
                    remaining = remaining[toCopy..];

                    flush = await _connection.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    // Peer reset / CompleteAsync can finish the output writer while a session write is
                    // in flight. Surface that as IOException so listeners classify peer disconnect
                    // rather than an operational failure.
                    throw new IOException("NNTP connection output completed while writing.", ex);
                }

                if (flush.IsCanceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new OperationCanceledException(cancellationToken);
                }

                if (flush.IsCompleted)
                {
                    throw new IOException("NNTP connection output completed while writing.");
                }
            }
            while (!remaining.IsEmpty);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            if (_ownsConnection)
            {
                // CompleteAsync is best-effort before DisposeAsync; teardown races must not skip disposal.
                try
                {
                    await _connection.CompleteAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
                catch (IOException ex)
                {
                    _ = ex; // observed; intentionally ignored at this best-effort boundary
                }
                catch (SocketException ex)
                {
                    _ = ex; // observed; intentionally ignored at this best-effort boundary
                }

                await _connection.DisposeAsync().ConfigureAwait(false);
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsConnection)
            {
                _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }

        private static int CopyToBuffer(ReadOnlySequence<byte> sequence, Span<byte> destination)
        {
            var copied = 0;
            var position = sequence.Start;
            while (copied < destination.Length && sequence.TryGet(ref position, out var memory, advance: true))
            {
                var available = destination.Length - copied;
                var slice = memory.Length <= available
                    ? memory.Span
                    : memory.Span[..available];
                slice.CopyTo(destination[copied..]);
                copied += slice.Length;
            }

            return copied;
        }
    }
}
