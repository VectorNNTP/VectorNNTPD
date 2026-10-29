using System.IO.Compression;

namespace VectorNNTPD.TransitService.Networking.Transport
{
    /// <summary>
    /// Bidirectional raw DEFLATE transform for an NNTP connection byte stream (RFC 8054 / RFC 1951).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps an underlying duplex stream (plain <see cref="System.Net.Sockets.NetworkStream"/> or
    /// <see cref="System.Net.Security.SslStream"/>) with independent compressor and decompressor state.
    /// Application writes are compressed toward the peer; application reads are decompressed from the peer.
    /// </para>
    /// <para>
    /// Wire format is <strong>raw DEFLATE</strong> (zlib <c>windowBits</c> in the negative range), matching
    /// RFC 8054 §4. This uses <see cref="DeflateStream"/>, which does <em>not</em> emit a zlib (RFC 1950)
    /// wrapper or a gzip wrapper. Do not substitute <see cref="ZLibStream"/> or <see cref="GZipStream"/>.
    /// </para>
    /// <para>
    /// <see cref="FlushAsync(System.Threading.CancellationToken)"/> forwards to
    /// <see cref="DeflateStream.FlushAsync(System.Threading.CancellationToken)"/>. On .NET 10 that
    /// implementation uses DEFLATE sync-flush semantics so pending compressed bytes become visible on
    /// the underlying stream while retaining the sliding dictionary. Finalization (end-of-stream
    /// trailer) occurs only when this stream is disposed.
    /// </para>
    /// <para>
    /// Concurrent <see cref="ReadAsync(System.Memory{byte},System.Threading.CancellationToken)"/> and
    /// <see cref="WriteAsync(System.ReadOnlyMemory{byte},System.Threading.CancellationToken)"/> are supported
    /// (full-duplex): the inflater only reads the inner stream; the deflater only writes it.
    /// </para>
    /// </remarks>
    internal sealed class NntpDeflateStream : Stream
    {
        private readonly Stream _inner;
        private readonly DeflateStream _compressor;
        private readonly DeflateStream _decompressor;
        private bool _disposed;

        /// <summary>
        /// Creates a duplex raw-DEFLATE layer over <paramref name="inner"/>.
        /// </summary>
        /// <param name="inner">Underlying duplex byte stream; ownership transfers to this instance.</param>
        /// <param name="compressionLevel">zlib-compatible compression level for the outbound compressor.</param>
        public NntpDeflateStream(Stream inner, CompressionLevel compressionLevel = CompressionLevel.Optimal)
        {
            ArgumentNullException.ThrowIfNull(inner);
            if (!inner.CanRead || !inner.CanWrite)
            {
                throw new ArgumentException("Inner stream must be readable and writable.", nameof(inner));
            }

            _inner = inner;
            // leaveOpen: true — this type owns and disposes the inner stream exactly once.
            _compressor = new DeflateStream(inner, compressionLevel, leaveOpen: true);
            _decompressor = new DeflateStream(inner, CompressionMode.Decompress, leaveOpen: true);
        }

        /// <inheritdoc />
        public override bool CanRead => !_disposed;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => !_disposed;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush() => _compressor.Flush();

        /// <inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _compressor.FlushAsync(cancellationToken);

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            _decompressor.Read(buffer, offset, count);

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _decompressor.ReadAsync(buffer, offset, count, cancellationToken);

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _decompressor.ReadAsync(buffer, cancellationToken);

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            _compressor.Write(buffer, offset, count);

        /// <inheritdoc />
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _compressor.WriteAsync(buffer, offset, count, cancellationToken);

        /// <inheritdoc />
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _compressor.WriteAsync(buffer, cancellationToken);

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                // Compressor dispose finalizes the DEFLATE stream (writes remaining bits / end marker).
                _compressor.Dispose();
                _decompressor.Dispose();
                _inner.Dispose();
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _compressor.DisposeAsync().ConfigureAwait(false);
            await _decompressor.DisposeAsync().ConfigureAwait(false);
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
