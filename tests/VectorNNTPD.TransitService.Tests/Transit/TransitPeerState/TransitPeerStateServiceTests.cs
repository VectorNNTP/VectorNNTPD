using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Transit;

public sealed class TransitPeerStateServiceTests
{
    [Fact]
    public void Namespace_IsTransit()
    {
        Assert.Equal("VectorNNTPD.TransitService.Transit", typeof(TransitPeerStateService).Namespace);
        Assert.Equal("VectorNNTPD.TransitService.Transit", typeof(DistributedTransitPeerStateTracker).Namespace);
        Assert.NotEqual(typeof(VectorNNTPD.TransitService.SessionState.SessionStateService), typeof(TransitPeerStateService));
    }

    [Fact]
    public async Task RenewalPass_ExtendsIdleOwnershipWithoutTraffic()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership, time: clock);
        Assert.True((await node.TryAdmitAsync("peer-a", 1)).Accepted);
        var first = membership.Expiry("peer-a", node.OwnerId);
        var service = new TransitPeerStateService(
            node,
            NullLogger<TransitPeerStateService>.Instance,
            clock,
            TimeSpan.FromSeconds(10));
        await service.StartAsync(CancellationToken.None);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!clock.HasScheduledTimers)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        using var renewWait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (membership.RenewCalls == 0)
        {
            renewWait.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        Assert.True(membership.Expiry("peer-a", node.OwnerId) > first);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, membership.OwnerCount("peer-a", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.False(node.IsAccepting);
    }

    [Fact]
    public async Task Stop_StopsRenewalThenReleaseOwner()
    {
        var membership = new InMemoryTransitPeerStateStore();
        var node = TransitPeerStateTestFactory.CreateTracker(membership);
        Assert.True((await node.TryAdmitAsync("peer-a", 2)).Accepted);
        var service = new TransitPeerStateService(
            node,
            NullLogger<TransitPeerStateService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, membership.OwnerCount("peer-a", node.OwnerId, TransitPeerStateTestFactory.NowMs()));
        Assert.False(node.IsAccepting);
    }
}
