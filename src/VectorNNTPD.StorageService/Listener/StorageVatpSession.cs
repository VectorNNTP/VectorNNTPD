using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Transport.ArticleTransfer;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;

namespace VectorNNTPD.StorageService.Listener
{

    /// <summary>
    /// VATP server session: client HELLO, then OPEN served from
    /// <see cref="IStorageArticleOpenBoundary"/> as META, credit-paced DATA/FIN, and END.
    /// STORE is a separate receive stream. After META matches the inbound bytes, the original
    /// article is enqueued. Path is not rewritten here. Retrieval does not rewrite Path.
    /// </summary>
    public sealed partial class StorageVatpSession : IAsyncDisposable
    {
        /// <summary>Local receive-stream correlation. Not a wire field and not a storage key.</summary>
        private static readonly Guid StoreReceiveCorrelation = new("5f5f5f5f-5f5f-5f5f-5f5f-5f5f5f5f5f5f");

        private static long _enqueueToResultTicks;
        private static long _enqueueToResultCount;
        private static long _windowWriteTicks;
        private static long _windowWriteCount;
        private static long _readWaitTicks;
        private static long _readCount;
        private static long _readBytes;
        private static long _readLt4K;
        private static long _readLt16K;
        private static long _readLt32K;
        private static long _readLt64K;
        private static long _readGe64K;
        private static long _readDurLt20Us;
        private static long _readDurLt50Us;
        private static long _readDurLt100Us;
        private static long _readDurLt500Us;
        private static long _readDurGe500Us;
        private static long _incompleteParses;
        private static long _framesParsed;
        private static long _processTicks;
        private static long _writeGateTicks;
        private static long _dataAcceptTicks;
        private static long _dataAcceptCount;
        private static long _dataCopyBytes;
        private static long _endValidateTicks;
        private static long _endValidateCount;
        private static long _readExact16K;
        private static long _framesThisRead0;
        private static long _framesThisRead1;
        private static long _framesThisReadMany;
        private static long _readsToFrame1;
        private static long _readsToFrame2;
        private static long _readsToFrame3;
        private static long _readsToFrame4;
        private static long _readsToFrame5Plus;
        private static long _partialAtRead0;
        private static long _partialAtReadHeader;
        private static long _partialAtReadLt16K;
        private static long _partialAtReadLt64K;
        private static long _partialAtReadGe64K;
        private static long _parseTicks;
        private static long _compactCount;
        private static long _compactBytes;
        private static long _compactTicks;
        private static int _clientWriteActive;
        private static long _clientWriteEndedAt;
        private static int _serverIoPhase;
        private static int _clientWriteServerPhase;
        private static long _readDuringWriteTicks;
        private static long _readDuringWriteCount;
        private static long _readAfterWriteTicks;
        private static long _readAfterWriteCount;
        private static long _readGapLt20Us;
        private static long _readGapLt100Us;
        private static long _readGapLt1Ms;
        private static long _readGapGe1Ms;
        private static long _clientWriteDuringReadTicks;
        private static long _clientWriteDuringReadCount;
        private static long _clientWriteDuringServerWriteTicks;
        private static long _clientWriteDuringServerWriteCount;
        private static long _clientWriteDuringOtherTicks;
        private static long _clientWriteDuringOtherCount;
        private static long _readyBehindPositive;
        private static long _readyBehindSamples;
        private static long _readyBehindSum;
        private static long _sendGapLt20Us;
        private static long _sendGapLt100Us;
        private static long _sendGapLt1Ms;
        private static long _sendGapGe1Ms;
        private static long _sessionCpu100Ns;

        /// <summary>Clears session path timing counters. Does not change acceptance.</summary>
        internal static void ResetEnqueueToResultTiming()
        {
            Interlocked.Exchange(ref _enqueueToResultTicks, 0);
            Interlocked.Exchange(ref _enqueueToResultCount, 0);
            Interlocked.Exchange(ref _windowWriteTicks, 0);
            Interlocked.Exchange(ref _windowWriteCount, 0);
            Interlocked.Exchange(ref _readWaitTicks, 0);
            Interlocked.Exchange(ref _readCount, 0);
            Interlocked.Exchange(ref _readBytes, 0);
            Interlocked.Exchange(ref _readLt4K, 0);
            Interlocked.Exchange(ref _readLt16K, 0);
            Interlocked.Exchange(ref _readLt32K, 0);
            Interlocked.Exchange(ref _readLt64K, 0);
            Interlocked.Exchange(ref _readGe64K, 0);
            Interlocked.Exchange(ref _readDurLt20Us, 0);
            Interlocked.Exchange(ref _readDurLt50Us, 0);
            Interlocked.Exchange(ref _readDurLt100Us, 0);
            Interlocked.Exchange(ref _readDurLt500Us, 0);
            Interlocked.Exchange(ref _readDurGe500Us, 0);
            Interlocked.Exchange(ref _incompleteParses, 0);
            Interlocked.Exchange(ref _framesParsed, 0);
            Interlocked.Exchange(ref _processTicks, 0);
            Interlocked.Exchange(ref _writeGateTicks, 0);
            Interlocked.Exchange(ref _dataAcceptTicks, 0);
            Interlocked.Exchange(ref _dataAcceptCount, 0);
            Interlocked.Exchange(ref _dataCopyBytes, 0);
            Interlocked.Exchange(ref _endValidateTicks, 0);
            Interlocked.Exchange(ref _endValidateCount, 0);
            Interlocked.Exchange(ref _readExact16K, 0);
            Interlocked.Exchange(ref _framesThisRead0, 0);
            Interlocked.Exchange(ref _framesThisRead1, 0);
            Interlocked.Exchange(ref _framesThisReadMany, 0);
            Interlocked.Exchange(ref _readsToFrame1, 0);
            Interlocked.Exchange(ref _readsToFrame2, 0);
            Interlocked.Exchange(ref _readsToFrame3, 0);
            Interlocked.Exchange(ref _readsToFrame4, 0);
            Interlocked.Exchange(ref _readsToFrame5Plus, 0);
            Interlocked.Exchange(ref _partialAtRead0, 0);
            Interlocked.Exchange(ref _partialAtReadHeader, 0);
            Interlocked.Exchange(ref _partialAtReadLt16K, 0);
            Interlocked.Exchange(ref _partialAtReadLt64K, 0);
            Interlocked.Exchange(ref _partialAtReadGe64K, 0);
            Interlocked.Exchange(ref _parseTicks, 0);
            Interlocked.Exchange(ref _compactCount, 0);
            Interlocked.Exchange(ref _compactBytes, 0);
            Interlocked.Exchange(ref _compactTicks, 0);
            Volatile.Write(ref _clientWriteActive, 0);
            Volatile.Write(ref _clientWriteEndedAt, 0);
            Volatile.Write(ref _serverIoPhase, 0);
            Volatile.Write(ref _clientWriteServerPhase, 0);
            Interlocked.Exchange(ref _readDuringWriteTicks, 0);
            Interlocked.Exchange(ref _readDuringWriteCount, 0);
            Interlocked.Exchange(ref _readAfterWriteTicks, 0);
            Interlocked.Exchange(ref _readAfterWriteCount, 0);
            Interlocked.Exchange(ref _readGapLt20Us, 0);
            Interlocked.Exchange(ref _readGapLt100Us, 0);
            Interlocked.Exchange(ref _readGapLt1Ms, 0);
            Interlocked.Exchange(ref _readGapGe1Ms, 0);
            Interlocked.Exchange(ref _clientWriteDuringReadTicks, 0);
            Interlocked.Exchange(ref _clientWriteDuringReadCount, 0);
            Interlocked.Exchange(ref _clientWriteDuringServerWriteTicks, 0);
            Interlocked.Exchange(ref _clientWriteDuringServerWriteCount, 0);
            Interlocked.Exchange(ref _clientWriteDuringOtherTicks, 0);
            Interlocked.Exchange(ref _clientWriteDuringOtherCount, 0);
            Interlocked.Exchange(ref _readyBehindPositive, 0);
            Interlocked.Exchange(ref _readyBehindSamples, 0);
            Interlocked.Exchange(ref _readyBehindSum, 0);
            Interlocked.Exchange(ref _sendGapLt20Us, 0);
            Interlocked.Exchange(ref _sendGapLt100Us, 0);
            Interlocked.Exchange(ref _sendGapLt1Ms, 0);
            Interlocked.Exchange(ref _sendGapGe1Ms, 0);
            Interlocked.Exchange(ref _sessionCpu100Ns, 0);
            StreamStorageVatpTransport.ResetIoSplit();
        }

        /// <summary>Gets how long successful RESULT writes took after <c>TryEnqueue</c> returned true.</summary>
        internal static (long Count, double Seconds) ReadEnqueueToResultTiming()
        {
            var count = Volatile.Read(ref _enqueueToResultCount);
            var ticks = Volatile.Read(ref _enqueueToResultTicks);
            return (count, ticks / (double)Stopwatch.Frequency);
        }

        /// <summary>Gets time spent copying STORE DATA, writing WINDOW credit, waiting to read, and validating END.</summary>
        internal static (
            long DataCount,
            double DataSeconds,
            long WindowCount,
            double WindowSeconds,
            double ReadWaitSeconds,
            long EndCount,
            double EndSeconds) ReadReceivePathTiming()
        {
            return (
                Volatile.Read(ref _dataAcceptCount),
                Volatile.Read(ref _dataAcceptTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _windowWriteCount),
                Volatile.Read(ref _windowWriteTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _readWaitTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _endValidateCount),
                Volatile.Read(ref _endValidateTicks) / (double)Stopwatch.Frequency);
        }

        /// <summary>
        /// Gets read sizes and the time between reads. Process time includes WINDOW and RESULT writes
        /// and does not overlap <see cref="ReadReceivePathTiming"/> read-wait time.
        /// </summary>
        internal static ReceiveObservation ReadReceiveObservation()
        {
            return new ReceiveObservation(
                Volatile.Read(ref _readCount),
                Volatile.Read(ref _readBytes),
                Volatile.Read(ref _readLt4K),
                Volatile.Read(ref _readLt16K),
                Volatile.Read(ref _readLt32K),
                Volatile.Read(ref _readLt64K),
                Volatile.Read(ref _readGe64K),
                Volatile.Read(ref _readDurLt20Us),
                Volatile.Read(ref _readDurLt50Us),
                Volatile.Read(ref _readDurLt100Us),
                Volatile.Read(ref _readDurLt500Us),
                Volatile.Read(ref _readDurGe500Us),
                Volatile.Read(ref _incompleteParses),
                Volatile.Read(ref _framesParsed),
                Volatile.Read(ref _processTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _writeGateTicks) / (double)Stopwatch.Frequency);
        }

        /// <summary>
        /// Gets frame-assembly counters. Parse and compact times sit inside process time and do not overlap read-wait.
        /// Timeout setup and stream-read times sit inside read-wait and do not overlap each other.
        /// </summary>
        internal static AssemblyObservation ReadAssemblyObservation()
        {
            var (timeoutSeconds, streamReadSeconds) = StreamStorageVatpTransport.ReadIoSplit();
            return new AssemblyObservation(
                Volatile.Read(ref _readExact16K),
                Volatile.Read(ref _framesThisRead0),
                Volatile.Read(ref _framesThisRead1),
                Volatile.Read(ref _framesThisReadMany),
                Volatile.Read(ref _readsToFrame1),
                Volatile.Read(ref _readsToFrame2),
                Volatile.Read(ref _readsToFrame3),
                Volatile.Read(ref _readsToFrame4),
                Volatile.Read(ref _readsToFrame5Plus),
                Volatile.Read(ref _partialAtRead0),
                Volatile.Read(ref _partialAtReadHeader),
                Volatile.Read(ref _partialAtReadLt16K),
                Volatile.Read(ref _partialAtReadLt64K),
                Volatile.Read(ref _partialAtReadGe64K),
                Volatile.Read(ref _parseTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _compactCount),
                Volatile.Read(ref _compactBytes),
                Volatile.Read(ref _compactTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _dataCopyBytes),
                timeoutSeconds,
                streamReadSeconds);
        }

        /// <summary>One level's frame-assembly counters. Times are elapsed and do not overlap.</summary>
        internal readonly record struct AssemblyObservation(
            long ReadExact16K,
            long FramesThisRead0,
            long FramesThisRead1,
            long FramesThisReadMany,
            long ReadsToFrame1,
            long ReadsToFrame2,
            long ReadsToFrame3,
            long ReadsToFrame4,
            long ReadsToFrame5Plus,
            long PartialAtRead0,
            long PartialAtReadHeader,
            long PartialAtReadLt16K,
            long PartialAtReadLt64K,
            long PartialAtReadGe64K,
            double ParseSeconds,
            long CompactCount,
            long CompactBytes,
            double CompactSeconds,
            long DataCopyBytes,
            double TimeoutSetupSeconds,
            double StreamReadSeconds);

        /// <summary>
        /// Correlates client writes with server reads on <see cref="Stopwatch.GetTimestamp"/>.
        /// Both sides run in this process, so the clock is the same. Intervals do not overlap within one side.
        /// </summary>
        internal static PaceObservation ReadPaceObservation()
        {
            return new PaceObservation(
                Volatile.Read(ref _readDuringWriteTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _readDuringWriteCount),
                Volatile.Read(ref _readAfterWriteTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _readAfterWriteCount),
                Volatile.Read(ref _readGapLt20Us),
                Volatile.Read(ref _readGapLt100Us),
                Volatile.Read(ref _readGapLt1Ms),
                Volatile.Read(ref _readGapGe1Ms),
                Volatile.Read(ref _clientWriteDuringReadTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _clientWriteDuringReadCount),
                Volatile.Read(ref _clientWriteDuringServerWriteTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _clientWriteDuringServerWriteCount),
                Volatile.Read(ref _clientWriteDuringOtherTicks) / (double)Stopwatch.Frequency,
                Volatile.Read(ref _clientWriteDuringOtherCount),
                Volatile.Read(ref _readyBehindPositive),
                Volatile.Read(ref _readyBehindSamples),
                Volatile.Read(ref _readyBehindSum),
                Volatile.Read(ref _sendGapLt20Us),
                Volatile.Read(ref _sendGapLt100Us),
                Volatile.Read(ref _sendGapLt1Ms),
                Volatile.Read(ref _sendGapGe1Ms),
                Volatile.Read(ref _sessionCpu100Ns) / 10_000_000d);
        }

        /// <summary>
        /// Records that the benchmark client is about to enter <c>SslStream.WriteAsync</c>.
        /// <paramref name="readyBehind"/> is other STORE slots that already have credit and a frame.
        /// <paramref name="afterLoopWait"/> is true when the send loop blocked since the previous write.
        /// </summary>
        internal static void NoteClientWriteStart(int readyBehind, bool afterLoopWait)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Volatile.Read(ref _clientWriteEndedAt);
            if (previous != 0 && !afterLoopWait)
            {
                NoteSendGap(now - previous);
            }

            if (readyBehind > 0)
            {
                Interlocked.Increment(ref _readyBehindPositive);
            }

            Interlocked.Add(ref _readyBehindSum, readyBehind);
            Interlocked.Increment(ref _readyBehindSamples);
            Volatile.Write(ref _clientWriteServerPhase, Volatile.Read(ref _serverIoPhase));
            Volatile.Write(ref _clientWriteActive, 1);
        }

        /// <summary>Records that the benchmark client's TLS write returned. <paramref name="elapsedTicks"/> is that write only.</summary>
        internal static void NoteClientWriteEnd(long elapsedTicks)
        {
            var phase = Volatile.Read(ref _clientWriteServerPhase);
            Volatile.Write(ref _clientWriteActive, 0);
            Volatile.Write(ref _clientWriteEndedAt, Stopwatch.GetTimestamp());
            if (phase == 1)
            {
                Interlocked.Add(ref _clientWriteDuringReadTicks, elapsedTicks);
                Interlocked.Increment(ref _clientWriteDuringReadCount);
            }
            else if (phase == 2)
            {
                Interlocked.Add(ref _clientWriteDuringServerWriteTicks, elapsedTicks);
                Interlocked.Increment(ref _clientWriteDuringServerWriteCount);
            }
            else
            {
                Interlocked.Add(ref _clientWriteDuringOtherTicks, elapsedTicks);
                Interlocked.Increment(ref _clientWriteDuringOtherCount);
            }
        }

        /// <summary>
        /// One level of client/server overlap. Read-during-write and read-after-write partition server read-wait.
        /// Client-write buckets partition client TLS-write time by the server phase at write start.
        /// </summary>
        internal readonly record struct PaceObservation(
            double ReadDuringClientWriteSeconds,
            long ReadDuringClientWriteCount,
            double ReadAfterClientWriteSeconds,
            long ReadAfterClientWriteCount,
            long ReadGapLt20Us,
            long ReadGapLt100Us,
            long ReadGapLt1Ms,
            long ReadGapGe1Ms,
            double ClientWriteDuringServerReadSeconds,
            long ClientWriteDuringServerReadCount,
            double ClientWriteDuringServerWriteSeconds,
            long ClientWriteDuringServerWriteCount,
            double ClientWriteDuringOtherSeconds,
            long ClientWriteDuringOtherCount,
            long ReadyBehindPositive,
            long ReadyBehindSamples,
            long ReadyBehindSum,
            long SendGapLt20Us,
            long SendGapLt100Us,
            long SendGapLt1Ms,
            long SendGapGe1Ms,
            double SessionCpuSeconds);

        /// <summary>One level's receive-size counters. Times are not CPU time.</summary>
        internal readonly record struct ReceiveObservation(
            long Reads,
            long ReadBytes,
            long ReadLt4K,
            long ReadLt16K,
            long ReadLt32K,
            long ReadLt64K,
            long ReadGe64K,
            long ReadDurLt20Us,
            long ReadDurLt50Us,
            long ReadDurLt100Us,
            long ReadDurLt500Us,
            long ReadDurGe500Us,
            long IncompleteParses,
            long Frames,
            double ProcessSeconds,
            double WriteGateSeconds);

        /// <summary>Counts one completed transport read by size. Does not change the bytes.</summary>
        private static void NoteRead(int bytes)
        {
            Interlocked.Increment(ref _readCount);
            Interlocked.Add(ref _readBytes, bytes);
            if (bytes < 4 * 1024)
            {
                Interlocked.Increment(ref _readLt4K);
            }
            else if (bytes < 16 * 1024)
            {
                Interlocked.Increment(ref _readLt16K);
            }
            else if (bytes < 32 * 1024)
            {
                Interlocked.Increment(ref _readLt32K);
                if (bytes == 16 * 1024)
                {
                    Interlocked.Increment(ref _readExact16K);
                }
            }
            else if (bytes < 64 * 1024)
            {
                Interlocked.Increment(ref _readLt64K);
            }
            else
            {
                Interlocked.Increment(ref _readGe64K);
            }
        }

        /// <summary>Counts how long one read call took. This is elapsed time, not CPU time.</summary>
        private static void NoteReadDuration(long ticks)
        {
            var microseconds = ticks * 1_000_000d / Stopwatch.Frequency;
            if (microseconds < 20)
            {
                Interlocked.Increment(ref _readDurLt20Us);
            }
            else if (microseconds < 50)
            {
                Interlocked.Increment(ref _readDurLt50Us);
            }
            else if (microseconds < 100)
            {
                Interlocked.Increment(ref _readDurLt100Us);
            }
            else if (microseconds < 500)
            {
                Interlocked.Increment(ref _readDurLt500Us);
            }
            else
            {
                Interlocked.Increment(ref _readDurGe500Us);
            }
        }

        /// <summary>Counts bytes already retained when a transport read starts. Does not move those bytes.</summary>
        private static void NotePartialAtRead(int buffered)
        {
            if (buffered == 0)
            {
                Interlocked.Increment(ref _partialAtRead0);
            }
            else if (buffered < VatpProtocol.HeaderLengthBytes)
            {
                Interlocked.Increment(ref _partialAtReadHeader);
            }
            else if (buffered < 16 * 1024)
            {
                Interlocked.Increment(ref _partialAtReadLt16K);
            }
            else if (buffered < 64 * 1024)
            {
                Interlocked.Increment(ref _partialAtReadLt64K);
            }
            else
            {
                Interlocked.Increment(ref _partialAtReadGe64K);
            }
        }

        /// <summary>Counts complete frames decoded from one transport read.</summary>
        private static void NoteFramesThisRead(int frames)
        {
            if (frames == 0)
            {
                Interlocked.Increment(ref _framesThisRead0);
            }
            else if (frames == 1)
            {
                Interlocked.Increment(ref _framesThisRead1);
            }
            else
            {
                Interlocked.Increment(ref _framesThisReadMany);
            }
        }

        /// <summary>Counts transport reads from the previous complete frame through this one.</summary>
        private static void NoteReadsToFrame(int reads)
        {
            if (reads <= 1)
            {
                Interlocked.Increment(ref _readsToFrame1);
            }
            else if (reads == 2)
            {
                Interlocked.Increment(ref _readsToFrame2);
            }
            else if (reads == 3)
            {
                Interlocked.Increment(ref _readsToFrame3);
            }
            else if (reads == 4)
            {
                Interlocked.Increment(ref _readsToFrame4);
            }
            else
            {
                Interlocked.Increment(ref _readsToFrame5Plus);
            }
        }

        /// <summary>Process-wide read-gap buckets. The buckets are mutually exclusive.</summary>
        private static void NoteReadGap(long ticks)
        {
            var microseconds = ticks * 1_000_000d / Stopwatch.Frequency;
            if (microseconds < 20)
            {
                Interlocked.Increment(ref _readGapLt20Us);
            }
            else if (microseconds < 100)
            {
                Interlocked.Increment(ref _readGapLt100Us);
            }
            else if (microseconds < 1_000)
            {
                Interlocked.Increment(ref _readGapLt1Ms);
            }
            else
            {
                Interlocked.Increment(ref _readGapGe1Ms);
            }
        }

        /// <summary>Process-wide send-gap buckets used by the bench client write markers.</summary>
        private static void NoteSendGap(long ticks)
        {
            var microseconds = ticks * 1_000_000d / Stopwatch.Frequency;
            if (microseconds < 20)
            {
                Interlocked.Increment(ref _sendGapLt20Us);
            }
            else if (microseconds < 100)
            {
                Interlocked.Increment(ref _sendGapLt100Us);
            }
            else if (microseconds < 1_000)
            {
                Interlocked.Increment(ref _sendGapLt1Ms);
            }
            else
            {
                Interlocked.Increment(ref _sendGapGe1Ms);
            }
        }

        private static void AddProcessTicks(long ticks) => Interlocked.Add(ref _processTicks, ticks);

        private static void AddReadWaitTicks(long ticks) => Interlocked.Add(ref _readWaitTicks, ticks);

        private static void AddParseTicks(long ticks) => Interlocked.Add(ref _parseTicks, ticks);

        private static void AddWriteGateTicks(long ticks) => Interlocked.Add(ref _writeGateTicks, ticks);

        private static void AddSessionCpu(long cpu100Ns) => Interlocked.Add(ref _sessionCpu100Ns, cpu100Ns);

        private static void NoteIncompleteParse() => Interlocked.Increment(ref _incompleteParses);

        private static void NoteFrameParsed() => Interlocked.Increment(ref _framesParsed);

        private static void NoteCompact(long ticks, int bytes)
        {
            Interlocked.Add(ref _compactTicks, ticks);
            Interlocked.Increment(ref _compactCount);
            Interlocked.Add(ref _compactBytes, bytes);
        }

        private static void NoteReadOverlap(bool clientWriting, long readTicks)
        {
            if (clientWriting)
            {
                Interlocked.Add(ref _readDuringWriteTicks, readTicks);
                Interlocked.Increment(ref _readDuringWriteCount);
            }
            else
            {
                Interlocked.Add(ref _readAfterWriteTicks, readTicks);
                Interlocked.Increment(ref _readAfterWriteCount);
            }
        }

        private static void SetServerIoPhase(int phase) => Volatile.Write(ref _serverIoPhase, phase);

        private static bool IsClientWriteActive() => Volatile.Read(ref _clientWriteActive) != 0;

        private static long ReadClientWriteEndedAt() => Volatile.Read(ref _clientWriteEndedAt);

        private static void NoteDataAccept(long ticks, long copiedBytes)
        {
            Interlocked.Add(ref _dataAcceptTicks, ticks);
            Interlocked.Increment(ref _dataAcceptCount);
            if (copiedBytes > 0)
            {
                Interlocked.Add(ref _dataCopyBytes, copiedBytes);
            }
        }

        private static void NoteWindowWrite(long ticks)
        {
            Interlocked.Add(ref _windowWriteTicks, ticks);
            Interlocked.Increment(ref _windowWriteCount);
        }

        private static void NoteEndValidate(long ticks)
        {
            Interlocked.Add(ref _endValidateTicks, ticks);
            Interlocked.Increment(ref _endValidateCount);
        }

        private static void NoteEnqueueToResult(long ticks)
        {
            Interlocked.Add(ref _enqueueToResultTicks, ticks);
            Interlocked.Increment(ref _enqueueToResultCount);
        }

        private readonly IStorageVatpTransport _transport;
        private readonly IStorageArticleOpenBoundary _openBoundary;
        private readonly StorageServerListenerRuntimeOptions _listener;
        private readonly ArticleTransferLimits _limits;
        private readonly ILogger _logger;
        private readonly IAcceptedArticleQueue? _acceptedArticles;
        private readonly StoreAssemblyAdmission? _storeAdmission;
        private readonly Dictionary<uint, SendStream> _streams = [];
        private readonly ConcurrentDictionary<uint, StoreStream> _storeStreams = [];
        private readonly ArticleTransferReadyRing _readyRing = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly object _storeGate = new();
        private readonly byte[] _smallWrite = new byte[512];
        private CancellationToken _runCancellation;

        /// <summary>Article buffers allocated by this session after a STORE reservation was granted.</summary>
        internal long StoreBodiesAllocated;
        private Exception? _fatalSessionException;
        private uint _maxFramePayload = VatpProtocol.DefaultMaxFramePayload;
        private bool _clientHelloComplete;
        private bool _serverHelloSent;
        private int _disposed;

        /// <summary>Initializes a VATP session over an established transport.</summary>
        public StorageVatpSession(
            IStorageVatpTransport transport,
            IStorageArticleOpenBoundary openBoundary,
            StorageServerListenerRuntimeOptions listener,
            ILogger logger,
            ArticleTransferLimits? limits = null,
            IAcceptedArticleQueue? acceptedArticles = null,
            StoreAssemblyAdmission? storeAdmission = null)
        {
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(openBoundary);
            ArgumentNullException.ThrowIfNull(listener);
            ArgumentNullException.ThrowIfNull(logger);
            _transport = transport;
            _openBoundary = openBoundary;
            _listener = listener;
            _limits = limits ?? ArticleTransferLimits.Default;
            _logger = logger;
            _acceptedArticles = acceptedArticles;
            _storeAdmission = storeAdmission;
        }

        /// <summary>Runs until peer close, cancellation, or fatal protocol error.</summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _runCancellation = cancellationToken;
            var capacity = _listener.ParserAccumulationMaxBytes;
            var parseBuffer = ArrayPool<byte>.Shared.Rent(capacity);
            var usable = Math.Min(parseBuffer.Length, capacity);
            var buffered = 0;
            var cpuStart = ThreadCpuTimes.Capture();
            try
            {
                // 0 means "no prior successful read yet"; after the first read this holds a timestamp.
                var processMark = 0L;
                var readsSinceFrame = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (processMark != 0)
                    {
                        AddProcessTicks(Stopwatch.GetTimestamp() - processMark);
                    }

                    if (buffered >= usable)
                    {
                        await WriteFailAsync(
                                VatpProtocol.ConnectionStreamId,
                                VatpErrorCode.FrameTooLarge,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    int bytesRead;
                    var clientWriting = IsClientWriteActive();
                    if (!clientWriting)
                    {
                        var ended = ReadClientWriteEndedAt();
                        if (ended != 0)
                        {
                            NoteReadGap(Stopwatch.GetTimestamp() - ended);
                        }
                    }

                    try
                    {
                        NotePartialAtRead(buffered);
                        SetServerIoPhase(1);
                        var readStarted = Stopwatch.GetTimestamp();
                        bytesRead = await _transport
                            .ReadAsync(parseBuffer.AsMemory(buffered, usable - buffered), cancellationToken)
                            .ConfigureAwait(false);
                        var readTicks = Stopwatch.GetTimestamp() - readStarted;
                        SetServerIoPhase(0);
                        AddReadWaitTicks(readTicks);
                        NoteReadDuration(readTicks);
                        NoteReadOverlap(clientWriting, readTicks);

                        processMark = Stopwatch.GetTimestamp();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (TimeoutException)
                    {
                        if (_fatalSessionException is not null)
                        {
                            throw _fatalSessionException;
                        }

                        return;
                    }

                    if (bytesRead == 0)
                    {
                        if (_fatalSessionException is not null)
                        {
                            throw _fatalSessionException;
                        }

                        return;
                    }

                    buffered += bytesRead;
                    NoteRead(bytesRead);
                    readsSinceFrame++;

                    var consumedTotal = 0;
                    var framesThisRead = 0;
                    while (true)
                    {
                        var remaining = new ReadOnlySequence<byte>(parseBuffer, consumedTotal, buffered - consumedTotal);
                        var parseStarted = Stopwatch.GetTimestamp();
                        var parsed = VatpFrameParser.ParseOneFrame(remaining, _maxFramePayload);
                        AddParseTicks(Stopwatch.GetTimestamp() - parseStarted);
                        if (parsed.Status == VatpFrameParseStatus.Incomplete)
                        {
                            NoteIncompleteParse();
                            break;
                        }

                        if (parsed.Status == VatpFrameParseStatus.Invalid)
                        {
                            await WriteFailAsync(
                                    VatpProtocol.ConnectionStreamId,
                                    parsed.Error,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            return;
                        }

                        consumedTotal += checked((int)parsed.ConsumedBytes);
                        framesThisRead++;
                        NoteFrameParsed();
                        if (parsed.Frame is not { } frame)
                        {
                            await WriteFailAsync(
                                    VatpProtocol.ConnectionStreamId,
                                    VatpErrorCode.InvalidFrameType,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            return;
                        }

                        var continueSession = await HandleFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                        if (!continueSession)
                        {
                            return;
                        }

                        if (_fatalSessionException is not null)
                        {
                            throw _fatalSessionException;
                        }
                    }

                    if (consumedTotal > 0)
                    {
                        var remainingBytes = buffered - consumedTotal;
                        if (remainingBytes > 0)
                        {
                            var compactStarted = Stopwatch.GetTimestamp();
                            Buffer.BlockCopy(parseBuffer, consumedTotal, parseBuffer, 0, remainingBytes);
                            NoteCompact(Stopwatch.GetTimestamp() - compactStarted, remainingBytes);
                        }

                        buffered = remainingBytes;
                    }

                    NoteFramesThisRead(framesThisRead);
                    if (framesThisRead > 0)
                    {
                        NoteReadsToFrame(readsSinceFrame);
                        readsSinceFrame = 0;
                    }
                }
            }
            finally
            {
                ReleaseStoreSlots();
                _streams.Clear();
                _storeStreams.Clear();
                _readyRing.Clear();
                ArrayPool<byte>.Shared.Return(parseBuffer);
                var cpuEnd = ThreadCpuTimes.Capture();
                AddSessionCpu((cpuEnd.Kernel - cpuStart.Kernel) + (cpuEnd.User - cpuStart.User));
                StorageVatpSessionLogMessages.ConnectionClosed(_logger);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _transport.DisposeAsync().ConfigureAwait(false);
            _writeGate.Dispose();
        }

        private async Task<bool> HandleFrameAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
        {
            switch (frame.Header.Type)
            {
                case VatpFrameType.Hello:
                    return await HandleHelloAsync(frame, cancellationToken).ConfigureAwait(false);
                case VatpFrameType.Open:
                    return await HandleOpenAsync(frame, cancellationToken).ConfigureAwait(false);
                case VatpFrameType.Store:
                    return await HandleStoreAsync(frame, cancellationToken).ConfigureAwait(false);
                case VatpFrameType.Meta:
                    return await HandleStoreMetaAsync(frame, cancellationToken).ConfigureAwait(false);
                case VatpFrameType.Data:
                    return await HandleStoreDataAsync(frame, cancellationToken).ConfigureAwait(false);
                case VatpFrameType.End:
                    return await HandleStoreEndAsync(frame, cancellationToken).ConfigureAwait(false);
                case VatpFrameType.Window:
                    return await HandleWindowAsync(frame, cancellationToken).ConfigureAwait(false);
                case VatpFrameType.Cancel:
                    return await HandleCancelAsync(frame, cancellationToken).ConfigureAwait(false);
                default:
                    await WriteFailAsync(
                            frame.Header.StreamId == VatpProtocol.ConnectionStreamId
                                ? VatpProtocol.ConnectionStreamId
                                : frame.Header.StreamId,
                            VatpErrorCode.InvalidFrameType,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return false;
            }
        }

        private async Task<bool> HandleHelloAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
        {
            if (frame.Header.StreamId != VatpProtocol.ConnectionStreamId)
            {
                await WriteFailAsync(VatpProtocol.ConnectionStreamId, VatpErrorCode.InvalidStreamId, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            if (_clientHelloComplete)
            {
                await WriteFailAsync(VatpProtocol.ConnectionStreamId, VatpErrorCode.InvalidHello, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            if (!VatpHello.TryDecode(frame.Payload, out var hello, out var error))
            {
                await WriteFailAsync(VatpProtocol.ConnectionStreamId, error, cancellationToken).ConfigureAwait(false);
                return false;
            }

            _maxFramePayload = Math.Min(hello.MaxFramePayload, VatpProtocol.DefaultMaxFramePayload);
            _clientHelloComplete = true;
            StorageVatpSessionLogMessages.ClientHelloAccepted(_logger, _maxFramePayload);

            if (!_serverHelloSent)
            {
                _serverHelloSent = true;
                var encoded = VatpFrameEncoder.EncodeHello(_maxFramePayload);
                await WriteEncodedAsync(encoded, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        private async Task<bool> HandleOpenAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
        {
            var streamId = frame.Header.StreamId;
            if (!_clientHelloComplete)
            {
                await WriteFailAsync(streamId, VatpErrorCode.InvalidStateTransition, cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (!VatpOpenPayload.TryDecode(frame.Payload, out var open, out var decodeError))
            {
                await WriteFailAsync(streamId, decodeError, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (streamId == VatpProtocol.ConnectionStreamId
                || _streams.ContainsKey(streamId)
                || _storeStreams.ContainsKey(streamId)
                || _streams.Count + _storeStreams.Count >= _limits.MaxStreamsPerConnection)
            {
                await WriteFailAsync(streamId, streamId == VatpProtocol.ConnectionStreamId
                        ? VatpErrorCode.InvalidStreamId
                        : VatpErrorCode.StreamTableError, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = _openBoundary.TryOpen(open.RequestId, open.ArticleId);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Accepted || result.Record.ParseStatus != ArticleParseStatus.CanonicalV1)
            {
                StorageVatpSessionLogMessages.OpenRejected(_logger, streamId, open.RequestId);
                await WriteFailAsync(streamId, VatpErrorCode.OpenRejected, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var record = result.Record;
            var meta = ArticleCanonicalTransferMeta.FromRecord(in record, result.SelectedDateHeaderName);
            var metaBytes = VatpMetaCodec.Encode(in meta);
            var stream = new SendStream(
                streamId,
                record,
                new ArticleTransferWindow(_limits.InitialStreamWindowBytes, _limits.MaxStreamCreditBytes));
            _streams.Add(streamId, stream);
            StorageVatpSessionLogMessages.OpenAccepted(_logger, streamId, open.RequestId);
            await WriteEncodedAsync(VatpFrameEncoder.EncodeMeta(streamId, metaBytes), cancellationToken).ConfigureAwait(false);
            stream.Phase = SendPhase.SendingData;
            if (stream.SentBytes < record.ArtSize && stream.Window.HasCredit)
            {
                _readyRing.Enqueue(streamId);
            }

            await PumpDataAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> HandleWindowAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
        {
            var streamId = frame.Header.StreamId;
            if (frame.Payload.Length != VatpProtocol.WindowPayloadLength)
            {
                await WriteFailAsync(streamId, VatpErrorCode.InvalidFrameLength, cancellationToken).ConfigureAwait(false);
                return true;
            }

            Span<byte> windowBytes = stackalloc byte[VatpProtocol.WindowPayloadLength];
            if (frame.Payload.IsSingleSegment)
            {
                frame.Payload.FirstSpan.CopyTo(windowBytes);
            }
            else
            {
                frame.Payload.CopyTo(windowBytes);
            }

            if (!VatpControlPayload.TryDecodeWindow(windowBytes, out var addCredit))
            {
                await WriteFailAsync(streamId, VatpErrorCode.InvalidFrameLength, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (addCredit == 0)
            {
                return true;
            }

            if (!_streams.TryGetValue(streamId, out var stream) || stream.Phase != SendPhase.SendingData)
            {
                await WriteFailAsync(streamId, VatpErrorCode.UnknownStream, cancellationToken).ConfigureAwait(false);
                return true;
            }

            _ = stream.Window.Add(addCredit);
            if (stream.SentBytes < stream.Record.ArtSize)
            {
                _readyRing.Enqueue(streamId);
            }

            await PumpDataAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> HandleCancelAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
        {
            var streamId = frame.Header.StreamId;
            if (_storeStreams.TryGetValue(streamId, out var store))
            {
                if (store.AcceptStarted)
                {
                    return true;
                }

                _ = store.Receive.TryCancel();
                RemoveStore(streamId);
                StorageVatpSessionLogMessages.TransferCancelled(_logger, streamId);
                await WriteFailAsync(streamId, VatpErrorCode.Cancelled, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (!_streams.Remove(streamId, out var stream))
            {
                await WriteFailAsync(streamId, VatpErrorCode.UnknownStream, cancellationToken).ConfigureAwait(false);
                return true;
            }

            _readyRing.Remove(streamId);
            stream.Phase = SendPhase.Cancelled;
            StorageVatpSessionLogMessages.TransferCancelled(_logger, streamId);
            return true;
        }

        private async Task PumpDataAsync(CancellationToken cancellationToken)
        {
            while (TryTakeDataFrame(out var encoded, out var completedStreamId))
            {
                await WriteEncodedAsync(encoded, cancellationToken).ConfigureAwait(false);
                if (completedStreamId is { } streamId)
                {
                    await WriteEncodedAsync(VatpFrameEncoder.EncodeEnd(streamId), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private bool TryTakeDataFrame(out VatpFrameEncoder.EncodedFrame encoded, out uint? completedStreamId)
        {
            encoded = default;
            completedStreamId = null;
            var attempts = _readyRing.Count;
            while (attempts-- > 0 && _readyRing.TryTakeNext(out var streamId))
            {
                if (!_streams.TryGetValue(streamId, out var stream) || stream.Phase != SendPhase.SendingData)
                {
                    continue;
                }

                var remaining = stream.Record.ArtSize - stream.SentBytes;
                if (remaining <= 0)
                {
                    CompleteStream(stream);
                    continue;
                }

                if (!stream.Window.HasCredit)
                {
                    continue;
                }

                var length = ArticleTransferReadyRing.ComputeDataPayloadLength(
                    remaining,
                    stream.Window.Credit,
                    _maxFramePayload);
                if (length <= 0 || !stream.Window.TryConsume(length))
                {
                    continue;
                }

                var fin = length == remaining;
                var payload = stream.Record.ArtData.Slice(stream.SentBytes, length);
                stream.SentBytes += length;
                encoded = VatpFrameEncoder.EncodeData(streamId, payload, fin);
                if (stream.SentBytes < stream.Record.ArtSize)
                {
                    _readyRing.Enqueue(streamId);
                }
                else
                {
                    CompleteStream(stream);
                    completedStreamId = streamId;
                }

                return true;
            }

            return false;
        }

        private void CompleteStream(SendStream stream)
        {
            _readyRing.Remove(stream.StreamId);
            stream.Phase = SendPhase.Completed;
            _ = _streams.Remove(stream.StreamId);
            StorageVatpSessionLogMessages.TransferCompleted(_logger, stream.StreamId);
        }

        private async Task WriteFailAsync(uint streamId, VatpErrorCode error, CancellationToken cancellationToken)
        {
            var encoded = VatpFrameEncoder.EncodeFail(streamId, error);
            await WriteEncodedAsync(encoded, cancellationToken).ConfigureAwait(false);
        }

        private async Task WriteEncodedAsync(VatpFrameEncoder.EncodedFrame encoded, CancellationToken cancellationToken)
        {
            SetServerIoPhase(2);
            try
            {
                var gateStarted = Stopwatch.GetTimestamp();
                await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                AddWriteGateTicks(Stopwatch.GetTimestamp() - gateStarted);
                try
                {
                    var headerLength = encoded.Header.Length;
                    var payloadLength = encoded.Payload.Length;
                    if (headerLength + payloadLength <= _smallWrite.Length)
                    {
                        if (headerLength > 0)
                        {
                            encoded.Header.Span.CopyTo(_smallWrite);
                        }

                        if (payloadLength > 0)
                        {
                            encoded.Payload.Span.CopyTo(_smallWrite.AsSpan(headerLength));
                        }

                        await WriteFullyAsync(_smallWrite.AsMemory(0, headerLength + payloadLength), cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (!encoded.Header.IsEmpty)
                    {
                        await WriteFullyAsync(encoded.Header, cancellationToken).ConfigureAwait(false);
                    }

                    if (!encoded.Payload.IsEmpty)
                    {
                        await WriteFullyAsync(encoded.Payload, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            finally
            {
                SetServerIoPhase(0);
            }
        }

        private async Task WriteFullyAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            var written = 0;
            while (written < payload.Length)
            {
                var accepted = await _transport.WriteAsync(payload[written..], cancellationToken).ConfigureAwait(false);
                if (accepted <= 0)
                {
                    throw new IOException("Transport returned zero accepted bytes.");
                }

                written += accepted;
            }
        }

        private void TrySetFatalSessionException(Exception exception)
        {
            _ = Interlocked.CompareExchange(ref _fatalSessionException, exception, null);
        }

        private enum SendPhase
        {
            SendingData,
            Completed,
            Cancelled,
        }

        private sealed class SendStream
        {
            public SendStream(uint streamId, ArticleRecord record, ArticleTransferWindow window)
            {
                StreamId = streamId;
                Record = record;
                Window = window;
            }

            public uint StreamId { get; }

            public ArticleRecord Record { get; }

            /// <summary>Mutable credit. Must stay a field so <see cref="ArticleTransferWindow.TryConsume"/> updates this stream.</summary>
            public ArticleTransferWindow Window;

            public int SentBytes { get; set; }

            public SendPhase Phase { get; set; }
        }
    }
}
