using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Audit
{
    /// <summary>
    /// Before/after measurement for the destuff scratch change. No-op unless BACKFILLER_DESTUFF_OPT is before or after.
    /// </summary>
    public sealed class DestuffOptimizationMeasurementTests
    {
        /// <summary>Local article corpus. The measurement returns immediately when this directory is not used because the phase variable is unset.</summary>
        private const string CorpusRoot = ".artifacts/Articles";

        /// <summary>Worker counts measured before the separate 1000-worker, 100,000-operation run.</summary>
        private static readonly int[] Levels = [1, 16, 64, 256, 512, 1000, 2000];

        /// <summary>
        /// Writes a destuff report when <c>BACKFILLER_DESTUFF_OPT</c> is <c>before</c> or <c>after</c>.
        /// Any other value, including unset, returns without reading the corpus or asserting.
        /// When enabled, destuff and record mismatches must be zero.
        /// </summary>
        [Fact]
        public async Task MeasureDestuffOptimization_WhenEnabled()
        {
            var phase = Environment.GetEnvironmentVariable("BACKFILLER_DESTUFF_OPT");
            if (phase is not ("before" or "after"))
            {
                return;
            }

            var reportDir = Path.Join(
                ".artifacts",
                "backfiller-realworld-audit");
            Directory.CreateDirectory(reportDir);
            var reportPath = Path.Join(reportDir, $"destuff-opt-{phase}.txt");
            await using var report = new StreamWriter(File.Create(reportPath));
            report.AutoFlush = true;
            void Line(string text)
            {
                Console.WriteLine(text);
                report.WriteLine(text);
            }

            var files = Directory.EnumerateFiles(CorpusRoot, "*", SearchOption.AllDirectories).ToArray();
            Array.Sort(files, StringComparer.Ordinal);
            var parser = new NntpArticleParser("backfiller.test");
            var sample = LoadSample(files, parser, 256, 256L * 1024 * 1024);
            Line($"phase={phase} sample={sample.Count} bytes={sample.Sum(static article => (long)article.Bytes.Length)}");

            var median = sample.OrderBy(static article => article.Bytes.Length).ElementAt(sample.Count / 2);
            var cold = MeasureCold(median.Bytes);
            var warm = MeasureWarm(median.Bytes);
            Line($"cold bytes={median.Bytes.Length} out={cold.Output} alloc={cold.Alloc} us={cold.Microseconds:F1} equal={cold.Equal}");
            Line($"warm bytes={median.Bytes.Length} iterations={warm.Iterations} allocPerCall={warm.AllocPerCall} us={warm.Microseconds:F1} equal={warm.Equal}");

            var corpus = ScanCorpus(files, parser);
            Line($"corpus files={corpus.Files} destuffMismatch={corpus.DestuffMismatch} recordMismatch={corpus.RecordMismatch} accepted={corpus.Accepted} yenc={corpus.YEnc} bodyLine={corpus.BodyLine} otherFail={corpus.OtherFail} sec={corpus.Seconds:F1} firstMismatch={corpus.FirstMismatch}");
            Assert.Equal(0, corpus.DestuffMismatch);
            Assert.Equal(0, corpus.RecordMismatch);
            Assert.Equal(13, corpus.YEnc);
            Assert.Equal(1, corpus.BodyLine);
            Assert.Equal(0, corpus.OtherFail);

            var frames = new byte[sample.Count][];
            var ids = new string[sample.Count];
            for (var i = 0; i < sample.Count; i++)
            {
                frames[i] = Frame(sample[i].Bytes);
                ids[i] = sample[i].MessageId;
            }

            var retention = CreateRetention(512L * 1024 * 1024);
            foreach (var concurrency in Levels)
            {
                var ops = Math.Max(4000, concurrency * 4);
                var row = await RunLevelAsync(retention, parser, frames, ids, concurrency, ops, Math.Min(ops, Math.Max(concurrency, 64)));
                Line(Format(row));
            }

            var longRun = await RunLevelAsync(retention, parser, frames, ids, 1000, 100_000, 1000);
            Line("long " + Format(longRun));
            await retention.DisposeAsync();
        }

        /// <summary>Forces a collection, then times one destuff of a single framed copy and records bytes allocated by that read.</summary>
        private static ColdMeasure MeasureCold(byte[] article)
        {
            var framed = Frame(article);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var reader = new NntpStreamReader(new MemoryStream(framed, writable: false), 64 * 1024);
            var payload = reader.ReadArticlePayloadAsync(5 * 1024 * 1024, TimeSpan.FromSeconds(30), CancellationToken.None)
                .GetAwaiter().GetResult();
            var us = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            var alloc = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
            return new ColdMeasure(payload.Length, alloc, us, payload.AsSpan().SequenceEqual(article));
        }

        /// <summary>Warms two reads of a 24-copy buffer, then averages allocation and time across 20 more destuffs of the same article.</summary>
        private static WarmMeasure MeasureWarm(byte[] article)
        {
            var framed = Frame(article);
            var repeated = new byte[framed.Length * 24];
            for (var i = 0; i < 24; i++)
            {
                framed.CopyTo(repeated, i * framed.Length);
            }

            var reader = new NntpStreamReader(new MemoryStream(repeated, writable: false), 64 * 1024);
            for (var i = 0; i < 2; i++)
            {
                _ = reader.ReadArticlePayloadAsync(5 * 1024 * 1024, TimeSpan.FromSeconds(30), CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            const int iterations = 20;
            var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var equal = true;
            var output = 0;
            for (var i = 0; i < iterations; i++)
            {
                var payload = reader.ReadArticlePayloadAsync(5 * 1024 * 1024, TimeSpan.FromSeconds(30), CancellationToken.None)
                    .GetAwaiter().GetResult();
                output = payload.Length;
                if (!payload.AsSpan().SequenceEqual(article))
                {
                    equal = false;
                }
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            var alloc = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
            return new WarmMeasure(iterations, output, alloc / iterations, elapsed.TotalMicroseconds / iterations, equal);
        }

        /// <summary>Destuffs every corpus file and compares that payload with a direct parse of the original bytes. Counts mismatches; does not throw on a rejected article.</summary>
        private static CorpusScan ScanCorpus(string[] files, NntpArticleParser parser)
        {
            var scan = new CorpusScan { Files = files.Length };
            var started = Stopwatch.GetTimestamp();
            foreach (var file in files)
            {
                var bytes = File.ReadAllBytes(file);
                var framed = Frame(bytes);
                var reader = new NntpStreamReader(new MemoryStream(framed, writable: false), 64 * 1024);
                var payload = reader.ReadArticlePayloadAsync(5 * 1024 * 1024, TimeSpan.FromSeconds(30), CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (!payload.AsSpan().SequenceEqual(bytes))
                {
                    scan.DestuffMismatch++;
                    scan.FirstMismatch ??= file;
                }

                var direct = ArticleRecordFactory.TryCreate(parser, bytes, ArticlePathMode.Traverse);
                var via = ArticleRecordFactory.TryCreate(parser, payload, ArticlePathMode.Traverse);
                if (direct.IsAccepted != via.IsAccepted
                    || direct.ParseFailure != via.ParseFailure
                    || direct.MaterializeFailure != via.MaterializeFailure
                    || (direct.IsAccepted && (
                        direct.Record.ArtSize != via.Record.ArtSize
                        || direct.Record.ArtHash != via.Record.ArtHash
                        || direct.Record.ArtId != via.Record.ArtId
                        || !direct.Record.MessageId.SequenceEqual(via.Record.MessageId)
                        || !direct.Record.ArtData.Span.SequenceEqual(via.Record.ArtData.Span))))
                {
                    scan.RecordMismatch++;
                }

                if (via.IsAccepted)
                {
                    scan.Accepted++;
                }
                else if (via.ParseFailure == NntpArticleParseFailureCode.YEncDecodingFailed)
                {
                    scan.YEnc++;
                }
                else if (via.ParseFailure == NntpArticleParseFailureCode.BodyLineTooLong)
                {
                    scan.BodyLine++;
                }
                else
                {
                    scan.OtherFail++;
                }
            }

            scan.Seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            return scan;
        }

        /// <summary>Discards one warmup pass, then records throughput, latency percentiles, allocation, GC, and CPU for one concurrency level.</summary>
        private static async Task<LevelRow> RunLevelAsync(
            ArticleRetentionAuthority retention,
            NntpArticleParser parser,
            byte[][] frames,
            string[] ids,
            int concurrency,
            int operations,
            int warmup)
        {
            await ExecuteAsync(retention, parser, frames, ids, concurrency, warmup);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var pauseBefore = GC.GetTotalPauseDuration();
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var locksBefore = Monitor.LockContentionCount;
            var started = Stopwatch.GetTimestamp();
            var latencies = await ExecuteAsync(retention, parser, frames, ids, concurrency, operations);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var allocAfter = GC.GetTotalAllocatedBytes(precise: true);
            var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalSeconds / elapsed.TotalSeconds;
            var pauseMs = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
            Array.Sort(latencies);
            long logical = 0;
            for (var i = 0; i < operations; i++)
            {
                logical += frames[i % frames.Length].Length;
            }

            var process = Process.GetCurrentProcess();
            var info = GC.GetGCMemoryInfo();
            return new LevelRow(
                concurrency,
                operations,
                elapsed.TotalSeconds,
                operations / elapsed.TotalSeconds,
                logical / elapsed.TotalSeconds / (1024 * 1024),
                Percentile(latencies, 0.50),
                Percentile(latencies, 0.95),
                Percentile(latencies, 0.99),
                (allocAfter - allocBefore) / (double)operations,
                (allocAfter - allocBefore) / elapsed.TotalSeconds,
                GC.CollectionCount(0) - gen0,
                GC.CollectionCount(1) - gen1,
                GC.CollectionCount(2) - gen2,
                pauseMs,
                cpu,
                process.WorkingSet64,
                GenerationSize(info, 3),
                GenerationSize(info, 4),
                ThreadPool.PendingWorkItemCount,
                Monitor.LockContentionCount - locksBefore);
        }

        /// <summary>Runs <paramref name="operations"/> handler calls on <paramref name="concurrency"/> workers. A non-success outcome throws. Each request is cancelled after success so retention does not fill.</summary>
        private static async Task<double[]> ExecuteAsync(
            ArticleRetentionAuthority retention,
            NntpArticleParser parser,
            byte[][] frames,
            string[] ids,
            int concurrency,
            int operations)
        {
            var latencies = new double[operations];
            var next = 0;
            var workers = new Task[concurrency];
            for (var worker = 0; worker < concurrency; worker++)
            {
                workers[worker] = Task.Run(async () =>
                {
                    var stream = new OnDemandFrameStream();
                    var reader = new NntpStreamReader(stream, 64 * 1024);
                    var retriever = new DestuffRetriever(stream, reader, frames, ids);
                    var handler = new ProviderArticleWorkHandler(retriever, retention, parser);
                    var channel = new FakeBackFillerRabbitMqChannel(1);
                    while (true)
                    {
                        var index = Interlocked.Increment(ref next) - 1;
                        if (index >= operations)
                        {
                            return;
                        }

                        var requestId = Guid.NewGuid();
                        var item = new ArticleWorkItem(
                            new ArticleWorkRequest(1, requestId, ids[index % ids.Length], "audit"),
                            "corr",
                            "reply",
                            new ArticleWorkSettlementLease(channel, (ulong)index + 1, 1));
                        var start = Stopwatch.GetTimestamp();
                        var result = await handler.HandleAsync(item, CancellationToken.None);
                        latencies[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        if (result.Outcome != ArticleWorkOutcome.Success)
                        {
                            throw new InvalidOperationException($"{result.Outcome} {result.Error} at {index}");
                        }

                        if (!retention.TryCancelPendingRequest(requestId))
                        {
                            throw new InvalidOperationException($"request {requestId} was not openable");
                        }
                    }
                });
            }

            await Task.WhenAll(workers);
            return latencies;
        }

        /// <summary>Keeps accepted articles with distinct Message-IDs until <paramref name="maxFiles"/> or <paramref name="maxBytes"/> is reached. Files without a Message-ID are skipped.</summary>
        private static List<Sample> LoadSample(string[] files, NntpArticleParser parser, int maxFiles, long maxBytes)
        {
            var sample = new List<Sample>(maxFiles);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long bytes = 0;
            foreach (var file in files)
            {
                if (sample.Count >= maxFiles || bytes >= maxBytes)
                {
                    break;
                }

                var payload = File.ReadAllBytes(file);
                if (!TryMessageId(payload, out var id) || !seen.Add(id))
                {
                    continue;
                }

                if (!ArticleRecordFactory.TryCreate(parser, payload, ArticlePathMode.Traverse).IsAccepted)
                {
                    continue;
                }

                sample.Add(new Sample(id, payload));
                bytes += payload.Length;
            }

            return sample;
        }

        /// <summary>Reads the first <c>Message-ID:</c> header. Stops at the header/body blank line. Does not unfold continuations.</summary>
        private static bool TryMessageId(ReadOnlySpan<byte> article, out string messageId)
        {
            messageId = "";
            var lineStart = 0;
            while (lineStart < article.Length)
            {
                var rest = article[lineStart..];
                var newline = rest.IndexOf((byte)'\n');
                var line = newline < 0 ? rest : rest[..newline];
                if (line.Length > 0 && line[^1] == (byte)'\r')
                {
                    line = line[..^1];
                }

                if (line.Length == 0)
                {
                    return false;
                }

                if (line.Length >= 11 && line.StartsWith("Message-ID:"u8))
                {
                    messageId = System.Text.Encoding.ASCII.GetString(line[11..].Trim((byte)' '));
                    return messageId.Length > 0;
                }

                if (newline < 0)
                {
                    return false;
                }

                lineStart += newline + 1;
            }

            return false;
        }

        /// <summary>Adds one stuffing dot to lines that already start with a dot and appends the NNTP terminating dot line. Does not change the retained article bytes.</summary>
        private static byte[] Frame(byte[] article)
        {
            using var buffer = new MemoryStream(article.Length + 64);
            var offset = 0;
            while (offset < article.Length)
            {
                var lineEnd = Array.IndexOf(article, (byte)'\n', offset);
                var next = lineEnd < 0 ? article.Length : lineEnd + 1;
                var contentEnd = lineEnd < 0
                    ? article.Length
                    : (lineEnd > offset && article[lineEnd - 1] == (byte)'\r' ? lineEnd - 1 : lineEnd);
                if (contentEnd > offset && article[offset] == (byte)'.')
                {
                    buffer.WriteByte((byte)'.');
                }

                buffer.Write(article, offset, next - offset);
                if (lineEnd < 0)
                {
                    buffer.WriteByte((byte)'\r');
                    buffer.WriteByte((byte)'\n');
                }

                offset = next;
            }

            buffer.WriteByte((byte)'.');
            buffer.WriteByte((byte)'\r');
            buffer.WriteByte((byte)'\n');
            return buffer.ToArray();
        }

        /// <summary>Builds a one-hour retention authority with a one-minute sweep interval and the given payload cap. It is not started as a hosted service.</summary>
        private static ArticleRetentionAuthority CreateRetention(long maximumBytes) =>
            new(
                new BackFillerArticleRetentionRuntimeOptions(maximumBytes, TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), 256),
                "backfiller.test",
                563,
                TimeProvider.System,
                NullLogger<ArticleRetentionAuthority>.Instance);

        /// <summary>Nearest-rank percentile of an already sorted latency array. The index is clamped inside the array.</summary>
        private static double Percentile(double[] sorted, double percentile)
        {
            var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }

        /// <summary>Size in bytes of GC generation <paramref name="generation"/> after the last collection. Returns -1 when that generation is absent.</summary>
        private static long GenerationSize(GCMemoryInfo info, int generation)
        {
            var generations = info.GenerationInfo;
            return (uint)generation < (uint)generations.Length ? generations[generation].SizeAfterBytes : -1;
        }

        /// <summary>One report line for a concurrency measurement. Values are formatted for the text file, not asserted.</summary>
        private static string Format(LevelRow row) =>
            $"concurrency={row.Concurrency} ops={row.Operations} sec={row.Seconds:F2} articlesPerSec={row.ArticlesPerSecond:F1} miBPerSec={row.MebibytesPerSecond:F1} p50ms={row.P50:F2} p95ms={row.P95:F2} p99ms={row.P99:F2} allocPerArticle={row.AllocPerArticle:F0} allocPerSec={row.AllocPerSecond:F0} gen0={row.Gen0} gen1={row.Gen1} gen2={row.Gen2} pauseMs={row.PauseMilliseconds:F1} cpu={row.Cpu:F2} rss={row.WorkingSet} loh={row.Loh} poh={row.Poh} pending={row.Pending} contentions={row.Contentions}";

        /// <summary>Serves preframed articles through one shared reader. It does not open a socket, and it always reports 220 after the reader returns.</summary>
        private sealed class DestuffRetriever(OnDemandFrameStream stream, NntpStreamReader reader, byte[][] frames, string[] ids) : INntpArticleRetriever
        {
            /// <summary>Framed NNTP payloads keyed by Message-ID for on-demand destuff measurements.</summary>
            private readonly Dictionary<string, byte[]> _frames = Build(frames, ids);

            /// <summary>Resets the shared stream to this Message-ID's frame and destuffs it. The retrieval result is always 220; rejection is the handler's concern.</summary>
            public async Task<ArticleRetrievalResult> RetrieveAsync(
                ArticleWorkItem item,
                int maxArticleBytes,
                Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult> consumePayload,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(consumePayload);
                stream.Begin(_frames[item.Request.MessageId]);
                _ = await reader.ReadArticlePayloadAsync(
                        5 * 1024 * 1024,
                        TimeSpan.FromMinutes(1),
                        consumePayload,
                        cancellationToken)
                    .ConfigureAwait(false);
                return ArticleRetrievalResult.Retrieved(220, "article follows");
            }

            /// <summary>Pairs each Message-ID with the framed bytes at the same index. The two arrays must be the same length.</summary>
            private static Dictionary<string, byte[]> Build(byte[][] framed, string[] messageIds)
            {
                var map = new Dictionary<string, byte[]>(messageIds.Length, StringComparer.Ordinal);
                for (var i = 0; i < messageIds.Length; i++)
                {
                    map[messageIds[i]] = framed[i];
                }

                return map;
            }
        }

        /// <summary>Read-only stream over one framed article. <see cref="Begin"/> replaces the buffer. Seek, write, and length changes throw.</summary>
        private sealed class OnDemandFrameStream : Stream
        {
            /// <summary>Framed bytes installed by <see cref="Begin"/>. Empty until the first call.</summary>
            private byte[] _frame = [];
            /// <summary>Next unread index in <see cref="_frame"/>.</summary>
            private int _offset;

            /// <summary>Installs <paramref name="frame"/> without copying it and rewinds the read cursor.</summary>
            internal void Begin(byte[] frame)
            {
                _frame = frame;
                _offset = 0;
            }

            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => false;

            /// <inheritdoc />
            public override long Length => _frame.Length;

            /// <summary>Read cursor. Setting it throws <see cref="NotSupportedException"/>.</summary>
            public override long Position { get => _offset; set => throw new NotSupportedException(); }

            /// <summary>No-op. The stream has no buffer of its own.</summary>
            public override void Flush()
            {
            }

            /// <summary>Copies the next unread framed bytes into <paramref name="buffer"/> starting at <paramref name="offset"/>. Returns 0 at the end of the frame.</summary>
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            /// <summary>Copies the next unread framed bytes into <paramref name="buffer"/>. Returns 0 at the end of the frame.</summary>
            public override int Read(Span<byte> buffer)
            {
                if (_offset >= _frame.Length)
                {
                    return 0;
                }

                var n = Math.Min(buffer.Length, _frame.Length - _offset);
                _frame.AsSpan(_offset, n).CopyTo(buffer);
                _offset += n;
                return n;
            }

            /// <summary>Same bytes as <see cref="Read(Span{byte})"/>, completed synchronously after a cancellation check.</summary>
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<int>(Read(buffer.Span));
            }

            /// <summary>Throws <see cref="NotSupportedException"/>. The frame is only read from the start.</summary>
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <summary>Throws <see cref="NotSupportedException"/>.</summary>
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <summary>Throws <see cref="NotSupportedException"/>. The frame is supplied by <see cref="Begin"/>.</summary>
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>One accepted corpus article kept for the timed runs.</summary>
        private readonly record struct Sample(string MessageId, byte[] Bytes);

        /// <summary>One forced-collection destuff: payload length, bytes allocated, microseconds, and whether the payload matched the article.</summary>
        private readonly record struct ColdMeasure(int Output, long Alloc, double Microseconds, bool Equal);

        /// <summary>Averages from the warm loop: iteration count, last payload length, allocated bytes per call, microseconds per call, and equality.</summary>
        private readonly record struct WarmMeasure(int Iterations, int Output, long AllocPerCall, double Microseconds, bool Equal);

        /// <summary>Counters from one full-corpus destuff and parse comparison.</summary>
        private sealed class CorpusScan
        {
            /// <summary>Corpus files processed in the destuff scan pass.</summary>
            public int Files { get; set; }

            /// <summary>Destuffed bytes that did not round-trip expected logical content.</summary>
            public int DestuffMismatch { get; set; }

            /// <summary>Built records that disagreed with source identity or size.</summary>
            public int RecordMismatch { get; set; }

            /// <summary>Articles accepted by parse and factory in the scan.</summary>
            public int Accepted { get; set; }

            /// <summary>Files whose parse failure is <see cref="NntpArticleParseFailureCode.YEncDecodingFailed"/>.</summary>
            public int YEnc { get; set; }

            /// <summary>Files whose parse failure is <see cref="NntpArticleParseFailureCode.BodyLineTooLong"/>.</summary>
            public int BodyLine { get; set; }

            /// <summary>Failures outside destuff, record, yEnc, and body-line buckets.</summary>
            public int OtherFail { get; set; }

            /// <summary>Elapsed seconds for the corpus scan.</summary>
            public double Seconds { get; set; }

            /// <summary>First mismatch label recorded for debugging output.</summary>
            public string? FirstMismatch { get; set; }
        }

        /// <summary>One concurrency row written to the measurement report. It is not compared with a baseline in this test.</summary>
        private readonly record struct LevelRow(
            int Concurrency,
            int Operations,
            double Seconds,
            double ArticlesPerSecond,
            double MebibytesPerSecond,
            double P50,
            double P95,
            double P99,
            double AllocPerArticle,
            double AllocPerSecond,
            int Gen0,
            int Gen1,
            int Gen2,
            double PauseMilliseconds,
            double Cpu,
            long WorkingSet,
            long Loh,
            long Poh,
            long Pending,
            long Contentions);
    }
}
