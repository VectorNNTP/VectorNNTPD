using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.NntpDb;

namespace VectorNNTPD.ReaderService.Tests.AccountBytes;

/// <summary>
/// Production-contract locks for the thirteen AccountBytes invariants.
/// </summary>
public sealed class AccountByteInvariantTests
{
    [Fact]
    public async Task Mysql_NeverGoesBelowZero()
    {
        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount("alice", 100);
        var first = await durable.ConsumeAsync("alice", 150);
        Assert.Equal(0, first.Remaining);
        Assert.Equal(100, first.Consumed);
        var second = await durable.ConsumeAsync("alice", 50);
        Assert.Equal(0, second.Remaining);
        Assert.Equal(0, second.Consumed);
        Assert.Equal(0, durable.Remaining("alice"));
    }

    [Fact]
    public void Redis_NeverGoesBelowZero()
    {
        var engine = new AccountByteEngine();
        Assert.Equal(0, engine.Apply("k", "b1", consumed: 50, mysqlRemainingAfter: -9));
        engine.Write("k", 10);
        Assert.Equal(0, engine.Apply("k", "b2", consumed: 1_000, mysqlRemainingAfter: 0));
        Assert.True(engine.Observe("k") >= 0);
    }

    [Fact]
    public void RedisApply_NeverIncreasesRemaining()
    {
        var engine = new AccountByteEngine();
        engine.Write("k", 50);
        Assert.Equal(50, engine.Apply("k", "b1", consumed: 10, mysqlRemainingAfter: 800));
        Assert.Equal(50, engine.Apply("k", "b1", consumed: 10, mysqlRemainingAfter: 800));
    }

    [Fact]
    public void RedisApply_DoesNotDecreaseBecauseOfConsumed()
    {
        var engine = new AccountByteEngine();
        engine.Write("k", 1000);
        Assert.Equal(900, engine.Apply("k", "batch-a", consumed: 100, mysqlRemainingAfter: 900));
        Assert.Equal(900, engine.Apply("k", "batch-a", consumed: 100, mysqlRemainingAfter: 900));
        Assert.Equal(900, engine.Apply("k", "other", consumed: 500, mysqlRemainingAfter: 900));
    }

    [Fact]
    public void EvictedBatchMark_ReplayIsHarmless()
    {
        var engine = new AccountByteEngine();
        engine.Write("k", 10_000);
        Assert.Equal(9_000, engine.Apply("k", "oldest", 1_000, 9_000));
        var remaining = 9_000L;
        for (var i = 0; i < AccountByteBatchId.MaxRetainedMarks; i++)
        {
            remaining -= 1;
            Assert.Equal(remaining, engine.Apply("k", "n" + i, 1, remaining));
        }

        Assert.False(engine.WasApplied("k", "oldest"));
        Assert.Equal(remaining, engine.Apply("k", "oldest", 1_000, 9_000));
    }

    [Fact]
    public async Task OutOfOrderNodes_CannotIncreaseEffectiveQuota()
    {
        var store = new RedisAccountByteStore(new VectorNNTPD.ReaderService.Tests.TestDoubles.FakeRedisService());
        Assert.Equal(800, await store.ApplyAsync("alice", "B", 100, 800));
        Assert.Equal(800, await store.ApplyAsync("alice", "A", 100, 900));
        Assert.Equal(800, await store.ObserveAsync("alice"));
    }

    [Fact]
    public async Task MissingRedisKey_CannotRestoreAboveMysqlRemaining()
    {
        var store = new RedisAccountByteStore(new VectorNNTPD.ReaderService.Tests.TestDoubles.FakeRedisService());
        Assert.Equal(AccountByteKeys.Missing, await store.ObserveAsync("alice"));
        Assert.Equal(400, await store.ApplyAsync("alice", "batch-a", 100, 400));
        Assert.Equal(400, await store.ApplyAsync("alice", "batch-a", 100, 400));
        Assert.Equal(400, await store.ObserveAsync("alice"));
    }

    [Fact]
    public async Task Crash_NeverReplaysCommittedMysqlBatch()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        durable.SeedByteAccount("alice", 1000);
        var first = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        first.CreateSink("alice").ObserveCopied(100);
        cluster.Unavailable = true;
        await first.ReconcileAsync();
        Assert.Equal(900, durable.Remaining("alice"));
        Assert.Equal(1, durable.ConsumeCalls);

        var restarted = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        cluster.Unavailable = false;
        await restarted.ReconcileAsync();
        Assert.Equal(1, durable.ConsumeCalls);
        Assert.Equal(900, durable.Remaining("alice"));
        Assert.Equal(AccountByteKeys.Missing, await cluster.ObserveAsync("alice"));
    }

    [Fact]
    public async Task Crash_MayLosePending_NeverDoubleCharges()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        durable.SeedByteAccount("alice", 1000);
        var first = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        first.CreateSink("alice").ObserveCopied(250);
        Assert.Equal(250, first.PendingBytes("alice"));
        Assert.Equal(0, durable.ConsumeCalls);

        var restarted = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        await restarted.ReconcileAsync();
        Assert.Equal(0, durable.ConsumeCalls);
        Assert.Equal(1000, durable.Remaining("alice"));
    }

    [Fact]
    public async Task MysqlTopUp_HiddenWhileRedisKeyExists()
    {
        var (tracker, durable, cluster) = CreateTracker();
        durable.SeedByteAccount("alice", 800);
        await cluster.ApplyAsync("alice", "seed", 0, 800);
        Assert.Equal(800, await tracker.ObserveRemainingAsync("alice"));
        durable.SeedByteAccount("alice", 9_000);
        Assert.Equal(800, await tracker.ObserveRemainingAsync("alice"));
    }

    [Fact]
    public async Task MysqlTopUp_VisibleOnlyAfterRedisKeyIsGone()
    {
        var (tracker, durable, cluster) = CreateTracker();
        durable.SeedByteAccount("alice", 800);
        await cluster.ApplyAsync("alice", "seed", 0, 800);
        durable.SeedByteAccount("alice", 9_000);
        cluster.Engine.Delete(InMemoryAccountByteStore.EncodingKey("alice"));
        Assert.Equal(9_000, await tracker.ObserveRemainingAsync("alice"));
    }

    [Fact]
    public async Task StaleHighRedis_IsFlooredOnObserve_SoLaterTopUpStaysHidden()
    {
        var (tracker, durable, cluster) = CreateTracker();
        durable.SeedByteAccount("alice", 0);
        await cluster.ApplyAsync("alice", "stale", 0, 1_000);
        Assert.Equal(0, await tracker.ObserveRemainingAsync("alice"));
        Assert.Equal(0, await cluster.ObserveAsync("alice"));
        durable.SeedByteAccount("alice", 5_000);
        Assert.Equal(0, await tracker.ObserveRemainingAsync("alice"));
    }

    [Fact]
    public void Writer_HasNoStoreIo()
    {
        var source = File.ReadAllText(
            Path.Join(FindRepositoryRoot(), "src", "VectorNNTPD.ReaderService", "Session", "CommandProcessor", "NntpResponseWriter.cs"));
        Assert.Contains("_output.Advance(toCopy);", source, StringComparison.Ordinal);
        Assert.Contains("ObserveCopied(toCopy)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IAccountByteStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("INntpDbConnection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ScriptEvaluate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConsumeAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QueryRemainingAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionAttach_DoesNotUseCachedByteLimitForExhaustion()
    {
        var source = File.ReadAllText(
            Path.Join(FindRepositoryRoot(), "src", "VectorNNTPD.ReaderService", "Session", "NntpSession.cs"));
        Assert.Contains("ObserveRemainingAsync(policy.Username", source, StringComparison.Ordinal);
        Assert.Contains("if (remaining is <= 0)", source, StringComparison.Ordinal);
        Assert.Contains("ClearExhausted(policy.Username)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("policy.ByteLimit < 0", source, StringComparison.Ordinal);
        Assert.DoesNotContain("policy.ByteLimit <= 0", source, StringComparison.Ordinal);
    }

    private static (AccountByteTracker Tracker, InMemoryAccountByteDurableStore Durable, InMemoryAccountByteStore Cluster) CreateTracker()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        var tracker = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        return (tracker, durable, cluster);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "VectorNNTPD.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
