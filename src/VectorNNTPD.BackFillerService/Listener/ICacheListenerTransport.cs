using VectorNNTPD.Common.Core;

namespace VectorNNTPD.BackFillerService.Listener
{
    /// <summary>
    /// One established connected transport owned by a cache Listener session.
    /// </summary>
    /// <remarks>
    /// The session does not accept or bind sockets. Implementations own read and write completion
    /// semantics, including how much of each buffer one call transfers. Disposal is
    /// <see cref="IAsyncDisposable"/>.
    /// </remarks>
    internal interface ICacheListenerTransport : IAsyncDisposable
    {
        /// <summary>Reads bytes into <paramref name="buffer"/>.</summary>
        /// <param name="buffer">Destination for the next read.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The number of bytes written to <paramref name="buffer"/>. Zero means the peer closed.</returns>
        ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

        /// <summary>Writes bytes from <paramref name="buffer"/>.</summary>
        /// <param name="buffer">Bytes to write. The implementation decides whether an empty buffer is a no-op.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>How many bytes were accepted by this call.</returns>
        ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Stream adapter that applies one no-progress deadline to each read or non-empty write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/> and
    /// <see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/> complete the supplied buffer
    /// or throw. They do not report a partial count. This transport therefore passes each buffer through
    /// in one call. Completion of that call is the progress event. The next call receives a new deadline.
    /// A blocked call is not treated as progressing, because this API exposes no intermediate send count.
    /// </para>
    /// <para>
    /// The deadline timer and <see cref="CancellationTokenSource"/> belong to this connection. One read or
    /// non-empty write may be in flight. A repeated caller token keeps its cancellation registration for the
    /// life of that token on this transport. Replacing that registration detaches it under the transport lock
    /// and disposes it only after the lock is released, because disposal waits for the callback and the callback
    /// acquires the same lock. A callback carries the epoch from registration and does not cancel a newer link.
    /// A timeout with the caller token still active becomes
    /// <see cref="TimeoutException"/>. Caller cancellation propagates as <see cref="OperationCanceledException"/>.
    /// An empty write returns zero and does not arm a deadline. This type does not interpret NNTP commands
    /// or article bytes.
    /// </para>
    /// </remarks>
    internal sealed class StreamCacheListenerTransport : ICacheListenerTransport
    {
        /// <summary>Connected stream. Disposed only when <see cref="_leaveInnerStreamOpen"/> is false.</summary>
        private readonly Stream _stream;

        /// <summary>No-progress limit for one read or one non-empty write.</summary>
        private readonly TimeSpan _ioProgressTimeout;

        /// <summary>When true, <see cref="DisposeAsync"/> leaves <see cref="_stream"/> open.</summary>
        private readonly bool _leaveInnerStreamOpen;

        /// <summary>One-shot timer for <see cref="_deadlineTicks"/>. Disarmed between operations.</summary>
        private readonly Timer _timer;

        /// <summary>Serializes deadline state. Not held while the stream operation runs.</summary>
        private readonly object _gate = new();

        /// <summary>Canceled when the armed deadline or the caller token fires. Reused after <see cref="CancellationTokenSource.TryReset"/>.</summary>
        private CancellationTokenSource _timeoutCts = new();

        /// <summary>Caller token currently registered to cancel <see cref="_timeoutCts"/>.</summary>
        private CancellationToken _observedCaller;

        /// <summary>Registration for <see cref="_observedCaller"/>. Empty when that token cannot be canceled.</summary>
        private CancellationTokenRegistration _callerRegistration;

        /// <summary>True after a cancelable caller token has been registered.</summary>
        private bool _callerLinked;

        /// <summary>
        /// Identity of <see cref="_callerRegistration"/>. A callback captured an older value and must not cancel.
        /// </summary>
        private int _callerEpoch;

        /// <summary>Invoked on the caller-cancel callback before it acquires <see cref="_gate"/>. Null in production.</summary>
        internal Action? BeforeCallerCancelLock { get; set; }

        /// <summary>
        /// Invoked after a registration is detached and <see cref="_gate"/> is released, before that registration is disposed.
        /// Null in production.
        /// </summary>
        internal Action? BeforeDisposeDetachedRegistration { get; set; }

        /// <summary>Non-zero while a deadline is armed. A timer callback ignores a different generation.</summary>
        private int _generation;

        /// <summary><see cref="Environment.TickCount64"/> value at which the armed operation has made no progress.</summary>
        private long _deadlineTicks = long.MaxValue;

        /// <summary>One while a read or non-empty write owns the deadline. Zero otherwise.</summary>
        private int _busy;

        /// <summary>Zero until the first <see cref="DisposeAsync"/>; then one. Disposal is idempotent.</summary>
        private int _disposed;

        /// <summary>Initializes a stream-backed transport.</summary>
        /// <param name="stream">Connected stream used for reads and writes.</param>
        /// <param name="ioProgressTimeout">Positive no-progress limit for each read or non-empty write.</param>
        /// <param name="leaveInnerStreamOpen">
        /// When <see langword="true"/>, disposing this transport does not dispose <paramref name="stream"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="ioProgressTimeout"/> is not positive.</exception>
        internal StreamCacheListenerTransport(Stream stream, TimeSpan ioProgressTimeout, bool leaveInnerStreamOpen = false)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ioProgressTimeout, TimeSpan.Zero);
            _stream = stream;
            _ioProgressTimeout = ioProgressTimeout;
            _leaveInnerStreamOpen = leaveInnerStreamOpen;
            _timer = new Timer(static state => ((StreamCacheListenerTransport)state!).OnTimeout(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        /// <summary>Reads the next bytes, failing when the read does not complete within the no-progress deadline.</summary>
        /// <param name="buffer">Destination passed to <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/>.</param>
        /// <param name="cancellationToken">Cancels the read. When already canceled, the timeout is not reported.</param>
        /// <returns>The count returned by the stream. Zero means the peer closed.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when this transport is already disposed.</exception>
        /// <exception cref="InvalidOperationException">Thrown when another read or non-empty write is still in progress.</exception>
        /// <exception cref="TimeoutException">
        /// Thrown when the no-progress deadline elapses and <paramref name="cancellationToken"/> is not canceled.
        /// </exception>
        /// <remarks>
        /// The stream does not report partial reads to this method. Returning from the stream is the progress
        /// event, including a zero-byte peer close. A synchronous or asynchronous
        /// <see cref="OperationCanceledException"/> is disposal, a no-progress timeout, or caller cancellation.
        /// Caller cancellation propagates as <see cref="OperationCanceledException"/>.
        /// </remarks>
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            var operationToken = Arm(cancellationToken);
            ValueTask<int> read;
            try
            {
                read = _stream.ReadAsync(buffer, operationToken);
            }
            catch (OperationCanceledException)
            {
                Disarm();
                ThrowTranslatedCancellation(cancellationToken, operation: "read");
                throw;
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                Disarm();
                throw;
            }

            if (read.IsCompletedSuccessfully)
            {
                Disarm();
                return read;
            }

            return FinishReadAsync(read, cancellationToken);
        }

        /// <summary>
        /// Writes the whole buffer or returns zero when it is empty. The successful return is the buffer length, not a partial count.
        /// </summary>
        /// <param name="buffer">Bytes passed unchanged to <see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.</param>
        /// <param name="cancellationToken">Cancels a non-empty write. When already canceled, the timeout is not reported.</param>
        /// <returns>Zero when <paramref name="buffer"/> is empty; otherwise <c>buffer.Length</c> after the stream write completes.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when this transport is already disposed.</exception>
        /// <exception cref="InvalidOperationException">Thrown when another read or non-empty write is still in progress.</exception>
        /// <exception cref="TimeoutException">
        /// Thrown when a non-empty write exceeds the no-progress deadline and <paramref name="cancellationToken"/> is not canceled.
        /// </exception>
        /// <remarks>
        /// One call covers the whole buffer. The stream contract does not expose accepted-byte progress, so the
        /// deadline is not reset while the call is blocked. A synchronous or asynchronous
        /// <see cref="OperationCanceledException"/> is disposal, a no-progress timeout, or caller cancellation.
        /// Caller cancellation propagates as <see cref="OperationCanceledException"/>.
        /// </remarks>
        public ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (buffer.IsEmpty)
            {
                return new ValueTask<int>(0);
            }

            var operationToken = Arm(cancellationToken);
            ValueTask write;
            try
            {
                write = _stream.WriteAsync(buffer, operationToken);
            }
            catch (OperationCanceledException)
            {
                Disarm();
                ThrowTranslatedCancellation(cancellationToken, operation: "write");
                throw;
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                Disarm();
                throw;
            }

            if (write.IsCompletedSuccessfully)
            {
                Disarm();
                return new ValueTask<int>(buffer.Length);
            }

            return FinishWriteAsync(write, buffer.Length, cancellationToken);
        }

        /// <summary>Disposes the timer, the deadline source, and the inner stream once.</summary>
        /// <returns>A task that completes when the inner stream disposal finishes, or immediately on a later call.</returns>
        /// <remarks>
        /// A second call is a no-op. An in-flight read or write is canceled and surfaces as
        /// <see cref="ObjectDisposedException"/> rather than <see cref="TimeoutException"/>.
        /// The caller registration is detached under the lock and disposed after the lock is released.
        /// The inner stream stays open when construction asked to leave it open.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            CancellationTokenSource cts;
            CancellationTokenRegistration registration;
            lock (_gate)
            {
                _generation = 0;
                _deadlineTicks = long.MaxValue;
                _callerEpoch = _callerEpoch == int.MaxValue ? 1 : _callerEpoch + 1;
                cts = _timeoutCts;
                registration = _callerRegistration;
                _callerRegistration = default;
                _callerLinked = false;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }

            await _timer.DisposeAsync().ConfigureAwait(false);
            BeforeDisposeDetachedRegistration?.Invoke();
            registration.Dispose();
            cts.Dispose();
            if (!_leaveInnerStreamOpen)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Arms the connection deadline and returns the token the stream operation must observe.</summary>
        /// <param name="cancellationToken">Caller token registered when it can be canceled and is not the token already linked.</param>
        /// <returns>The token canceled by the deadline or by <paramref name="cancellationToken"/>.</returns>
        /// <exception cref="InvalidOperationException">Another read or non-empty write still owns the deadline.</exception>
        /// <exception cref="ObjectDisposedException">This transport is disposed.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is already canceled.</exception>
        private CancellationToken Arm(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("A listener transport performs one read or write at a time.");
            }

            CancellationTokenSource? retired = null;
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                cancellationToken.ThrowIfCancellationRequested();

                CancellationTokenRegistration detached = default;
                var hasDetached = false;
                var register = false;
                var epoch = 0;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed == 1, this);
                    if (!IsCurrentCaller(cancellationToken))
                    {
                        detached = DetachCallerRegistration(cancellationToken, out epoch);
                        hasDetached = true;
                        register = cancellationToken.CanBeCanceled;
                    }
                }

                using (detached)
                {
                    if (hasDetached)
                    {
                        // The using disposes after this hook returns, still outside _gate.
                        // Dispose waits for the old callback, which observes the epoch published above.
                        BeforeDisposeDetachedRegistration?.Invoke();
                    }
                }

                CancellationToken token;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed == 1, this);
                    if (!_timeoutCts.TryReset())
                    {
                        retired = _timeoutCts;
                        _timeoutCts = new CancellationTokenSource();
                    }

                    var dueMilliseconds = Math.Max(1L, (long)_ioProgressTimeout.TotalMilliseconds);
                    _generation = _generation == int.MaxValue ? 1 : _generation + 1;
                    _deadlineTicks = Environment.TickCount64 + dueMilliseconds;
                    _timer.Change(TimeSpan.FromMilliseconds(dueMilliseconds), Timeout.InfiniteTimeSpan);
                    token = _timeoutCts.Token;
                }

                if (register)
                {
                    // UnsafeRegister runs the callback inline when the token is already canceled.
                    RegisterCaller(cancellationToken, epoch);
                }

                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                retired?.Dispose();
                return token;
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                retired?.Dispose();
                Disarm();
                throw;
            }
        }

        /// <summary>Runs <paramref name="action"/> while holding the transport lock. Tests use this to block the caller-cancel callback.</summary>
        /// <param name="action">Work that must observe or hold <see cref="_gate"/>.</param>
        internal void InvokeUnderGate(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            lock (_gate)
            {
                action();
            }
        }

        /// <summary>Stops the deadline timer and releases the single-operation slot.</summary>
        /// <remarks>
        /// A callback queued for this operation re-reads <see cref="_generation"/> when it runs.
        /// After this method publishes generation zero, that callback does not cancel. The next
        /// <see cref="Arm"/> publishes a new generation and deadline before its stream call.
        /// </remarks>
        private void Disarm()
        {
            lock (_gate)
            {
                _generation = 0;
                _deadlineTicks = long.MaxValue;
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
            }

            Volatile.Write(ref _busy, 0);
        }

        /// <summary>Cancels the active operation when the armed deadline has passed.</summary>
        /// <remarks>
        /// The callback does not remember the operation that queued it. Under <see cref="_gate"/> it
        /// re-reads the generation and deadline. Generation zero means the operation already finished, so
        /// the callback returns. A future deadline belongs to the operation that is armed now; the callback
        /// reschedules that deadline and does not cancel. A passed deadline detaches the current source
        /// and only then cancels that detached instance. A source installed by a later
        /// <see cref="Arm"/> is a different object, so this cancel cannot reach it.
        /// </remarks>
        private void OnTimeout()
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            CancellationTokenSource? expired = null;
            lock (_gate)
            {
                if (_disposed == 1 || _generation == 0)
                {
                    return;
                }

                var remaining = _deadlineTicks - Environment.TickCount64;
                if (remaining > 0)
                {
                    _timer.Change(TimeSpan.FromMilliseconds(remaining), Timeout.InfiniteTimeSpan);
                    return;
                }

                _generation = 0;
                expired = _timeoutCts;
                _timeoutCts = new CancellationTokenSource();
            }

            try
            {
                // Cancel the detached source only. The field already names the source a later operation will arm.
                expired.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Dispose won the race.
            }
        }

        /// <summary>Maps a canceled stream operation to disposal or timeout.</summary>
        /// <param name="callerToken">Token supplied by the session. When this token is canceled, the caller keeps the original cancellation.</param>
        /// <param name="operation"><c>read</c> or <c>write</c>, included in the timeout message.</param>
        /// <remarks>
        /// Called from both the synchronous failure path and the asynchronous completion path.
        /// Disposal wins over timeout. A canceled caller token returns without throwing so the caller rethrows its
        /// <see cref="OperationCanceledException"/>.
        /// </remarks>
        private void ThrowTranslatedCancellation(CancellationToken callerToken, string operation)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                throw new ObjectDisposedException(nameof(StreamCacheListenerTransport));
            }

            if (!callerToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Listener transport {operation} exceeded no-progress timeout of {_ioProgressTimeout}.");
            }
        }

        /// <summary>Reports whether <paramref name="cancellationToken"/> is already the linked caller token.</summary>
        /// <param name="cancellationToken">Token presented by the operation about to arm.</param>
        /// <returns><see langword="true"/> when no registration replacement is required.</returns>
        private bool IsCurrentCaller(CancellationToken cancellationToken)
        {
            return _observedCaller.Equals(cancellationToken) && (_callerLinked || !cancellationToken.CanBeCanceled);
        }

        /// <summary>Detaches the installed registration and publishes <paramref name="cancellationToken"/> as the observed caller.</summary>
        /// <param name="cancellationToken">Token the next registration will observe.</param>
        /// <param name="epoch">Epoch a new callback must present before it can cancel.</param>
        /// <returns>The previous registration. The caller disposes it after releasing <see cref="_gate"/>.</returns>
        private CancellationTokenRegistration DetachCallerRegistration(CancellationToken cancellationToken, out int epoch)
        {
            var detached = _callerRegistration;
            _callerRegistration = default;
            _callerLinked = false;
            _observedCaller = cancellationToken;
            _callerEpoch = _callerEpoch == int.MaxValue ? 1 : _callerEpoch + 1;
            epoch = _callerEpoch;
            return detached;
        }

        /// <summary>Registers <paramref name="cancellationToken"/> for <paramref name="epoch"/> after <see cref="_gate"/> has been released.</summary>
        /// <param name="cancellationToken">Cancelable token whose callback should cancel the active operation.</param>
        /// <param name="epoch">Epoch published by <see cref="DetachCallerRegistration"/>.</param>
        /// <remarks>
        /// An already canceled token runs <see cref="OnCallerCanceled"/> before this method stores the registration.
        /// That callback takes <see cref="_gate"/>, so this method must not be called while the lock is held.
        /// A registration that loses the epoch race is disposed here, still outside the lock.
        /// </remarks>
        private void RegisterCaller(CancellationToken cancellationToken, int epoch)
        {
            var registration = cancellationToken.UnsafeRegister(
                static state => ((CallerCancelState)state!).Invoke(),
                new CallerCancelState(this, epoch));

            CancellationTokenRegistration superseded = default;
            var discard = false;
            lock (_gate)
            {
                if (_disposed == 1 || _callerEpoch != epoch)
                {
                    discard = true;
                    superseded = registration;
                }
                else
                {
                    _callerRegistration = registration;
                    _callerLinked = true;
                }
            }

            if (discard)
            {
                superseded.Dispose();
            }
        }

        /// <summary>Cancels the active operation when the registered caller token fires for <paramref name="epoch"/>.</summary>
        /// <param name="epoch">Epoch captured when this callback was registered.</param>
        private void OnCallerCanceled(int epoch)
        {
            BeforeCallerCancelLock?.Invoke();
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_disposed == 1 || epoch != _callerEpoch)
                {
                    return;
                }

                cts = _timeoutCts;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Dispose won the race.
            }
        }

        /// <summary>State for one caller-token registration. The callback closes over its epoch, not the current field.</summary>
        private sealed class CallerCancelState
        {
            /// <summary>Transport whose operation should cancel.</summary>
            private readonly StreamCacheListenerTransport _transport;

            /// <summary>Epoch that was current when this registration was created.</summary>
            private readonly int _epoch;

            /// <summary>Captures the transport and the epoch the callback is allowed to cancel.</summary>
            /// <param name="transport">Transport that owns the registration.</param>
            /// <param name="epoch">Epoch published for this registration.</param>
            internal CallerCancelState(StreamCacheListenerTransport transport, int epoch)
            {
                _transport = transport;
                _epoch = epoch;
            }

            /// <summary>Forwards the callback to <see cref="OnCallerCanceled"/>.</summary>
            internal void Invoke() => _transport.OnCallerCanceled(_epoch);
        }

        /// <summary>Awaits an incomplete or failed read and translates deadline cancellation.</summary>
        /// <param name="read">In-flight stream read.</param>
        /// <param name="cancellationToken">Caller token used to distinguish timeout from caller cancellation.</param>
        /// <returns>The stream read count.</returns>
        private async ValueTask<int> FinishReadAsync(ValueTask<int> read, CancellationToken cancellationToken)
        {
            try
            {
                return await read.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ThrowTranslatedCancellation(cancellationToken, operation: "read");
                throw;
            }
            finally
            {
                Disarm();
            }
        }

        /// <summary>Awaits an incomplete or failed write and translates deadline cancellation.</summary>
        /// <param name="write">In-flight stream write.</param>
        /// <param name="length">Buffer length returned after the stream accepts every byte.</param>
        /// <param name="cancellationToken">Caller token used to distinguish timeout from caller cancellation.</param>
        /// <returns><paramref name="length"/> after the write completes.</returns>
        private async ValueTask<int> FinishWriteAsync(ValueTask write, int length, CancellationToken cancellationToken)
        {
            try
            {
                await write.ConfigureAwait(false);
                return length;
            }
            catch (OperationCanceledException)
            {
                ThrowTranslatedCancellation(cancellationToken, operation: "write");
                throw;
            }
            finally
            {
                Disarm();
            }
        }
    }
}
