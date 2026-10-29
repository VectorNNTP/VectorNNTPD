using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.SessionState;

public sealed class SessionStateServiceTests
{
    private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress V4B = IPAddress.Parse("198.51.100.20");
    private static readonly IPAddress V6A = IPAddress.Parse("2001:db8::10");

    [Fact]
    public void Types_UseSessionStateNamespace()
    {
        Assert.Equal("VectorNNTPD.ReaderService.SessionState", typeof(SessionStateService).Namespace);
        Assert.Equal("VectorNNTPD.ReaderService.SessionState", typeof(ISessionStateLeaseManager).Namespace);
        Assert.Equal("VectorNNTPD.ReaderService.SessionState", typeof(ISessionStateTracker).Namespace);
        Assert.Equal("VectorNNTPD.ReaderService.SessionState", typeof(DistributedSessionStateTracker).Namespace);
        Assert.Equal("VectorNNTPD.ReaderService.SessionState", typeof(InMemorySessionStateTracker).Namespace);
        Assert.Equal("VectorNNTPD.ReaderService.SessionState", typeof(SessionAdmissionResult).Namespace);
        Assert.Equal("VectorNNTPD.ReaderService.SessionState", typeof(SourceAddressIdentity).Namespace);
    }

    [Fact]
    public async Task Start_RenewsThenStopReleasesOwnership()
    {
        var leases = new FakeLeaseManager();
        var clock = new ControllableTimeProvider();
        var service = new SessionStateService(
            leases,
            NullLogger<SessionStateService>.Instance,
            clock,
            TimeSpan.FromSeconds(10));

        await service.StartAsync(CancellationToken.None);
        Assert.Equal("SessionState", service.Name);
        Assert.NotNull(service.Execution);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref leases.Renewals) < 3)
        {
            wait.Token.ThrowIfCancellationRequested();
            if (!clock.HasScheduledTimers)
            {
                await Task.Yield();
                continue;
            }

            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();
        }

        await service.StopAsync(CancellationToken.None);
        Assert.True(leases.Renewals >= 3);
        Assert.Equal(1, leases.ReleaseAll);
    }

    [Fact]
    public async Task Start_IsIdempotent()
    {
        var leases = new FakeLeaseManager();
        var service = new SessionStateService(
            leases,
            NullAccountByteAccountant.Instance,
            NullLogger<SessionStateService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(1, leases.ReleaseAll);
    }

    [Fact]
    public async Task OneAccountMultipleSessionsAndIps_OneRedisEvalPerCycle()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var node = CreateNode(store);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s3", V6A));
        var afterAdmit = redis.Database.ScriptEvaluateCount;

        await node.RenewLeasesAsync();
        Assert.Equal(afterAdmit + 1, redis.Database.ScriptEvaluateCount);

        await node.RenewLeasesAsync();
        Assert.Equal(afterAdmit + 2, redis.Database.ScriptEvaluateCount);
    }

    [Fact]
    public async Task MultipleAccounts_EachGetsOneRedisEval()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var node = CreateNode(store);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "a1", V4A, "alice"));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "b1", V6A, "bob"));
        var afterAdmit = redis.Database.ScriptEvaluateCount;

        await node.RenewLeasesAsync();
        Assert.Equal(afterAdmit + 2, redis.Database.ScriptEvaluateCount);
    }

    [Fact]
    public async Task AfterSessionRelease_RemainingOwnershipStillRenews()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4B));
        await node.ReleaseAsync("alice", "s1");
        var before = membership.RenewCalls;

        await node.RenewLeasesAsync();
        Assert.Equal(before + 1, membership.RenewCalls);
        Assert.Equal(1, membership.OwnerSessionCount("alice", node.OwnerId, NowMs()));
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));
        Assert.True(membership.HasSourceOwner("alice", "198.51.100.20", node.OwnerId, NowMs()));
    }

    [Fact]
    public async Task LastSessionRelease_StopsAccountRenewal()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A));
        await node.ReleaseAsync("alice", "s1");
        var before = membership.RenewCalls;

        await node.RenewLeasesAsync();
        Assert.Equal(before, membership.RenewCalls);
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task ServiceRenewal_DoesNotResurrectReleasedSession()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4B));
        await node.ReleaseAsync("alice", "s1");

        var leases = new CountingLeaseManager(node);
        var service = new SessionStateService(
            leases,
            NullLogger<SessionStateService>.Instance,
            clock,
            TimeSpan.FromSeconds(10));
        await service.StartAsync(CancellationToken.None);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref leases.Renewals) < 1)
        {
            wait.Token.ThrowIfCancellationRequested();
            if (!clock.HasScheduledTimers)
            {
                await Task.Yield();
                continue;
            }

            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();
        }

        Assert.Equal(1, membership.OwnerSessionCount("alice", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.True(membership.HasSourceOwner("alice", "198.51.100.20", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RedisUnavailableDuringRenew_DoesNotRecreateOwnership()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var node = CreateNode(store);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A));
        await node.ReleaseAllOwnershipAsync();
        redis.IsUnavailable = true;

        await node.RenewLeasesAsync();
        redis.Recover();
        Assert.Equal(
            SessionAdmissionResult.Success,
            await Admit(CreateNode(store, incarnation: "other"), "take", V6A));
    }

    private static DistributedSessionStateTracker CreateNode(
        ISessionStateStore membership,
        TimeProvider? time = null,
        string? incarnation = null) =>
        new(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01",
            time ?? TimeProvider.System,
            incarnation: incarnation);

    private static ValueTask<SessionAdmissionResult> Admit(
        DistributedSessionStateTracker node,
        string sessionId,
        IPAddress ip,
        string account = "alice") =>
        node.TryAdmitAsync(account, sessionId, ip, sessionLimit: 5, srcIpLimit: 3);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class FakeLeaseManager : ISessionStateLeaseManager
    {
        public int Renewals;
        public int ReleaseAll;

        public ValueTask RenewLeasesAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Renewals);
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseAllOwnershipAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ReleaseAll);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingLeaseManager : ISessionStateLeaseManager
    {
        private readonly DistributedSessionStateTracker _inner;

        public CountingLeaseManager(DistributedSessionStateTracker inner)
        {
            _inner = inner;
        }

        public int Renewals;

        public ValueTask RenewLeasesAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Renewals);
            return _inner.RenewLeasesAsync(cancellationToken);
        }

        public ValueTask ReleaseAllOwnershipAsync(CancellationToken cancellationToken = default) =>
            _inner.ReleaseAllOwnershipAsync(cancellationToken);
    }
}
