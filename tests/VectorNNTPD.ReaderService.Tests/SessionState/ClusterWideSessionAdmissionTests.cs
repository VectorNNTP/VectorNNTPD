using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.SessionState;

public sealed class ClusterWideSessionAdmissionTests
{
    private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress V4B = IPAddress.Parse("198.51.100.20");
    private static readonly IPAddress V4C = IPAddress.Parse("203.0.113.30");
    private static readonly IPAddress V6A = IPAddress.Parse("2001:db8::10");

    [Fact]
    public async Task NodeB_CannotExceedNodeAClusterSessionLimit()
    {
        var membership = new InMemorySessionStateStore();
        var nodeA = CreateNode("nntpd01", membership);
        var nodeB = CreateNode("nntpd02", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a1", V4A, sessionLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a2", V4A, sessionLimit: 2));
        Assert.Equal(SessionAdmissionResult.SessionLimitExceeded, await Admit(nodeB, "b1", V6A, sessionLimit: 2));
        Assert.Equal(2, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(0, nodeB.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task NodeB_CanAdmitAfterNodeAReleasesSlot()
    {
        var membership = new InMemorySessionStateStore();
        var nodeA = CreateNode("nntpd01", membership);
        var nodeB = CreateNode("nntpd02", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a1", V4A, sessionLimit: 1));
        Assert.Equal(SessionAdmissionResult.SessionLimitExceeded, await Admit(nodeB, "b1", V6A, sessionLimit: 1));
        await nodeA.ReleaseAsync("alice", "a1");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeB, "b1", V6A, sessionLimit: 1));
        Assert.Equal(1, membership.OwnerSessionCount("alice", nodeB.OwnerId, NowMs()));
        Assert.Equal(0, membership.OwnerSessionCount("alice", nodeA.OwnerId, NowMs()));
    }

    [Fact]
    public async Task MultipleNodes_ContributeToSameGlobalSessionCount()
    {
        var membership = new InMemorySessionStateStore();
        var nodeA = CreateNode("nntpd01", membership);
        var nodeB = CreateNode("nntpd02", membership);
        var nodeC = CreateNode("nntpd03", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a1", V4A, sessionLimit: 10));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a2", V4A, sessionLimit: 10));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a3", V4A, sessionLimit: 10));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a4", V4A, sessionLimit: 10));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeB, "b1", V6A, sessionLimit: 10));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeB, "b2", V6A, sessionLimit: 10));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeB, "b3", V6A, sessionLimit: 10));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeC, "c1", V4B, sessionLimit: 10));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeC, "c2", V4B, sessionLimit: 10));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeC, "c3", V4B, sessionLimit: 10));
        Assert.Equal(10, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(SessionAdmissionResult.SessionLimitExceeded, await Admit(nodeA, "a5", V4A, sessionLimit: 10));
    }

    [Fact]
    public async Task SessionLimitZero_RemainsUnlimited()
    {
        var membership = new InMemorySessionStateStore();
        var nodeA = CreateNode("nntpd01", membership);
        var nodeB = CreateNode("nntpd02", membership);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, $"a{i}", V4A, sessionLimit: 0, srcIpLimit: 2));
            Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeB, $"b{i}", V6A, sessionLimit: 0, srcIpLimit: 2));
        }

        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(nodeA, "x", V4B, sessionLimit: 0, srcIpLimit: 2));
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
    }

    [Fact]
    public async Task SessionFiveSourceTwo_ThirdIpRejectedAsSessionLimit()
    {
        var membership = new InMemorySessionStateStore();
        var nodeA = CreateNode("nntpd01", membership);
        var nodeB = CreateNode("nntpd02", membership);
        var nodeC = CreateNode("nntpd03", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a1", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a2", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a3", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeB, "b1", V6A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeB, "b2", V6A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(
            SessionAdmissionResult.SessionLimitExceeded,
            await Admit(nodeC, "c1", V4B, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(5, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(2, membership.ActiveSourceIps("alice", NowMs()).Count);
        Assert.False(membership.HasSourceOwner("alice", "198.51.100.20", nodeC.OwnerId, NowMs()));
        Assert.Equal(0, nodeC.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task SessionTenSourceTwo_ThirdDistinctIpRejected()
    {
        var membership = new InMemorySessionStateStore();
        var nodeA = CreateNode("nntpd01", membership);
        var nodeB = CreateNode("nntpd02", membership);
        var nodeC = CreateNode("nntpd03", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a1", V4A, sessionLimit: 10, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a2", V4A, sessionLimit: 10, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeB, "b1", V6A, sessionLimit: 10, srcIpLimit: 2));
        Assert.Equal(
            SessionAdmissionResult.SourceAddressLimitExceeded,
            await Admit(nodeC, "c1", V4B, sessionLimit: 10, srcIpLimit: 2));
        Assert.Equal(3, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(2, membership.ActiveSourceIps("alice", NowMs()).Count);
    }

    [Fact]
    public async Task SameSourceIp_ConsumesManySessionSlotsAndOneSourceSlot()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s3", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(3, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(["192.0.2.10"], membership.ActiveSourceIps("alice", NowMs()));
        Assert.Equal(0, node.LocalHotPathAdmits);
        Assert.Equal(3, node.DistributedAdmits);
    }

    [Fact]
    public async Task Ipv4AndIpv6_AreDistinctSources_MappedIpv6FollowsNormalization()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        var mapped = IPAddress.Parse("::ffff:192.0.2.10");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", mapped, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s3", V6A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(
            SessionAdmissionResult.SourceAddressLimitExceeded,
            await Admit(node, "s4", V4B, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(3, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(2, membership.ActiveSourceIps("alice", NowMs()).Count);
    }

    [Fact]
    public async Task RejectedSessionLimit_DoesNotRegisterSourceOwnership()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 1, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.SessionLimitExceeded, await Admit(node, "s2", V6A, sessionLimit: 1, srcIpLimit: 2));
        Assert.Equal(1, membership.ActiveSessionCount("alice", NowMs()));
        Assert.False(membership.HasSourceOwner("alice", "2001:db8::10", node.OwnerId, NowMs()));
        Assert.Equal(["192.0.2.10"], membership.ActiveSourceIps("alice", NowMs()));
    }

    [Fact]
    public async Task RejectedSourceLimit_DoesNotRegisterSessionOwnership()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 10, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(node, "s2", V6A, sessionLimit: 10, srcIpLimit: 1));
        Assert.Equal(1, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(1, membership.OwnerSessionCount("alice", node.OwnerId, NowMs()));
        Assert.False(membership.HasSourceOwner("alice", "2001:db8::10", node.OwnerId, NowMs()));
    }

    [Fact]
    public async Task Release_DecrementsExactlyOneSessionOnSameNode()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 5));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A, sessionLimit: 5));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s3", V4A, sessionLimit: 5));
        await node.ReleaseAsync("alice", "s2");
        Assert.Equal(2, membership.OwnerSessionCount("alice", node.OwnerId, NowMs()));
        Assert.Equal(2, node.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task Release_LastSessionForIpRemovesThisNodesSourceOwnership()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "v4", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "v6", V6A, sessionLimit: 5, srcIpLimit: 2));
        await node.ReleaseAsync("alice", "v4");
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));
        Assert.True(membership.HasSourceOwner("alice", "2001:db8::10", node.OwnerId, NowMs()));
        Assert.Equal(1, membership.ActiveSessionCount("alice", NowMs()));
    }

    [Fact]
    public async Task Release_DoesNotAffectAnotherNodesOwnership()
    {
        var membership = new InMemorySessionStateStore();
        var nodeA = CreateNode("nntpd01", membership);
        var nodeB = CreateNode("nntpd02", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a1", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeB, "b1", V6A, sessionLimit: 5, srcIpLimit: 2));
        await nodeA.ReleaseAsync("alice", "a1");
        Assert.Equal(0, membership.OwnerSessionCount("alice", nodeA.OwnerId, NowMs()));
        Assert.Equal(1, membership.OwnerSessionCount("alice", nodeB.OwnerId, NowMs()));
        Assert.True(membership.HasSourceOwner("alice", "2001:db8::10", nodeB.OwnerId, NowMs()));
    }

    [Fact]
    public async Task ExpiredOwnership_FreesSessionAndSourceCapacity()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var crashed = CreateNode("nntpd01", membership, clock);
        var live = CreateNode("nntpd02", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(crashed, "dead", V4A, sessionLimit: 1, srcIpLimit: 1));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(live, "live", V6A, sessionLimit: 1, srcIpLimit: 1));
        Assert.Equal(1, membership.ActiveSessionCount("alice", clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", crashed.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.True(membership.HasSourceOwner("alice", "2001:db8::10", live.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task ExpiryOfOneNode_DoesNotRemoveAnotherNodesOwnership()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var dying = CreateNode("nntpd01", membership, clock);
        var live = CreateNode("nntpd02", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(dying, "d1", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(live, "l1", V6A, sessionLimit: 5, srcIpLimit: 2));
        clock.Advance(TimeSpan.FromSeconds(20));
        await live.RenewLeasesAsync();
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(1, membership.OwnerSessionCount("alice", live.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.Equal(0, membership.OwnerSessionCount("alice", dying.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.True(membership.HasSourceOwner("alice", "2001:db8::10", live.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task StaleReleaseFromOldIncarnation_CannotRemoveNewOwnership()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var oldIncarnation = CreateNode("nntpd01", membership, clock, incarnation: "old");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(oldIncarnation, "old", V4A, sessionLimit: 1, srcIpLimit: 1));
        clock.Advance(TimeSpan.FromSeconds(31));
        var newIncarnation = CreateNode("nntpd01", membership, clock, incarnation: "new");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(newIncarnation, "new", V4A, sessionLimit: 1, srcIpLimit: 1));
        await oldIncarnation.ReleaseAsync("alice", "old");
        Assert.Equal(1, membership.OwnerSessionCount("alice", newIncarnation.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.True(membership.HasSourceOwner("alice", "192.0.2.10", newIncarnation.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", oldIncarnation.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task RedisUnavailable_FailsClosedWithoutPartialMembership()
    {
        var membership = new InMemorySessionStateStore { Unavailable = true };
        var node = CreateNode("nntpd01", membership);
        Assert.Equal(SessionAdmissionResult.Unavailable, await Admit(node, "s1", V4A, sessionLimit: 2, srcIpLimit: 2));
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Empty(membership.ActiveSourceIps("alice", NowMs()));
    }

    [Fact]
    public async Task FiniteSessionLimit_DoesNotUseSourceIpHotPath()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 5, srcIpLimit: 1));
        membership.Unavailable = true;
        Assert.Equal(SessionAdmissionResult.Unavailable, await Admit(node, "s2", V4A, sessionLimit: 5, srcIpLimit: 1));
        Assert.Equal(0, node.LocalHotPathAdmits);
        Assert.Equal(1, node.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task RenewalFailure_DoesNotExtendLeases()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 0, srcIpLimit: 1));
        membership.Unavailable = true;
        clock.Advance(TimeSpan.FromSeconds(10));
        await node.RenewLeasesAsync();
        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.Equal(SessionAdmissionResult.Unavailable, await Admit(node, "s2", V4A, sessionLimit: 0, srcIpLimit: 1));
    }

    [Fact]
    public async Task ConcurrentNodes_LastSessionSlot_ExactlyOneSucceeds()
    {
        var membership = new InMemorySessionStateStore();
        var nodeA = CreateNode("nntpd01", membership);
        var nodeB = CreateNode("nntpd02", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "held", V4A, sessionLimit: 2, srcIpLimit: 4));
        var results = new SessionAdmissionResult[2];
        Parallel.Invoke(
            () => results[0] = Admit(nodeA, "a", V4B, sessionLimit: 2, srcIpLimit: 4).AsTask().GetAwaiter().GetResult(),
            () => results[1] = Admit(nodeB, "b", V4C, sessionLimit: 2, srcIpLimit: 4).AsTask().GetAwaiter().GetResult());

        Assert.Equal(1, results.Count(static r => r == SessionAdmissionResult.Success));
        Assert.Equal(1, results.Count(static r => r == SessionAdmissionResult.SessionLimitExceeded));
        Assert.Equal(2, membership.ActiveSessionCount("alice", NowMs()));
    }

    [Fact]
    public async Task ConcurrentNodes_LastSourceSlot_ExactlyOneSucceeds()
    {
        var membership = new InMemorySessionStateStore();
        var nodeA = CreateNode("nntpd01", membership);
        var nodeB = CreateNode("nntpd02", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "held", V4A, sessionLimit: 10, srcIpLimit: 2));
        var results = new SessionAdmissionResult[2];
        Parallel.Invoke(
            () => results[0] = Admit(nodeA, "a", V4B, sessionLimit: 10, srcIpLimit: 2).AsTask().GetAwaiter().GetResult(),
            () => results[1] = Admit(nodeB, "b", V6A, sessionLimit: 10, srcIpLimit: 2).AsTask().GetAwaiter().GetResult());

        Assert.Equal(1, results.Count(static r => r == SessionAdmissionResult.Success));
        Assert.Equal(1, results.Count(static r => r == SessionAdmissionResult.SourceAddressLimitExceeded));
        Assert.Equal(2, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(2, membership.ActiveSourceIps("alice", NowMs()).Count);
    }

    [Fact]
    public async Task RedisStore_OneEvalPerAdmitAndRelease()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var node = new DistributedSessionStateTracker(
            store,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 3, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A, sessionLimit: 3, srcIpLimit: 2));
        Assert.Equal(2, redis.Database.ScriptEvaluateCount);
        await node.ReleaseAsync("alice", "s1");
        Assert.Equal(3, redis.Database.ScriptEvaluateCount);
        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(node, "s3", V6A, sessionLimit: 3, srcIpLimit: 1));
        Assert.Equal(4, redis.Database.ScriptEvaluateCount);
    }

    [Fact]
    public void Engine_RejectedAdmission_LeavesHashesUnchanged()
    {
        var engine = new SessionStateEngine();
        const string src = "src:alice";
        const string sess = "sess:alice";
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(src, sess, "192.0.2.10", "nntpd01:a", sessionLimit: 1, srcIpLimit: 2, 0, 30_000, 1, 1));
        Assert.Equal(
            SessionStateEngine.RejectedSessionLimit,
            engine.TryAdmitStatus(src, sess, "2001:db8::10", "nntpd02:b", sessionLimit: 1, srcIpLimit: 2, 0, 30_000, 1, 1));
        Assert.Equal(1, engine.ActiveSessionCount(sess, 0));
        Assert.False(engine.HasSourceOwner(src, "2001:db8::10", "nntpd02:b", 0));

        Assert.Equal(
            SessionStateEngine.AcceptedExisting,
            engine.TryAdmitStatus(src, sess, "192.0.2.10", "nntpd03:c", sessionLimit: 10, srcIpLimit: 1, 0, 30_000, 1, 1));
        var sessions = engine.ActiveSessionCount(sess, 0);
        Assert.Equal(
            SessionStateEngine.RejectedSourceLimit,
            engine.TryAdmitStatus(src, sess, "198.51.100.20", "nntpd04:d", sessionLimit: 10, srcIpLimit: 1, 0, 30_000, 1, 1));
        Assert.Equal(sessions, engine.ActiveSessionCount(sess, 0));
        Assert.False(engine.HasSourceOwner(src, "198.51.100.20", "nntpd04:d", 0));
    }

    [Fact]
    public void Engine_StaleGenerationRelease_DoesNotRemoveNewerOwnership()
    {
        var engine = new SessionStateEngine();
        const string src = "src:alice";
        const string sess = "sess:alice";
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(src, sess, "192.0.2.10", "nntpd01:old", 1, 1, 0, 30_000, 1, 1));
        _ = engine.TryAdmitStatus(src, sess, "192.0.2.10", "nntpd01:new", 1, 1, 31_000, 30_000, 2, 2);
        _ = engine.Release(src, sess, "192.0.2.10", "nntpd01:old", 1, 1);
        Assert.Equal(1, engine.OwnerSessionCount(sess, "nntpd01:new", 31_000));
        Assert.True(engine.HasSourceOwner(src, "192.0.2.10", "nntpd01:new", 31_000));
    }

    [Fact]
    public void Engine_SameOwnerNewGenerationAdmit_ReplacesStaleCount()
    {
        var engine = new SessionStateEngine();
        const string src = "src:alice";
        const string sess = "sess:alice";
        const string owner = "nntpd01:a";
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(src, sess, "192.0.2.10", owner, 2, 2, 0, 30_000, 1, 1));
        Assert.Equal(1, engine.OwnerSessionCount(sess, owner, 0));

        Assert.Equal(
            SessionStateEngine.AcceptedExisting,
            engine.TryAdmitStatus(src, sess, "192.0.2.10", owner, 2, 2, 0, 30_000, 2, 2));
        Assert.Equal(1, engine.OwnerSessionCount(sess, owner, 0));
        Assert.True(engine.TryGetOwnership(sess, owner, out _, out var sessionGeneration, out var sessionCount));
        Assert.Equal(2, sessionGeneration);
        Assert.Equal(1, sessionCount);

        _ = engine.Release(src, sess, "192.0.2.10", owner, 2, 2);
        Assert.Equal(0, engine.OwnerSessionCount(sess, owner, 0));
        Assert.False(engine.HasSourceOwner(src, "192.0.2.10", owner, 0));
    }

    [Fact]
    public async Task RedisUnavailableRelease_ThenReadmit_ReleaseClearsOwnership()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 2, srcIpLimit: 2));
        Assert.Equal(1, membership.ActiveSessionCount("alice", NowMs()));

        membership.Unavailable = true;
        await node.ReleaseAsync("alice", "s1");
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        Assert.Equal(1, membership.ActiveSessionCount("alice", NowMs()));

        membership.Unavailable = false;
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A, sessionLimit: 2, srcIpLimit: 2));
        Assert.Equal(1, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(1, membership.OwnerSessionCount("alice", node.OwnerId, NowMs()));

        await node.ReleaseAsync("alice", "s2");
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
    }

    private static ValueTask<SessionAdmissionResult> Admit(
        DistributedSessionStateTracker node,
        string sessionId,
        IPAddress ip,
        int sessionLimit = 0,
        int srcIpLimit = 0) =>
        node.TryAdmitAsync("alice", sessionId, ip, sessionLimit, srcIpLimit);

    private static DistributedSessionStateTracker CreateNode(
        string nodeId,
        InMemorySessionStateStore membership,
        TimeProvider? time = null,
        string? incarnation = null) =>
        new(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            nodeId,
            time ?? TimeProvider.System,
            incarnation: incarnation);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
