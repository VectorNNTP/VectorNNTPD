using System.Net;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Transit;
using Microsoft.Extensions.Logging.Abstractions;

namespace VectorNNTPD.ReaderService.Tests.Transit;

public sealed class TransitPeerStateTrackerTests
{
    private const string Peer = "peer-a";
    private const string Other = "peer-b";

    [Fact]
    public async Task FirstConnection_BelowAtAndOver_ThenReleaseAndReconnect()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership);
        Assert.True((await node.TryAdmitAsync(Peer, 2)).Accepted);
        Assert.True((await node.TryAdmitAsync(Peer, 2)).Accepted);
        Assert.False((await node.TryAdmitAsync(Peer, 2)).Accepted);
        Assert.Equal(2, node.GetLocalCount(Peer));
        Assert.Equal(2, membership.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));

        await node.ReleaseAsync(Peer, 1);
        Assert.Equal(1, node.GetLocalCount(Peer));
        Assert.True((await node.TryAdmitAsync(Peer, 2)).Accepted);
        await node.ReleaseAsync(Peer, 1);
        await node.ReleaseAsync(Peer, 1);
        Assert.Equal(0, node.GetLocalCount(Peer));
        Assert.Equal(0, membership.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));
    }

    [Fact]
    public async Task MaxZero_IsClosed_NotUnlimited()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership);
        Assert.False((await node.TryAdmitAsync(Peer, 0)).Accepted);
        Assert.Equal(0, node.GetLocalCount(Peer));
        Assert.Equal(0, membership.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(1, node.DistributedRejects);
    }

    [Fact]
    public async Task SamePeerAcrossTwoNodes_SharesClusterLimit()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var a = TransitPeerStateTestFactory.CreateTracker(membership, "nntpd01", "a");
        var b = TransitPeerStateTestFactory.CreateTracker(membership, "nntpd02", "b");
        Assert.True((await a.TryAdmitAsync(Peer, 2)).Accepted);
        Assert.True((await b.TryAdmitAsync(Peer, 2)).Accepted);
        Assert.False((await a.TryAdmitAsync(Peer, 2)).Accepted);
        Assert.False((await b.TryAdmitAsync(Peer, 2)).Accepted);
        Assert.Equal(2, membership.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));
        await a.ReleaseAsync(Peer, 1);
        Assert.True((await b.TryAdmitAsync(Peer, 2)).Accepted);
        Assert.Equal(2, membership.OwnerCount(Peer, b.OwnerId, TransitPeerStateTestFactory.NowMs()));
    }

    [Fact]
    public async Task SamePeerAcrossNNodes_EnforcesExactGlobalLimit()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var nodes = Enumerable.Range(1, 5)
            .Select(i => TransitPeerStateTestFactory.CreateTracker(membership, $"nntpd{i:00}", i.ToString()))
            .ToArray();
        var admitted = 0;
        // codeql[cs/linq/missed-where] Async TryAdmit filter; Where cannot await.
        foreach (var node in nodes)
        {
            if ((await node.TryAdmitAsync(Peer, 3)).Accepted)
            {
                admitted++;
            }
        }

        Assert.Equal(3, admitted);
        Assert.Equal(3, membership.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));
        Assert.False((await nodes[0].TryAdmitAsync(Peer, 3)).Accepted);
    }

    [Fact]
    public async Task DifferentPeers_RemainIsolated()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership);
        Assert.True((await node.TryAdmitAsync(Peer, 1)).Accepted);
        Assert.True((await node.TryAdmitAsync(Other, 1)).Accepted);
        Assert.False((await node.TryAdmitAsync(Peer, 1)).Accepted);
        Assert.Equal(1, membership.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(1, membership.ActiveCount(Other, TransitPeerStateTestFactory.NowMs()));
    }

    [Fact]
    public async Task ConcurrentAdmissionsAcrossNodes_DoNotExceedLimit()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var a = TransitPeerStateTestFactory.CreateTracker(membership, "nntpd01", "a");
        var b = TransitPeerStateTestFactory.CreateTracker(membership, "nntpd02", "b");
        var admitted = 0;
        await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            await Task.Yield();
            var node = i % 2 == 0 ? a : b;
            if ((await node.TryAdmitAsync(Peer, 7)).Accepted)
            {
                Interlocked.Increment(ref admitted);
            }
        }));
        Assert.Equal(7, admitted);
        Assert.Equal(7, membership.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(7, a.GetLocalCount(Peer) + b.GetLocalCount(Peer));
    }

    [Fact]
    public async Task TwoSourceIpsMappedToOneIdentifier_ShareOneLimit()
    {
        var store = new TransitConfigurationStore();
        store.Replace(
            TransitTestPeers.Snapshot(
                Peer,
                TransitTestPeers.Peer(maxIncoming: 1, allowFrom: ["192.0.2.10", "198.51.100.20"])));
        var (limiter, _, membership) = TransitPeerStateTestFactory.CreateLimiter(store);
        var peers = TransitPeerAuthorization.CreateForStore(store);
        var first = CreateSession(peers, IPAddress.Parse("192.0.2.10"));
        var second = CreateSession(peers, IPAddress.Parse("198.51.100.20"));
        Assert.Equal(Peer, first.Authorization.TransitPeerName);
        Assert.Equal(Peer, second.Authorization.TransitPeerName);
        var lease = await TransitConnectionAdmission.TryAdmitAsync(limiter, first);
        Assert.True(lease.Admitted);
        Assert.False((await TransitConnectionAdmission.TryAdmitAsync(limiter, second)).Admitted);
        Assert.Equal(1, membership.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));
        await lease.Lease.DisposeAsync();
    }

    [Fact]
    public async Task Ipv4AndIpv6_RemainDistinctAclIdentities_ButShareIdentifierLimit()
    {
        var store = new TransitConfigurationStore();
        store.Replace(
            TransitTestPeers.Snapshot(
                Peer,
                TransitTestPeers.Peer(maxIncoming: 1, allowFrom: ["192.0.2.10", "2001:db8::10"])));
        var (limiter, _, _) = TransitPeerStateTestFactory.CreateLimiter(store);
        var peers = TransitPeerAuthorization.CreateForStore(store);
        var v4 = CreateSession(peers, IPAddress.Parse("192.0.2.10"));
        var v6 = CreateSession(peers, IPAddress.Parse("2001:db8::10"));
        Assert.Equal(Peer, v4.Authorization.TransitPeerName);
        Assert.Equal(Peer, v6.Authorization.TransitPeerName);
        var lease = await TransitConnectionAdmission.TryAdmitAsync(limiter, v4);
        Assert.True(lease.Admitted);
        Assert.False((await TransitConnectionAdmission.TryAdmitAsync(limiter, v6)).Admitted);
        await lease.Lease.DisposeAsync();
    }

    [Fact]
    public async Task IdleConnection_RemainsCounted_UntilReleased()
    {
        var clock = new VectorNNTPD.ReaderService.Tests.Fixtures.ControllableTimeProvider();
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership, time: clock);
        Assert.True((await node.TryAdmitAsync(Peer, 1)).Accepted);
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(1, membership.ActiveCount(Peer, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.False((await node.TryAdmitAsync(Peer, 1)).Accepted);
        Assert.Equal(1, node.GetLocalCount(Peer));
    }

    [Fact]
    public async Task Renewal_ExtendsLease_IndependentlyOfTraffic()
    {
        var clock = new VectorNNTPD.ReaderService.Tests.Fixtures.ControllableTimeProvider();
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership, time: clock);
        Assert.True((await node.TryAdmitAsync(Peer, 1)).Accepted);
        var first = membership.Expiry(Peer, node.OwnerId);
        clock.Advance(TimeSpan.FromSeconds(10));
        await node.RenewLeasesAsync();
        var renewed = membership.Expiry(Peer, node.OwnerId);
        Assert.True(renewed > first);
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(1, membership.ActiveCount(Peer, clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task RenewalFailure_DoesNotExtendOwnership()
    {
        var clock = new VectorNNTPD.ReaderService.Tests.Fixtures.ControllableTimeProvider();
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership, time: clock);
        Assert.True((await node.TryAdmitAsync(Peer, 1)).Accepted);
        var first = membership.Expiry(Peer, node.OwnerId);
        membership.Unavailable = true;
        clock.Advance(TimeSpan.FromSeconds(10));
        await node.RenewLeasesAsync();
        Assert.Equal(first, membership.Expiry(Peer, node.OwnerId));
        Assert.Equal(1, node.GetLocalCount(Peer));
    }

    [Fact]
    public async Task SameOwnerNewGeneration_ReplacesStaleGeneration()
    {
        var membership = new InMemoryTransitPeerStateStore();
        membership.Engine.WriteOwnership(InMemoryTransitPeerStateStore.ConnectionKey(Peer), "nntpd01:a", 40_000, 9, 7);
        var node = TransitPeerStateTestFactory.CreateTracker(membership, incarnation: "a");
        Assert.True((await node.TryAdmitAsync(Peer, 20)).Accepted);
        Assert.True(membership.Engine.TryGetOwnership(
            InMemoryTransitPeerStateStore.ConnectionKey(Peer),
            node.OwnerId,
            out _,
            out var generation,
            out var count));
        Assert.Equal(1, generation);
        Assert.Equal(1, count);
        await node.ReleaseAsync(Peer, 1);
        Assert.Equal(0, membership.OwnerCount(Peer, node.OwnerId, TransitPeerStateTestFactory.NowMs()));
    }

    [Fact]
    public async Task StaleReleaseAndRenew_DoNotAffectNewerGeneration()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership);
        Assert.True((await node.TryAdmitAsync(Peer, 4)).Accepted);
        await membership.ReleaseAsync(Peer, node.OwnerId, 99);
        Assert.Equal(1, membership.OwnerCount(Peer, node.OwnerId, TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(
            TransitPeerStateRenewStatus.Lost,
            await membership.RenewAsync(Peer, node.OwnerId, 99, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30)));
        Assert.Equal(1, membership.OwnerCount(Peer, node.OwnerId, TransitPeerStateTestFactory.NowMs()));
    }

    [Fact]
    public async Task UnknownPeer_DoesNotCreateState()
    {
        var store = new TransitConfigurationStore();
        store.Replace(TransitTestPeers.Snapshot(Peer, TransitTestPeers.Peer(maxIncoming: 1, allowFrom: ["192.0.2.10"])));
        var (limiter, _, membership) = TransitPeerStateTestFactory.CreateLimiter(store);
        Assert.False((await limiter.TryAcquireAsync("missing")).Admitted);
        Assert.Equal(0, membership.ActiveCount("missing", TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(0, membership.AdmitCalls);
    }

    private static NntpSession CreateSession(ITransitPeerAuthorization peers, IPAddress source)
    {
        var input = new System.IO.Pipelines.Pipe();
        var output = new System.IO.Pipelines.Pipe();
        return new NntpSession(
            new IdentityPipeConnection(input.Reader, output.Writer, source),
            NullLogger<NntpSession>.Instance,
            transitPeerAuthorization: peers);
    }

    private sealed class IdentityPipeConnection(
        System.IO.Pipelines.PipeReader input,
        System.IO.Pipelines.PipeWriter output,
        IPAddress address) : INntpConnection
    {
        private readonly CancellationTokenSource _closed = new();

        public System.IO.Pipelines.PipeReader Input { get; } = input;
        public System.IO.Pipelines.PipeWriter Output { get; } = output;
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
