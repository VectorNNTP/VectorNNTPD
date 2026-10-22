using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Transport.ArticleTransfer;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Listener;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.TestDoubles;
using Xunit.Abstractions;
using VectorNNTPD.StorageService;

namespace VectorNNTPD.StorageService.Tests.Listener
{

    /// <summary>
    /// One TCP/TLS connection, many concurrent VATP STORE streams. Opt-in via
    /// <c>VECTORNNTPD_STORAGE_SINGLE=1</c>.
    /// </summary>
    public sealed class StorageVatpSingleConnectionBench
    {
        private const int BodyBytes = 768_000;
        private const int InitialWindowBytes = 256 * 1024;
        private const int MaxFramePayload = 64 * 1024;
        private const long QueueByteCap = 256L * 1024 * 1024;

        private readonly ITestOutputHelper _output;

        public StorageVatpSingleConnectionBench(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Two_streams_on_one_connection_both_enqueue()
        {
            PipelinedVatpClient.ResetConnectionCount();
            var first = PrepareArticle("body-a\r\n", "<one-a@bench.test>", 1);
            var second = PrepareArticle("body-b\r\n", "<one-b@bench.test>", 2);
            await using var host = await IngressHost.StartAsync();
            await using var client = await PipelinedVatpClient.ConnectAsync(host.Port, [first, second]);
            await client.RunAsync(articleLimit: 2, CancellationToken.None);
            Assert.Equal(1, client.ConnectionsOpened);
            Assert.Equal(2, client.Submitted);
            Assert.Equal(2, client.Succeeded);
            Assert.Equal(0, client.Failed);
            Assert.Equal(2, host.Queue.EnqueuedCount);
            Assert.Equal(2, client.PeakInFlight);
        }

        [Fact]
        public async Task Single_connection_ingress_when_enabled()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_SINGLE"), "1", StringComparison.Ordinal))
            {
                return;
            }

            ThreadPool.GetMinThreads(out var workers, out var io);
            var minThreads = Math.Max(workers, Environment.ProcessorCount * 8);
            ThreadPool.SetMinThreads(minThreads, Math.Max(io, minThreads));
            var depths = ParseList(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_SINGLE_DEPTHS"), [1, 2, 4, 8, 16, 32, 64]);
            var measureSeconds = ParsePositive(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_SINGLE_SECONDS"), 8);
            var warmupSeconds = ParsePositive(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_SINGLE_WARMUP"), 2);
            var report = new StringBuilder();
            report.AppendLine("topology=1tcp+1tls acceptance=AcceptedArticleQueue.TryEnqueue pathWorker=false");
            report.AppendLine($"bodyBytes={BodyBytes} measureSeconds={measureSeconds} warmupSeconds={warmupSeconds} serverGc={GCSettings.IsServerGC} processors={Environment.ProcessorCount} aesIsa={System.Runtime.Intrinsics.X86.Aes.IsSupported}");

            foreach (var depth in depths)
            {
                if (depth is < 1 or > 64)
                {
                    throw new InvalidOperationException($"In-flight depth {depth} is outside the 64-stream connection cap.");
                }

                var line = await RunLevelAsync(depth, warmupSeconds, measureSeconds);
                report.AppendLine(line);
                _output.WriteLine(line);
            }

            var directory = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".artifacts", "experiments"));
            Directory.CreateDirectory(directory);
            var path = Path.Join(directory, "vatp-single-connection-raw.txt");
            await File.WriteAllTextAsync(path, report.ToString());
            _output.WriteLine(path);
        }

        private async Task<string> RunLevelAsync(int depth, int warmupSeconds, int measureSeconds)
        {
            PipelinedVatpClient.ResetConnectionCount();
            StorageVatpSession.ResetEnqueueToResultTiming();
            var articles = new PreparedArticle[depth];
            for (var i = 0; i < depth; i++)
            {
                articles[i] = PrepareArticle(BodyBytes, $"<s{i + 1:0000}@bench.test>", (uint)(i + 1));
            }

            await using var host = await IngressHost.StartAsync();
            await using var client = await PipelinedVatpClient.ConnectAsync(host.Port, articles);
            var tcpBefore = IPGlobalProperties.GetIPGlobalProperties().GetTcpIPv4Statistics();
            using var runCts = new CancellationTokenSource();
            var sending = client.RunAsync(articleLimit: null, runCts.Token);
            var started = Stopwatch.StartNew();
            var origin = Take(host, client, TimeSpan.Zero);
            Sample? warmup = null;
            var depthMax = 0L;
            var capped = false;
            while (started.Elapsed < TimeSpan.FromSeconds(warmupSeconds + measureSeconds))
            {
                if (host.Queue.QueuedBytes > QueueByteCap)
                {
                    capped = true;
                    break;
                }

                var depthNow = host.Queue.EnqueuedCount - host.Queue.DequeuedCount;
                if (depthNow > depthMax)
                {
                    depthMax = depthNow;
                }

                if (warmup is null && started.Elapsed >= TimeSpan.FromSeconds(warmupSeconds))
                {
                    warmup = Take(host, client, started.Elapsed);
                }

                var remaining = TimeSpan.FromSeconds(warmupSeconds + measureSeconds) - started.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250));
            }

            var steadyEnd = Take(host, client, started.Elapsed);
            if (steadyEnd.Depth > depthMax)
            {
                depthMax = steadyEnd.Depth;
            }

            client.StopStarting();
            await sending.WaitAsync(TimeSpan.FromSeconds(60));
            var idle = await WaitForDrainAsync(host, TimeSpan.FromSeconds(30));
            var finalSample = Take(host, client, started.Elapsed);
            var clientSendBuffer = client.SendBuffer;
            var clientReceiveBuffer = client.ReceiveBuffer;
            var negotiatedTls = client.NegotiatedTls;
            var serverSendBuffer = StorageVatpListenerService.LastAcceptedSendBuffer;
            var serverReceiveBuffer = StorageVatpListenerService.LastAcceptedReceiveBuffer;
            await client.DisposeAsync();
            var timing = StorageVatpSession.ReadEnqueueToResultTiming();
            var path = StorageVatpSession.ReadReceivePathTiming();
            var reads = StorageVatpSession.ReadReceiveObservation();
            var assembly = StorageVatpSession.ReadAssemblyObservation();
            var pace = StorageVatpSession.ReadPaceObservation();
            var tls = StreamStorageVatpTransport.ReadTlsThreadCost();
            var tcpAfter = IPGlobalProperties.GetIPGlobalProperties().GetTcpIPv4Statistics();
            var steady = warmup is null ? "n/a" : FormatRate(warmup.Value, steadyEnd, articles[0].ArtSize);
            var allocated = finalSample.Allocated - origin.Allocated;
            return
                $"depth={depth} connections={client.ConnectionsOpened} peakInFlight={client.PeakInFlight} capped={capped} drained={idle} steady=[{steady}] " +
                $"depthMax={depthMax} depthAtEnd={steadyEnd.Depth} " +
                $"submitted={client.Submitted} succeeded={client.Succeeded} failed={client.Failed} " +
                $"enqueued={finalSample.Enqueued} dequeued={finalSample.Dequeued} " +
                $"creditWaitSec={client.CreditWaitSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} resultWaitSec={client.ResultWaitSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} blockedSec={client.BlockedSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} clientWriteSec={client.WriteSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"enqueueToResultCount={timing.Count} enqueueToResultSec={timing.Seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"dataAcceptSec={path.DataSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} windowWriteSec={path.WindowSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} windowFrames={path.WindowCount} readWaitSec={path.ReadWaitSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} processSec={reads.ProcessSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} writeGateSec={reads.WriteGateSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} endValidateSec={path.EndSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"reads={reads.Reads} readBytes={reads.ReadBytes} readLt4K={reads.ReadLt4K} readLt16K={reads.ReadLt16K} readLt32K={reads.ReadLt32K} readExact16K={assembly.ReadExact16K} readLt64K={reads.ReadLt64K} readGe64K={reads.ReadGe64K} readUsLt20={reads.ReadDurLt20Us} readUsLt50={reads.ReadDurLt50Us} readUsLt100={reads.ReadDurLt100Us} readUsLt500={reads.ReadDurLt500Us} readUsGe500={reads.ReadDurGe500Us} incompleteParses={reads.IncompleteParses} frames={reads.Frames} " +
                $"framesRead0={assembly.FramesThisRead0} framesRead1={assembly.FramesThisRead1} framesReadMany={assembly.FramesThisReadMany} readsToFrame1={assembly.ReadsToFrame1} readsToFrame2={assembly.ReadsToFrame2} readsToFrame3={assembly.ReadsToFrame3} readsToFrame4={assembly.ReadsToFrame4} readsToFrame5Plus={assembly.ReadsToFrame5Plus} " +
                $"partial0={assembly.PartialAtRead0} partialHeader={assembly.PartialAtReadHeader} partialLt16K={assembly.PartialAtReadLt16K} partialLt64K={assembly.PartialAtReadLt64K} partialGe64K={assembly.PartialAtReadGe64K} " +
                $"parseSec={assembly.ParseSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} compactCount={assembly.CompactCount} compactBytes={assembly.CompactBytes} compactSec={assembly.CompactSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} dataCopyBytes={assembly.DataCopyBytes} timeoutSetupSec={assembly.TimeoutSetupSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} streamReadSec={assembly.StreamReadSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"readDuringWriteSec={pace.ReadDuringClientWriteSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} readDuringWriteN={pace.ReadDuringClientWriteCount} readAfterWriteSec={pace.ReadAfterClientWriteSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} readAfterWriteN={pace.ReadAfterClientWriteCount} readGapLt20={pace.ReadGapLt20Us} readGapLt100={pace.ReadGapLt100Us} readGapLt1Ms={pace.ReadGapLt1Ms} readGapGe1Ms={pace.ReadGapGe1Ms} " +
                $"clientWriteDuringReadSec={pace.ClientWriteDuringServerReadSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} clientWriteDuringReadN={pace.ClientWriteDuringServerReadCount} clientWriteDuringServerWriteSec={pace.ClientWriteDuringServerWriteSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} clientWriteDuringServerWriteN={pace.ClientWriteDuringServerWriteCount} clientWriteDuringOtherSec={pace.ClientWriteDuringOtherSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} clientWriteDuringOtherN={pace.ClientWriteDuringOtherCount} " +
                $"readyBehindPositive={pace.ReadyBehindPositive} readyBehindSamples={pace.ReadyBehindSamples} readyBehindSum={pace.ReadyBehindSum} sendGapLt20={pace.SendGapLt20Us} sendGapLt100={pace.SendGapLt100Us} sendGapLt1Ms={pace.SendGapLt1Ms} sendGapGe1Ms={pace.SendGapGe1Ms} " +
                $"sessionCpuSec={pace.SessionCpuSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} sendCpuSec={client.SendCpuSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} recvCpuSec={client.ReceiveCpuSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"clientSndBuf={clientSendBuffer} clientRcvBuf={clientReceiveBuffer} serverSndBuf={serverSendBuffer} serverRcvBuf={serverReceiveBuffer} " +
                $"tcpSegSent={tcpAfter.SegmentsSent - tcpBefore.SegmentsSent} tcpSegRecv={tcpAfter.SegmentsReceived - tcpBefore.SegmentsReceived} tcpRetrans={tcpAfter.SegmentsResent - tcpBefore.SegmentsResent} tcpErrors={tcpAfter.ErrorsReceived - tcpBefore.ErrorsReceived} tcpResets={tcpAfter.ResetsSent - tcpBefore.ResetsSent} " +
                $"tlsReadSame={tls.ReadSameThread} tlsReadHop={tls.ReadHops} tlsReadSameSec={tls.ReadSameThreadSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} tlsReadHopSec={tls.ReadHopSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} tlsReadCpuSec={tls.ReadSameThreadCpuSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"tlsWriteSame={tls.WriteSameThread} tlsWriteHop={tls.WriteHops} tlsWriteSameSec={tls.WriteSameThreadSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} tlsWriteHopSec={tls.WriteHopSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} tlsWriteCpuSec={tls.WriteSameThreadCpuSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"clientWriteSame={client.ClientWriteSame} clientWriteHop={client.ClientWriteHop} clientWriteSameSec={client.ClientWriteSameSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} clientWriteHopSec={client.ClientWriteHopSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} clientWriteCpuSec={client.ClientWriteSameCpuSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"gen0={finalSample.Gen0 - origin.Gen0} gen1={finalSample.Gen1 - origin.Gen1} gen2={finalSample.Gen2 - origin.Gen2} " +
                $"allocatedBytes={allocated} heapBytes={finalSample.HeapBytes} workingSet={finalSample.WorkingSet} cpuMs={(finalSample.Cpu - origin.Cpu).TotalMilliseconds:F0} " +
                $"artSize={articles[0].ArtSize} dataFrames={articles[0].Data.Length} tls={negotiatedTls}";
        }

        private static string FormatRate(Sample start, Sample end, int artSize)
        {
            var seconds = (end.Elapsed - start.Elapsed).TotalSeconds;
            if (seconds <= 0)
            {
                return "seconds=0";
            }

            var enqueued = end.Enqueued - start.Enqueued;
            var body = enqueued * (double)BodyBytes;
            var canonical = enqueued * (double)artSize;
            var wire = end.WireBytes - start.WireBytes;
            return
                $"seconds={seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} enqueued={enqueued} enqueuedPerSec={(enqueued / seconds).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"bodyGbps={(body * 8d / seconds / 1_000_000_000d).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} artGbps={(canonical * 8d / seconds / 1_000_000_000d).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"wireGbps={(wire * 8d / seconds / 1_000_000_000d).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} depthStart={start.Depth} depthEnd={end.Depth}";
        }

        private static Sample Take(IngressHost host, PipelinedVatpClient client, TimeSpan elapsed)
        {
            using var process = Process.GetCurrentProcess();
            return new Sample(
                elapsed,
                host.Queue.EnqueuedCount,
                host.Queue.DequeuedCount,
                host.Queue.QueuedBytes,
                client.WireBytes,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                GC.GetTotalMemory(forceFullCollection: false),
                GC.GetTotalAllocatedBytes(precise: false),
                process.WorkingSet64,
                process.TotalProcessorTime);
        }

        private static async Task<bool> WaitForDrainAsync(IngressHost host, TimeSpan timeout)
        {
            var deadline = Stopwatch.StartNew();
            while (host.Queue.DequeuedCount < host.Queue.EnqueuedCount)
            {
                if (deadline.Elapsed > timeout)
                {
                    return false;
                }

                await Task.Delay(20);
            }

            return true;
        }

        private static int[] ParseList(string? text, int[] fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
        }

        private static int ParsePositive(string? text, int fallback) =>
            int.TryParse(text, out var value) && value > 0 ? value : fallback;

        private static PreparedArticle PrepareArticle(int bodyBytes, string messageId, uint streamId)
        {
            var body = new byte[bodyBytes];
            Array.Fill(body, (byte)'x');
            for (var line = 0; line < bodyBytes / 100; line++)
            {
                var offset = line * 100;
                body[offset] = (byte)('A' + ((int)streamId % 26));
                body[offset + 1] = (byte)('0' + (line % 10));
                body[offset + 98] = (byte)'\r';
                body[offset + 99] = (byte)'\n';
            }

            return PrepareArticle(body, messageId, streamId);
        }

        private static PreparedArticle PrepareArticle(string body, string messageId, uint streamId) =>
            PrepareArticle(Encoding.ASCII.GetBytes(body), messageId, streamId);

        private static PreparedArticle PrepareArticle(ReadOnlySpan<byte> body, string messageId, uint streamId)
        {
            var header = Encoding.ASCII.GetBytes(
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: " + messageId + "\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: vatp-bench\r\n" +
                "\r\n");
            var text = new byte[header.Length + body.Length];
            header.CopyTo(text);
            body.CopyTo(text.AsSpan(header.Length));
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser("nntpd01.usenet.ninja"), text);
            if (!created.IsAccepted)
            {
                throw new InvalidOperationException($"Article build failed: {created.MaterializeFailure}");
            }

            var art = created.Record.ArtData;
            var frames = new List<byte[]>();
            var lengths = new List<int>();
            var offset = 0;
            while (offset < art.Length)
            {
                var length = Math.Min(MaxFramePayload, art.Length - offset);
                var fin = offset + length == art.Length;
                frames.Add(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeData(streamId, art.Slice(offset, length), fin)));
                lengths.Add(length);
                offset += length;
            }

            var id = new byte[ArticleId.Length];
            created.Record.ArtId.CopyTo(id);
            var store = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeStore(streamId, id));
            var meta = ArticleCanonicalTransferMeta.FromRecord(created.Record, created.SelectedDateHeaderName);
            var metaFrame = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeMeta(streamId, VatpMetaCodec.Encode(in meta)));
            var end = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(streamId));
            return new PreparedArticle(store, metaFrame, frames.ToArray(), lengths.ToArray(), end, created.Record.ArtSize);
        }

        private readonly record struct Sample(
            TimeSpan Elapsed,
            long Enqueued,
            long Dequeued,
            long QueuedBytes,
            long WireBytes,
            int Gen0,
            int Gen1,
            int Gen2,
            long HeapBytes,
            long Allocated,
            long WorkingSet,
            TimeSpan Cpu)
        {
            public long Depth => Enqueued - Dequeued;
        }

        private sealed record PreparedArticle(byte[] Store, byte[] Meta, byte[][] Data, int[] DataLengths, byte[] End, int ArtSize);

        private sealed class IngressHost : IAsyncDisposable
        {
            private readonly StorageVatpListenerService _listener;
            private readonly CancellationTokenSource _drainCts = new();
            private readonly Task _drain;

            private IngressHost(StorageVatpListenerService listener, AcceptedArticleQueue queue, int port)
            {
                _listener = listener;
                Queue = queue;
                Port = port;
                _drain = Task.Run(() => DrainAsync(queue, _drainCts.Token));
            }

            public AcceptedArticleQueue Queue { get; }

            public int Port { get; }

            public static async Task<IngressHost> StartAsync()
            {
                var port = StorageServerTestOptions.GetFreeTcpPort();
                var readiness = new AcmeCertificateReadiness();
                readiness.MarkReady();
                var queue = new AcceptedArticleQueue();
                var listener = new StorageVatpListenerService(
                    CreateRuntime(port),
                    TestListenerCertificates.CreatePublishedProvider(),
                    NullStorageArticleOpenBoundary.Instance,
                    readiness,
                    NullLogger<StorageVatpListenerService>.Instance,
                    queue,
                    new StoreAssemblyAdmission(1024));
                await listener.StartAsync(CancellationToken.None);
                return new IngressHost(listener, queue, port);
            }

            public async ValueTask DisposeAsync()
            {
                await _listener.DisposeAsync();
                await _drainCts.CancelAsync();
                try
                {
                    await _drain.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException ex)
                {
                    ExpectedException.Ignore(ex);
                }
                catch (OperationCanceledException ex)
                {
                    ExpectedException.Ignore(ex);
                }

                _drainCts.Dispose();
            }

            private static async Task DrainAsync(AcceptedArticleQueue queue, CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var delivery = await queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
                        if (!delivery.HasArticle)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            private static StorageServerRuntimeOptions CreateRuntime(int tlsPort)
            {
                var identity = StorageServerTestOptions.CreateValid();
                identity.BindAddress = ["127.0.0.1"];
                identity.BindPortTls = tlsPort;
                var acme = StorageServerTestOptions.CreateValidAcme(identity);
                acme.BindAddress = ["127.0.0.1"];
                acme.BindPortTls = tlsPort;
                return StorageServerRuntimeOptionsFactory.Create(identity, acme);
            }
        }

        private sealed class PipelinedVatpClient : IAsyncDisposable
        {
            private static int _opened;

            private readonly TcpClient _tcp;
            private readonly SslStream _ssl;
            private readonly Slot[] _slots;
            private readonly Task _reader;
            private readonly SemaphoreSlim _signal = new(0);
            private readonly TaskCompletionSource _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenSource _dispose = new();
            private readonly byte[] _window = new byte[VatpProtocol.WindowPayloadLength];
            private readonly byte[] _outcome = new byte[1];
            private int _stopNew;
            private long _submitted;
            private long _succeeded;
            private long _failed;
            private long _wireBytes;
            private long _creditWaitTicks;
            private long _resultWaitTicks;
            private long _blockedTicks;
            private long _writeTicks;
            private long _clientWriteSame;
            private long _clientWriteHop;
            private long _clientWriteHopTicks;
            private long _clientWriteSameTicks;
            private long _clientWriteSameCpu;
            private long _sendCpu100Ns;
            private long _recvCpu100Ns;
            private int _afterLoopWait;
            private int _peakInFlight;
            private Exception? _fault;

            private PipelinedVatpClient(TcpClient tcp, SslStream ssl, PreparedArticle[] articles)
            {
                _tcp = tcp;
                _ssl = ssl;
                _slots = new Slot[articles.Length];
                for (var i = 0; i < articles.Length; i++)
                {
                    _slots[i] = new Slot(articles[i])
                    {
                        Credit = InitialWindowBytes,
                    };
                }

                _reader = ReadLoopAsync();
            }

            public int ConnectionsOpened { get; private set; }

            public int PeakInFlight => Volatile.Read(ref _peakInFlight);

            public long Submitted => Volatile.Read(ref _submitted);

            public long Succeeded => Volatile.Read(ref _succeeded);

            public long Failed => Volatile.Read(ref _failed);

            public long WireBytes => Volatile.Read(ref _wireBytes);

            public double CreditWaitSeconds => Volatile.Read(ref _creditWaitTicks) / (double)Stopwatch.Frequency;

            public double ResultWaitSeconds => Volatile.Read(ref _resultWaitTicks) / (double)Stopwatch.Frequency;

            public double BlockedSeconds => Volatile.Read(ref _blockedTicks) / (double)Stopwatch.Frequency;

            public double WriteSeconds => Volatile.Read(ref _writeTicks) / (double)Stopwatch.Frequency;

            public double SendCpuSeconds => Volatile.Read(ref _sendCpu100Ns) / 10_000_000d;

            public double ReceiveCpuSeconds => Volatile.Read(ref _recvCpu100Ns) / 10_000_000d;

            public long ClientWriteSame => Volatile.Read(ref _clientWriteSame);

            public long ClientWriteHop => Volatile.Read(ref _clientWriteHop);

            public double ClientWriteSameSeconds => Volatile.Read(ref _clientWriteSameTicks) / (double)Stopwatch.Frequency;

            public double ClientWriteHopSeconds => Volatile.Read(ref _clientWriteHopTicks) / (double)Stopwatch.Frequency;

            public double ClientWriteSameCpuSeconds => Volatile.Read(ref _clientWriteSameCpu) / 10_000_000d;

            public int SendBuffer => _tcp.Client.SendBufferSize;

            public int ReceiveBuffer => _tcp.Client.ReceiveBufferSize;

            public string NegotiatedTls => $"{_ssl.SslProtocol}/{_ssl.NegotiatedCipherSuite}";

            public static void ResetConnectionCount() => Interlocked.Exchange(ref _opened, 0);

            public static async Task<PipelinedVatpClient> ConnectAsync(int port, PreparedArticle[] articles)
            {
                var opened = Interlocked.Increment(ref _opened);
                if (opened != 1)
                {
                    throw new InvalidOperationException($"Benchmark opened TCP connection {opened}; exactly one is required.");
                }

                var tcp = new TcpClient { NoDelay = true };
                tcp.Client.SendBufferSize = 1 << 20;
                tcp.Client.ReceiveBufferSize = 1 << 20;
                await tcp.ConnectAsync(IPAddress.Loopback, port);
                var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                    },
                    CancellationToken.None);
                var client = new PipelinedVatpClient(tcp, ssl, articles)
                {
                    ConnectionsOpened = opened,
                };
                await client.WriteAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload)));
                using var helloTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client._hello.Task.WaitAsync(helloTimeout.Token);
                if (client._fault is not null)
                {
                    throw client._fault;
                }

                return client;
            }

            public void StopStarting() => Volatile.Write(ref _stopNew, 1);

            public async Task RunAsync(int? articleLimit, CancellationToken cancellationToken)
            {
                var inFlight = 0;
                var cpuStart = ThreadCpuTimes.Capture();
                try
                {
                    while (true)
                    {
                        if (_fault is not null)
                        {
                            throw _fault;
                        }

                        var progressed = false;
                        var waitingCredit = false;
                        for (var i = 0; i < _slots.Length; i++)
                        {
                            var slot = _slots[i];
                            var results = Volatile.Read(ref slot.Results);
                            if (slot.Phase == SlotPhase.WaitingResult && results != slot.ResultsSeen)
                            {
                                Interlocked.Add(ref _resultWaitTicks, Stopwatch.GetTimestamp() - slot.ResultWaitStart);
                                slot.ResultsSeen = results;
                                slot.Phase = SlotPhase.Idle;
                                slot.DataIndex = 0;
                                Volatile.Write(ref slot.Credit, InitialWindowBytes);
                                inFlight--;
                                progressed = true;
                            }

                            if (slot.Phase == SlotPhase.Idle)
                            {
                                if (Volatile.Read(ref _stopNew) == 1 || (articleLimit is int limit && Volatile.Read(ref _submitted) >= limit))
                                {
                                    continue;
                                }

                                await WriteAsync(slot.Article.Store, i);
                                await WriteAsync(slot.Article.Meta, i);
                                slot.Phase = SlotPhase.Sending;
                                slot.DataIndex = 0;
                                inFlight++;
                                UpdatePeak(inFlight);
                                Interlocked.Increment(ref _submitted);
                                progressed = true;
                                continue;
                            }

                            if (slot.Phase != SlotPhase.Sending)
                            {
                                continue;
                            }

                            if (slot.DataIndex < slot.Article.Data.Length)
                            {
                                var need = slot.Article.DataLengths[slot.DataIndex];
                                if (!TryTakeCredit(slot, need))
                                {
                                    waitingCredit = true;
                                    continue;
                                }

                                await WriteAsync(slot.Article.Data[slot.DataIndex], i);
                                slot.DataIndex++;
                                progressed = true;
                                continue;
                            }

                            await WriteAsync(slot.Article.End, i);
                            slot.ResultWaitStart = Stopwatch.GetTimestamp();
                            slot.Phase = SlotPhase.WaitingResult;
                            progressed = true;
                        }

                        if (inFlight == 0 && (Volatile.Read(ref _stopNew) == 1 || (articleLimit is int done && Volatile.Read(ref _submitted) >= done)))
                        {
                            return;
                        }

                        if (progressed)
                        {
                            continue;
                        }

                        var blocked = Stopwatch.GetTimestamp();
                        await _signal.WaitAsync(cancellationToken);
                        var waited = Stopwatch.GetTimestamp() - blocked;
                        Interlocked.Add(ref _blockedTicks, waited);
                        Volatile.Write(ref _afterLoopWait, 1);
                        if (waitingCredit)
                        {
                            Interlocked.Add(ref _creditWaitTicks, waited);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    StopStarting();
                    while (inFlight > 0 && _fault is null)
                    {
                        await _signal.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                        for (var i = 0; i < _slots.Length; i++)
                        {
                            var slot = _slots[i];
                            if (slot.Phase == SlotPhase.WaitingResult && Volatile.Read(ref slot.Results) != slot.ResultsSeen)
                            {
                                slot.ResultsSeen = slot.Results;
                                slot.Phase = SlotPhase.Idle;
                                inFlight--;
                            }
                        }
                    }
                }
                finally
                {
                    var cpuEnd = ThreadCpuTimes.Capture();
                    Interlocked.Add(ref _sendCpu100Ns, (cpuEnd.Kernel - cpuStart.Kernel) + (cpuEnd.User - cpuStart.User));
                }
            }

            private int _disposedClient;

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposedClient, 1) == 1)
                {
                    return;
                }

                await _dispose.CancelAsync();
                try
                {
                    await _reader;
                }
                catch (OperationCanceledException ex)
                {
                    ExpectedException.Ignore(ex);
                }
                catch (IOException ex)
                {
                    ExpectedException.Ignore(ex);
                }

                await _ssl.DisposeAsync();
                _tcp.Dispose();
                _dispose.Dispose();
                _signal.Dispose();
            }

            private void UpdatePeak(int inFlight)
            {
                int observed;
                while ((observed = Volatile.Read(ref _peakInFlight)) < inFlight)
                {
                    if (Interlocked.CompareExchange(ref _peakInFlight, inFlight, observed) == observed)
                    {
                        return;
                    }
                }
            }

            private static bool TryTakeCredit(Slot slot, int need)
            {
                while (true)
                {
                    var current = Volatile.Read(ref slot.Credit);
                    if (current < need)
                    {
                        return false;
                    }

                    if (Interlocked.CompareExchange(ref slot.Credit, current - need, current) == current)
                    {
                        return true;
                    }
                }
            }

            private async Task WriteAsync(byte[] frame, int slotIndex = -1)
            {
                var readyBehind = slotIndex < 0 ? 0 : CountSendableOthers(slotIndex);
                var afterLoopWait = Interlocked.Exchange(ref _afterLoopWait, 0) == 1;
                StorageVatpSession.NoteClientWriteStart(readyBehind, afterLoopWait);
                var threadId = ThreadCpuTimes.CurrentThreadId;
                var cpu = ThreadCpuTimes.Capture();
                var started = Stopwatch.GetTimestamp();
                await _ssl.WriteAsync(frame);
                var elapsed = Stopwatch.GetTimestamp() - started;
                Interlocked.Add(ref _writeTicks, elapsed);
                Interlocked.Add(ref _wireBytes, frame.Length);
                StorageVatpSession.NoteClientWriteEnd(elapsed);
                if (threadId != 0 && threadId == ThreadCpuTimes.CurrentThreadId)
                {
                    var after = ThreadCpuTimes.Capture();
                    Interlocked.Increment(ref _clientWriteSame);
                    Interlocked.Add(ref _clientWriteSameTicks, elapsed);
                    var used = (after.Kernel - cpu.Kernel) + (after.User - cpu.User);
                    if (used > 0)
                    {
                        Interlocked.Add(ref _clientWriteSameCpu, used);
                    }
                }
                else
                {
                    Interlocked.Increment(ref _clientWriteHop);
                    Interlocked.Add(ref _clientWriteHopTicks, elapsed);
                }
            }

            private int CountSendableOthers(int except)
            {
                var ready = 0;
                for (var i = 0; i < _slots.Length; i++)
                {
                    if (i == except)
                    {
                        continue;
                    }

                    var slot = _slots[i];
                    if (slot.Phase != SlotPhase.Sending || slot.DataIndex >= slot.Article.Data.Length)
                    {
                        continue;
                    }

                    if (Volatile.Read(ref slot.Credit) >= slot.Article.DataLengths[slot.DataIndex])
                    {
                        ready++;
                    }
                }

                return ready;
            }

            private async Task ReadLoopAsync()
            {
                var buffer = new byte[256 * 1024];
                var filled = 0;
                var cpuStart = ThreadCpuTimes.Capture();
                try
                {
                    while (!_dispose.IsCancellationRequested)
                    {
                        var read = await _ssl.ReadAsync(buffer.AsMemory(filled), _dispose.Token);
                        if (read == 0)
                        {
                            Fault(new IOException("VATP peer closed the connection."));
                            _signal.Release();
                            return;
                        }

                        filled += read;
                        var offset = 0;
                        while (true)
                        {
                            var parsed = VatpFrameParser.ParseOneFrame(
                                new ReadOnlySequence<byte>(buffer, offset, filled - offset),
                                VatpProtocol.DefaultMaxFramePayload);
                            if (parsed.Status == VatpFrameParseStatus.Incomplete)
                            {
                                break;
                            }

                            if (parsed.Status != VatpFrameParseStatus.Success || parsed.Frame is null)
                            {
                                Fault(new InvalidOperationException($"VATP parse failed: {parsed.Error}"));
                                _signal.Release();
                                return;
                            }

                            var frame = parsed.Frame.Value;
                            var streamId = frame.Header.StreamId;
                            switch (frame.Header.Type)
                            {
                                case VatpFrameType.Hello:
                                    _hello.TrySetResult();
                                    break;
                                case VatpFrameType.Window:
                                    frame.Payload.CopyTo(_window);
                                    if (streamId < 1 || streamId > (uint)_slots.Length || !VatpControlPayload.TryDecodeWindow(_window, out var add))
                                    {
                                        Fault(new InvalidOperationException("Invalid WINDOW."));
                                        _signal.Release();
                                        return;
                                    }

                                    Interlocked.Add(ref _slots[(int)streamId - 1].Credit, add);
                                    _signal.Release();
                                    break;
                                case VatpFrameType.Result:
                                    frame.Payload.CopyTo(_outcome);
                                    if (streamId < 1 || streamId > (uint)_slots.Length)
                                    {
                                        Fault(new InvalidOperationException($"RESULT for unknown stream {streamId}."));
                                        _signal.Release();
                                        return;
                                    }

                                    if (_outcome[0] == VatpStoreOutcome.Accepted)
                                    {
                                        Interlocked.Increment(ref _succeeded);
                                    }
                                    else
                                    {
                                        Interlocked.Increment(ref _failed);
                                    }

                                    Interlocked.Increment(ref _slots[(int)streamId - 1].Results);
                                    _signal.Release();
                                    break;
                                case VatpFrameType.Fail:
                                    Interlocked.Increment(ref _failed);
                                    Fault(new InvalidOperationException($"VATP FAIL on stream {streamId}."));
                                    if (streamId >= 1 && (int)streamId <= _slots.Length)
                                    {
                                        Interlocked.Increment(ref _slots[(int)streamId - 1].Results);
                                    }

                                    _signal.Release();
                                    return;
                            }

                            offset += checked((int)parsed.ConsumedBytes);
                        }

                        if (offset > 0)
                        {
                            Buffer.BlockCopy(buffer, offset, buffer, 0, filled - offset);
                            filled -= offset;
                        }
                    }
                }
                catch (OperationCanceledException ex) when (_dispose.IsCancellationRequested)
                {
                    ExpectedException.Ignore(ex);
                }
                catch (Exception ex)
                {
                    Fault(ex);
                    _signal.Release();
                }
                finally
                {
                    var cpuEnd = ThreadCpuTimes.Capture();
                    Interlocked.Add(ref _recvCpu100Ns, (cpuEnd.Kernel - cpuStart.Kernel) + (cpuEnd.User - cpuStart.User));
                }
            }

            private void Fault(Exception exception) => Interlocked.CompareExchange(ref _fault, exception, null);

            private enum SlotPhase
            {
                Idle = 0,
                Sending = 3,
                WaitingResult = 4,
            }

            private sealed class Slot(PreparedArticle article)
            {
                public PreparedArticle Article { get; } = article;

                public SlotPhase Phase { get; set; }

                public int DataIndex { get; set; }

                public long Credit;

                public long Results;

                public long ResultsSeen { get; set; }

                public long ResultWaitStart { get; set; }
            }
        }
    }
}
