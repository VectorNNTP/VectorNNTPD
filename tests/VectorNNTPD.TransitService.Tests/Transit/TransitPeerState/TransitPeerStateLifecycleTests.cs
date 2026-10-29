using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Transit;

public sealed class TransitPeerStateLifecycleTests
{
    private static readonly IPAddress Source = IPAddress.Parse("192.0.2.10");

    [Fact]
    public async Task Quit_ReleasesAdmittedConnectionOnce()
    {
        await using var live = await StartAdmittedAsync();
        await live.WriteClientLineAsync("QUIT");
        Assert.StartsWith("205 ", await live.ReadClientLineAsync(), StringComparison.Ordinal);
        await live.Run;
        await live.FinalizeLeaseAsync();
        Assert.Equal(NntpSessionLifetime.Finalized, live.Session.Lifetime);
        Assert.Equal(1, live.Tracker.ReleaseCalls);
        Assert.Equal(0, live.Membership.ActiveCount(TransitTestPeers.DefaultPeerName, TransitPeerStateTestFactory.NowMs()));
        await live.FinalizeLeaseAsync();
        Assert.Equal(1, live.Tracker.ReleaseCalls);
    }

    [Fact]
    public async Task RemoteEof_ReleasesAdmittedConnectionOnce()
    {
        await using var live = await StartAdmittedAsync();
        await live.CompleteClientInputAsync();
        await live.Run;
        await live.FinalizeLeaseAsync();
        Assert.Equal(1, live.Tracker.ReleaseCalls);
    }

    [Fact]
    public async Task ConnectionReset_ReleasesAdmittedConnectionOnce()
    {
        await using var live = await StartAdmittedAsync();
        await live.CompleteClientInputAsync(new SocketException((int)SocketError.ConnectionReset));
        await live.Run;
        await live.FinalizeLeaseAsync();
        Assert.Equal(1, live.Tracker.ReleaseCalls);
    }

    [Fact]
    public async Task SocketException_ReleasesAdmittedConnectionOnce()
    {
        await using var live = await StartAdmittedAsync();
        await live.CompleteClientInputAsync(new SocketException((int)SocketError.ConnectionAborted));
        await live.Run;
        await live.FinalizeLeaseAsync();
        Assert.Equal(1, live.Tracker.ReleaseCalls);
    }

    [Fact]
    public async Task IoException_ReleasesAdmittedConnectionOnce()
    {
        await using var live = await StartAdmittedAsync();
        await live.CompleteClientInputAsync(new IOException("peer disappeared"));
        await live.Run;
        await live.FinalizeLeaseAsync();
        Assert.Equal(1, live.Tracker.ReleaseCalls);
    }

    [Fact]
    public async Task IdleTimeout_ReleasesAdmittedConnectionOnce()
    {
        var clock = new ControllableTimeProvider();
        await using var live = await StartAdmittedAsync(clock);
        await live.Session.IdleWatchArmed;
        clock.Advance(TimeSpan.FromSeconds(2));
        await live.Run;
        await live.FinalizeLeaseAsync();
        Assert.Equal(TcpDisconnectReason.IdleTimeout, live.Session.CloseReasonForTests);
        Assert.Equal(1, live.Tracker.ReleaseCalls);
    }

    [Fact]
    public async Task Cancellation_ReleasesAdmittedConnectionOnce()
    {
        await using var live = await StartAdmittedAsync();
        await live.CancelRunAsync();
        await live.Run;
        await live.FinalizeLeaseAsync();
        Assert.Equal(1, live.Tracker.ReleaseCalls);
    }

    [Fact]
    public async Task ListenerShutdown_FinalizesThenReleaseOwner()
    {
        await using var live = await StartAdmittedAsync();
        await live.CancelRunAsync();
        await live.Run;
        await live.FinalizeLeaseAsync();
        Assert.Equal(0, live.Membership.ActiveCount(TransitTestPeers.DefaultPeerName, TransitPeerStateTestFactory.NowMs()));
        await live.Tracker.ReleaseAllOwnershipAsync();
        Assert.False(live.Tracker.IsAccepting);
        Assert.False((await live.Tracker.TryAdmitAsync(TransitTestPeers.DefaultPeerName, 4)).Accepted);
    }

    [Fact]
    public async Task ApplicationShutdown_ServiceStopClearsOwnership()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership);
        Assert.True((await node.TryAdmitAsync(TransitTestPeers.DefaultPeerName, 2)).Accepted);
        var service = new TransitPeerStateService(
            node,
            NullLogger<TransitPeerStateService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, membership.OwnerCount(TransitTestPeers.DefaultPeerName, node.OwnerId, TransitPeerStateTestFactory.NowMs()));
        Assert.False(node.IsAccepting);
    }

    [Fact]
    public async Task DoubleFinalization_ReleasesOnce()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership);
        var admitted = await node.TryAdmitAsync(TransitTestPeers.DefaultPeerName, 2);
        var lease = new TransitInboundConnectionLease(ct => node.ReleaseAsync(TransitTestPeers.DefaultPeerName, admitted.Generation, ct));
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        // codeql[cs/dispose-not-called-on-throw] Intentional multi-dispose contract exercise for TransitInboundConnectionLease.
        lease.Dispose();
        Assert.Equal(1, node.ReleaseCalls);
        Assert.Equal(0, node.GetLocalCount(TransitTestPeers.DefaultPeerName));
    }

    [Fact]
    public async Task NeverAdmitted_DoesNotRelease()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership);
        await TransitInboundConnectionLease.None.DisposeAsync();
        await node.ReleaseAsync(TransitTestPeers.DefaultPeerName, 1);
        Assert.Equal(0, node.ReleaseCalls);
        Assert.Equal(0, membership.ReleaseCalls);
    }

    private static async Task<LiveConnection> StartAdmittedAsync(ControllableTimeProvider? clock = null)
    {
        var store = new TransitConfigurationStore();
        store.Replace(
            TransitTestPeers.Snapshot(
                TransitTestPeers.DefaultPeerName,
                TransitTestPeers.Peer(maxIncoming: 4, allowFrom: [Source.ToString()])));
        var (limiter, tracker, membership) = TransitPeerStateTestFactory.CreateLimiter(store);
        var peers = TransitPeerAuthorization.CreateForStore(store);
        var live = new LiveConnection(limiter, tracker, membership, peers, clock);
        var outcome = await TransitConnectionAdmission.TryAdmitAsync(limiter, live.Session);
        Assert.True(outcome.Admitted);
        live.AttachLease(outcome.Lease);
        live.Start();
        var greeting = await live.ReadClientLineAsync();
        Assert.True(
            greeting.StartsWith("200 ", StringComparison.Ordinal)
            || greeting.StartsWith("201 ", StringComparison.Ordinal),
            greeting);
        return live;
    }

    private sealed class LiveConnection : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();
        private readonly CancellationTokenSource _runCts = new();
        private TransitInboundConnectionLease _lease = TransitInboundConnectionLease.None;

        public LiveConnection(
            ITransitInboundConnectionLimiter limiter,
            DistributedTransitPeerStateTracker tracker,
            InMemoryTransitPeerStateStore membership,
            ITransitPeerAuthorization peers,
            ControllableTimeProvider? clock)
        {
            Limiter = limiter;
            Tracker = tracker;
            Membership = membership;
            Connection = new LifecyclePipeConnection(_clientToServer.Reader, _serverToClient.Writer, Source);
            Session = new NntpSession(
                Connection,
                NullLogger<NntpSession>.Instance,
                transitPeerAuthorization: peers,
                commandIdleTimeout: clock is null ? null : TimeSpan.FromSeconds(1),
                timeProvider: clock);
        }

        public ITransitInboundConnectionLimiter Limiter { get; }

        public DistributedTransitPeerStateTracker Tracker { get; }

        public InMemoryTransitPeerStateStore Membership { get; }

        public NntpSession Session { get; }

        public LifecyclePipeConnection Connection { get; }

        public Task Run { get; private set; } = Task.CompletedTask;

        public void AttachLease(TransitInboundConnectionLease lease) => _lease = lease;

        public void Start() => Run = Session.RunAsync(_runCts.Token);

        public Task CancelRunAsync() => _runCts.CancelAsync();

        public async Task FinalizeLeaseAsync() => await _lease.DisposeAsync();

        public async Task WriteClientLineAsync(string line)
        {
            var payload = System.Text.Encoding.ASCII.GetBytes(line + "\r\n");
            await _clientToServer.Writer.WriteAsync(payload);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token)
                ?? throw new InvalidOperationException("Server closed.");
        }

        public async Task CompleteClientInputAsync(Exception? exception = null)
        {
            await _clientToServer.Writer.CompleteAsync(exception);
        }

        public async ValueTask DisposeAsync()
        {
            await FinalizeLeaseAsync();
            _runCts.Dispose();
            await Connection.DisposeAsync();
        }
    }

    internal sealed class LifecyclePipeConnection(
        PipeReader input,
        PipeWriter output,
        IPAddress address) : INntpConnection
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
            VectorNNTPD.Common.Networking.Certificates.ITlsCertificateContextProvider certificateProvider,
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
