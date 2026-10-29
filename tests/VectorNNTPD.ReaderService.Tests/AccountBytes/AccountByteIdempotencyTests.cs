using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.AccountBytes;

/// <summary>
/// Production <see cref="RedisAccountByteStore"/> + script dispatch (FakeRedis → engine).
/// These cases must stay aligned with live Lua EVAL tests.
/// </summary>
public sealed class AccountByteIdempotencyTests
{
    [Fact]
    public async Task SameBatch_AppliedTwice_RedisUnchanged()
    {
        var store = CreateStore();
        Assert.Equal(900, await store.ApplyAsync("alice", "batch-a", 100, 900));
        Assert.Equal(900, await store.ApplyAsync("alice", "batch-a", 100, 900));
        Assert.Equal(900, await store.ObserveAsync("alice"));
    }

    [Fact]
    public async Task SameBatch_AppliedTenTimes_RedisUnchanged()
    {
        var store = CreateStore();
        Assert.Equal(900, await store.ApplyAsync("alice", "batch-a", 100, 900));
        for (var i = 0; i < 9; i++)
        {
            Assert.Equal(900, await store.ApplyAsync("alice", "batch-a", 100, 900));
        }

        Assert.Equal(900, await store.ObserveAsync("alice"));
    }

    [Fact]
    public async Task BatchAThenB_FloorsToDurable800()
    {
        var store = CreateStore();
        Assert.Equal(900, await store.ApplyAsync("alice", "A", 100, 900));
        Assert.Equal(800, await store.ApplyAsync("alice", "B", 100, 800));
        Assert.Equal(800, await store.ObserveAsync("alice"));
    }

    [Fact]
    public async Task BatchBThenA_DoesNotExceedDurable800()
    {
        var store = CreateStore();
        Assert.Equal(800, await store.ApplyAsync("alice", "B", 100, 800));
        Assert.Equal(800, await store.ApplyAsync("alice", "A", 100, 900));
        Assert.Equal(800, await store.ObserveAsync("alice"));
    }

    [Fact]
    public async Task ADuplicatedAfterB_Unchanged()
    {
        var store = CreateStore();
        Assert.Equal(800, await store.ApplyAsync("alice", "B", 100, 800));
        Assert.Equal(800, await store.ApplyAsync("alice", "A", 100, 900));
        Assert.Equal(800, await store.ApplyAsync("alice", "A", 100, 900));
        Assert.Equal(800, await store.ObserveAsync("alice"));
    }

    [Fact]
    public async Task BDuplicatedAfterA_Unchanged()
    {
        var store = CreateStore();
        Assert.Equal(900, await store.ApplyAsync("alice", "A", 100, 900));
        Assert.Equal(800, await store.ApplyAsync("alice", "B", 100, 800));
        Assert.Equal(800, await store.ApplyAsync("alice", "B", 100, 800));
        Assert.Equal(800, await store.ObserveAsync("alice"));
    }

    [Fact]
    public async Task AmbiguousResult_RetrySameBatch_DoesNotConsumeTwice()
    {
        var redis = new FakeRedisService();
        var store = new RedisAccountByteStore(redis);
        Assert.Equal(900, await store.ApplyAsync("alice", "batch-a", 100, 900));
        Assert.Equal(900, await store.ApplyAsync("alice", "batch-a", 100, 900));
        Assert.Equal(900, await store.ObserveAsync("alice"));
    }

    [Fact]
    public async Task MissingKey_RepeatedSameBatch_InitializesOnce()
    {
        var store = CreateStore();
        Assert.Equal(900, await store.ApplyAsync("alice", "batch-a", 100, 900));
        Assert.Equal(900, await store.ApplyAsync("alice", "batch-a", 100, 900));
        Assert.Equal(900, await store.ObserveAsync("alice"));
    }

    [Fact]
    public async Task RedisBelowMysql_NeverRepairedUpward()
    {
        var redis = new FakeRedisService();
        var store = new RedisAccountByteStore(redis);
        var key = System.Text.Encoding.UTF8.GetString(AccountByteKeys.Create("alice"));
        redis.Database.AccountByteEngine.Write(key, 800);
        Assert.Equal(800, await store.ApplyAsync("alice", "batch-a", 100, 900));
        Assert.Equal(800, await store.ObserveAsync("alice"));
    }

    [Fact]
    public async Task RedisAboveMysql_FloorsDown()
    {
        var redis = new FakeRedisService();
        var store = new RedisAccountByteStore(redis);
        var key = System.Text.Encoding.UTF8.GetString(AccountByteKeys.Create("alice"));
        redis.Database.AccountByteEngine.Write(key, 50_000);
        Assert.Equal(900, await store.ApplyAsync("alice", "batch-a", 100, 900));
        Assert.Equal(900, await store.ObserveAsync("alice"));
    }

    [Fact]
    public async Task ConcurrentNodes_WithDuplicateRetries_FinalIsMinMysqlAfter()
    {
        var redis = new FakeRedisService();
        var store = new RedisAccountByteStore(redis);
        await Task.WhenAll(
            store.ApplyAsync("alice", "A", 100, 900).AsTask(),
            store.ApplyAsync("alice", "B", 100, 800).AsTask(),
            store.ApplyAsync("alice", "A", 100, 900).AsTask(),
            store.ApplyAsync("alice", "B", 100, 800).AsTask());
        Assert.Equal(800, await store.ObserveAsync("alice"));
    }

    private static RedisAccountByteStore CreateStore() => new(new FakeRedisService());
}
