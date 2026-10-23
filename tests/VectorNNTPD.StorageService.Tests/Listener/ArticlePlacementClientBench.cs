using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService.Tests.Support.Placement;
using Xunit.Abstractions;

namespace VectorNNTPD.StorageService.Tests.Listener
{
    /// <summary>
    /// Measures <see cref="ArticlePlacementClient"/> against the StorageServer NNTP listener.
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
            _output.WriteLine($"artBytes={record.ArtSize} bodyBytes={StorageNntpThroughputBench.BodyBytes}");
            _output.WriteLine(await MeasureAsync(record, outstanding: 1, TimeSpan.FromSeconds(seconds)));
            _output.WriteLine(await MeasureAsync(record, outstanding: 2, TimeSpan.FromSeconds(seconds)));
        }

        private async Task<string> MeasureAsync(ArticleRecord record, int outstanding, TimeSpan duration)
        {
            await using var host = await StorageNntpThroughputBench.StartPlacementIngressHostAsync();
            await using var client = new ArticlePlacementClient(
                NullLogger<ArticlePlacementClient>.Instance,
                outstanding)
            {
                TestTcpConnectHost = "127.0.0.1",
                ServerCertificateValidationCallback = static (_, _, _, _) => true,
            };
            var warmup = await client.PlaceAsync(record, host.Target, CancellationToken.None);
            if (warmup.Kind != ArticlePlacementKind.Accepted || client.ConnectCount != 1)
            {
                throw new InvalidOperationException($"Warmup failed kind={warmup.Kind} connects={client.ConnectCount} failure={warmup.Failure}");
            }

            _ = await host.Queue.DequeueAsync(CancellationToken.None);

            var accepted = 0;
            var failed = 0;
            var clock = Stopwatch.StartNew();
            var workers = new Task[outstanding];
            for (var worker = 0; worker < outstanding; worker++)
            {
                workers[worker] = Task.Run(async () =>
                {
                    while (clock.Elapsed < duration)
                    {
                        var result = await client.PlaceAsync(record, host.Target, CancellationToken.None);
                        if (result.Kind == ArticlePlacementKind.Accepted)
                        {
                            Interlocked.Increment(ref accepted);
                            _ = await host.Queue.DequeueAsync(CancellationToken.None);
                        }
                        else
                        {
                            Interlocked.Increment(ref failed);
                        }
                    }
                });
            }

            await Task.WhenAll(workers);
            var snapshot = client.Snapshot();
            return $"outstanding={outstanding} accepted={accepted} failed={failed} " +
                   $"peak={snapshot.PeakActive} responseWaitMs={snapshot.CreditWaitTicks * 1000.0 / Stopwatch.Frequency:F1}";
        }

        private static ArticleRecord CreateRecord()
        {
            var text =
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <placement-bench@seg.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: placement-bench\r\n" +
                "\r\n" +
                new string('y', StorageNntpThroughputBench.BodyBytes) + "\r\n";
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser("nntpd01.usenet.ninja"),
                Encoding.ASCII.GetBytes(text),
                ArticlePathMode.Normalize);
            Assert.True(created.IsAccepted);
            return created.Record;
        }
    }
}
