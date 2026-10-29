using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.SessionState;

namespace VectorNNTPD.ReaderService.Tests.AccountBytes;

/// <summary>
/// Live EVAL of <see cref="AccountByteScripts"/> against the dedicated test Redis.
/// </summary>
public sealed class AccountByteRedisLuaIntegrationTests : IClassFixture<AccountBytesRedisIntegrationFixture>
{
    private readonly AccountBytesRedisIntegrationFixture _redis;

    public AccountByteRedisLuaIntegrationTests(AccountBytesRedisIntegrationFixture redis)
    {
        _redis = redis;
    }

    [SessionStateRedisIntegrationFact]
    public void ConnectsToAuthorizedTestRedis()
    {
        Assert.True(_redis.IsConfigured);
        Assert.Equal(SessionStateRedisIntegration.AuthorizedEndpoint, _redis.Endpoint);
        Assert.False(string.IsNullOrWhiteSpace(_redis.ServerVersion));
    }

    [SessionStateRedisIntegrationFact]
    public async Task Apply_MissingKey_InitializesFromMysqlAfter()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(900, await _redis.ApplyAsync(account, 100, 900));
        Assert.Equal(900, await _redis.ObserveAsync(account));
        Assert.Null(await _redis.KeyTimeToLiveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Apply_ConcurrentNodes_NeverIncreaseOrGoNegative()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(8_000, await _redis.ApplyAsync(account, 0, 8_000));
        var first = _redis.ApplyAsync(account, 300, 7_700);
        var second = _redis.ApplyAsync(account, 500, 7_200);
        await Task.WhenAll(first.AsTask(), second.AsTask());
        var remaining = await _redis.ObserveAsync(account);
        Assert.True(remaining is >= 0 and <= 7_200);
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Apply_StaleHigh_FloorsToMysql()
    {
        var account = await _redis.CreateAccountAsync();
        await _redis.SeedAsync(account, 50_000);
        Assert.Equal(100, await _redis.ApplyAsync(account, 100, 100));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Apply_RedisAhead_DoesNotRestoreQuota()
    {
        var account = await _redis.CreateAccountAsync();
        await _redis.SeedAsync(account, 40);
        Assert.Equal(40, await _redis.ApplyAsync(account, 10, 800));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Observe_Missing_ReturnsSentinel_AndDoesNotCreate()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(AccountByteKeys.Missing, await _redis.ObserveAsync(account));
        Assert.Equal(AccountByteKeys.Missing, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Apply_Zero_StaysZero()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(0, await _redis.ApplyAsync(account, 10, 0));
        Assert.Equal(0, await _redis.ApplyAsync(account, 25, 0));
        Assert.Equal(0, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task MissingKeyAfterDelete_ReinitializesFromDurableRemaining()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(500, await _redis.ApplyAsync(account, 100, 500));
        await _redis.DeleteWithoutFixtureTrackingAsync(account);
        Assert.Equal(AccountByteKeys.Missing, await _redis.ObserveAsync(account));
        Assert.Equal(400, await _redis.ApplyAsync(account, 100, 400));
        await _redis.DeleteKeyAsync(account);
    }
}
