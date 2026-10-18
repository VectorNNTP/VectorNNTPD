using System.Diagnostics;
using System.IO.Hashing;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.BackFillerService.Tests.Audit
{
    /// <summary>
    /// Manual real-corpus audit driver. Returns immediately unless BACKFILLER_AUDIT=1.
    /// Not part of the default correctness suite. Does not change production code.
    /// </summary>
    public sealed class BackFillerRealWorldAuditTests
    {
        /// <summary>Directory of on-disk articles. Read only when <c>BACKFILLER_AUDIT</c> is <c>1</c>.</summary>
        private const string CorpusRoot = ".artifacts/Articles";

        /// <summary>Worker counts for the timed handler loop, before the separate 1000-worker run.</summary>
        private static readonly int[] ConcurrencyLevels = [1, 16, 64, 256, 512, 1000, 2000];

        /// <summary>
        /// Writes a corpus report when <c>BACKFILLER_AUDIT</c> is <c>1</c>. Any other value returns without reading files.
        /// When enabled, body, identity, hash, and size mismatches in the functional pass must be zero.
        /// </summary>
        [Fact]
        public async Task RealCorpusAudit_WhenEnabled()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("BACKFILLER_AUDIT"), "1", StringComparison.Ordinal))
            {
                return;
            }

            var files = Directory.EnumerateFiles(CorpusRoot, "*", SearchOption.AllDirectories).ToArray();
            Array.Sort(files, StringComparer.Ordinal);
            var reportDir = Path.Join(
                ".artifacts",
                "backfiller-realworld-audit");
            Directory.CreateDirectory(reportDir);
            var reportPath = Path.Join(reportDir, "MEASUREMENTS.txt");
            await using var report = new StreamWriter(File.Create(reportPath));
            report.AutoFlush = true;
            void Line(string text)
            {
                Console.WriteLine(text);
                report.WriteLine(text);
            }

            Line($"machine cores={Environment.ProcessorCount} files={files.Length}");
            var header = ScanHeaders(files);
            Line(
                $"headerScan files={header.Files} withMessageId={header.WithMessageId} missingMessageId={header.MissingMessageId} noSeparator={header.NoSeparator} duplicateIds={header.DuplicateIds} uniqueIds={header.UniqueIds}");

            var parser = new NntpArticleParser("backfiller.test");
            var functionalRetention = CreateRetention(maximumBytes: 64L * 1024 * 1024);
            var functional = await RunFunctionalAsync(files, parser, functionalRetention, Line);
            Line(
                $"functional accepted={functional.Accepted} failed={functional.Failed} bodyMismatch={functional.BodyMismatch} idMismatch={functional.IdMismatch} hashMismatch={functional.HashMismatch} artIdMismatch={functional.ArtIdMismatch} sizeMismatch={functional.SizeMismatch} elapsedSec={functional.Seconds:F1} retainedBytes={functionalRetention.RetainedPayloadBytes} retainedCount={functionalRetention.RetainedCount}");
            foreach (var pair in functional.Failures.OrderByDescending(static pair => pair.Value))
            {
                Line($"functionalFailure {pair.Key}={pair.Value}");
            }

            await functionalRetention.DisposeAsync();
            Assert.Equal(0, functional.BodyMismatch);
            Assert.Equal(0, functional.IdMismatch);
            Assert.Equal(0, functional.HashMismatch);
            Assert.Equal(0, functional.ArtIdMismatch);
            Assert.Equal(0, functional.SizeMismatch);

            var sample = LoadSample(files, parser, maxFiles: 256, maxBytes: 256L * 1024 * 1024);
            Line($"preload files={sample.Count} bytes={sample.Sum(static article => (long)article.Bytes.Length)}");
            var retention = CreateRetention(maximumBytes: 512L * 1024 * 1024);
            var retriever = new IndexedRetriever(sample);
            var handler = new ProviderArticleWorkHandler(retriever, retention, parser);
            var channel = new FakeBackFillerRabbitMqChannel(1);

            foreach (var concurrency in ConcurrencyLevels)
            {
                var ops = Math.Max(4000, concurrency * 4);
                var row = await RunConcurrentAsync(handler, retention, channel, sample, concurrency, ops, warmupOps: Math.Min(ops, Math.Max(concurrency, 64)));
                Line(FormatRow(row));
            }

            var destuff = MeasureDestuff(sample[0].Bytes);
            Line(
                $"destuff bytes={sample[0].Bytes.Length} iterations={destuff.Iterations} allocPerCall={destuff.AllocPerCall} equal={destuff.RoundTripEqual}");

            await RunFailureCasesAsync(retention, parser, sample[0], Line);

            var longRun = await RunConcurrentAsync(
                handler,
                retention,
                channel,
                sample,
                concurrency: 1000,
                operations: 100_000,
                warmupOps: 1000);
            Line("longRun " + FormatRow(longRun));
            Line($"retentionBytesAfter={retention.RetainedPayloadBytes} retainedCount={retention.RetainedCount}");
            await retention.DisposeAsync();
        }

        /// <summary>Parses and retains every file, then cancels the request so the article does not stay pinned. Progress is written every 2000 files.</summary>
        private static async Task<FunctionalResult> RunFunctionalAsync(
            string[] files,
            NntpArticleParser parser,
            ArticleRetentionAuthority retention,
            Action<string> line)
        {
            var result = new FunctionalResult();
            var started = Stopwatch.GetTimestamp();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < files.Length; i++)
            {
                var bytes = await File.ReadAllBytesAsync(files[i]);
                if (!TryReadMessageId(bytes, out var messageId))
                {
                    result.Failed++;
                    Increment(result.Failures, "MissingMessageId");
                    continue;
                }

                if (!seen.Add(messageId))
                {
                    Increment(result.Failures, "DuplicateMessageIdFile");
                }

                var created = ArticleRecordFactory.TryCreate(parser, bytes, ArticlePathMode.Traverse);
                if (!created.IsAccepted)
                {
                    result.Failed++;
                    var code = created.ParseFailure != NntpArticleParseFailureCode.None
                        ? created.ParseFailure.ToString()
                        : created.MaterializeFailure.ToString();
                    Increment(result.Failures, code);
                    continue;
                }

                var record = created.Record;
                if (record.ArtSize != record.ArtData.Length)
                {
                    result.SizeMismatch++;
                }

                if (!record.MessageId.SequenceEqual(System.Text.Encoding.ASCII.GetBytes(messageId)))
                {
                    result.IdMismatch++;
                }

                if (record.ArtId != ArticleId.FromMessageId(record.MessageId))
                {
                    result.ArtIdMismatch++;
                }

                if (record.ArtHash != XxHash3.HashToUInt64(record.ArtData.Span))
                {
                    result.HashMismatch++;
                }

                if (!BodyEquals(bytes, record.ArtData.Span))
                {
                    result.BodyMismatch++;
                }

                var requestId = Guid.NewGuid();
                var retained = retention.RetainCanonical(messageId, requestId, record, created.SelectedDateHeaderName);
                if (!retained.IsAvailable)
                {
                    result.Failed++;
                    Increment(result.Failures, "Retain" + retained.Kind);
                    continue;
                }

                Assert.True(retention.TryCancelPendingRequest(requestId));
                result.Accepted++;
                if ((i + 1) % 2000 == 0)
                {
                    line($"functional progress {i + 1}/{files.Length} accepted={result.Accepted} failed={result.Failed}");
                }
            }

            result.Seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            return result;
        }

        /// <summary>Discards <paramref name="warmupOps"/> handler calls, then records throughput, latency, allocation, and retention size for one concurrency.</summary>
        private static async Task<ConcurrencyRow> RunConcurrentAsync(
            ProviderArticleWorkHandler handler,
            ArticleRetentionAuthority retention,
            FakeBackFillerRabbitMqChannel channel,
            IReadOnlyList<SampleArticle> sample,
            int concurrency,
            int operations,
            int warmupOps)
        {
            await ExecuteAsync(handler, retention, channel, sample, concurrency, warmupOps);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var started = Stopwatch.GetTimestamp();
            var latencies = await ExecuteAsync(handler, retention, channel, sample, concurrency, operations);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var allocAfter = GC.GetTotalAllocatedBytes(precise: true);
            Array.Sort(latencies);
            var process = Process.GetCurrentProcess();
            var info = GC.GetGCMemoryInfo();
            long logicalBytes = 0;
            for (var i = 0; i < operations; i++)
            {
                logicalBytes += sample[i % sample.Count].Bytes.Length;
            }

            return new ConcurrencyRow(
                concurrency,
                operations,
                elapsed.TotalSeconds,
                operations / elapsed.TotalSeconds,
                logicalBytes / elapsed.TotalSeconds / (1024 * 1024),
                Percentile(latencies, 0.50),
                Percentile(latencies, 0.95),
                Percentile(latencies, 0.99),
                latencies[^1],
                (allocAfter - allocBefore) / (double)operations,
                GC.CollectionCount(0) - gen0,
                GC.CollectionCount(1) - gen1,
                GC.CollectionCount(2) - gen2,
                process.WorkingSet64,
                process.PrivateMemorySize64,
                info.HeapSizeBytes,
                GenerationSize(info, 3),
                GenerationSize(info, 4),
                ThreadPool.PendingWorkItemCount,
                ThreadPool.ThreadCount,
                retention.RetainedPayloadBytes,
                retention.RetainedCount);
        }

        /// <summary>Runs <paramref name="operations"/> successes across <paramref name="concurrency"/> workers. A non-success outcome throws, and each request is cancelled afterward.</summary>
        private static async Task<double[]> ExecuteAsync(
            ProviderArticleWorkHandler handler,
            ArticleRetentionAuthority retention,
            FakeBackFillerRabbitMqChannel channel,
            IReadOnlyList<SampleArticle> sample,
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
                    while (true)
                    {
                        var index = Interlocked.Increment(ref next) - 1;
                        if (index >= operations)
                        {
                            return;
                        }

                        var article = sample[index % sample.Count];
                        var requestId = Guid.NewGuid();
                        var item = new ArticleWorkItem(
                            new ArticleWorkRequest(1, requestId, article.MessageId, "audit"),
                            "corr",
                            "reply",
                            new ArticleWorkSettlementLease(channel, (ulong)index + 1, 1));
                        var start = Stopwatch.GetTimestamp();
                        var result = await handler.HandleAsync(item, CancellationToken.None);
                        latencies[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        if (result.Outcome != ArticleWorkOutcome.Success)
                        {
                            throw new InvalidOperationException(
                                $"concurrency outcome {result.Outcome} {result.Error} at {index}");
                        }

                        if (!retention.TryCancelPendingRequest(requestId))
                        {
                            throw new InvalidOperationException($"request {requestId} was not openable after success");
                        }
                    }
                });
            }

            await Task.WhenAll(workers);
            return latencies;
        }

        /// <summary>Checks not-found, cancellation, Message-ID mismatch, a one-byte article, and the success, provider-failure, and cancelled dispositions.</summary>
        private static async Task RunFailureCasesAsync(
            ArticleRetentionAuthority retention,
            NntpArticleParser parser,
            SampleArticle article,
            Action<string> line)
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var failing = new ScriptedRetriever();
            var handler = new ProviderArticleWorkHandler(failing, retention, parser);
            failing.Next = ArticleRetrievalResult.Failed(
                ArticleRetrievalKind.ArticleNotFound,
                430,
                "no such article",
                sessionReusable: true);
            var missing = await handler.HandleAsync(Item(channel, article.MessageId), CancellationToken.None);
            line($"failure notFound outcome={missing.Outcome}");
            Assert.Equal(ArticleWorkOutcome.ArticleNotFound, missing.Outcome);

            failing.Next = ArticleRetrievalResult.Retrieved(220, "ok", new RetrievedArticle((byte[])article.Bytes.Clone()));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            var cancel = await handler.HandleAsync(Item(channel, article.MessageId), cancelled.Token);
            line($"failure cancelled outcome={cancel.Outcome}");
            Assert.Equal(ArticleWorkOutcome.Cancelled, cancel.Outcome);

            failing.Next = ArticleRetrievalResult.Retrieved(220, "ok", new RetrievedArticle((byte[])article.Bytes.Clone()));
            var mismatch = await handler.HandleAsync(Item(channel, "<not-the-article@example>"), CancellationToken.None);
            line($"failure mismatch outcome={mismatch.Outcome} error={mismatch.Error}");
            Assert.Equal(ArticleWorkOutcome.InvalidArticle, mismatch.Outcome);

            failing.Next = ArticleRetrievalResult.Retrieved(220, "ok", new RetrievedArticle([(byte)'x']));
            var malformed = await handler.HandleAsync(Item(channel, article.MessageId), CancellationToken.None);
            line($"failure malformed outcome={malformed.Outcome} error={malformed.Error}");
            Assert.Equal(ArticleWorkOutcome.InvalidArticle, malformed.Outcome);

            var success = ArticleWorkDispositionPlanner.Create(ArticleWorkOutcome.Success, replyable: true, cancellationRequested: false);
            var requeue = ArticleWorkDispositionPlanner.Create(ArticleWorkOutcome.ProviderFailure, replyable: true, cancellationRequested: false);
            var cancelledDisposition = ArticleWorkDispositionPlanner.Create(ArticleWorkOutcome.Success, replyable: true, cancellationRequested: true);
            line($"disposition success ack={success.Acknowledge} requeue={success.Requeue}");
            line($"disposition providerFailure ack={requeue.Acknowledge} requeue={requeue.Requeue}");
            line($"disposition cancelled ack={cancelledDisposition.Acknowledge} requeue={cancelledDisposition.Requeue}");
            Assert.True(success.Acknowledge);
            Assert.False(success.Requeue);
            Assert.False(requeue.Acknowledge);
            Assert.True(requeue.Requeue);
            Assert.False(cancelledDisposition.Acknowledge);
            Assert.True(cancelledDisposition.Requeue);
        }

        /// <summary>Destuffs one framed article 20 times and records average allocated bytes and whether every payload matched.</summary>
        private static DestuffMeasure MeasureDestuff(byte[] article)
        {
            var framed = FrameAsNntp(article);
            const int iterations = 20;
            var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var equal = true;
            for (var i = 0; i < iterations; i++)
            {
                var reader = new NntpStreamReader(new MemoryStream(framed, writable: false), 64 * 1024);
                var payload = reader.ReadArticlePayloadAsync(5 * 1024 * 1024, TimeSpan.FromSeconds(30), CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                if (!payload.AsSpan().SequenceEqual(article))
                {
                    equal = false;
                }
            }

            var alloc = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
            return new DestuffMeasure(iterations, alloc / iterations, equal);
        }

        /// <summary>Stuffs lines that start with a dot and appends the NNTP terminating dot line.</summary>
        private static byte[] FrameAsNntp(byte[] article)
        {
            using var buffer = new MemoryStream(article.Length + 64);
            var offset = 0;
            while (offset < article.Length)
            {
                var lineEnd = Array.IndexOf(article, (byte)'\n', offset);
                var next = lineEnd < 0 ? article.Length : lineEnd + 1;
                var contentEnd = lineEnd < 0 ? article.Length : (lineEnd > offset && article[lineEnd - 1] == (byte)'\r' ? lineEnd - 1 : lineEnd);
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

        /// <summary>Keeps accepted articles with distinct Message-IDs until <paramref name="maxFiles"/> or <paramref name="maxBytes"/>.</summary>
        private static List<SampleArticle> LoadSample(
            string[] files,
            NntpArticleParser parser,
            int maxFiles,
            long maxBytes)
        {
            var sample = new List<SampleArticle>(maxFiles);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long bytes = 0;
            foreach (var file in files)
            {
                if (sample.Count >= maxFiles || bytes >= maxBytes)
                {
                    break;
                }

                var payload = File.ReadAllBytes(file);
                if (!TryReadMessageId(payload, out var messageId) || !seen.Add(messageId))
                {
                    continue;
                }

                if (!ArticleRecordFactory.TryCreate(parser, payload, ArticlePathMode.Traverse).IsAccepted)
                {
                    continue;
                }

                sample.Add(new SampleArticle(messageId, payload));
                bytes += payload.Length;
            }

            return sample;
        }

        /// <summary>Counts Message-IDs in the first 16 KiB of each file. A missing separator is counted only inside that prefix.</summary>
        private static HeaderScan ScanHeaders(string[] files)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var scan = new HeaderScan { Files = files.Length };
            var buffer = new byte[16 * 1024];
            foreach (var file in files)
            {
                int read;
                using (var stream = File.OpenRead(file))
                {
                    read = stream.Read(buffer, 0, buffer.Length);
                }

                var span = buffer.AsSpan(0, read);
                if (span.IndexOf("\r\n\r\n"u8) < 0 && span.IndexOf("\n\n"u8) < 0)
                {
                    scan.NoSeparator++;
                }

                if (!TryReadMessageId(span, out var messageId))
                {
                    scan.MissingMessageId++;
                    continue;
                }

                scan.WithMessageId++;
                if (!ids.Add(messageId))
                {
                    scan.DuplicateIds++;
                }
            }

            scan.UniqueIds = ids.Count;
            return scan;
        }

        /// <summary>ASCII equality that folds A-Z to a-z. Other bytes, including non-ASCII, must match exactly.</summary>
        private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                var a = left[i];
                var b = right[i];
                if (a is >= (byte)'A' and <= (byte)'Z')
                {
                    a += 32;
                }

                if (b is >= (byte)'A' and <= (byte)'Z')
                {
                    b += 32;
                }

                if (a != b)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Reads the first Message-ID header, ignoring ASCII case of the name. Stops at the blank line and does not unfold continuations.</summary>
        private static bool TryReadMessageId(ReadOnlySpan<byte> article, out string messageId)
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

                if (line.Length >= 11 && AsciiEqualsIgnoreCase(line[..11], "Message-ID:"u8))
                {
                    var value = line[11..].Trim((byte)' ');
                    messageId = System.Text.Encoding.ASCII.GetString(value);
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

        /// <summary>Compares the bodies after the first blank line. Headers are not compared.</summary>
        private static bool BodyEquals(ReadOnlySpan<byte> source, ReadOnlySpan<byte> canonical)
        {
            var sourceBody = Body(source);
            var canonicalBody = Body(canonical);
            return sourceBody.SequenceEqual(canonicalBody);
        }

        /// <summary>Bytes after the first CRLF CRLF, or after the first LF LF. Empty when neither separator is present.</summary>
        private static ReadOnlySpan<byte> Body(ReadOnlySpan<byte> article)
        {
            var crlf = article.IndexOf("\r\n\r\n"u8);
            if (crlf >= 0)
            {
                return article[(crlf + 4)..];
            }

            var lf = article.IndexOf("\n\n"u8);
            return lf >= 0 ? article[(lf + 2)..] : [];
        }

        /// <summary>One-hour retention with a one-minute sweep interval and the given payload cap. The sweep service is not started.</summary>
        private static ArticleRetentionAuthority CreateRetention(long maximumBytes) =>
            new(
                new BackFillerArticleRetentionRuntimeOptions(
                    maximumBytes,
                    TimeSpan.FromHours(1),
                    TimeSpan.FromMinutes(1),
                    256),
                "backfiller.test",
                563,
                TimeProvider.System,
                NullLogger<ArticleRetentionAuthority>.Instance);

        /// <summary>One audit work item on delivery tag 1. The request id is new on every call.</summary>
        private static ArticleWorkItem Item(FakeBackFillerRabbitMqChannel channel, string messageId) =>
            new(
                new ArticleWorkRequest(1, Guid.NewGuid(), messageId, "audit"),
                "corr",
                "reply",
                new ArticleWorkSettlementLease(channel, 1, 1));

        /// <summary>Adds one to <paramref name="key"/>, inserting it when absent.</summary>
        private static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out var value);
            counts[key] = value + 1;
        }

        /// <summary>Nearest-rank percentile of a sorted array. The index is clamped inside the array.</summary>
        private static double Percentile(double[] sorted, double percentile)
        {
            var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }

        /// <summary>Bytes in GC generation <paramref name="generation"/> after the last collection, or -1 when that generation is absent.</summary>
        private static long GenerationSize(GCMemoryInfo info, int generation)
        {
            var generations = info.GenerationInfo;
            return (uint)generation < (uint)generations.Length ? generations[generation].SizeAfterBytes : -1;
        }

        /// <summary>One report line. The numbers are written to the measurement file and are not compared with a baseline.</summary>
        private static string FormatRow(ConcurrencyRow row) =>
            $"concurrency={row.Concurrency} ops={row.Operations} sec={row.Seconds:F2} articlesPerSec={row.ArticlesPerSecond:F1} miBPerSec={row.MebibytesPerSecond:F1} p50ms={row.P50:F2} p95ms={row.P95:F2} p99ms={row.P99:F2} maxms={row.Max:F2} allocPerArticle={row.AllocPerArticle:F0} gen0={row.Gen0} gen1={row.Gen1} gen2={row.Gen2} workingSet={row.WorkingSet} privateBytes={row.PrivateBytes} heap={row.Heap} loh={row.Loh} poh={row.Poh} pendingWork={row.PendingWorkItems} threads={row.Threads} retainedBytes={row.RetainedBytes} retainedCount={row.RetainedCount}";

        /// <summary>Returns corpus bytes for a known Message-ID. It ignores <c>maxArticleBytes</c> and does not open a provider session.</summary>
        private sealed class IndexedRetriever(IReadOnlyList<SampleArticle> sample) : INntpArticleRetriever
        {
            /// <summary>Message-ID to raw article bytes served without live NNTP I/O.</summary>
            private readonly Dictionary<string, byte[]> _payloads = sample.ToDictionary(
                static article => article.MessageId,
                static article => article.Bytes,
                StringComparer.Ordinal);

            /// <summary>Invokes the payload callback and returns 220. A cancelled token returns failure and does not call the callback.</summary>
            public Task<ArticleRetrievalResult> RetrieveAsync(
                ArticleWorkItem item,
                int maxArticleBytes,
                Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult> consumePayload,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(consumePayload);
                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromResult(ArticleRetrievalResult.Failed(
                        ArticleRetrievalKind.Cancelled,
                        null,
                        "cancelled",
                        sessionReusable: false));
                }

                var source = _payloads[item.Request.MessageId];
                _ = consumePayload(source);
                return Task.FromResult(ArticleRetrievalResult.Retrieved(220, "article follows"));
            }
        }

        /// <summary>Returns whatever <see cref="Next"/> the test stored. It does not parse or size-check the payload.</summary>
        private sealed class ScriptedRetriever : INntpArticleRetriever
        {
            /// <summary>Result returned by the next retrieve. Defaults to a provider failure named unset.</summary>
            public ArticleRetrievalResult Next { get; set; } = ArticleRetrievalResult.Failed(
                ArticleRetrievalKind.ProviderFailure,
                null,
                "unset",
                sessionReusable: false);

            /// <summary>Returns <see cref="Next"/>. When that result carries article bytes, the callback is invoked and its return value is ignored.</summary>
            public Task<ArticleRetrievalResult> RetrieveAsync(
                ArticleWorkItem item,
                int maxArticleBytes,
                Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult> consumePayload,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(consumePayload);
                if (Next.Article is not null)
                {
                    _ = consumePayload(Next.Article.Memory);
                }

                return Task.FromResult(Next);
            }
        }

        /// <summary>Counters from one pass over every corpus file.</summary>
        private sealed class FunctionalResult
        {
            /// <summary>Articles that completed the audited pipeline successfully.</summary>
            public int Accepted { get; set; }

            /// <summary>Articles that failed before or during pipeline processing.</summary>
            public int Failed { get; set; }

            /// <summary>Articles whose body after the blank line differs from <c>ArtData</c>.</summary>
            public int BodyMismatch { get; set; }

            /// <summary>Message-ID field disagreements after processing.</summary>
            public int IdMismatch { get; set; }

            /// <summary>Content hash disagreements for accepted articles.</summary>
            public int HashMismatch { get; set; }

            /// <summary>Computed ArtId disagreements versus expected identity.</summary>
            public int ArtIdMismatch { get; set; }

            /// <summary>Accepted records whose <c>ArtSize</c> is not <c>ArtData.Length</c>.</summary>
            public int SizeMismatch { get; set; }

            /// <summary>Wall-clock seconds for the functional audit pass.</summary>
            public double Seconds { get; set; }

            /// <summary>Parse and retain failure names and how many files produced each one.</summary>
            public Dictionary<string, int> Failures { get; } = new(StringComparer.Ordinal);
        }

        /// <summary>Header counts from the first 16 KiB of each file.</summary>
        private sealed class HeaderScan
        {
            /// <summary>Total corpus files examined in the header scan.</summary>
            public int Files { get; set; }

            /// <summary>Files containing a recognizable Message-ID header.</summary>
            public int WithMessageId { get; set; }

            /// <summary>Files without a Message-ID header.</summary>
            public int MissingMessageId { get; set; }

            /// <summary>Files lacking a header/body separator.</summary>
            public int NoSeparator { get; set; }

            /// <summary>Duplicate Message-ID values observed across files.</summary>
            public int DuplicateIds { get; set; }

            /// <summary>Distinct Message-ID values counted.</summary>
            public int UniqueIds { get; set; }
        }

        /// <summary>One accepted article kept for the concurrent and failure runs.</summary>
        private readonly record struct SampleArticle(string MessageId, byte[] Bytes);

        /// <summary>Twenty destuff iterations: average allocated bytes and whether every payload matched the article.</summary>
        private readonly record struct DestuffMeasure(int Iterations, long AllocPerCall, bool RoundTripEqual);

        /// <summary>One concurrency measurement written to the report. It is not checked against a stored baseline.</summary>
        private readonly record struct ConcurrencyRow(
            int Concurrency,
            int Operations,
            double Seconds,
            double ArticlesPerSecond,
            double MebibytesPerSecond,
            double P50,
            double P95,
            double P99,
            double Max,
            double AllocPerArticle,
            int Gen0,
            int Gen1,
            int Gen2,
            long WorkingSet,
            long PrivateBytes,
            long Heap,
            long Loh,
            long Poh,
            long PendingWorkItems,
            int Threads,
            long RetainedBytes,
            int RetainedCount);
    }
}
