using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.TransitService.Transit;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.TransitService.Tests.Transit;

/// <summary>
/// Proves stream-plane TAKETHIS and IHAVE apply
/// <c>MaxArticleBytes</c> to the same destuffed article bytes.
/// </summary>
public sealed class TransitReceiveSizeAccountingTests
{
    private const int Limit = 1_000_000;

    private static readonly IPAddress Peer = IPAddress.Parse("198.18.0.82");

    [Fact]
    public async Task StuffedOverTheWire_WithinDestuffedLimit_IsAcceptedOnEveryReceivePath()
    {
        var id = "<size-stuffed-fit@example.com>";
        var stuffed = StuffedWithinDestuffedLimit(id, out var destuffedLength, out var canonicalSize);
        Assert.True(destuffedLength <= Limit);
        // CanonicalV1 ArtData keeps dot-stuffing, so the canonical size is never smaller than the stuffed
        // wire size: the stuffed wire exceeds the destuffed length yet still fits within the limit.
        Assert.True(stuffed.Length > destuffedLength);
        Assert.True(stuffed.Length <= canonicalSize);
        Assert.True(canonicalSize <= Limit);

        foreach (var path in new[] { ReceivePath.StreamTakeThis, ReceivePath.IHave })
        {
            await using var harness = await CommandHarness.StartAsync();
            var writes = harness.History.Writes.Count;
            await harness.OfferAsync(path, id, stuffed);

            var accepted = await harness.ReadLineAsync();
            if (path == ReceivePath.IHave)
            {
                Assert.StartsWith("335 ", accepted, StringComparison.Ordinal);
                accepted = await harness.ReadLineAsync();
                Assert.StartsWith("235 ", accepted, StringComparison.Ordinal);
            }
            else
            {
                Assert.StartsWith("239 ", accepted, StringComparison.Ordinal);
            }

            Assert.Equal(1, harness.Queue.Count);
            var queued = await harness.Queue.DequeueAsync(CancellationToken.None);
            Assert.NotNull(queued);
            Assert.Equal(id, queued.MessageId);
            Assert.True(queued.Record.ArtSize <= Limit);
            Assert.Equal(writes + 1, harness.History.Writes.Count);
            Assert.True(harness.History.ContainsLocal(HistoryDigest.FromMessageId(Encoding.ASCII.GetBytes(id))));
        }
    }

    [Fact]
    public async Task DestuffedOverLimit_RejectsOnEveryReceivePath_AndStaysFramed()
    {
        var id = "<size-plain-over@example.com>";
        var article = PlainOverLimit(id);
        Assert.True(article.Length > Limit);

        foreach (var path in new[] { ReceivePath.StreamTakeThis, ReceivePath.IHave })
        {
            await using var harness = await CommandHarness.StartAsync();
            var writes = harness.History.Writes.Count;
            await harness.OfferAsync(path, id, article);

            var reply = await harness.ReadLineAsync();
            if (path == ReceivePath.IHave)
            {
                Assert.StartsWith("335 ", reply, StringComparison.Ordinal);
                reply = await harness.ReadLineAsync();
                Assert.StartsWith("437 ", reply, StringComparison.Ordinal);
            }
            else
            {
                Assert.StartsWith("439 ", reply, StringComparison.Ordinal);
            }

            await harness.WriteCommandAsync("DATE\r\n");
            var date = await harness.ReadLineAsync();
            Assert.StartsWith("111 ", date, StringComparison.Ordinal);
            Assert.Equal(0, harness.Queue.Count);
            Assert.Equal(writes, harness.History.Writes.Count);
            Assert.False(harness.History.ContainsLocal(HistoryDigest.FromMessageId(Encoding.ASCII.GetBytes(id))));
        }
    }

    [Fact]
    public async Task StuffedOverDestuffedLimit_RejectsOnEveryReceivePath()
    {
        var id = "<size-stuffed-over@example.com>";
        var stuffed = StuffedOverDestuffedLimit(id, out var destuffedLength);
        Assert.True(destuffedLength > Limit);
        Assert.True(stuffed.Length > destuffedLength);

        foreach (var path in new[] { ReceivePath.StreamTakeThis, ReceivePath.IHave })
        {
            await using var harness = await CommandHarness.StartAsync();
            await harness.OfferAsync(path, id, stuffed);
            var reply = await harness.ReadLineAsync();
            if (path == ReceivePath.IHave)
            {
                Assert.StartsWith("335 ", reply, StringComparison.Ordinal);
                reply = await harness.ReadLineAsync();
                Assert.StartsWith("437 ", reply, StringComparison.Ordinal);
            }
            else
            {
                Assert.StartsWith("439 ", reply, StringComparison.Ordinal);
            }

            await harness.WriteCommandAsync("DATE\r\n");
            Assert.StartsWith("111 ", await harness.ReadLineAsync(), StringComparison.Ordinal);
            Assert.Equal(0, harness.Queue.Count);
            Assert.False(harness.History.ContainsLocal(HistoryDigest.FromMessageId(Encoding.ASCII.GetBytes(id))));
        }
    }

    [Fact]
    public async Task DestuffedWithinLimit_CanonicalOverLimit_IsRejectedByMaterialization()
    {
        var id = "<size-canonical-over@example.com>";
        var article = PlainWithinDestuffedButCanonicalOver(id, out var destuffedLength, out var canonicalSize);
        Assert.True(destuffedLength <= Limit);
        Assert.True(canonicalSize > Limit);

        foreach (var path in new[] { ReceivePath.StreamTakeThis, ReceivePath.IHave })
        {
            await using var harness = await CommandHarness.StartAsync();
            var writes = harness.History.Writes.Count;
            await harness.OfferAsync(path, id, article);
            var reply = await harness.ReadLineAsync();
            if (path == ReceivePath.IHave)
            {
                Assert.StartsWith("335 ", reply, StringComparison.Ordinal);
                reply = await harness.ReadLineAsync();
                Assert.StartsWith("437 ", reply, StringComparison.Ordinal);
            }
            else
            {
                Assert.StartsWith("439 ", reply, StringComparison.Ordinal);
            }

            Assert.Equal(0, harness.Queue.Count);
            Assert.Equal(writes, harness.History.Writes.Count);
        }
    }

    private static byte[] StuffedWithinDestuffedLimit(string id, out int destuffedLength, out int canonicalSize)
    {
        var growth = CanonicalGrowth(id);
        var headerLength = Encoding.ASCII.GetByteCount(CanonicalArticleText.Destuffed(id, string.Empty));
        var lines = (Limit - growth - headerLength) / 4;
        Assert.True(lines > 0);
        var body = new StringBuilder(lines * 3);
        for (var i = 0; i < lines; i++)
        {
            body.Append(".\r\n");
        }

        var destuffed = CanonicalArticleText.Destuffed(id, body.ToString());
        destuffedLength = Encoding.ASCII.GetByteCount(destuffed);
        canonicalSize = CanonicalLength(destuffed);
        var stuffed = Encoding.ASCII.GetBytes(destuffed.Replace("\r\n.", "\r\n..", StringComparison.Ordinal));
        Assert.True(stuffed.Length > destuffedLength);
        return stuffed;
    }

    private static byte[] StuffedOverDestuffedLimit(string id, out int destuffedLength)
    {
        var headerLength = Encoding.ASCII.GetByteCount(CanonicalArticleText.Destuffed(id, string.Empty));
        var lines = ((Limit - headerLength) / 3) + 1;
        var body = new StringBuilder(lines * 3);
        for (var i = 0; i < lines; i++)
        {
            body.Append(".\r\n");
        }

        var destuffed = CanonicalArticleText.Destuffed(id, body.ToString());
        destuffedLength = Encoding.ASCII.GetByteCount(destuffed);
        return Encoding.ASCII.GetBytes(destuffed.Replace("\r\n.", "\r\n..", StringComparison.Ordinal));
    }

    private static byte[] PlainOverLimit(string id) =>
        Encoding.ASCII.GetBytes(Sized(id, Limit + 2));

    private static byte[] PlainWithinDestuffedButCanonicalOver(
        string id,
        out int destuffedLength,
        out int canonicalSize)
    {
        var growth = CanonicalGrowth(id);
        Assert.True(growth > 0);
        var article = Sized(id, Limit - growth + 1);
        destuffedLength = article.Length;
        canonicalSize = CanonicalLength(article);
        Assert.True(destuffedLength <= Limit);
        Assert.True(canonicalSize > Limit);
        return Encoding.ASCII.GetBytes(article);
    }

    private static string Sized(string id, int destuffedLength)
    {
        var skeleton = CanonicalArticleText.Destuffed(id, string.Empty);
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

        var article = CanonicalArticleText.Destuffed(id, body.ToString());
        Assert.Equal(destuffedLength, article.Length);
        return article;
    }

    private static int CanonicalGrowth(string id)
    {
        var probe = CanonicalArticleText.Destuffed(id, "probe\r\n");
        return CanonicalLength(probe) - Encoding.ASCII.GetByteCount(probe);
    }

    private static int CanonicalLength(string destuffed)
    {
        var parser = new NntpArticleParser(NntpdOptions.FormatFqdn(1, "usenet.ninja"));
        var bytes = Encoding.ASCII.GetBytes(destuffed);
        var created = ArticleRecordIngress.TryCreateFromDestuffed(
            parser,
            bytes,
            maxArticleBytes: 5 * 1024 * 1024,
            ArticlePathCanonicalizer.OrganizationalTrackerHost);
        Assert.True(created.IsAccepted, created.ParseFailure + " / " + created.MaterializeFailure);
        return created.Record.ArtSize;
    }

    private enum ReceivePath
    {
        StreamTakeThis,
        IHave,
    }

    private sealed class CommandHarness : IAsyncDisposable
    {
        private readonly Pipe _clientToServer;
        private readonly Pipe _serverToClient;
        private readonly NntpSession _session;
        private readonly Task _run;

        private CommandHarness(
            Pipe clientToServer,
            Pipe serverToClient,
            NntpSession session,
            Task run,
            HistoryDb history,
            ArticleIngestionQueue queue)
        {
            _clientToServer = clientToServer;
            _serverToClient = serverToClient;
            _session = session;
            _run = run;
            History = history;
            Queue = queue;
        }

        public HistoryDb History { get; }

        public ArticleIngestionQueue Queue { get; }

        public static async Task<CommandHarness> StartAsync()
        {
            var clientToServer = new Pipe(new PipeOptions(
                pauseWriterThreshold: 8 * 1024 * 1024,
                resumeWriterThreshold: 4 * 1024 * 1024,
                minimumSegmentSize: 4 * 1024,
                useSynchronizationContext: false));
            var serverToClient = new Pipe(NntpPipeOptions.Create());
            var history = new HistoryDb(
                new FakeRedisService(),
                TimeSpan.FromHours(2),
                NullLogger<HistoryDb>.Instance,
                TimeProvider.System,
                new HistoryWriteQueue());
            var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
            {
                QueueCapacity = 4,
                MaxArticleBytes = Limit,
            });
            var connection = new PipeNntpConnection(
                clientToServer.Reader,
                serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(Peer, 40000)));
            var session = new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                transitPeerAuthorization: TransitTestPeers.ForAllowFrom(Peer),
                historyDb: history);
            var run = session.RunAsync();
            var harness = new CommandHarness(clientToServer, serverToClient, session, run, history, queue);
            Assert.StartsWith("2", await harness.ReadLineAsync(), StringComparison.Ordinal);
            return harness;
        }

        public async Task OfferAsync(ReceivePath path, string id, byte[] article)
        {
            var command = path == ReceivePath.IHave ? "IHAVE" : "TAKETHIS";
            await WriteCommandAsync($"{command} {id}\r\n");
            await _clientToServer.Writer.WriteAsync(article);
            await WriteCommandAsync(".\r\n");
        }

        public async Task WriteCommandAsync(string command)
        {
            var result = await _clientToServer.Writer.WriteAsync(Encoding.ASCII.GetBytes(command));
            Assert.False(result.IsCompleted);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadLineAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, timeout.Token);
            Assert.NotNull(line);
            return line;
        }

        public async ValueTask DisposeAsync()
        {
            await WriteCommandAsync("QUIT\r\n");
            Assert.Equal("205 Connection closing", await ReadLineAsync());
            await _run;
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
