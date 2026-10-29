using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.SessionState;

/// <summary>
/// Lease liveness: Redis TTL is crash recovery, not client idle. Renewal is
/// independent of NNTP traffic and aggregated per account.
/// </summary>
public sealed class AdmissionLeaseLivenessTests
{
    private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress V4B = IPAddress.Parse("198.51.100.20");
    private static readonly IPAddress V6A = IPAddress.Parse("2001:db8::10");

    [Fact]
    public async Task IdleAuthenticatedSessions_RemainOwnedAcrossMultipleLeaseIntervals()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership, clock);
        var peer = CreateNode("nntpd02", membership, clock);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(
                SessionAdmissionResult.Success,
                await Admit(node, $"s{i}", V4A, sessionLimit: 10, srcIpLimit: 2));
        }

        var previous = Require(membership.SessionExpiry("alice", node.OwnerId));
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await node.RenewLeasesAsync();
            var next = Require(membership.SessionExpiry("alice", node.OwnerId));
            Assert.True(next > previous);
            previous = next;
        }

        Assert.Equal(10, membership.ActiveSessionCount("alice", clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.Equal(10, membership.OwnerSessionCount("alice", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.True(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.Equal(
            SessionAdmissionResult.SessionLimitExceeded,
            await Admit(peer, "extra", V6A, sessionLimit: 10, srcIpLimit: 2));
    }

    [Fact]
    public async Task RenewalService_ExtendsIdleOwnershipWithoutClientTraffic()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership, clock, leaseTtl: TimeSpan.FromMinutes(5));
        var peer = CreateNode("nntpd02", membership, clock, leaseTtl: TimeSpan.FromMinutes(5));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 1, srcIpLimit: 1));
        var previous = Require(membership.SessionExpiry("alice", node.OwnerId));
        var leases = new CountingLeaseManager(node);
        var service = new SessionStateService(
            leases,
            NullLogger<SessionStateService>.Instance,
            clock,
            TimeSpan.FromSeconds(10));
        await service.StartAsync(CancellationToken.None);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref leases.Renewals) < 3
            || membership.SessionExpiry("alice", node.OwnerId) is not { } current
            || current <= previous)
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

        Assert.True(membership.SessionExpiry("alice", node.OwnerId) > previous);
        Assert.Equal(1, membership.ActiveSessionCount("alice", clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.Equal(
            SessionAdmissionResult.SessionLimitExceeded,
            await Admit(peer, "extra", V6A, sessionLimit: 1, srcIpLimit: 1));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Renewal_MovesExpiryForwardFromEachSuccessfulRenewal()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 1, srcIpLimit: 1));

        var admitExpiry = Require(membership.SessionExpiry("alice", node.OwnerId));
        Assert.Equal(30_000, admitExpiry);
        Assert.Equal(admitExpiry, membership.SourceExpiry("alice", "192.0.2.10", node.OwnerId));

        clock.Advance(TimeSpan.FromSeconds(10));
        await node.RenewLeasesAsync();
        var firstRenew = Require(membership.SessionExpiry("alice", node.OwnerId));
        Assert.Equal(40_000, firstRenew);
        Assert.Equal(firstRenew, membership.SourceExpiry("alice", "192.0.2.10", node.OwnerId));

        clock.Advance(TimeSpan.FromSeconds(10));
        await node.RenewLeasesAsync();
        var secondRenew = Require(membership.SessionExpiry("alice", node.OwnerId));
        Assert.Equal(50_000, secondRenew);
        Assert.True(secondRenew > firstRenew);
        Assert.Equal(1, membership.ActiveSessionCount("alice", clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task CrashWithoutTeardown_ExpiresOwnershipAndFreesCapacity()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var crashed = CreateNode("nntpd01", membership, clock);
        var live = CreateNode("nntpd02", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(crashed, "held", V4A, sessionLimit: 1, srcIpLimit: 1));
        Assert.Equal(
            SessionAdmissionResult.SessionLimitExceeded,
            await Admit(live, "wait", V6A, sessionLimit: 1, srcIpLimit: 1));

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(0, membership.ActiveSessionCount("alice", clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(live, "take", V6A, sessionLimit: 1, srcIpLimit: 1));
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", crashed.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.True(membership.HasSourceOwner("alice", "2001:db8::10", live.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.Equal(1, crashed.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task RenewalFailure_DoesNotExtendOrRecreateOwnership()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 1, srcIpLimit: 1));
        var original = Require(membership.SessionExpiry("alice", node.OwnerId));

        membership.Unavailable = true;
        clock.Advance(TimeSpan.FromSeconds(10));
        await node.RenewLeasesAsync();
        Assert.Equal(original, membership.SessionExpiry("alice", node.OwnerId));

        clock.Advance(TimeSpan.FromSeconds(21));
        await node.RenewLeasesAsync();
        Assert.Equal(original, membership.SessionExpiry("alice", node.OwnerId));
        Assert.Equal(0, membership.ActiveSessionCount("alice", clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.Equal(
            SessionAdmissionResult.Unavailable,
            await Admit(node, "s2", V4A, sessionLimit: 1, srcIpLimit: 1));
        Assert.Equal(1, node.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task LostLease_IsNotRecreatedByRenewal()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership, clock);
        var peer = CreateNode("nntpd02", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 1, srcIpLimit: 1));
        await membership.ReleaseOwnerAsync("alice", node.OwnerId);
        Assert.Null(membership.SessionExpiry("alice", node.OwnerId));

        await node.RenewLeasesAsync();
        Assert.Null(membership.SessionExpiry("alice", node.OwnerId));
        Assert.Null(membership.SourceExpiry("alice", "192.0.2.10", node.OwnerId));
        Assert.Equal(1, node.GetLocalSessionCount("alice"));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(peer, "take", V6A, sessionLimit: 1, srcIpLimit: 1));
    }

    [Fact]
    public async Task PartialRenewal_ExtendsNoneOfTheRequestedOwnership()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "v4", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "v6", V6A, sessionLimit: 5, srcIpLimit: 2));
        var sessionExpiry = Require(membership.SessionExpiry("alice", node.OwnerId));
        var v4Expiry = Require(membership.SourceExpiry("alice", "192.0.2.10", node.OwnerId));
        var v6Expiry = Require(membership.SourceExpiry("alice", "2001:db8::10", node.OwnerId));

        membership.BreakSourceGeneration("alice", "2001:db8::10", node.OwnerId);
        clock.Advance(TimeSpan.FromSeconds(10));
        await node.RenewLeasesAsync();

        Assert.Equal(sessionExpiry, membership.SessionExpiry("alice", node.OwnerId));
        Assert.Equal(v4Expiry, membership.SourceExpiry("alice", "192.0.2.10", node.OwnerId));
        Assert.Equal(v6Expiry, membership.SourceExpiry("alice", "2001:db8::10", node.OwnerId));
    }

    [Fact]
    public async Task RenewalFaultForOneAccount_DoesNotCorruptAnotherAccount()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "a1", V4A, "alice", sessionLimit: 2, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "b1", V6A, "bob", sessionLimit: 2, srcIpLimit: 2));
        var aliceExpiry = Require(membership.SessionExpiry("alice", node.OwnerId));
        var bobExpiry = Require(membership.SessionExpiry("bob", node.OwnerId));

        membership.FaultRenewAccount = "alice";
        clock.Advance(TimeSpan.FromSeconds(10));
        await node.RenewLeasesAsync();

        Assert.Equal(aliceExpiry, membership.SessionExpiry("alice", node.OwnerId));
        Assert.True(membership.SessionExpiry("bob", node.OwnerId) > bobExpiry);
        Assert.Equal(1, membership.OwnerSessionCount("alice", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.Equal(1, membership.OwnerSessionCount("bob", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task NodesRenewIndependently_StoppedNodeExpiresAlone()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node1 = CreateNode("nntpd01", membership, clock);
        var node2 = CreateNode("nntpd02", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node1, "a1", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node2, "b1", V6A, sessionLimit: 5, srcIpLimit: 2));

        clock.Advance(TimeSpan.FromSeconds(10));
        await node1.RenewLeasesAsync();
        await node2.RenewLeasesAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await node2.RenewLeasesAsync();
        clock.Advance(TimeSpan.FromSeconds(21));

        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        Assert.Equal(0, membership.OwnerSessionCount("alice", node1.OwnerId, now));
        Assert.Equal(1, membership.OwnerSessionCount("alice", node2.OwnerId, now));
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", node1.OwnerId, now));
        Assert.True(membership.HasSourceOwner("alice", "2001:db8::10", node2.OwnerId, now));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node2, "b2", V4B, sessionLimit: 5, srcIpLimit: 2));
    }

    [Fact]
    public async Task PartialSessionRelease_KeepsRemainingOwnershipOnRenewal()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s3", V6A, sessionLimit: 5, srcIpLimit: 2));
        await node.ReleaseAsync("alice", "s1");
        Assert.Equal(2, membership.OwnerSessionCount("alice", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));

        clock.Advance(TimeSpan.FromSeconds(10));
        await node.RenewLeasesAsync();
        Assert.Equal(2, membership.OwnerSessionCount("alice", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.True(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.True(membership.HasSourceOwner("alice", "2001:db8::10", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    [Fact]
    public void EngineRenew_DoesNotRecreateMissingFields()
    {
        var engine = new SessionStateEngine();
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus("src", "sess", "192.0.2.10", "nntpd01:a", 1, 1, 0, 30_000, 1, 1));
        _ = engine.ReleaseOwner("src", "sess", "nntpd01:a");
        Assert.Equal(0, engine.Renew("src", "sess", "nntpd01:a", 1, 10_000, 30_000, [("192.0.2.10", 1)]));
        Assert.False(engine.TryGetOwnership("sess", "nntpd01:a", out _, out _, out _));
        Assert.False(engine.HasSourceOwner("src", "192.0.2.10", "nntpd01:a", 10_000));
    }

    private static long Require(long? expiry) =>
        expiry ?? throw new InvalidOperationException("Expected ownership expiry.");

    private static ValueTask<SessionAdmissionResult> Admit(
        DistributedSessionStateTracker node,
        string sessionId,
        IPAddress ip,
        int sessionLimit = 0,
        int srcIpLimit = 0) =>
        Admit(node, sessionId, ip, "alice", sessionLimit, srcIpLimit);

    private static ValueTask<SessionAdmissionResult> Admit(
        DistributedSessionStateTracker node,
        string sessionId,
        IPAddress ip,
        string account,
        int sessionLimit = 0,
        int srcIpLimit = 0) =>
        node.TryAdmitAsync(account, sessionId, ip, sessionLimit, srcIpLimit);

    private static DistributedSessionStateTracker CreateNode(
        string nodeId,
        InMemorySessionStateStore membership,
        TimeProvider? time = null,
        TimeSpan? leaseTtl = null) =>
        new(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            nodeId,
            time ?? TimeProvider.System,
            leaseTtl: leaseTtl);

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
