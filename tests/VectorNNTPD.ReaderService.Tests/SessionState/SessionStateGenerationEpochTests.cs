using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.SessionState;

/// <summary>
/// Generation-epoch increment/replace: same generation increments; a new
/// generation for the same owner replaces stale count with 1. Different owners
/// are separate HASH fields and are not replaced.
/// </summary>
public sealed class SessionStateGenerationEpochTests
{
    private const string Src = "src:alice";
    private const string Sess = "sess:alice";
    private const string O1 = "nntpd01:a";
    private const string O2 = "nntpd02:b";
    private const string Ip = "192.0.2.10";
    private const long LeaseMs = 30_000;
    private static readonly IPAddress V4A = IPAddress.Parse(Ip);

    [Fact]
    public void TryAdmitScript_IncrementIsAtomicAndGenerationAware()
    {
        var script = SessionStateScripts.TryAdmit;
        Assert.Contains("local function increment(key, field, gen)", script, StringComparison.Ordinal);
        Assert.Contains("if storedGen and storedGen == gen then", script, StringComparison.Ordinal);
        Assert.Contains("tostring((count or 0) + 1)", script, StringComparison.Ordinal);
        Assert.Contains("tostring(expiry) .. '|' .. gen .. '|1'", script, StringComparison.Ordinal);
        var incrementAt = script.IndexOf("local function increment", StringComparison.Ordinal);
        var pruneAt = script.IndexOf("prune(srcKey)", StringComparison.Ordinal);
        Assert.True(incrementAt > 0 && incrementAt < pruneAt);
        Assert.True(script.IndexOf("increment(sessKey, owner, sessionGen)", StringComparison.Ordinal) > pruneAt);
        Assert.True(script.IndexOf("increment(srcKey, srcField, sourceGen)", StringComparison.Ordinal) > pruneAt);
    }

    [Fact]
    public void SameOwnerSameGeneration_IncrementsCountOnEngineAndLuaRule()
    {
        AssertIncrementParity(
            owner: O1,
            storedGeneration: 1,
            storedCount: 1,
            requestedGeneration: 1,
            expectedGeneration: 1,
            expectedCount: 2);
    }

    [Fact]
    public void SameOwnerNewGeneration_ReplacesStaleCountOnEngineAndLuaRule()
    {
        AssertIncrementParity(
            owner: O1,
            storedGeneration: 1,
            storedCount: 1,
            requestedGeneration: 2,
            expectedGeneration: 2,
            expectedCount: 1);
    }

    [Fact]
    public void SameOwnerNewGeneration_AfterMultipleStaleSessions_ReplacesWithCountOne()
    {
        AssertIncrementParity(
            owner: O1,
            storedGeneration: 1,
            storedCount: 7,
            requestedGeneration: 2,
            expectedGeneration: 2,
            expectedCount: 1);
    }

    [Fact]
    public void DifferentOwner_CreatesSeparateField_LeavesExistingOwnerUnchanged()
    {
        var engine = new SessionStateEngine();
        engine.WriteOwnership(Sess, O1, expiryUnixMs: 30_000, generation: 1, count: 7);
        engine.WriteOwnership(Src, SessionStateKeys.SourceField(Ip, O1), 30_000, 1, 7);

        Assert.Equal(
            SessionStateEngine.AcceptedExisting,
            engine.TryAdmitStatus(Src, Sess, Ip, O2, sessionLimit: 20, srcIpLimit: 4, 0, LeaseMs, 9, 9));

        AssertOwnership(engine, Sess, O1, generation: 1, count: 7);
        AssertOwnership(engine, Sess, O2, generation: 9, count: 1);
        AssertOwnership(engine, Src, SessionStateKeys.SourceField(Ip, O1), 1, 7);
        AssertOwnership(engine, Src, SessionStateKeys.SourceField(Ip, O2), 9, 1);
        Assert.Equal(8, engine.ActiveSessionCount(Sess, 0));
        Assert.Equal([Ip], engine.ActiveSourceIps(Src, 0));
    }

    [Fact]
    public void ReleaseAfterReplacement_DeletesNewEpoch_OldCountDoesNotSurvive()
    {
        var engine = SeedThenReplace(storedCount: 5);
        _ = engine.Release(Src, Sess, Ip, O1, 2, 2);
        Assert.False(engine.TryGetOwnership(Sess, O1, out _, out _, out _));
        Assert.False(engine.HasSourceOwner(Src, Ip, O1, 0));
        Assert.Equal(0, engine.ActiveSessionCount(Sess, 0));
    }

    [Fact]
    public void StaleReleaseAfterReplacement_LeavesNewEpochUnchanged()
    {
        var engine = SeedThenReplace(storedCount: 5);
        AssertOwnership(engine, Sess, O1, 2, 1);
        _ = engine.Release(Src, Sess, Ip, O1, 1, 1);
        AssertOwnership(engine, Sess, O1, 2, 1);
        AssertOwnership(engine, Src, SessionStateKeys.SourceField(Ip, O1), 2, 1);
        _ = engine.Release(Src, Sess, Ip, O1, 2, 2);
        Assert.False(engine.TryGetOwnership(Sess, O1, out _, out _, out _));
        Assert.False(engine.HasSourceOwner(Src, Ip, O1, 0));
    }

    [Fact]
    public void StaleRenewAfterReplacement_DoesNotExtendOrRecreateOldGeneration()
    {
        var engine = SeedThenReplace(storedCount: 5);
        Assert.True(engine.TryGetOwnership(Sess, O1, out var expiryBefore, out _, out _));
        Assert.Equal(0, engine.Renew(Src, Sess, O1, sessionGeneration: 1, nowUnixMs: 1_000, LeaseMs, [(Ip, 1)]));
        Assert.True(engine.TryGetOwnership(Sess, O1, out var expiryAfterStale, out var generation, out var count));
        Assert.Equal(2, generation);
        Assert.Equal(1, count);
        Assert.Equal(expiryBefore, expiryAfterStale);

        Assert.Equal(1, engine.Renew(Src, Sess, O1, sessionGeneration: 2, nowUnixMs: 1_000, LeaseMs, [(Ip, 2)]));
        Assert.True(engine.TryGetOwnership(Sess, O1, out var expiryRenewed, out generation, out count));
        Assert.Equal(2, generation);
        Assert.Equal(1, count);
        Assert.True(expiryRenewed > expiryAfterStale);
    }

    [Fact]
    public void ExpiredOldGeneration_IsPrunedThenNewEpochStartsAtCountOne()
    {
        var engine = new SessionStateEngine();
        engine.WriteOwnership(Sess, O1, expiryUnixMs: 1_000, generation: 1, count: 7);
        engine.WriteOwnership(Src, SessionStateKeys.SourceField(Ip, O1), 1_000, 1, 7);
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(Src, Sess, Ip, O1, 20, 4, nowUnixMs: 2_000, LeaseMs, 2, 2));
        AssertOwnership(engine, Sess, O1, 2, 1, nowUnixMs: 2_000);
        AssertOwnership(engine, Src, SessionStateKeys.SourceField(Ip, O1), 2, 1, nowUnixMs: 2_000);
    }

    [Fact]
    public void UnexpiredOldGeneration_IsReplacedWithoutWaitingForTtl()
    {
        var engine = new SessionStateEngine();
        engine.WriteOwnership(Sess, O1, expiryUnixMs: 80_000, generation: 1, count: 7);
        engine.WriteOwnership(Src, SessionStateKeys.SourceField(Ip, O1), 80_000, 1, 7);
        Assert.Equal(
            SessionStateEngine.AcceptedExisting,
            engine.TryAdmitStatus(Src, Sess, Ip, O1, 20, 4, nowUnixMs: 1_000, LeaseMs, 2, 2));
        AssertOwnership(engine, Sess, O1, 2, 1, nowUnixMs: 1_000);
        AssertOwnership(engine, Src, SessionStateKeys.SourceField(Ip, O1), 2, 1, nowUnixMs: 1_000);
    }

    [Fact]
    public void SessionOwnershipReplacement_DoesNotAlterAnotherOwner()
    {
        var engine = new SessionStateEngine();
        engine.WriteOwnership(Sess, O1, 30_000, 1, 7);
        engine.WriteOwnership(Sess, O2, 30_000, 9, 3);
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(Src, Sess, Ip, O1, sessionLimit: 20, srcIpLimit: 0, 0, LeaseMs, 2, 0));
        AssertOwnership(engine, Sess, O1, 2, 1);
        AssertOwnership(engine, Sess, O2, 9, 3);
        Assert.False(engine.TryGetOwnership(Src, SessionStateKeys.SourceField(Ip, O1), out _, out _, out _));
    }

    [Fact]
    public void SourceOwnershipReplacement_DoesNotAlterAnotherOwnerOfSameIp()
    {
        var engine = new SessionStateEngine();
        engine.WriteOwnership(Src, SessionStateKeys.SourceField(Ip, O1), 30_000, 1, 7);
        engine.WriteOwnership(Src, SessionStateKeys.SourceField(Ip, O2), 30_000, 9, 3);
        Assert.Equal(
            SessionStateEngine.AcceptedExisting,
            engine.TryAdmitStatus(Src, Sess, Ip, O1, sessionLimit: 0, srcIpLimit: 4, 0, LeaseMs, 0, 2));
        AssertOwnership(engine, Src, SessionStateKeys.SourceField(Ip, O1), 2, 1);
        AssertOwnership(engine, Src, SessionStateKeys.SourceField(Ip, O2), 9, 3);
        Assert.False(engine.TryGetOwnership(Sess, O1, out _, out _, out _));
        Assert.Equal([Ip], engine.ActiveSourceIps(Src, 0));
    }

    [Fact]
    public async Task RedisStoreDispatch_SameOwnerNewGeneration_ReplacesThroughTryAdmitScript()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var now = DateTimeOffset.UnixEpoch;
        var ttl = TimeSpan.FromSeconds(30);
        Assert.True((await store.TryAdmitAsync("alice", Ip, O1, 20, 4, 1, 1, now, ttl)).Accepted);
        Assert.Equal(1, redis.Database.ScriptEvaluateCount);
        SeedStoreEngine(redis, generation: 1, count: 7);
        Assert.True((await store.TryAdmitAsync("alice", Ip, O1, 20, 4, 2, 2, now, ttl)).Accepted);
        var engine = redis.Database.SessionStateEngine;
        var sessKey = Encoding.UTF8.GetString(SessionStateKeys.CreateSession("alice"));
        var srcKey = Encoding.UTF8.GetString(SessionStateKeys.CreateSource("alice"));
        AssertOwnership(engine, sessKey, O1, 2, 1);
        AssertOwnership(engine, srcKey, SessionStateKeys.SourceField(Ip, O1), 2, 1);
        Assert.True(redis.Database.ScriptEvaluateCount >= 2);
    }

    [Fact]
    public async Task Tracker_RedisDownRelease_ReadmitUsesNewEpochThenClears()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership, "inc1");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "A"));
        Assert.True(membership.Engine.TryGetOwnership(
            InMemorySessionStateStore.SessionKey("alice"),
            node.OwnerId,
            out _,
            out var sessionGeneration1,
            out var sessionCount1));
        Assert.True(membership.Engine.TryGetOwnership(
            InMemorySessionStateStore.SourceKey("alice"),
            SessionStateKeys.SourceField(Ip, node.OwnerId),
            out _,
            out var sourceGeneration1,
            out var sourceCount1));
        Assert.Equal(1, sessionCount1);
        Assert.Equal(1, sourceCount1);
        Assert.True(sessionGeneration1 > 0);
        Assert.True(sourceGeneration1 > 0);

        membership.Unavailable = true;
        await node.ReleaseAsync("alice", "A");
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        AssertOwnership(
            membership.Engine,
            InMemorySessionStateStore.SessionKey("alice"),
            node.OwnerId,
            sessionGeneration1,
            1);
        AssertOwnership(
            membership.Engine,
            InMemorySessionStateStore.SourceKey("alice"),
            SessionStateKeys.SourceField(Ip, node.OwnerId),
            sourceGeneration1,
            1);

        membership.Unavailable = false;
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "B"));
        Assert.True(membership.Engine.TryGetOwnership(
            InMemorySessionStateStore.SessionKey("alice"),
            node.OwnerId,
            out _,
            out var sessionGeneration2,
            out var sessionCount2));
        Assert.True(membership.Engine.TryGetOwnership(
            InMemorySessionStateStore.SourceKey("alice"),
            SessionStateKeys.SourceField(Ip, node.OwnerId),
            out _,
            out var sourceGeneration2,
            out var sourceCount2));
        Assert.True(sessionGeneration2 > sessionGeneration1);
        Assert.True(sourceGeneration2 > sourceGeneration1);
        Assert.Equal(1, sessionCount2);
        Assert.Equal(1, sourceCount2);

        await node.ReleaseAsync("alice", "B");
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
        Assert.False(membership.HasSourceOwner("alice", Ip, node.OwnerId, NowMs()));

        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "C"));
        Assert.True(membership.Engine.TryGetOwnership(
            InMemorySessionStateStore.SessionKey("alice"),
            node.OwnerId,
            out _,
            out var generation3,
            out var count3));
        Assert.True(generation3 > sessionGeneration2);
        Assert.Equal(1, count3);
        await node.ReleaseAsync("alice", "C");
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
    }

    [Fact]
    public async Task Tracker_RepeatedStaleEpochs_NeverAccumulateCounts()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership, "inc1");
        long previousGeneration = 0;
        for (var epoch = 1; epoch <= 3; epoch++)
        {
            Assert.Equal(SessionAdmissionResult.Success, await Admit(node, $"s{epoch}"));
            Assert.True(membership.Engine.TryGetOwnership(
                InMemorySessionStateStore.SessionKey("alice"),
                node.OwnerId,
                out _,
                out var generation,
                out var count));
            Assert.Equal(1, count);
            Assert.True(generation > previousGeneration);
            previousGeneration = generation;
            membership.Unavailable = true;
            await node.ReleaseAsync("alice", $"s{epoch}");
            membership.Unavailable = false;
        }

        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s4"));
        Assert.True(membership.Engine.TryGetOwnership(
            InMemorySessionStateStore.SessionKey("alice"),
            node.OwnerId,
            out _,
            out var finalGeneration,
            out var finalCount));
        Assert.Equal(1, finalCount);
        Assert.True(finalGeneration > previousGeneration);
        await node.ReleaseAsync("alice", "s4");
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
        Assert.False(membership.HasSourceOwner("alice", Ip, node.OwnerId, NowMs()));
        Assert.False(membership.Engine.TryGetOwnership(
            InMemorySessionStateStore.SessionKey("alice"),
            node.OwnerId,
            out _,
            out _,
            out _));
    }

    [Fact]
    public async Task Tracker_SourceReplacement_DoesNotDropPeerOwnerOfSameIp()
    {
        var membership = new InMemorySessionStateStore();
        var nodeA = CreateNode(membership, "incA", "nntpd01");
        var nodeB = CreateNode(membership, "incB", "nntpd02");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a1"));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeB, "b1"));
        Assert.Equal([Ip], membership.ActiveSourceIps("alice", NowMs()));

        membership.Unavailable = true;
        await nodeA.ReleaseAsync("alice", "a1");
        membership.Unavailable = false;
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a2"));

        Assert.True(membership.HasSourceOwner("alice", Ip, nodeB.OwnerId, NowMs()));
        Assert.Equal(1, membership.OwnerSessionCount("alice", nodeB.OwnerId, NowMs()));
        Assert.Equal(1, membership.OwnerSessionCount("alice", nodeA.OwnerId, NowMs()));
        Assert.Equal([Ip], membership.ActiveSourceIps("alice", NowMs()));
        Assert.Equal(2, membership.ActiveSessionCount("alice", NowMs()));
    }

    private static void AssertIncrementParity(
        string owner,
        long storedGeneration,
        int storedCount,
        long requestedGeneration,
        long expectedGeneration,
        int expectedCount)
    {
        var sessionField = owner;
        var sourceField = SessionStateKeys.SourceField(Ip, owner);
        var luaSession = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [sessionField] = SessionStateKeys.Value(30_000, storedGeneration, storedCount),
        };
        var luaSource = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [sourceField] = SessionStateKeys.Value(30_000, storedGeneration, storedCount),
        };
        ApplyLuaIncrement(luaSession, sessionField, requestedGeneration, expiry: 30_000);
        ApplyLuaIncrement(luaSource, sourceField, requestedGeneration, expiry: 30_000);
        AssertLuaOwnership(luaSession, sessionField, expectedGeneration, expectedCount);
        AssertLuaOwnership(luaSource, sourceField, expectedGeneration, expectedCount);

        var engine = new SessionStateEngine();
        engine.WriteOwnership(Sess, sessionField, 30_000, storedGeneration, storedCount);
        engine.WriteOwnership(Src, sourceField, 30_000, storedGeneration, storedCount);
        _ = engine.TryAdmitStatus(Src, Sess, Ip, owner, 20, 4, 0, LeaseMs, requestedGeneration, requestedGeneration);
        AssertOwnership(engine, Sess, sessionField, expectedGeneration, expectedCount);
        AssertOwnership(engine, Src, sourceField, expectedGeneration, expectedCount);
    }

    private static SessionStateEngine SeedThenReplace(int storedCount)
    {
        var engine = new SessionStateEngine();
        engine.WriteOwnership(Sess, O1, 30_000, 1, storedCount);
        engine.WriteOwnership(Src, SessionStateKeys.SourceField(Ip, O1), 30_000, 1, storedCount);
        Assert.Equal(
            SessionStateEngine.AcceptedExisting,
            engine.TryAdmitStatus(Src, Sess, Ip, O1, 20, 4, 0, LeaseMs, 2, 2));
        AssertOwnership(engine, Sess, O1, 2, 1);
        AssertOwnership(engine, Src, SessionStateKeys.SourceField(Ip, O1), 2, 1);
        return engine;
    }

    private static void ApplyLuaIncrement(
        Dictionary<string, string> hash,
        string field,
        long generation,
        long expiry)
    {
        var gen = generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (hash.TryGetValue(field, out var current)
            && SessionStateKeys.TrySplitValue(current, out _, out var storedGeneration, out var count)
            && storedGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture) == gen)
        {
            hash[field] = SessionStateKeys.Value(expiry, storedGeneration, count + 1);
            return;
        }

        hash[field] = SessionStateKeys.Value(expiry, generation, 1);
    }

    private static void AssertLuaOwnership(
        Dictionary<string, string> hash,
        string field,
        long generation,
        int count)
    {
        Assert.True(hash.TryGetValue(field, out var value));
        Assert.True(SessionStateKeys.TrySplitValue(value, out _, out var storedGeneration, out var storedCount));
        Assert.Equal(generation, storedGeneration);
        Assert.Equal(count, storedCount);
    }

    private static void AssertOwnership(
        SessionStateEngine engine,
        string key,
        string field,
        long generation,
        int count,
        long nowUnixMs = 0)
    {
        Assert.True(engine.TryGetOwnership(key, field, out var expiry, out var storedGeneration, out var storedCount));
        Assert.Equal(generation, storedGeneration);
        Assert.Equal(count, storedCount);
        Assert.True(expiry > nowUnixMs);
    }

    private static void SeedStoreEngine(FakeRedisService redis, long generation, int count)
    {
        var engine = redis.Database.SessionStateEngine;
        var sessKey = Encoding.UTF8.GetString(SessionStateKeys.CreateSession("alice"));
        var srcKey = Encoding.UTF8.GetString(SessionStateKeys.CreateSource("alice"));
        engine.WriteOwnership(sessKey, O1, 30_000, generation, count);
        engine.WriteOwnership(srcKey, SessionStateKeys.SourceField(Ip, O1), 30_000, generation, count);
    }

    private static ValueTask<SessionAdmissionResult> Admit(DistributedSessionStateTracker node, string sessionId) =>
        node.TryAdmitAsync("alice", sessionId, V4A, sessionLimit: 20, srcIpLimit: 4);

    private static DistributedSessionStateTracker CreateNode(
        InMemorySessionStateStore membership,
        string incarnation,
        string nodeId = "nntpd01") =>
        new(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            nodeId,
            TimeProvider.System,
            incarnation: incarnation);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
