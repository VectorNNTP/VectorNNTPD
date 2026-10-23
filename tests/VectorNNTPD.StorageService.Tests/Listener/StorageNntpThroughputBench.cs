using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Listener;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.Support.Placement;
using VectorNNTPD.StorageService.Tests.TestDoubles;
using Xunit.Abstractions;

namespace VectorNNTPD.StorageService.Tests.Listener
{
    /// <summary>
    /// Minimal NNTP TAKETHIS throughput harness. Sustained measurement runs only when
    /// <c>VECTORNNTPD_STORAGE_NNTP_BENCH=1</c>.
    /// </summary>
    public sealed class StorageNntpThroughputBench
    {
        private readonly ITestOutputHelper _output;

        public StorageNntpThroughputBench(ITestOutputHelper output) => _output = output;

        public static int BodyBytes => 256;

        [Fact]
        public async Task Warm_path_places_one_article()
        {
            await using var host = await StartPlacementIngressHostAsync();
            await using var client = new ArticlePlacementClient(NullLogger<ArticlePlacementClient>.Instance, 1)
            {
                TestTcpConnectHost = "127.0.0.1",
                ServerCertificateValidationCallback = static (_, _, _, _) => true,
            };
            var record = CreateRecord();
            var result = await client.PlaceAsync(record, host.Target, CancellationToken.None);
            Assert.Equal(ArticlePlacementKind.Accepted, result.Kind);
            Assert.Equal(1, host.Queue.EnqueuedCount);
        }

        [Fact]
        public async Task Optional_sustained_sweep()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_NNTP_BENCH"), "1", StringComparison.Ordinal))
            {
                _output.WriteLine("Set VECTORNNTPD_STORAGE_NNTP_BENCH=1 to measure TAKETHIS throughput.");
                return;
            }

            await using var host = await StartPlacementIngressHostAsync();
            await using var client = new ArticlePlacementClient(NullLogger<ArticlePlacementClient>.Instance, 2)
            {
                TestTcpConnectHost = "127.0.0.1",
                ServerCertificateValidationCallback = static (_, _, _, _) => true,
            };
            var record = CreateRecord();
            var accepted = 0;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(3))
            {
                var result = await client.PlaceAsync(record, host.Target, CancellationToken.None);
                if (result.Kind == ArticlePlacementKind.Accepted)
                {
                    accepted++;
                    _ = await host.Queue.DequeueAsync(CancellationToken.None);
                }
            }

            _output.WriteLine($"accepted={accepted} elapsedMs={clock.ElapsedMilliseconds}");
            Assert.True(accepted > 0);
        }

        public static async Task<PlacementIngressHost> StartPlacementIngressHostAsync()
        {
            var port = StorageServerTestOptions.GetFreeTcpPort();
            var identity = StorageServerTestOptions.CreateValid();
            identity.BindAddress = ["127.0.0.1"];
            identity.BindPortTls = port;
            var acme = StorageServerTestOptions.CreateValidAcme(identity);
            acme.BindAddress = ["127.0.0.1"];
            acme.BindPortTls = port;
            var runtime = StorageServerRuntimeOptionsFactory.Create(identity, acme);
            var queue = new AcceptedArticleQueue();
            var readiness = new AcmeCertificateReadiness();
            readiness.MarkReady();
            var listener = new StorageNntpListenerService(
                runtime,
                TestListenerCertificates.CreatePublishedProvider(),
                readiness,
                NullLogger<StorageNntpListenerService>.Instance,
                acceptedArticles: queue,
                assemblyAdmission: new StoreAssemblyAdmission(64));
            await listener.StartAsync(CancellationToken.None);
            var target = new StorageServerFleetEntry(
                1,
                "cache01.usenet.ninja",
                10_000,
                0,
                10_000,
                DateTimeOffset.UtcNow,
                port);
            return new PlacementIngressHost(port, queue, listener, target);
        }

        private static ArticleRecord CreateRecord()
        {
            var text =
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <bench@seg.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: bench\r\n" +
                "\r\n" +
                new string('x', BodyBytes) + "\r\n";
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser("nntpd01.usenet.ninja"),
                Encoding.ASCII.GetBytes(text),
                ArticlePathMode.Normalize);
            Assert.True(created.IsAccepted);
            return created.Record;
        }

        public sealed class PlacementIngressHost : IAsyncDisposable
        {
            private readonly StorageNntpListenerService _listener;

            internal PlacementIngressHost(
                int port,
                AcceptedArticleQueue queue,
                StorageNntpListenerService listener,
                StorageServerFleetEntry target)
            {
                Port = port;
                Queue = queue;
                _listener = listener;
                Target = target;
            }

            public int Port { get; }

            public AcceptedArticleQueue Queue { get; }

            public StorageServerFleetEntry Target { get; }

            public async ValueTask DisposeAsync() => await _listener.DisposeAsync();
        }
    }
}
