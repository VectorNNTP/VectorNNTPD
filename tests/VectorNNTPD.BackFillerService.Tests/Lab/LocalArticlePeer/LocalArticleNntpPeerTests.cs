using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Nntp;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    public sealed class LocalArticleNntpPeerTests
    {
        [Fact]
        public async Task Peer_serves_greeting_capabilities_article_and_quit()
        {
            var dir = CreateFixtureCorpus(out var messageId);
            await using var peer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions { CorpusRoot = dir, Port = 0, MaxConcurrentSessions = 4 });

            Assert.True(peer.Port > 0);
            Assert.Equal(1, peer.Index.Count);

            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, peer.Port);
            await using var stream = client.GetStream();
            Assert.StartsWith("200 ", await ReadLineAsync(stream));

            await WriteAsync(stream, "CAPABILITIES\r\n"u8.ToArray());
            Assert.StartsWith("101 ", await ReadLineAsync(stream));
            while (true)
            {
                var cap = await ReadLineAsync(stream);
                if (cap == ".")
                {
                    break;
                }

                Assert.False(cap.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase));
            }

            await WriteAsync(stream, EncodingAscii($"ARTICLE {messageId}\r\n"));
            Assert.StartsWith("220 ", await ReadLineAsync(stream));
            var bodyLines = new List<string>();
            while (true)
            {
                var line = await ReadLineAsync(stream);
                if (line == ".")
                {
                    break;
                }

                bodyLines.Add(line);
            }

            Assert.Contains(bodyLines, static l => l.StartsWith("Message-ID:", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(bodyLines, static l => l == "hello");

            await WriteAsync(stream, "QUIT\r\n"u8.ToArray());
            Assert.StartsWith("205 ", await ReadLineAsync(stream));

            await peer.Metrics.WaitForArticleSuccessAsync(expected: 1);
            Assert.Equal(1, peer.Metrics.ArticleSuccess);
            Assert.Equal(0, peer.Metrics.ArticleMissing);
            Assert.Equal(0, peer.Metrics.OutstandingArticles);
        }

        [Fact]
        public async Task Peer_returns_430_for_unknown_message_id()
        {
            var dir = CreateFixtureCorpus(out _);
            await using var peer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions { CorpusRoot = dir, Port = 0 });

            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, peer.Port);
            await using var stream = client.GetStream();
            _ = await ReadLineAsync(stream);
            await WriteAsync(stream, "ARTICLE <missing@test>\r\n"u8.ToArray());
            Assert.StartsWith("430 ", await ReadLineAsync(stream));
            Assert.Equal(1, peer.Metrics.ArticleMissing);
        }

        [Fact]
        public async Task Peer_auth_flow_accepts_configured_credentials()
        {
            var dir = CreateFixtureCorpus(out var messageId);
            await using var peer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions
                {
                    CorpusRoot = dir,
                    Port = 0,
                    Username = "labuser",
                    Password = "labpass",
                });

            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, peer.Port);
            await using var stream = client.GetStream();
            _ = await ReadLineAsync(stream);
            await WriteAsync(stream, "AUTHINFO USER labuser\r\n"u8.ToArray());
            Assert.StartsWith("381 ", await ReadLineAsync(stream));
            await WriteAsync(stream, "AUTHINFO PASS labpass\r\n"u8.ToArray());
            Assert.StartsWith("281 ", await ReadLineAsync(stream));
            await WriteAsync(stream, EncodingAscii($"ARTICLE {messageId}\r\n"));
            Assert.StartsWith("220 ", await ReadLineAsync(stream));
            await DrainMultilineUntilTerminatorAsync(stream);
        }

        [Fact]
        public async Task BackFiller_client_downloads_article_from_peer_without_tls_or_auth()
        {
            var dir = CreateFixtureCorpus(out var messageId);
            await using var peer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions { CorpusRoot = dir, Port = 0 });

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
            Assert.NotNull(result.Article);
            Assert.Contains("hello"u8, result.Article!.Memory.Span);
            await peer.Metrics.WaitForArticleSuccessAsync(expected: 1);
            Assert.Equal(1, peer.Metrics.ArticleSuccess);
        }

        /// <summary>
        /// Lab rewrite shape (null credentials, cleartext loopback) is accepted by the peer without AUTHINFO.
        /// </summary>
        [Fact]
        public async Task Peer_accepts_unauthenticated_flow_matching_lab_rewrite()
        {
            var dir = CreateFixtureCorpus(out var messageId);
            await using var peer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions
                {
                    CorpusRoot = dir,
                    Port = 0,
                    Username = null,
                    Password = null,
                });
            Assert.False(
                new LocalArticleNntpPeerOptions { CorpusRoot = dir, Username = null, Password = null }
                    .RequiresAuthentication);

            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, peer.Port);
            await using var stream = client.GetStream();
            Assert.StartsWith("200 ", await ReadLineAsync(stream));
            await WriteAsync(stream, "CAPABILITIES\r\n"u8.ToArray());
            Assert.StartsWith("101 ", await ReadLineAsync(stream));
            await DrainMultilineUntilTerminatorAsync(stream);

            await WriteAsync(stream, EncodingAscii($"ARTICLE {messageId}\r\n"));
            Assert.StartsWith("220 ", await ReadLineAsync(stream));
            await DrainMultilineUntilTerminatorAsync(stream);

            await peer.Metrics.WaitForArticleSuccessAsync(expected: 1);
            Assert.Equal(1, peer.Metrics.ArticleSuccess);
            Assert.Equal(0, peer.Metrics.ProtocolErrors);
        }

        [Fact]
        public async Task Concurrent_article_requests_complete()
        {
            var dir = CreateFixtureCorpus(out var messageId);
            await using var peer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions { CorpusRoot = dir, Port = 0, MaxConcurrentSessions = 16 });

            var tasks = Enumerable.Range(0, 8).Select(async _ =>
            {
                var provider = new BackFillerProviderDefinition(
                    "LabLocal", "127.0.0.1", peer.Port, false, null, null, 0, 1);
                await using var session = new NntpProviderSession(
                    provider, NntpSessionOptions.Default, NullLogger.Instance);
                Assert.Null(await session.ConnectAsync(new TcpNntpTransportFactory(), CancellationToken.None));
                using var result = await session.DownloadArticleAsync(messageId, CancellationToken.None);
                Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
            });
            await Task.WhenAll(tasks);
            await peer.Metrics.WaitForArticleSuccessAsync(expected: 8);
            Assert.Equal(8, peer.Metrics.ArticleSuccess);
            Assert.Equal(0, peer.Metrics.OutstandingArticles);
        }

        [Fact]
        public async Task Dispose_releases_listener_and_allows_rebind()
        {
            var dir = CreateFixtureCorpus(out _);
            int port;
            await using (var peer = await LocalArticleNntpPeer.StartAsync(
                             new LocalArticleNntpPeerOptions { CorpusRoot = dir, Port = 0 }))
            {
                port = peer.Port;
                Assert.True(port > 0);
            }

            await using var peer2 = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions { CorpusRoot = dir, Port = port });
            Assert.Equal(port, peer2.Port);
        }

        [Fact]
        public async Task Rejects_sessions_beyond_concurrency_limit()
        {
            var dir = CreateFixtureCorpus(out var messageId);
            await using var peer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions { CorpusRoot = dir, Port = 0, MaxConcurrentSessions = 1 });

            var provider = new BackFillerProviderDefinition(
                "LabLocal", "127.0.0.1", peer.Port, false, null, null, 0, 1);
            await using var session = new NntpProviderSession(
                provider, NntpSessionOptions.Default, NullLogger.Instance);
            Assert.Null(await session.ConnectAsync(new TcpNntpTransportFactory(), CancellationToken.None));

            // Hold the single session open while a raw TCP connect should be rejected.
            using var extra = new System.Net.Sockets.TcpClient();
            await extra.ConnectAsync(System.Net.IPAddress.Loopback, peer.Port);
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (peer.Metrics.RejectedConnections < 1 && DateTime.UtcNow < deadline)
            {
                await Task.Yield();
            }

            Assert.True(peer.Metrics.RejectedConnections >= 1);

            using var result = await session.DownloadArticleAsync(messageId, CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
        }

        [Fact]
        public async Task LabBinaryIdentity_capture_hashes_loaded_assemblies()
        {
            var snap = LabBinaryIdentity.Capture();
            Assert.False(string.IsNullOrWhiteSpace(snap.BackFillerSha256));
            Assert.False(string.IsNullOrWhiteSpace(snap.CommonSha256));
            Assert.False(string.IsNullOrWhiteSpace(snap.PeerAssemblySha256));
            Assert.False(string.IsNullOrWhiteSpace(snap.RuntimeVersion));
        }

        private static string CreateFixtureCorpus(out string messageId)
        {
            messageId = $"<fixture-{Guid.NewGuid():N}@lab.test>";
            var dir = Path.Join(Path.GetTempPath(), "vnntp-local-peer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var body = $"Message-ID: {messageId}\r\nFrom: lab@test\r\n\r\nhello\r\n";
            File.WriteAllText(Path.Join(dir, "article1"), body);
            return dir;
        }

        private static byte[] EncodingAscii(string s) => System.Text.Encoding.ASCII.GetBytes(s);

        private static async Task WriteAsync(Stream stream, byte[] bytes) =>
            await stream.WriteAsync(bytes);

        /// <summary>Consumes NNTP multiline response lines until the terminator <c>.</c>.</summary>
        private static async Task DrainMultilineUntilTerminatorAsync(Stream stream)
        {
            while (await ReadLineAsync(stream) != ".")
            {
                // Intentionally discard article / capability lines.
            }
        }

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
                    return System.Text.Encoding.ASCII.GetString(buffer.GetRange(0, buffer.Count - 2).ToArray());
                }
            }

            return System.Text.Encoding.ASCII.GetString(buffer.ToArray());
        }
    }
}
