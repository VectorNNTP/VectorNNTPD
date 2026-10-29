using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.History;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.Redis;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Transit;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.Transit;

/// <summary>
/// Role-isolation coverage for Receive <c>DeferOnDuplicate</c>: a Reader session answers every Transit feed
/// command with <c>502 Command not available on this server</c> before history is consulted, so the
/// duplicate/defer replies (431/438/436/435/239) are never produced on the Reader service.
/// </summary>
/// <remarks>The 431/438/436/435/239 behavior is covered by the Transit service tests.</remarks>
public sealed class TransitDeferOnDuplicateCommandTests
{
    private const string CommandNotAvailable = "502 Command not available on this server";
    private static readonly IPAddress PeerAddress = IPAddress.Parse("198.18.0.81");
    private static readonly byte[] SeenId = "<seen@example.com>"u8.ToArray();

    // CHECK is covered with TransitPeerAuthorization.Disabled, the authorization a Reader-only host registers.
    // A transit-authorized peer is never produced there; such a peer's CHECK would be admitted by the session
    // CHECK window ahead of the dispatcher, which is not a reachable Reader state.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Check_Seen_Returns502_AndDoesNotReplyDuplicate(bool readerMode)
    {
        var history = CreateHistory(new FakeRedisService());
        history.Remember(SeenId);
        var writes = history.Writes.Count;
        await using var duplex = await CommandDuplex.CreateAsync();
        var session = duplex.CreateSession(history, TransitPeerAuthorization.Disabled, readerMode);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <seen@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        Assert.Equal(0, session.ArticleIngestion.Count);
        Assert.Equal(writes, history.Writes.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task Check_Miss_Returns502_AndDoesNotConsultHistory()
    {
        var history = CreateHistory(new FakeRedisService());
        await using var duplex = await CommandDuplex.CreateAsync();
        var session = duplex.CreateSession(history, TransitPeerAuthorization.Disabled, readerMode: false);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <miss@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId("<miss@example.com>"u8)));

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task Check_HistoryUnavailable_Returns502_NotDeferral()
    {
        var redis = new FakeRedisService();
        redis.Database.ExistsException = new RedisUnavailableException("down");
        var history = CreateHistory(redis);
        await using var duplex = await CommandDuplex.CreateAsync();
        var session = duplex.CreateSession(history, TransitPeerAuthorization.Disabled, readerMode: false);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <down@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IHave_Seen_Returns502_AndDoesNotRequestTheBody(bool defer)
    {
        var history = CreateHistory(new FakeRedisService());
        history.Remember(SeenId);
        var writes = history.Writes.Count;
        await using var duplex = await CommandDuplex.CreateAsync();
        var session = duplex.CreateSession(history, defer, readerMode: false);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <seen@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        Assert.Equal(0, session.ArticleIngestion.Count);
        Assert.Equal(writes, history.Writes.Count);

        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await QuitAsync(duplex, run);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IHave_Miss_Returns502_AndNothingIsIngested(bool defer)
    {
        var history = CreateHistory(new FakeRedisService());
        var writes = history.Writes.Count;
        await using var duplex = await CommandDuplex.CreateAsync();
        var session = duplex.CreateSession(history, defer, readerMode: false);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <miss@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        Assert.Equal(0, session.ArticleIngestion.Count);
        Assert.Equal(writes, history.Writes.Count);

        await QuitAsync(duplex, run);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TakeThis_Seen_Returns502_AndNothingIsIngested(bool serial)
    {
        var history = CreateHistory(new FakeRedisService());
        history.Remember(SeenId);
        var writes = history.Writes.Count;
        await using var duplex = await CommandDuplex.CreateAsync();
        var session = duplex.CreateSession(history, TransitPeerAuthorization.Disabled, readerMode: serial);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        // No article body is sent: the Reader never reads one for an unavailable command.
        await duplex.WriteClientLineAsync("TAKETHIS <seen@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("TAKETHIS <next@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());

        Assert.Equal(0, session.ArticleIngestion.Count);
        Assert.Equal(writes, history.Writes.Count);
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId("<next@example.com>"u8)));

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task ExistingConnection_AndFreshConnection_BothReturn502_AfterCatalogueReplace()
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

        await existingDuplex.WriteClientLineAsync("IHAVE <seen@example.com>");
        Assert.Equal(CommandNotAvailable, await existingDuplex.ReadClientLineAsync());

        store.Replace(Snapshot(defer: false));
        Assert.True(existing.Authorization.TransitPeerPolicy.DeferOnDuplicate);
        await existingDuplex.WriteClientLineAsync("IHAVE <seen@example.com>");
        Assert.Equal(CommandNotAvailable, await existingDuplex.ReadClientLineAsync());

        await using var freshDuplex = await CommandDuplex.CreateAsync();
        var fresh = freshDuplex.CreateSession(history, peers, readerMode: false);
        var freshRun = fresh.RunAsync();
        _ = await freshDuplex.ReadClientLineAsync();
        Assert.False(fresh.Authorization.TransitPeerPolicy!.DeferOnDuplicate);
        await freshDuplex.WriteClientLineAsync("IHAVE <seen@example.com>");
        Assert.Equal(CommandNotAvailable, await freshDuplex.ReadClientLineAsync());

        await QuitAsync(freshDuplex, freshRun);
        await QuitAsync(existingDuplex, existingRun);
    }
    private static HistoryDb CreateHistory(FakeRedisService redis) =>
        new(redis, TimeSpan.FromHours(2), NullLogger<HistoryDb>.Instance, TimeProvider.System, new HistoryWriteQueue());

    private static TransitConfigurationSnapshot Snapshot(bool defer) =>
        TransitTestPeers.Snapshot(
            TransitTestPeers.DefaultPeerName,
            TransitTestPeers.Peer(allowFrom: [PeerAddress.ToString()], deferOnDuplicate: defer));

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
