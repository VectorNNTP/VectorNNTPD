using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
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

namespace VectorNNTPD.StorageService.Tests.Listener
{

    /// <summary>
    /// End-to-end VATP STORE benchmark. The sustained sweep runs only when
    /// <c>VECTORNNTPD_STORAGE_BENCH=1</c>. The single-article fact always runs.
    /// </summary>
    public sealed class StorageVatpThroughputBench
    {
        public const int BodyBytes = 768_000;
        private const int InitialWindowBytes = 256 * 1024;
        private const int MaxFramePayload = 64 * 1024;
        private const long QueueByteCap = 256L * 1024 * 1024;
        private const string PathIdentity = "cache01.usenet.ninja";

        private readonly ITestOutputHelper _output;

        public StorageVatpThroughputBench(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task One_tls_article_is_accepted_and_path_processed()
        {
            var prepared = Prepare(Encoding.ASCII.GetBytes("body-line\r\n"), "<one-tls@bench.test>", 1);
            byte[]? rewritten = null;
            await using var host = await BenchHost.StartAsync(seen => rewritten = seen.ToArray());
            await using var session = await VatpLoadSession.ConnectAsync(host.Port);
            await session.SendArticlesAsync([prepared], count: 1, CancellationToken.None);

            var drained = await WaitForProcessedAsync(host, expected: 1, TimeSpan.FromSeconds(10));
            Assert.True(drained);
            var processor = host.Processor ?? throw new InvalidOperationException("Path worker was not started.");
            Assert.Equal(1, session.Accepted);
            Assert.Equal(0, session.Rejected);
            Assert.Equal(1, processor.Processed);
            Assert.Equal(0, processor.Rejected);
            Assert.Equal(0, host.Queue.EnqueuedCount - host.Queue.DequeuedCount);
            Assert.NotNull(rewritten);
            var text = Encoding.ASCII.GetString(rewritten);
            Assert.Contains(PathIdentity, text, StringComparison.Ordinal);
            Assert.Contains("body-line\r\n", text, StringComparison.Ordinal);
            Assert.Contains("nntpd01.usenet.ninja", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Sequential_pipe_accepts_each_store_before_the_next()
        {
            var pool = BuildPool(2);
            await using var host = await BenchHost.StartAsync(rewritten: null, ingressOnly: true);
            await using var session = await VatpLoadSession.ConnectAsync(host.Port);
            await session.SendArticlesAsync(pool, count: 2, CancellationToken.None, pipe: 1);

            var drained = await WaitForDequeuedAsync(host, expected: 2, TimeSpan.FromSeconds(30));
            Assert.True(drained);
            Assert.Equal(1, session.PipeUsed);
            Assert.Equal(1, session.PipeMaxOutstanding);
            Assert.Equal(2, session.Accepted);
            Assert.Equal(2, session.TransfersCompleted);
            Assert.Equal(0, session.Rejected);
            Assert.Equal(0, session.TransfersOutstanding);
            Assert.Equal(2, host.Queue.EnqueuedCount);
            Assert.Equal(2, host.Queue.DequeuedCount);
        }

        [Fact]
        public async Task Piped_stores_on_one_connection_complete_independently()
        {
            var pool = BuildPool(4);
            await using var host = await BenchHost.StartAsync(rewritten: null, ingressOnly: true);
            await using var session = await VatpLoadSession.ConnectAsync(host.Port);
            await session.SendArticlesAsync(pool, count: 4, CancellationToken.None, pipe: 4);

            var drained = await WaitForDequeuedAsync(host, expected: 4, TimeSpan.FromSeconds(30));
            Assert.True(drained);
            Assert.Equal(4, session.PipeUsed);
            Assert.True(session.PipeMaxOutstanding >= 2);
            Assert.Equal(4, session.Accepted);
            Assert.Equal(4, session.TransfersCompleted);
            Assert.Equal(0, session.Rejected);
            Assert.Equal(0, session.TransfersOutstanding);
            Assert.Equal(4, host.Queue.EnqueuedCount);
            Assert.Equal(4, host.Queue.DequeuedCount);
        }

        [Fact]
        public async Task Sustained_vatp_path_when_bench_enabled()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_BENCH"), "1", StringComparison.Ordinal))
            {
                return;
            }

            var sessions = ParseSessions(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_BENCH_SESSIONS"));
            var measureSeconds = ParsePositive(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_BENCH_SECONDS"), 8);
            var warmupSeconds = ParsePositive(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_BENCH_WARMUP"), 2);
            var pool = BuildPool(16);
            var artSize = pool[0].ArtSize;
            var report = new StringBuilder();
            report.AppendLine($"bodyBytes={BodyBytes} artSize={artSize} pool={pool.Length} measureSeconds={measureSeconds} warmupSeconds={warmupSeconds}");
            report.AppendLine($"runtime={Environment.Version} os={Environment.OSVersion} processors={Environment.ProcessorCount} serverGc={GCSettings.IsServerGC}");

            foreach (var sessionCount in sessions)
            {
                var line = await RunLevelAsync(pool, sessionCount, warmupSeconds, measureSeconds);
                report.AppendLine(line);
                _output.WriteLine(line);
            }

            await WriteReportAsync(report, "vatp-bench-raw.txt");
        }

        [Fact]
        public async Task Ingress_enqueue_when_bench_enabled()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_BENCH"), "1", StringComparison.Ordinal))
            {
                return;
            }

            ThreadPool.GetMinThreads(out var workerThreads, out var ioThreads);
            var minThreads = Math.Max(workerThreads, Environment.ProcessorCount * 8);
            ThreadPool.SetMinThreads(minThreads, Math.Max(ioThreads, minThreads));
            var sessions = ParseSessions(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_BENCH_SESSIONS"));
            var measureSeconds = ParsePositive(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_BENCH_SECONDS"), 8);
            var warmupSeconds = ParsePositive(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_BENCH_WARMUP"), 2);
            var pipeRequested = ParsePositive(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_BENCH_PIPE"), 1);
            var pipe = Math.Min(pipeRequested, ArticleTransferLimits.Default.MaxStreamsPerConnection);
            var initialWindow = ParseBenchWindow(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_BENCH_WINDOW"));
            var pool = BuildPool(16);
            var report = new StringBuilder();
            report.AppendLine("mode=ingress-enqueue boundary=AcceptedArticleQueue.TryEnqueue pathWorker=false");
            report.AppendLine($"threadPoolMin={minThreads}");
            report.AppendLine($"bodyBytes={BodyBytes} artSize={pool[0].ArtSize} pool={pool.Length} measureSeconds={measureSeconds} warmupSeconds={warmupSeconds} pipeRequested={pipeRequested} pipe={pipe} serverMaxStreams={ArticleTransferLimits.Default.MaxStreamsPerConnection} initialWindow={initialWindow}");
            report.AppendLine($"runtime={Environment.Version} os={Environment.OSVersion} processors={Environment.ProcessorCount} serverGc={GCSettings.IsServerGC} gcLatency={GCSettings.LatencyMode} lohCompact={GCSettings.LargeObjectHeapCompactionMode}");

            foreach (var sessionCount in sessions)
            {
                var line = await RunLevelAsync(pool, sessionCount, warmupSeconds, measureSeconds, ingressOnly: true, pipe: pipe, initialWindowBytes: initialWindow);
                report.AppendLine(line);
                _output.WriteLine(line);
            }

            await WriteReportAsync(report, "vatp-ingress-raw.txt");
        }

        private async Task WriteReportAsync(StringBuilder report, string fileName)
        {
            var directory = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                ".artifacts", "experiments"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(path, report.ToString());
            _output.WriteLine(path);
        }

        private async Task<string> RunLevelAsync(
            Prepared[] pool,
            int sessionCount,
            int warmupSeconds,
            int measureSeconds,
            bool ingressOnly = false,
            int pipe = 1,
            int initialWindowBytes = InitialWindowBytes)
        {
            await using var host = await BenchHost.StartAsync(rewritten: null, ingressOnly: ingressOnly, initialWindowBytes: initialWindowBytes);
            var sessions = new VatpLoadSession[sessionCount];
            for (var i = 0; i < sessions.Length; i++)
            {
                sessions[i] = await VatpLoadSession.ConnectAsync(host.Port);
            }

            using var sendCts = new CancellationTokenSource();
            var senders = sessions.Select(session => session.SendArticlesAsync(pool, count: null, sendCts.Token, pipe, initialWindowBytes)).ToArray();
            var started = Stopwatch.StartNew();
            var origin = Sample.Take(host, sessions, TimeSpan.Zero);
            var samples = new List<Sample>();
            var capped = false;
            var warmup = (Sample?)null;
            var measureEnd = (Sample?)null;
            var warmupStamp = 0L;
            var measureStamp = 0L;

            while (started.Elapsed < TimeSpan.FromSeconds(warmupSeconds + measureSeconds))
            {
                if (host.Queue.QueuedBytes > QueueByteCap)
                {
                    capped = true;
                    break;
                }

                samples.Add(Sample.Take(host, sessions, started.Elapsed));
                var delay = TimeSpan.FromMilliseconds(250);
                var remaining = TimeSpan.FromSeconds(warmupSeconds + measureSeconds) - started.Elapsed;
                if (remaining < delay)
                {
                    delay = remaining;
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }

                if (warmup is null && started.Elapsed >= TimeSpan.FromSeconds(warmupSeconds))
                {
                    warmup = Sample.Take(host, sessions, started.Elapsed);
                    warmupStamp = Stopwatch.GetTimestamp();
                }
            }

            measureEnd = Sample.Take(host, sessions, started.Elapsed);
            measureStamp = Stopwatch.GetTimestamp();
            await sendCts.CancelAsync();
            try
            {
                await Task.WhenAll(senders);
            }
            catch (OperationCanceledException)
            {
            }

            var drained = ingressOnly
                ? await WaitForDequeuedAsync(host, host.Queue.EnqueuedCount, TimeSpan.FromSeconds(30))
                : await WaitForProcessedAsync(host, host.Queue.EnqueuedCount, TimeSpan.FromSeconds(60));
            var drainedAt = Sample.Take(host, sessions, started.Elapsed);
            double creditWait = 0;
            double slotWait = 0;
            var pipeUsed = 0;
            var pipeMax = 0;
            var incomplete = 0L;
            var initialCredit = 0;
            var windowFrames = 0;
            long windowBytes = 0;
            var windowAddMin = 0;
            var windowAddMax = 0;
            var cipher = sessions.Length == 0 ? "none" : sessions[0].NegotiatedCipher;
            var latencies = new List<long>();
            foreach (var session in sessions)
            {
                creditWait += session.CreditWaitSeconds;
                slotWait += session.SlotWaitSeconds;
                pipeUsed = Math.Max(pipeUsed, session.PipeUsed);
                pipeMax = Math.Max(pipeMax, session.PipeMaxOutstanding);
                incomplete += session.TransfersOutstanding;
                initialCredit = session.InitialCredit;
                windowFrames += session.WindowFrames;
                windowBytes += session.WindowBytes;
                if (session.WindowFrames > 0)
                {
                    windowAddMin = windowAddMin == 0
                        ? session.WindowAddMin
                        : Math.Min(windowAddMin, session.WindowAddMin);
                }

                windowAddMax = Math.Max(windowAddMax, session.WindowAddMax);
                session.CopySteadyLatencies(warmupStamp, measureStamp, latencies);
                await session.DisposeAsync();
            }

            var (resultMedianUs, resultP95Us, resultSamples) = PercentileMicroseconds(latencies);

            var cold = samples.Count >= 2 ? Rate(samples[0], samples[Math.Min(4, samples.Count - 1)], pool[0].ArtSize) : "n/a";
            var steady = warmup is null
                ? "n/a"
                : Rate(warmup.Value, measureEnd.Value, pool[0].ArtSize);
            var steadyAllocated = warmup is null ? 0 : measureEnd.Value.AllocatedBytes - warmup.Value.AllocatedBytes;
            var steadyPauseMs = warmup is null
                ? 0
                : (measureEnd.Value.GcPause - warmup.Value.GcPause).TotalMilliseconds;
            var depthMax = measureEnd.Value.Depth;
            foreach (var sample in samples)
            {
                if (sample.Depth > depthMax)
                {
                    depthMax = sample.Depth;
                }
            }

            return
                $"sessions={sessionCount} capped={capped} drained={drained} cold=[{cold}] steady=[{steady}] " +
                $"depthMax={depthMax} depthAtMeasureEnd={measureEnd.Value.Depth} depthAfterDrain={drainedAt.Depth} " +
                $"enqueued={drainedAt.Enqueued} dequeued={drainedAt.Dequeued} processed={drainedAt.Processed} rejectedPath={drainedAt.PathRejected} " +
                $"accepted={drainedAt.Accepted} rejected={drainedAt.Rejected} " +
                $"gen0={drainedAt.Gen0 - origin.Gen0} gen1={drainedAt.Gen1 - origin.Gen1} gen2={drainedAt.Gen2 - origin.Gen2} " +
                $"allocatedBytes={drainedAt.AllocatedBytes - origin.AllocatedBytes} steadyAllocatedBytes={steadyAllocated} " +
                $"gcPauseMs={(drainedAt.GcPause - origin.GcPause).TotalMilliseconds:F1} steadyGcPauseMs={steadyPauseMs:F1} " +
                $"cipher={cipher} " +
                $"heapBytes={drainedAt.HeapBytes} workingSet={drainedAt.WorkingSet} " +
                $"cpuMs={(drainedAt.Cpu - origin.Cpu).TotalMilliseconds:F0} workerSec={host.Processor?.WorkerSeconds ?? 0:F3} ingressOnly={ingressOnly} " +
                $"pipe={pipeUsed} pipeMax={pipeMax} initialCredit={initialCredit} windowFrames={windowFrames} windowBytes={windowBytes} windowAddMin={windowAddMin} windowAddMax={windowAddMax} creditWaitSec={creditWait:F3} slotWaitSec={slotWait:F3} " +
                $"resultN={resultSamples} resultMedianUs={resultMedianUs:F0} resultP95Us={resultP95Us:F0} incomplete={incomplete}";
        }

        private static (double MedianUs, double P95Us, int Count) PercentileMicroseconds(List<long> ticks)
        {
            if (ticks.Count == 0)
            {
                return (0, 0, 0);
            }

            ticks.Sort();
            var median = ticks[ticks.Count / 2];
            var p95Index = (int)((ticks.Count - 1) * 0.95d);
            var p95 = ticks[p95Index];
            var scale = 1_000_000d / Stopwatch.Frequency;
            return (median * scale, p95 * scale, ticks.Count);
        }

        private static string Rate(Sample start, Sample end, int artSize)
        {
            var seconds = (end.Elapsed - start.Elapsed).TotalSeconds;
            if (seconds <= 0)
            {
                return "seconds=0";
            }

            var accepted = end.Accepted - start.Accepted;
            var enqueued = end.Enqueued - start.Enqueued;
            var processed = end.Processed - start.Processed;
            var wire = end.WireBytes - start.WireBytes;
            var payload = enqueued * (double)BodyBytes;
            var canonical = enqueued * (double)artSize;
            var processedPayload = processed * (double)BodyBytes;
            return
                $"seconds={seconds:F3} enqueuedPerSec={enqueued / seconds:F1} acceptedPerSec={accepted / seconds:F1} processedPerSec={processed / seconds:F1} " +
                $"enqueueBodyGbps={Gbps(payload, seconds):F3} enqueueArtGbps={Gbps(canonical, seconds):F3} " +
                $"processedBodyGbps={Gbps(processedPayload, seconds):F3} " +
                $"wireGbps={Gbps(wire, seconds):F3} " +
                $"depthStart={start.Depth} depthEnd={end.Depth} queuedBytesEnd={end.QueuedBytes}";
        }

        private static double Gbps(double bytes, double seconds) => bytes * 8d / seconds / 1_000_000_000d;

        private static async Task<bool> WaitForProcessedAsync(BenchHost host, long expected, TimeSpan timeout)
        {
            var deadline = Stopwatch.StartNew();
            var processor = host.Processor ?? throw new InvalidOperationException("Path worker was not started.");
            while (processor.Processed + processor.Rejected < expected)
            {
                if (deadline.Elapsed > timeout)
                {
                    return false;
                }

                await Task.Delay(20);
            }

            return true;
        }

        private static async Task<bool> WaitForDequeuedAsync(BenchHost host, long expected, TimeSpan timeout)
        {
            var deadline = Stopwatch.StartNew();
            while (host.Queue.DequeuedCount < expected)
            {
                if (deadline.Elapsed > timeout)
                {
                    return false;
                }

                await Task.Delay(20);
            }

            return true;
        }

        private static int[] ParseSessions(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return [1, 2, 4, 8, 16];
            }

            return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
        }

        private static int ParsePositive(string? text, int fallback) =>
            int.TryParse(text, out var value) && value > 0 ? value : fallback;

        /// <summary>
        /// Parses <c>VECTORNNTPD_STORAGE_BENCH_WINDOW</c>. Unset means <see cref="ArticleTransferLimits.Default"/>.
        /// Any other value throws. Values are not clamped.
        /// </summary>
        internal static int ParseBenchWindow(string? text)
        {
            const int Kib = 1024;
            ReadOnlySpan<int> allowed = [256 * Kib, 512 * Kib, 1024 * Kib, 2 * 1024 * Kib, 4 * 1024 * Kib];
            if (string.IsNullOrWhiteSpace(text))
            {
                var production = ArticleTransferLimits.Default.InitialStreamWindowBytes;
                if (!allowed.Contains(production))
                {
                    throw new InvalidOperationException(
                        $"Production initial window {production} is not one of the benchmark sizes.");
                }

                return production;
            }

            if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
                || !allowed.Contains(value))
            {
                throw new InvalidOperationException(
                    "VECTORNNTPD_STORAGE_BENCH_WINDOW must be 262144, 524288, 1048576, 2097152, or 4194304 bytes.");
            }

            if (value > ArticleTransferLimits.Default.MaxStreamCreditBytes)
            {
                throw new InvalidOperationException(
                    $"Initial window {value} exceeds MaxStreamCreditBytes {ArticleTransferLimits.Default.MaxStreamCreditBytes}.");
            }

            return value;
        }

        private static Prepared[] BuildPool(int count)
        {
            var pool = new Prepared[count];
            var body = new byte[BodyBytes];
            for (var article = 0; article < count; article++)
            {
                Array.Fill(body, (byte)'x');
                for (var line = 0; line < BodyBytes / 100; line++)
                {
                    var offset = line * 100;
                    body[offset] = (byte)('A' + (article % 26));
                    body[offset + 1] = (byte)('0' + (line % 10));
                    body[offset + 98] = (byte)'\r';
                    body[offset + 99] = (byte)'\n';
                }

                var id = $"<b{article:0000}@bench.test>";
                pool[article] = Prepare(body, id, streamId: 1);
            }

            return pool;
        }

        private static Prepared Prepare(ReadOnlySpan<byte> body, string messageId, uint streamId)
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
            var parser = new NntpArticleParser("nntpd01.usenet.ninja");
            var created = ArticleRecordFactory.TryCreate(parser, text);
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
            var wire = store.Length + metaFrame.Length + end.Length + frames.Sum(static frame => frame.Length);
            return new Prepared(store, metaFrame, frames.ToArray(), lengths.ToArray(), end, created.Record.ArtSize, wire);
        }

        private readonly record struct Sample(
            TimeSpan Elapsed,
            long Enqueued,
            long Dequeued,
            long QueuedBytes,
            long Processed,
            long PathRejected,
            long Accepted,
            long Rejected,
            long WireBytes,
            int Gen0,
            int Gen1,
            int Gen2,
            long AllocatedBytes,
            TimeSpan GcPause,
            long HeapBytes,
            long WorkingSet,
            TimeSpan Cpu)
        {
            public long Depth => Enqueued - Dequeued;

            public static Sample Take(BenchHost host, IReadOnlyList<VatpLoadSession> sessions, TimeSpan elapsed)
            {
                long accepted = 0;
                long rejected = 0;
                long wire = 0;
                foreach (var session in sessions)
                {
                    accepted += session.Accepted;
                    rejected += session.Rejected;
                    wire += session.WireBytes;
                }

                using var process = Process.GetCurrentProcess();
                return new Sample(
                    elapsed,
                    host.Queue.EnqueuedCount,
                    host.Queue.DequeuedCount,
                    host.Queue.QueuedBytes,
                    host.Processor?.Processed ?? 0,
                    host.Processor?.Rejected ?? 0,
                    accepted,
                    rejected,
                    wire,
                    GC.CollectionCount(0),
                    GC.CollectionCount(1),
                    GC.CollectionCount(2),
                    GC.GetTotalAllocatedBytes(precise: false),
                    GC.GetTotalPauseDuration(),
                    GC.GetTotalMemory(forceFullCollection: false),
                    process.WorkingSet64,
                    process.TotalProcessorTime);
            }
        }

        private sealed record Prepared(
            byte[] Store,
            byte[] Meta,
            byte[][] Data,
            int[] DataLengths,
            byte[] End,
            int ArtSize,
            int WireBytes);

        private sealed class CountingProcessor : IAcceptedArticleProcessor
        {
            private readonly AcceptedArticlePathProcessor _inner;
            private long _processed;
            private long _rejected;
            private long _ticks;

            public CountingProcessor(Action<ReadOnlyMemory<byte>>? rewritten)
            {
                _inner = new AcceptedArticlePathProcessor(PathIdentity, NullLogger.Instance, rewritten);
            }

            public long Processed => Volatile.Read(ref _processed);

            public long Rejected => Volatile.Read(ref _rejected);

            public double WorkerSeconds => Volatile.Read(ref _ticks) / (double)Stopwatch.Frequency;

            public void EnsureReady() => _inner.EnsureReady();

            public ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                var started = Stopwatch.GetTimestamp();
                var result = _inner.ProcessAsync(article, cancellationToken).GetAwaiter().GetResult();
                Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - started);
                if (result != AcceptedArticleProcessResult.Completed)
                {
                    Interlocked.Increment(ref _rejected);
                    return ValueTask.FromResult(result);
                }

                Interlocked.Increment(ref _processed);
                return ValueTask.FromResult(result);
            }
        }

        internal static Task<PlacementIngressHost> StartPlacementIngressHostAsync() =>
            PlacementIngressHost.OpenAsync();

        internal sealed class PlacementIngressHost : IAsyncDisposable
        {
            private readonly BenchHost _host;

            private PlacementIngressHost(BenchHost host) => _host = host;

            public int Port => _host.Port;

            public AcceptedArticleQueue Queue => _host.Queue;

            public static async Task<PlacementIngressHost> OpenAsync() =>
                new(await BenchHost.StartAsync(rewritten: null, ingressOnly: true).ConfigureAwait(false));

            public ValueTask DisposeAsync() => _host.DisposeAsync();
        }

        private sealed class BenchHost : IAsyncDisposable
        {
            private readonly StorageVatpListenerService _listener;
            private readonly AcceptedArticleWorker? _worker;
            private readonly CancellationTokenSource? _drainCts;
            private readonly Task? _drain;

            private BenchHost(
                StorageVatpListenerService listener,
                AcceptedArticleWorker? worker,
                AcceptedArticleQueue queue,
                CountingProcessor? processor,
                int port,
                CancellationTokenSource? drainCts,
                Task? drain)
            {
                _listener = listener;
                _worker = worker;
                _drainCts = drainCts;
                _drain = drain;
                Queue = queue;
                Processor = processor;
                Port = port;
            }

            public AcceptedArticleQueue Queue { get; }

            public CountingProcessor? Processor { get; }

            public int Port { get; }

            public static async Task<BenchHost> StartAsync(
                Action<ReadOnlyMemory<byte>>? rewritten,
                bool ingressOnly = false,
                int? initialWindowBytes = null)
            {
                var port = StorageServerTestOptions.GetFreeTcpPort();
                var readiness = new AcmeCertificateReadiness();
                readiness.MarkReady();
                var queue = new AcceptedArticleQueue();
                CountingProcessor? processor = ingressOnly ? null : new CountingProcessor(rewritten);
                AcceptedArticleWorker? worker = processor is null
                    ? null
                    : new AcceptedArticleWorker(queue, processor, NullLogger<AcceptedArticleWorker>.Instance);
                ArticleTransferLimits? limits = initialWindowBytes is null
                    ? null
                    : new ArticleTransferLimits { InitialStreamWindowBytes = initialWindowBytes.Value };
                var listener = new StorageVatpListenerService(
                    CreateRuntime(port),
                    TestListenerCertificates.CreatePublishedProvider(),
                    NullStorageArticleOpenBoundary.Instance,
                    readiness,
                    NullLogger<StorageVatpListenerService>.Instance,
                    queue,
                    new StoreAssemblyAdmission(1024),
                    limits);
                await listener.StartAsync(CancellationToken.None);
                if (worker is not null)
                {
                    await worker.StartAsync(CancellationToken.None);
                    return new BenchHost(listener, worker, queue, processor, port, drainCts: null, drain: null);
                }

                var drainCts = new CancellationTokenSource();
                var drain = Task.Run(() => DrainAsync(queue, drainCts.Token));
                return new BenchHost(listener, worker: null, queue, processor: null, port, drainCts, drain);
            }

            public async ValueTask DisposeAsync()
            {
                await _listener.DisposeAsync();
                if (_drainCts is not null)
                {
                    await _drainCts.CancelAsync();
                    if (_drain is not null)
                    {
                        try
                        {
                            await _drain.WaitAsync(TimeSpan.FromSeconds(5));
                        }
                        catch (TimeoutException)
                        {
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }

                    _drainCts.Dispose();
                }

                if (_worker is not null)
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _worker.StopAsync(stop.Token);
                }
            }

            private static async Task DrainAsync(AcceptedArticleQueue queue, CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    AcceptedArticleDelivery delivery;
                    try
                    {
                        delivery = await queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (!delivery.HasArticle)
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

        private sealed class VatpLoadSession : IAsyncDisposable
        {
            private readonly SslStream _ssl;
            private readonly TcpClient _tcp;
            private readonly Task _reader;
            private readonly SemaphoreSlim _helloGate = new(0);
            private readonly SemaphoreSlim _doneGate = new(0);
            private readonly ConcurrentQueue<TransferSlot> _freeSlots = new();
            private readonly List<LatencySample> _latencies = [];
            private readonly object _latencyLock = new();
            private readonly CancellationTokenSource _dispose = new();
            private readonly byte[] _window = new byte[VatpProtocol.WindowPayloadLength];
            private readonly byte[] _outcome = new byte[1];
            private TransferSlot[]? _slots;
            private SemaphoreSlim? _slotGate;
            private long _accepted;
            private long _rejected;
            private long _wireBytes;
            private long _creditWaitTicks;
            private long _slotWaitTicks;
            private long _outstanding;
            private long _started;
            private long _completed;
            private int _pipeUsed;
            private int _pipeMaxOutstanding;
            private int _initialCredit;
            private int _windowFrames;
            private long _windowBytes;
            private int _windowAddMin = int.MaxValue;
            private int _windowAddMax;
            private Exception? _fault;

            private VatpLoadSession(TcpClient tcp, SslStream ssl)
            {
                _tcp = tcp;
                _ssl = ssl;
                _reader = ReadLoopAsync();
            }

            public long Accepted => Volatile.Read(ref _accepted);

            public long Rejected => Volatile.Read(ref _rejected);

            public long WireBytes => Volatile.Read(ref _wireBytes);

            public double CreditWaitSeconds => Volatile.Read(ref _creditWaitTicks) / (double)Stopwatch.Frequency;

            /// <summary>Gets time the single writer blocked because every stream slot was awaiting RESULT.</summary>
            public double SlotWaitSeconds => Volatile.Read(ref _slotWaitTicks) / (double)Stopwatch.Frequency;

            public int PipeUsed => _pipeUsed;

            public int PipeMaxOutstanding => Volatile.Read(ref _pipeMaxOutstanding);

            public long TransfersCompleted => Volatile.Read(ref _completed);

            public long TransfersOutstanding => Math.Max(0, Volatile.Read(ref _started) - Volatile.Read(ref _completed));

            public int InitialCredit => _initialCredit;

            public int WindowFrames => _windowFrames;

            public long WindowBytes => _windowBytes;

            public int WindowAddMin => _windowFrames == 0 ? 0 : _windowAddMin;

            public int WindowAddMax => _windowAddMax;

            public string NegotiatedCipher =>
                $"{_ssl.SslProtocol}/{_ssl.NegotiatedCipherSuite}";

            public static async Task<VatpLoadSession> ConnectAsync(int port)
            {
                var tcp = new TcpClient();
                tcp.NoDelay = true;
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
                var session = new VatpLoadSession(tcp, ssl);
                await session.WriteAsync(VatpFrameEncoder.ToSingleBuffer(
                    VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload)));
                if (!await session._helloGate.WaitAsync(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("StorageServer did not answer HELLO.");
                }

                if (session._fault is not null)
                {
                    throw session._fault;
                }

                return session;
            }

            public async Task SendArticlesAsync(
                IReadOnlyList<Prepared> pool,
                int? count,
                CancellationToken cancellationToken,
                int pipe = 1,
                int initialCredit = InitialWindowBytes)
            {
                if (initialCredit < 1 || initialCredit > ArticleTransferLimits.Default.MaxStreamCreditBytes)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(initialCredit),
                        initialCredit,
                        "Initial credit must be positive and no greater than MaxStreamCreditBytes.");
                }

                _initialCredit = initialCredit;
                if (_slots is not null)
                {
                    throw new InvalidOperationException("A load session sends one article loop.");
                }

                var limit = ArticleTransferLimits.Default.MaxStreamsPerConnection;
                _pipeUsed = Math.Clamp(pipe, 1, limit);
                var slots = new TransferSlot[_pipeUsed];
                for (var i = 0; i < slots.Length; i++)
                {
                    slots[i] = new TransferSlot((uint)(i + 1));
                    _freeSlots.Enqueue(slots[i]);
                }

                _slots = slots;
                _slotGate = new SemaphoreSlim(_pipeUsed, _pipeUsed);
                var sent = 0;
                var index = 0;
                while (count is null || sent < count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_fault is not null)
                    {
                        throw _fault;
                    }

                    var slot = await AcquireSlotAsync(cancellationToken);
                    var article = pool[index];
                    index = (index + 1) % pool.Count;
                    var now = Interlocked.Increment(ref _outstanding);
                    UpdatePipeMax(now);
                    Interlocked.Increment(ref _started);
                    Volatile.Write(ref slot.EndTimestamp, 0);
                    Volatile.Write(ref slot.Credit, initialCredit);
                    Volatile.Write(ref slot.AcceptWindow, 1);
                    Volatile.Write(ref slot.Active, 1);
                    await WriteStampedAsync(article.Store, slot.StreamId);
                    await WriteStampedAsync(article.Meta, slot.StreamId);
                    for (var frame = 0; frame < article.Data.Length; frame++)
                    {
                        await WaitForCreditAsync(slot, article.DataLengths[frame], cancellationToken);
                        await WriteStampedAsync(article.Data[frame], slot.StreamId);
                    }

                    Volatile.Write(ref slot.AcceptWindow, 0);
                    Volatile.Write(ref slot.EndTimestamp, Stopwatch.GetTimestamp());
                    await WriteStampedAsync(article.End, slot.StreamId);
                    sent++;
                }

                while (Volatile.Read(ref _outstanding) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_fault is not null)
                    {
                        throw _fault;
                    }

                    await _doneGate.WaitAsync(cancellationToken);
                }
            }

            public void CopySteadyLatencies(long warmupStamp, long measureStamp, List<long> latencies)
            {
                lock (_latencyLock)
                {
                    foreach (var sample in _latencies)
                    {
                        if (sample.CompletedTimestamp >= warmupStamp && sample.CompletedTimestamp <= measureStamp)
                        {
                            latencies.Add(sample.LatencyTicks);
                        }
                    }
                }
            }

            public async ValueTask DisposeAsync()
            {
                await _dispose.CancelAsync();
                try
                {
                    await _reader;
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }

                await _ssl.DisposeAsync();
                _tcp.Dispose();
                _dispose.Dispose();
                _helloGate.Dispose();
                _doneGate.Dispose();
                _slotGate?.Dispose();
                if (_slots is not null)
                {
                    foreach (var slot in _slots)
                    {
                        slot.CreditGate.Dispose();
                    }
                }
            }

            private async Task<TransferSlot> AcquireSlotAsync(CancellationToken cancellationToken)
            {
                var gate = _slotGate ?? throw new InvalidOperationException("Stream slots are not open.");
                if (!gate.Wait(0, cancellationToken))
                {
                    var started = Stopwatch.GetTimestamp();
                    await gate.WaitAsync(cancellationToken);
                    Interlocked.Add(ref _slotWaitTicks, Stopwatch.GetTimestamp() - started);
                }

                if (!_freeSlots.TryDequeue(out var slot))
                {
                    if (_fault is not null)
                    {
                        throw _fault;
                    }

                    throw new InvalidOperationException("Stream slot semaphore and free list diverged.");
                }

                return slot;
            }

            private void UpdatePipeMax(long outstanding)
            {
                int previous;
                do
                {
                    previous = Volatile.Read(ref _pipeMaxOutstanding);
                    if (outstanding <= previous)
                    {
                        return;
                    }
                }
                while (Interlocked.CompareExchange(ref _pipeMaxOutstanding, (int)outstanding, previous) != previous);
            }

            private async Task WaitForCreditAsync(TransferSlot slot, int bytes, CancellationToken cancellationToken)
            {
                var waiting = false;
                var started = 0L;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_fault is not null)
                    {
                        throw _fault;
                    }

                    var current = Volatile.Read(ref slot.Credit);
                    if (current >= bytes && Interlocked.CompareExchange(ref slot.Credit, current - bytes, current) == current)
                    {
                        if (waiting)
                        {
                            Interlocked.Add(ref _creditWaitTicks, Stopwatch.GetTimestamp() - started);
                        }

                        return;
                    }

                    if (!waiting)
                    {
                        waiting = true;
                        started = Stopwatch.GetTimestamp();
                    }

                    await slot.CreditGate.WaitAsync(cancellationToken);
                }
            }

            private async Task WriteStampedAsync(byte[] frame, uint streamId)
            {
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), streamId);
                await _ssl.WriteAsync(frame);
                Interlocked.Add(ref _wireBytes, frame.Length);
            }

            private async Task WriteAsync(byte[] frame)
            {
                await _ssl.WriteAsync(frame);
                Interlocked.Add(ref _wireBytes, frame.Length);
            }

            private async Task ReadLoopAsync()
            {
                var buffer = new byte[256 * 1024];
                var filled = 0;
                try
                {
                    while (!_dispose.IsCancellationRequested)
                    {
                        var read = await _ssl.ReadAsync(buffer.AsMemory(filled), _dispose.Token);
                        if (read == 0)
                        {
                            Fault(new IOException("VATP peer closed the connection."));
                            return;
                        }

                        filled += read;
                        var offset = 0;
                        Span<byte> window = _window;
                        Span<byte> outcome = _outcome;
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
                                return;
                            }

                            var frame = parsed.Frame.Value;
                            switch (frame.Header.Type)
                            {
                                case VatpFrameType.Hello:
                                    _helloGate.Release();
                                    break;
                                case VatpFrameType.Window:
                                    frame.Payload.CopyTo(window);
                                    if (!VatpControlPayload.TryDecodeWindow(window, out var add))
                                    {
                                        Fault(new InvalidOperationException("Invalid WINDOW."));
                                        return;
                                    }

                                    NoteWindow(add);
                                    ApplyWindow(frame.Header.StreamId, add);
                                    break;
                                case VatpFrameType.Result:
                                    frame.Payload.CopyTo(outcome);
                                    if (!TryCompleteSlot(frame.Header.StreamId, outcome[0] == VatpStoreOutcome.Accepted))
                                    {
                                        Fault(new InvalidOperationException($"RESULT for inactive stream {frame.Header.StreamId}."));
                                        return;
                                    }

                                    break;
                                case VatpFrameType.Fail:
                                    if (frame.Header.StreamId == VatpProtocol.ConnectionStreamId
                                        || !TryCompleteSlot(frame.Header.StreamId, accepted: false))
                                    {
                                        Interlocked.Increment(ref _rejected);
                                    }

                                    Fault(new InvalidOperationException("VATP FAIL during transfer."));
                                    WakeSenders();
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
                catch (OperationCanceledException) when (_dispose.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    Fault(ex);
                    WakeSenders();
                }
            }

            private void NoteWindow(uint add)
            {
                _windowFrames++;
                _windowBytes += add;
                if (add < _windowAddMin)
                {
                    _windowAddMin = (int)add;
                }

                if (add > _windowAddMax)
                {
                    _windowAddMax = (int)add;
                }
            }

            private void ApplyWindow(uint streamId, uint add)
            {
                var slot = FindSlot(streamId);
                if (slot is null)
                {
                    Fault(new InvalidOperationException($"WINDOW for unknown stream {streamId}."));
                    return;
                }

                if (Volatile.Read(ref slot.AcceptWindow) == 0)
                {
                    return;
                }

                Interlocked.Add(ref slot.Credit, add);
                slot.CreditGate.Release();
            }

            private bool TryCompleteSlot(uint streamId, bool accepted)
            {
                var slot = FindSlot(streamId);
                if (slot is null || Interlocked.Exchange(ref slot.Active, 0) != 1)
                {
                    return false;
                }

                Volatile.Write(ref slot.AcceptWindow, 0);
                if (accepted)
                {
                    Interlocked.Increment(ref _accepted);
                }
                else
                {
                    Interlocked.Increment(ref _rejected);
                }

                var end = Volatile.Read(ref slot.EndTimestamp);
                if (end != 0)
                {
                    var completed = Stopwatch.GetTimestamp();
                    lock (_latencyLock)
                    {
                        _latencies.Add(new LatencySample(completed, completed - end));
                    }
                }

                Interlocked.Increment(ref _completed);
                Interlocked.Decrement(ref _outstanding);
                _freeSlots.Enqueue(slot);
                _slotGate?.Release();
                slot.CreditGate.Release();
                _doneGate.Release();
                return true;
            }

            private TransferSlot? FindSlot(uint streamId)
            {
                var slots = _slots;
                if (slots is null || streamId == 0 || streamId > (uint)slots.Length)
                {
                    return null;
                }

                return slots[streamId - 1];
            }

            private void WakeSenders()
            {
                try
                {
                    _helloGate.Release();
                    _doneGate.Release();
                    _slotGate?.Release();
                    if (_slots is null)
                    {
                        return;
                    }

                    foreach (var slot in _slots)
                    {
                        slot.CreditGate.Release();
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            }

            private void Fault(Exception exception) =>
                Interlocked.CompareExchange(ref _fault, exception, null);

            private sealed class TransferSlot
            {
                public TransferSlot(uint streamId) => StreamId = streamId;

                public uint StreamId { get; }

                public SemaphoreSlim CreditGate { get; } = new(0);

                public long Credit;

                public int AcceptWindow;

                public int Active;

                public long EndTimestamp;
            }

            private readonly record struct LatencySample(long CompletedTimestamp, long LatencyTicks);
        }
    }
}
