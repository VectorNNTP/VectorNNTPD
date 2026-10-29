namespace VectorNNTPD.ReaderService.Networking.Proxy
{
    /// <summary>
    /// Stream that yields a fixed prefix before reading from an inner stream (TLS leftover after PROXY).
    /// </summary>
    internal sealed class PrefixedStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _leaveInnerOpen;
        private byte[]? _prefix;
        private int _prefixOffset;

        public PrefixedStream(Stream inner, ReadOnlyMemory<byte> prefix, bool leaveInnerOpen = false)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
            _leaveInnerOpen = leaveInnerOpen;
            if (!prefix.IsEmpty)
            {
                _prefix = prefix.ToArray();
            }
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => _inner.CanWrite;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush() => _inner.Flush();

        /// <inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        /// <inheritdoc />
        public override int Read(Span<byte> buffer)
        {
            if (_prefix is { } prefix && _prefixOffset < prefix.Length)
            {
                var available = prefix.Length - _prefixOffset;
                var take = Math.Min(available, buffer.Length);
                prefix.AsSpan(_prefixOffset, take).CopyTo(buffer);
                _prefixOffset += take;
                if (_prefixOffset >= prefix.Length)
                {
                    _prefix = null;
                }

                return take;
            }

            return _inner.Read(buffer);
        }

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix is { } prefix && _prefixOffset < prefix.Length)
            {
                var available = prefix.Length - _prefixOffset;
                var take = Math.Min(available, buffer.Length);
                prefix.AsSpan(_prefixOffset, take).CopyTo(buffer.Span);
                _prefixOffset += take;
                if (_prefixOffset >= prefix.Length)
                {
                    _prefix = null;
                }

                return ValueTask.FromResult(take);
            }

            return _inner.ReadAsync(buffer, cancellationToken);
        }

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);

        /// <inheritdoc />
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveInnerOpen)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            if (!_leaveInnerOpen)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
