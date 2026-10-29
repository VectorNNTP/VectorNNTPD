using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Transit;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.Transit;

/// <summary>
/// Role-isolation coverage: a Reader session never exposes the Transit receive commands, regardless of the
/// per-peer receive policy (size, patterns, article types, credentials) that would apply on the Transit service.
/// </summary>
/// <remarks>
/// The receive-policy behavior itself (size min, receive patterns, article types, peer credentials) is covered by
/// the Transit service tests. Here the contract is that the Reader dispatcher answers
/// <c>502 Command not available on this server</c> before any receive policy, authentication, or article read.
/// </remarks>
[Collection("NntpCommandLoggerCollection")]
public sealed class TransitReceivePolicyCommandTests
{
    private const string CommandNotAvailable = "502 Command not available on this server";
    private const int GlobalMax = 5 * 1024 * 1024;
    private const int OneMib = 1 * 1024 * 1024;
    private const int TenMib = 10 * 1024 * 1024;

    private static readonly IPAddress PeerAddress = IPAddress.Parse("192.0.2.10");

    public static IEnumerable<object[]> ArticlePaths()
    {
        yield return [ReceivePath.SerialTakeThis];
        yield return [ReceivePath.PipelinedTakeThis];
        yield return [ReceivePath.IHave];
    }

    public static IEnumerable<object[]> SizeCases()
    {
        foreach (var path in new[] { ReceivePath.SerialTakeThis, ReceivePath.PipelinedTakeThis, ReceivePath.IHave })
        {
            yield return [path, (long)OneMib];
            yield return [path, (long)TenMib];
            yield return [path, 0L];
        }
    }

    public static IEnumerable<object[]> BlankCredentialCases()
    {
        yield return ["", "secret"];
        yield return ["feed", ""];
        yield return ["", ""];
    }

    [Theory]
    [MemberData(nameof(SizeCases))]
    public async Task MaxArticleBytes_PeerPolicy_DoesNotExposeReceiveCommandsOnReader(ReceivePath path, long peerMax)
    {
        var policy = Peer(maxSize: peerMax, sendPatterns: "!alt.test");
        await using var harness = await Harness.OpenAsync(policy, path);
        Assert.Equal(peerMax, harness.Session.Authorization.TransitPeerPolicy!.MaxSize);

        await harness.AssertFeedCommandsUnavailableAsync("<small@example.test>");
    }

    [Theory]
    [MemberData(nameof(ArticlePaths))]
    public async Task ReceivePatterns_PeerPolicy_DoNotExposeReceiveCommandsOnReader(ReceivePath path)
    {
        foreach (var patterns in new[]
                 {
                     "*",
                     "*,!unidata.*",
                     "news.*,!news.misc,*.misc",
                     "alt.*,@alt.binaries.warez,misc.*",
                 })
        {
            await using var harness = await Harness.OpenAsync(Peer(receivePatterns: patterns, sendPatterns: "*"), path);
            await harness.AssertFeedCommandsUnavailableAsync("<patterns@example.test>");
        }
    }

    [Theory]
    [MemberData(nameof(ArticlePaths))]
    public async Task ReceiveArticleTypes_PeerPolicy_DoNotExposeReceiveCommandsOnReader(ReceivePath path)
    {
        foreach (var receiveTypes in new[] { TransitMessageTypes.All, TransitMessageTypes.Default })
        {
            await using var harness = await Harness.OpenAsync(
                Peer(receiveTypes: receiveTypes, sendTypes: TransitMessageTypes.All),
                path);
            await harness.AssertFeedCommandsUnavailableAsync("<types@example.test>");
        }
    }

    [Fact]
    public async Task Credentials_BothSet_ReaderAnswers502_NotAuthenticationRequired()
    {
        await using var harness = await Harness.OpenAsync(Peer(username: "feed", password: "secret"), ReceivePath.PipelinedTakeThis);
        Assert.True(harness.Session.Authorization.TransitPeerPolicy!.HasPeerCredentials);
        Assert.False(harness.Session.Authorization.AuthorizedTransit);

        await harness.AssertFeedCommandsUnavailableAsync("<locked@example.test>");

        await harness.Duplex.WriteClientLineAsync("AUTHINFO USER feed");
        Assert.StartsWith("381 ", await harness.Duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.Duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("481 Authentication failed", await harness.Duplex.ReadClientLineAsync());
        Assert.False(harness.Session.Authentication.IsAuthenticated);

        await harness.AssertFeedCommandsUnavailableAsync("<still-locked@example.test>");
    }

    [Theory]
    [MemberData(nameof(BlankCredentialCases))]
    public async Task Credentials_EitherBlank_ReaderStillDoesNotExposeFeedCommands(string username, string password)
    {
        await using var harness = await Harness.OpenAsync(Peer(username: username, password: password), ReceivePath.PipelinedTakeThis);
        Assert.False(harness.Session.Authorization.TransitPeerPolicy!.HasPeerCredentials);

        await harness.AssertFeedCommandsUnavailableAsync("<open@example.test>");
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
            siteName = "news.example";
            return true;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Task _run;

        private Harness(FeedDuplex duplex, NntpSession session, ArticleIngestionQueue queue, Task run)
        {
            Duplex = duplex;
            Session = session;
            Queue = queue;
            _run = run;
        }

        public FeedDuplex Duplex { get; }

        public NntpSession Session { get; }

        public ArticleIngestionQueue Queue { get; }

        public static async Task<Harness> OpenAsync(TransitPeerPolicy policy, ReceivePath path)
        {
            var store = new TransitConfigurationStore();
            store.Replace(Snapshot(policy));
            var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
            {
                QueueCapacity = 8,
                MaxArticleBytes = 64 * 1024 * 1024,
            });
            var duplex = await FeedDuplex.CreateAsync();
            var session = duplex.CreateSession(TransitPeerAuthorization.CreateForStore(store), queue);
            session.SharedConfiguration = new FixedSharedPolicy(GlobalMax);
            if (path == ReceivePath.SerialTakeThis)
            {
                session.SetMode(NntpSessionMode.Reader);
            }

            var run = session.RunAsync();
            _ = await duplex.ReadClientLineAsync();
            return new Harness(duplex, session, queue, run);
        }

        /// <summary>
        /// Sends the Transit-only receive commands. Each must be answered 502 by the role gate; no article
        /// body is sent because the Reader never reads one, and nothing may reach the ingestion queue.
        /// </summary>
        /// <remarks>
        /// A Reader-only host registers <see cref="TransitPeerAuthorization.Disabled"/>, so
        /// <c>AuthorizedTransit</c> is never true there. These tests inject a named peer to prove the peer policy
        /// does not expose the commands. When the injected peer is already transit-authorized, CHECK and
        /// stream-plane TAKETHIS are admitted by the session windows ahead of the dispatcher (an unreachable state
        /// on a real Reader host), so only the dispatcher-routed commands are exercised; the all-commands case is
        /// covered with the production Reader authorization by <c>TransitReceiveSizeAccountingTests</c> and
        /// <c>TransitDeferOnDuplicateCommandTests</c>.
        /// </remarks>
        public async Task AssertFeedCommandsUnavailableAsync(string id)
        {
            var commands = Session.Authorization.AuthorizedTransit
                ? new[] { "MODE STREAM", "IHAVE " + id }
                : new[] { "MODE STREAM", "CHECK " + id, "IHAVE " + id, "TAKETHIS " + id };
            foreach (var command in commands)
            {
                await Duplex.WriteClientLineAsync(command);
                Assert.Equal(CommandNotAvailable, await Duplex.ReadClientLineAsync());
            }

            Assert.Equal(0, Queue.Count);
        }

        public async ValueTask DisposeAsync()
        {
            await Duplex.WriteClientLineAsync("QUIT");
            _ = await Duplex.ReadClientLineAsync();
            await _run;
            await Duplex.DisposeAsync();
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
