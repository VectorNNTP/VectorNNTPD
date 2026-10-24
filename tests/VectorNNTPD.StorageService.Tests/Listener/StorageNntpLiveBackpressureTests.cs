using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Listener;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.TestDoubles;

namespace VectorNNTPD.StorageService.Tests.Listener
{
    /// <summary>
    /// Live TLS proof that TAKETHIS enqueue completes before persistence finishes.
    /// Full-queue <see cref="IAcceptedArticleQueue.ReserveAsync"/> backpressure is covered in-process by
    /// <see cref="StorageNntpQueueIngressTests.Full_queue_blocks_on_reserve_until_dequeue_then_accepts"/>.
    /// </summary>
    [Collection("StorageNntpLive")]
    public sealed class StorageNntpLiveBackpressureTests
    {
        private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task Available_capacity_accepts_before_worker_persistence()
        {
            var article = Article("avail-0001");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var processor = new GatePublisher(release.Task);
            await using var host = await LiveHost.StartAsync(article.Record.ArtSize * 4L, processor, startWorker: true);

            await using var client = await RawNntpClient.ConnectAsync(host.Port);
            Assert.Equal(239, await client.TakeThisAsync(article));
            await WaitUntilAsync(() => processor.Entered >= 1);
            Assert.Equal(0, processor.Finished);
            release.TrySetResult();
            await WaitUntilAsync(() => processor.Finished >= 1 && host.Queue.AccountedBytes == 0);
        }

        private static BuiltArticle Article(string token)
        {
            var messageId = $"<{token}@backpressure.test>";
            var text =
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                $"Message-ID: {messageId}\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: backpressure\r\n" +
                "\r\n" +
                "body-line-for-backpressure\r\n";
            var logical = Encoding.ASCII.GetBytes(text);
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser("nntpd01.usenet.ninja"),
                logical,
                ArticlePathMode.Normalize);
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            return new BuiltArticle(messageId, created.Record, logical);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(Safety);
            while (!condition())
            {
                await Task.Delay(5, timeout.Token);
            }
        }

        private readonly record struct BuiltArticle(string MessageId, ArticleRecord Record, byte[] Logical);

        private sealed class GatePublisher : IAcceptedArticleProcessor
        {
            private readonly Task _release;

            public GatePublisher(Task release) => _release = release;

            public int Entered { get; private set; }

            public int Finished { get; private set; }

            public void EnsureReady()
            {
            }

            public async ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                Entered++;
                await _release.WaitAsync(cancellationToken).ConfigureAwait(false);
                Finished++;
                return AcceptedArticleProcessResult.Completed;
            }
        }

        private sealed class RawNntpClient : IAsyncDisposable
        {
            private readonly TcpClient _tcp;
            private readonly SslStream _ssl;
            private readonly List<byte> _pending = [];
            private readonly byte[] _readBuffer = new byte[16 * 1024];

            private RawNntpClient(TcpClient tcp, SslStream ssl)
            {
                _tcp = tcp;
                _ssl = ssl;
            }

            public static async Task<RawNntpClient> ConnectAsync(int port)
            {
                var tcp = new TcpClient { NoDelay = true };
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
                var client = new RawNntpClient(tcp, ssl);
                Assert.Equal(200, await client.ReadStatusAsync());
                return client;
            }

            public async Task<int> TakeThisAsync(BuiltArticle article)
            {
                await _ssl.WriteAsync(Encoding.ASCII.GetBytes("TAKETHIS " + article.MessageId + "\r\n"));
                await _ssl.WriteAsync(DotStuff(article.Logical));
                await _ssl.WriteAsync(".\r\n"u8.ToArray());
                return await ReadStatusAsync();
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    await _ssl.DisposeAsync();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    ExpectedException.Ignore(ex);
                }

                _tcp.Dispose();
            }

            private async Task<int> ReadStatusAsync()
            {
                using var timeout = new CancellationTokenSource(Safety);
                while (true)
                {
                    for (var i = 0; i < _pending.Count - 1; i++)
                    {
                        if (_pending[i] == (byte)'\r' && _pending[i + 1] == (byte)'\n')
                        {
                            var line = Encoding.ASCII.GetString(_pending.GetRange(0, i).ToArray());
                            _pending.RemoveRange(0, i + 2);
                            return ((line[0] - '0') * 100) + ((line[1] - '0') * 10) + (line[2] - '0');
                        }
                    }

                    var read = await _ssl.ReadAsync(_readBuffer, timeout.Token);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    for (var i = 0; i < read; i++)
                    {
                        _pending.Add(_readBuffer[i]);
                    }
                }
            }

            private static byte[] DotStuff(ReadOnlySpan<byte> article)
            {
                using var ms = new MemoryStream(article.Length + 8);
                var atLineStart = true;
                for (var i = 0; i < article.Length; i++)
                {
                    var b = article[i];
                    if (atLineStart && b == (byte)'.')
                    {
                        ms.WriteByte((byte)'.');
                    }

                    ms.WriteByte(b);
                    atLineStart = b == (byte)'\n';
                }

                return ms.ToArray();
            }
        }

        private sealed class LiveHost : IAsyncDisposable
        {
            private readonly StorageNntpListenerService _listener;
            private readonly AcceptedArticleWorker? _worker;

            private LiveHost(
                AcceptedArticleQueue queue,
                StorageNntpListenerService listener,
                AcceptedArticleWorker? worker,
                int port)
            {
                Queue = queue;
                _listener = listener;
                _worker = worker;
                Port = port;
            }

            public int Port { get; }

            public AcceptedArticleQueue Queue { get; }

            public static async Task<LiveHost> StartAsync(
                long maxQueuedBytes,
                IAcceptedArticleProcessor? processor = null,
                bool startWorker = false)
            {
                var port = StorageServerTestOptions.GetFreeTcpPort();
                var identity = StorageServerTestOptions.CreateValid();
                identity.BindAddress = ["127.0.0.1"];
                identity.BindPortTls = port;
                var acme = StorageServerTestOptions.CreateValidAcme(identity);
                acme.BindAddress = ["127.0.0.1"];
                acme.BindPortTls = port;
                var runtime = StorageServerRuntimeOptionsFactory.Create(identity, acme);
                var queue = new AcceptedArticleQueue(maxQueuedBytes, TimeProvider.System);
                var readiness = new AcmeCertificateReadiness();
                readiness.MarkReady();
                var listener = new StorageNntpListenerService(
                    runtime,
                    TestListenerCertificates.CreatePublishedProvider(),
                    readiness,
                    NullLogger<StorageNntpListenerService>.Instance,
                    acceptedArticles: queue,
                    assemblyAdmission: new StoreAssemblyAdmission(8));
                await listener.StartAsync(CancellationToken.None);

                AcceptedArticleWorker? worker = null;
                if (startWorker)
                {
                    ArgumentNullException.ThrowIfNull(processor);
                    worker = new AcceptedArticleWorker(
                        queue,
                        processor,
                        NullLogger<AcceptedArticleWorker>.Instance,
                        new SpoolPersistenceOptions { MinWorkers = 1, MaxWorkers = 1 },
                        new VectorNNTPD.StorageService.Storage.SpoolPersistence.SpoolPressure(),
                        new VectorNNTPD.StorageService.Storage.SpoolPersistence.SpoolPersistenceCounters(),
                        TimeProvider.System);
                    await worker.StartAsync(CancellationToken.None);
                }

                return new LiveHost(queue, listener, worker, port);
            }

            public async ValueTask DisposeAsync()
            {
                if (_worker is not null)
                {
                    await _worker.StopAsync(CancellationToken.None);
                }

                await _listener.DisposeAsync();
            }
        }
    }

    [CollectionDefinition("StorageNntpLive", DisableParallelization = true)]
    public sealed class StorageNntpLiveCollection
    {
    }
}
