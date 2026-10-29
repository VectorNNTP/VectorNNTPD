using VectorNNTPD.ReaderService.Redis;
using VectorNNTPD.ReaderService.Transit;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Transit;

public sealed class TransitPeerStateFailureTests
{
    private const string Peer = "peer-a";
    private const string Other = "peer-b";

    [Fact]
    public async Task RedisUnavailableDuringAdmission_FailsClosedWithoutLocalSlot()
    {
        var membership = new InMemoryTransitPeerStateStore { Unavailable = true };
        var node = TransitPeerStateTestFactory.CreateTracker(membership);
        var result = await node.TryAdmitAsync(Peer, 4);
        Assert.Equal(TransitPeerStateAdmitStatus.Unavailable, result.Status);
        Assert.Equal(0, node.GetLocalCount(Peer));
        Assert.Equal(1, node.UnavailableRejects);
        Assert.Equal(0, membership.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));
    }

    [Fact]
    public async Task RedisEvalFailureDuringAdmission_FailsClosed()
    {
        var redis = new FakeRedisService();
        redis.Database.ScriptException = new RedisUnavailableException("eval failed");
        var store = new RedisTransitPeerStateStore(redis);
        var node = TransitPeerStateTestFactory.CreateTracker(store);
        var result = await node.TryAdmitAsync(Peer, 4);
        Assert.Equal(TransitPeerStateAdmitStatus.Unavailable, result.Status);
        Assert.Equal(0, node.GetLocalCount(Peer));
    }

    [Fact]
    public async Task OceAfterSuccessfulEval_CompensatesAndRethrows()
    {
        var membership = new InMemoryTransitPeerStateStore
        {
            ThrowAfterAdmit = new OperationCanceledException(),
        };
        var node = TransitPeerStateTestFactory.CreateTracker(membership);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.TryAdmitAsync(Peer, 4).AsTask());
        Assert.Equal(0, node.GetLocalCount(Peer));
        Assert.Equal(0, node.DistributedAdmits);
        Assert.Equal(0, membership.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(1, membership.ReleaseCalls);
    }

    [Fact]
    public async Task RedisUnavailableDuringRelease_FinalizesLocally()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership);
        var admitted = await node.TryAdmitAsync(Peer, 4);
        Assert.True(admitted.Accepted);
        membership.Unavailable = true;
        await node.ReleaseAsync(Peer, admitted.Generation);
        Assert.Equal(0, node.GetLocalCount(Peer));
        Assert.Equal(1, node.ReleaseCalls);
        Assert.Equal(1, membership.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));
    }

    [Fact]
    public async Task RedisUnavailableDuringRenewal_DoesNotExtend()
    {
        var clock = new VectorNNTPD.ReaderService.Tests.Fixtures.ControllableTimeProvider();
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership, time: clock);
        Assert.True((await node.TryAdmitAsync(Peer, 1)).Accepted);
        var expiry = membership.Expiry(Peer, node.OwnerId);
        membership.Unavailable = true;
        clock.Advance(TimeSpan.FromSeconds(10));
        await node.RenewLeasesAsync();
        Assert.Equal(expiry, membership.Expiry(Peer, node.OwnerId));
        Assert.Equal(1, node.GetLocalCount(Peer));
    }

    [Fact]
    public async Task ShutdownCleanup_ReleaseOwnerClearsRemainingOwnership()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership);
        Assert.True((await node.TryAdmitAsync(Peer, 4)).Accepted);
        Assert.True((await node.TryAdmitAsync(Peer, 4)).Accepted);
        await node.ReleaseAllOwnershipAsync();
        Assert.False(node.IsAccepting);
        Assert.Equal(0, membership.OwnerCount(Peer, node.OwnerId, TransitPeerStateTestFactory.NowMs()));
        Assert.False((await node.TryAdmitAsync(Peer, 4)).Accepted);
    }

    [Fact]
    public async Task ReleaseOwner_IsIsolatedToThisOwnerAndPeer()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var a = TransitPeerStateTestFactory.CreateTracker(membership, "nntpd01", "a");
        var b = TransitPeerStateTestFactory.CreateTracker(membership, "nntpd02", "b");
        Assert.True((await a.TryAdmitAsync(Peer, 4)).Accepted);
        Assert.True((await b.TryAdmitAsync(Peer, 4)).Accepted);
        Assert.True((await a.TryAdmitAsync(Other, 4)).Accepted);
        await a.ReleaseAllOwnershipAsync();
        Assert.Equal(0, membership.OwnerCount(Peer, a.OwnerId, TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(0, membership.OwnerCount(Other, a.OwnerId, TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(1, membership.OwnerCount(Peer, b.OwnerId, TransitPeerStateTestFactory.NowMs()));
        Assert.True((await b.TryAdmitAsync(Other, 4)).Accepted);
    }

    [Fact]
    public async Task NeverAdmitted_NeverIssuesRelease()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership);
        await node.ReleaseAsync(Peer, 1);
        Assert.Equal(0, node.ReleaseCalls);
        Assert.Equal(0, membership.ReleaseCalls);
    }
}
