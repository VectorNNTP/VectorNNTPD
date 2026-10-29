using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.TransitService.Tests.Session;

public sealed class NntpSessionFoundationTests
{
    [Fact]
    public void Authorization_DefaultsDenyTransitAndPosting()
    {
        var authz = NntpAuthorization.Unauthenticated;
        Assert.False(authz.IsAuthenticated);
        Assert.False(authz.AuthorizedReader);
        Assert.False(authz.AuthorizedTransit);
        Assert.False(authz.PostingPermitted);
        Assert.False(authz.StreamingPermitted);
    }

    [Fact]
    public void CommandParser_SplitsVerbAndArguments()
    {
        var caps = NntpCommandTestParse.ParseCommand("CAPABILITIES");
        Assert.Equal(NntpVerb.Capabilities, caps.Verb);
        Assert.Equal(0, caps.TokenCount);
        Assert.True(caps.IsValid);

        var (auth, authLine) = NntpCommandTestParse.Parse("AUTHINFO USER fred");
        Assert.Equal(NntpVerb.AuthInfo, auth.Verb);
        Assert.Equal(NntpVerb.User, auth.Qualifier);
        Assert.Equal("fred", Encoding.ASCII.GetString(auth.ArgumentSpan(authLine)));

        var empty = NntpCommandParser.Parse("   "u8);
        Assert.False(empty.IsValid);
        Assert.Equal(NntpParseStatus.Empty, empty.Status);
    }

    [Fact]
    public async Task Dispatcher_UnknownCommand_Returns500()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "NOSUCHCMD");
        Assert.Contains("500 Unknown command", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatcher_UnknownAuthinfoVariant_Returns501()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "AUTHINFO GENERIC x");
        Assert.Contains("501 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatcher_ReaderCommandOnTransit_Returns502BeforeAuthentication()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "ARTICLE");
        Assert.Contains("502 Command not available on this server", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatcher_TransitCommandWithoutAuth_Returns480()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "CHECK <msg@example.com>");
        Assert.Contains("480 Authentication required", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatcher_TransitWhenAuthenticatedButNotAuthorized_Returns502()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: false,
            postingPermitted: false,
            streamingPermitted: false));
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "IHAVE <msg@example.com>");
        Assert.Contains("502 Permission denied", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatcher_ModeStreamWhenAuthenticatedWithoutStreaming_Returns502()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: true,
            postingPermitted: false,
            streamingPermitted: false));
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "MODE STREAM");
        Assert.Contains("502 Streaming not permitted", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_GreetingAndQuit_PublicCommands()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var sessionTask = duplex.CreateSession().RunAsync();

        Assert.StartsWith("201 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("DATE");
        var date = await duplex.ReadClientLineAsync();
        Assert.StartsWith("111 ", date, StringComparison.Ordinal);
        Assert.Equal(18, date.Length); // "111 " + 14 digit stamp

        await duplex.WriteClientLineAsync("MODE READER");
        Assert.Equal("502 Command not available on this server", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("CAPABILITIES");
        Assert.StartsWith("101 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var caps = new List<string>();
        while (true)
        {
            var line = await duplex.ReadClientLineAsync();
            if (line == ".")
            {
                break;
            }

            caps.Add(line);
        }

        Assert.Contains("VERSION 2", caps);
        Assert.Contains("AUTHINFO USER", caps);
        Assert.Contains("COMPRESS DEFLATE", caps);
        Assert.Contains("IHAVE", caps);
        Assert.Contains("STREAMING", caps);
        Assert.Contains("SPEEDTEST", caps);
        Assert.DoesNotContain("READER", caps);
        Assert.DoesNotContain("MODE-READER", caps);
        Assert.DoesNotContain("POST", caps);
        Assert.DoesNotContain(caps, static c => c.StartsWith("LIST", StringComparison.Ordinal));

        await duplex.WriteClientLineAsync("QUIT");
        Assert.StartsWith("205 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await sessionTask;
    }

    [Fact]
    public async Task ModeReader_OnTransit_Returns502_EvenWhenPostingPermitted()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(NntpAuthorization.Unauthenticated.With(postingPermitted: true));
        var sessionTask = session.RunAsync();

        Assert.StartsWith("200 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("MODE READER");
        Assert.Equal("502 Command not available on this server", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("QUIT");
        Assert.StartsWith("205 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await sessionTask;
    }

    private sealed class SessionTestDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        private SessionTestDuplex()
        {
        }

        public PipeWriter ServerOutput => _serverToClient.Writer;
        public PipeReader ServerInput => _clientToServer.Reader;

        public static Task<SessionTestDuplex> CreateAsync() => Task.FromResult(new SessionTestDuplex());

        public NntpSession CreateSession()
        {
            var connection = new PipeNntpConnection(
                ServerInput,
                ServerOutput,
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

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public bool IsCompressed => Volatile.Read(ref _compressed) == 1;
        public CancellationToken ConnectionClosed => _cts.Token;
        public bool IsCompleted => _cts.IsCancellationRequested;
        public long OutboundIdleVersion => 0;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _compressed, 1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }

        private int _compressed;
    }
}
