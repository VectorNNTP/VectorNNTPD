using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService;
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

        /// <summary>
        /// In-memory (pipe) TAKETHIS allocation sample. Set <c>VECTORNNTPD_STORAGE_NNTP_BENCH=1</c>.
        /// Isolates session parse/assembly/materialize/queue from TLS.
        /// </summary>
        [Fact]
        public async Task Optional_in_memory_allocation_sample()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_NNTP_BENCH"), "1", StringComparison.Ordinal))
            {
                _output.WriteLine("Set VECTORNNTPD_STORAGE_NNTP_BENCH=1 to measure in-memory TAKETHIS allocations.");
                return;
            }

            const int warmup = 64;
            const int measured = 512;
            var queue = new AcceptedArticleQueue();
            await using var harness = await InMemoryHarness.StartAsync(queue);
            var wire = BuildWireArticle();

            for (var i = 0; i < warmup; i++)
            {
                Assert.Equal(239, await harness.TakeThisAsync(wire));
                _ = await queue.DequeueAsync(CancellationToken.None);
            }

            // Snapshot counters after warm-up without forcing GC. Deltas may include ambient
            // runtime activity; compare relative runs with the same harness only.
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < measured; i++)
            {
                Assert.Equal(239, await harness.TakeThisAsync(wire));
                _ = await queue.DequeueAsync(CancellationToken.None);
            }

            clock.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            // Includes harness status-line reads (PipeDuplex copies) as well as the session path.
            // Compare before/after only with an identical harness; do not treat the absolute
            // per-article figure as server-only cost or as a pristine GC baseline.
            _output.WriteLine(
                $"in-memory articles={measured} bodyBytes={BodyBytes} elapsedMs={clock.ElapsedMilliseconds} "
                + $"allocatedBytes={allocated} allocatedPerArticle={allocated / (double)measured:0} "
                + $"gen0={GC.CollectionCount(0) - gen0} gen1={GC.CollectionCount(1) - gen1} gen2={GC.CollectionCount(2) - gen2} "
                + $"articlesPerSec={measured / clock.Elapsed.TotalSeconds:0} "
                + $"note=includes-harness-status-reads;no-forced-gc");
        }

        private static byte[] BuildWireArticle()
        {
            var messageId = "<bench-alloc@seg.test>";
            var text =
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                $"Message-ID: {messageId}\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: bench\r\n" +
                "\r\n" +
                new string('x', BodyBytes) + "\r\n";
            // Transfer form: body lines that start with '.' are stuffed; terminator appended.
            var bodyStart = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
            var header = Encoding.ASCII.GetBytes(text[..bodyStart]);
            var body = Encoding.ASCII.GetBytes(text[bodyStart..]);
            var stuffed = new byte[header.Length + body.Length + 3];
            header.CopyTo(stuffed, 0);
            body.CopyTo(stuffed, header.Length);
            stuffed[^3] = (byte)'.';
            stuffed[^2] = (byte)'\r';
            stuffed[^1] = (byte)'\n';
            var command = Encoding.ASCII.GetBytes($"TAKETHIS {messageId}\r\n");
            var framed = new byte[command.Length + stuffed.Length];
            command.CopyTo(framed, 0);
            stuffed.CopyTo(framed, command.Length);
            return framed;
        }

        private sealed class InMemoryHarness : IAsyncDisposable
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _run;
            private readonly PipeDuplex _transport;

            private InMemoryHarness(PipeDuplex transport, StorageNntpFeedSession session)
            {
                _transport = transport;
                _run = session.RunAsync(_cts.Token);
            }

            public static async Task<InMemoryHarness> StartAsync(IAcceptedArticleQueue queue)
            {
                var transport = new PipeDuplex();
                var session = new StorageNntpFeedSession(
                    transport,
                    new StorageServerListenerRuntimeOptions(262144, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 8),
                    NullLogger.Instance,
                    "cache01.usenet.ninja",
                    acceptedArticles: queue,
                    assemblyAdmission: new StoreAssemblyAdmission(8));
                var harness = new InMemoryHarness(transport, session);
                _ = await harness.ReadStatusAsync();
                return harness;
            }

            public async Task<int> TakeThisAsync(byte[] framed)
            {
                await _transport.ClientWriteAsync(framed);
                return await ReadStatusAsync();
            }

            private async Task<int> ReadStatusAsync()
            {
                var line = new List<byte>(64);
                while (true)
                {
                    var b = new byte[1];
                    var read = await _transport.ClientReadAsync(b);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    if (b[0] == (byte)'\n' && line.Count > 0 && line[^1] == (byte)'\r')
                    {
                        line.RemoveAt(line.Count - 1);
                        break;
                    }

                    line.Add(b[0]);
                }

                var text = Encoding.ASCII.GetString(line.ToArray());
                return int.Parse(text.AsSpan(0, 3));
            }

            public async ValueTask DisposeAsync()
            {
                await _cts.CancelAsync();
                await _transport.DisposeAsync();
                try
                {
                    await _run;
                }
                catch (OperationCanceledException ex)
                {
                    // Expected when DisposeAsync cancels the session RunAsync token.
                    ExpectedException.Ignore(ex);
                }

                _cts.Dispose();
            }
        }

        private sealed class PipeDuplex : IStorageListenerTransport
        {
            private readonly Channel<byte[]> _toServer = Channel.CreateUnbounded<byte[]>();
            private readonly Channel<byte[]> _toClient = Channel.CreateUnbounded<byte[]>();
            private byte[] _serverPending = [];
            private int _serverPendingOffset;
            private byte[] _clientPending = [];
            private int _clientPendingOffset;

            public Task ClientWriteAsync(byte[] bytes) =>
                _toServer.Writer.WriteAsync(bytes).AsTask();

            public async Task<int> ClientReadAsync(byte[] buffer)
            {
                if (_clientPendingOffset >= _clientPending.Length)
                {
                    _clientPending = await _toClient.Reader.ReadAsync().ConfigureAwait(false);
                    _clientPendingOffset = 0;
                }

                var count = Math.Min(buffer.Length, _clientPending.Length - _clientPendingOffset);
                _clientPending.AsSpan(_clientPendingOffset, count).CopyTo(buffer);
                _clientPendingOffset += count;
                return count;
            }

            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (_serverPendingOffset >= _serverPending.Length)
                {
                    _serverPending = await _toServer.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    _serverPendingOffset = 0;
                }

                var count = Math.Min(buffer.Length, _serverPending.Length - _serverPendingOffset);
                _serverPending.AsSpan(_serverPendingOffset, count).CopyTo(buffer.Span);
                _serverPendingOffset += count;
                return count;
            }

            public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                await _toClient.Writer.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
                return buffer.Length;
            }

            public ValueTask DisposeAsync()
            {
                _toServer.Writer.TryComplete();
                _toClient.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
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
