using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.CommandProcessor;

namespace VectorNNTPD.TransitService.Tests.Session;

/// <summary>
/// The Transit role does not host reader commands. Every reader-only verb (article retrieval, group
/// selection, LIST, OVER/HDR, POST) is answered <c>502 Command not available on this server</c> before
/// authentication, authorization, or syntax-specific handling, and never changes session state.
/// The reader command behavior itself is covered by the ReaderService test project.
/// </summary>
public sealed class TransitReaderCommandIsolationTests
{
    private const string CommandNotAvailable = "502 Command not available on this server";

    [Theory]
    [InlineData("ARTICLE")]
    [InlineData("ARTICLE 1")]
    [InlineData("ARTICLE <a@example.com>")]
    [InlineData("HEAD")]
    [InlineData("HEAD 1")]
    [InlineData("HEAD <a@example.com>")]
    [InlineData("BODY")]
    [InlineData("BODY 1")]
    [InlineData("BODY <a@example.com>")]
    [InlineData("STAT")]
    [InlineData("STAT 1")]
    [InlineData("STAT <a@example.com>")]
    [InlineData("GROUP misc.test")]
    [InlineData("LISTGROUP")]
    [InlineData("LISTGROUP misc.test")]
    [InlineData("LAST")]
    [InlineData("NEXT")]
    [InlineData("OVER 1-2")]
    [InlineData("HDR Subject 1")]
    [InlineData("LIST")]
    [InlineData("LIST ACTIVE")]
    [InlineData("LIST ACTIVE misc.*")]
    [InlineData("LIST COUNTS")]
    [InlineData("LIST NEWSGROUPS")]
    [InlineData("LIST OVERVIEW.FMT")]
    [InlineData("LIST HEADERS")]
    [InlineData("LIST MOTD")]
    [InlineData("POST")]
    public async Task ReaderCommand_Unauthenticated_Returns502(string commandLine)
    {
        await using var duplex = new IsolationDuplex();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, commandLine);
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
    }

    [Theory]
    [InlineData("ARTICLE 1")]
    [InlineData("ARTICLE <a@example.com>")]
    [InlineData("HEAD <a@example.com>")]
    [InlineData("BODY <a@example.com>")]
    [InlineData("STAT <a@example.com>")]
    [InlineData("GROUP misc.test")]
    [InlineData("LISTGROUP misc.test")]
    [InlineData("LAST")]
    [InlineData("NEXT")]
    [InlineData("OVER 1-2")]
    [InlineData("HDR Subject 1")]
    [InlineData("LIST")]
    [InlineData("LIST ACTIVE")]
    [InlineData("LIST COUNTS")]
    [InlineData("LIST NEWSGROUPS")]
    [InlineData("LIST OVERVIEW.FMT")]
    [InlineData("LIST HEADERS")]
    [InlineData("LIST MOTD")]
    [InlineData("POST")]
    public async Task ReaderCommand_FullyAuthorized_StillReturns502(string commandLine)
    {
        await using var duplex = new IsolationDuplex();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: true,
            postingPermitted: true,
            streamingPermitted: true));
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, commandLine);
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task Post_OnTransit_Returns502_AndDoesNotEnterArticleInput()
    {
        await using var duplex = new IsolationDuplex();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: false,
            postingPermitted: true,
            streamingPermitted: false));
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());

        // If POST had opened article input, DATE would be consumed as article text instead of answered.
        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        Assert.StartsWith("205 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await run;
    }

    [Fact]
    public async Task ReaderCommands_OnTransit_DoNotSelectGroupOrChangeSessionMode()
    {
        await using var duplex = new IsolationDuplex();
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("GROUP misc.test");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("LIST");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("MODE READER");
        Assert.Equal(CommandNotAvailable, await duplex.ReadClientLineAsync());
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);

        await duplex.WriteClientLineAsync("QUIT");
        Assert.StartsWith("205 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await run;
    }

    private sealed class IsolationDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public NntpSession CreateSession()
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            return new NntpSession(connection, NullLogger<NntpSession>.Instance);
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

    private sealed class PipeNntpConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();

        public PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
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
