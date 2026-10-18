using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Xunit.Abstractions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Retention
{
    /// <summary>
    /// Records claim cost for large articles. Bounds prove the claim does not copy ArtData.
    /// Elapsed times are evidence, not pass conditions.
    /// The lock-held scan repeats <see cref="ArticleWorkDeliveryFraming.IsFrameable"/> under one private lock.
    /// That reconstructs the article walk removed from the retention gate. It is not a run of the previous binary.
    /// </summary>
    public sealed class ArticleRetentionClaimMeasurementTests
    {
        /// <summary>Approximate retained body size from issue 153.</summary>
        private const int TargetBodyBytes = 740 * 1024;

        /// <summary>Captures measurement lines.</summary>
        private readonly ITestOutputHelper _output;

        /// <summary>Creates the measurement fixture.</summary>
        /// <param name="output">xUnit output for the recorded samples.</param>
        public ArticleRetentionClaimMeasurementTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Measures hit and miss claims. A hit scans ArtData after the retention lock is released.
        /// Wall-clock time includes every worker. The reconstructed scan is the same article check under one lock,
        /// not a measurement of the previous retention implementation.
        /// </summary>
        [Fact]
        public void Large_article_claims_do_not_copy_the_article_and_record_contention()
        {
            var body = BuildBody(TargetBodyBytes);
            var authority = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero)),
                maxBytes: 64 * 1024 * 1024);
            const int articles = 32;
            var ids = new byte[articles][];
            var memories = new ReadOnlyMemory<byte>[articles];
            for (var i = 0; i < articles; i++)
            {
                var prepared = RetentionTestArticles.RetainPrepared(authority, $"<m{i}@measure.test>", body);
                ids[i] = Encoding.ASCII.GetBytes(prepared.MessageId);
                memories[i] = prepared.Record.ArtData;
            }

            var artSize = memories[0].Length;
            var miss = Encoding.ASCII.GetBytes("<missing@measure.test>");
            _output.WriteLine(
                $"runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} "
                + $"cpu={Environment.ProcessorCount} config=Release-or-current-host artSize={artSize} articles={articles}");

            const int iterations = 40;
            Measure("claim-hit", 1, iterations, worker => ClaimAndRelease(authority, ids[worker]));
            Measure("claim-hit", 8, iterations, worker => ClaimAndRelease(authority, ids[worker]));
            Measure("claim-hit", 32, iterations, worker => ClaimAndRelease(authority, ids[worker]));
            Measure("claim-miss", 1, iterations, _ => Assert.Equal(
                ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(miss).Kind));
            Measure("claim-miss", 8, iterations, _ => Assert.Equal(
                ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(miss).Kind));
            Measure("claim-miss", 32, iterations, _ => Assert.Equal(
                ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(miss).Kind));

            var scanGate = new object();
            Measure("reconstructed-lock-held-scan", 1, iterations, worker =>
            {
                lock (scanGate)
                {
                    Assert.True(ArticleWorkDeliveryFraming.IsFrameable(memories[worker].Span));
                }
            });
            Measure("reconstructed-lock-held-scan", 32, iterations, worker =>
            {
                lock (scanGate)
                {
                    Assert.True(ArticleWorkDeliveryFraming.IsFrameable(memories[worker].Span));
                }
            });

            const int allocationIterations = 1000;
            var beforeHits = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < allocationIterations; i++)
            {
                var claimed = authority.TryClaimForDelivery(ids[0]);
                claimed.Claim!.Dispose();
            }

            var hitBytes = GC.GetAllocatedBytesForCurrentThread() - beforeHits;
            var beforeMisses = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < allocationIterations; i++)
            {
                _ = authority.TryClaimForDelivery(miss);
            }

            var missBytes = GC.GetAllocatedBytesForCurrentThread() - beforeMisses;
            _output.WriteLine(
                $"allocations iterations={allocationIterations} hitBytes={hitBytes} "
                + $"hitPerOp={hitBytes / allocationIterations} missBytes={missBytes} "
                + $"missPerOp={missBytes / allocationIterations} artSize={artSize}");

            Assert.True(hitBytes < (long)allocationIterations * artSize / 8);
            Assert.True(missBytes < (long)allocationIterations * artSize / 8);
            Assert.True(missBytes < allocationIterations * 2048L);
        }

        /// <summary>Claims one retained article and releases it.</summary>
        private static void ClaimAndRelease(ArticleRetentionAuthority authority, byte[] messageId)
        {
            var claimed = authority.TryClaimForDelivery(messageId);
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            claimed.Claim!.Dispose();
        }

        /// <summary>Runs <paramref name="operation"/> on long-running tasks and prints wall-clock throughput and latency.</summary>
        private void Measure(string name, int concurrency, int iterationsPerWorker, Action<int> operation)
        {
            var samples = new long[concurrency * iterationsPerWorker];
            var tasks = new Task[concurrency];
            using var started = new Barrier(concurrency);
            var wallStart = Stopwatch.GetTimestamp();
            for (var worker = 0; worker < concurrency; worker++)
            {
                var index = worker;
                tasks[worker] = Task.Factory.StartNew(
                () =>
                {
                    started.SignalAndWait();
                    for (var i = 0; i < iterationsPerWorker; i++)
                    {
                        var timestamp = Stopwatch.GetTimestamp();
                        operation(index);
                        samples[(index * iterationsPerWorker) + i] = Stopwatch.GetTimestamp() - timestamp;
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            }

            Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(60)));
            var wallSeconds = (Stopwatch.GetTimestamp() - wallStart) / (double)Stopwatch.Frequency;

            Array.Sort(samples);
            var p50 = Percentile(samples, 0.50);
            var p95 = Percentile(samples, 0.95);
            var wallOps = samples.Length / wallSeconds;
            _output.WriteLine(
                $"{name} concurrency={concurrency} iterations={samples.Length} "
                + $"wallSeconds={wallSeconds:F3} wallOpsPerSec={wallOps:F0} p50us={p50:F1} p95us={p95:F1}");
        }

        /// <summary>Nearest-rank percentile of timestamp deltas, in microseconds.</summary>
        private static double Percentile(long[] sortedTicks, double fraction)
        {
            var index = (int)Math.Clamp(Math.Ceiling(fraction * sortedTicks.Length) - 1, 0, sortedTicks.Length - 1);
            return sortedTicks[index] * 1_000_000d / Stopwatch.Frequency;
        }

        /// <summary>Builds a CRLF body of about <paramref name="targetBytes"/> without exceeding the 1024-byte line limit.</summary>
        private static string BuildBody(int targetBytes)
        {
            var line = new string('x', 998) + "\r\n";
            var builder = new StringBuilder(targetBytes + line.Length);
            while (builder.Length < targetBytes)
            {
                builder.Append(line);
            }

            return builder.ToString();
        }
    }
}
