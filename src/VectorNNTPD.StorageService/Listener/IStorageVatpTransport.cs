using System.Diagnostics;

namespace VectorNNTPD.StorageService.Listener
{

    /// <summary>One established connected transport owned by a StorageServer VATP session.</summary>
    public interface IStorageVatpTransport : IAsyncDisposable
    {
        /// <summary>Reads bytes. Zero means the peer closed.</summary>
        ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

        /// <summary>Writes bytes and returns how many were accepted.</summary>
        ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

        /// <summary>
        /// Suspends or resumes the read no-progress timeout.
        /// STORE admission suspends it while the client must stay silent, then resumes it
        /// when that wait ends so a later stall still times out. The default does nothing.
        /// </summary>
        /// <param name="suspended"><see langword="true"/> to disarm the outstanding read timer.</param>
        void SetReadProgressSuspended(bool suspended) { }
    }

    /// <summary>
    /// Stream adapter that applies the configured I/O no-progress timeout to each read or write.
    /// Successive reads reuse one timeout source, and successive writes reuse another.
    /// One read may overlap one write. STORE admission does that when it grants WINDOW while the
    /// session is blocked waiting for the next client frame. Two overlapping reads, or two
    /// overlapping writes, are not supported. A read and a write keep separate timeout sources,
    /// so a WINDOW write that passes <see cref="CancellationToken.None"/> cannot cancel the read.
    /// </summary>
    /// <remarks>
    /// Timeout CTS and dispose state are per transport instance. I/O-split and TLS thread-cost
    /// counters are process-wide aggregates for bench observation (same model as
    /// <see cref="StorageVatpSession"/> receive metrics): every live transport contributes through
    /// <see cref="Interlocked"/> updates, and <see cref="ResetIoSplit"/> clears the process totals.
    /// </remarks>
    public sealed class StreamStorageVatpTransport : IStorageVatpTransport
    {
        private readonly Stream _stream;
        private readonly TimeSpan _ioProgressTimeout;
        private readonly bool _leaveInnerStreamOpen;
        private readonly object _readTimeoutGate = new();
        private readonly object _writeTimeoutGate = new();
        private CancellationTokenSource? _readTimeout;
        private CancellationToken _readTimeoutCaller;
        private CancellationTokenSource? _writeTimeout;
        private CancellationToken _writeTimeoutCaller;
        private int _suspendReadProgress;
        private int _disposed;

        // Process-wide bench counters. Updated only from static helpers so concurrent transports
        // share one Interlocked total without instance methods writing static fields directly.
        private static long _ioTimeoutTicks;
        private static long _streamReadTicks;
        private static long _readSameThreadCount;
        private static long _readHopCount;
        private static long _readHopElapsedTicks;
        private static long _readSameThreadElapsedTicks;
        private static long _readSameThreadCpu100Ns;
        private static long _serverWriteSameThreadCount;
        private static long _serverWriteHopCount;
        private static long _serverWriteHopElapsedTicks;
        private static long _serverWriteSameThreadElapsedTicks;
        private static long _serverWriteSameThreadCpu100Ns;

        /// <summary>Clears the timeout-setup and stream-read split. Does not change I/O.</summary>
        internal static void ResetIoSplit()
        {
            Interlocked.Exchange(ref _ioTimeoutTicks, 0);
            Interlocked.Exchange(ref _streamReadTicks, 0);
            Interlocked.Exchange(ref _readSameThreadCount, 0);
            Interlocked.Exchange(ref _readHopCount, 0);
            Interlocked.Exchange(ref _readHopElapsedTicks, 0);
            Interlocked.Exchange(ref _readSameThreadElapsedTicks, 0);
            Interlocked.Exchange(ref _readSameThreadCpu100Ns, 0);
            Interlocked.Exchange(ref _serverWriteSameThreadCount, 0);
            Interlocked.Exchange(ref _serverWriteHopCount, 0);
            Interlocked.Exchange(ref _serverWriteHopElapsedTicks, 0);
            Interlocked.Exchange(ref _serverWriteSameThreadElapsedTicks, 0);
            Interlocked.Exchange(ref _serverWriteSameThreadCpu100Ns, 0);
        }

        /// <summary>
        /// Gets elapsed time spent arming the no-progress timeout and elapsed time inside the inner stream read.
        /// The two intervals do not overlap. Neither is CPU time.
        /// </summary>
        internal static (double TimeoutSetupSeconds, double StreamReadSeconds) ReadIoSplit()
        {
            return (
                Volatile.Read(ref _ioTimeoutTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _streamReadTicks) / (double)Stopwatch.Frequency);
        }

        /// <summary>
        /// Gets same-thread TLS cost. CPU is only summed when the call resumed on the thread that started it.
        /// </summary>
        internal static TlsThreadCost ReadTlsThreadCost()
        {
            return new TlsThreadCost(
                Volatile.Read(ref _readSameThreadCount),
                Volatile.Read(ref _readHopCount),
                Volatile.Read(ref _readHopElapsedTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _readSameThreadElapsedTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _readSameThreadCpu100Ns) / 10_000_000d,
                Volatile.Read(ref _serverWriteSameThreadCount),
                Volatile.Read(ref _serverWriteHopCount),
                Volatile.Read(ref _serverWriteHopElapsedTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _serverWriteSameThreadElapsedTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _serverWriteSameThreadCpu100Ns) / 10_000_000d);
        }

        /// <summary>Same-thread TLS read and write cost. Hop counts are calls whose resume thread differed.</summary>
        internal readonly record struct TlsThreadCost(
            long ReadSameThread,
            long ReadHops,
            double ReadHopSeconds,
            double ReadSameThreadSeconds,
            double ReadSameThreadCpuSeconds,
            long WriteSameThread,
            long WriteHops,
            double WriteHopSeconds,
            double WriteSameThreadSeconds,
            double WriteSameThreadCpuSeconds);

        /// <summary>Adds timeout-arming elapsed ticks to the process-wide I/O split.</summary>
        private static void NoteIoTimeoutTicks(long elapsedTicks) =>
            Interlocked.Add(ref _ioTimeoutTicks, elapsedTicks);

        /// <summary>Adds inner-stream read elapsed ticks to the process-wide I/O split.</summary>
        private static void NoteStreamReadTicks(long elapsedTicks) =>
            Interlocked.Add(ref _streamReadTicks, elapsedTicks);

        /// <summary>
        /// Records TLS read cost. Same-thread CPU is counted only when resume stays on the start thread.
        /// </summary>
        private static void NoteReadTlsThreadCost(int threadId, long cpuKernel, long cpuUser, long elapsedTicks)
        {
            var after = ThreadCpuTimes.Capture();
            if (threadId != 0 && threadId == ThreadCpuTimes.CurrentThreadId)
            {
                Interlocked.Increment(ref _readSameThreadCount);
                Interlocked.Add(ref _readSameThreadElapsedTicks, elapsedTicks);
                var cpu = (after.Kernel - cpuKernel) + (after.User - cpuUser);
                if (cpu > 0)
                {
                    Interlocked.Add(ref _readSameThreadCpu100Ns, cpu);
                }
            }
            else
            {
                Interlocked.Increment(ref _readHopCount);
                Interlocked.Add(ref _readHopElapsedTicks, elapsedTicks);
            }
        }

        /// <summary>
        /// Records TLS write cost. Same-thread CPU is counted only when resume stays on the start thread.
        /// </summary>
        private static void NoteWriteTlsThreadCost(int threadId, long cpuKernel, long cpuUser, long elapsedTicks)
        {
            var after = ThreadCpuTimes.Capture();
            if (threadId != 0 && threadId == ThreadCpuTimes.CurrentThreadId)
            {
                Interlocked.Increment(ref _serverWriteSameThreadCount);
                Interlocked.Add(ref _serverWriteSameThreadElapsedTicks, elapsedTicks);
                var cpu = (after.Kernel - cpuKernel) + (after.User - cpuUser);
                if (cpu > 0)
                {
                    Interlocked.Add(ref _serverWriteSameThreadCpu100Ns, cpu);
                }
            }
            else
            {
                Interlocked.Increment(ref _serverWriteHopCount);
                Interlocked.Add(ref _serverWriteHopElapsedTicks, elapsedTicks);
            }
        }

        /// <summary>Initializes a stream-backed transport.</summary>
        public StreamStorageVatpTransport(Stream stream, TimeSpan ioProgressTimeout, bool leaveInnerStreamOpen = false)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ioProgressTimeout, TimeSpan.Zero);
            _stream = stream;
            _ioProgressTimeout = ioProgressTimeout;
            _leaveInnerStreamOpen = leaveInnerStreamOpen;
        }

        /// <inheritdoc />
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            var timeoutStarted = Stopwatch.GetTimestamp();
            CancellationTokenSource timeoutCts;
            lock (_readTimeoutGate)
            {
                timeoutCts = RentIoTimeout(ref _readTimeout, ref _readTimeoutCaller, cancellationToken);
                if (_suspendReadProgress != 0)
                {
                    timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
                }
            }

            NoteIoTimeoutTicks(Stopwatch.GetTimestamp() - timeoutStarted);
            try
            {
                var threadId = ThreadCpuTimes.CurrentThreadId;
                var cpu = ThreadCpuTimes.Capture();
                var streamStarted = Stopwatch.GetTimestamp();
                var read = await _stream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
                var elapsed = Stopwatch.GetTimestamp() - streamStarted;
                NoteStreamReadTicks(elapsed);
                NoteReadTlsThreadCost(threadId, cpu.Kernel, cpu.User, elapsed);
                return read;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Storage VATP transport read exceeded no-progress timeout of {_ioProgressTimeout}.");
            }
        }

        /// <inheritdoc />
        public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (buffer.IsEmpty)
            {
                return 0;
            }

            CancellationTokenSource timeoutCts;
            lock (_writeTimeoutGate)
            {
                timeoutCts = RentIoTimeout(ref _writeTimeout, ref _writeTimeoutCaller, cancellationToken);
            }
            try
            {
                var threadId = ThreadCpuTimes.CurrentThreadId;
                var cpu = ThreadCpuTimes.Capture();
                var started = Stopwatch.GetTimestamp();
                await _stream.WriteAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
                NoteWriteTlsThreadCost(threadId, cpu.Kernel, cpu.User, Stopwatch.GetTimestamp() - started);
                return buffer.Length;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Storage VATP transport write exceeded no-progress timeout of {_ioProgressTimeout}.");
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            CancellationTokenSource? readTimeout;
            CancellationTokenSource? writeTimeout;
            lock (_readTimeoutGate)
            {
                readTimeout = _readTimeout;
                _readTimeout = null;
            }

            lock (_writeTimeoutGate)
            {
                writeTimeout = _writeTimeout;
                _writeTimeout = null;
            }

            readTimeout?.Dispose();
            writeTimeout?.Dispose();
            if (!_leaveInnerStreamOpen)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public void SetReadProgressSuspended(bool suspended)
        {
            lock (_readTimeoutGate)
            {
                var next = suspended ? 1 : 0;
                if (_suspendReadProgress == next || Volatile.Read(ref _disposed) == 1)
                {
                    return;
                }

                _suspendReadProgress = next;
                var timeout = _readTimeout;
                if (timeout is null)
                {
                    return;
                }

                try
                {
                    timeout.CancelAfter(suspended ? Timeout.InfiniteTimeSpan : _ioProgressTimeout);
                }
                catch (ObjectDisposedException ex)
                {
                    // CancelAfter after Dispose races with SetReadProgressSuspended.
                    ExpectedException.Ignore(ex);
                }
            }
        }

        /// <summary>
        /// Reuses one timeout source for successive operations of the same direction.
        /// Caller holds the gate for that direction. Read and write never share a source.
        /// </summary>
        private CancellationTokenSource RentIoTimeout(
            ref CancellationTokenSource? slot,
            ref CancellationToken caller,
            CancellationToken cancellationToken)
        {
            var timeout = slot;
            if (timeout is null || caller != cancellationToken || !timeout.TryReset())
            {
                timeout?.Dispose();
                timeout = cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : new CancellationTokenSource();
                slot = timeout;
                caller = cancellationToken;
            }

            timeout.CancelAfter(_ioProgressTimeout);
            return timeout;
        }
    }
}
