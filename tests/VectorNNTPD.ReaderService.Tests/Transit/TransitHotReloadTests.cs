using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Transit;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.Transit;

public sealed class TransitHotReloadTests
{
    [Fact]
    public void Reload_AddModifyRemovePeerAndAllowFrom_ReplacesSnapshotAtomically()
    {
        using var manager = new ConfigurationManager();
        manager.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Transit:alpha:PeerName"] = "Alpha",
            ["Transit:alpha:MaxIncomingConnections"] = "4",
            ["Transit:alpha:MaxOutgoingConnections"] = "1",
            ["Transit:alpha:AllowFrom:0"] = "192.0.2.10",
        });

        var services = new ServiceCollection();
        services.AddLogging(static b => b.ClearProviders());
        services.AddOptions<TransitPeersOptions>().Bind(manager.GetSection("Transit"));
        services.AddSingleton<IOptionsChangeTokenSource<TransitPeersOptions>>(
            new ConfigurationChangeTokenSource<TransitPeersOptions>(manager));
        services.AddSingleton<TransitConfigurationStore>();
        services.AddSingleton<TransitConfigurationHotReload>();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<TransitConfigurationStore>();
        _ = provider.GetRequiredService<TransitConfigurationHotReload>();
        var authz = new TransitPeerAuthorization(
            store,
            new EmptyDnsCache(),
            provider.GetRequiredService<TransitConfigurationHotReload>(),
            NullLogger<TransitPeerAuthorization>.Instance);

        Assert.Equal("alpha", authz.Resolve(IPAddress.Parse("192.0.2.10")).TransitPeerName);
        Assert.Equal(4, store.Current.Peers["alpha"].MaxIncomingConnections);

        manager["Transit:alpha:MaxIncomingConnections"] = "2";
        manager["Transit:alpha:AllowFrom:0"] = "198.51.100.10";
        manager["Transit:beta:PeerName"] = "Beta";
        manager["Transit:beta:MaxIncomingConnections"] = "1";
        manager["Transit:beta:MaxOutgoingConnections"] = "0";
        manager["Transit:beta:AllowFrom:0"] = "203.0.113.5";
        ((IConfigurationRoot)manager).Reload();

        Assert.Null(authz.Resolve(IPAddress.Parse("192.0.2.10")).TransitPeerName);
        Assert.Equal("alpha", authz.Resolve(IPAddress.Parse("198.51.100.10")).TransitPeerName);
        Assert.Equal("beta", authz.Resolve(IPAddress.Parse("203.0.113.5")).TransitPeerName);
        Assert.Equal(2, store.Current.Peers["alpha"].MaxIncomingConnections);

        manager["Transit:alpha:Ssl"] = "not-a-mode";
        ((IConfigurationRoot)manager).Reload();
        Assert.Equal(2, store.Current.Peers["alpha"].MaxIncomingConnections);
        Assert.Equal("alpha", authz.Resolve(IPAddress.Parse("198.51.100.10")).TransitPeerName);

        store.Replace(TransitTestPeers.Snapshot("beta", TransitTestPeers.Peer(allowFrom: ["203.0.113.5"])));
        Assert.False(store.Current.Peers.ContainsKey("alpha"));
        Assert.Null(authz.Resolve(IPAddress.Parse("198.51.100.10")).TransitPeerName);
        Assert.Equal("beta", authz.Resolve(IPAddress.Parse("203.0.113.5")).TransitPeerName);
    }

    [Fact]
    public void Reload_AllowFromChange_NewConnectionLosesPeerA_ExistingSessionUnchanged()
    {
        using var manager = new ConfigurationManager();
        manager.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Transit:test-peer:PeerName"] = TransitTestPeers.DefaultPeerDisplayName,
            ["Transit:test-peer:MaxIncomingConnections"] = "10",
            ["Transit:test-peer:MaxOutgoingConnections"] = "0",
            ["Transit:test-peer:AllowFrom:0"] = "192.0.2.10",
        });

        var services = new ServiceCollection();
        services.AddLogging(static b => b.ClearProviders());
        services.AddOptions<TransitPeersOptions>().Bind(manager.GetSection("Transit"));
        services.AddSingleton<IOptionsChangeTokenSource<TransitPeersOptions>>(
            new ConfigurationChangeTokenSource<TransitPeersOptions>(manager));
        services.AddSingleton<TransitConfigurationStore>();
        services.AddSingleton<TransitConfigurationHotReload>();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<TransitConfigurationStore>();
        var hotReload = provider.GetRequiredService<TransitConfigurationHotReload>();
        var peers = new TransitPeerAuthorization(
            store,
            new EmptyDnsCache(),
            hotReload,
            NullLogger<TransitPeerAuthorization>.Instance);

        var source = IPAddress.Parse("192.0.2.10");
        var existing = peers.Resolve(source);
        Assert.Equal(TransitTestPeers.DefaultPeerName, existing.TransitPeerName);

        manager["Transit:test-peer:AllowFrom:0"] = "198.51.100.10";
        ((IConfigurationRoot)manager).Reload();

        Assert.Equal(TransitTestPeers.DefaultPeerName, existing.TransitPeerName);
        Assert.True(existing.AuthorizedTransit);
        var fresh = peers.Resolve(source);
        Assert.Null(fresh.TransitPeerName);
        Assert.False(fresh.AuthorizedTransit);
        Assert.Equal(TransitTestPeers.DefaultPeerName, peers.Resolve(IPAddress.Parse("198.51.100.10")).TransitPeerName);
    }

    [Fact]
    public async Task Reload_AllowFromChange_ExistingSessionKeepsTransit_NewSessionDoesNot()
    {
        using var manager = new ConfigurationManager();
        manager.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Transit:test-peer:PeerName"] = TransitTestPeers.DefaultPeerDisplayName,
            ["Transit:test-peer:MaxIncomingConnections"] = "10",
            ["Transit:test-peer:MaxOutgoingConnections"] = "0",
            ["Transit:test-peer:AllowFrom:0"] = "192.0.2.10",
        });

        var services = new ServiceCollection();
        services.AddLogging(static b => b.ClearProviders());
        services.AddOptions<TransitPeersOptions>().Bind(manager.GetSection("Transit"));
        services.AddSingleton<IOptionsChangeTokenSource<TransitPeersOptions>>(
            new ConfigurationChangeTokenSource<TransitPeersOptions>(manager));
        services.AddSingleton<TransitConfigurationStore>();
        services.AddSingleton<TransitConfigurationHotReload>();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<TransitConfigurationStore>();
        var hotReload = provider.GetRequiredService<TransitConfigurationHotReload>();
        var peers = new TransitPeerAuthorization(
            store,
            new EmptyDnsCache(),
            hotReload,
            NullLogger<TransitPeerAuthorization>.Instance);

        var source = IPAddress.Parse("192.0.2.10");
        await using var existingDuplex = await SessionDuplex.CreateAsync(source);
        var existing = existingDuplex.CreateSession(peers);
        Assert.Equal(TransitTestPeers.DefaultPeerName, existing.Authorization.TransitPeerName);
        var existingRun = existing.RunAsync();
        _ = await existingDuplex.ReadClientLineAsync();

        await existingDuplex.WriteClientLineAsync("IHAVE <keep@ex.com>");
        Assert.Equal("502 Command not available on this server", await existingDuplex.ReadClientLineAsync());

        manager["Transit:test-peer:AllowFrom:0"] = "198.51.100.10";
        ((IConfigurationRoot)manager).Reload();

        Assert.Equal(TransitTestPeers.DefaultPeerName, existing.Authorization.TransitPeerName);
        Assert.True(existing.Authorization.AuthorizedTransit);
        await existingDuplex.WriteClientLineAsync("IHAVE <still@ex.com>");
        Assert.Equal("502 Command not available on this server", await existingDuplex.ReadClientLineAsync());

        await using var freshDuplex = await SessionDuplex.CreateAsync(source);
        var fresh = freshDuplex.CreateSession(peers);
        Assert.Null(fresh.Authorization.TransitPeerName);
        Assert.False(fresh.Authorization.AuthorizedTransit);
        var freshRun = fresh.RunAsync();
        _ = await freshDuplex.ReadClientLineAsync();
        await freshDuplex.WriteClientLineAsync("IHAVE <gone@ex.com>");
        Assert.Equal("502 Command not available on this server", await freshDuplex.ReadClientLineAsync());

        await existingDuplex.WriteClientLineAsync("QUIT");
        _ = await existingDuplex.ReadClientLineAsync();
        await existingRun;
        await freshDuplex.WriteClientLineAsync("QUIT");
        _ = await freshDuplex.ReadClientLineAsync();
        await freshRun;
    }

    [Fact]
    public void Reload_UpdatesPathTokenMaxSizeAndMessageTypesAtomically()
    {
        var store = new TransitConfigurationStore();
        store.Replace(
            TransitTestPeers.Snapshot(
                TransitTestPeers.DefaultPeerName,
                TransitTestPeers.Peer(
                    allowFrom: ["192.0.2.10"],
                    pathToken: "old.token",
                    maxSize: 1024,
                    messageTypes: ["default"])));
        var peers = TransitPeerAuthorization.CreateForStore(store);
        var existing = peers.Resolve(IPAddress.Parse("192.0.2.10"));
        Assert.Equal("old.token", existing.TransitPeerPolicy!.PathToken);
        Assert.Equal(1024, existing.TransitPeerPolicy.MaxSize);
        Assert.Equal(TransitMessageTypes.Default, existing.TransitPeerPolicy.MessageTypes);

        store.Replace(
            TransitTestPeers.Snapshot(
                TransitTestPeers.DefaultPeerName,
                TransitTestPeers.Peer(
                    allowFrom: ["192.0.2.10"],
                    pathToken: "new.token",
                    maxSize: 2048,
                    messageTypes: ["control", "binary"])));

        Assert.Equal("old.token", existing.TransitPeerPolicy.PathToken);
        var fresh = peers.Resolve(IPAddress.Parse("192.0.2.10"));
        Assert.Equal("new.token", fresh.TransitPeerPolicy!.PathToken);
        Assert.Equal(2048, fresh.TransitPeerPolicy.MaxSize);
        Assert.Equal(TransitMessageTypes.Control | TransitMessageTypes.Binary, fresh.TransitPeerPolicy.MessageTypes);
    }

    [Fact]
    public void Reload_UpdatesRemainingPeerFieldsAtomically()
    {
        var store = new TransitConfigurationStore();
        store.Replace(
            TransitTestPeers.Snapshot(
                TransitTestPeers.DefaultPeerName,
                TransitTestPeers.Peer(
                    maxIncoming: 4,
                    maxOutgoing: 2,
                    allowFrom: ["192.0.2.10"],
                    username: "old-user",
                    password: "old-pass",
                    ssl: "TLS",
                    patterns: "comp.*",
                    connectTo: ["news.example.net:119"],
                    deferOnDuplicate: true)));
        var peers = TransitPeerAuthorization.CreateForStore(store);
        var existing = peers.Resolve(IPAddress.Parse("192.0.2.10"));
        Assert.Equal("old-user", existing.TransitPeerPolicy!.Username);
        Assert.Equal("old-pass", existing.TransitPeerPolicy.Password);
        Assert.Equal(TransitSslMode.Tls, existing.TransitPeerPolicy.Ssl);
        Assert.True(existing.TransitPeerPolicy.Patterns.MatchesNewsgroup("comp.lang.c"));
        Assert.True(existing.TransitPeerPolicy.DeferOnDuplicate);
        Assert.Equal(4, existing.TransitPeerPolicy.MaxIncomingConnections);
        Assert.Equal(2, existing.TransitPeerPolicy.MaxOutgoingConnections);
        Assert.Equal("news.example.net", existing.TransitPeerPolicy.ConnectTo.Single().Host);

        store.Replace(
            TransitTestPeers.Snapshot(
                TransitTestPeers.DefaultPeerName,
                TransitTestPeers.Peer(
                    maxIncoming: 8,
                    maxOutgoing: 3,
                    allowFrom: ["192.0.2.10"],
                    username: "new-user",
                    password: "new-pass",
                    ssl: "STARTTLS",
                    patterns: "alt.*",
                    connectTo: ["other.example.net:563"],
                    deferOnDuplicate: false)));

        Assert.Equal("old-user", existing.TransitPeerPolicy.Username);
        Assert.Equal("old-pass", existing.TransitPeerPolicy.Password);
        Assert.True(existing.TransitPeerPolicy.DeferOnDuplicate);
        var fresh = peers.Resolve(IPAddress.Parse("192.0.2.10"));
        Assert.Equal("new-user", fresh.TransitPeerPolicy!.Username);
        Assert.Equal("new-pass", fresh.TransitPeerPolicy.Password);
        Assert.Equal(TransitSslMode.StartTls, fresh.TransitPeerPolicy.Ssl);
        Assert.True(fresh.TransitPeerPolicy.Patterns.MatchesNewsgroup("alt.test"));
        Assert.False(fresh.TransitPeerPolicy.Patterns.MatchesNewsgroup("comp.lang.c"));
        Assert.False(fresh.TransitPeerPolicy.DeferOnDuplicate);
        Assert.Equal(8, fresh.TransitPeerPolicy.MaxIncomingConnections);
        Assert.Equal(3, fresh.TransitPeerPolicy.MaxOutgoingConnections);
        Assert.Equal("other.example.net", fresh.TransitPeerPolicy.ConnectTo.Single().Host);
    }

    [Fact]
    public void Reload_InvalidConfiguration_KeepsLastValidSnapshot()
    {
        var store = new TransitConfigurationStore();
        store.Replace(TransitTestPeers.Snapshot("alpha", TransitTestPeers.Peer(allowFrom: ["192.0.2.10"])));
        var validator = new TransitPeersOptionsValidator();
        var invalid = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(ssl: "not-a-mode"));
        var result = validator.Validate(null, invalid);
        Assert.True(result.Failed);
        Assert.Equal("alpha", store.Current.Peers.Keys.Single());
    }

    private sealed class EmptyDnsCache : ITransitDnsAddressCache
    {
        public IReadOnlySet<IPAddress> GetResolved(string hostname) => new HashSet<IPAddress>();

        public Task RefreshAllAsync(TransitConfigurationSnapshot snapshot, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RefreshDueAsync(TransitConfigurationSnapshot snapshot, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public TimeSpan GetDelayUntilNextRefresh(TransitConfigurationSnapshot snapshot) => Timeout.InfiniteTimeSpan;
    }

    private sealed class SessionDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();
        private readonly IPAddress _clientAddress;

        private SessionDuplex(IPAddress clientAddress) => _clientAddress = clientAddress;

        public static Task<SessionDuplex> CreateAsync(IPAddress clientAddress) =>
            Task.FromResult(new SessionDuplex(clientAddress));

        public NntpSession CreateSession(ITransitPeerAuthorization peers)
        {
            var connection = new ReloadPipeConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                _clientAddress);
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                transitPeerAuthorization: peers);
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

    private sealed class ReloadPipeConnection(PipeReader input, PipeWriter output, IPAddress address) : INntpConnection
    {
        private readonly CancellationTokenSource _closed = new();

        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
        public ConnectionClientIdentity ClientIdentity { get; } = ConnectionClientIdentity.Direct(new IPEndPoint(address, 40000));
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
