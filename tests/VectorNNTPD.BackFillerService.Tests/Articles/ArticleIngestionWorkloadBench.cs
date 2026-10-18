using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using VectorNNTPD.Common.Articles.Processing;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Retention;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.YEnc;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Articles
{
    /// <summary>
    /// Synthetic ingestion measurements for the real parser, canonicalizer, hasher, and reader.
    /// Sizes are constructed, not captured articles. Timing assertions are intentionally absent.
    /// </summary>
    public sealed class ArticleIngestionWorkloadBench
    {
        /// <summary>
        /// Runs factory, pipeline, stage, scan, concurrent, retention, and CRC measurements for synthetic sizes; console output only.
        /// </summary>
        [Fact]
        public async Task Measure_synthetic_article_sizes()
        {
            Console.WriteLine(
                $"runtime={RuntimeInformation.FrameworkDescription} arch={RuntimeInformation.ProcessArchitecture} cpus={Environment.ProcessorCount} avx2={Avx2.IsSupported} pclmul={Pclmulqdq.IsSupported} sse42={Sse42.IsSupported}");

            var parser = new NntpArticleParser("backfiller.test");
            var samples = new (string Name, byte[] Logical, bool ExpectAccepted)[]
            {
                ("small-plain", Article("small", Repeat("line\r\n"u8, 64), includePath: true), true),
                ("32KiB-plain", Article("p32", Repeat("line\r\n"u8, 32 * 1024), includePath: true), true),
                ("256KiB-plain", Article("p256", Repeat("line\r\n"u8, 256 * 1024), includePath: true), true),
                ("750KiB-plain", Article("p750", Repeat("line\r\n"u8, 750 * 1024), includePath: true), true),
                ("750KiB-mixed", Article("m750", MixedBody(750 * 1024), includePath: true), true),
                ("750KiB-dot", Article("d750", Repeat(".\r\n"u8, 750 * 1024), includePath: true), true),
                ("750KiB-yenc", YEncArticle(750 * 1024), true),
                ("750KiB-path-insert", Article("path750", Repeat("line\r\n"u8, 750 * 1024), includePath: false), true),
                ("1MiB-plain", Article("p1m", Repeat("line\r\n"u8, (1024 * 1024) - 180), includePath: true), true),
                ("2MiB-plain", Article("p2m", Repeat("line\r\n"u8, 2 * 1024 * 1024), includePath: true), true),
                ("750KiB-truncated", Truncate(YEncArticle(750 * 1024), 64), false),
            };

            foreach (var sample in samples)
            {
                var iterations = sample.Name.StartsWith("750KiB", StringComparison.Ordinal) ? 48 : 24;
                if (!sample.ExpectAccepted)
                {
                    MeasureRejection(parser, sample.Name, sample.Logical, iterations);
                    continue;
                }

                MeasureFactory(parser, sample.Name, sample.Logical, iterations);
                await MeasurePipelineAsync(parser, sample.Name, sample.Logical, Math.Min(iterations, 16));
            }

            MeasureStages(parser, Article("p750", Repeat("line\r\n"u8, 750 * 1024), includePath: true), "750KiB-plain");
            var yenc = YEncArticle(750 * 1024);
            MeasureStages(parser, yenc, "750KiB-yenc");
            MeasureStages(parser, Article("d750", Repeat(".\r\n"u8, 750 * 1024), includePath: true), "750KiB-dot");
            MeasureScanCandidates(Article("d750", Repeat(".\r\n"u8, 750 * 1024), includePath: true), "750KiB-dot");
            MeasureScanCandidates(Article("p750", Repeat("line\r\n"u8, 750 * 1024), includePath: true), "750KiB-plain");
            MeasureConcurrent(parser, Article("p750", Repeat("line\r\n"u8, 750 * 1024), includePath: true), "750KiB-plain", 64);
            MeasureConcurrent(parser, Article("d750", Repeat(".\r\n"u8, 750 * 1024), includePath: true), "750KiB-dot", 64);

            Console.WriteLine("layers: factory=TryCreate only; pipeline=NntpStreamReader consume callback plus TryCreate; retention=authority admission of an already built record. Live provider and RabbitMQ are not measured. Percentiles are order statistics of the printed sample count, not confidence intervals. p99 with fewer than 100 samples is a single tail observation.");
            // Generation deltas below are not taken from a forced collection. They are not comparable to a run that called GC.Collect first.
            var plain = Article("p750", Repeat("line\r\n"u8, 750 * 1024), includePath: true);
            var mixed = Article("m750", MixedBody(750 * 1024), includePath: true);
            var dots = Article("d750", Repeat(".\r\n"u8, 750 * 1024), includePath: true);
            var path = Article("path750", Repeat("line\r\n"u8, 750 * 1024), includePath: false);
            for (var run = 1; run <= 3; run++)
            {
                MeasureFactory(parser, $"run{run}-750KiB-plain", plain, 48);
                MeasureFactory(parser, $"run{run}-750KiB-mixed", mixed, 48);
                MeasureFactory(parser, $"run{run}-750KiB-dot", dots, 48);
                MeasureFactory(parser, $"run{run}-750KiB-yenc", yenc, 48);
                MeasureFactory(parser, $"run{run}-750KiB-path", path, 48);
                await MeasurePipelineAsync(parser, $"run{run}-750KiB-plain", plain, 16);
                await MeasurePipelineAsync(parser, $"run{run}-750KiB-mixed", mixed, 16);
                await MeasurePipelineAsync(parser, $"run{run}-750KiB-dot", dots, 16);
                await MeasurePipelineAsync(parser, $"run{run}-750KiB-yenc", yenc, 16);
                await MeasurePipelineAsync(parser, $"run{run}-750KiB-path", path, 16);
            }

            await MeasureRetentionAsync(parser, plain);
            MeasureCrc();
        }

        /// <summary>Times repeated <see cref="ArticleRecordFactory.TryCreate"/> and prints throughput, percentiles, and allocation.</summary>
        /// <param name="parser">Shared parser.</param>
        /// <param name="name">Sample label.</param>
        /// <param name="logical">Destuffed logical bytes.</param>
        /// <param name="iterations">Timed iterations after warmup.</param>
        private static void MeasureFactory(NntpArticleParser parser, string name, byte[] logical, int iterations)
        {
            for (var i = 0; i < 2; i++)
            {
                Assert.True(ArticleRecordFactory.TryCreate(parser, logical, ArticlePathMode.Traverse).IsAccepted);
            }

            var samples = new long[iterations];
            long allocated = 0;
            var managedPeak = GC.GetTotalMemory(forceFullCollection: false);
            var g0 = GC.CollectionCount(0);
            var g1 = GC.CollectionCount(1);
            var g2 = GC.CollectionCount(2);
            int wire = 0;
            for (var i = 0; i < iterations; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                var created = ArticleRecordFactory.TryCreate(parser, logical, ArticlePathMode.Traverse);
                samples[i] = Stopwatch.GetTimestamp() - start;
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.True(created.IsAccepted, created.ParseFailure.ToString());
                wire = created.Record.ArtSize;
                var managed = GC.GetTotalMemory(forceFullCollection: false);
                if (managed > managedPeak)
                {
                    managedPeak = managed;
                }
            }

            var elapsed = samples.Sum();
            Array.Sort(samples);
            var ms = elapsed * 1000.0 / Stopwatch.Frequency;
            var mib = LogicalMebibytes(logical.Length, iterations);
            Console.WriteLine(
                $"{name} factory logical={logical.Length} wire={wire} n={iterations} articles/s={iterations / (ms / 1000.0):F0} MiB/s={mib / (ms / 1000.0):F1} alloc/article={allocated / iterations} p50us={TicksToUs(samples[PercentileIndex(iterations, 50)]):F0} p95us={TicksToUs(samples[PercentileIndex(iterations, 95)]):F0} p99us={TicksToUs(samples[PercentileIndex(iterations, 99)]):F0} gen0={GC.CollectionCount(0) - g0} gen1={GC.CollectionCount(1) - g1} gen2={GC.CollectionCount(2) - g2} managedPeak={managedPeak}");
        }

        /// <summary>Times reader destuff plus factory callback on a cycling framed stream.</summary>
        /// <param name="parser">Shared parser.</param>
        /// <param name="name">Sample label.</param>
        /// <param name="logical">Logical article bytes.</param>
        /// <param name="iterations">Timed iterations after one cold read.</param>
        private static async Task MeasurePipelineAsync(NntpArticleParser parser, string name, byte[] logical, int iterations)
        {
            var wire = Frame(logical);
            var reader = new NntpStreamReader(new CyclingStream(wire), 64 * 1024);
            var maxBytes = logical.Length;
            var coldBefore = GC.GetAllocatedBytesForCurrentThread();
            var callerThread = Environment.CurrentManagedThreadId;
            var offThread = 0;
            var cold = await reader.ReadArticlePayloadAsync(
                maxBytes,
                TimeSpan.FromSeconds(30),
                memory =>
                {
                    if (Environment.CurrentManagedThreadId != callerThread)
                    {
                        offThread++;
                    }

                    return ArticleRecordFactory.TryCreate(parser, memory, ArticlePathMode.Traverse);
                },
                CancellationToken.None);
            var coldAlloc = GC.GetAllocatedBytesForCurrentThread() - coldBefore;
            Assert.True(cold.IsAccepted, cold.ParseFailure.ToString());

            var samples = new long[iterations];
            long allocated = 0;
            var g0 = GC.CollectionCount(0);
            var g1 = GC.CollectionCount(1);
            var g2 = GC.CollectionCount(2);
            for (var i = 0; i < iterations; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                var created = await reader.ReadArticlePayloadAsync(
                    maxBytes,
                    TimeSpan.FromSeconds(30),
                    memory =>
                    {
                        if (Environment.CurrentManagedThreadId != callerThread)
                        {
                            offThread++;
                        }

                        return ArticleRecordFactory.TryCreate(parser, memory, ArticlePathMode.Traverse);
                    },
                    CancellationToken.None);
                samples[i] = Stopwatch.GetTimestamp() - start;
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            }

            var elapsed = samples.Sum();
            Array.Sort(samples);
            var ms = elapsed * 1000.0 / Stopwatch.Frequency;
            var mib = LogicalMebibytes(logical.Length, iterations);
            var workingSet = Process.GetCurrentProcess().WorkingSet64;
            Console.WriteLine(
                $"{name} pipeline logical={logical.Length} n={iterations} articles/s={iterations / (ms / 1000.0):F0} MiB/s={mib / (ms / 1000.0):F1} alloc/article={allocated / iterations} coldAlloc={coldAlloc} offThreadCallbacks={offThread} p50us={TicksToUs(samples[PercentileIndex(iterations, 50)]):F0} p95us={TicksToUs(samples[PercentileIndex(iterations, 95)]):F0} p99us={TicksToUs(samples[PercentileIndex(iterations, 99)]):F0} gen0={GC.CollectionCount(0) - g0} gen1={GC.CollectionCount(1) - g1} gen2={GC.CollectionCount(2) - g2} workingSet={workingSet}");
        }

        /// <summary>Times rejected <see cref="ArticleRecordFactory.TryCreate"/> attempts (for example truncated yEnc).</summary>
        /// <param name="parser">Shared parser.</param>
        /// <param name="name">Sample label.</param>
        /// <param name="logical">Bytes expected to reject.</param>
        /// <param name="iterations">Timed iterations.</param>
        private static void MeasureRejection(NntpArticleParser parser, string name, byte[] logical, int iterations)
        {
            var samples = new long[iterations];
            long allocated = 0;
            for (var i = 0; i < iterations; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                var created = ArticleRecordFactory.TryCreate(parser, logical, ArticlePathMode.Traverse);
                samples[i] = Stopwatch.GetTimestamp() - start;
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.False(created.IsAccepted);
            }

            var elapsed = samples.Sum();
            var ms = elapsed * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine($"{name} rejection logical={logical.Length} n={iterations} rejects/s={iterations / (ms / 1000.0):F0} alloc/article={allocated / iterations}");
        }

        /// <summary>Prints average microseconds for parse, materialize, hash, and full yEnc validation stages.</summary>
        /// <param name="parser">Shared parser.</param>
        /// <param name="logical">Accepted logical article.</param>
        /// <param name="name">Sample label.</param>
        private static void MeasureStages(NntpArticleParser parser, byte[] logical, string name)
        {
            const int iterations = 24;
            var parseTicks = Time("parse", () =>
            {
                var parsed = parser.Parse(logical);
                Assert.True(parsed.IsAccepted);
            }, iterations);
            var parsed = parser.Parse(logical);
            var materializeTicks = Time("materialize", () =>
            {
                var materialized = NntpArticleCanonicalMaterializer.Materialize(parsed, ArticlePathMode.Traverse);
                Assert.True(materialized.IsAccepted);
            }, iterations);
            var materialized = NntpArticleCanonicalMaterializer.Materialize(parsed, ArticlePathMode.Traverse);
            var hashTicks = Time("hash", () =>
            {
                _ = XxHash3.HashToUInt64(materialized.ArticleBytes);
            }, iterations);
            var yencTicks = Time("yenc", () =>
            {
                var validation = YEncArticleValidator.Validate(parsed.BodyBytes.Span);
                Assert.True(validation.IsValid);
            }, iterations);
            Console.WriteLine(
                $"{name} stages n={iterations} parseUs={TicksToUs(parseTicks / iterations):F0} materializeUs={TicksToUs(materializeTicks / iterations):F0} hashUs={TicksToUs(hashTicks / iterations):F0} fullValidatorUs={TicksToUs(yencTicks / iterations):F0}");
        }

        /// <summary>Compares production dot counting against an index-of baseline on the parsed body.</summary>
        /// <param name="logical">Logical article containing a body to scan.</param>
        /// <param name="name">Sample label.</param>
        private static void MeasureScanCandidates(byte[] logical, string name)
        {
            var parsedBodyStart = logical.AsSpan().IndexOf("\r\n\r\n"u8);
            Assert.True(parsedBodyStart > 0);
            var body = logical.AsSpan(parsedBodyStart + 4).ToArray();
            Assert.Equal(ArticleBodyWireStuffer.CountExtraDots(body), CountExtraDotsIndexOf(body));
            const int iterations = 40;
            var scalar = Time("scalar", () =>
            {
                var extra = ArticleBodyWireStuffer.CountExtraDots(body);
                if (name.Contains("dot", StringComparison.Ordinal) && extra == 0)
                {
                    throw new InvalidOperationException("dot fixture produced no stuffing dots");
                }
            }, iterations);
            var indexed = Time("index", () =>
            {
                _ = CountExtraDotsIndexOf(body);
            }, iterations);
            Console.WriteLine(
                $"{name} scan n={iterations} body={body.Length} scalarUs={TicksToUs(scalar / iterations):F0} indexOfUs={TicksToUs(indexed / iterations):F0}");
        }

        /// <summary>Parallel factory throughput and allocation snapshot for one logical article.</summary>
        /// <param name="parser">Shared parser.</param>
        /// <param name="logical">Logical bytes.</param>
        /// <param name="name">Sample label.</param>
        /// <param name="operations">Concurrent worker count.</param>
        private static void MeasureConcurrent(NntpArticleParser parser, byte[] logical, string name, int operations)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workers = new Task[operations];
            var g0 = GC.CollectionCount(0);
            var g1 = GC.CollectionCount(1);
            var g2 = GC.CollectionCount(2);
            var before = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < operations; i++)
            {
                workers[i] = Task.Run(() =>
                {
                    gate.Task.GetAwaiter().GetResult();
                    var created = ArticleRecordFactory.TryCreate(parser, logical, ArticlePathMode.Traverse);
                    if (!created.IsAccepted)
                    {
                        throw new InvalidOperationException(created.ParseFailure.ToString());
                    }
                });
            }

            var start = Stopwatch.GetTimestamp();
            gate.TrySetResult();
            Task.WaitAll(workers);
            var elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Console.WriteLine(
                $"{name} concurrent={operations} elapsedMs={elapsedMs:F1} articles/s={operations / (elapsedMs / 1000.0):F0} alloc/article={allocated / operations} gen0={GC.CollectionCount(0) - g0} gen1={GC.CollectionCount(1) - g1} gen2={GC.CollectionCount(2) - g2} workingSet={Process.GetCurrentProcess().WorkingSet64}");
        }

        /// <summary>Prints allocation for first retain versus duplicate <see cref="IArticleRetentionAuthority.RetainCanonical"/> on one record.</summary>
        /// <param name="parser">Parser used to build the record.</param>
        /// <param name="logical">750KiB-class plain sample.</param>
        private static async Task MeasureRetentionAsync(NntpArticleParser parser, byte[] logical)
        {
            var created = ArticleRecordFactory.TryCreate(parser, logical, ArticlePathMode.Traverse);
            Assert.True(created.IsAccepted);
            await using var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, 8 * 1024 * 1024);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var admitted = authority.RetainCanonical(
                "<p750@example.test>",
                Guid.NewGuid(),
                created.Record,
                created.SelectedDateHeaderName);
            var admitAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(ArticleRetentionKind.Retained, admitted.Kind);

            before = GC.GetAllocatedBytesForCurrentThread();
            var duplicate = authority.RetainCanonical(
                "<p750@example.test>",
                Guid.NewGuid(),
                created.Record,
                created.SelectedDateHeaderName);
            var duplicateAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, duplicate.Kind);
            Assert.Equal(created.Record.ArtSize, authority.RetainedPayloadBytes);
            Console.WriteLine(
                $"750KiB-plain retention wire={created.Record.ArtSize} admitAlloc={admitAlloc} duplicateAlloc={duplicateAlloc} retainedBytes={authority.RetainedPayloadBytes}");
        }

        /// <summary>Compares scalar yEnc CRC32 against <see cref="Crc32"/> block and chunked append timings.</summary>
        private static void MeasureCrc()
        {
            const int bytes = 750 * 1024;
            var decoded = new byte[bytes];
            var scalar = YEncCrc32.Compute(decoded);
            var accelerated = new Crc32();
            accelerated.Append(decoded);
            Assert.Equal(scalar, accelerated.GetCurrentHashAsUInt32());

            const int iterations = 20;
            var scalarTicks = Time("scalar-crc", () => _ = YEncCrc32.Compute(decoded), iterations);
            var blockTicks = Time("block-crc", () =>
            {
                accelerated.Reset();
                accelerated.Append(decoded);
                _ = accelerated.GetCurrentHashAsUInt32();
            }, iterations);
            var chunkTicks = Time("chunk-crc", () =>
            {
                accelerated.Reset();
                for (var offset = 0; offset < decoded.Length; offset += 512)
                {
                    accelerated.Append(decoded.AsSpan(offset, Math.Min(512, decoded.Length - offset)));
                }

                _ = accelerated.GetCurrentHashAsUInt32();
            }, iterations);
            Console.WriteLine(
                $"crc32 decoded={bytes} n={iterations} scalarUs={TicksToUs(scalarTicks / iterations):F0} hashingBlockUs={TicksToUs(blockTicks / iterations):F0} hashing512Us={TicksToUs(chunkTicks / iterations):F0}");
        }

        /// <summary>Runs one warmup plus <paramref name="iterations"/> timed calls and returns total elapsed ticks.</summary>
        /// <param name="_">Unused label for call-site readability.</param>
        /// <param name="action">Stage under measurement.</param>
        /// <param name="iterations">Timed repetitions after warmup.</param>
        /// <returns>Stopwatch ticks across timed iterations.</returns>
        private static long Time(string _, Action action, int iterations)
        {
            action();
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++)
            {
                action();
            }

            return Stopwatch.GetTimestamp() - start;
        }

        /// <summary>Reference dot-line counter used to validate <see cref="ArticleBodyWireStuffer.CountExtraDots"/> in scan benches.</summary>
        /// <param name="body">Parsed body span.</param>
        /// <returns>Count of body lines that begin with a dot.</returns>
        private static int CountExtraDotsIndexOf(ReadOnlySpan<byte> body)
        {
            var extra = 0;
            var index = 0;
            while (index < body.Length)
            {
                var start = index;
                var newline = body[index..].IndexOf((byte)'\n');
                int contentEnd;
                if (newline < 0)
                {
                    index = body.Length;
                    contentEnd = index;
                }
                else
                {
                    contentEnd = index + newline;
                    index = contentEnd + 1;
                }

                if (contentEnd > start && body[contentEnd - 1] == (byte)'\r')
                {
                    contentEnd--;
                }

                if (contentEnd > start && body[start] == (byte)'.')
                {
                    extra++;
                }
            }

            return extra;
        }

        /// <summary>Maps a percentile to a zero-based index in a sorted sample array.</summary>
        /// <param name="count">Sample length.</param>
        /// <param name="percentile">Percentile in [0, 100].</param>
        /// <returns>Clamped index for order statistics.</returns>
        private static int PercentileIndex(int count, int percentile) =>
            Math.Clamp((int)Math.Ceiling(count * (percentile / 100.0)) - 1, 0, count - 1);

        /// <summary>Converts stopwatch ticks to microseconds for bench console lines.</summary>
        /// <param name="ticks">Elapsed ticks.</param>
        /// <returns>Microseconds at the current frequency.</returns>
        private static double TicksToUs(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

        /// <summary>Builds ASCII logical articles with optional Path header.</summary>
        /// <param name="id">Message-ID local part.</param>
        /// <param name="body">Body bytes after headers.</param>
        /// <param name="includePath">When true, includes a Path header line.</param>
        /// <returns>Concatenated header and body.</returns>
        private static byte[] Article(string id, byte[] body, bool includePath)
        {
            var header = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
                + $"Message-ID: <{id}@example.test>\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "Subject: s\r\n"
                + (includePath ? "Path: peer.example\r\n" : string.Empty)
                + "\r\n");
            var article = new byte[header.Length + body.Length];
            header.CopyTo(article, 0);
            body.CopyTo(article, header.Length);
            return article;
        }

        /// <summary>Repeats a line pattern until at least <paramref name="minimumBytes"/> of body exist.</summary>
        /// <param name="line">Line template including line ending bytes.</param>
        /// <param name="minimumBytes">Minimum body size target.</param>
        /// <returns>Trimmed body buffer.</returns>
        private static byte[] Repeat(ReadOnlySpan<byte> line, int minimumBytes)
        {
            var body = new byte[minimumBytes + line.Length];
            var written = 0;
            while (written < minimumBytes)
            {
                line.CopyTo(body.AsSpan(written));
                written += line.Length;
            }

            return body.AsSpan(0, written).ToArray();
        }

        /// <summary>Generates variable-length lines with periodic dot-leading lines for mixed stuffing benches.</summary>
        /// <param name="minimumBytes">Target minimum body size.</param>
        /// <returns>Synthetic mixed body bytes.</returns>
        private static byte[] MixedBody(int minimumBytes)
        {
            ReadOnlySpan<byte> lengths = [24, 48, 80, 120];
            var body = new byte[minimumBytes + 128];
            var written = 0;
            var lineNumber = 0;
            while (written + 4 < minimumBytes)
            {
                var length = lengths[lineNumber % lengths.Length];
                if (lineNumber % 17 == 0)
                {
                    body[written++] = (byte)'.';
                }

                for (var i = 0; i < length; i++)
                {
                    body[written++] = (byte)('a' + ((lineNumber + i) % 26));
                }

                body[written++] = (byte)'\r';
                body[written++] = (byte)'\n';
                lineNumber++;
            }

            return body.AsSpan(0, written).ToArray();
        }

        /// <summary>Builds a valid yEnc section with begin/end lines sized near <paramref name="minimumBytes"/> logical footprint.</summary>
        /// <param name="minimumBytes">Approximate total logical size target.</param>
        /// <returns>Full logical article including yEnc headers in the body.</returns>
        private static byte[] YEncArticle(int minimumBytes)
        {
            const int lineLength = 128;
            var decodedSize = Math.Max(lineLength, ((minimumBytes - 256) / (lineLength + 2)) * lineLength);
            var decoded = new byte[decodedSize];
            var crc = YEncCrc32.Compute(decoded);
            var begin = Encoding.ASCII.GetBytes($"=ybegin line=128 size={decodedSize} name=t.bin\r\n");
            var end = Encoding.ASCII.GetBytes($"=yend size={decodedSize} crc32={crc:x8}\r\n");
            var lines = decodedSize / lineLength;
            var body = new byte[begin.Length + (lines * (lineLength + 2)) + end.Length];
            begin.CopyTo(body, 0);
            var offset = begin.Length;
            for (var line = 0; line < lines; line++)
            {
                body.AsSpan(offset, lineLength).Fill((byte)(0 + 42));
                offset += lineLength;
                body[offset++] = (byte)'\r';
                body[offset++] = (byte)'\n';
            }

            end.CopyTo(body.AsSpan(offset));
            return Article("yenc", body, includePath: true);
        }

        /// <summary>Shortens an article from the tail to synthesize truncated rejection samples.</summary>
        /// <param name="article">Source logical bytes.</param>
        /// <param name="trailingBytesToDrop">Bytes removed from the end.</param>
        /// <returns>Truncated copy.</returns>
        private static byte[] Truncate(byte[] article, int trailingBytesToDrop) =>
            article.AsSpan(0, article.Length - trailingBytesToDrop).ToArray();

        /// <summary>NNTP multiline framing with dot stuffing and terminator for pipeline benches.</summary>
        /// <param name="logical">Destuffed logical article.</param>
        /// <returns>Wire bytes fed to <see cref="CyclingStream"/>.</returns>
        private static byte[] Frame(byte[] logical)
        {
            var buffer = new byte[logical.Length + (logical.Length / 2) + 8];
            var written = 0;
            var index = 0;
            while (index < logical.Length)
            {
                var end = index;
                while (end < logical.Length && logical[end] != (byte)'\n')
                {
                    end++;
                }

                var contentEnd = end;
                if (contentEnd > index && logical[contentEnd - 1] == (byte)'\r')
                {
                    contentEnd--;
                }

                if (contentEnd > index && logical[index] == (byte)'.')
                {
                    buffer[written++] = (byte)'.';
                }

                logical.AsSpan(index, end - index).CopyTo(buffer.AsSpan(written));
                written += end - index;
                if (end < logical.Length)
                {
                    buffer[written++] = logical[end];
                    index = end + 1;
                }
                else
                {
                    break;
                }
            }

            buffer[written++] = (byte)'.';
            buffer[written++] = (byte)'\r';
            buffer[written++] = (byte)'\n';
            return buffer.AsSpan(0, written).ToArray();
        }

        /// <summary>
        /// Converts a logical length and iteration count to mebibytes.
        /// Both factors are converted before multiplication so the product is not computed in <see cref="int"/>.
        /// </summary>
        /// <param name="logicalLength">Logical article length in bytes.</param>
        /// <param name="iterations">Number of measured iterations.</param>
        /// <returns>The product in mebibytes. Bench sizes fit exactly; the full <see cref="int"/> domain is a printed rate, not an exact byte count.</returns>
        private static double LogicalMebibytes(int logicalLength, int iterations)
            => (double)logicalLength * iterations / (1024d * 1024d);

        /// <summary>Guards <see cref="LogicalMebibytes"/> against int overflow when multiplying length by iteration count.</summary>
        [Fact]
        public void Logical_mebibytes_multiply_before_the_unit_conversion()
        {
            Assert.Equal(3d, LogicalMebibytes(1024 * 1024, 3));
            Assert.True(unchecked(int.MaxValue * 2) < 0);
            var widened = LogicalMebibytes(int.MaxValue, 2);
            Assert.Equal((double)int.MaxValue * 2d / (1024d * 1024d), widened);
            Assert.True(widened > 0d);
        }

        /// <summary>Read-only stream that loops one NNTP frame indefinitely for repeated reader measurements.</summary>
        private sealed class CyclingStream(byte[] frame) : Stream
        {
            /// <summary>Cursor within the repeating frame buffer.</summary>
            private int _position;

            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => false;

            /// <inheritdoc />
            public override long Length => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            /// <inheritdoc />
            public override int Read(Span<byte> buffer)
            {
                var written = 0;
                while (written < buffer.Length)
                {
                    var count = Math.Min(buffer.Length - written, frame.Length - _position);
                    frame.AsSpan(_position, count).CopyTo(buffer[written..]);
                    _position += count;
                    if (_position == frame.Length)
                    {
                        _position = 0;
                    }

                    written += count;
                }

                return written;
            }

            /// <inheritdoc />
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<int>(Read(buffer.Span));
            }

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
