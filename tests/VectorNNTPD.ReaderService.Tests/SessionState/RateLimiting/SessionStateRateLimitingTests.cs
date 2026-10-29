using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.Common.Networking.RateLimiting;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.ReaderService.SessionState.RateLimiting;
namespace VectorNNTPD.ReaderService.Tests.SessionState.RateLimiting;

public sealed class SessionStateRateLimitingTests
{
    private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress V4B = IPAddress.Parse("198.51.100.20");
    /// <summary>Database <c>account_rate_limit</c> for 10 Mbps: 10,000,000 bits/sec.</summary>
    private const int TenMbpsBps = 10_000_000;

    [Fact]
    public void Policy_RateZero_IsUnlimitedAndDoesNotRequireTracking()
    {
        var policy = new NntpAccountPolicy("alice", 0, 0, 10, 0, "c");
        Assert.False(policy.RequiresRateTracking);
        Assert.True(policy.RequiresAdmission);
        Assert.Equal(0, policy.RateLimitBps);
        Assert.Equal(0, policy.ByteLimit);
    }

    [Fact]
    public void Policy_PositiveRate_RequiresTrackingRegardlessOfByteLimit()
    {
        var policy = new NntpAccountPolicy("alice", 10, 100, 10, 0, "c");
        Assert.True(policy.RequiresRateTracking);
        Assert.Equal(10, policy.RateLimitBps);
        Assert.Equal(100, policy.ByteLimit);
    }

    [Fact]
    public void Policy_PositiveRate_RequiresAdmissionWhenSessionLimitsAreZero()
    {
        var policy = new NntpAccountPolicy("alice", 240, 999, 0, 0, "c");
        Assert.True(policy.RequiresRateTracking);
        Assert.True(policy.RequiresAdmission);
    }

    [Fact]
    public void DatabaseAccount_240Bps_OneSession_LimiterCapIs30()
    {
        var rates = new AccountRateAllocator();
        var cap = new FakeCap();
        rates.ObserveClusterSessionCount("alice", 1);
        rates.Register("alice", "s1", cap, 240);
        Assert.Equal(30, cap.MaxSendBytesPerSecond);
        Assert.NotEqual(30_000_000, cap.MaxSendBytesPerSecond);
    }

    [Fact]
    public void Allocator_PositiveSubEightBps_IsBlocked()
    {
        var rates = new AccountRateAllocator();
        var cap = new FakeCap();
        rates.ObserveClusterSessionCount("alice", 1);
        rates.Register("alice", "s1", cap, 7);
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, cap.MaxSendBytesPerSecond);
    }

    [Fact]
    public void Allocator_TenMillionBps_TenSessions_Is125000Each()
    {
        var rates = new AccountRateAllocator();
        var caps = Register(rates, "alice", 10_000_000, 10);
        Assert.Equal(1_250_000, AccountRateFormula.AccountBytesPerSecond(10_000_000));
        AssertCaps(caps, 125_000);
    }

    [Fact]
    public void Allocator_TenMillionBps_SevenSessions_Is178571Each()
    {
        var rates = new AccountRateAllocator();
        var caps = Register(rates, "alice", 10_000_000, 7);
        AssertCaps(caps, 178_571);
    }

    [Fact]
    public void Allocator_TenThenSevenThenEight_UpdatesExistingCaps()
    {
        var rates = new AccountRateAllocator();
        var caps = Register(rates, "alice", TenMbpsBps, 10);
        AssertCaps(caps, AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 10));

        Unregister(rates, "alice", caps, 3);
        rates.ObserveClusterSessionCount("alice", 7);
        AssertCaps(caps.Take(7), AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 7));

        var extra = new FakeCap();
        rates.Register("alice", "s10", extra, TenMbpsBps);
        rates.ObserveClusterSessionCount("alice", 8);
        Assert.Equal(AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 8), extra.MaxSendBytesPerSecond);
        AssertCaps(caps.Take(7).Append(extra), AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 8));
        Assert.True(AccountRateFormula.AggregateDoesNotExceed(TenMbpsBps, 8));
    }

    [Fact]
    public void Allocator_OneSession_GetsFullAccountRate()
    {
        var rates = new AccountRateAllocator();
        var cap = new FakeCap();
        rates.ObserveClusterSessionCount("alice", 1);
        rates.Register("alice", "s1", cap, TenMbpsBps);
        Assert.Equal(1_250_000, cap.MaxSendBytesPerSecond);
    }

    [Fact]
    public void Allocator_ZeroSessions_DoesNotCreatePhantomCap()
    {
        var rates = new AccountRateAllocator();
        rates.ObserveClusterSessionCount("alice", 0);
        Assert.Equal(0, rates.LocalSessionCount("alice"));
        var cap = new FakeCap();
        rates.Register("alice", "s1", cap, TenMbpsBps);
        rates.ObserveClusterSessionCount("alice", 1);
        Assert.Equal(1_250_000, cap.MaxSendBytesPerSecond);
    }

    [Fact]
    public void Allocator_RateZero_LeavesUnlimitedCap()
    {
        var rates = new AccountRateAllocator();
        var cap = new FakeCap();
        rates.Register("alice", "s1", cap, 0);
        Assert.Equal(0, cap.MaxSendBytesPerSecond);
        Assert.Equal(0, rates.LocalSessionCount("alice"));
    }

    [Fact]
    public async Task Distributed_ExistingLocalSessionsUpdateOnJoinAndLeave()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var caps = new FakeCap[10];
        for (var i = 0; i < 10; i++)
        {
            caps[i] = new FakeCap();
            Assert.Equal(
                SessionAdmissionResult.Success,
                await node.TryAdmitAsync("alice", "s" + i, V4A, sessionLimit: 10, srcIpLimit: 0, TenMbpsBps));
            rates.Register("alice", "s" + i, caps[i], TenMbpsBps);
        }

        AssertCaps(caps, 125_000);

        for (var i = 7; i < 10; i++)
        {
            rates.Unregister("alice", "s" + i);
            await node.ReleaseAsync("alice", "s" + i);
        }

        AssertCaps(caps.Take(7), AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 7));

        var extra = new FakeCap();
        Assert.Equal(
            SessionAdmissionResult.Success,
            await node.TryAdmitAsync("alice", "s10", V4A, 10, 0, TenMbpsBps));
        rates.Register("alice", "s10", extra, TenMbpsBps);
        Assert.Equal(AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 8), extra.MaxSendBytesPerSecond);
        AssertCaps(caps.Take(7).Append(extra), AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 8));
    }

    [Fact]
    public async Task Distributed_UsesActiveCount_NotSessionLimit()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var first = new FakeCap();
        var second = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s1", V4A, 10, 0, TenMbpsBps));
        rates.Register("alice", "s1", first, TenMbpsBps);
        Assert.Equal(1_250_000, first.MaxSendBytesPerSecond);
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s2", V4B, 10, 0, TenMbpsBps));
        rates.Register("alice", "s2", second, TenMbpsBps);
        Assert.Equal(625_000, first.MaxSendBytesPerSecond);
        Assert.Equal(625_000, second.MaxSendBytesPerSecond);
    }

    [Fact]
    public async Task Distributed_SessionLimitExceeded_DoesNotChangeExistingCaps()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var caps = await RegisterAdmitted(rates, node, 10);
        Assert.Equal(
            SessionAdmissionResult.SessionLimitExceeded,
            await node.TryAdmitAsync("alice", "overflow", V4A, 10, 0, TenMbpsBps));
        AssertCaps(caps, 125_000);
        Assert.Equal(10, node.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task Distributed_RateOnly_TracksSessionsWhenSessionLimitIsZero()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var first = new FakeCap();
        var second = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s1", V4A, 0, 0, TenMbpsBps));
        rates.Register("alice", "s1", first, TenMbpsBps);
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s2", V4A, 0, 0, TenMbpsBps));
        rates.Register("alice", "s2", second, TenMbpsBps);
        Assert.Equal(625_000, first.MaxSendBytesPerSecond);
        Assert.Equal(625_000, second.MaxSendBytesPerSecond);
        Assert.Equal(0, node.LocalHotPathAdmits);
        Assert.Equal(2, node.DistributedAdmits);
    }

    [Fact]
    public async Task MultiNode_OtherNodeConvergesOnRenew()
    {
        var membership = new InMemorySessionStateStore();
        var ratesA = new AccountRateAllocator();
        var ratesB = new AccountRateAllocator();
        var nodeA = CreateNode(membership, "nntpd01", ratesA);
        var nodeB = CreateNode(membership, "nntpd02", ratesB);
        var a1 = new FakeCap();
        var a2 = new FakeCap();
        var a3 = new FakeCap();
        var b1 = new FakeCap();
        var b2 = new FakeCap();

        Assert.Equal(SessionAdmissionResult.Success, await nodeA.TryAdmitAsync("alice", "a1", V4A, 10, 0, TenMbpsBps));
        ratesA.Register("alice", "a1", a1, TenMbpsBps);
        Assert.Equal(SessionAdmissionResult.Success, await nodeA.TryAdmitAsync("alice", "a2", V4A, 10, 0, TenMbpsBps));
        ratesA.Register("alice", "a2", a2, TenMbpsBps);
        Assert.Equal(SessionAdmissionResult.Success, await nodeA.TryAdmitAsync("alice", "a3", V4A, 10, 0, TenMbpsBps));
        ratesA.Register("alice", "a3", a3, TenMbpsBps);
        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("alice", "b1", V4B, 10, 0, TenMbpsBps));
        ratesB.Register("alice", "b1", b1, TenMbpsBps);
        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("alice", "b2", V4B, 10, 0, TenMbpsBps));
        ratesB.Register("alice", "b2", b2, TenMbpsBps);

        var three = AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 3);
        var five = AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 5);
        AssertCaps([a1, a2, a3], three);
        Assert.True(Allocated(b1, b2) + Allocated(a1, a2, a3) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));
        Assert.True(Allocated(b1, b2) < five);

        await nodeA.RenewLeasesAsync();
        AssertCaps([a1, a2, a3], five);
        Assert.True(Allocated(a1, a2, a3, b1, b2) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));

        ratesB.Unregister("alice", "b2");
        await nodeB.ReleaseAsync("alice", "b2");
        AssertCaps([a1, a2, a3], five);
        Assert.True(Allocated(a1, a2, a3, b1) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));

        await nodeA.RenewLeasesAsync();
        AssertCaps([a1, a2, a3], five);
        Assert.True(Allocated(a1, a2, a3, b1) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));
    }

    [Fact]
    public async Task MultiNode_JoinOnOtherNode_UpdatesLocalCapsOnRenew()
    {
        var membership = new InMemorySessionStateStore();
        var ratesA = new AccountRateAllocator();
        var ratesB = new AccountRateAllocator();
        var nodeA = CreateNode(membership, "nntpd01", ratesA);
        var nodeB = CreateNode(membership, "nntpd02", ratesB);
        var a1 = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await nodeA.TryAdmitAsync("alice", "a1", V4A, 10, 0, TenMbpsBps));
        ratesA.Register("alice", "a1", a1, TenMbpsBps);
        Assert.Equal(1_250_000, a1.MaxSendBytesPerSecond);

        var b1 = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("alice", "b1", V4B, 10, 0, TenMbpsBps));
        ratesB.Register("alice", "b1", b1, TenMbpsBps);
        Assert.Equal(1_250_000, a1.MaxSendBytesPerSecond);
        Assert.True(Allocated(b1) <= 3);
        Assert.True(Allocated(a1, b1) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));

        await nodeA.RenewLeasesAsync();
        Assert.Equal(625_000, a1.MaxSendBytesPerSecond);
        Assert.True(Allocated(a1, b1) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));
    }

    [Fact]
    public async Task CrossNodeJoin_TenMillionBps_DoesNotExceedAggregateBeforeRemoteReconcile()
    {
        var clock = new ManualClock();
        var membership = new InMemorySessionStateStore();
        var ratesA = new AccountRateAllocator(clock);
        var ratesB = new AccountRateAllocator(clock);
        var nodeA = CreateNode(membership, "nntpd01", ratesA);
        var nodeB = CreateNode(membership, "nntpd02", ratesB);

        var aCaps = new FakeCap[7];
        for (var i = 0; i < 7; i++)
        {
            aCaps[i] = new FakeCap();
            Assert.Equal(
                SessionAdmissionResult.Success,
                await nodeA.TryAdmitAsync("alice", "a" + i, V4A, 10, 0, TenMbpsBps));
            ratesA.Register("alice", "a" + i, aCaps[i], TenMbpsBps);
        }

        var seven = AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 7);
        AssertCaps(aCaps, seven);

        var b1 = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("alice", "b1", V4B, 10, 0, TenMbpsBps));
        ratesB.Register("alice", "b1", b1, TenMbpsBps);
        AssertCaps(aCaps, seven);
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, b1.MaxSendBytesPerSecond);
        Assert.True(Allocated(aCaps) + Allocated(b1) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));

        await nodeA.RenewLeasesAsync();
        var eight = AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 8);
        AssertCaps(aCaps, eight);
        Assert.True(Allocated(aCaps) + Allocated(b1) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));

        clock.Advance(SessionStateDefaults.LeaseTtl);
        ratesB.ObserveClusterSessionCount("alice", 8);
        Assert.True(b1.MaxSendBytesPerSecond <= 0);
        AssertCaps(aCaps, eight);
        Assert.True(Allocated(aCaps) + Allocated(b1) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));
    }

    [Fact]
    public async Task ExactScenario_TenThenSevenThenEight_LocalAndTwoNodes()
    // codeql[cs/complex-block] Exact multi-node rate scenario keeps steps inline for readability.
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var caps = await RegisterAdmitted(rates, node, 10);
        AssertCaps(caps, 125_000);
        Assert.True(10 * 125_000 <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));

        for (var i = 7; i < 10; i++)
        {
            rates.Unregister("alice", "s" + i);
            await node.ReleaseAsync("alice", "s" + i);
        }

        var seven = AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 7);
        AssertCaps(caps.Take(7), seven);

        var extra = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s10", V4A, 10, 0, TenMbpsBps));
        rates.Register("alice", "s10", extra, TenMbpsBps);
        var eight = AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 8);
        Assert.Equal(eight, extra.MaxSendBytesPerSecond);
        AssertCaps(caps.Take(7).Append(extra), eight);

        var clock = new ManualClock();
        var split = new InMemorySessionStateStore();
        var ratesA = new AccountRateAllocator(clock);
        var ratesB = new AccountRateAllocator(clock);
        var nodeA = CreateNode(split, "nntpd01", ratesA);
        var nodeB = CreateNode(split, "nntpd02", ratesB);
        var aCaps = new FakeCap[7];
        for (var i = 0; i < 7; i++)
        {
            aCaps[i] = new FakeCap();
            Assert.Equal(SessionAdmissionResult.Success, await nodeA.TryAdmitAsync("bob", "a" + i, V4A, 10, 0, TenMbpsBps));
            ratesA.Register("bob", "a" + i, aCaps[i], TenMbpsBps);
        }

        var bCaps = new FakeCap[3];
        for (var i = 0; i < 3; i++)
        {
            bCaps[i] = new FakeCap();
            Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("bob", "b" + i, V4B, 10, 0, TenMbpsBps));
            ratesB.Register("bob", "b" + i, bCaps[i], TenMbpsBps);
            Assert.True(Allocated(aCaps) + Allocated(bCaps.Take(i + 1)) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));
        }

        await nodeA.RenewLeasesAsync();
        AssertCaps(aCaps, 125_000);
        Assert.True(Allocated(aCaps) + Allocated(bCaps) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));
        clock.Advance(SessionStateDefaults.LeaseTtl);
        ratesB.ObserveClusterSessionCount("bob", 10);
        AssertCaps(aCaps, 125_000);
        Assert.True(Allocated(aCaps) + Allocated(bCaps) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));

        for (var i = 0; i < 3; i++)
        {
            ratesB.Unregister("bob", "b" + i);
            await nodeB.ReleaseAsync("bob", "b" + i);
        }

        AssertCaps(aCaps, 125_000);
        await nodeA.RenewLeasesAsync();
        AssertCaps(aCaps, seven);

        var rejoin = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("bob", "b0", V4B, 10, 0, TenMbpsBps));
        ratesB.Register("bob", "b0", rejoin, TenMbpsBps);
        AssertCaps(aCaps, seven);
        Assert.True(Allocated(rejoin) <= 3);
        Assert.True(Allocated(aCaps) + Allocated(rejoin) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));
        await nodeA.RenewLeasesAsync();
        AssertCaps(aCaps, eight);
        clock.Advance(SessionStateDefaults.LeaseTtl);
        ratesB.ObserveClusterSessionCount("bob", 8);
        Assert.True(rejoin.MaxSendBytesPerSecond <= 0);
        AssertCaps(aCaps, eight);
        Assert.True(Allocated(aCaps) + Allocated(rejoin) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));
    }

    [Fact]
    public async Task RapidJoinLeave_NeverExceedsAggregate()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var caps = new Dictionary<string, FakeCap>(StringComparer.Ordinal);
        for (var i = 0; i < 20; i++)
        {
            var id = "s" + (i % 7);
            if (caps.ContainsKey(id))
            {
                rates.Unregister("alice", id);
                await node.ReleaseAsync("alice", id);
                caps.Remove(id);
            }
            else
            {
                Assert.Equal(
                    SessionAdmissionResult.Success,
                    await node.TryAdmitAsync("alice", id, V4A, 10, 0, TenMbpsBps));
                var cap = new FakeCap();
                rates.Register("alice", id, cap, TenMbpsBps);
                caps[id] = cap;
            }

            if (caps.Count > 0)
            {
                var expected = AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, caps.Count);
                AssertCaps(caps.Values, expected);
                Assert.True(expected * caps.Count <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));
            }
        }
    }

    [Fact]
    public async Task SimultaneousJoins_ShareTheSameFloor()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var admits = Enumerable.Range(0, 10)
            .Select(i => node.TryAdmitAsync("alice", "s" + i, V4A, 10, 0, TenMbpsBps).AsTask());
        var results = await Task.WhenAll(admits);
        Assert.All(results, static result => Assert.Equal(SessionAdmissionResult.Success, result));

        var caps = new FakeCap[10];
        for (var i = 0; i < 10; i++)
        {
            caps[i] = new FakeCap();
            rates.Register("alice", "s" + i, caps[i], TenMbpsBps);
        }

        rates.ObserveClusterSessionCount("alice", 10);
        AssertCaps(caps, 125_000);
    }

    [Fact]
    public async Task SuccessfulReleaseToZero_DoesNotNeedObserveZero_NextAdmitGetsFullRate()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var first = new FakeCap();
        var second = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s1", V4A, 0, 0, TenMbpsBps));
        rates.Register("alice", "s1", first, TenMbpsBps);
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s2", V4A, 0, 0, TenMbpsBps));
        rates.Register("alice", "s2", second, TenMbpsBps);
        Assert.Equal(625_000, first.MaxSendBytesPerSecond);

        rates.Unregister("alice", "s1");
        await node.ReleaseAsync("alice", "s1");
        Assert.Equal(1_250_000, second.MaxSendBytesPerSecond);
        Assert.Equal(1, membership.ActiveSessionCount("alice", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        rates.Unregister("alice", "s2");
        await node.ReleaseAsync("alice", "s2");
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        Assert.Equal(0, rates.LocalSessionCount("alice"));
        Assert.Equal(0, membership.ActiveSessionCount("alice", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var released = node.ReleaseCalls;
        await node.ReleaseAsync("alice", "s2");
        Assert.Equal(released, node.ReleaseCalls);

        var again = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s3", V4A, 0, 0, TenMbpsBps));
        rates.Register("alice", "s3", again, TenMbpsBps);
        Assert.Equal(1_250_000, again.MaxSendBytesPerSecond);
    }

    [Fact]
    public async Task ReleaseWhenMembershipUnavailable_DoesNotRaiseJoinerToFullRate()
    {
        var fixture = await AdmitEstablishedThenJoinersAsync();
        fixture.Membership.Unavailable = true;
        fixture.RatesB.Unregister("alice", "b2");
        await fixture.NodeB.ReleaseAsync("alice", "b2");

        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, fixture.B1.MaxSendBytesPerSecond);
        AssertCaps([fixture.A1, fixture.A2], fixture.Quarter);
        Assert.Equal(1, fixture.NodeB.GetLocalSessionCount("alice"));
        Assert.Equal(1, fixture.RatesB.LocalSessionCount("alice"));
        Assert.Equal(4, fixture.Membership.ActiveSessionCount("alice", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.True(Allocated(fixture.A1, fixture.A2, fixture.B1) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));
    }

    [Fact]
    public async Task UnavailableRelease_ThenSuccessfulRenew_ObservesUnreleasedClusterTotal()
    {
        var fixture = await AdmitEstablishedThenJoinersAsync();
        fixture.Membership.Unavailable = true;
        fixture.RatesB.Unregister("alice", "b2");
        await fixture.NodeB.ReleaseAsync("alice", "b2");
        fixture.Membership.Unavailable = false;

        await fixture.NodeA.RenewLeasesAsync();
        await fixture.NodeB.RenewLeasesAsync();

        AssertCaps([fixture.A1, fixture.A2], fixture.Quarter);
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, fixture.B1.MaxSendBytesPerSecond);
        Assert.Equal(4, fixture.Membership.ActiveSessionCount("alice", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.True(Allocated(fixture.A1, fixture.A2, fixture.B1) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));
    }

    [Fact]
    public async Task UnavailableRelease_ThenSuccessfulLaterRelease_DoesNotOvershoot()
    {
        var fixture = await AdmitEstablishedThenJoinersAsync();
        fixture.Membership.Unavailable = true;
        fixture.RatesB.Unregister("alice", "b2");
        await fixture.NodeB.ReleaseAsync("alice", "b2");
        var releasesAfterFail = fixture.NodeB.ReleaseCalls;
        await fixture.NodeB.ReleaseAsync("alice", "b2");
        Assert.Equal(releasesAfterFail, fixture.NodeB.ReleaseCalls);

        fixture.Membership.Unavailable = false;
        fixture.RatesB.Unregister("alice", "b1");
        await fixture.NodeB.ReleaseAsync("alice", "b1");
        Assert.Equal(0, fixture.NodeB.GetLocalSessionCount("alice"));
        Assert.Equal(0, fixture.RatesB.LocalSessionCount("alice"));
        Assert.Equal(3, fixture.Membership.ActiveSessionCount("alice", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        await fixture.NodeA.RenewLeasesAsync();
        AssertCaps([fixture.A1, fixture.A2], fixture.Quarter);
        Assert.True(Allocated(fixture.A1, fixture.A2) <= AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));
    }

    [Fact]
    public async Task ReleaseDuringRenew_ExistingCapsFollowReleaseTotal()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var caps = await RegisterAdmitted(rates, node, 10);
        membership.NotifyRenewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        membership.BlockRenew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renew = node.RenewLeasesAsync().AsTask();
        await membership.NotifyRenewStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        rates.Unregister("alice", "s9");
        await node.ReleaseAsync("alice", "s9");
        AssertCaps(caps.Take(9), AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 9));
        membership.BlockRenew.TrySetResult();
        await renew.WaitAsync(TimeSpan.FromSeconds(2));
        AssertCaps(caps.Take(9), AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 9));
    }

    [Fact]
    public async Task FakeRedisWritePath_DoesNotEvaluateOnLimiterWrite()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var rates = new AccountRateAllocator();
        var node = new DistributedSessionStateTracker(
            store,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01",
            TimeProvider.System,
            rates: rates);
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s1", V4A, 10, 0, TenMbpsBps));
        var evals = redis.Database.ScriptEvaluateCount;
        await using var inner = new MemoryStream();
        await using var limiter = new OutboundRateLimiter(inner, 0, leaveInnerOpen: true);
        rates.Register("alice", "s1", limiter, TenMbpsBps);
        limiter.UpdateMaxSendBytesPerSecond(125_000);
        await limiter.WriteAsync("281 Authentication accepted\r\n"u8.ToArray());
        rates.ObserveClusterSessionCount("alice", 1);
        Assert.Equal(evals, redis.Database.ScriptEvaluateCount);

        await node.RenewLeasesAsync();
        Assert.Equal(evals + 1, redis.Database.ScriptEvaluateCount);
    }

    private static async Task<TwoNodeRateFixture> AdmitEstablishedThenJoinersAsync()
    {
        var membership = new InMemorySessionStateStore();
        var ratesA = new AccountRateAllocator();
        var ratesB = new AccountRateAllocator();
        var nodeA = CreateNode(membership, "nntpd01", ratesA);
        var nodeB = CreateNode(membership, "nntpd02", ratesB);
        var a1 = new FakeCap();
        var a2 = new FakeCap();
        var b1 = new FakeCap();
        var b2 = new FakeCap();

        Assert.Equal(SessionAdmissionResult.Success, await nodeA.TryAdmitAsync("alice", "a1", V4A, 10, 0, TenMbpsBps));
        ratesA.Register("alice", "a1", a1, TenMbpsBps);
        Assert.Equal(SessionAdmissionResult.Success, await nodeA.TryAdmitAsync("alice", "a2", V4A, 10, 0, TenMbpsBps));
        ratesA.Register("alice", "a2", a2, TenMbpsBps);
        AssertCaps([a1, a2], 625_000);

        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("alice", "b1", V4B, 10, 0, TenMbpsBps));
        ratesB.Register("alice", "b1", b1, TenMbpsBps);
        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("alice", "b2", V4B, 10, 0, TenMbpsBps));
        ratesB.Register("alice", "b2", b2, TenMbpsBps);
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, b1.MaxSendBytesPerSecond);
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, b2.MaxSendBytesPerSecond);

        await nodeA.RenewLeasesAsync();
        var quarter = AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 4);
        AssertCaps([a1, a2], quarter);
        return new TwoNodeRateFixture(membership, ratesA, ratesB, nodeA, nodeB, a1, a2, b1, b2, quarter);
    }

    private static async Task<FakeCap[]> RegisterAdmitted(
        AccountRateAllocator rates,
        DistributedSessionStateTracker node,
        int count)
    {
        var caps = new FakeCap[count];
        for (var i = 0; i < count; i++)
        {
            caps[i] = new FakeCap();
            Assert.Equal(
                SessionAdmissionResult.Success,
                await node.TryAdmitAsync("alice", "s" + i, V4A, count, 0, TenMbpsBps));
            rates.Register("alice", "s" + i, caps[i], TenMbpsBps);
        }

        return caps;
    }

    private static FakeCap[] Register(AccountRateAllocator rates, string account, int rateBps, int count)
    {
        var caps = new FakeCap[count];
        rates.ObserveClusterSessionCount(account, count);
        for (var i = 0; i < count; i++)
        {
            caps[i] = new FakeCap();
            rates.Register(account, "s" + i, caps[i], rateBps);
        }

        return caps;
    }

    private static void Unregister(AccountRateAllocator rates, string account, FakeCap[] caps, int remove)
    {
        for (var i = caps.Length - remove; i < caps.Length; i++)
        {
            rates.Unregister(account, "s" + i);
        }
    }

    private static void AssertCaps(IEnumerable<FakeCap> caps, long expected)
    {
        foreach (var cap in caps)
        {
            Assert.Equal(expected, cap.MaxSendBytesPerSecond);
        }
    }

    private static long Allocated(params FakeCap[] caps) =>
        Allocated((IEnumerable<FakeCap>)caps);

    private static long Allocated(IEnumerable<FakeCap> caps) =>
        caps.Sum(static cap => AccountRateFormula.AllocatedBytesPerSecond(cap.MaxSendBytesPerSecond));

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(delta.Ticks);
            _utcNow += delta;
        }
    }

    private static DistributedSessionStateTracker CreateNode(
        ISessionStateStore membership,
        string nodeId,
        IAccountRateAllocator rates) =>
        new(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            nodeId,
            TimeProvider.System,
            rates: rates);

    private sealed record TwoNodeRateFixture(
        InMemorySessionStateStore Membership,
        AccountRateAllocator RatesA,
        AccountRateAllocator RatesB,
        DistributedSessionStateTracker NodeA,
        DistributedSessionStateTracker NodeB,
        FakeCap A1,
        FakeCap A2,
        FakeCap B1,
        FakeCap B2,
        long Quarter);

    private sealed class FakeCap : IOutboundRateCap
    {
        public long MaxSendBytesPerSecond { get; private set; }

        public void UpdateMaxSendBytesPerSecond(long bytesPerSecond) => MaxSendBytesPerSecond = bytesPerSecond;
    }
}
