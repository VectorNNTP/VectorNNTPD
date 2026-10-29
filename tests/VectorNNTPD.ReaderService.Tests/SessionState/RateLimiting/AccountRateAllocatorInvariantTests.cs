using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.SessionState.RateLimiting;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.SessionState.RateLimiting;

public sealed class AccountRateAllocatorInvariantTests
{
    private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress V4B = IPAddress.Parse("198.51.100.20");
    private static readonly IPAddress V4C = IPAddress.Parse("203.0.113.30");
    private const int OneMbpsBps = 1_000_000;
    private const int TenMbpsBps = 10_000_000;

    [Fact]
    public async Task ThreeNode_LeftoverWouldOvershoot_JoinersAreBlocked()
    {
        var clock = new ManualClock();
        var membership = new InMemorySessionStateStore();
        var a = CreateNode(membership, "nntpd01", clock);
        var b = CreateNode(membership, "nntpd02", clock);
        var c = CreateNode(membership, "nntpd03", clock);

        var aCaps = await AdmitMany(a, "alice", "a", V4A, 6, TenMbpsBps);
        AssertAggregate(TenMbpsBps, aCaps);

        var b1 = await AdmitOne(b, "alice", "b1", V4B, TenMbpsBps);
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, b1.MaxSendBytesPerSecond);
        AssertAggregate(TenMbpsBps, aCaps, [b1]);

        var c1 = await AdmitOne(c, "alice", "c1", V4C, TenMbpsBps);
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, c1.MaxSendBytesPerSecond);
        AssertAggregate(TenMbpsBps, aCaps, [b1], [c1]);
    }

    [Fact]
    public async Task ThreeNode_UserSequence_NeverExceedsAggregate()
    {
        var clock = new ManualClock();
        var membership = new InMemorySessionStateStore();
        var a = CreateNode(membership, "nntpd01", clock);
        var b = CreateNode(membership, "nntpd02", clock);
        var c = CreateNode(membership, "nntpd03", clock);

        var aCaps = await AdmitMany(a, "alice", "a", V4A, 5, TenMbpsBps);
        AssertAggregate(TenMbpsBps, aCaps);

        var bCaps = new List<FakeCap>();
        for (var i = 0; i < 3; i++)
        {
            bCaps.Add(await AdmitOne(b, "alice", "b" + i, V4B, TenMbpsBps));
            AssertAggregate(TenMbpsBps, aCaps, bCaps);
        }

        var c1 = await AdmitOne(c, "alice", "c1", V4C, TenMbpsBps);
        AssertAggregate(TenMbpsBps, aCaps, bCaps, [c1]);

        var c2 = await AdmitOne(c, "alice", "c2", V4C, TenMbpsBps);
        AssertAggregate(TenMbpsBps, aCaps, bCaps, [c1, c2]);

        b.Rates.Unregister("alice", "b2");
        await b.Tracker.ReleaseAsync("alice", "b2");
        bCaps.RemoveAt(2);
        AssertAggregate(TenMbpsBps, aCaps, bCaps, [c1, c2]);

        await a.Tracker.RenewLeasesAsync();
        AssertAggregate(TenMbpsBps, aCaps, bCaps, [c1, c2]);
        AssertCaps(aCaps, AccountRateFormula.EqualShareCap(TenMbpsBps, 9));

        await c.Tracker.RenewLeasesAsync();
        AssertAggregate(TenMbpsBps, aCaps, bCaps, [c1, c2]);

        await b.Tracker.RenewLeasesAsync();
        AssertAggregate(TenMbpsBps, aCaps, bCaps, [c1, c2]);

        clock.Advance(SessionStateDefaults.LeaseTtl);
        a.Rates.ObserveClusterSessionCount("alice", 9);
        b.Rates.ObserveClusterSessionCount("alice", 9);
        c.Rates.ObserveClusterSessionCount("alice", 9);
        AssertAggregate(TenMbpsBps, aCaps, bCaps, [c1, c2]);
        Assert.True(c1.MaxSendBytesPerSecond <= 0);
        Assert.True(c2.MaxSendBytesPerSecond <= 0);
    }

    [Fact]
    public void EstablishedEqualShare_IsNotReclassifiedAsJoiner()
    {
        var clock = new ManualClock();
        var rates = new AccountRateAllocator(clock);
        var cap = new FakeCap();
        rates.ObserveClusterSessionCount("alice", 1);
        rates.Register("alice", "a1", cap, OneMbpsBps);
        Assert.Equal(125_000, cap.MaxSendBytesPerSecond);

        rates.ObserveClusterSessionCount("alice", 400);
        Assert.True(cap.MaxSendBytesPerSecond <= AccountRateFormula.EqualShareCap(OneMbpsBps, 400));
        Assert.True(AccountRateFormula.AllocatedBytesPerSecond(cap.MaxSendBytesPerSecond) <= 125_000);
    }

    [Theory]
    [InlineData(10_000_000, 64)]
    [InlineData(1_000_000, 32)]
    [InlineData(3_000_000, 16)]
    [InlineData(1_000_000, 200)]
    public async Task StateMachine_ArbitraryJoinLeaveRenew_NeverExceedsAggregate(int rateBps, int sessionLimit)
    {
        var clock = new ManualClock();
        var membership = new InMemorySessionStateStore();
        var nodes = new[]
        {
            CreateNode(membership, "nntpd01", clock),
            CreateNode(membership, "nntpd02", clock),
            CreateNode(membership, "nntpd03", clock),
        };
        var ips = new[] { V4A, V4B, V4C };
        var rng = new Random(rateBps + sessionLimit);
        var nextId = 0;

        for (var step = 0; step < 80; step++)
        {
            var node = nodes[rng.Next(nodes.Length)];
            var roll = rng.Next(6);
            if (roll <= 2 && node.Sessions.Count < sessionLimit)
            {
                var id = "s" + nextId++;
                var result = await node.Tracker.TryAdmitAsync(
                    "alice",
                    id,
                    ips[Array.IndexOf(nodes, node)],
                    sessionLimit,
                    0,
                    rateBps);
                if (result == SessionAdmissionResult.Success)
                {
                    var cap = new FakeCap();
                    node.Rates.Register("alice", id, cap, rateBps);
                    node.Sessions[id] = cap;
                }
            }
            else if (roll == 3 && node.Sessions.Count > 0)
            {
                var id = node.Sessions.Keys.ElementAt(rng.Next(node.Sessions.Count));
                node.Rates.Unregister("alice", id);
                await node.Tracker.ReleaseAsync("alice", id);
                node.Sessions.Remove(id);
            }
            else if (roll == 4)
            {
                await node.Tracker.RenewLeasesAsync();
            }
            else
            {
                clock.Advance(SessionStateDefaults.LeaseTtl);
                var live = nodes.Sum(static n => n.Sessions.Count);
                if (live > 0)
                {
                    foreach (var liveNode in nodes)
                    {
                        await liveNode.Tracker.RenewLeasesAsync();
                        liveNode.Rates.ObserveClusterSessionCount("alice", live);
                    }
                }
            }

            AssertAggregate(rateBps, nodes.Select(static n => n.Sessions.Values));
        }
    }

    [Fact]
    public void Allocator_FloorZero_BlocksInsteadOfUnlimited()
    {
        var clock = new ManualClock();
        var rates = new AccountRateAllocator(clock);
        rates.ObserveClusterSessionCount("alice", 200_000);
        var cap = new FakeCap();
        rates.Register("alice", "s1", cap, OneMbpsBps);
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, cap.MaxSendBytesPerSecond);

        clock.Advance(SessionStateDefaults.LeaseTtl);
        rates.ObserveClusterSessionCount("alice", 200_000);
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, cap.MaxSendBytesPerSecond);
        Assert.Equal(0, AccountRateFormula.AllocatedBytesPerSecond(cap.MaxSendBytesPerSecond));
    }

    private static async Task<FakeCap[]> AdmitMany(Node node, string account, string prefix, IPAddress ip, int count, int rateBps)
    {
        var caps = new FakeCap[count];
        for (var i = 0; i < count; i++)
        {
            caps[i] = await AdmitOne(node, account, prefix + i, ip, rateBps);
        }

        return caps;
    }

    private static async Task<FakeCap> AdmitOne(Node node, string account, string id, IPAddress ip, int rateBps)
    {
        Assert.Equal(
            SessionAdmissionResult.Success,
            await node.Tracker.TryAdmitAsync(account, id, ip, 64, 0, rateBps));
        var cap = new FakeCap();
        node.Rates.Register(account, id, cap, rateBps);
        node.Sessions[id] = cap;
        return cap;
    }

    private static void AssertAggregate(int rateBps, params IEnumerable<FakeCap>[] groups) =>
        AssertAggregate(rateBps, groups.AsEnumerable());

    private static void AssertAggregate(int rateBps, IEnumerable<IEnumerable<FakeCap>> groups)
    {
        var caps = groups.SelectMany(static group => group)
            .Select(static cap => cap.MaxSendBytesPerSecond)
            .ToArray();
        var allocated = caps.Sum(AccountRateFormula.AllocatedBytesPerSecond);
        var limit = AccountRateFormula.AccountBytesPerSecond(rateBps);
        Assert.True(
            allocated <= limit,
            $"allocated {allocated} > {limit}; caps=[{string.Join(",", caps)}]");
    }

    private static void AssertCaps(IEnumerable<FakeCap> caps, long expected)
    {
        foreach (var cap in caps)
        {
            Assert.Equal(expected, cap.MaxSendBytesPerSecond);
        }
    }

    private static Node CreateNode(ISessionStateStore membership, string nodeId, TimeProvider clock)
    {
        var rates = new AccountRateAllocator(clock);
        return new Node(
            rates,
            new DistributedSessionStateTracker(
                membership,
                NullLogger<DistributedSessionStateTracker>.Instance,
                nodeId,
                TimeProvider.System,
                rates: rates));
    }

    private sealed class Node
    {
        public Node(AccountRateAllocator rates, DistributedSessionStateTracker tracker)
        {
            Rates = rates;
            Tracker = tracker;
        }

        public AccountRateAllocator Rates { get; }

        public DistributedSessionStateTracker Tracker { get; }

        public Dictionary<string, FakeCap> Sessions { get; } = new(StringComparer.Ordinal);
    }

    private sealed class FakeCap : IOutboundRateCap
    {
        public long MaxSendBytesPerSecond { get; private set; }

        public void UpdateMaxSendBytesPerSecond(long bytesPerSecond) => MaxSendBytesPerSecond = bytesPerSecond;
    }

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
}
