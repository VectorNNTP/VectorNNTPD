using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.SessionState.RateLimiting;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Authentication;

public sealed class AccountPolicyMatrixTests
{
    private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress V4B = IPAddress.Parse("198.51.100.20");

    [Fact]
    public void A_ByteAndRatePositive_BothEnforced()
    {
        var record = MemoryNntpUserRecordStore.Create(
            "alice",
            "x",
            rateLimitBps: 2_400,
            byteLimit: 1_000_000);
        var policy = NntpAccountPolicy.FromRecord(record);
        Assert.Equal(1_000_000, policy.ByteLimit);
        Assert.Equal(2_400, policy.RateLimitBps);
        Assert.True(policy.RequiresRateTracking);
        Assert.Equal(300, AccountRateFormula.AccountBytesPerSecond(policy.RateLimitBps));

        var rates = new AccountRateAllocator();
        var cap = new FakeCap();
        rates.ObserveClusterSessionCount(policy.Username, 1);
        rates.Register(policy.Username, "s1", cap, policy.RateLimitBps);
        Assert.Equal(300, cap.MaxSendBytesPerSecond);

        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount(policy.Username, policy.ByteLimit);
        var tracker = new AccountByteTracker(durable, new InMemoryAccountByteStore(), NullLogger<AccountByteTracker>.Instance);
        tracker.CreateSink(policy.Username).ObserveCopied(100);
        Assert.False(tracker.IsExhausted(policy.Username));
        Assert.Equal(300, cap.MaxSendBytesPerSecond);
    }

    [Fact]
    public void B_BytePositive_RateZero_QuotaOnly()
    {
        var policy = new NntpAccountPolicy("alice", 0, 1_000_000, 0, 0, "c");
        Assert.False(policy.RequiresRateTracking);
        Assert.False(policy.RequiresAdmission);
        Assert.Equal(0, AccountRateFormula.EqualShareCap(policy.RateLimitBps, 1));
        Assert.Equal(1_000_000, policy.ByteLimit);
    }

    [Fact]
    public void C_ByteZero_RatePositive_ExhaustedWithRatePolicy()
    {
        var policy = new NntpAccountPolicy("alice", 10_000_000, 0, 0, 0, "c");
        Assert.Equal(0, policy.ByteLimit);
        Assert.True(policy.RequiresRateTracking);
        Assert.Equal(1_250_000, AccountRateFormula.EqualShareCap(policy.RateLimitBps, 1));
    }

    [Fact]
    public void D_BothZero_ExhaustedAndUnlimitedRate()
    {
        var policy = new NntpAccountPolicy("alice", 0, 0, 0, 0, "c");
        Assert.Equal(0, policy.ByteLimit);
        Assert.False(policy.RequiresRateTracking);
        Assert.Equal(0, AccountRateFormula.EqualShareCap(policy.RateLimitBps, 1));
    }

    [Fact]
    public void E_MultipleSessions_ShareRateAndOneByteLedger()
    {
        var rates = new AccountRateAllocator();
        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount("alice", 1_000);
        var bytes = new AccountByteTracker(durable, new InMemoryAccountByteStore(), NullLogger<AccountByteTracker>.Instance);
        var caps = new FakeCap[2];
        for (var i = 0; i < 2; i++)
        {
            caps[i] = new FakeCap();
            rates.Register("alice", "s" + i, caps[i], 2_400);
            bytes.CreateSink("alice").ObserveCopied(50);
        }

        rates.ObserveClusterSessionCount("alice", 2);
        Assert.Equal(150, caps[0].MaxSendBytesPerSecond);
        Assert.Equal(150, caps[1].MaxSendBytesPerSecond);
        Assert.Equal(100, bytes.PendingBytes("alice"));
    }

    [Fact]
    public async Task F_TwoNodes_RateSafetyAndByteLedgerCoexist()
    {
        var membership = new InMemorySessionStateStore();
        var ratesA = new AccountRateAllocator();
        var ratesB = new AccountRateAllocator();
        var nodeA = new DistributedSessionStateTracker(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01",
            TimeProvider.System,
            rates: ratesA);
        var nodeB = new DistributedSessionStateTracker(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd02",
            TimeProvider.System,
            rates: ratesB);
        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount("alice", 5_000);
        var bytes = new AccountByteTracker(durable, new InMemoryAccountByteStore(), NullLogger<AccountByteTracker>.Instance);

        Assert.Equal(SessionAdmissionResult.Success, await nodeA.TryAdmitAsync("alice", "a1", V4A, 10, 0, 2_400));
        var a1 = new FakeCap();
        ratesA.Register("alice", "a1", a1, 2_400);
        bytes.CreateSink("alice").ObserveCopied(25);

        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("alice", "b1", V4B, 10, 0, 2_400));
        var b1 = new FakeCap();
        ratesB.Register("alice", "b1", b1, 2_400);
        bytes.CreateSink("alice").ObserveCopied(25);

        Assert.True(
            AccountRateFormula.AllocatedBytesPerSecond(a1.MaxSendBytesPerSecond)
            + AccountRateFormula.AllocatedBytesPerSecond(b1.MaxSendBytesPerSecond)
            <= AccountRateFormula.AccountBytesPerSecond(2_400));
        Assert.Equal(50, bytes.PendingBytes("alice"));
        Assert.Equal(5_000, durable.Remaining("alice"));
    }

    [Fact]
    public void G_SubEightBps_IsBlocked()
    {
        var rates = new AccountRateAllocator();
        var cap = new FakeCap();
        rates.ObserveClusterSessionCount("alice", 1);
        rates.Register("alice", "s1", cap, 7);
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, cap.MaxSendBytesPerSecond);
    }

    [Fact]
    public async Task H_RateOnly_TracksSessionsWhenLimitsAreZero()
    {
        var node = new InMemorySessionStateTracker(new AccountRateAllocator());
        Assert.Equal(
            SessionAdmissionResult.Success,
            await node.TryAdmitAsync("alice", "s1", V4A, 0, 0, 2_400));
        Assert.Equal(
            SessionAdmissionResult.Success,
            await node.TryAdmitAsync("alice", "s2", V4A, 0, 0, 2_400));
    }

    [Fact]
    public void I_SelectUserByName_OmitsAccountType_AndKeepsOrdinals()
    {
        Assert.DoesNotContain("account_type", NntpUserQueries.SelectUserByName, StringComparison.Ordinal);
        Assert.Contains("account_rate_limit, account_byte_limit, account_session_limit, account_srcip_limit", NntpUserQueries.SelectUserByName, StringComparison.Ordinal);
        Assert.Contains("account_art_type", NntpUserQueries.SelectUserByName, StringComparison.Ordinal);
        Assert.DoesNotContain("account_type", NntpUserQueries.SelectByteQuotaForUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain("account_type", NntpUserQueries.SelectAccountByteRemaining, StringComparison.Ordinal);
        Assert.StartsWith("SELECT account_byte_limit", NntpUserQueries.SelectByteQuotaForUpdate, StringComparison.Ordinal);
        Assert.StartsWith("SELECT account_byte_limit", NntpUserQueries.SelectAccountByteRemaining, StringComparison.Ordinal);
    }

    [Fact]
    public void J_ConcurrentSinks_ShareAccountLedger()
    {
        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount("alice", 1_000);
        var tracker = new AccountByteTracker(durable, new InMemoryAccountByteStore(), NullLogger<AccountByteTracker>.Instance);
        tracker.CreateSink("alice").ObserveCopied(10);
        tracker.CreateSink("alice").ObserveCopied(20);
        Assert.Equal(30, tracker.PendingBytes("alice"));
    }

    [Fact]
    public void NullLimits_MapToZero()
    {
        Assert.Equal(0, VectorNNTPD.ReaderService.NntpDb.MySqlNntpDbConnection.ConvertByteLimit(null));
        Assert.Equal(0, AccountRateFormula.AccountBytesPerSecond(0));
    }

    private sealed class FakeCap : IOutboundRateCap
    {
        public long MaxSendBytesPerSecond { get; private set; }

        public void UpdateMaxSendBytesPerSecond(long bytesPerSecond) => MaxSendBytesPerSecond = bytesPerSecond;
    }
}
