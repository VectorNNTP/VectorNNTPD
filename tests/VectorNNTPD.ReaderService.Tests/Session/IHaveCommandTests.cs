using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.History;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Session;

public sealed class IHaveCommandTests
{
    private static NntpAuthorization TransitAuth { get; } = new(
        isAuthenticated: true,
        authorizedReader: false,
        authorizedTransit: true,
        postingPermitted: false,
        streamingPermitted: true);

    // IHAVE is a Transit-only command. A Reader session answers it with the role gate before the message-id is
    // checked against history, before the ingestion queue/budget is consulted, and before any article is read, so
    // none of the Transit IHAVE outcomes (335/235/435/436/437) can occur. Each test keeps the dependency
    // configuration of the former Transit IHAVE scenario and proves it is never touched.
    private const string CommandNotAvailable = "502 Command not available on this server";

    [Fact]
    public async Task ValidIhave_Returns502_AndEnqueuesNothing()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <want@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        await AssertStillFramedAsync(duplex);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.QueuedBytes);
    }

    [Fact]
    public async Task ValidIhave_UnknownNewsgroup_WantTrash_Returns502_AndWritesNoNews()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(
            queue,
            newsLog: news,
            transit: new TransitOptions { WantTrash = true, LogTrash = true });
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <ihave-wanttrash@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        await AssertStillFramedAsync(duplex);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
        Assert.Equal(0, queue.Count);
        Assert.Empty(news.Events);
    }

    [Fact]
    public async Task ValidIhave_DoesNotEvaluatePostFilter()
    {
        var filter = new CountingPostFilter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, postFilter: filter);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <want-pf@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        Assert.Equal(0, filter.EvaluateCalls);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task HistoryHit_Returns502_NotArticleNotWanted()
    {
        var redis = new FakeRedisService();
        var history = new HistoryDb(
            redis,
            TimeSpan.FromHours(2),
            NullLogger<HistoryDb>.Instance,
            TimeProvider.System,
            new HistoryWriteQueue());
        history.Remember("<have@example.com>"u8.ToArray());
        var writes = history.Writes.Count;
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 2 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, history, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <have@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        Assert.Empty(news.Events);
        await AssertStillFramedAsync(duplex);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
        Assert.Equal(0, queue.Count);
        Assert.Equal(writes, history.Writes.Count);
    }

    [Fact]
    public async Task RedisUnavailable_Returns502_NotTransferDeferral()
    {
        var redis = new FakeRedisService { IsUnavailable = true };
        var history = new HistoryDb(
            redis,
            TimeSpan.FromHours(2),
            NullLogger<HistoryDb>.Instance,
            TimeProvider.System,
            new HistoryWriteQueue());
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 2 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <later@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task MalformedMessageId_Is501()
    {
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(new ArticleIngestionQueue(new ArticleIngestionOptions()));
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE not-an-id");
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task QueueUnavailable_Returns502_NotTransferDeferral()
    {
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(DisabledArticleIngestionQueue.Instance);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <q@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task ArticleLargerThanQueueBudget_Returns502_AndWritesNoRejectedNews()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions { MaxArticleBytes = 1024 },
            transitQueueMemoryLimit: 16);
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <budget@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Empty(news.Events);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task BudgetExhausted_Returns502Immediately_AndDoesNotWait()
    {
        var held = FillArticle("<held@ex.com>");
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions(),
            transitQueueMemoryLimit: held.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(held));
        Assert.False(queue.TryProbeCapacity());

        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var response = duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("IHAVE <nowait@example.com>");
        Assert.Equal(CommandNotAvailable, await response.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(held.Payload.Length, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task QueueCompleted_Returns502_AndLeavesAccounting()
    {
        var sample = FillArticle("<shut@example.com>");
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions(),
            transitQueueMemoryLimit: sample.Payload.Length);
        queue.Complete();
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <shut@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public void BudgetExhausted_WarningIsRateLimited()
    {
        IHaveAdmissionLog.ResetForTests();
        IHaveAdmissionLog.WarningInterval = TimeSpan.FromHours(1);
        var recording = new RecordingLogger();
        var held = FillArticle("<held@ex.com>");
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions(),
            transitQueueMemoryLimit: held.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(held));

        IHaveAdmissionLog.BudgetExhausted(recording, queue);
        IHaveAdmissionLog.BudgetExhausted(recording, queue);
        var first = Assert.Single(recording.Warnings);
        Assert.Contains("TransitQueueMemoryLimit exhausted", first, StringComparison.Ordinal);
        Assert.Contains($"{held.Payload.Length}/{held.Payload.Length}", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Subject:", first, StringComparison.Ordinal);

        IHaveAdmissionLog.ResetForTests();
        IHaveAdmissionLog.BudgetExhausted(recording, queue);
        Assert.Equal(2, recording.Warnings.Count);
        IHaveAdmissionLog.ResetForTests();
    }

    [Fact]
    public async Task TooLargeConfiguration_Returns502_AndWritesNoRejectedNews()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions { QueueCapacity = 2, MaxArticleBytes = 16 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <big@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        await AssertStillFramedAsync(duplex);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
        Assert.Equal(0, queue.Count);
        Assert.Empty(news.Events);
    }

    [Fact]
    public async Task NewsWriter_IsNeverCalled_ForUnavailableIhave()
    {
        var news = new ThrowingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <news-fail-437@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, news.WriteCalls);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Capabilities_DoesNotAdvertiseIhave()
    {
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(new ArticleIngestionQueue(new ArticleIngestionOptions()));
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("CAPABILITIES");
        var lines = new List<string>();
        string line;
        do
        {
            line = await duplex.ReadClientLineAsync();
            lines.Add(line);
        }
        while (line != ".");

        Assert.DoesNotContain("IHAVE", lines);
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task WantTrashFalse_UnknownGroup_Returns502_AndWritesNoRejectedNews()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(
            queue,
            newsLog: news,
            transit: new TransitOptions { WantTrash = false, LogTrash = false },
            catalogue: CarriedCatalogue("alt.test"));
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <ihave-notrash@example.com>");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        Assert.Empty(news.Events);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Theory]
    [InlineData("<incomplete@example.com>")]
    [InlineData("<bad-mid@example.com>")]
    [InlineData("<bad-date@example.com>")]
    [InlineData("<yenc-meta@example.com>")]
    [InlineData("<yenc-crc@example.com>")]
    [InlineData("<command@example.com>")]
    public async Task IngressRejectionScenarios_Return502_AndWriteNoRejectedNews(string commandId)
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: false,
            authorizedTransit: true,
            postingPermitted: false,
            streamingPermitted: true,
            transitPeerName: "BlueWorldHosting"));
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE " + commandId);
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        await AssertStillFramedAsync(duplex);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
        Assert.Equal(0, queue.Count);
        Assert.Empty(news.Events);
    }

    private static async Task AssertStillFramedAsync(IHaveDuplex duplex)
    {
        // No 335 was sent, so no article body is expected; the next command is parsed as a command.
        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }
    private static InboundArticle FillArticle(string messageId) =>
        CanonicalArticleText.CreateQueued(messageId, InboundArticleProducer.IHave);

    private static INewsgroupCatalogue CarriedCatalogue(string group) =>
        new StaticNewsgroupCatalogue(
            NewsgroupSnapshot.Create(
                [new NewsgroupDefinition(group, string.Empty, 2, 1, NewsgroupPostingStatus.Allowed)]));

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class IHaveDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();

        public static Task<IHaveDuplex> CreateAsync() => Task.FromResult(new IHaveDuplex());

        public NntpSession CreateSession(
            IArticleIngestionQueue queue,
            IHistoryDb? history = null,
            IPostFilter? postFilter = null,
            INewsLogWriter? newsLog = null,
            TransitOptions? transit = null,
            INewsgroupCatalogue? catalogue = null)
        {
            var connection = new PipeConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                historyDb: history,
                postFilter: postFilter,
                newsgroupCatalogue: catalogue,
                newsLog: newsLog,
                transit: transit);
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

        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

        public System.Net.EndPoint? LocalEndPoint => null;

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
