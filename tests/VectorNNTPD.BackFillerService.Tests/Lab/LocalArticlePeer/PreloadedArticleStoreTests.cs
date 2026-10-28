using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Nntp;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    public sealed class PreloadedArticleStoreTests
    {
        [Fact]
        public async Task Preload_matches_file_path_framing_byte_for_byte_including_terminator()
        {
            var dir = CreateFixtureCorpus(out var messageId, includeDotLine: true);
            var index = await CorpusArticleIndex.BuildAsync(dir);
            Assert.True(index.TryResolve(messageId, out var path));

            var (logical, framed) = await NntpWireFramer.ReadAndFrameAsync(path, 1024 * 1024, CancellationToken.None);
            var status = Encoding.ASCII.GetBytes($"220 0 {messageId} article follows\r\n");
            var expectedWire = new byte[status.Length + framed.Length];
            Buffer.BlockCopy(status, 0, expectedWire, 0, status.Length);
            Buffer.BlockCopy(framed, 0, expectedWire, status.Length, framed.Length);

            var store = await PreloadedArticleStore.LoadAsync(index, 1024 * 1024);
            Assert.Equal(1, store.Count);
            Assert.Equal(0, store.Metrics.LoadFailures);
            Assert.True(store.TryGet(messageId, out var preloaded));
            Assert.Equal(logical.Length, preloaded.LogicalByteCount);
            Assert.True(expectedWire.AsSpan().SequenceEqual(preloaded.WireResponse));
            Assert.True(EndsWithArticleTerminator(preloaded.WireResponse));
            Assert.Contains("..dotline"u8, preloaded.WireResponse.AsSpan());

            await using var filePeer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions { CorpusRoot = dir, Port = 0 });
            await using var memPeer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions
                {
                    CorpusRoot = dir,
                    Port = 0,
                    PreloadArticlesIntoMemory = true,
                });

            var fileWire = await CaptureArticleResponseAsync(filePeer.Port, messageId);
            var memWire = await CaptureArticleResponseAsync(memPeer.Port, messageId);
            Assert.True(expectedWire.AsSpan().SequenceEqual(fileWire));
            Assert.True(expectedWire.AsSpan().SequenceEqual(memWire));
            // Client read can finish before the peer records success after flush; wait on settlement.
            await filePeer.Metrics.WaitForArticleSuccessAsync(expected: 1);
            await memPeer.Metrics.WaitForArticleSuccessAsync(expected: 1);
            Assert.Equal(1, filePeer.Metrics.ArticleSuccess);
            Assert.Equal(1, memPeer.Metrics.ArticleSuccess);
            Assert.Equal(filePeer.Metrics.LogicalBytesTransferred, memPeer.Metrics.LogicalBytesTransferred);
            Assert.Equal(filePeer.Metrics.WireBytesTransferred, memPeer.Metrics.WireBytesTransferred);
        }

        [Fact]
        public async Task Concurrent_sessions_share_precomputed_buffers_without_corruption()
        {
            var dir = CreateFixtureCorpus(out var messageId, includeDotLine: true);
            await using var peer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions
                {
                    CorpusRoot = dir,
                    Port = 0,
                    MaxConcurrentSessions = 32,
                    PreloadArticlesIntoMemory = true,
                });

            Assert.True(peer.PreloadedStore!.TryGet(messageId, out var article));
            var expected = article.WireResponse;

            var tasks = Enumerable.Range(0, 16).Select(async _ =>
            {
                var wire = await CaptureArticleResponseAsync(peer.Port, messageId);
                Assert.True(expected.AsSpan().SequenceEqual(wire));
            });
            await Task.WhenAll(tasks);
            await peer.Metrics.WaitForArticleSuccessAsync(expected: 16);
            Assert.Equal(16, peer.Metrics.ArticleSuccess);
            Assert.Equal(0, peer.Metrics.OutstandingArticles);
            Assert.True(peer.PreloadedStore.TryGet(messageId, out var again));
            Assert.Same(expected, again.WireResponse);
        }

        [Fact]
        public async Task Missing_article_returns_430_in_memory_mode()
        {
            var dir = CreateFixtureCorpus(out _, includeDotLine: false);
            await using var peer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions
                {
                    CorpusRoot = dir,
                    Port = 0,
                    PreloadArticlesIntoMemory = true,
                });

            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, peer.Port);
            await using var stream = client.GetStream();
            _ = await ReadLineAsync(stream);
            await stream.WriteAsync("ARTICLE <missing@test>\r\n"u8.ToArray());
            Assert.StartsWith("430 ", await ReadLineAsync(stream));
            Assert.Equal(1, peer.Metrics.ArticleMissing);
            Assert.Equal(0, peer.Metrics.ArticleSuccess);
        }

        [Fact]
        public async Task Startup_fails_when_indexed_article_cannot_be_loaded()
        {
            var dir = CreateFixtureCorpus(out var messageId, includeDotLine: false);
            var index = await CorpusArticleIndex.BuildAsync(dir);
            Assert.Equal(1, index.Count);
            Assert.True(index.TryResolve(messageId, out var path));
            File.Delete(path);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await PreloadedArticleStore.LoadAsync(index, maxArticleBytes: 1024 * 1024));
            Assert.Contains("failed to load", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Memory_mode_counters_and_dispose_rebind()
        {
            var dir = CreateFixtureCorpus(out var messageId, includeDotLine: false);
            int port;
            await using (var peer = await LocalArticleNntpPeer.StartAsync(
                             new LocalArticleNntpPeerOptions
                             {
                                 CorpusRoot = dir,
                                 Port = 0,
                                 PreloadArticlesIntoMemory = true,
                             }))
            {
                port = peer.Port;
                var wire = await CaptureArticleResponseAsync(peer.Port, messageId);
                Assert.True(EndsWithArticleTerminator(wire));
                await peer.Metrics.WaitForArticleSuccessAsync(expected: 1);
                Assert.Equal(1, peer.Metrics.ArticleRequests);
                Assert.Equal(1, peer.Metrics.ArticleSuccess);
                Assert.True(peer.Metrics.LogicalBytesTransferred > 0);
                Assert.True(peer.Metrics.WireBytesTransferred > peer.Metrics.LogicalBytesTransferred);
                Assert.Equal(0, peer.Metrics.OutstandingArticles);
            }

            await using var peer2 = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions
                {
                    CorpusRoot = dir,
                    Port = port,
                    PreloadArticlesIntoMemory = true,
                });
            Assert.Equal(port, peer2.Port);
            Assert.Equal("memory", peer2.PeerMode);
        }

        [Fact]
        public async Task BackFiller_client_downloads_from_memory_peer()
        {
            var dir = CreateFixtureCorpus(out var messageId, includeDotLine: false);
            await using var peer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions
                {
                    CorpusRoot = dir,
                    Port = 0,
                    PreloadArticlesIntoMemory = true,
                });

            LocalUpstreamIsolation.EnsureLocalUpstreamOrThrow("127.0.0.1");
            var provider = new BackFillerProviderDefinition(
                "LabLocal",
                "127.0.0.1",
                peer.Port,
                UseTls: false,
                Username: null,
                Password: null,
                MinSessions: 0,
                MaxSessions: 1);
            await using var session = new NntpProviderSession(
                provider,
                NntpSessionOptions.Default,
                NullLogger.Instance);
            var transport = new TcpNntpTransportFactory();
            Assert.Null(await session.ConnectAsync(transport, CancellationToken.None));
            using var result = await session.DownloadArticleAsync(messageId, CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
            Assert.Contains("hello"u8, result.Article!.Memory.Span);
            await peer.Metrics.WaitForArticleSuccessAsync(expected: 1);
            Assert.Equal(1, peer.Metrics.ArticleSuccess);
        }

        private static string CreateFixtureCorpus(out string messageId, bool includeDotLine)
        {
            messageId = $"<fixture-{Guid.NewGuid():N}@lab.test>";
            var dir = Path.Join(Path.GetTempPath(), "vnntp-preload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var body = includeDotLine
                ? $"Message-ID: {messageId}\r\nFrom: lab@test\r\n\r\nhello\r\n.dotline\r\n"
                : $"Message-ID: {messageId}\r\nFrom: lab@test\r\n\r\nhello\r\n";
            File.WriteAllText(Path.Join(dir, "article1"), body);
            return dir;
        }

        /// <summary>
        /// Captures the raw ARTICLE response (status + framed body) by reading until the multiline terminator.
        /// </summary>
        private static async Task<byte[]> CaptureArticleResponseAsync(int port, string messageId)
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
            await using var stream = client.GetStream();
            _ = await ReadLineAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"ARTICLE {messageId}\r\n"));

            using var buffer = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                var n = await stream.ReadAsync(one);
                if (n == 0)
                {
                    break;
                }

                buffer.WriteByte(one[0]);
                if (buffer.Length >= 5)
                {
                    var arr = buffer.GetBuffer();
                    var len = (int)buffer.Length;
                    if (arr[len - 5] == (byte)'\r'
                        && arr[len - 4] == (byte)'\n'
                        && arr[len - 3] == (byte)'.'
                        && arr[len - 2] == (byte)'\r'
                        && arr[len - 1] == (byte)'\n')
                    {
                        // Ensure this is a solitary terminator line (preceded by CRLF or start-of-body after status).
                        break;
                    }
                }
            }

            return buffer.ToArray();
        }

        private static bool EndsWithArticleTerminator(byte[] wire) =>
            wire.Length >= 5
            && wire[^5] == (byte)'\r'
            && wire[^4] == (byte)'\n'
            && wire[^3] == (byte)'.'
            && wire[^2] == (byte)'\r'
            && wire[^1] == (byte)'\n';

        private static async Task<string> ReadLineAsync(Stream stream)
        {
            var buffer = new List<byte>(128);
            var one = new byte[1];
            while (true)
            {
                var n = await stream.ReadAsync(one);
                if (n == 0)
                {
                    break;
                }

                buffer.Add(one[0]);
                if (buffer.Count >= 2 && buffer[^2] == (byte)'\r' && buffer[^1] == (byte)'\n')
                {
                    return Encoding.ASCII.GetString(buffer.GetRange(0, buffer.Count - 2).ToArray());
                }
            }

            return Encoding.ASCII.GetString(buffer.ToArray());
        }
    }
}
