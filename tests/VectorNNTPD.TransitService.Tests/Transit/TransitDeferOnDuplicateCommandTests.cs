using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.TransitService.Redis;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Transit;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.TransitService.Tests.Transit;

/// <summary>
/// Command-path coverage for Receive <c>DeferOnDuplicate</c> on CHECK and IHAVE.
/// TAKETHIS keeps the accepted-duplicate reply for both flag values.
/// </summary>
public sealed class TransitDeferOnDuplicateCommandTests
{
    private static readonly IPAddress PeerAddress = IPAddress.Parse("198.18.0.81");
    private static readonly byte[] SeenId = "<seen@example.com>"u8.ToArray();

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Check_Seen_UsesDeferFlag(bool defer, bool readerMode)
    {
        var history = CreateHistory(new FakeRedisService());
        history.Remember(SeenId);
        await using var duplex = await CommandDuplex.CreateAsync();
        var session = duplex.CreateSession(history, defer, readerMode);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <seen@example.com>");
        Assert.Equal(defer ? "431 <seen@example.com>" : "438 <seen@example.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(0, session.ArticleIngestion.Count);

        await QuitAsync(duplex, run);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Check_Miss_Stays238(bool defer)
    {
        var history = CreateHistory(new FakeRedisService());
        await using var duplex = await CommandDuplex.CreateAsync();
        var session = duplex.CreateSession(history, defer, readerMode: false);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <miss@example.com>");
        Assert.Equal(
            "238 <miss@example.com> send article to be transferred",
            await duplex.ReadClientLineAsync());
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId("<miss@example.com>"u8)));

        await QuitAsync(duplex, run);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Check_Unavailable_Stays431(bool defer)
    {
        var redis = new FakeRedisService();
        redis.Database.ExistsException = new RedisUnavailableException("down");
        var history = CreateHistory(redis);
        await using var duplex = await CommandDuplex.CreateAsync();
        var session = duplex.CreateSession(history, defer, readerMode: false);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <down@example.com>");
        Assert.Equal("431 <down@example.com>", await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IHave_Seen_DoesNotRequestTheBody(bool defer)
    {
        var history = CreateHistory(new FakeRedisService());
        history.Remember(SeenId);
        var writes = history.Writes.Count;
        await using var duplex = await CommandDuplex.CreateAsync();
        var session = duplex.CreateSession(history, defer, readerMode: false);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <seen@example.com>");
        Assert.Equal(
            defer ? "436 Transfer not possible; try again later" : "435 Article not wanted",
            await duplex.ReadClientLineAsync());
        Assert.Equal(0, session.ArticleIngestion.Count);
        Assert.Equal(writes, history.Writes.Count);

        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await QuitAsync(duplex, run);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IHave_Miss_Stays335(bool defer)
    {
        var history = CreateHistory(new FakeRedisService());
        await using var duplex = await CommandDuplex.CreateAsync();
        var session = duplex.CreateSession(history, defer, readerMode: false);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <miss@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(Article("<miss@example.com>"));
        Assert.Equal("235 Article transferred OK", await duplex.ReadClientLineAsync());
        Assert.Equal(1, session.ArticleIngestion.Count);

        await QuitAsync(duplex, run);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task TakeThis_Seen_Returns239_AndTheNextArticleStaysInOrder(bool serial, bool defer)
    {
        var history = CreateHistory(new FakeRedisService());
        history.Remember(SeenId);
        var writes = history.Writes.Count;
        await using var duplex = await CommandDuplex.CreateAsync();
        var session = duplex.CreateSession(history, defer, readerMode: serial);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var duplicate = "TAKETHIS <seen@example.com>\r\n" + Article("<seen@example.com>");
        var next = "TAKETHIS <next@example.com>\r\n" + Article("<next@example.com>");
        if (serial)
        {
            await duplex.WriteClientAsync(duplicate);
            Assert.Equal("239 <seen@example.com>", await duplex.ReadClientLineAsync());
            Assert.Equal(0, session.ArticleIngestion.Count);
            Assert.Equal(writes, history.Writes.Count);

            await duplex.WriteClientAsync(next);
            Assert.Equal("239 <next@example.com>", await duplex.ReadClientLineAsync());
        }
        else
        {
            await duplex.WriteClientAsync(duplicate + next);
            Assert.Equal("239 <seen@example.com>", await duplex.ReadClientLineAsync());
            Assert.Equal("239 <next@example.com>", await duplex.ReadClientLineAsync());
        }

        Assert.Equal(1, session.ArticleIngestion.Count);
        Assert.Equal(writes + 1, history.Writes.Count);
        Assert.True(history.ContainsLocal(HistoryDigest.FromMessageId("<next@example.com>"u8)));

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task ExistingConnection_KeepsDeferOnDuplicate_AfterCatalogueReplace()
    {
        var store = new TransitConfigurationStore();
        store.Replace(Snapshot(defer: true));
        var peers = TransitPeerAuthorization.CreateForStore(store);
        var history = CreateHistory(new FakeRedisService());
        history.Remember(SeenId);

        await using var existingDuplex = await CommandDuplex.CreateAsync();
        var existing = existingDuplex.CreateSession(history, peers, readerMode: false);
        var existingRun = existing.RunAsync();
        _ = await existingDuplex.ReadClientLineAsync();
        Assert.True(existing.Authorization.TransitPeerPolicy!.DeferOnDuplicate);

        await existingDuplex.WriteClientLineAsync("CHECK <seen@example.com>");
        Assert.Equal("431 <seen@example.com>", await existingDuplex.ReadClientLineAsync());

        store.Replace(Snapshot(defer: false));
        Assert.True(existing.Authorization.TransitPeerPolicy.DeferOnDuplicate);
        await existingDuplex.WriteClientLineAsync("CHECK <seen@example.com>");
        Assert.Equal("431 <seen@example.com>", await existingDuplex.ReadClientLineAsync());

        await using var freshDuplex = await CommandDuplex.CreateAsync();
        var fresh = freshDuplex.CreateSession(history, peers, readerMode: false);
        var freshRun = fresh.RunAsync();
        _ = await freshDuplex.ReadClientLineAsync();
        Assert.False(fresh.Authorization.TransitPeerPolicy!.DeferOnDuplicate);
        await freshDuplex.WriteClientLineAsync("CHECK <seen@example.com>");
        Assert.Equal("438 <seen@example.com>", await freshDuplex.ReadClientLineAsync());

        await QuitAsync(freshDuplex, freshRun);
        await QuitAsync(existingDuplex, existingRun);
    }

    private static HistoryDb CreateHistory(FakeRedisService redis) =>
        new(redis, TimeSpan.FromHours(2), NullLogger<HistoryDb>.Instance, TimeProvider.System, new HistoryWriteQueue());

    private static TransitConfigurationSnapshot Snapshot(bool defer) =>
        TransitTestPeers.Snapshot(
            TransitTestPeers.DefaultPeerName,
            TransitTestPeers.Peer(allowFrom: [PeerAddress.ToString()], deferOnDuplicate: defer));

    private static string Article(string messageId) =>
        CanonicalArticleText.Destuffed(messageId) + ".\r\n";

    private static async Task QuitAsync(CommandDuplex duplex, Task run)
    {
        await duplex.WriteClientLineAsync("QUIT");
        Assert.Equal("205 Connection closing", await duplex.ReadClientLineAsync());
        await run;
    }

    private sealed class CommandDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public static Task<CommandDuplex> CreateAsync() => Task.FromResult(new CommandDuplex());

        public NntpSession CreateSession(IHistoryDb history, bool defer, bool readerMode) =>
            CreateSession(
                history,
                TransitTestPeers.ForAllowFrom(PeerAddress, deferOnDuplicate: defer),
                readerMode);

        public NntpSession CreateSession(IHistoryDb history, ITransitPeerAuthorization peers, bool readerMode)
        {
            var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
            {
                QueueCapacity = 4,
                MaxArticleBytes = 1024 * 1024,
            });
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(PeerAddress, 40000)));
            var session = new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                transitPeerAuthorization: peers,
                historyDb: history);
            if (readerMode)
            {
                session.SetMode(NntpSessionMode.Reader);
            }

            return session;
        }

        public async Task WriteClientAsync(string payload)
        {
            var bytes = Encoding.ASCII.GetBytes(payload);
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task WriteClientLineAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
    }

    private sealed class PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
        : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();

        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
        public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public EndPoint? LocalEndPoint => null;
        public ConnectionClientIdentity ClientIdentity { get; } = identity;
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
