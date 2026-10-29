using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.SessionState;

/// <summary>
/// Compensating RELEASE after TRY_ADMIT + OCE: generation, owner, source, and
/// account-gate serialization.
/// </summary>
public sealed class SessionStateAdmissionCompensationTests
{
    private const string Src = "src:alice";
    private const string Sess = "sess:alice";
    private const string O1 = "nntpd01:a";
    private const string O2 = "nntpd02:b";
    private const string IpA = "192.0.2.10";
    private const string IpB = "198.51.100.20";
    private static readonly IPAddress V4A = IPAddress.Parse(IpA);
    private static readonly IPAddress V4B = IPAddress.Parse(IpB);

    [Fact]
    public async Task SameAccountAdmission_WaitsUntilCompensationReleasesGate()
    {
        var inner = new InMemorySessionStateStore();
        var store = new CompensationStore(inner);
        var node = CreateNode(store);
        using var cts = new CancellationTokenSource();
        var admitA = node.TryAdmitAsync("alice", "A", V4A, sessionLimit: 5, srcIpLimit: 2, cts.Token).AsTask();
        await store.Admitted.Task;
        Assert.Equal(1, inner.ActiveSessionCount("alice", NowMs()));

        var admitB = node.TryAdmitAsync("alice", "B", V4B, sessionLimit: 5, srcIpLimit: 2).AsTask();
        Assert.False(admitB.IsCompleted);

        Assert.True(inner.Engine.TryGetOwnership(
            InMemorySessionStateStore.SessionKey("alice"),
            node.OwnerId,
            out _,
            out var canceledSessionGeneration,
            out var canceledSessionCount));
        Assert.Equal(1, canceledSessionCount);
        Assert.True(inner.Engine.TryGetOwnership(
            InMemorySessionStateStore.SourceKey("alice"),
            SessionStateKeys.SourceField(IpA, node.OwnerId),
            out _,
            out var canceledSourceGeneration,
            out var canceledSourceCount));
        Assert.Equal(1, canceledSourceCount);

        cts.Cancel();
        store.Continue.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admitA);
        Assert.Equal(SessionAdmissionResult.Success, await admitB);
        Assert.Equal(1, node.GetLocalSessionCount("alice"));
        Assert.Equal(1, inner.ActiveSessionCount("alice", NowMs()));
        Assert.False(inner.HasSourceOwner("alice", IpA, node.OwnerId, NowMs()));
        Assert.True(inner.HasSourceOwner("alice", IpB, node.OwnerId, NowMs()));
        Assert.Equal(node.OwnerId, store.LastReleaseOwnerId);
        Assert.Equal(IpA, store.LastReleaseIp);
        Assert.Equal(canceledSessionGeneration, store.LastReleaseSessionGeneration);
        Assert.Equal(canceledSourceGeneration, store.LastReleaseSourceGeneration);
        Assert.Equal(CancellationToken.None, store.LastReleaseToken);
        Assert.True(inner.Engine.TryGetOwnership(
            InMemorySessionStateStore.SessionKey("alice"),
            node.OwnerId,
            out _,
            out var liveSessionGeneration,
            out _));
        Assert.NotEqual(canceledSessionGeneration, liveSessionGeneration);
    }

    [Fact]
    public async Task CanceledCallerToken_IsNotUsedForCompensatingRelease()
    {
        var inner = new InMemorySessionStateStore();
        var store = new CompensationStore(inner);
        var node = CreateNode(store);
        using var cts = new CancellationTokenSource();
        var admit = node.TryAdmitAsync("alice", "A", V4A, sessionLimit: 2, srcIpLimit: 2, cts.Token).AsTask();
        await store.Admitted.Task;
        cts.Cancel();
        Assert.True(cts.Token.IsCancellationRequested);
        store.Continue.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admit);
        Assert.Equal(CancellationToken.None, store.LastReleaseToken);
        Assert.False(store.LastReleaseToken.CanBeCanceled);
        Assert.Equal(1, store.ReleaseCalls);
        Assert.Equal(0, inner.ActiveSessionCount("alice", NowMs()));
    }

    [Fact]
    public async Task CompensationWhenRedisUnavailable_LeavesTtlOwnershipAndNoLocalCommit()
    {
        var inner = new InMemorySessionStateStore();
        var store = new CompensationStore(inner) { UnavailableOnRelease = true };
        var node = CreateNode(store);
        using var cts = new CancellationTokenSource();
        var admit = node.TryAdmitAsync("alice", "A", V4A, sessionLimit: 2, srcIpLimit: 2, cts.Token).AsTask();
        await store.Admitted.Task;
        cts.Cancel();
        store.Continue.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admit);
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        Assert.Equal(0, node.DistributedAdmits);
        Assert.Equal(1, inner.ActiveSessionCount("alice", NowMs()));
        Assert.True(inner.HasSourceOwner("alice", IpA, node.OwnerId, NowMs()));
        Assert.Equal(1, store.ReleaseCalls);
    }

    [Fact]
    public async Task NonCanceledStoreException_DoesNotCompensate()
    {
        var inner = new InMemorySessionStateStore();
        var store = new CompensationStore(inner) { ThrowAfterAdmit = new InvalidOperationException("store fault") };
        var node = CreateNode(store);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.TryAdmitAsync("alice", "A", V4A, sessionLimit: 2, srcIpLimit: 2).AsTask());
        Assert.Equal(0, store.ReleaseCalls);
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        Assert.Equal(1, inner.ActiveSessionCount("alice", NowMs()));
    }

    [Fact]
    public async Task ObjectDisposedException_DoesNotCompensate()
    {
        var inner = new InMemorySessionStateStore();
        var store = new CompensationStore(inner) { ThrowAfterAdmit = new ObjectDisposedException("membership") };
        var node = CreateNode(store);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => node.TryAdmitAsync("alice", "A", V4A, sessionLimit: 2, srcIpLimit: 2).AsTask());
        Assert.Equal(0, store.ReleaseCalls);
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        Assert.Equal(1, inner.ActiveSessionCount("alice", NowMs()));
    }

    [Fact]
    public void Engine_SameGenerationCompensation_RemovesCountOneAndDecrementsCountTwo()
    {
        var engine = new SessionStateEngine();
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(Src, Sess, IpA, O1, 10, 4, 0, 30_000, 1, 1));
        _ = engine.Release(Src, Sess, IpA, O1, 1, 1);
        Assert.Equal(0, engine.OwnerSessionCount(Sess, O1, 0));
        Assert.False(engine.HasSourceOwner(Src, IpA, O1, 0));

        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(Src, Sess, IpA, O1, 10, 4, 0, 30_000, 1, 1));
        Assert.Equal(
            SessionStateEngine.AcceptedExisting,
            engine.TryAdmitStatus(Src, Sess, IpA, O1, 10, 4, 0, 30_000, 1, 1));
        Assert.Equal(2, engine.OwnerSessionCount(Sess, O1, 0));
        _ = engine.Release(Src, Sess, IpA, O1, 1, 1);
        Assert.Equal(1, engine.OwnerSessionCount(Sess, O1, 0));
        Assert.True(engine.HasSourceOwner(Src, IpA, O1, 0));
    }

    [Fact]
    public void Engine_StaleGenerationCompensation_LeavesReplacedEpoch()
    {
        var engine = new SessionStateEngine();
        engine.WriteOwnership(Sess, O1, 30_000, 1, 1);
        engine.WriteOwnership(Src, SessionStateKeys.SourceField(IpA, O1), 30_000, 1, 1);
        Assert.Equal(
            SessionStateEngine.AcceptedExisting,
            engine.TryAdmitStatus(Src, Sess, IpA, O1, 10, 4, 0, 30_000, 2, 2));
        Assert.True(engine.TryGetOwnership(Sess, O1, out _, out var sessionGeneration, out var sessionCount));
        Assert.Equal(2, sessionGeneration);
        Assert.Equal(1, sessionCount);
        _ = engine.Release(Src, Sess, IpA, O1, 1, 1);
        Assert.True(engine.TryGetOwnership(Sess, O1, out _, out sessionGeneration, out sessionCount));
        Assert.Equal(2, sessionGeneration);
        Assert.Equal(1, sessionCount);
        Assert.True(engine.HasSourceOwner(Src, IpA, O1, 0));
    }

    [Fact]
    public void Engine_SourceCompensation_LeavesOtherOwnerAndOtherIp()
    {
        var engine = new SessionStateEngine();
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(Src, Sess, IpA, O1, 10, 4, 0, 30_000, 1, 1));
        Assert.Equal(
            SessionStateEngine.AcceptedExisting,
            engine.TryAdmitStatus(Src, Sess, IpA, O2, 10, 4, 0, 30_000, 9, 9));
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(Src, Sess, IpB, O1, 10, 4, 0, 30_000, 1, 3));
        _ = engine.Release(Src, Sess, IpA, O1, 1, 1);
        Assert.False(engine.HasSourceOwner(Src, IpA, O1, 0));
        Assert.True(engine.HasSourceOwner(Src, IpA, O2, 0));
        Assert.True(engine.HasSourceOwner(Src, IpB, O1, 0));
        Assert.Equal(1, engine.OwnerSessionCount(Sess, O2, 0));
        Assert.Equal(1, engine.OwnerSessionCount(Sess, O1, 0));
    }

    [Fact]
    public void Engine_DualOwnershipCompensation_RemovesBothWhenCountIsOne()
    {
        var engine = new SessionStateEngine();
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(Src, Sess, IpA, O1, 10, 4, 0, 30_000, 1, 1));
        _ = engine.Release(Src, Sess, IpA, O1, 1, 1);
        Assert.Equal(0, engine.ActiveSessionCount(Sess, 0));
        Assert.False(engine.HasSourceOwner(Src, IpA, O1, 0));
    }

    [Fact]
    public void Engine_DualOwnershipCompensation_KeepsSourceWhenAnotherSessionRemains()
    {
        var engine = new SessionStateEngine();
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(Src, Sess, IpA, O1, 10, 4, 0, 30_000, 1, 1));
        Assert.Equal(
            SessionStateEngine.AcceptedExisting,
            engine.TryAdmitStatus(Src, Sess, IpA, O1, 10, 4, 0, 30_000, 1, 1));
        Assert.Equal(2, engine.OwnerSessionCount(Sess, O1, 0));
        Assert.True(engine.TryGetOwnership(Src, SessionStateKeys.SourceField(IpA, O1), out _, out _, out var sourceCount));
        Assert.Equal(2, sourceCount);
        _ = engine.Release(Src, Sess, IpA, O1, 1, 1);
        Assert.Equal(1, engine.OwnerSessionCount(Sess, O1, 0));
        Assert.True(engine.HasSourceOwner(Src, IpA, O1, 0));
        Assert.True(engine.TryGetOwnership(Src, SessionStateKeys.SourceField(IpA, O1), out _, out var sourceGeneration, out sourceCount));
        Assert.Equal(1, sourceGeneration);
        Assert.Equal(1, sourceCount);
    }

    [Fact]
    public void Engine_DualOwnershipCompensation_SessionCountTwoSourceCountOne_RemovesOnlyCanceledIp()
    {
        var engine = new SessionStateEngine();
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(Src, Sess, IpA, O1, 10, 4, 0, 30_000, 1, 1));
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(Src, Sess, IpB, O1, 10, 4, 0, 30_000, 1, 3));
        Assert.Equal(2, engine.OwnerSessionCount(Sess, O1, 0));
        Assert.True(engine.TryGetOwnership(Src, SessionStateKeys.SourceField(IpA, O1), out _, out _, out var ipACount));
        Assert.Equal(1, ipACount);
        _ = engine.Release(Src, Sess, IpA, O1, 1, 1);
        Assert.Equal(1, engine.OwnerSessionCount(Sess, O1, 0));
        Assert.False(engine.HasSourceOwner(Src, IpA, O1, 0));
        Assert.True(engine.HasSourceOwner(Src, IpB, O1, 0));
    }

    [Fact]
    public void Engine_DelayedCompensation_AfterReleaseOwner_DoesNotRecreateOrTouchNewIncarnation()
    {
        var engine = new SessionStateEngine();
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(Src, Sess, IpA, "nntpd01:old", 10, 4, 0, 30_000, 1, 1));
        _ = engine.ReleaseOwner(Src, Sess, "nntpd01:old");
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus(Src, Sess, IpA, "nntpd01:new", 10, 4, 0, 30_000, 2, 2));
        _ = engine.Release(Src, Sess, IpA, "nntpd01:old", 1, 1);
        Assert.Equal(1, engine.OwnerSessionCount(Sess, "nntpd01:new", 0));
        Assert.True(engine.HasSourceOwner(Src, IpA, "nntpd01:new", 0));
        Assert.False(engine.HasSourceOwner(Src, IpA, "nntpd01:old", 0));
        Assert.Equal(0, engine.OwnerSessionCount(Sess, "nntpd01:old", 0));
    }

    [Fact]
    public async Task Tracker_DifferentNodeAdmit_IsIndependentOfCompensation()
    {
        var membership = new InMemorySessionStateStore();
        var storeA = new CompensationStore(membership);
        var nodeA = CreateNode(storeA, "nntpd01", "incA");
        var nodeB = CreateNode(membership, "nntpd02", "incB");
        using var cts = new CancellationTokenSource();
        var admitA = nodeA.TryAdmitAsync("alice", "A", V4A, sessionLimit: 5, srcIpLimit: 2, cts.Token).AsTask();
        await storeA.Admitted.Task;
        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("alice", "B", V4A, 5, 2));
        cts.Cancel();
        storeA.Continue.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admitA);
        Assert.Equal(0, nodeA.GetLocalSessionCount("alice"));
        Assert.Equal(1, nodeB.GetLocalSessionCount("alice"));
        Assert.False(membership.HasSourceOwner("alice", IpA, nodeA.OwnerId, NowMs()));
        Assert.True(membership.HasSourceOwner("alice", IpA, nodeB.OwnerId, NowMs()));
        Assert.Equal(1, membership.OwnerSessionCount("alice", nodeB.OwnerId, NowMs()));
    }

    [Fact]
    public void ReleaseScript_RequiresOwnerAndGeneration()
    {
        var script = SessionStateScripts.Release;
        Assert.Contains("local function decrement(key, field, gen)", script, StringComparison.Ordinal);
        Assert.Contains("if storedGen ~= gen then", script, StringComparison.Ordinal);
        Assert.Contains("decrement(sessKey, owner, sessionGen)", script, StringComparison.Ordinal);
        Assert.Contains("decrement(srcKey, srcField, sourceGen)", script, StringComparison.Ordinal);
        Assert.Contains("ip .. '\\31' .. owner", script, StringComparison.Ordinal);
        Assert.DoesNotContain("HDEL', sessKey, owner", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedisStore_StaleGenerationRelease_LeavesReplacedEpoch()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var now = DateTimeOffset.UnixEpoch;
        var ttl = TimeSpan.FromSeconds(30);
        Assert.True((await store.TryAdmitAsync("alice", IpA, O1, 10, 4, 1, 1, now, ttl)).Accepted);
        Assert.True((await store.TryAdmitAsync("alice", IpA, O1, 10, 4, 2, 2, now, ttl)).Accepted);
        await store.ReleaseAsync("alice", IpA, O1, 1, 1);
        var engine = redis.Database.SessionStateEngine;
        var sessKey = Encoding.UTF8.GetString(SessionStateKeys.CreateSession("alice"));
        var srcKey = Encoding.UTF8.GetString(SessionStateKeys.CreateSource("alice"));
        Assert.True(engine.TryGetOwnership(sessKey, O1, out _, out var sessionGeneration, out var sessionCount));
        Assert.Equal(2, sessionGeneration);
        Assert.Equal(1, sessionCount);
        Assert.True(engine.HasSourceOwner(srcKey, IpA, O1, 0));
        Assert.True(engine.TryGetOwnership(srcKey, SessionStateKeys.SourceField(IpA, O1), out _, out var sourceGeneration, out var sourceCount));
        Assert.Equal(2, sourceGeneration);
        Assert.Equal(1, sourceCount);
    }

    [Fact]
    public async Task RedisStore_SameGenerationRelease_DecrementsWithoutDeletingOtherOwner()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var now = DateTimeOffset.UnixEpoch;
        var ttl = TimeSpan.FromSeconds(30);
        Assert.True((await store.TryAdmitAsync("alice", IpA, O1, 10, 4, 1, 1, now, ttl)).Accepted);
        Assert.True((await store.TryAdmitAsync("alice", IpA, O1, 10, 4, 1, 1, now, ttl)).Accepted);
        Assert.True((await store.TryAdmitAsync("alice", IpA, O2, 10, 4, 9, 9, now, ttl)).Accepted);
        await store.ReleaseAsync("alice", IpA, O1, 1, 1);
        var engine = redis.Database.SessionStateEngine;
        var sessKey = Encoding.UTF8.GetString(SessionStateKeys.CreateSession("alice"));
        var srcKey = Encoding.UTF8.GetString(SessionStateKeys.CreateSource("alice"));
        Assert.Equal(1, engine.OwnerSessionCount(sessKey, O1, 0));
        Assert.Equal(1, engine.OwnerSessionCount(sessKey, O2, 0));
        Assert.True(engine.HasSourceOwner(srcKey, IpA, O1, 0));
        Assert.True(engine.HasSourceOwner(srcKey, IpA, O2, 0));
        await store.ReleaseAsync("alice", IpA, O1, 1, 1);
        Assert.Equal(0, engine.OwnerSessionCount(sessKey, O1, 0));
        Assert.False(engine.HasSourceOwner(srcKey, IpA, O1, 0));
        Assert.Equal(1, engine.OwnerSessionCount(sessKey, O2, 0));
        Assert.True(engine.HasSourceOwner(srcKey, IpA, O2, 0));
    }

    [Fact]
    public void LuaDecrementRule_StaleGenerationIsANoOp()
    {
        var session = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [O1] = SessionStateKeys.Value(30_000, 2, 1),
        };
        var source = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SessionStateKeys.SourceField(IpA, O1)] = SessionStateKeys.Value(30_000, 2, 1),
            [SessionStateKeys.SourceField(IpA, O2)] = SessionStateKeys.Value(30_000, 9, 3),
        };
        ApplyLuaDecrement(session, O1, 1);
        ApplyLuaDecrement(source, SessionStateKeys.SourceField(IpA, O1), 1);
        Assert.True(session.TryGetValue(O1, out var sessionValue));
        Assert.True(SessionStateKeys.TrySplitValue(sessionValue, out _, out var sessionGeneration, out var sessionCount));
        Assert.Equal(2, sessionGeneration);
        Assert.Equal(1, sessionCount);
        Assert.True(source.ContainsKey(SessionStateKeys.SourceField(IpA, O1)));
        Assert.True(source.ContainsKey(SessionStateKeys.SourceField(IpA, O2)));
    }

    [Fact]
    public async Task DelayedCompensation_AfterReleaseOwner_LeavesNewIncarnation()
    {
        var membership = new InMemorySessionStateStore();
        var crashed = CreateNode(membership, "nntpd01", "old");
        Assert.Equal(SessionAdmissionResult.Success, await crashed.TryAdmitAsync("alice", "A", V4A, 5, 2));
        Assert.True(membership.Engine.TryGetOwnership(
            InMemorySessionStateStore.SessionKey("alice"),
            crashed.OwnerId,
            out _,
            out var staleSessionGeneration,
            out _));
        Assert.True(membership.Engine.TryGetOwnership(
            InMemorySessionStateStore.SourceKey("alice"),
            SessionStateKeys.SourceField(IpA, crashed.OwnerId),
            out _,
            out var staleSourceGeneration,
            out _));
        await crashed.ReleaseAllOwnershipAsync();
        var restarted = CreateNode(membership, "nntpd01", "new");
        Assert.Equal(SessionAdmissionResult.Success, await restarted.TryAdmitAsync("alice", "B", V4A, 5, 2));
        await membership.ReleaseAsync("alice", IpA, crashed.OwnerId, staleSessionGeneration, staleSourceGeneration);
        Assert.Equal(1, membership.OwnerSessionCount("alice", restarted.OwnerId, NowMs()));
        Assert.True(membership.HasSourceOwner("alice", IpA, restarted.OwnerId, NowMs()));
        Assert.Equal(0, membership.OwnerSessionCount("alice", crashed.OwnerId, NowMs()));
        Assert.False(membership.HasSourceOwner("alice", IpA, crashed.OwnerId, NowMs()));
    }

    private static void ApplyLuaDecrement(Dictionary<string, string> hash, string field, long generation)
    {
        var gen = generation.ToString(CultureInfo.InvariantCulture);
        if (!hash.TryGetValue(field, out var current)
            || !SessionStateKeys.TrySplitValue(current, out var expiry, out var storedGeneration, out var count)
            || storedGeneration.ToString(CultureInfo.InvariantCulture) != gen)
        {
            return;
        }

        if (count <= 1)
        {
            hash.Remove(field);
            return;
        }

        hash[field] = SessionStateKeys.Value(expiry, storedGeneration, count - 1);
    }

    private static DistributedSessionStateTracker CreateNode(
        ISessionStateStore store,
        string nodeId = "nntpd01",
        string? incarnation = null) =>
        new(
            store,
            NullLogger<DistributedSessionStateTracker>.Instance,
            nodeId,
            TimeProvider.System,
            incarnation: incarnation);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class CompensationStore : ISessionStateStore
    {
        private readonly InMemorySessionStateStore _inner;

        public CompensationStore(InMemorySessionStateStore inner)
        {
            _inner = inner;
        }

        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool UnavailableOnRelease { get; set; }

        public Exception? ThrowAfterAdmit { get; set; }

        public int ReleaseCalls { get; private set; }

        public CancellationToken LastReleaseToken { get; private set; } = new(canceled: true);

        public string? LastReleaseOwnerId { get; private set; }

        public string? LastReleaseIp { get; private set; }

        public long LastReleaseSessionGeneration { get; private set; }

        public long LastReleaseSourceGeneration { get; private set; }

        public async ValueTask<SessionStateAdmitResult> TryAdmitAsync(
            string accountName,
            string normalizedSourceIp,
            string ownerId,
            int sessionLimit,
            int srcIpLimit,
            long sessionGeneration,
            long sourceGeneration,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default,
            bool trackSessions = false)
        {
            var result = await _inner.TryAdmitAsync(
                accountName,
                normalizedSourceIp,
                ownerId,
                sessionLimit,
                srcIpLimit,
                sessionGeneration,
                sourceGeneration,
                now,
                leaseTtl,
                CancellationToken.None,
                trackSessions).ConfigureAwait(false);
            Admitted.TrySetResult();
            if (ThrowAfterAdmit is { } fault)
            {
                throw fault;
            }

            await Continue.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        public ValueTask<int> ReleaseAsync(
            string accountName,
            string normalizedSourceIp,
            string ownerId,
            long sessionGeneration,
            long sourceGeneration,
            CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            LastReleaseToken = cancellationToken;
            LastReleaseOwnerId = ownerId;
            LastReleaseIp = normalizedSourceIp;
            LastReleaseSessionGeneration = sessionGeneration;
            LastReleaseSourceGeneration = sourceGeneration;
            if (UnavailableOnRelease)
            {
                _inner.Unavailable = true;
            }

            return _inner.ReleaseAsync(
                accountName,
                normalizedSourceIp,
                ownerId,
                sessionGeneration,
                sourceGeneration,
                cancellationToken);
        }

        public ValueTask<SessionStateRenewResult> RenewAsync(
            string accountName,
            string ownerId,
            long sessionGeneration,
            IReadOnlyList<(string Ip, long Generation)> sources,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default) =>
            _inner.RenewAsync(accountName, ownerId, sessionGeneration, sources, now, leaseTtl, cancellationToken);

        public ValueTask<SessionStateRenewAndApplyResult> RenewAndApplyAsync(
            string accountName,
            string ownerId,
            long sessionGeneration,
            IReadOnlyList<(string Ip, long Generation)> sources,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            string batchId,
            long consumed,
            long mysqlRemainingAfter,
            CancellationToken cancellationToken = default) =>
            _inner.RenewAndApplyAsync(
                accountName,
                ownerId,
                sessionGeneration,
                sources,
                now,
                leaseTtl,
                batchId,
                consumed,
                mysqlRemainingAfter,
                cancellationToken);

        public ValueTask ReleaseOwnerAsync(
            string accountName,
            string ownerId,
            CancellationToken cancellationToken = default) =>
            _inner.ReleaseOwnerAsync(accountName, ownerId, cancellationToken);
    }
}
