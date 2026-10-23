using System.Diagnostics;
using System.Net.Security;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService.Tests.Support.Placement;
using Xunit.Abstractions;

namespace VectorNNTPD.StorageService.Tests.Listener
{

    /// <summary>
    /// Measures <see cref="ArticlePlacementClient"/> against the StorageServer ingress listener.
    /// Runs only when <c>VECTORNNTPD_STORAGE_PLACEMENT_BENCH=1</c>.
    /// </summary>
    public sealed class ArticlePlacementClientBench
    {
        private readonly ITestOutputHelper _output;

        public ArticlePlacementClientBench(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task One_connection_compares_one_and_two_outstanding_stores()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_PLACEMENT_BENCH"), "1", StringComparison.Ordinal))
            {
                _output.WriteLine("Set VECTORNNTPD_STORAGE_PLACEMENT_BENCH=1 to measure ArticlePlacementClient.");
                return;
            }
            var seconds = 8;
            var configured = Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_PLACEMENT_SECONDS");
            if (int.TryParse(configured, out var parsed) && parsed is >= 1 and <= 120)
            {
                seconds = parsed;
            }

            var record = CreateRecord();
            _output.WriteLine($"artBytes={record.ArtSize} bodyBytes={StorageVatpThroughputBench.BodyBytes} window=1048576");
            _output.WriteLine(await MeasureAsync(record, outstanding: 1, TimeSpan.FromSeconds(seconds)));
            _output.WriteLine(await MeasureAsync(record, outstanding: 2, TimeSpan.FromSeconds(seconds)));
        }

        private async Task<string> MeasureAsync(ArticleRecord record, int outstanding, TimeSpan duration)
        {
            await using var host = await StorageVatpThroughputBench.StartPlacementIngressHostAsync();
            await using var client = new ArticlePlacementClient(
                NullLogger<ArticlePlacementClient>.Instance,
                outstanding)
            {
                TestTcpConnectHost = "127.0.0.1",
                ServerCertificateValidationCallback = static (_, _, _, _) => true,
            };
            var target = new StorageServerFleetEntry(
                1,
                "cache01.usenet.ninja",
                10_000,
                0,
                10_000,
                DateTimeOffset.UtcNow,
                host.Port);
            var warmup = await client.PlaceAsync(record, target, CancellationToken.None);
            if (warmup.Kind != ArticlePlacementKind.Accepted || client.ConnectCount != 1)
            {
                throw new InvalidOperationException($"Warmup failed kind={warmup.Kind} connects={client.ConnectCount} failure={warmup.Failure}");
            }

            var accepted = 0;
            var duplicate = 0;
            var rejected = 0;
            var failed = 0;
            var ambiguous = 0;
            var cancelled = 0;
            var payload = 0L;
            var inflight = 0;
            var peak = 0;
            var queueMax = 0L;
            var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var gc2 = GC.CollectionCount(2);
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            var workers = new Task[outstanding];
            for (var worker = 0; worker < outstanding; worker++)
            {
                workers[worker] = Task.Run(async () =>
                {
                    while (clock.Elapsed < duration)
                    {
                        var now = Interlocked.Increment(ref inflight);
                        int seen;
                        do
                        {
                            seen = Volatile.Read(ref peak);
                            if (now <= seen)
                            {
                                break;
                            }
                        }
                        while (Interlocked.CompareExchange(ref peak, now, seen) != seen);

                        try
                        {
                            var result = await client.PlaceAsync(record, target, CancellationToken.None).ConfigureAwait(false);
                            var queued = host.Queue.QueuedBytes;
                            long previousQueue;
                            do
                            {
                                previousQueue = Volatile.Read(ref queueMax);
                                if (queued <= previousQueue)
                                {
                                    break;
                                }
                            }
                            while (Interlocked.CompareExchange(ref queueMax, queued, previousQueue) != previousQueue);

                            if (result.Kind == ArticlePlacementKind.Accepted)
                            {
                                Interlocked.Increment(ref accepted);
                                Interlocked.Add(ref payload, record.ArtSize);
                            }
                            else if (result.Kind == ArticlePlacementKind.Duplicate)
                            {
                                Interlocked.Increment(ref duplicate);
                                Interlocked.Add(ref payload, record.ArtSize);
                            }
                            else if (result.Kind == ArticlePlacementKind.AcknowledgementNotObserved)
                            {
                                Interlocked.Increment(ref ambiguous);
                            }
                            else if (result.Kind == ArticlePlacementKind.Cancelled)
                            {
                                Interlocked.Increment(ref cancelled);
                            }
                            else if (result.Succeeded)
                            {
                                Interlocked.Increment(ref accepted);
                            }
                            else if (result.Kind is ArticlePlacementKind.RejectedInvalid or ArticlePlacementKind.RejectedPressure or ArticlePlacementKind.RejectedCapacity or ArticlePlacementKind.Conflict)
                            {
                                Interlocked.Increment(ref rejected);
                            }
                            else
                            {
                                Interlocked.Increment(ref failed);
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref inflight);
                        }
                    }
                });
            }

            await Task.WhenAll(workers);
            clock.Stop();
            var counters = client.Snapshot();
            var seconds = clock.Elapsed.TotalSeconds;
            var artGbps = payload * 8 / seconds / 1_000_000_000d;
            var bodyGbps = (accepted + duplicate) * (long)StorageVatpThroughputBench.BodyBytes * 8 / seconds / 1_000_000_000d;
            var articlesPerSecond = (accepted + duplicate) / seconds;
            var alloc = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var resultUs = counters.ResultCount == 0
                ? 0
                : counters.ResultTicks * 1_000_000d / Stopwatch.Frequency / counters.ResultCount;
            var resultMaxUs = counters.ResultMaxTicks * 1_000_000d / Stopwatch.Frequency;
            var slotMs = counters.SlotWaitTicks * 1000d / Stopwatch.Frequency;
            var creditMs = counters.CreditWaitTicks * 1000d / Stopwatch.Frequency;
            return
                $"outstanding={outstanding} seconds={seconds:F3} artGbps={artGbps:F3} bodyGbps={bodyGbps:F3} articlesPerSec={articlesPerSecond:F1} " +
                $"connects={client.ConnectCount} callerPeak={Volatile.Read(ref peak)} connectionPeak={counters.PeakActive} " +
                $"accepted={Volatile.Read(ref accepted)} duplicate={Volatile.Read(ref duplicate)} rejected={Volatile.Read(ref rejected)} " +
                $"failed={Volatile.Read(ref failed)} ambiguous={Volatile.Read(ref ambiguous)} cancelled={Volatile.Read(ref cancelled)} " +
                $"resultAvgUs={resultUs:F1} resultMaxUs={resultMaxUs:F1} slotWaitMs={slotMs:F1} creditWaitMs={creditMs:F1} " +
                $"queueMaxBytes={Volatile.Read(ref queueMax)} allocBytes={alloc} gc={GC.CollectionCount(0) - gc0}/{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2} cpuMs={cpuMs:F0}";
        }

        private static ArticleRecord CreateRecord()
        {
            var body = new byte[StorageVatpThroughputBench.BodyBytes];
            for (var offset = 0; offset < body.Length; offset += 80)
            {
                body.AsSpan(offset, 78).Fill((byte)'x');
                body[offset + 78] = (byte)'\r';
                body[offset + 79] = (byte)'\n';
            }

            var header = Encoding.ASCII.GetBytes(
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <placement-bench@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: placement-bench\r\n" +
                "\r\n");
            var article = new byte[header.Length + body.Length];
            header.CopyTo(article, 0);
            body.CopyTo(article, header.Length);
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser("nntpd01.usenet.ninja"), article);
            if (!created.IsAccepted)
            {
                throw new InvalidOperationException("Placement benchmark article was not accepted.");
            }

            return created.Record;
        }
    }
}
