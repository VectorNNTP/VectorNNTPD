using System.Buffers;
using System.Diagnostics;

namespace VectorNNTPD.ReaderService.Networking.Transport
{
    /// <summary>
    /// DIAGNOSTIC-ONLY per-connection aggregate of stream Read/Write/Flush and pipe wait times.
    /// </summary>
    /// <remarks>
    /// No per-operation objects. Samples are stored in preallocated <see cref="int"/> arrays
    /// (microseconds). Excess operations update counters only.
    /// </remarks>
    internal sealed class TransportIoSession
    {
        /// <summary>Maximum duration samples retained per series.</summary>
        public const int SampleCapacity = 32768;

        private readonly int[] _rxOpUs = new int[SampleCapacity];
        private readonly int[] _rxAwaitUs = new int[SampleCapacity];
        private readonly int[] _txOpUs = new int[SampleCapacity];
        private readonly int[] _txAwaitUs = new int[SampleCapacity];
        private readonly int[] _rxPipeFlushUs = new int[SampleCapacity];
        private readonly int[] _txPipeWaitUs = new int[SampleCapacity];
        private readonly int[] _flushUs = new int[SampleCapacity];

        private int _rxOpSamples;
        private int _txOpSamples;
        private int _rxPipeSamples;
        private int _txPipeSamples;
        private int _flushSamples;

        private long _rxOps;
        private long _txOps;
        private long _flushOps;
        private long _rxPipeFlushes;
        private long _txPipeWaits;
        private long _txReadShapes;
        private long _txReadEmpty;
        private long _txReadBytes;
        private long _txReadSegments;
        private int _txReadMinBytes = int.MaxValue;
        private int _txReadMaxBytes;
        private int _txReadMinSegments = int.MaxValue;
        private int _txReadMaxSegments;
        private int _txReadMinSegBytes = int.MaxValue;
        private int _txReadMaxSegBytes;

        private long _rxBytes;
        private long _txBytes;
        private long _rxRequested;
        private long _txRequested;

        private int _rxMinBytes = int.MaxValue;
        private int _rxMaxBytes;
        private int _txMinBytes = int.MaxValue;
        private int _txMaxBytes;

        private long _rxSync;
        private long _rxAsync;
        private long _txSync;
        private long _txAsync;
        private long _flushSync;
        private long _flushAsync;
        private long _rxPipeSync;
        private long _rxPipeAsync;
        private long _txPipeSync;
        private long _txPipeAsync;

        private long _firstRxTs;
        private long _lastRxTs;
        private long _firstTxTs;
        private long _lastTxTs;
        private readonly long _createdTs = Stopwatch.GetTimestamp();

        private readonly int[] _readToFirstWriteUs = new int[SampleCapacity];
        private readonly int[] _betweenWritesUs = new int[SampleCapacity];
        private readonly int[] _lastWriteToAdvanceUs = new int[SampleCapacity];
        private readonly int[] _advanceUs = new int[SampleCapacity];
        private readonly int[] _advanceToNextReadUs = new int[SampleCapacity];
        private readonly int[] _transportWriteUs = new int[SampleCapacity];
        private readonly int[] _copyUs = new int[SampleCapacity];

        private int _readToFirstWriteSamples;
        private int _betweenWriteSamples;
        private int _lastWriteToAdvanceSamples;
        private int _advanceSamples;
        private int _advanceToNextReadSamples;
        private int _transportWriteSamples;
        private int _copySamples;

        private long _sendLoopStartTs;
        private long _sendLoopEndTs;
        private long _readToFirstWriteTicks;
        private long _betweenWriteTicks;
        private long _lastWriteToAdvanceTicks;
        private long _advanceTicks;
        private long _advanceToNextReadTicks;
        private long _transportWriteTicks;
        private long _copyTicks;
        private long _advanceCount;
        private long _betweenWriteCount;
        private long _transportWriteCount;
        private long _copyCount;
        private long _copyBytes;

        /// <summary>TX output-pipe pause threshold actually used by this connection.</summary>
        public long TxPipePauseBytes { get; set; } = NntpPipeOptions.PauseWriterThreshold;

        /// <summary>TX output-pipe resume threshold actually used by this connection.</summary>
        public long TxPipeResumeBytes { get; set; } = NntpPipeOptions.ResumeWriterThreshold;

        /// <summary>DIAGNOSTIC write-aggregate target, or 0 when the experiment is inactive.</summary>
        public int WriteGranularityTargetBytes { get; set; }

        /// <summary>Records one stream <c>ReadAsync</c>.</summary>
        public void RecordReceive(int requested, int received, long startedTs, long awaitStartTs, bool sync)
        {
            var endTs = Stopwatch.GetTimestamp();
            var opUs = ToUs(startedTs, endTs);
            var awaitUs = sync ? 0 : ToUs(awaitStartTs, endTs);
            Interlocked.Increment(ref _rxOps);
            Interlocked.Add(ref _rxBytes, received);
            Interlocked.Add(ref _rxRequested, requested);
            UpdateMin(ref _rxMinBytes, received);
            UpdateMax(ref _rxMaxBytes, received);
            if (sync)
            {
                Interlocked.Increment(ref _rxSync);
            }
            else
            {
                Interlocked.Increment(ref _rxAsync);
            }

            StampWindow(ref _firstRxTs, ref _lastRxTs, startedTs, endTs);
            var index = Interlocked.Increment(ref _rxOpSamples) - 1;
            if ((uint)index < SampleCapacity)
            {
                _rxOpUs[index] = opUs;
                _rxAwaitUs[index] = awaitUs;
            }
        }

        /// <summary>Records one stream <c>WriteAsync</c>.</summary>
        public void RecordSend(int requested, int sent, long startedTs, long awaitStartTs, bool sync)
        {
            var endTs = Stopwatch.GetTimestamp();
            var opUs = ToUs(startedTs, endTs);
            var awaitUs = sync ? 0 : ToUs(awaitStartTs, endTs);
            Interlocked.Increment(ref _txOps);
            Interlocked.Add(ref _txBytes, sent);
            Interlocked.Add(ref _txRequested, requested);
            UpdateMin(ref _txMinBytes, sent);
            UpdateMax(ref _txMaxBytes, sent);
            if (sync)
            {
                Interlocked.Increment(ref _txSync);
            }
            else
            {
                Interlocked.Increment(ref _txAsync);
            }

            StampWindow(ref _firstTxTs, ref _lastTxTs, startedTs, endTs);
            var index = Interlocked.Increment(ref _txOpSamples) - 1;
            if ((uint)index < SampleCapacity)
            {
                _txOpUs[index] = opUs;
                _txAwaitUs[index] = awaitUs;
            }
        }

        /// <summary>Records one stream <c>FlushAsync</c> (not mixed into send stats).</summary>
        public void RecordFlush(long startedTs, long awaitStartTs, bool sync)
        {
            var endTs = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _flushOps);
            if (sync)
            {
                Interlocked.Increment(ref _flushSync);
            }
            else
            {
                Interlocked.Increment(ref _flushAsync);
            }

            var index = Interlocked.Increment(ref _flushSamples) - 1;
            if ((uint)index < SampleCapacity)
            {
                _flushUs[index] = ToUs(startedTs, endTs);
                _ = awaitStartTs;
            }
        }

        /// <summary>Records one application-input <c>PipeWriter.FlushAsync</c> (RX backpressure).</summary>
        public void RecordRxPipeFlush(long startedTs, long awaitStartTs, bool sync)
        {
            var endTs = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _rxPipeFlushes);
            if (sync)
            {
                Interlocked.Increment(ref _rxPipeSync);
            }
            else
            {
                Interlocked.Increment(ref _rxPipeAsync);
            }

            var index = Interlocked.Increment(ref _rxPipeSamples) - 1;
            if ((uint)index < SampleCapacity)
            {
                _rxPipeFlushUs[index] = ToUs(startedTs, endTs);
                _ = awaitStartTs;
            }
        }

        /// <summary>
        /// Records one application-output <c>PipeReader.ReadAsync</c>.
        /// Includes waiting for the producer; this is not socket time.
        /// </summary>
        public void RecordTxPipeWait(long startedTs, long awaitStartTs, bool sync)
        {
            var endTs = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _txPipeWaits);
            if (sync)
            {
                Interlocked.Increment(ref _txPipeSync);
            }
            else
            {
                Interlocked.Increment(ref _txPipeAsync);
            }

            var index = Interlocked.Increment(ref _txPipeSamples) - 1;
            if ((uint)index < SampleCapacity)
            {
                _txPipeWaitUs[index] = ToUs(startedTs, endTs);
                _ = awaitStartTs;
            }
        }

        /// <summary>
        /// Records the shape of one non-empty TX <see cref="ReadOnlySequence{T}"/> from <c>PipeReader.ReadAsync</c>.
        /// Empty buffers are counted separately and do not affect averages.
        /// </summary>
        public void RecordTxReadShape(in ReadOnlySequence<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                Interlocked.Increment(ref _txReadEmpty);
                return;
            }

            var bytesLong = buffer.Length;
            var bytes = bytesLong > int.MaxValue ? int.MaxValue : (int)bytesLong;
            var segments = 0;
            var minSeg = int.MaxValue;
            var maxSeg = 0;
            foreach (var memory in buffer)
            {
                var length = memory.Length;
                if (length == 0)
                {
                    continue;
                }

                segments++;
                if (length < minSeg)
                {
                    minSeg = length;
                }

                if (length > maxSeg)
                {
                    maxSeg = length;
                }
            }

            Interlocked.Increment(ref _txReadShapes);
            Interlocked.Add(ref _txReadBytes, bytesLong);
            Interlocked.Add(ref _txReadSegments, segments);
            UpdateMin(ref _txReadMinBytes, bytes);
            UpdateMax(ref _txReadMaxBytes, bytes);
            UpdateMin(ref _txReadMinSegments, segments);
            UpdateMax(ref _txReadMaxSegments, segments);
            if (segments > 0)
            {
                UpdateMin(ref _txReadMinSegBytes, minSeg);
                UpdateMax(ref _txReadMaxSegBytes, maxSeg);
            }
        }

        /// <summary>Marks <c>NntpConnection.SendAsync</c> entry (monotonic).</summary>
        public void RecordSendLoopStart() =>
            Interlocked.CompareExchange(ref _sendLoopStartTs, Stopwatch.GetTimestamp(), 0);

        /// <summary>Marks <c>NntpConnection.SendAsync</c> exit after the read loop.</summary>
        public void RecordSendLoopEnd() =>
            Volatile.Write(ref _sendLoopEndTs, Stopwatch.GetTimestamp());

        /// <summary>ReadAsync completion → first transport WriteAsync of that iteration.</summary>
        public void RecordReadToFirstWrite(long readDoneTs, long firstWriteTs) =>
            RecordSpan(
                readDoneTs,
                firstWriteTs,
                ref _readToFirstWriteTicks,
                ref _readToFirstWriteSamples,
                _readToFirstWriteUs);

        /// <summary>Gap between consecutive transport WriteAsync calls in one ReadResult.</summary>
        public void RecordBetweenWrites(long previousWriteEndTs, long nextWriteStartTs)
        {
            Interlocked.Increment(ref _betweenWriteCount);
            RecordSpan(
                previousWriteEndTs,
                nextWriteStartTs,
                ref _betweenWriteTicks,
                ref _betweenWriteSamples,
                _betweenWritesUs);
        }

        /// <summary>Last WriteAsync completion → AdvanceTo (includes FlushAsync in the current loop).</summary>
        public void RecordLastWriteToAdvance(long lastWriteEndTs, long advanceStartTs) =>
            RecordSpan(
                lastWriteEndTs,
                advanceStartTs,
                ref _lastWriteToAdvanceTicks,
                ref _lastWriteToAdvanceSamples,
                _lastWriteToAdvanceUs);

        /// <summary>Times one <c>PipeReader.AdvanceTo</c>.</summary>
        public void RecordAdvanceTo(long startedTs, long endedTs)
        {
            Interlocked.Increment(ref _advanceCount);
            RecordSpan(startedTs, endedTs, ref _advanceTicks, ref _advanceSamples, _advanceUs);
        }

        /// <summary>AdvanceTo return → next ReadAsync issued.</summary>
        public void RecordAdvanceToNextRead(long advanceEndTs, long nextReadIssuedTs) =>
            RecordSpan(
                advanceEndTs,
                nextReadIssuedTs,
                ref _advanceToNextReadTicks,
                ref _advanceToNextReadSamples,
                _advanceToNextReadUs);

        /// <summary>Times one <c>ConnectionByteTransport.WriteAsync</c> (admission + stream write).</summary>
        public void RecordTransportWrite(long startedTs, long endedTs)
        {
            Interlocked.Increment(ref _transportWriteCount);
            RecordSpan(startedTs, endedTs, ref _transportWriteTicks, ref _transportWriteSamples, _transportWriteUs);
        }

        /// <summary>Times one copy into the diagnostic write-aggregate buffer.</summary>
        public void RecordCopy(int bytes, long startedTs, long endedTs)
        {
            if (bytes < 0)
            {
                return;
            }

            Interlocked.Increment(ref _copyCount);
            Interlocked.Add(ref _copyBytes, bytes);
            RecordSpan(startedTs, endedTs, ref _copyTicks, ref _copySamples, _copyUs);
        }

        /// <summary>Snapshots counters and duration samples for a report.</summary>
        public TransportIoSnapshot Snapshot()
        {
            var now = Stopwatch.GetTimestamp();
            return new TransportIoSnapshot(
                ConnectionElapsed: Stopwatch.GetElapsedTime(_createdTs, now),
                RxWindow: Window(_firstRxTs, _lastRxTs),
                TxWindow: Window(_firstTxTs, _lastTxTs),
                Rx: Direction(
                    Volatile.Read(ref _rxOps),
                    Volatile.Read(ref _rxBytes),
                    Volatile.Read(ref _rxRequested),
                    Volatile.Read(ref _rxMinBytes),
                    Volatile.Read(ref _rxMaxBytes),
                    Volatile.Read(ref _rxSync),
                    Volatile.Read(ref _rxAsync),
                    CopySamples(_rxOpUs, Volatile.Read(ref _rxOpSamples)),
                    CopySamples(_rxAwaitUs, Volatile.Read(ref _rxOpSamples))),
                Tx: Direction(
                    Volatile.Read(ref _txOps),
                    Volatile.Read(ref _txBytes),
                    Volatile.Read(ref _txRequested),
                    Volatile.Read(ref _txMinBytes),
                    Volatile.Read(ref _txMaxBytes),
                    Volatile.Read(ref _txSync),
                    Volatile.Read(ref _txAsync),
                    CopySamples(_txOpUs, Volatile.Read(ref _txOpSamples)),
                    CopySamples(_txAwaitUs, Volatile.Read(ref _txOpSamples))),
                FlushOps: Volatile.Read(ref _flushOps),
                FlushSync: Volatile.Read(ref _flushSync),
                FlushAsync: Volatile.Read(ref _flushAsync),
                FlushUs: CopySamples(_flushUs, Volatile.Read(ref _flushSamples)),
                RxPipeFlushes: Volatile.Read(ref _rxPipeFlushes),
                RxPipeSync: Volatile.Read(ref _rxPipeSync),
                RxPipeAsync: Volatile.Read(ref _rxPipeAsync),
                RxPipeFlushUs: CopySamples(_rxPipeFlushUs, Volatile.Read(ref _rxPipeSamples)),
                TxPipeWaits: Volatile.Read(ref _txPipeWaits),
                TxPipeSync: Volatile.Read(ref _txPipeSync),
                TxPipeAsync: Volatile.Read(ref _txPipeAsync),
                TxPipeWaitUs: CopySamples(_txPipeWaitUs, Volatile.Read(ref _txPipeSamples)),
                TxPipePauseBytes: TxPipePauseBytes,
                TxPipeResumeBytes: TxPipeResumeBytes,
                TxRead: new TransportIoReadShape(
                    Volatile.Read(ref _txReadShapes),
                    Volatile.Read(ref _txReadEmpty),
                    Volatile.Read(ref _txReadBytes),
                    Volatile.Read(ref _txReadSegments),
                    MinOrZero(_txReadMinBytes),
                    Volatile.Read(ref _txReadMaxBytes),
                    MinOrZero(_txReadMinSegments),
                    Volatile.Read(ref _txReadMaxSegments),
                    MinOrZero(_txReadMinSegBytes),
                    Volatile.Read(ref _txReadMaxSegBytes)),
                Loop: new TransportIoTxLoop(
                    SendAsyncElapsed: Window(
                        Volatile.Read(ref _sendLoopStartTs),
                        EndOrNow(Volatile.Read(ref _sendLoopEndTs), now)),
                    ReadToFirstWrite: TicksToTimeSpan(Volatile.Read(ref _readToFirstWriteTicks)),
                    BetweenWrites: TicksToTimeSpan(Volatile.Read(ref _betweenWriteTicks)),
                    LastWriteToAdvance: TicksToTimeSpan(Volatile.Read(ref _lastWriteToAdvanceTicks)),
                    AdvanceTo: TicksToTimeSpan(Volatile.Read(ref _advanceTicks)),
                    AdvanceToNextRead: TicksToTimeSpan(Volatile.Read(ref _advanceToNextReadTicks)),
                    TransportWrite: TicksToTimeSpan(Volatile.Read(ref _transportWriteTicks)),
                    AdvanceCount: Volatile.Read(ref _advanceCount),
                    BetweenWriteCount: Volatile.Read(ref _betweenWriteCount),
                    TransportWriteCount: Volatile.Read(ref _transportWriteCount),
                    ReadToFirstWriteUs: CopySamples(_readToFirstWriteUs, Volatile.Read(ref _readToFirstWriteSamples)),
                    BetweenWriteUs: CopySamples(_betweenWritesUs, Volatile.Read(ref _betweenWriteSamples)),
                    LastWriteToAdvanceUs: CopySamples(_lastWriteToAdvanceUs, Volatile.Read(ref _lastWriteToAdvanceSamples)),
                    AdvanceUs: CopySamples(_advanceUs, Volatile.Read(ref _advanceSamples)),
                    AdvanceToNextReadUs: CopySamples(_advanceToNextReadUs, Volatile.Read(ref _advanceToNextReadSamples)),
                    TransportWriteUs: CopySamples(_transportWriteUs, Volatile.Read(ref _transportWriteSamples))),
                Coalesce: new TransportIoWriteCoalesce(
                    TargetBytes: WriteGranularityTargetBytes,
                    CopyCount: Volatile.Read(ref _copyCount),
                    CopyBytes: Volatile.Read(ref _copyBytes),
                    CopyTime: TicksToTimeSpan(Volatile.Read(ref _copyTicks)),
                    CopyUs: CopySamples(_copyUs, Volatile.Read(ref _copySamples))));
        }

        /// <summary>Writes the connection dump and returns the path.</summary>
        public string Write(string directory, string sessionLabel)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionLabel);
            Directory.CreateDirectory(directory);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var safe = Sanitize(sessionLabel);
            var path = Path.Join(directory, $"{stamp}-{safe}.txt");
            File.WriteAllText(path, TransportIoReport.Format(Snapshot(), sessionLabel), System.Text.Encoding.UTF8);
            return path;
        }

        private static TransportIoDirection Direction(
            long ops,
            long bytes,
            long requested,
            int minBytes,
            int maxBytes,
            long sync,
            long async,
            int[] opUs,
            int[] awaitUs) =>
            new(
                ops,
                bytes,
                requested,
                MinOrZero(minBytes),
                maxBytes,
                sync,
                async,
                opUs,
                awaitUs);

        private static int MinOrZero(int value) => value == int.MaxValue ? 0 : value;

        private static void RecordSpan(long start, long end, ref long ticks, ref int samples, int[] us)
        {
            if (start <= 0 || end < start)
            {
                return;
            }

            Interlocked.Add(ref ticks, end - start);
            var index = Interlocked.Increment(ref samples) - 1;
            if ((uint)index < SampleCapacity)
            {
                us[index] = ToUs(start, end);
            }
        }

        private static TimeSpan TicksToTimeSpan(long ticks) =>
            ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);

        private static long EndOrNow(long end, long now) => end > 0 ? end : now;

        private static TimeSpan Window(long first, long last) =>
            first > 0 && last >= first ? Stopwatch.GetElapsedTime(first, last) : TimeSpan.Zero;

        private static void StampWindow(ref long first, ref long last, long start, long end)
        {
            if (Interlocked.CompareExchange(ref first, start, 0) == 0)
            {
                // first write won
            }

            long current;
            do
            {
                current = Volatile.Read(ref last);
                if (end <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref last, end, current) != current);
        }

        private static int[] CopySamples(int[] source, int written)
        {
            var count = Math.Min(Math.Max(written, 0), SampleCapacity);
            if (count == 0)
            {
                return [];
            }

            var copy = new int[count];
            Array.Copy(source, copy, count);
            return copy;
        }

        private static void UpdateMin(ref int field, int value)
        {
            var current = Volatile.Read(ref field);
            while (value < current)
            {
                var original = Interlocked.CompareExchange(ref field, value, current);
                if (original == current)
                {
                    return;
                }

                current = original;
            }
        }

        private static void UpdateMax(ref int field, int value)
        {
            var current = Volatile.Read(ref field);
            while (value > current)
            {
                var original = Interlocked.CompareExchange(ref field, value, current);
                if (original == current)
                {
                    return;
                }

                current = original;
            }
        }

        private static int ToUs(long start, long end)
        {
            if (start <= 0 || end < start)
            {
                return 0;
            }

            var us = Stopwatch.GetElapsedTime(start, end).TotalMicroseconds;
            if (us >= int.MaxValue)
            {
                return int.MaxValue;
            }

            if (us <= 0)
            {
                return 0;
            }

            return (int)us;
        }

        private static string Sanitize(string value)
        {
            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (!char.IsAsciiLetterOrDigit(chars[i]) && chars[i] is not '-' and not '_')
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }
    }

    /// <summary>Immutable counters and duration samples for one connection.</summary>
    internal readonly record struct TransportIoSnapshot(
        TimeSpan ConnectionElapsed,
        TimeSpan RxWindow,
        TimeSpan TxWindow,
        TransportIoDirection Rx,
        TransportIoDirection Tx,
        long FlushOps,
        long FlushSync,
        long FlushAsync,
        int[] FlushUs,
        long RxPipeFlushes,
        long RxPipeSync,
        long RxPipeAsync,
        int[] RxPipeFlushUs,
        long TxPipeWaits,
        long TxPipeSync,
        long TxPipeAsync,
        int[] TxPipeWaitUs,
        long TxPipePauseBytes,
        long TxPipeResumeBytes,
        TransportIoReadShape TxRead,
        TransportIoTxLoop Loop,
        TransportIoWriteCoalesce Coalesce);

    /// <summary>DIAGNOSTIC write-granularity copy counters (when the experiment is active).</summary>
    internal readonly record struct TransportIoWriteCoalesce(
        int TargetBytes,
        long CopyCount,
        long CopyBytes,
        TimeSpan CopyTime,
        int[] CopyUs);

    /// <summary>Accumulated <c>NntpConnection.SendAsync</c> loop-stage timings (when probe enabled).</summary>
    internal readonly record struct TransportIoTxLoop(
        TimeSpan SendAsyncElapsed,
        TimeSpan ReadToFirstWrite,
        TimeSpan BetweenWrites,
        TimeSpan LastWriteToAdvance,
        TimeSpan AdvanceTo,
        TimeSpan AdvanceToNextRead,
        TimeSpan TransportWrite,
        long AdvanceCount,
        long BetweenWriteCount,
        long TransportWriteCount,
        int[] ReadToFirstWriteUs,
        int[] BetweenWriteUs,
        int[] LastWriteToAdvanceUs,
        int[] AdvanceUs,
        int[] AdvanceToNextReadUs,
        int[] TransportWriteUs);

    /// <summary>Shape of TX <c>PipeReader.ReadAsync</c> buffers (non-empty reads).</summary>
    internal readonly record struct TransportIoReadShape(
        long Reads,
        long EmptyReads,
        long Bytes,
        long Segments,
        int MinBytes,
        int MaxBytes,
        int MinSegments,
        int MaxSegments,
        int MinSegmentBytes,
        int MaxSegmentBytes);

    /// <summary>One direction of stream I/O (RX read or TX write).</summary>
    internal readonly record struct TransportIoDirection(
        long Ops,
        long Bytes,
        long Requested,
        int MinBytes,
        int MaxBytes,
        long Sync,
        long Async,
        int[] OpUs,
        int[] AwaitUs);
}
