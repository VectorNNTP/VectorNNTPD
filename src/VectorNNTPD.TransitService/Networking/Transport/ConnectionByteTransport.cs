using System.Diagnostics;
using System.Net.Security;
using VectorNNTPD.TransitService.SessionState.RateLimiting;

namespace VectorNNTPD.TransitService.Networking.Transport
{
    /// <summary>
    /// Single owner of byte-stream I/O under <see cref="NntpConnection"/> pumps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pumps call <see cref="ReadAsync"/> / <see cref="WriteAsync"/> only. They never touch the socket
    /// or <see cref="SslStream"/> directly. The active stream may be replaced during an in-place TLS
    /// upgrade after <see cref="QuiesceAsync"/> guarantees no outstanding stream I/O.
    /// </para>
    /// <para>
    /// Quiescence blocks new application reads/writes at a gate, cancels in-flight <em>reads</em>
    /// (freeing the socket for TLS handshake or DEFLATE wrap), and waits for in-flight <em>writes</em>
    /// to finish under the caller token. Admission of an I/O operation and registration of that
    /// operation as outstanding are atomic with respect to the quiescence state transition under
    /// <c>_gate</c>.
    /// </para>
    /// </remarks>
    internal sealed class ConnectionByteTransport : IAsyncDisposable
    {
        private const int StateActive = 0;
        private const int StateReadsPaused = 1;
        private const int StateQuiesced = 2;
        private const int StateAbandoned = 3;
        private const int StateDisposed = 4;

        private readonly object _gate = new();
        private Stream _stream;
        private CancellationTokenSource _readCts = new();
        private TaskCompletionSource _resumeTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _idleTcs;
        private TaskCompletionSource? _readIdleTcs;
        private int _activeReads;
        private int _activeWrites;
        private int _state;
        private bool _isTls;
        private bool _isCompressed;
        private byte[]? _pendingUpgradePrefix;
        private readonly OutboundRateLimiter _rateLimiter;

        public ConnectionByteTransport(Stream stream, bool isTls, OutboundRateLimiter? rateLimiter = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (rateLimiter is not null)
            {
                _rateLimiter = rateLimiter;
                _stream = stream;
            }
            else if (stream is OutboundRateLimiter existing)
            {
                _rateLimiter = existing;
                _stream = existing;
            }
            else
            {
                _rateLimiter = new OutboundRateLimiter(stream, initialMaxSendBytesPerSecond: 0, leaveInnerOpen: false);
                _stream = _rateLimiter;
            }

            _isTls = isTls;
            _resumeTcs.SetResult();
        }

        /// <summary>
        /// Wire-level outbound cap. Sits under TLS/DEFLATE so throttled bytes are
        /// the octets written toward the socket.
        /// </summary>
        internal OutboundRateLimiter RateLimiter => _rateLimiter;

        public bool IsTls
        {
            get
            {
                lock (_gate)
                {
                    return _isTls;
                }
            }
        }

        public bool IsCompressed
        {
            get
            {
                lock (_gate)
                {
                    return _isCompressed;
                }
            }
        }

        /// <summary>
        /// Test-only: awaited after <see cref="WaitUntilReadableAsync"/> / writable gate returns and before admission under
        /// <c>_gate</c>. Used to reproduce the historical Active-observed / not-yet-counted window.
        /// </summary>
        internal Func<ValueTask>? AfterActiveBeforeAdmitProbe { get; set; }

        /// <summary>
        /// Test-only: awaited inside <see cref="WriteAsync"/> after write admission and before the stream write.
        /// Used to hold <c>SendAsync</c> between <c>ReadAsync</c> and <c>AdvanceTo</c>.
        /// </summary>
        internal Func<ValueTask>? BeforeStreamWriteProbe { get; set; }

        /// <summary>
        /// Test-only: awaited inside <see cref="ReadAsync"/> after read admission and before the stream read.
        /// </summary>
        internal Func<ValueTask>? BeforeStreamReadProbe { get; set; }

        /// <summary>
        /// Test-only: awaited after a successful stream read returns and before the count is
        /// handed back to <c>ReceiveAsync</c> for <c>Advance</c>.
        /// </summary>
        internal Func<ValueTask>? AfterStreamReadProbe { get; set; }

        /// <summary>DIAGNOSTIC-ONLY session attached when <see cref="TransportIoProbe"/> is enabled.</summary>
        internal TransportIoSession? Io { get; set; }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitUntilReadableAsync(cancellationToken).ConfigureAwait(false);

                if (AfterActiveBeforeAdmitProbe is { } probe)
                {
                    await probe().ConfigureAwait(false);
                }

                Stream stream;
                CancellationToken readToken;
                lock (_gate)
                {
                    ThrowIfUnavailable();
                    if (_state != StateActive)
                    {
                        continue;
                    }

                    _activeReads++;
                    stream = _stream;
                    readToken = _readCts.Token;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(readToken, cancellationToken);
                try
                {
                    if (BeforeStreamReadProbe is { } readProbe)
                    {
                        await readProbe().ConfigureAwait(false);
                    }

                    var received = await ReadStreamAsync(stream, buffer, linked.Token).ConfigureAwait(false);
                    if (AfterStreamReadProbe is { } afterRead)
                    {
                        await afterRead().ConfigureAwait(false);
                    }

                    return received;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    continue;
                }
                finally
                {
                    CompleteOperation(isRead: true);
                }
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitUntilWritableAsync(cancellationToken).ConfigureAwait(false);

                if (AfterActiveBeforeAdmitProbe is { } probe)
                {
                    await probe().ConfigureAwait(false);
                }

                Stream stream;
                lock (_gate)
                {
                    ThrowIfUnavailable();
                    if (_state is not (StateActive or StateReadsPaused))
                    {
                        continue;
                    }

                    _activeWrites++;
                    stream = _stream;
                }

                try
                {
                    if (BeforeStreamWriteProbe is { } writeProbe)
                    {
                        await writeProbe().ConfigureAwait(false);
                    }

                    await WriteStreamAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
                    return;
                }
                finally
                {
                    CompleteOperation(isRead: false);
                }
            }
        }

        /// <summary>
        /// Flushes the active stream (required for DEFLATE sync-flush so compressed bytes reach the peer).
        /// </summary>
        public async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitUntilWritableAsync(cancellationToken).ConfigureAwait(false);

                if (AfterActiveBeforeAdmitProbe is { } probe)
                {
                    await probe().ConfigureAwait(false);
                }

                Stream stream;
                lock (_gate)
                {
                    ThrowIfUnavailable();
                    if (_state is not (StateActive or StateReadsPaused))
                    {
                        continue;
                    }

                    _activeWrites++;
                    stream = _stream;
                }

                try
                {
                    await FlushStreamAsync(stream, cancellationToken).ConfigureAwait(false);
                    return;
                }
                finally
                {
                    CompleteOperation(isRead: false);
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether the transport is in the reads-paused upgrade drain state.
        /// </summary>
        public bool IsReadsPaused
        {
            get
            {
                lock (_gate)
                {
                    return _state is StateReadsPaused or StateQuiesced;
                }
            }
        }

        /// <summary>
        /// After a successful <see cref="ReadAsync"/>, decides whether bytes may enter the application input pipe.
        /// </summary>
        /// <remarks>
        /// When quiesced for TLS/DEFLATE upgrade, socket octets already read must not be injected into
        /// <see cref="NntpConnection.Input"/> — they are retained as a prefix for the upgraded stream
        /// (ClientHello race after a STARTTLS <c>382</c> response).
        /// </remarks>
        /// <returns>
        /// <see langword="true"/> if the caller should write <paramref name="data"/> to the application pipe;
        /// <see langword="false"/> if the bytes were claimed for the pending upgrade prefix.
        /// </returns>
        public bool TryCommitReadToApplication(ReadOnlySpan<byte> data)
        {
            lock (_gate)
            {
                if (_state is not (StateReadsPaused or StateQuiesced or StateAbandoned))
                {
                    return true;
                }

                if (data.IsEmpty)
                {
                    return false;
                }

                if (_pendingUpgradePrefix is null)
                {
                    _pendingUpgradePrefix = data.ToArray();
                }
                else
                {
                    var combined = new byte[_pendingUpgradePrefix.Length + data.Length];
                    _pendingUpgradePrefix.CopyTo(combined, 0);
                    data.CopyTo(combined.AsSpan(_pendingUpgradePrefix.Length));
                    _pendingUpgradePrefix = combined;
                }

                return false;
            }
        }

        /// <summary>Takes any socket octets retained during quiescence for the upgraded stream wrap.</summary>
        public ReadOnlyMemory<byte> TakePendingUpgradePrefix()
        {
            lock (_gate)
            {
                var prefix = _pendingUpgradePrefix;
                _pendingUpgradePrefix = null;
                return prefix ?? ReadOnlyMemory<byte>.Empty;
            }
        }

        /// <summary>
        /// Restores Active I/O after a post-quiescence precondition failure (no stream replacement).
        /// </summary>
        public void ResumeFromQuiesceWithoutUpgrade()
        {
            TaskCompletionSource resume;
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_state is not (StateQuiesced or StateReadsPaused))
                {
                    throw new InvalidOperationException("Transport is not quiesced.");
                }

                _state = StateActive;
                _idleTcs = null;
                resume = _resumeTcs;
                _pendingUpgradePrefix = null;
            }

            resume.TrySetResult();
        }

        /// <summary>
        /// Cancels in-flight reads and blocks new reads while still allowing writes (outbound drain).
        /// </summary>
        public async Task PauseReadsAsync(CancellationToken cancellationToken)
        {
            CancellationTokenSource oldReadCts;
            TaskCompletionSource? readIdle;
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_state is StateReadsPaused or StateQuiesced)
                {
                    throw new InvalidOperationException("Transport reads are already paused.");
                }

                _state = StateReadsPaused;
                _resumeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                oldReadCts = _readCts;
                _readCts = new CancellationTokenSource();
                if (_activeReads == 0)
                {
                    readIdle = null;
                }
                else
                {
                    readIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _readIdleTcs = readIdle;
                }
            }

            await oldReadCts.CancelAsync().ConfigureAwait(false);
            oldReadCts.Dispose();

            if (readIdle is not null)
            {
                await readIdle.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// After <see cref="PauseReadsAsync"/>, blocks writes and waits until no stream I/O is outstanding.
        /// </summary>
        public async Task QuiesceWritesAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource idle;
            bool alreadyIdle;
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_state == StateQuiesced)
                {
                    throw new InvalidOperationException("Transport is already quiesced.");
                }

                if (_state != StateReadsPaused)
                {
                    throw new InvalidOperationException("Pause reads before quiescing writes.");
                }

                _state = StateQuiesced;
                idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _idleTcs = idle;
                alreadyIdle = _activeReads == 0 && _activeWrites == 0;
            }

            if (alreadyIdle)
            {
                idle.TrySetResult();
            }

            await idle.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Stops application stream I/O and waits until the underlying stream has no outstanding operations.
        /// </summary>
        public async Task QuiesceAsync(CancellationToken cancellationToken)
        {
            await PauseReadsAsync(cancellationToken).ConfigureAwait(false);
            await QuiesceWritesAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the quiesced plain stream so the caller can wrap it in <see cref="SslStream"/>.
        /// </summary>
        public Stream TakeQuiescedStreamForTlsWrap()
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_state != StateQuiesced)
                {
                    throw new InvalidOperationException("Transport must be quiesced before TLS wrap.");
                }

                if (_isTls)
                {
                    throw new InvalidOperationException("Transport is already TLS-protected.");
                }

                if (_isCompressed)
                {
                    throw new InvalidOperationException("Cannot negotiate TLS after DEFLATE is active.");
                }

                return _stream;
            }
        }

        /// <summary>
        /// Returns the quiesced stream (plain or TLS) so the caller can wrap it in <see cref="NntpDeflateStream"/>.
        /// </summary>
        public Stream TakeQuiescedStreamForDeflateWrap()
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_state != StateQuiesced)
                {
                    throw new InvalidOperationException("Transport must be quiesced before DEFLATE wrap.");
                }

                if (_isCompressed)
                {
                    throw new InvalidOperationException("Transport is already DEFLATE-compressed.");
                }

                return _stream;
            }
        }

        /// <summary>
        /// Publishes an authenticated <see cref="SslStream"/> and resumes pump I/O.
        /// </summary>
        public void PublishTlsAndResume(SslStream sslStream)
        {
            ArgumentNullException.ThrowIfNull(sslStream);
            TaskCompletionSource resume;
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_state != StateQuiesced)
                {
                    throw new InvalidOperationException("Transport must be quiesced before publishing TLS.");
                }

                if (_isCompressed)
                {
                    throw new InvalidOperationException("Cannot publish TLS after DEFLATE is active.");
                }

                _stream = sslStream;
                _isTls = true;
                _state = StateActive;
                _idleTcs = null;
                resume = _resumeTcs;
            }

            resume.TrySetResult();
        }

        /// <summary>
        /// Publishes a bidirectional raw-DEFLATE stream and resumes pump I/O.
        /// </summary>
        public void PublishDeflateAndResume(NntpDeflateStream deflateStream)
        {
            ArgumentNullException.ThrowIfNull(deflateStream);
            TaskCompletionSource resume;
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_state != StateQuiesced)
                {
                    throw new InvalidOperationException("Transport must be quiesced before publishing DEFLATE.");
                }

                if (_isCompressed)
                {
                    throw new InvalidOperationException("Transport is already DEFLATE-compressed.");
                }

                _stream = deflateStream;
                _isCompressed = true;
                _state = StateActive;
                _idleTcs = null;
                resume = _resumeTcs;
            }

            resume.TrySetResult();
        }

        /// <summary>
        /// Marks the transport abandoned after a failed upgrade and wakes waiters for connection teardown.
        /// Does not restore plaintext I/O.
        /// </summary>
        public void AbortQuiesceForConnectionTeardown()
        {
            TaskCompletionSource? resume;
            TaskCompletionSource? idle;
            lock (_gate)
            {
                if (_state != StateQuiesced)
                {
                    return;
                }

                _state = StateAbandoned;
                resume = _resumeTcs;
                idle = _idleTcs;
                _idleTcs = null;
            }

            idle?.TrySetResult();
            resume.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            Stream stream;
            CancellationTokenSource readCts;
            TaskCompletionSource resume;
            TaskCompletionSource? idle;
            lock (_gate)
            {
                if (_state == StateDisposed)
                {
                    return;
                }

                _state = StateDisposed;
                stream = _stream;
                readCts = _readCts;
                resume = _resumeTcs;
                idle = _idleTcs;
                _idleTcs = null;
            }

            idle?.TrySetResult();
            resume.TrySetResult();
            await readCts.CancelAsync().ConfigureAwait(false);
            readCts.Dispose();
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        private void ThrowIfUnavailable()
        {
            if (_state == StateAbandoned)
            {
                throw new ObjectDisposedException(nameof(ConnectionByteTransport), "Transport abandoned after failed TLS upgrade.");
            }

            ObjectDisposedException.ThrowIf(_state == StateDisposed, this);
        }

        private void CompleteOperation(bool isRead)
        {
            TaskCompletionSource? idle = null;
            TaskCompletionSource? readIdle = null;
            lock (_gate)
            {
                if (isRead)
                {
                    _activeReads--;
                }
                else
                {
                    _activeWrites--;
                }

                if (isRead
                    && _state == StateReadsPaused
                    && _readIdleTcs is not null
                    && _activeReads == 0)
                {
                    readIdle = _readIdleTcs;
                    _readIdleTcs = null;
                }

                if (_state == StateQuiesced
                    && _idleTcs is not null
                    && _activeReads == 0
                    && _activeWrites == 0)
                {
                    idle = _idleTcs;
                }
            }

            readIdle?.TrySetResult();
            idle?.TrySetResult();
        }

        private async ValueTask<int> ReadStreamAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var io = Io;
            if (io is null)
            {
                return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            var started = Stopwatch.GetTimestamp();
            var vt = stream.ReadAsync(buffer, cancellationToken);
            if (vt.IsCompletedSuccessfully)
            {
                var n = vt.Result;
                io.RecordReceive(buffer.Length, n, started, started, sync: true);
                return n;
            }

            var awaitStart = Stopwatch.GetTimestamp();
            var received = await vt.ConfigureAwait(false);
            io.RecordReceive(buffer.Length, received, started, awaitStart, sync: false);
            return received;
        }

        private async ValueTask WriteStreamAsync(Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            var io = Io;
            if (io is null)
            {
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                return;
            }

            var started = Stopwatch.GetTimestamp();
            var vt = stream.WriteAsync(buffer, cancellationToken);
            if (vt.IsCompletedSuccessfully)
            {
                io.RecordSend(buffer.Length, buffer.Length, started, started, sync: true);
                return;
            }

            var awaitStart = Stopwatch.GetTimestamp();
            await vt.ConfigureAwait(false);
            io.RecordSend(buffer.Length, buffer.Length, started, awaitStart, sync: false);
        }

        private async ValueTask FlushStreamAsync(Stream stream, CancellationToken cancellationToken)
        {
            var io = Io;
            if (io is null)
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var started = Stopwatch.GetTimestamp();
            var vt = stream.FlushAsync(cancellationToken);
            if (vt.IsCompletedSuccessfully)
            {
                io.RecordFlush(started, started, sync: true);
                return;
            }

            var awaitStart = Stopwatch.GetTimestamp();
            await vt.ConfigureAwait(false);
            io.RecordFlush(started, awaitStart, sync: false);
        }

        private async Task WaitUntilReadableAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    ThrowIfUnavailable();
                    if (_state == StateActive)
                    {
                        return;
                    }

                    wait = _resumeTcs.Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task WaitUntilWritableAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    ThrowIfUnavailable();
                    if (_state is StateActive or StateReadsPaused)
                    {
                        return;
                    }

                    wait = _resumeTcs.Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
