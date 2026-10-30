namespace VectorNNTPD.Common.Networking.RateLimiting
{
    /// <summary>
    /// Stream wrapper that throttles writes to a dynamically adjustable bytes/sec cap.
    /// Reads, flushes, and <c>cap == 0</c> are passthrough. <c>cap &lt; 0</c> blocks
    /// until the cap is updated.
    /// </summary>
    /// <remarks>
    /// Adapted from Vector.NNTP <c>DynamicSendRateLimitedStream</c>: one-second write window,
    /// 25 ms backoff, live <see cref="UpdateMaxSendBytesPerSecond"/> during writes.
    /// Writes are serialized. Cancellation aborts the backoff. Dispose does not wait
    /// for an in-flight write; the write token must be cancelled by the connection.
    /// </remarks>
    public sealed class OutboundRateLimiter : Stream, IOutboundRateCap
    {
        private readonly Stream _inner;
        private readonly bool _leaveInnerOpen;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private long _maxSendBytesPerSecond;
        private long _windowStartMs;
        private long _bytesInWindow;
        private int _disposed;

        /// <summary>Initializes a limiter over <paramref name="inner"/>.</summary>
        public OutboundRateLimiter(
            Stream inner,
            long initialMaxSendBytesPerSecond,
            bool leaveInnerOpen,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
            _leaveInnerOpen = leaveInnerOpen;
            _time = timeProvider ?? TimeProvider.System;
            _maxSendBytesPerSecond = initialMaxSendBytesPerSecond;
            _windowStartMs = NowMs();
        }

        /// <inheritdoc />
        public override bool CanRead => _inner.CanRead;

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
        public long MaxSendBytesPerSecond => Interlocked.Read(ref _maxSendBytesPerSecond);

        /// <inheritdoc />
        public void UpdateMaxSendBytesPerSecond(long bytesPerSecond) =>
            Interlocked.Exchange(ref _maxSendBytesPerSecond, bytesPerSecond);

        /// <inheritdoc />
        public override void Flush() => _inner.Flush();

        /// <inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            var cap = Interlocked.Read(ref _maxSendBytesPerSecond);
            if (buffer.IsEmpty || cap == 0)
            {
                await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                return;
            }

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                var offset = 0;
                while (offset < buffer.Length)
                {
                    cap = Interlocked.Read(ref _maxSendBytesPerSecond);
                    if (cap == 0)
                    {
                        await _inner.WriteAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (cap < 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(25), _time, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    var sliceBytes = buffer.Length - offset;
                    if (sliceBytes > cap)
                    {
                        sliceBytes = cap > int.MaxValue ? int.MaxValue : (int)cap;
                    }

                    await ThrottleAsync(sliceBytes, cap, cancellationToken).ConfigureAwait(false);
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                    await _inner.WriteAsync(buffer.Slice(offset, sliceBytes), cancellationToken)
                        .ConfigureAwait(false);
                    _bytesInWindow += sliceBytes;
                    offset += sliceBytes;
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _writeGate.Dispose();
                if (!_leaveInnerOpen)
                {
                    _inner.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _writeGate.Dispose();
                if (!_leaveInnerOpen)
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }

        private async Task ThrottleAsync(int nextBytes, long cap, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                var now = NowMs();
                if (now - _windowStartMs >= 1000)
                {
                    _windowStartMs = now;
                    _bytesInWindow = 0;
                }

                if (_bytesInWindow + nextBytes <= cap)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), _time, cancellationToken).ConfigureAwait(false);
            }
        }

        private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();
    }
}
