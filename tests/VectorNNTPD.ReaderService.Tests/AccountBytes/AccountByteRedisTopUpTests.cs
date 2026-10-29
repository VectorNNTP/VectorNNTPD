using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.SessionState;

namespace VectorNNTPD.ReaderService.Tests.AccountBytes;

/// <summary>Live production Lua DELETE / APPLY / OBSERVE for quota top-up invalidation.</summary>
public sealed class AccountByteRedisTopUpTests : IClassFixture<AccountBytesRedisIntegrationFixture>
{
    private readonly AccountBytesRedisIntegrationFixture _redis;

    public AccountByteRedisTopUpTests(AccountBytesRedisIntegrationFixture redis)
    {
        _redis = redis;
    }

    [SessionStateRedisIntegrationFact]
    public async Task TopUpWithoutDelete_EffectiveStaysAtRedis()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(50, await _redis.ApplyAsync(account, "seed", 50, 50));
        Assert.Equal(50, await _redis.ApplyAsync(account, "top", 0, 1000));
        Assert.Equal(50, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task DeleteThenObserve_IsMissing_ApplyInitializesToMysql()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(50, await _redis.ApplyAsync(account, "seed", 50, 50));
        Assert.Equal(1, await _redis.DeleteAsync(account));
        Assert.Equal(AccountByteKeys.Missing, await _redis.ObserveAsync(account));
        Assert.Equal(1000, await _redis.ApplyAsync(account, "after", 0, 1000));
        Assert.Equal(1000, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task ExhaustedRedisZero_TopUpMysql_StaysZeroUntilDelete()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(0, await _redis.ApplyAsync(account, "seed", 0, 0));
        Assert.Equal(0, await _redis.ApplyAsync(account, "top", 0, 1000));
        Assert.Equal(0, await _redis.ObserveAsync(account));
        Assert.Equal(1, await _redis.DeleteAsync(account));
        Assert.Equal(AccountByteKeys.Missing, await _redis.ObserveAsync(account));
        Assert.Equal(1000, await _redis.ApplyAsync(account, "init", 0, 1000));
        Assert.Equal(1000, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task MissingKey_ApplyNeverInitializesAboveMysql()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(AccountByteKeys.Missing, await _redis.ObserveAsync(account));
        Assert.Equal(400, await _redis.ApplyAsync(account, "init", 100, 400));
        Assert.Equal(400, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task DeleteIsIdempotent_AndDoesNotCreate()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(0, await _redis.DeleteAsync(account));
        Assert.Equal(AccountByteKeys.Missing, await _redis.ObserveAsync(account));
        Assert.Equal(0, await _redis.DeleteAsync(account));
        await _redis.DeleteKeyAsync(account);
    }
}
