using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.History;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Session.Commands.Posting;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Session;

public sealed class PostNewsgroupCatalogueTests
{
    private static readonly NntpAuthorization Poster = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: true,
        streamingPermitted: false);

    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    [Fact]
    public void Session_WithCatalogue_UsesCataloguePolicy()
    {
        var session = new NntpSession(
            new ClosedConnection(),
            NullLogger<NntpSession>.Instance,
            newsgroupCatalogue: new StaticNewsgroupCatalogue(Snapshot(Allowed("misc.test"))));
        Assert.IsType<CatalogueNewsgroupPostingPolicy>(session.NewsgroupPostingPolicy);
    }

    [Fact]
    public void Session_WithoutCatalogue_KeepsSyntaxOnlyPolicy()
    {
        var session = new NntpSession(new ClosedConnection(), NullLogger<NntpSession>.Instance);
        Assert.Same(SyntaxOnlyNewsgroupPostingPolicy.Instance, session.NewsgroupPostingPolicy);
    }

    [Fact]
    public async Task AllowedGroup_Returns240_AdmitsAndRemembers()
    {
        var (queue, history, inbound) = await PostAsync(
            Snapshot(Allowed("misc.test")),
            "misc.test",
            expected: "240 Article received OK");
        Assert.Equal(1, queue.TryAdmitCalls);
        Assert.Equal(1, history.RememberCalls);
        Assert.Equal(1, history.PeekCalls);
        Assert.NotNull(inbound);
        var text = Encoding.ASCII.GetString(inbound!.Payload.Span);
        Assert.Contains(".POSTED", text, StringComparison.Ordinal);
        Assert.Contains("Injection-Info: nntpd01.usenet.ninja;", text, StringComparison.Ordinal);
        Assert.Contains("X-Trace: ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProhibitedGroup_Returns441_WithoutAdmitOrRemember()
    {
        await AssertRejectedAsync(Snapshot(Group("no.post", NewsgroupPostingStatus.Prohibited)), "no.post");
    }

    [Fact]
    public async Task ModeratedGroup_Returns441_WithoutAdmitOrRemember()
    {
        await AssertRejectedAsync(Snapshot(Group("mod.test", NewsgroupPostingStatus.Moderated)), "mod.test");
    }

    [Fact]
    public async Task ClosedGroup_Returns441_WithoutAdmitOrRemember()
    {
        await AssertRejectedAsync(
            Snapshot(Group("closed.test", NewsgroupPostingStatus.NoPostingOrPeerArticles)),
            "closed.test");
    }

    [Fact]
    public async Task PeerOnlyGroup_Returns441_WithoutAdmitOrRemember()
    {
        await AssertRejectedAsync(Snapshot(Group("peer.only", NewsgroupPostingStatus.PeerOnly)), "peer.only");
    }

    [Fact]
    public async Task UnknownGroup_Returns441_WithoutAdmitOrRemember()
    {
        await AssertRejectedAsync(Snapshot(Allowed("misc.test")), "unknown.group");
    }

    [Fact]
    public async Task MultipleAllowed_Returns240()
    {
        var (queue, history, _) = await PostAsync(
            Snapshot(Allowed("misc.test"), Allowed("comp.test")),
            "misc.test,comp.test",
            expected: "240 Article received OK");
        Assert.Equal(1, queue.TryAdmitCalls);
        Assert.Equal(1, history.RememberCalls);
    }

    [Fact]
    public async Task Multiple_OneUnknown_Returns441()
    {
        await AssertRejectedAsync(Snapshot(Allowed("misc.test")), "misc.test,unknown.group");
    }

    [Fact]
    public async Task Multiple_OneProhibited_Returns441()
    {
        await AssertRejectedAsync(
            Snapshot(Allowed("misc.test"), Group("no.post", NewsgroupPostingStatus.Prohibited)),
            "misc.test,no.post");
    }

    [Fact]
    public async Task Multiple_OneModerated_Returns441()
    {
        await AssertRejectedAsync(
            Snapshot(Allowed("misc.test"), Group("mod.test", NewsgroupPostingStatus.Moderated)),
            "misc.test,mod.test");
    }

    [Fact]
    public async Task Multiple_OneClosed_Returns441()
    {
        await AssertRejectedAsync(
            Snapshot(Allowed("misc.test"), Group("closed.test", NewsgroupPostingStatus.NoPostingOrPeerArticles)),
            "misc.test,closed.test");
    }

    [Fact]
    public async Task Multiple_OnePeerOnly_Returns441()
    {
        await AssertRejectedAsync(
            Snapshot(Allowed("misc.test"), Group("peer.only", NewsgroupPostingStatus.PeerOnly)),
            "misc.test,peer.only");
    }

    [Fact]
    public async Task MixedCaseGroup_Returns240()
    {
        var (queue, history, _) = await PostAsync(
            Snapshot(Allowed("misc.test")),
            "MISC.TEST",
            expected: "240 Article received OK");
        Assert.Equal(1, queue.TryAdmitCalls);
        Assert.Equal(1, history.RememberCalls);
    }

    [Fact]
    public async Task ExactDuplicateNewsgroups_Returns441_WithoutCatalogueLookup()
    {
        var catalogue = new StaticNewsgroupCatalogue(Snapshot(Allowed("misc.test")));
        var queue = new RecordingIngestionQueue(NewQueue());
        var history = new RecordingHistoryDb();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, history, catalogue);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(Article("misc.test,misc.test") + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.TryAdmitCalls);
        Assert.Equal(0, history.RememberCalls);
        Assert.Equal(0, catalogue.CurrentReadCount);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task Post_UsesSingleCapturedSnapshot_WhenCatalogueIsReplaced()
    {
        var first = Snapshot(Allowed("misc.test"), Allowed("comp.test"));
        var catalogue = new PublishOnReadCatalogue(first, NewsgroupSnapshot.Empty);
        var queue = new RecordingIngestionQueue(NewQueue());
        var history = new RecordingHistoryDb();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, history, catalogue);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(Article("misc.test,comp.test") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());
        Assert.Equal(1, catalogue.Reads);
        Assert.Equal(1, queue.TryAdmitCalls);
        Assert.Equal(1, history.RememberCalls);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task EmptyNewsgroups_Returns441_WithoutAdmit()
    {
        var catalogue = new StaticNewsgroupCatalogue(Snapshot(Allowed("misc.test")));
        var queue = new RecordingIngestionQueue(NewQueue());
        var history = new RecordingHistoryDb();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, history, catalogue);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(Article(string.Empty) + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.TryAdmitCalls);
        Assert.Equal(0, history.RememberCalls);

        await QuitAsync(duplex, run);
    }

    private static async Task AssertRejectedAsync(NewsgroupSnapshot snapshot, string newsgroups)
    {
        var (queue, history, inbound) = await PostAsync(snapshot, newsgroups, expected: "441 Posting failed");
        Assert.Equal(0, queue.TryAdmitCalls);
        Assert.Equal(0, history.RememberCalls);
        Assert.Equal(0, history.PeekCalls);
        Assert.Null(inbound);
    }

    private static async Task<(RecordingIngestionQueue Queue, RecordingHistoryDb History, InboundArticle? Admitted)>
        PostAsync(NewsgroupSnapshot snapshot, string newsgroups, string expected)
    {
        var catalogue = new StaticNewsgroupCatalogue(snapshot);
        var queue = new RecordingIngestionQueue(NewQueue());
        var history = new RecordingHistoryDb();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, history, catalogue);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(Article(newsgroups) + ".\r\n");
        Assert.Equal(expected, await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
        return (queue, history, queue.Admitted.Count == 0 ? null : queue.Admitted[0]);
    }

    private static ArticleIngestionQueue NewQueue() =>
        new(new ArticleIngestionOptions { QueueCapacity = 8, MaxArticleBytes = NntpdOptions.DefaultMaxArticleSize });

    private static async Task QuitAsync(PostDuplex duplex, Task run)
    {
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    private static string Article(string newsgroups)
    {
        return
            "Date: " + PostRfcDate.Format(DateTimeOffset.UtcNow) + "\r\n" +
            "From: poster@example.com\r\n" +
            "Newsgroups: " + newsgroups + "\r\n" +
            "Subject: test\r\n" +
            "Message-ID: <ok@example.com>\r\n" +
            "\r\n" +
            "body\r\n";
    }

    private static NewsgroupSnapshot Snapshot(params NewsgroupDefinition[] definitions) =>
        NewsgroupSnapshot.Create(definitions);

    private static NewsgroupDefinition Allowed(string name) => Group(name, NewsgroupPostingStatus.Allowed);

    private static NewsgroupDefinition Group(string name, NewsgroupPostingStatus status) =>
        new(name, string.Empty, 2, 1, status);

    private sealed class PublishOnReadCatalogue : INewsgroupCatalogue
    {
        private readonly StaticNewsgroupCatalogue _inner;
        private readonly NewsgroupSnapshot _replacement;

        public PublishOnReadCatalogue(NewsgroupSnapshot first, NewsgroupSnapshot replacement)
        {
            _inner = new StaticNewsgroupCatalogue(first);
            _replacement = replacement;
        }

        public int Reads => _inner.CurrentReadCount;

        public NewsgroupSnapshot Current
        {
            get
            {
                var snapshot = _inner.Current;
                _inner.Publish(_replacement);
                return snapshot;
            }
        }
    }

    private sealed class RecordingHistoryDb : IHistoryDb
    {
        public int PeekCalls { get; private set; }

        public int RememberCalls { get; private set; }

        public ValueTask<HistoryLookupResult> LookupAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            new(HistoryLookupResult.Unseen);

        public ValueTask<HistoryLookupResult> PeekAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default)
        {
            PeekCalls++;
            return new(HistoryLookupResult.Unseen);
        }

        public void Remember(ReadOnlyMemory<byte> messageId) => RememberCalls++;

        public bool ContainsLocal(in HistoryDigest digest) => false;
    }

    private sealed class RecordingIngestionQueue : IArticleIngestionQueue
    {
        private readonly IArticleIngestionQueue _inner;

        public RecordingIngestionQueue(IArticleIngestionQueue inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        public int TryAdmitCalls { get; private set; }

        public List<InboundArticle> Admitted { get; } = [];

        public long MemoryLimitBytes => _inner.MemoryLimitBytes;

        public long QueuedBytes => _inner.QueuedBytes;

        public long PeakQueuedBytes => _inner.PeakQueuedBytes;

        public int MaxArticleBytes => _inner.MaxArticleBytes;

        public int Count => _inner.Count;

        public int PeakCount => _inner.PeakCount;

        public bool IsAccepting => _inner.IsAccepting;

        public ValueTask<ArticleEnqueueResult> EnqueueAsync(
            InboundArticle article,
            CancellationToken cancellationToken) =>
            _inner.EnqueueAsync(article, cancellationToken);

        public bool TryProbeCapacity() => _inner.TryProbeCapacity();

        public ArticleEnqueueResult TryAdmit(InboundArticle article)
        {
            TryAdmitCalls++;
            var result = _inner.TryAdmit(article);
            if (result == ArticleEnqueueResult.Accepted)
            {
                Admitted.Add(article);
            }

            return result;
        }

        public bool TryEnqueue(InboundArticle article) => _inner.TryEnqueue(article);

        public void Complete() => _inner.Complete();

        public ValueTask<InboundArticle?> DequeueAsync(CancellationToken cancellationToken) =>
            _inner.DequeueAsync(cancellationToken);
    }

    private sealed class PostDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public NntpSession CreateSession(
            IArticleIngestionQueue queue,
            IHistoryDb historyDb,
            INewsgroupCatalogue catalogue)
        {
            var connection = new PipeConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)));
            var session = new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                historyDb: historyDb,
                postingTraceProtector: AesGcmPostingTraceProtector.Create(
                    new NntpdOptions { XTraceKey = TestHostFactory.TestXTraceKey }),
                newsgroupCatalogue: catalogue);
            session.SetAuthorization(Poster);
            return session;
        }

        public async Task WriteClientLineAsync(string line) => await WriteClientAsync(line + "\r\n");

        public async Task WriteClientAsync(string payload)
        {
            await _clientToServer.Writer.WriteAsync(Encoding.ASCII.GetBytes(payload));
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(Safety);
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

    private sealed class ClosedConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();

        public PipeReader Input => throw new InvalidOperationException();

        public PipeWriter Output => throw new InvalidOperationException();

        public EndPoint? RemoteEndPoint => null;

        public EndPoint? LocalEndPoint => null;

        public ConnectionClientIdentity ClientIdentity { get; } =
            ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119));

        public bool IsTls => false;

        public bool IsCompressed => false;

        public CancellationToken ConnectionClosed => _cts.Token;

        public bool IsCompleted => true;

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

        public Task CompleteAsync(Exception? exception = null) => Task.CompletedTask;

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
