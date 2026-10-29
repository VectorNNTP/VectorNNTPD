using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.Transit;

namespace VectorNNTPD.TransitService.Tests.Session;

/// <summary>
/// RFC 4644 CHECK stub authorization — must match TAKETHIS
/// (<see cref="NntpCommandAccess.RequiresTransit"/> / <see cref="NntpAuthorization.AuthorizedTransit"/>).
/// </summary>
public sealed class CheckCommandTests
{
    private static readonly IPAddress TransitPeer = IPAddress.Parse("198.18.0.70");
    private static readonly IPAddress OtherPeer = IPAddress.Parse("198.18.0.71");

    [Fact]
    public void Catalog_CheckAndTakeThis_ShareRequiresTransit()
    {
        var (check, checkLine) = NntpCommandTestParse.Parse("CHECK <msg@example.com>");
        var takeThis = NntpCommandTestParse.ParseCommand("TAKETHIS <msg@example.com>");
        Assert.True(check.IsValid);
        Assert.True(takeThis.IsValid);
        Assert.Equal(NntpVerb.Check, check.Verb);
        Assert.Equal(NntpVerb.TakeThis, takeThis.Verb);
        Assert.Equal("CHECK", DefaultNntpCommandCatalog.DisplayName(check.Verb, check.Qualifier));
        Assert.Equal("TAKETHIS", DefaultNntpCommandCatalog.DisplayName(takeThis.Verb, takeThis.Qualifier));
        Assert.Equal(NntpCommandAccess.RequiresTransit, DefaultNntpCommandCatalog.GetAccess(check.Verb, check.Qualifier));
        Assert.Equal(
            DefaultNntpCommandCatalog.GetAccess(takeThis.Verb, takeThis.Qualifier),
            DefaultNntpCommandCatalog.GetAccess(check.Verb, check.Qualifier));
        Assert.Equal("<msg@example.com>", Encoding.ASCII.GetString(check.ArgumentSpan(checkLine)));
    }

    [Fact]
    public async Task TransitPeer_Check_Returns238_WithoutUserAuthentication()
    {
        var peers = TransitTestPeers.ForAllowFrom(TransitPeer);
        await using var duplex = await CheckDuplex.CreateAsync(TransitPeer);
        var session = duplex.CreateSession(peers);
        AssertNamedTransitPeer(session);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<i.am.an.article.you.will.want@example.com>";
        await duplex.WriteClientLineAsync("CHECK " + id);
        Assert.Equal("238 " + id + " send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.False(session.Authorization.IsAuthenticated);
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TransitPeer_CheckDoesNotRequireModeStream()
    {
        var peers = TransitTestPeers.ForAllowFrom(TransitPeer);
        await using var duplex = await CheckDuplex.CreateAsync(TransitPeer);
        var session = duplex.CreateSession(peers);
        AssertNamedTransitPeer(session);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);
        await duplex.WriteClientLineAsync("CHECK <want@example.com>");
        Assert.Equal(
            "238 <want@example.com> send article to be transferred",
            await duplex.ReadClientLineAsync());
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TransitPeer_CheckThenTakeThis_SameAuthorization()
    {
        var peers = TransitTestPeers.ForAllowFrom(TransitPeer);
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await CheckDuplex.CreateAsync(TransitPeer);
        var session = duplex.CreateSession(peers, queue);
        AssertNamedTransitPeer(session);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <x@ex.com>");
        Assert.Equal("238 <x@ex.com> send article to be transferred", await duplex.ReadClientLineAsync());

        await duplex.WriteClientAsync(
            "TAKETHIS <x@ex.com>\r\n" + CanonicalArticleText.Destuffed("<x@ex.com>") + ".\r\n");
        Assert.Equal("239 <x@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task NonAuthorizedPeer_CheckAndTakeThis_BothReturn480()
    {
        var peers = TransitTestPeers.ForAllowFrom(TransitPeer);
        await using var duplex = await CheckDuplex.CreateAsync(OtherPeer);
        var session = duplex.CreateSession(peers);
        Assert.False(session.Authorization.IsAuthenticated);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.Null(session.Authorization.TransitPeerName);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <msg@example.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("TAKETHIS <msg@example.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task UnauthenticatedDefault_CheckAndTakeThis_BothReturn480()
    {
        await using var duplex = await CheckDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <msg@example.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("TAKETHIS <x@ex.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task AuthenticatedWithoutTransit_CheckAndTakeThis_BothReturn502()
    {
        await using var duplex = await CheckDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: false,
            postingPermitted: false,
            streamingPermitted: false));
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <msg@example.com>");
        Assert.Equal("502 Permission denied", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("TAKETHIS <msg@example.com>");
        Assert.Equal("502 Permission denied", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TransitPeer_MissingMessageId_Returns501AfterAuthorization()
    {
        var peers = TransitTestPeers.ForAllowFrom(TransitPeer);
        await using var duplex = await CheckDuplex.CreateAsync(TransitPeer);
        var session = duplex.CreateSession(peers);
        AssertNamedTransitPeer(session);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK");
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    /// <summary>
    /// Syntax is rejected before authorization gates. Old string parser treated
    /// <c>CHECK</c> as a valid command and returned 480; the byte parser is the
    /// syntactic boundary, so missing message-id is 501.
    /// </summary>
    [Fact]
    public async Task NonAuthorizedPeer_MissingMessageId_Returns501SyntaxError()
    {
        await using var duplex = await CheckDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK");
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    private static void AssertNamedTransitPeer(NntpSession session)
    {
        Assert.False(session.Authorization.IsAuthenticated);
        Assert.True(session.Authorization.AuthorizedTransit);
        Assert.Equal(TransitTestPeers.DefaultPeerName, session.Authorization.TransitPeerName);
        Assert.NotNull(session.Authorization.TransitPeerPolicy);
        Assert.Equal(TransitPeer, session.ClientAddress);
    }

    private sealed class CheckDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());
        private readonly IPAddress _clientAddress;

        private CheckDuplex(IPAddress clientAddress) => _clientAddress = clientAddress;

        public static Task<CheckDuplex> CreateAsync(IPAddress? clientAddress = null) =>
            Task.FromResult(new CheckDuplex(clientAddress ?? IPAddress.Loopback));

        public NntpSession CreateSession(
            ITransitPeerAuthorization? peers = null,
            IArticleIngestionQueue? queue = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(_clientAddress, 40000)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                transitPeerAuthorization: peers);
        }

        public async Task WriteClientLineAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task WriteClientAsync(string payload)
        {
            var bytes = Encoding.ASCII.GetBytes(payload);
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

    private sealed class PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity) : INntpConnection
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
