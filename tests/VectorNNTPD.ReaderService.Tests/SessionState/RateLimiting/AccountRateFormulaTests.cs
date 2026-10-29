using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.Common.Networking.RateLimiting;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.ReaderService.SessionState.RateLimiting;
namespace VectorNNTPD.ReaderService.Tests.SessionState.RateLimiting;

public sealed class AccountRateFormulaTests
{
    private const int OneMbpsBps = 1_000_000;
    private const int TenMbpsBps = 10_000_000;

    [Fact]
    public void DatabaseRate_240Bps_Is30BytesPerSecond_Not30Million()
    {
        Assert.Equal(30, AccountRateFormula.AccountBytesPerSecond(240));
        Assert.Equal(30, AccountRateFormula.PerSessionBytesPerSecond(240, 1));
        Assert.Equal(30, AccountRateFormula.EqualShareCap(240, 1));
        Assert.NotEqual(30_000_000, AccountRateFormula.AccountBytesPerSecond(240));
    }

    [Fact]
    public void OneMillionBps_IsOneMbps_125000BytesPerSecond()
    {
        Assert.Equal(8, AccountRateFormula.BitsPerByte);
        Assert.Equal(125_000, AccountRateFormula.AccountBytesPerSecond(OneMbpsBps));
        Assert.Equal(1_250_000, AccountRateFormula.AccountBytesPerSecond(TenMbpsBps));
    }

    [Fact]
    public void Policy_Database240Bps_MapsToRateLimitBps()
    {
        var record = MemoryNntpUserRecordStore.Create(
            "alice",
            "x",
            sessionLimit: 1,
            srcIpLimit: 1,
            rateLimitBps: 240,
            byteLimit: 10_000_000_000);
        var policy = NntpAccountPolicy.FromRecord(record);
        Assert.Equal(240, record.RateLimitBps);
        Assert.True(policy.RequiresRateTracking);
        Assert.Equal(240, policy.RateLimitBps);
        Assert.Equal(30, AccountRateFormula.EqualShareCap(policy.RateLimitBps, 1));
        Assert.NotEqual(30_000_000, AccountRateFormula.AccountBytesPerSecond(policy.RateLimitBps));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void PositiveSubEightBps_IsBlockedNotUnlimited(int rateBps)
    {
        Assert.Equal(0, AccountRateFormula.AccountBytesPerSecond(rateBps));
        Assert.Equal(0, AccountRateFormula.PerSessionBytesPerSecond(rateBps, 1));
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, AccountRateFormula.EqualShareCap(rateBps, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ZeroOrNegativeBps_IsUnlimitedSentinel(int rateBps)
    {
        Assert.Equal(0, AccountRateFormula.AccountBytesPerSecond(rateBps));
        Assert.Equal(0, AccountRateFormula.PerSessionBytesPerSecond(rateBps, 1));
        Assert.Equal(0, AccountRateFormula.EqualShareCap(rateBps, 1));
        Assert.True(AccountRateFormula.AggregateDoesNotExceed(rateBps, 10));
    }

    [Fact]
    public void ZeroActiveSessions_HasNoAllocation()
    {
        Assert.Equal(0, AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, 0));
        Assert.True(AccountRateFormula.AggregateDoesNotExceed(TenMbpsBps, 0));
    }

    [Theory]
    [InlineData(10_000_000, 1, 1_250_000)]
    [InlineData(10_000_000, 2, 625_000)]
    [InlineData(10_000_000, 5, 250_000)]
    [InlineData(10_000_000, 10, 125_000)]
    [InlineData(10_000_000, 7, 178_571)]
    [InlineData(10_000_000, 8, 156_250)]
    [InlineData(10_000_000, 3, 416_666)]
    [InlineData(1_000, 1, 125)]
    [InlineData(8_000, 1, 1_000)]
    public void PerSession_FloorsAccountBytesByActiveCount(int rateBps, int sessions, long expected)
    {
        Assert.Equal(expected, AccountRateFormula.PerSessionBytesPerSecond(rateBps, sessions));
        Assert.True(AccountRateFormula.AggregateDoesNotExceed(rateBps, sessions));
        Assert.True(
            expected * sessions <= AccountRateFormula.AccountBytesPerSecond(rateBps));
    }

    [Fact]
    public void UsesActiveCount_NotConfiguredSessionLimit()
    {
        Assert.Equal(625_000, AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, activeSessions: 2));
        Assert.NotEqual(125_000, AccountRateFormula.PerSessionBytesPerSecond(TenMbpsBps, activeSessions: 2));
    }

    [Fact]
    public void AggregateNeverExceeds_ForEverySessionCountThroughLimit()
    {
        for (var sessions = 1; sessions <= 64; sessions++)
        {
            Assert.True(AccountRateFormula.AggregateDoesNotExceed(TenMbpsBps, sessions));
        }
    }

    [Fact]
    public void ConservativeJoin_IsUnusedRemainderAfterPreviousSplit()
    {
        Assert.Equal(3, AccountRateFormula.ConservativeJoinBytesPerSecond(TenMbpsBps, 7));
        Assert.Equal(0, AccountRateFormula.ConservativeJoinBytesPerSecond(TenMbpsBps, 8));
        Assert.Equal(0, AccountRateFormula.ConservativeJoinBytesPerSecond(TenMbpsBps, 10));
        Assert.Equal(0, AccountRateFormula.ConservativeJoinBytesPerSecond(0, 7));
    }

    [Fact]
    public void AccountBytesPerSecond_OverflowSafeForIntMaxBps()
    {
        Assert.Equal(int.MaxValue / 8, AccountRateFormula.AccountBytesPerSecond(int.MaxValue));
    }

    [Fact]
    public void EqualShareCap_FloorZero_IsBlockedNotUnlimited()
    {
        Assert.Equal(0, AccountRateFormula.PerSessionBytesPerSecond(OneMbpsBps, 200_000));
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, AccountRateFormula.EqualShareCap(OneMbpsBps, 200_000));
        Assert.Equal(0, AccountRateFormula.EqualShareCap(0, 1));
        Assert.Equal(125_000, AccountRateFormula.EqualShareCap(OneMbpsBps, 1));
    }
}
