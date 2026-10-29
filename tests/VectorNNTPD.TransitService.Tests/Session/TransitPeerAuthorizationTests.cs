using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.Networking.Transport;
using VectorNNTPD.TransitService.Tests.Transit;

namespace VectorNNTPD.TransitService.Tests.Session;

/// <summary>Connection-time transit/streaming peer ACL (top-level named <c>Transit</c> peers).</summary>
[Collection(nameof(TransportTestHostCollection))]
public sealed class TransitPeerAuthorizationTests
{
    private static readonly IPAddress AllowedPeer = IPAddress.Parse("198.18.0.70");
    private static readonly IPAddress OtherPeer = IPAddress.Parse("198.18.0.71");

    [Fact]
    public async Task DefaultAcl_ModeStream_Returns480()
    {
        await using var duplex = await PeerDuplex.CreateAsync(AllowedPeer);
        var session = duplex.CreateSession(TransitPeerAuthorization.Disabled);
        AssertPeerUnauthorized(session);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task AllowedPeer_ReceivesTransitStreamingPrivileges_NotAuthenticated()
    {
        var peers = TransitTestPeers.ForAllowFrom(AllowedPeer);
        await using var duplex = await PeerDuplex.CreateAsync(AllowedPeer);
        var session = duplex.CreateSession(peers);

        AssertNamedTransitPeer(session);
        Assert.False(session.Authentication.IsAuthenticated);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task AllowedPeer_ModeStream_Returns203_WithoutChangingMode()
    {
        var peers = TransitTestPeers.ForAllowFrom(AllowedPeer);
        await using var duplex = await PeerDuplex.CreateAsync(AllowedPeer);
        var session = duplex.CreateSession(peers);
        AssertNamedTransitPeer(session);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await duplex.ReadClientLineAsync());
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);
        Assert.False(session.Authorization.IsAuthenticated);
        Assert.Equal(TransitTestPeers.DefaultPeerName, session.Authorization.TransitPeerName);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task AllowedPeer_CheckTakeThisIhave_NotBlockedByAuthentication()
    {
        var peers = TransitTestPeers.ForAllowFrom(AllowedPeer);
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await PeerDuplex.CreateAsync(AllowedPeer);
        var session = duplex.CreateSession(peers, queue);
        AssertNamedTransitPeer(session);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <x@ex.com>");
        Assert.Equal("238 <x@ex.com> send article to be transferred", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("IHAVE <x@ex.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientBytesAsync(Encoding.ASCII.GetBytes(CanonicalArticleText.Stuffed("<x@ex.com>") + ".\r\n"));
        Assert.Equal("235 Article transferred OK", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("TAKETHIS <x@ex.com>");
        await duplex.WriteClientBytesAsync(Encoding.ASCII.GetBytes(CanonicalArticleText.Destuffed("<x@ex.com>") + ".\r\n"));
        Assert.Equal("239 <x@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(2, queue.Count);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task AllowedPeer_PostAndReaderCommands_AreUnavailableOnTransit()
    {
        var peers = TransitTestPeers.ForAllowFrom(AllowedPeer);
        await using var duplex = await PeerDuplex.CreateAsync(AllowedPeer);
        var session = duplex.CreateSession(peers);
        AssertNamedTransitPeer(session);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("502 Command not available on this server", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("LIST");
        Assert.Equal("502 Command not available on this server", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("GROUP alt.test");
        Assert.Equal("502 Command not available on this server", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("ARTICLE");
        Assert.Equal("502 Command not available on this server", await duplex.ReadClientLineAsync());

        Assert.False(session.Authorization.IsAuthenticated);
        Assert.False(session.Authorization.AuthorizedReader);
        Assert.False(session.Authorization.PostingPermitted);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task DifferentSourceAddress_DoesNotReceiveTransitPrivileges()
    {
        var peers = TransitTestPeers.ForAllowFrom(AllowedPeer);
        await using var duplex = await PeerDuplex.CreateAsync(OtherPeer);
        var session = duplex.CreateSession(peers);
        AssertPeerUnauthorized(session);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("TAKETHIS <x@ex.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task AllowedPeer_FailedAuthinfo_RestoresPeerPrivileges_NotAuthenticated()
    {
        var peers = TransitTestPeers.ForAllowFrom(AllowedPeer);
        await using var duplex = await PeerDuplex.CreateAsync(AllowedPeer);
        var session = duplex.CreateSession(peers);
        AssertNamedTransitPeer(session);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER nobody");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS wrong");
        Assert.Equal("481 Authentication failed", await duplex.ReadClientLineAsync());

        Assert.False(session.Authorization.IsAuthenticated);
        Assert.True(session.Authorization.AuthorizedTransit);
        Assert.True(session.Authorization.StreamingPermitted);
        Assert.Equal(TransitTestPeers.DefaultPeerName, session.Authorization.TransitPeerName);

        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public void Resolve_MatchesIpv6AndMappedIpv4()
    {
        var v6 = IPAddress.Parse("2001:db8::1");
        var peers = TransitTestPeers.ForAllowFrom([v6, AllowedPeer]);
        AssertTransitPeer(peers.Resolve(v6), TransitTestPeers.DefaultPeerName);
        AssertTransitPeer(peers.Resolve(AllowedPeer), TransitTestPeers.DefaultPeerName);
        AssertTransitPeer(peers.Resolve(IPAddress.Parse("::ffff:198.18.0.70")), TransitTestPeers.DefaultPeerName);
        Assert.Equal(NntpAuthorization.Unauthenticated, peers.Resolve(OtherPeer));
    }

    [Fact]
    public async Task TcpProxyEffectiveClient_AllowedPeer_GrantsTransit()
    {
        var trusted = new TrustedProxyHosts([IPAddress.Loopback]);
        var peers = TransitTestPeers.ForAllowFrom(AllowedPeer);
        await using var host = await TransportTestHost.StartPlainWithProxyAsync(trusted);

        using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(host.EndPoint);
        await clientSocket.SendAsync(Encoding.ASCII.GetBytes(
            "PROXY TCP4 198.18.0.70 127.0.0.1 40000 119\r\n"));

        await using var server = await host.AcceptAsync();
        Assert.Equal(AllowedPeer, server.ClientIdentity.ClientAddress);

        var session = new NntpSession(
            server,
            NullLogger<NntpSession>.Instance,
            transitPeerAuthorization: peers);
        AssertNamedTransitPeer(session);

        var sessionTask = session.RunAsync();
        var greeting = await ReadPlainLineAsync(clientSocket);
        Assert.StartsWith("20", greeting, StringComparison.Ordinal);

        await clientSocket.SendAsync("MODE STREAM\r\n"u8.ToArray());
        Assert.Equal("203 Streaming permitted", await ReadPlainLineAsync(clientSocket));

        await clientSocket.SendAsync("QUIT\r\n"u8.ToArray());
        Assert.StartsWith("205 ", await ReadPlainLineAsync(clientSocket), StringComparison.Ordinal);
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void AssertNamedTransitPeer(NntpSession session)
    {
        AssertTransitPeer(session.Authorization, TransitTestPeers.DefaultPeerName);
        Assert.Equal(AllowedPeer, session.ClientAddress);
    }

    private static void AssertTransitPeer(NntpAuthorization authorization, string peerName)
    {
        Assert.False(authorization.IsAuthenticated);
        Assert.True(authorization.AuthorizedTransit);
        Assert.True(authorization.StreamingPermitted);
        Assert.False(authorization.AuthorizedReader);
        Assert.False(authorization.PostingPermitted);
        Assert.Equal(peerName, authorization.TransitPeerName);
        Assert.NotNull(authorization.TransitPeerPolicy);
    }

    private static void AssertPeerUnauthorized(NntpSession session)
    {
        Assert.False(session.Authorization.IsAuthenticated);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.False(session.Authorization.StreamingPermitted);
        Assert.False(session.Authorization.AuthorizedReader);
        Assert.False(session.Authorization.PostingPermitted);
        Assert.Null(session.Authorization.TransitPeerName);
    }

    private static async Task<string> ReadPlainLineAsync(Socket socket)
    {
        var buffer = new byte[512];
        var total = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (total < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(total), cts.Token);
            Assert.True(n > 0);
            total += n;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var idx = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (idx >= 0)
            {
                return text[..idx];
            }
        }

        throw new InvalidOperationException("Line too long.");
    }

    private sealed class PeerDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();
        private readonly IPAddress _clientAddress;

        private PeerDuplex(IPAddress clientAddress) => _clientAddress = clientAddress;

        public static Task<PeerDuplex> CreateAsync(IPAddress clientAddress) =>
            Task.FromResult(new PeerDuplex(clientAddress));

        public NntpSession CreateSession(
            ITransitPeerAuthorization peers,
            IArticleIngestionQueue? queue = null)
        {
            var identity = ConnectionClientIdentity.Direct(new IPEndPoint(_clientAddress, 40000));
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                identity);
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
        private int _compressed;

        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
        public ConnectionClientIdentity ClientIdentity { get; } = identity;
        public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public EndPoint? LocalEndPoint => null;
        public bool IsTls => false;
        public bool IsCompressed => Volatile.Read(ref _compressed) == 1;
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
            VectorNNTPD.Common.Networking.Certificates.ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _compressed, 1);
            return Task.CompletedTask;
        }

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipherSuite)
        {
            tlsVersion = string.Empty;
            cipherSuite = string.Empty;
            return false;
        }
    }
}
