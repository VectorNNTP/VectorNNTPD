using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Authentication;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.Session;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Transit;

public sealed class TransitAuthinfoAndCommandTests
{
    private static readonly IPAddress PeerAddress = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress OtherAddress = IPAddress.Parse("198.51.100.1");

    [Fact]
    public async Task TransitPeer_WithCredentials_AcceptsMatchingAuthinfo()
    {
        var peers = CreatePeer(username: "feed", password: "s3cret");
        await using var duplex = await CommandDuplex.CreateAsync(PeerAddress);
        var session = duplex.CreateSession(peers);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("AUTHINFO USER feed");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS s3cret");
        Assert.Equal("281 Authentication accepted", await duplex.ReadClientLineAsync());
        Assert.True(session.Authentication.IsAuthenticated);
        Assert.Equal("feed", session.Authentication.Username);
        Assert.True(session.Authorization.AuthorizedTransit);
        Assert.Equal(TransitTestPeers.DefaultPeerName, session.Authorization.TransitPeerName);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TransitPeer_WithCredentials_RejectsInvalidPassword()
    {
        var peers = CreatePeer(username: "feed", password: "s3cret");
        await using var duplex = await CommandDuplex.CreateAsync(PeerAddress);
        var session = duplex.CreateSession(peers);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("AUTHINFO USER feed");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS wrong");
        Assert.Equal("481 Authentication failed", await duplex.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.True(session.Authorization.StreamingPermitted);
        Assert.Equal(TransitTestPeers.DefaultPeerName, session.Authorization.TransitPeerName);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TransitPeer_AuthinfoDoesNotLogPassword()
    {
        var recording = new RecordingLoggerFactory();
        var peers = CreatePeer(username: "feed", password: "peer-secret-xyz");
        await using var duplex = await CommandDuplex.CreateAsync(PeerAddress);
        var session = duplex.CreateSession(peers, loggerFactory: recording);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("AUTHINFO USER feed");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("AUTHINFO PASS peer-secret-xyz");
        Assert.Equal("281 Authentication accepted", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        var joined = string.Join('\n', recording.Messages);
        Assert.DoesNotContain("peer-secret-xyz", joined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authinfo_IsPublic_ForStreamAndNonTransit_AndModeReaderIsUnavailable()
    {
        var user = NntpCommandTestParse.ParseCommand("AUTHINFO USER x");
        var pass = NntpCommandTestParse.ParseCommand("AUTHINFO PASS y");
        Assert.True(user.IsValid);
        Assert.True(pass.IsValid);
        Assert.Equal(NntpCommandAccess.Public, DefaultNntpCommandCatalog.GetAccess(user.Verb, user.Qualifier));
        Assert.Equal(NntpCommandAccess.Public, DefaultNntpCommandCatalog.GetAccess(pass.Verb, pass.Qualifier));

        await using var readerDuplex = await CommandDuplex.CreateAsync(OtherAddress);
        var reader = readerDuplex.CreateSession(CreatePeer());
        var readerRun = reader.RunAsync();
        _ = await readerDuplex.ReadClientLineAsync();
        await readerDuplex.WriteClientLineAsync("MODE READER");
        Assert.Equal("502 Command not available on this server", await readerDuplex.ReadClientLineAsync());
        Assert.Equal(NntpSessionMode.Unspecified, reader.Mode);
        await readerDuplex.WriteClientLineAsync("AUTHINFO USER reader");
        Assert.StartsWith("381 ", await readerDuplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await readerDuplex.WriteClientLineAsync("QUIT");
        _ = await readerDuplex.ReadClientLineAsync();
        await readerRun;

        var transit = CreatePeer();
        await using var streamDuplex = await CommandDuplex.CreateAsync(PeerAddress);
        var stream = streamDuplex.CreateSession(transit);
        var streamRun = stream.RunAsync();
        _ = await streamDuplex.ReadClientLineAsync();
        await streamDuplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await streamDuplex.ReadClientLineAsync());
        await streamDuplex.WriteClientLineAsync("AUTHINFO USER feed");
        Assert.StartsWith("381 ", await streamDuplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await streamDuplex.WriteClientLineAsync("QUIT");
        _ = await streamDuplex.ReadClientLineAsync();
        await streamRun;

        await using var anonDuplex = await CommandDuplex.CreateAsync(OtherAddress);
        var anon = anonDuplex.CreateSession(CreatePeer());
        Assert.Null(anon.Authorization.TransitPeerName);
        var anonRun = anon.RunAsync();
        _ = await anonDuplex.ReadClientLineAsync();
        await anonDuplex.WriteClientLineAsync("AUTHINFO USER anybody");
        Assert.StartsWith("381 ", await anonDuplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await anonDuplex.WriteClientLineAsync("QUIT");
        _ = await anonDuplex.ReadClientLineAsync();
        await anonRun;
    }

    [Fact]
    public async Task TransitPeer_RejectsWrongUsernameOrIncompleteConfiguredCredentials()
    {
        var both = CreatePeer(username: "feed", password: "s3cret");
        await using var duplex = await CommandDuplex.CreateAsync(PeerAddress);
        var session = duplex.CreateSession(both);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("AUTHINFO USER not-feed");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS s3cret");
        Assert.Equal("481 Authentication failed", await duplex.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Equal(TransitTestPeers.DefaultPeerName, session.Authorization.TransitPeerName);
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;

        await using var userOnlyDuplex = await CommandDuplex.CreateAsync(PeerAddress);
        var userOnly = userOnlyDuplex.CreateSession(CreatePeer(username: "feed", password: ""));
        var userOnlyRun = userOnly.RunAsync();
        _ = await userOnlyDuplex.ReadClientLineAsync();
        await userOnlyDuplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await userOnlyDuplex.ReadClientLineAsync());
        await userOnlyDuplex.WriteClientLineAsync("AUTHINFO USER feed");
        _ = await userOnlyDuplex.ReadClientLineAsync();
        await userOnlyDuplex.WriteClientLineAsync("AUTHINFO PASS anything");
        Assert.Equal("481 Authentication failed", await userOnlyDuplex.ReadClientLineAsync());
        await userOnlyDuplex.WriteClientLineAsync("QUIT");
        _ = await userOnlyDuplex.ReadClientLineAsync();
        await userOnlyRun;

        await using var passOnlyDuplex = await CommandDuplex.CreateAsync(PeerAddress);
        var passOnly = passOnlyDuplex.CreateSession(CreatePeer(username: "", password: "s3cret"));
        var passOnlyRun = passOnly.RunAsync();
        _ = await passOnlyDuplex.ReadClientLineAsync();
        await passOnlyDuplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await passOnlyDuplex.ReadClientLineAsync());
        await passOnlyDuplex.WriteClientLineAsync("AUTHINFO USER feed");
        _ = await passOnlyDuplex.ReadClientLineAsync();
        await passOnlyDuplex.WriteClientLineAsync("AUTHINFO PASS s3cret");
        Assert.Equal("481 Authentication failed", await passOnlyDuplex.ReadClientLineAsync());
        await passOnlyDuplex.WriteClientLineAsync("QUIT");
        _ = await passOnlyDuplex.ReadClientLineAsync();
        await passOnlyRun;
    }

    [Fact]
    public async Task NonTransitSource_PeerUsernamePassword_DoesNotCreateTransitIdentity()
    {
        var peers = CreatePeer(username: "feed", password: "s3cret");
        await using var duplex = await CommandDuplex.CreateAsync(OtherAddress);
        var session = duplex.CreateSession(peers);
        Assert.Null(session.Authorization.TransitPeerName);
        Assert.False(session.Authorization.AuthorizedTransit);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER feed");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS s3cret");
        Assert.Equal("481 Authentication failed", await duplex.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.Null(session.Authorization.TransitPeerName);

        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("CHECK <x@ex.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("IHAVE <x@ex.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("TAKETHIS <x@ex.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task NonTransitAuthinfoSuccess_DoesNotGrantTransit()
    {
        var provider = new ReaderOnlyAuthenticationProvider();
        await using var duplex = await CommandDuplex.CreateAsync(OtherAddress);
        var session = duplex.CreateSession(CreatePeer(username: "feed", password: "s3cret"), authenticationProvider: provider);
        Assert.Null(session.Authorization.TransitPeerName);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("AUTHINFO USER reader");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("281 Authentication accepted", await duplex.ReadClientLineAsync());
        Assert.True(session.Authorization.IsAuthenticated);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.Null(session.Authorization.TransitPeerName);
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TransitPeer_BlankCredentials_DoNotCreatePeerAuth()
    {
        var peers = CreatePeer();
        await using var duplex = await CommandDuplex.CreateAsync(PeerAddress);
        var session = duplex.CreateSession(peers);
        Assert.False(session.Authorization.TransitPeerPolicy!.HasPeerCredentials);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("AUTHINFO USER feed");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS anything");
        Assert.Equal("481 Authentication failed", await duplex.ReadClientLineAsync());
        Assert.True(session.Authorization.AuthorizedTransit);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task NamedTransitPeer_AllowsStreamCheckTakeThisIhave()
    {
        var peers = CreatePeer();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await CommandDuplex.CreateAsync(PeerAddress);
        var session = duplex.CreateSession(peers, queue);
        Assert.Equal(TransitTestPeers.DefaultPeerName, session.Authorization.TransitPeerName);
        Assert.Equal(PeerAddress, session.ClientAddress);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await duplex.ReadClientLineAsync());
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);

        await duplex.WriteClientLineAsync("CHECK <x@ex.com>");
        Assert.Equal("238 <x@ex.com> send article to be transferred", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("IHAVE <x@ex.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientBytesAsync(Encoding.ASCII.GetBytes(CanonicalArticleText.Stuffed("<x@ex.com>") + ".\r\n"));
        Assert.Equal("235 Article transferred OK", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("TAKETHIS <x@ex.com>");
        await duplex.WriteClientBytesAsync(Encoding.ASCII.GetBytes(CanonicalArticleText.Destuffed("<x@ex.com>") + ".\r\n"));
        Assert.Equal("239 <x@ex.com>", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task NonTransitClient_StillRejectedForTransitCommands()
    {
        var peers = CreatePeer();
        await using var duplex = await CommandDuplex.CreateAsync(OtherAddress);
        var session = duplex.CreateSession(peers);
        Assert.Null(session.Authorization.TransitPeerName);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("CHECK <x@ex.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("TAKETHIS <x@ex.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("IHAVE <x@ex.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task AuthenticatedNonTransit_TransitCommandsReturn502()
    {
        var provider = new ReaderOnlyAuthenticationProvider();
        await using var duplex = await CommandDuplex.CreateAsync(OtherAddress);
        var session = duplex.CreateSession(CreatePeer(), authenticationProvider: provider);
        Assert.Null(session.Authorization.TransitPeerName);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER reader");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("281 Authentication accepted", await duplex.ReadClientLineAsync());
        Assert.True(session.Authorization.IsAuthenticated);
        Assert.True(session.Authorization.AuthorizedReader);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.Null(session.Authorization.TransitPeerName);

        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("502 Streaming not permitted", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("CHECK <x@ex.com>");
        Assert.Equal("502 Permission denied", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("TAKETHIS <x@ex.com>");
        Assert.Equal("502 Permission denied", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("IHAVE <x@ex.com>");
        Assert.Equal("502 Permission denied", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public void ChangingNamedPeerPolicy_AffectsNewIdentificationOnly()
    {
        var store = new TransitConfigurationStore();
        store.Replace(
            TransitTestPeers.Snapshot(
                TransitTestPeers.DefaultPeerName,
                TransitTestPeers.Peer(allowFrom: [PeerAddress.ToString()], patterns: "comp.*")));
        var peers = TransitPeerAuthorization.CreateForStore(store);

        var first = peers.Resolve(PeerAddress);
        Assert.Equal(TransitTestPeers.DefaultPeerName, first.TransitPeerName);
        Assert.True(first.TransitPeerPolicy!.Patterns.MatchesNewsgroup("comp.lang.c"));
        Assert.False(first.TransitPeerPolicy.Patterns.MatchesNewsgroup("alt.test"));

        store.Replace(
            TransitTestPeers.Snapshot(
                TransitTestPeers.DefaultPeerName,
                TransitTestPeers.Peer(allowFrom: [PeerAddress.ToString()], patterns: "alt.*")));

        Assert.True(first.TransitPeerPolicy.Patterns.MatchesNewsgroup("comp.lang.c"));
        var second = peers.Resolve(PeerAddress);
        Assert.Equal(TransitTestPeers.DefaultPeerName, second.TransitPeerName);
        Assert.True(second.TransitPeerPolicy!.Patterns.MatchesNewsgroup("alt.test"));
        Assert.False(second.TransitPeerPolicy.Patterns.MatchesNewsgroup("comp.lang.c"));
    }

    private static ITransitPeerAuthorization CreatePeer(string username = "", string password = "") =>
        TransitTestPeers.ForAllowFrom(
            PeerAddress,
            username: username,
            password: password);

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public ConcurrentBag<string> Messages { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentBag<string> messages) : ILogger
        {
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
                messages.Add(formatter(state, exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();

                public void Dispose()
                {
                }
            }
        }
    }

    private sealed class ReaderOnlyAuthenticationProvider : INntpAuthenticationProvider
    {
        public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            if (username == "reader" && password == "secret")
            {
                return ValueTask.FromResult(NntpAuthenticationResult.Success(
                    "reader",
                    new NntpAuthorization(
                        isAuthenticated: true,
                        authorizedReader: true,
                        authorizedTransit: false,
                        postingPermitted: false,
                        streamingPermitted: false)));
            }

            return ValueTask.FromResult(NntpAuthenticationResult.Failed);
        }
    }

    private sealed class CommandDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();
        private readonly IPAddress _clientAddress;

        private CommandDuplex(IPAddress clientAddress) => _clientAddress = clientAddress;

        public static Task<CommandDuplex> CreateAsync(IPAddress clientAddress) =>
            Task.FromResult(new CommandDuplex(clientAddress));

        public NntpSession CreateSession(
            ITransitPeerAuthorization peers,
            IArticleIngestionQueue? queue = null,
            INntpAuthenticationProvider? authenticationProvider = null,
            ILoggerFactory? loggerFactory = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(_clientAddress, 40000)));
            return new NntpSession(
                connection,
                loggerFactory?.CreateLogger<NntpSession>() ?? NullLogger<NntpSession>.Instance,
                authenticationProvider: authenticationProvider,
                loggerFactory: loggerFactory,
                articleIngestion: queue,
                transitPeerAuthorization: peers);
        }

        public async Task WriteClientLineAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task WriteClientBytesAsync(ReadOnlyMemory<byte> bytes)
        {
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
        private readonly CancellationTokenSource _closed = new();

        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
        public ConnectionClientIdentity ClientIdentity { get; } = identity;
        public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public EndPoint? LocalEndPoint => null;
        public bool IsTls => false;
        public bool IsCompressed => false;
        public bool IsCompleted => _closed.IsCancellationRequested;
        public long OutboundIdleVersion => 0;
        public CancellationToken ConnectionClosed => _closed.Token;

        public Task CompleteAsync(Exception? exception = null)
        {
            _closed.Cancel();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _closed.Dispose();
            return ValueTask.CompletedTask;
        }

        public Task WaitForOutboundDeliveryAsync(long outboundIdleVersionBeforeFlush, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipherSuite)
        {
            tlsVersion = string.Empty;
            cipherSuite = string.Empty;
            return false;
        }
    }
}
