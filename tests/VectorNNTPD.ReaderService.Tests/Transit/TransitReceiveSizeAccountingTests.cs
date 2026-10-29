using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.History;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.ReaderService.Transit;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.Transit;

/// <summary>
/// Role isolation for the receive size scenarios: the Reader service answers MODE STREAM, IHAVE and TAKETHIS with
/// <c>502 Command not available on this server</c> whatever the article size, so no article is read, nothing is
/// queued, and no history entry is written. The Transit service tests prove the size accounting itself.
/// </summary>
/// <remarks>
/// Sessions use <see cref="TransitPeerAuthorization.Disabled"/>, which is what a Reader-only host registers.
/// Each scenario still builds its article and asserts the size precondition that defines it, so the scenario stays
/// pinned to the same wire/destuffed/canonical boundaries as the Transit tests.
/// </remarks>
public sealed class TransitReceiveSizeAccountingTests
{
    private const int Limit = 1_000_000;
    private const int CanonicalizationMaxArticleBytes = 5 * 1024 * 1024;
    private const string CommandNotAvailable = "502 Command not available on this server";

    private static readonly IPAddress Peer = IPAddress.Parse("198.18.0.82");

    [Fact]
    public async Task StuffedOverTheWire_WithinDestuffedLimit_ReceiveCommandsAreUnavailableOnReader()
    {
        var id = "<size-stuffed-fit@example.com>";
        var stuffed = StuffedWithinDestuffedLimit(id, out var destuffedLength, out var canonicalSize);
        Assert.True(destuffedLength <= Limit);
        Assert.True(stuffed.Length > destuffedLength);
        Assert.True(canonicalSize <= Limit);

        await AssertUnavailableOnEveryReaderPathAsync(id);
    }

    [Fact]
    public async Task DestuffedOverLimit_ReceiveCommandsAreUnavailableOnReader_AndStayFramed()
    {
        var id = "<size-plain-over@example.com>";
        var article = PlainOverLimit(id);
        Assert.True(article.Length > Limit);

        await AssertUnavailableOnEveryReaderPathAsync(id);
    }

    [Fact]
    public async Task StuffedOverDestuffedLimit_ReceiveCommandsAreUnavailableOnReader()
    {
        var id = "<size-stuffed-over@example.com>";
        var stuffed = StuffedOverDestuffedLimit(id, out var destuffedLength);
        Assert.True(destuffedLength > Limit);
        Assert.True(stuffed.Length > destuffedLength);

        await AssertUnavailableOnEveryReaderPathAsync(id);
    }

    [Fact]
    public async Task DestuffedWithinLimit_CanonicalOverLimit_ReceiveCommandsAreUnavailableOnReader()
    {
        var id = "<size-canonical-over@example.com>";
        _ = PlainWithinDestuffedButCanonicalOver(id, out var destuffedLength, out var canonicalSize);
        Assert.True(destuffedLength <= Limit);
        Assert.True(canonicalSize > Limit);

        await AssertUnavailableOnEveryReaderPathAsync(id);
    }

    private static async Task AssertUnavailableOnEveryReaderPathAsync(string id)
    {
        foreach (var afterModeReader in new[] { false, true })
        {
            await using var harness = await CommandHarness.StartAsync();
            var writes = harness.History.Writes.Count;
            await harness.AssertReceiveCommandsUnavailableAsync(id, afterModeReader);

            Assert.Equal(0, harness.Queue.Count);
            Assert.Equal(writes, harness.History.Writes.Count);
            Assert.False(harness.History.ContainsLocal(HistoryDigest.FromMessageId(Encoding.ASCII.GetBytes(id))));
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

        // CanonicalV1 ArtData is stuffed, so canonical size >= stuffed wire size. A wire larger than Limit therefore
        // cannot canonicalize within Limit; the article is sized so the stuffed canonical form fits (4 bytes per
        // ".\r\n" line) while stuffing still adds one byte per line over the destuffed form.
        Assert.True(stuffed.Length > destuffedLength);
        Assert.True(stuffed.Length <= canonicalSize);
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
            maxArticleBytes: CanonicalizationMaxArticleBytes,
            ArticlePathCanonicalizer.OrganizationalTrackerHost);
        Assert.True(created.IsAccepted, created.ParseFailure + " / " + created.MaterializeFailure);
        return created.Record.ArtSize;
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
                transitPeerAuthorization: TransitPeerAuthorization.Disabled,
                historyDb: history);
            var run = session.RunAsync();
            var harness = new CommandHarness(clientToServer, serverToClient, session, run, history, queue);
            Assert.StartsWith("2", await harness.ReadLineAsync(), StringComparison.Ordinal);
            return harness;
        }

        /// <summary>
        /// Sends MODE STREAM, IHAVE and TAKETHIS (optionally after MODE READER). Each is answered 502 by the role gate;
        /// no article body is written because the Reader never reads one, and DATE proves the stream stays framed.
        /// </summary>
        public async Task AssertReceiveCommandsUnavailableAsync(string id, bool afterModeReader)
        {
            if (afterModeReader)
            {
                await WriteCommandAsync("MODE READER\r\n");
                Assert.StartsWith("201 ", await ReadLineAsync(), StringComparison.Ordinal);
            }

            foreach (var command in new[] { "MODE STREAM", $"IHAVE {id}", $"TAKETHIS {id}" })
            {
                await WriteCommandAsync(command + "\r\n");
                Assert.Equal(CommandNotAvailable, await ReadLineAsync());
            }

            await WriteCommandAsync("DATE\r\n");
            Assert.StartsWith("111 ", await ReadLineAsync(), StringComparison.Ordinal);
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
