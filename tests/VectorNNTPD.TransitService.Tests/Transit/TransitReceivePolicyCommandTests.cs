using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.YEnc;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Transit;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.TransitService.Tests.Transit;

/// <summary>
/// Command-path coverage for incoming Transit receive size, newsgroup patterns, article types, and credentials.
/// </summary>
[Collection("NntpCommandLoggerCollection")]
public sealed class TransitReceivePolicyCommandTests
{
    private const int GlobalMax = 5 * 1024 * 1024;
    private const int OneMib = 1 * 1024 * 1024;
    private const int TenMib = 10 * 1024 * 1024;
    private const string SiteName = "news.example";

    private static readonly IPAddress PeerAddress = IPAddress.Parse("192.0.2.10");
    private static readonly NntpArticleParser Parser = new(NntpdOptions.FormatFqdn(1, "usenet.ninja"));
    private static readonly string YencBody = BuildYencBody();

    public static IEnumerable<object[]> SizeCases()
    {
        foreach (var path in new[] { ReceivePath.SerialTakeThis, ReceivePath.PipelinedTakeThis, ReceivePath.IHave })
        {
            yield return [path, (long)OneMib, OneMib];
            yield return [path, (long)TenMib, GlobalMax];
            yield return [path, 0L, GlobalMax];
        }
    }

    public static IEnumerable<object[]> ArticlePaths()
    {
        yield return [ReceivePath.SerialTakeThis];
        yield return [ReceivePath.PipelinedTakeThis];
        yield return [ReceivePath.IHave];
    }

    public static IEnumerable<object[]> TakeThisAndIHave()
    {
        yield return [ReceivePath.PipelinedTakeThis];
        yield return [ReceivePath.IHave];
    }

    [Theory]
    [MemberData(nameof(SizeCases))]
    public async Task MaxArticleBytes_UsesMinOfGlobalAndPeer(ReceivePath path, long peerMax, int effective)
    {
        var policy = Peer(maxSize: peerMax, sendPatterns: "!alt.test");
        await using var harness = await Harness.OpenAsync(policy, path);
        Assert.Equal(peerMax, harness.Session.Authorization.TransitPeerPolicy!.MaxSize);

        var smallId = "<small@example.test>";
        var small = await harness.OfferAsync(smallId, CanonicalArticleText.Destuffed(smallId), accept: true);
        Assert.Contains(SiteName, Encoding.ASCII.GetString(small!.Record.ArtData.Span), StringComparison.Ordinal);

        if (effective == OneMib)
        {
            var fitId = "<fit@example.test>";
            var fitted = FittedToCanonical(fitId, effective);
            var accepted = await harness.OfferAsync(fitId, fitted, accept: true);
            Assert.Equal(effective, accepted!.Record.ArtSize);
        }

        var overId = "<over@example.test>";
        await harness.OfferAsync(overId, Sized(overId, effective + 1), accept: false);
    }

    [Theory]
    [MemberData(nameof(ArticlePaths))]
    public async Task ReceivePatterns_MatchExcludeLastMatchPoison_AndIgnoreSendPatterns(ReceivePath path)
    {
        await using (var allow = await Harness.OpenAsync(Peer(receivePatterns: "*", sendPatterns: "!alt.test"), path))
        {
            await allow.CheckAsync("<check-allow@example.test>", wanted: true);
            var accepted = await allow.OfferAsync("<star@example.test>", GroupArticle("<star@example.test>", "alt.test"), accept: true);
            Assert.Equal(ArticleType.Default, accepted!.Record.ArtType);
        }

        await using (var bang = await Harness.OpenAsync(Peer(receivePatterns: "*,!unidata.*", sendPatterns: "*"), path))
        {
            await bang.CheckAsync("<check-bang@example.test>", wanted: true);
            await bang.OfferAsync(
                "<unidata@example.test>",
                GroupArticle("<unidata@example.test>", "unidata.maps"),
                accept: false);
            await bang.OfferAsync(
                "<comp@example.test>",
                GroupArticle("<comp@example.test>", "comp.lang.c"),
                accept: true);
        }

        await using (var lastAllows = await Harness.OpenAsync(Peer(receivePatterns: "news.*,!news.misc,*.misc"), path))
        {
            await lastAllows.OfferAsync(
                "<last-allow@example.test>",
                GroupArticle("<last-allow@example.test>", "news.misc"),
                accept: true);
        }

        await using (var lastRejects = await Harness.OpenAsync(Peer(receivePatterns: "news.*,!news.misc"), path))
        {
            await lastRejects.OfferAsync(
                "<last-reject@example.test>",
                GroupArticle("<last-reject@example.test>", "news.misc"),
                accept: false);
        }

        await using (var poison = await Harness.OpenAsync(Peer(receivePatterns: "alt.*,@alt.binaries.warez,misc.*"), path))
        {
            await poison.OfferAsync(
                "<poison@example.test>",
                GroupArticle("<poison@example.test>", "misc.test,alt.binaries.warez"),
                accept: false);
            await poison.OfferAsync(
                "<cross@example.test>",
                GroupArticle("<cross@example.test>", "misc.test,alt.fan"),
                accept: true);
        }

        await using (var receiveRejects = await Harness.OpenAsync(Peer(receivePatterns: "*,!alt.test", sendPatterns: "*"), path))
        {
            await receiveRejects.OfferAsync(
                "<send-ignored@example.test>",
                GroupArticle("<send-ignored@example.test>", "alt.test"),
                accept: false);
        }
    }

    [Theory]
    [MemberData(nameof(TakeThisAndIHave))]
    public async Task ReceiveArticleTypes_AllAllowsClassifiedTypes_TextOnlyRejectsTheRest(ReceivePath path)
    {
        var samples = Samples();
        await using (var all = await Harness.OpenAsync(
            Peer(receiveTypes: TransitMessageTypes.All, sendTypes: TransitMessageTypes.Default),
            path))
        {
            foreach (var sample in samples)
            {
                var id = "<all-" + sample.Name + "@example.test>";
                var article = Typed(id, sample.ExtraHeader, sample.Body);
                var classified = Classify(article);
                Assert.Equal(sample.Expected, classified);
                Assert.True(ArticleTypeCapabilities.Allows(ArticleTypeCapabilities.All, classified));
                var queued = await all.OfferAsync(id, article, accept: true);
                Assert.Equal(classified, queued!.Record.ArtType);
            }
        }

        await using var textOnly = await Harness.OpenAsync(
            Peer(receiveTypes: TransitMessageTypes.Default, sendTypes: TransitMessageTypes.All),
            path);
        foreach (var sample in samples)
        {
            var id = "<text-" + sample.Name + "@example.test>";
            var article = Typed(id, sample.ExtraHeader, sample.Body);
            var classified = Classify(article);
            var allowed = ArticleTypeCapabilities.Allows(ArticleTypeCapabilities.TextOnly, classified);
            var queued = await textOnly.OfferAsync(id, article, accept: allowed);
            if (allowed)
            {
                Assert.Equal(ArticleType.Default, classified);
                Assert.Equal(classified, queued!.Record.ArtType);
            }
            else
            {
                Assert.NotEqual(ArticleType.Default, classified);
                Assert.False(ArticleTypeCapabilities.Allows(ArticleType.Default, classified));
            }
        }
    }

    [Fact]
    public async Task Credentials_BothSet_RequireMatchingAuthinfoBeforeFeedCommands()
    {
        await using var harness = await Harness.OpenAsync(Peer(username: "feed", password: "secret"), ReceivePath.PipelinedTakeThis);
        Assert.True(harness.Session.Authorization.TransitPeerPolicy!.HasPeerCredentials);
        Assert.False(harness.Session.Authorization.AuthorizedTransit);
        Assert.True(harness.Session.Authorization.StreamingPermitted);

        await harness.Duplex.WriteClientLineAsync("CHECK <locked@example.test>");
        Assert.Equal("480 Authentication required", await harness.Duplex.ReadClientLineAsync());
        await harness.Duplex.WriteClientLineAsync("IHAVE <locked@example.test>");
        Assert.Equal("480 Authentication required", await harness.Duplex.ReadClientLineAsync());
        await harness.Duplex.WriteClientLineAsync("TAKETHIS <locked@example.test>");
        Assert.Equal("480 Authentication required", await harness.Duplex.ReadClientLineAsync());

        await harness.Duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await harness.Duplex.ReadClientLineAsync());
        await harness.Duplex.WriteClientLineAsync("AUTHINFO USER feed");
        Assert.StartsWith("381 ", await harness.Duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.Duplex.WriteClientLineAsync("AUTHINFO PASS wrong");
        Assert.Equal("481 Authentication failed", await harness.Duplex.ReadClientLineAsync());
        Assert.False(harness.Session.Authorization.AuthorizedTransit);
        await harness.Duplex.WriteClientLineAsync("CHECK <still-locked@example.test>");
        Assert.Equal("480 Authentication required", await harness.Duplex.ReadClientLineAsync());
        await harness.Duplex.WriteClientLineAsync("IHAVE <still-locked@example.test>");
        Assert.Equal("480 Authentication required", await harness.Duplex.ReadClientLineAsync());
        await harness.Duplex.WriteClientLineAsync("TAKETHIS <still-locked@example.test>");
        Assert.Equal("480 Authentication required", await harness.Duplex.ReadClientLineAsync());

        await harness.Duplex.WriteClientLineAsync("AUTHINFO USER feed");
        Assert.StartsWith("381 ", await harness.Duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.Duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("281 Authentication accepted", await harness.Duplex.ReadClientLineAsync());
        Assert.True(harness.Session.Authorization.AuthorizedTransit);

        await harness.CheckAsync("<open@example.test>", wanted: true);
        await harness.OfferAsync("<ihave-open@example.test>", CanonicalArticleText.Destuffed("<ihave-open@example.test>"), accept: true, forceIHave: true);
        await harness.OfferAsync("<take-open@example.test>", CanonicalArticleText.Destuffed("<take-open@example.test>"), accept: true);
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("feed", "")]
    [InlineData("", "")]
    public async Task Credentials_EitherBlank_LeavesFeedCommandsOpen(string username, string password)
    {
        await using var harness = await Harness.OpenAsync(Peer(username: username, password: password), ReceivePath.PipelinedTakeThis);
        Assert.False(harness.Session.Authorization.TransitPeerPolicy!.HasPeerCredentials);
        Assert.True(harness.Session.Authorization.AuthorizedTransit);
        await harness.CheckAsync("<open-check@example.test>", wanted: true);
        await harness.OfferAsync("<open-ihave@example.test>", CanonicalArticleText.Destuffed("<open-ihave@example.test>"), accept: true, forceIHave: true);
        await harness.OfferAsync("<open-take@example.test>", CanonicalArticleText.Destuffed("<open-take@example.test>"), accept: true);
    }

    [Fact]
    public async Task PreviousSnapshot_DoesNotMixReceiveFieldsWithTheNextRevision()
    {
        var store = new TransitConfigurationStore();
        store.Replace(Snapshot(Peer(maxSize: 2048, receivePatterns: "*"), publicationId: 1));
        await using var oldSession = await Harness.OpenAsync(store, ReceivePath.PipelinedTakeThis, globalMax: 4096);
        var captured = oldSession.Session.Authorization.TransitPeerPolicy;
        Assert.NotNull(captured);
        Assert.Equal(2048, captured.MaxSize);
        Assert.Equal("*", captured.ReceivePatterns.Expression);

        await oldSession.OfferAsync("<old-allow@example.test>", GroupArticle("<old-allow@example.test>", "alt.test"), accept: true);
        await oldSession.OfferAsync("<old-size@example.test>", Sized("<old-size@example.test>", 2049), accept: false);

        store.Replace(Snapshot(Peer(maxSize: 10_000, receivePatterns: "*,!alt.test"), publicationId: 2));
        Assert.Same(captured, oldSession.Session.Authorization.TransitPeerPolicy);
        await oldSession.OfferAsync("<old-still@example.test>", GroupArticle("<old-still@example.test>", "alt.test"), accept: true);
        await oldSession.OfferAsync("<old-still-size@example.test>", Sized("<old-still-size@example.test>", 3000), accept: false);

        await using var fresh = await Harness.OpenAsync(store, ReceivePath.PipelinedTakeThis, globalMax: 4096);
        Assert.NotSame(captured, fresh.Session.Authorization.TransitPeerPolicy);
        Assert.Equal(10_000, fresh.Session.Authorization.TransitPeerPolicy!.MaxSize);
        Assert.Equal("*,!alt.test", fresh.Session.Authorization.TransitPeerPolicy.ReceivePatterns.Expression);
        await fresh.OfferAsync("<new-alt@example.test>", GroupArticle("<new-alt@example.test>", "alt.test"), accept: false);
        await fresh.OfferAsync("<new-comp@example.test>", GroupArticle("<new-comp@example.test>", "comp.lang.c"), accept: true);
        await fresh.OfferAsync("<new-size@example.test>", Sized("<new-size@example.test>", 3000, "comp.lang.c"), accept: true);
    }

    private static TransitPeerPolicy Peer(
        long maxSize = TenMib,
        string receivePatterns = "*",
        string sendPatterns = "*",
        TransitMessageTypes receiveTypes = TransitMessageTypes.All,
        TransitMessageTypes sendTypes = TransitMessageTypes.All,
        string username = "",
        string password = "")
    {
        Assert.True(NewsfeedsPattern.TryParse(receivePatterns, out var receive, out var receiveError), receiveError);
        Assert.True(NewsfeedsPattern.TryParse(sendPatterns, out var send, out var sendError), sendError);
        return new TransitPeerPolicy(
            "peer",
            "Receive Peer",
            maxIncomingConnections: 8,
            maxOutgoingConnections: 0,
            literalPrefixes: [IpPrefix.Host(PeerAddress)],
            dnsHostnames: [],
            connectTo: [],
            username,
            password,
            TransitSslMode.None,
            send!,
            deferOnDuplicate: true,
            pathToken: string.Empty,
            maxSize,
            sendTypes,
            receive!,
            receiveTypes,
            sendMaxArticleBytes: 100,
            sendUsername: "send-user",
            sendPassword: "send-secret",
            pathExclusions: ["send-only"]);
    }

    private static TransitConfigurationSnapshot Snapshot(TransitPeerPolicy policy, long publicationId = 1) =>
        new(
            new Dictionary<string, TransitPeerPolicy>(StringComparer.Ordinal) { [policy.Identifier] = policy },
            publicationId: publicationId);

    private static string GroupArticle(string id, string newsgroups) =>
        CanonicalArticleText.Destuffed(id, "body\r\n", newsgroups);

    private static string Sized(string id, int destuffedLength, string newsgroups = "alt.test")
    {
        var skeleton = CanonicalArticleText.Destuffed(id, string.Empty, newsgroups);
        var bodyLength = destuffedLength - skeleton.Length;
        Assert.True(bodyLength >= 2);
        var body = new StringBuilder(bodyLength);
        var left = bodyLength;
        while (left > 0)
        {
            var content = Math.Min(60, left - 2);
            if (left - (content + 2) == 1)
            {
                content--;
            }

            Assert.InRange(content, 0, 60);
            body.Append('x', content);
            body.Append("\r\n");
            left -= content + 2;
        }

        var article = CanonicalArticleText.Destuffed(id, body.ToString(), newsgroups);
        Assert.Equal(destuffedLength, article.Length);
        return article;
    }

    private static string FittedToCanonical(string id, int canonicalLimit)
    {
        var probe = Sized(id, 256);
        var growth = CanonicalGrowth(probe);
        var fitted = Sized(id, canonicalLimit - growth);
        Assert.Equal(canonicalLimit, CanonicalGrowth(fitted) + fitted.Length);
        return fitted;
    }

    private static int CanonicalGrowth(string article)
    {
        var bytes = Encoding.ASCII.GetBytes(article);
        var created = ArticleRecordIngress.TryCreateFromDestuffed(
            Parser,
            bytes,
            bytes.Length + 65_536,
            Encoding.UTF8.GetBytes(SiteName));
        Assert.True(
            created.IsAccepted,
            created.ParseFailure + " / " + created.MaterializeFailure);
        return created.Record.ArtSize - bytes.Length;
    }

    private static string Typed(string id, string extraHeader, string body)
    {
        var text = CanonicalArticleText.Destuffed(id, body);
        if (extraHeader.Length == 0)
        {
            return text;
        }

        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, split), "\r\n", extraHeader, text.AsSpan(split));
    }

    private static ArticleType Classify(string article)
    {
        var created = ArticleRecordIngress.TryCreateFromDestuffed(Parser, Encoding.ASCII.GetBytes(article));
        Assert.True(created.IsAccepted);
        return created.Record.ArtType;
    }

    private static ArticleSample[] Samples() =>
    [
        new("text", string.Empty, "body\r\n", ArticleType.Default),
        new("control", "Control: newgroup alt.example\r\n", "body\r\n", ArticleType.Control),
        new("cancel", "Control: cancel <victim@example.test>\r\n", "body\r\n", ArticleType.Control | ArticleType.Cancel),
        new("mime", "Mime-Version: 1.0\r\n", "body\r\n", ArticleType.Mime),
        new("binary", "Content-Type: application/octet-stream\r\n", "body\r\n", ArticleType.Binary | ArticleType.Mime),
        new("yenc", string.Empty, YencBody, ArticleType.Binary | ArticleType.YEncoded),
        new("html", "Content-Type: text/html; charset=utf-8\r\n", "body\r\n", ArticleType.Html | ArticleType.Mime),
        new("multipart", "Content-Type: multipart/mixed; boundary=abc\r\n", "body\r\n", ArticleType.Multipart | ArticleType.Mime),
    ];

    private static string BuildYencBody()
    {
        var decoded = new byte[] { 0x41 };
        var crc = YEncCrc32.Compute(decoded);
        var encoded = unchecked((byte)(decoded[0] + 42));
        return $"=ybegin line=128 size=1 name=t.bin\r\n{(char)encoded}\r\n=yend size=1 crc32={crc:x8}\r\n";
    }

    private readonly record struct ArticleSample(string Name, string ExtraHeader, string Body, ArticleType Expected);

    public enum ReceivePath
    {
        SerialTakeThis,
        PipelinedTakeThis,
        IHave,
    }

    private sealed class FixedSharedPolicy : INntpArticlePolicySource
    {
        public FixedSharedPolicy(int maxArticleBytes) => MaxArticleBytes = maxArticleBytes;

        public int MaxArticleBytes { get; }

        public bool TryGetArticlePolicy(out int maxArticleBytes, out string siteName)
        {
            maxArticleBytes = MaxArticleBytes;
            siteName = SiteName;
            return true;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Task _run;
        private readonly ReceivePath _path;

        private Harness(FeedDuplex duplex, NntpSession session, ArticleIngestionQueue queue, Task run, ReceivePath path)
        {
            Duplex = duplex;
            Session = session;
            Queue = queue;
            _run = run;
            _path = path;
        }

        public FeedDuplex Duplex { get; }

        public NntpSession Session { get; }

        public ArticleIngestionQueue Queue { get; }

        public static Task<Harness> OpenAsync(TransitPeerPolicy policy, ReceivePath path) =>
            OpenAsync(Store(Snapshot(policy)), path, GlobalMax);

        public static async Task<Harness> OpenAsync(TransitConfigurationStore store, ReceivePath path, int globalMax)
        {
            var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
            {
                QueueCapacity = 8,
                MaxArticleBytes = 64 * 1024 * 1024,
            });
            var duplex = await FeedDuplex.CreateAsync();
            var session = duplex.CreateSession(TransitPeerAuthorization.CreateForStore(store), queue);
            session.SharedConfiguration = new FixedSharedPolicy(globalMax);
            if (path == ReceivePath.SerialTakeThis)
            {
                session.SetMode(NntpSessionMode.Reader);
            }

            var run = session.RunAsync();
            _ = await duplex.ReadClientLineAsync();
            return new Harness(duplex, session, queue, run, path);
        }

        public async Task CheckAsync(string id, bool wanted)
        {
            await Duplex.WriteClientLineAsync("CHECK " + id);
            var expected = wanted
                ? "238 " + id + " send article to be transferred"
                : "480 Authentication required";
            Assert.Equal(expected, await Duplex.ReadClientLineAsync());
        }

        public async Task<InboundArticle?> OfferAsync(string id, string article, bool accept, bool forceIHave = false)
        {
            var ihave = forceIHave || _path == ReceivePath.IHave;
            if (ihave)
            {
                await Duplex.WriteClientLineAsync("IHAVE " + id);
                Assert.Equal("335 Send article to be transferred", await Duplex.ReadClientLineAsync());
                await Duplex.WriteClientAsync(article + ".\r\n");
                Assert.Equal(
                    accept ? "235 Article transferred OK" : "437 Transfer rejected; do not retry",
                    await Duplex.ReadClientLineAsync());
            }
            else
            {
                await Duplex.WriteClientAsync("TAKETHIS " + id + "\r\n" + article + ".\r\n");
                Assert.Equal(accept ? "239 " + id : "439 " + id, await Duplex.ReadClientLineAsync());
            }

            if (!accept)
            {
                Assert.Equal(0, Queue.Count);
                return null;
            }

            var queued = await Queue.DequeueAsync(CancellationToken.None);
            Assert.NotNull(queued);
            Assert.Equal(id, queued.MessageId);
            Assert.Equal(0, Queue.Count);
            return queued;
        }

        public async ValueTask DisposeAsync()
        {
            await Duplex.WriteClientLineAsync("QUIT");
            _ = await Duplex.ReadClientLineAsync();
            await _run;
            await Duplex.DisposeAsync();
        }

        private static TransitConfigurationStore Store(TransitConfigurationSnapshot snapshot)
        {
            var store = new TransitConfigurationStore();
            store.Replace(snapshot);
            return store;
        }
    }

    private sealed class FeedDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();

        public static Task<FeedDuplex> CreateAsync() => Task.FromResult(new FeedDuplex());

        public NntpSession CreateSession(ITransitPeerAuthorization peers, IArticleIngestionQueue queue)
        {
            var connection = new PipeConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(PeerAddress, 119)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                transitPeerAuthorization: peers);
        }

        public async Task WriteClientLineAsync(string line)
        {
            await _clientToServer.Writer.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task WriteClientAsync(string payload)
        {
            await _clientToServer.Writer.WriteAsync(Encoding.ASCII.GetBytes(payload));
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }

        private sealed class PipeConnection : INntpConnection
        {
            private readonly CancellationTokenSource _cts = new();

            public PipeConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
            {
                Input = input;
                Output = output;
                ClientIdentity = identity;
            }

            public PipeReader Input { get; }

            public PipeWriter Output { get; }

            public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

            public EndPoint? LocalEndPoint => null;

            public ConnectionClientIdentity ClientIdentity { get; }

            public bool IsTls => false;

            public bool IsCompressed => false;

            public CancellationToken ConnectionClosed => _cts.Token;

            public bool IsCompleted => _cts.IsCancellationRequested;

            public long OutboundIdleVersion => 0;

            public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
            {
                tlsVersion = string.Empty;
                cipher = string.Empty;
                return false;
            }

            public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task WaitForOutboundDeliveryAsync(
                long outboundIdleVersionBeforeFlush,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task WaitForOutboundDeliveryAndPauseReadsAsync(
                long outboundIdleVersionBeforeFlush,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task CompleteAsync(Exception? exception = null)
            {
                _cts.Cancel();
                return Task.CompletedTask;
            }

            public Task UpgradeToTlsAsync(
                ITlsCertificateContextProvider certificateProvider,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public ValueTask DisposeAsync()
            {
                _cts.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
