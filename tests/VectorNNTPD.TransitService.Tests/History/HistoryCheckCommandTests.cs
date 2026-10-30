using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.TransitService.Redis;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.TransitService.Tests.Transit;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.TransitService.Tests.History;

public sealed class HistoryCheckCommandTests
{
    private static readonly IPAddress TransitPeer = IPAddress.Parse("198.18.0.70");
    private static readonly byte[] WantedId = "<i.am.an.article.you.will.want@example.com>"u8.ToArray();

    [Fact]
    public async Task DoubleMiss_Returns238_ByteIdenticalToStaticCompose()
    {
        var redis = new FakeRedisService();
        var history = CreateHistory(redis);
        await using var duplex = await HistoryCheckDuplex.CreateAsync();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <i.am.an.article.you.will.want@example.com>");
        var line = await duplex.ReadClientLineAsync();
        Assert.Equal("238 <i.am.an.article.you.will.want@example.com> send article to be transferred", line);

        var expected = NntpResponseCompose.Concat(
            NntpResponses.CheckPrefix.Span,
            WantedId,
            NntpResponses.CheckSuffix.Span);
        Assert.Equal(Encoding.ASCII.GetString(expected).TrimEnd('\r', '\n'), line);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
        Assert.Equal(1, redis.Database.KeyExistsCount);
        Assert.Equal(0, redis.Database.SetCount);
        Assert.Equal(0, history.Writes.Count);
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(WantedId)));
    }

    [Fact]
    public async Task MemoryHit_Returns431_AndDoesNotQueryRedis()
    {
        var redis = new FakeRedisService();
        var history = CreateHistory(redis);
        _ = await history.LookupAsync(WantedId);
        redis.Database.KeyExistsCount = 0;

        await using var duplex = await HistoryCheckDuplex.CreateAsync();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <i.am.an.article.you.will.want@example.com>");
        Assert.Equal("431 <i.am.an.article.you.will.want@example.com>", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
        Assert.Equal(0, redis.Database.KeyExistsCount);
    }

    [Fact]
    public async Task RedisHit_Returns431_Not238_AndDoesNotWriteRedis()
    {
        var redis = new FakeRedisService();
        redis.Database.Seed(HistoryRedisKeys.Create(HistoryDigest.FromMessageId(WantedId)));
        var history = CreateHistory(redis);

        await using var duplex = await HistoryCheckDuplex.CreateAsync();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <i.am.an.article.you.will.want@example.com>");
        Assert.Equal("431 <i.am.an.article.you.will.want@example.com>", await duplex.ReadClientLineAsync());
        Assert.True(history.ContainsLocal(HistoryDigest.FromMessageId(WantedId)));
        Assert.Equal(0, redis.Database.SetCount);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task RedisUnavailable_Returns431_Not238()
    {
        var redis = new FakeRedisService();
        redis.Database.ExistsException = new RedisUnavailableException("down");
        var history = CreateHistory(redis);

        await using var duplex = await HistoryCheckDuplex.CreateAsync();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <i.am.an.article.you.will.want@example.com>");
        Assert.Equal("431 <i.am.an.article.you.will.want@example.com>", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("CHECK <other@example.com>");
        Assert.Equal("431 <other@example.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(1, redis.Database.KeyExistsCount);
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(WantedId)));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task RedisRecovery_Returns238Then238_WithoutRecording()
    {
        var redis = new FakeRedisService();
        redis.Database.ExistsException = new RedisUnavailableException("down");
        var history = CreateHistory(redis);

        await using var duplex = await HistoryCheckDuplex.CreateAsync();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <i.am.an.article.you.will.want@example.com>");
        Assert.Equal("431 <i.am.an.article.you.will.want@example.com>", await duplex.ReadClientLineAsync());

        redis.Recover();
        redis.Database.ExistsException = null;
        await duplex.WriteClientLineAsync("CHECK <i.am.an.article.you.will.want@example.com>");
        Assert.Equal(
            "238 <i.am.an.article.you.will.want@example.com> send article to be transferred",
            await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("CHECK <i.am.an.article.you.will.want@example.com>");
        Assert.Equal(
            "238 <i.am.an.article.you.will.want@example.com> send article to be transferred",
            await duplex.ReadClientLineAsync());
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(WantedId)));
        Assert.Equal(0, history.Writes.Count);
        Assert.Equal(0, redis.Database.SetCount);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task DoubleMiss_DoesNotWaitForRedisPersistence()
    {
        var redis = new FakeRedisService();
        redis.Database.BlockSet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = CreateHistory(redis);

        await using var duplex = await HistoryCheckDuplex.CreateAsync();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <i.am.an.article.you.will.want@example.com>");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var line = await duplex.ReadClientLineAsync();
        Assert.Equal("238 <i.am.an.article.you.will.want@example.com> send article to be transferred", line);
        Assert.Equal(0, redis.Database.SetCount);
        Assert.Equal(0, history.Writes.Count);
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(WantedId)));
        redis.Database.BlockSet.SetResult();

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run.WaitAsync(cts.Token);
    }

    [Fact]
    public async Task RepeatedUnknownCheck_Remains238_AndDoesNotMutateHistory()
    {
        var redis = new FakeRedisService();
        var history = CreateHistory(redis);
        await using var duplex = await HistoryCheckDuplex.CreateAsync();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string wanted = "238 <i.am.an.article.you.will.want@example.com> send article to be transferred";
        await duplex.WriteClientLineAsync("CHECK <i.am.an.article.you.will.want@example.com>");
        Assert.Equal(wanted, await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("CHECK <i.am.an.article.you.will.want@example.com>");
        Assert.Equal(wanted, await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("CHECK <i.am.an.article.you.will.want@example.com>");
        Assert.Equal(wanted, await duplex.ReadClientLineAsync());

        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(WantedId)));
        Assert.Equal(0, history.Writes.Count);
        Assert.Equal(0, redis.Database.SetCount);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task UnknownCheck_ThenTakeThis_AcceptsAndRemembers()
    {
        var redis = new FakeRedisService();
        var history = CreateHistory(redis);
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await HistoryCheckDuplex.CreateAsync();
        var session = duplex.CreateSession(history, queue);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <i.am.an.article.you.will.want@example.com>");
        Assert.Equal(
            "238 <i.am.an.article.you.will.want@example.com> send article to be transferred",
            await duplex.ReadClientLineAsync());
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(WantedId)));
        Assert.Equal(0, history.Writes.Count);

        await duplex.WriteClientAsync(
            "TAKETHIS <i.am.an.article.you.will.want@example.com>\r\n" +
            CanonicalArticleText.Destuffed("<i.am.an.article.you.will.want@example.com>") +
            ".\r\n");
        Assert.Equal(
            "239 <i.am.an.article.you.will.want@example.com>",
            await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);
        Assert.True(history.ContainsLocal(HistoryDigest.FromMessageId(WantedId)));
        Assert.Equal(1, history.Writes.Count);

        await duplex.WriteClientLineAsync("CHECK <i.am.an.article.you.will.want@example.com>");
        Assert.Equal("431 <i.am.an.article.you.will.want@example.com>", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    private static HistoryDb CreateHistory(FakeRedisService redis) =>
        new(redis, TimeSpan.FromHours(2), NullLogger<HistoryDb>.Instance, TimeProvider.System, new HistoryWriteQueue());

    private sealed class HistoryCheckDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public static Task<HistoryCheckDuplex> CreateAsync() => Task.FromResult(new HistoryCheckDuplex());

        public NntpSession CreateSession(IHistoryDb history, IArticleIngestionQueue? queue = null)
        {
            var peers = TransitTestPeers.ForAllowFrom(TransitPeer);
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(TransitPeer, 40000)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                transitPeerAuthorization: peers,
                historyDb: history);
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
