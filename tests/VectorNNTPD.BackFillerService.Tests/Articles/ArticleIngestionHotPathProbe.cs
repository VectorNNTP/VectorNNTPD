using System.Diagnostics;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Articles
{
    /// <summary>
    /// Steady-state allocation probe for destuff plus canonical retention. Not a correctness gate.
    /// </summary>
    public sealed class ArticleIngestionHotPathProbe
    {
        /// <summary>
        /// Prints per-sample factory and pipeline allocation rates for several synthetic sizes; not a timing gate.
        /// </summary>
        [Fact]
        public async Task Measure_steady_state_allocations()
        {
            var parser = new NntpArticleParser("backfiller.test");
            var cases = new (string Name, byte[] Logical)[]
            {
                ("small-plain", Article("small", "hello\r\n")),
                ("typical-plain", Article("typical", Body("line\r\n", 32 * 1024))),
                ("large-plain", Article("large", Body("line\r\n", 256 * 1024))),
                ("dot-heavy", Article("dots", Body(".\r\n", 32 * 1024))),
                ("path-only", Article("path", "body\r\n")),
            };

            foreach (var sample in cases)
            {
                const int iterations = 200;
                long factoryAllocated = 0;
                var factoryTicks = 0L;
                for (var i = 0; i < iterations; i++)
                {
                    var startAlloc = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    var created = ArticleRecordFactory.TryCreate(parser, sample.Logical, ArticlePathMode.Traverse);
                    factoryTicks += Stopwatch.GetTimestamp() - start;
                    factoryAllocated += GC.GetAllocatedBytesForCurrentThread() - startAlloc;
                    if (!created.IsAccepted)
                    {
                        Console.WriteLine($"{sample.Name} rejected parse={created.ParseFailure} materialize={created.MaterializeFailure}");
                        break;
                    }
                }

                var wire = Frame(sample.Logical);
                var copies = iterations + 1;
                var repeated = new byte[wire.Length * copies];
                for (var i = 0; i < copies; i++)
                {
                    wire.CopyTo(repeated.AsSpan(i * wire.Length));
                }

                var maxBytes = sample.Logical.Length + wire.Length + 1024;
                long pipelineAllocated = 0;
                var pipelineTicks = 0L;
                var pipelineReader = new NntpStreamReader(new MemoryStream(repeated, writable: false), 64 * 1024);
                _ = await pipelineReader.ReadArticlePayloadAsync(
                    maxBytes,
                    TimeSpan.FromSeconds(5),
                    memory => ArticleRecordFactory.TryCreate(parser, memory, ArticlePathMode.Traverse),
                    CancellationToken.None);
                for (var i = 0; i < iterations; i++)
                {
                    var startAlloc = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    var created = await pipelineReader.ReadArticlePayloadAsync(
                        maxBytes,
                        TimeSpan.FromSeconds(5),
                        memory => ArticleRecordFactory.TryCreate(parser, memory, ArticlePathMode.Traverse),
                        CancellationToken.None);
                    pipelineTicks += Stopwatch.GetTimestamp() - start;
                    pipelineAllocated += GC.GetAllocatedBytesForCurrentThread() - startAlloc;
                    Assert.True(created.IsAccepted, created.ParseFailure.ToString());
                }

                var factoryMs = factoryTicks * 1000.0 / Stopwatch.Frequency;
                var pipelineMs = pipelineTicks * 1000.0 / Stopwatch.Frequency;
                Console.WriteLine(
                    $"{sample.Name} logical={sample.Logical.Length} factoryAlloc={factoryAllocated / iterations} factory/s={iterations / (factoryMs / 1000.0):F0} pipelineAlloc={pipelineAllocated / iterations} pipeline/s={iterations / (pipelineMs / 1000.0):F0}");
            }

            MeasureConcurrent(parser, Article("typical", Body("line\r\n", 32 * 1024)), "typical-plain");
            MeasureConcurrent(parser, Article("dots", Body(".\r\n", 32 * 1024)), "dot-heavy");
        }

        /// <summary>Times parallel <see cref="ArticleRecordFactory.TryCreate"/> on one logical article for throughput logging.</summary>
        /// <param name="parser">Shared parser instance.</param>
        /// <param name="logical">Destuffed logical article bytes.</param>
        /// <param name="name">Label printed with results.</param>
        private static void MeasureConcurrent(NntpArticleParser parser, byte[] logical, string name)
        {
            const int operations = 1000;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workers = new Task[operations];
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
            Console.WriteLine($"{name} concurrent={operations} elapsedMs={elapsedMs:F1} articles/s={operations / (elapsedMs / 1000.0):F0}");
        }

        /// <summary>Minimal ASCII headers plus body for probe articles.</summary>
        /// <param name="id">Message-ID local part.</param>
        /// <param name="body">Body bytes after the separator.</param>
        /// <returns>Logical article bytes.</returns>
        private static byte[] Article(string id, string body) =>
            Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
                + $"Message-ID: <{id}@example.test>\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "Subject: s\r\n"
                + "Path: peer.example\r\n"
                + "\r\n"
                + body);

        /// <summary>Repeats a line until at least <paramref name="minimumBytes"/> of body text exist.</summary>
        /// <param name="line">Line template including its ending.</param>
        /// <param name="minimumBytes">Minimum total body size.</param>
        /// <returns>Expanded body string.</returns>
        private static string Body(string line, int minimumBytes)
        {
            var builder = new StringBuilder(minimumBytes + line.Length);
            while (builder.Length < minimumBytes)
            {
                builder.Append(line);
            }

            return builder.ToString();
        }

        /// <summary>NNTP multiline framing with dot stuffing and a terminating <c>.\r\n</c> for reader probes.</summary>
        /// <param name="logical">Destuffed article bytes.</param>
        /// <returns>Framed wire bytes suitable for <see cref="NntpStreamReader"/>.</returns>
        private static byte[] Frame(byte[] logical)
        {
            var buffer = new byte[logical.Length * 2 + 8];
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
    }
}
