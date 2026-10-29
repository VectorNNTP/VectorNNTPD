using StackExchange.Redis;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.SessionState;

namespace VectorNNTPD.ReaderService.Tests.AccountBytes;

/// <summary>
/// Live EVAL of production <see cref="AccountByteScripts.Apply"/> for retry
/// and concurrent-node idempotency. Not a fake engine path.
/// </summary>
public sealed class AccountByteRedisIdempotencyTests : IClassFixture<AccountBytesRedisIntegrationFixture>
{
    private readonly AccountBytesRedisIntegrationFixture _redis;

    public AccountByteRedisIdempotencyTests(AccountBytesRedisIntegrationFixture redis)
    {
        _redis = redis;
    }

    [SessionStateRedisIntegrationFact]
    public async Task SameBatch_AppliedTwice_RedisUnchanged()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(900, await _redis.ApplyAsync(account, "batch-a", 100, 900));
        Assert.Equal(RedisType.Hash, await _redis.KeyTypeAsync(account));
        Assert.True(await _redis.BatchMarkExistsAsync(account, "batch-a"));
        Assert.Equal(900, await _redis.ApplyAsync(account, "batch-a", 100, 900));
        Assert.Equal(900, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task SameBatch_AppliedTenTimes_RedisUnchanged()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(900, await _redis.ApplyAsync(account, "batch-a", 100, 900));
        for (var i = 0; i < 9; i++)
        {
            Assert.Equal(900, await _redis.ApplyAsync(account, "batch-a", 100, 900));
        }

        Assert.Equal(900, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task BatchAThenB_FloorsToDurable800()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(900, await _redis.ApplyAsync(account, "A", 100, 900));
        Assert.Equal(800, await _redis.ApplyAsync(account, "B", 100, 800));
        Assert.Equal(800, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task BatchBThenA_DoesNotExceedDurable800()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(800, await _redis.ApplyAsync(account, "B", 100, 800));
        Assert.Equal(800, await _redis.ApplyAsync(account, "A", 100, 900));
        Assert.Equal(800, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task ADuplicatedAfterB_Unchanged()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(800, await _redis.ApplyAsync(account, "B", 100, 800));
        Assert.Equal(800, await _redis.ApplyAsync(account, "A", 100, 900));
        Assert.Equal(800, await _redis.ApplyAsync(account, "A", 100, 900));
        Assert.Equal(800, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task BDuplicatedAfterA_Unchanged()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(900, await _redis.ApplyAsync(account, "A", 100, 900));
        Assert.Equal(800, await _redis.ApplyAsync(account, "B", 100, 800));
        Assert.Equal(800, await _redis.ApplyAsync(account, "B", 100, 800));
        Assert.Equal(800, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task AmbiguousResult_RetrySameBatch_DoesNotConsumeTwice()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(900, await _redis.ApplyAsync(account, "lost-reply", 100, 900));
        Assert.Equal(900, await _redis.ApplyAsync(account, "lost-reply", 100, 900));
        Assert.True(await _redis.BatchMarkExistsAsync(account, "lost-reply"));
        Assert.Equal(900, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task MissingKey_RepeatedSameBatch_InitializesOnce()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(AccountByteKeys.Missing, await _redis.ObserveAsync(account));
        Assert.Equal(900, await _redis.ApplyAsync(account, "batch-a", 100, 900));
        Assert.Equal(900, await _redis.ApplyAsync(account, "batch-a", 100, 900));
        Assert.Equal(900, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task RedisBelowMysql_NeverRepairedUpward()
    {
        var account = await _redis.CreateAccountAsync();
        await _redis.SeedAsync(account, 800);
        Assert.Equal(800, await _redis.ApplyAsync(account, "batch-a", 100, 900));
        Assert.Equal(800, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task RedisAboveMysql_FloorsDown()
    {
        var account = await _redis.CreateAccountAsync();
        await _redis.SeedAsync(account, 50_000);
        Assert.Equal(900, await _redis.ApplyAsync(account, "batch-a", 100, 900));
        Assert.Equal(900, await _redis.ObserveAsync(account));
        await _redis.DeleteKeyAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task ConcurrentNodes_WithDuplicateRetries_FinalIsMinMysqlAfter()
    {
        var account = await _redis.CreateAccountAsync();
        await Task.WhenAll(
            _redis.ApplyAsync(account, "A", 100, 900).AsTask(),
            _redis.ApplyAsync(account, "B", 100, 800).AsTask(),
            _redis.ApplyAsync(account, "A", 100, 900).AsTask(),
            _redis.ApplyAsync(account, "B", 100, 800).AsTask());
        Assert.Equal(800, await _redis.ObserveAsync(account));
        Assert.True(await _redis.BatchMarkExistsAsync(account, "A"));
        Assert.True(await _redis.BatchMarkExistsAsync(account, "B"));
        await _redis.DeleteKeyAsync(account);
    }
}
