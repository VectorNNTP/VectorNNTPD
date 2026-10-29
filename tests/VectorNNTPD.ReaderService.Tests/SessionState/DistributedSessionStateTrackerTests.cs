using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.SessionState;

public sealed class DistributedSessionStateTrackerTests
{
    private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress V4B = IPAddress.Parse("198.51.100.20");
    private static readonly IPAddress V6A = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress V6B = IPAddress.Parse("2001:db8::11");

    [Fact]
    public async Task SameIpv4Repeated_IsOneDistinctSource()
    {
        var (node, _) = CreateNode("nntpd01");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s3", V4A, srcIpLimit: 1));
        Assert.Equal(3, node.GetLocalIpCount("alice", "192.0.2.10"));
        Assert.Equal(3, node.GetLocalSessionCount("alice"));
        Assert.Equal(1, node.DistributedAdmits);
        Assert.Equal(2, node.LocalHotPathAdmits);
    }

    [Fact]
    public async Task SameIpv6Repeated_IsOneDistinctSource()
    {
        var (node, _) = CreateNode("nntpd01");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V6A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V6A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(node, "s3", V6B, srcIpLimit: 1));
    }

    [Fact]
    public async Task CompressedAndExpandedIpv6_AreOneSource()
    {
        var (node, _) = CreateNode("nntpd01");
        var expanded = IPAddress.Parse("2001:0db8:0000:0000:0000:0000:0000:0010");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V6A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", expanded, srcIpLimit: 1));
        Assert.Equal(2, node.GetLocalIpCount("alice", "2001:db8::10"));
    }

    [Fact]
    public async Task Ipv4MappedIpv6_FollowsExistingNormalization()
    {
        var (node, _) = CreateNode("nntpd01");
        var mapped = IPAddress.Parse("::ffff:192.0.2.10");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", mapped, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(node, "s3", V4B, srcIpLimit: 1));
        Assert.Equal(2, node.GetLocalIpCount("alice", "192.0.2.10"));
    }

    [Fact]
    public async Task Ipv4ThenIpv6_WithLimitOne_IsRejected()
    {
        var (node, _) = CreateNode("nntpd01");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(node, "s2", V6A, srcIpLimit: 1));
        Assert.Equal(0, node.GetLocalSessionCount("bob"));
        Assert.Equal(1, node.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task Ipv6ThenIpv4_WithLimitOne_IsRejected()
    {
        var (node, _) = CreateNode("nntpd01");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V6A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(node, "s2", V4A, srcIpLimit: 1));
    }

    [Fact]
    public async Task MixedIpv4AndIpv6_EachConsumeOneSlot()
    {
        var (node, _) = CreateNode("nntpd01");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V6A, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(node, "s3", V4B, srcIpLimit: 2));
    }

    [Fact]
    public async Task SameSourceAcrossNodes_IsOneDistinctSource()
    {
        var membership = new InMemorySessionStateStore();
        var node1 = CreateNode("nntpd01", membership);
        var node2 = CreateNode("nntpd02", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node1, "s1", V4A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node2, "s2", V4A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(node1, "s3", V4B, srcIpLimit: 1));
    }

    [Fact]
    public async Task DifferentSourcesAcrossNodes_ObeyLimit()
    {
        var membership = new InMemorySessionStateStore();
        var node1 = CreateNode("nntpd01", membership);
        var node2 = CreateNode("nntpd02", membership);
        var node3 = CreateNode("nntpd03", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node1, "s1", V4A, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node2, "s2", V6A, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(node3, "s3", V4B, srcIpLimit: 2));
    }

    [Fact]
    public async Task ConcurrentDistributedAdmit_CannotExceedLimit()
    {
        var membership = new InMemorySessionStateStore();
        var node1 = CreateNode("nntpd01", membership);
        var node2 = CreateNode("nntpd02", membership);
        var results = new SessionAdmissionResult[2];
        Parallel.Invoke(
            () => results[0] = Admit(node1, "s1", V4A, srcIpLimit: 1).AsTask().GetAwaiter().GetResult(),
            () => results[1] = Admit(node2, "s2", V6A, srcIpLimit: 1).AsTask().GetAwaiter().GetResult());

        Assert.Equal(1, results.Count(static r => r == SessionAdmissionResult.Success));
        Assert.Equal(1, results.Count(static r => r == SessionAdmissionResult.SourceAddressLimitExceeded));
    }

    [Fact]
    public async Task ConcurrentSameSourceDifferentNodes_BothSucceed()
    {
        var membership = new InMemorySessionStateStore();
        var node1 = CreateNode("nntpd01", membership);
        var node2 = CreateNode("nntpd02", membership);
        var results = new SessionAdmissionResult[2];
        Parallel.Invoke(
            () => results[0] = Admit(node1, "s1", V6A, srcIpLimit: 1).AsTask().GetAwaiter().GetResult(),
            () => results[1] = Admit(node2, "s2", V6A, srcIpLimit: 1).AsTask().GetAwaiter().GetResult());

        Assert.All(results, static r => Assert.Equal(SessionAdmissionResult.Success, r));
    }

    [Fact]
    public async Task LocalReferenceCounts_ReleaseOnlyOnFinalSession()
    {
        var (node, membership) = CreateNode("nntpd01");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "a", V4A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "b", V4A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "c", V4A, srcIpLimit: 1));
        await node.ReleaseAsync("alice", "a");
        Assert.Equal(2, node.GetLocalIpCount("alice", "192.0.2.10"));
        Assert.True(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));
        await node.ReleaseAsync("alice", "b");
        Assert.Equal(1, node.GetLocalIpCount("alice", "192.0.2.10"));
        await node.ReleaseAsync("alice", "c");
        Assert.Equal(0, node.GetLocalIpCount("alice", "192.0.2.10"));
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));
    }

    [Fact]
    public async Task OneNodeCannotReleaseAnotherNodesOwnership()
    {
        var membership = new InMemorySessionStateStore();
        var node1 = CreateNode("nntpd01", membership);
        var node2 = CreateNode("nntpd02", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node1, "s1", V4A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node2, "s2", V4A, srcIpLimit: 1));
        await node1.ReleaseAsync("alice", "s1");
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", node1.OwnerId, NowMs()));
        Assert.True(membership.HasSourceOwner("alice", "192.0.2.10", node2.OwnerId, NowMs()));
        var node3 = CreateNode("nntpd03", membership);
        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(node3, "s3", V4B, srcIpLimit: 1));
    }

    [Fact]
    public async Task ExpiredOwnership_NoLongerConsumesCapacity()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node1 = CreateNode("nntpd01", membership, clock);
        var node2 = CreateNode("nntpd02", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node1, "s1", V4A, srcIpLimit: 1));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node2, "s2", V6A, srcIpLimit: 1));
    }

    [Fact]
    public async Task LiveOwnershipRenewal_PreservesCapacity()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node1 = CreateNode("nntpd01", membership, clock);
        var node2 = CreateNode("nntpd02", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node1, "s1", V4A, srcIpLimit: 1));
        clock.Advance(TimeSpan.FromSeconds(20));
        await node1.RenewLeasesAsync();
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(node2, "s2", V6A, srcIpLimit: 1));
    }

    [Fact]
    public async Task RedisUnavailable_CannotCreateNewSourceMembership()
    {
        var (node, membership) = CreateNode("nntpd01");
        membership.Unavailable = true;
        Assert.Equal(SessionAdmissionResult.Unavailable, await Admit(node, "s1", V4A, srcIpLimit: 1));
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        Assert.Equal(1, node.UnavailableRejects);
    }

    [Fact]
    public async Task RedisUnavailable_DoesNotBlockLiveLocalHotPath()
    {
        var (node, membership) = CreateNode("nntpd01");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, srcIpLimit: 1));
        membership.Unavailable = true;
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A, srcIpLimit: 1));
        Assert.Equal(1, node.LocalHotPathAdmits);
        Assert.Equal(SessionAdmissionResult.Unavailable, await Admit(node, "s3", V6A, srcIpLimit: 2));
    }

    [Fact]
    public async Task RedisUnavailableDuringRenewal_DoesNotExtendHotPathPastLease()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, srcIpLimit: 1));
        membership.Unavailable = true;
        clock.Advance(TimeSpan.FromSeconds(10));
        await node.RenewLeasesAsync();
        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.Equal(SessionAdmissionResult.Unavailable, await Admit(node, "s2", V4A, srcIpLimit: 1));
        Assert.Equal(1, node.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task SessionLimitAndSourceIpLimit_RemainIndependent()
    {
        var (node, membership) = CreateNode("nntpd01");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 2, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A, sessionLimit: 2, srcIpLimit: 1));
        Assert.Equal(
            SessionAdmissionResult.SessionLimitExceeded,
            await Admit(node, "s3", V4A, sessionLimit: 2, srcIpLimit: 1));
        Assert.Equal(
            SessionAdmissionResult.SessionLimitExceeded,
            await Admit(node, "s4", V6A, sessionLimit: 2, srcIpLimit: 1));
        Assert.Equal(2, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(["192.0.2.10"], membership.ActiveSourceIps("alice", NowMs()));
    }

    [Fact]
    public async Task ZeroSourceLimit_SkipsDistributedMembership()
    {
        var (node, membership) = CreateNode("nntpd01");
        membership.Unavailable = true;
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, srcIpLimit: 0));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V6A, srcIpLimit: 0));
        Assert.Equal(0, node.DistributedAdmits);
    }

    [Fact]
    public async Task CancellationDuringDistributedAdmit_DoesNotLeakMembership()
    {
        var membership = new InMemorySessionStateStore
        {
            BlockAdmit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var node = CreateNode("nntpd01", membership);
        using var cts = new CancellationTokenSource();
        var admit = node.TryAdmitAsync("alice", "s1", V4A, 0, 1, cts.Token).AsTask();
        cts.Cancel();
        membership.BlockAdmit.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admit);
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        Assert.Empty(membership.ActiveSourceIps("alice", NowMs()));
    }

    [Fact]
    public async Task FinalReleaseRacingNewLocalSession_ReestablishesOwnership()
    {
        var (node, membership) = CreateNode("nntpd01");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, srcIpLimit: 1));
        await node.ReleaseAsync("alice", "s1");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A, srcIpLimit: 1));
        Assert.True(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));
        Assert.Equal(1, node.GetLocalIpCount("alice", "192.0.2.10"));
    }

    [Fact]
    public async Task ReAdmitSameSession_IsIdempotent()
    {
        var (node, _) = CreateNode("nntpd01");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, srcIpLimit: 1));
        Assert.Equal(1, node.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task RedisStore_UsesSharedFakeRedisEngine()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var node1 = new DistributedSessionStateTracker(
            store,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01");
        var node2 = new DistributedSessionStateTracker(
            store,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd02");

        Assert.Equal(SessionAdmissionResult.Success, await Admit(node1, "s1", V4A, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(node2, "s2", V6A, srcIpLimit: 1));
        Assert.True(redis.Database.ScriptEvaluateCount >= 2);
    }

    [Fact]
    public async Task RedisStoreUnavailable_FailsClosed()
    {
        var redis = new FakeRedisService { IsUnavailable = true };
        var store = new RedisSessionStateStore(redis);
        var node = new DistributedSessionStateTracker(
            store,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01");
        Assert.Equal(SessionAdmissionResult.Unavailable, await Admit(node, "s1", V4A, srcIpLimit: 1));
    }

    private static ValueTask<SessionAdmissionResult> Admit(
        DistributedSessionStateTracker node,
        string sessionId,
        IPAddress ip,
        int sessionLimit = 0,
        int srcIpLimit = 0) =>
        node.TryAdmitAsync("alice", sessionId, ip, sessionLimit, srcIpLimit);

    private static (DistributedSessionStateTracker Node, InMemorySessionStateStore Membership) CreateNode(
        string nodeId)
    {
        var membership = new InMemorySessionStateStore();
        return (CreateNode(nodeId, membership), membership);
    }

    private static DistributedSessionStateTracker CreateNode(
        string nodeId,
        InMemorySessionStateStore membership,
        TimeProvider? time = null) =>
        new(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            nodeId,
            time ?? TimeProvider.System);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
