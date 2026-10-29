using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;

namespace VectorNNTPD.ReaderService.Tests.AccountBytes;

public sealed class AccountByteTopUpTests
{
    [Fact]
    public async Task TopUpWithoutInvalidation_KeepsEffectiveAtRedis()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 100);
        await cluster.ApplyAsync("alice", "seed", 0, 50);
        Assert.Equal(50, await tracker.ObserveRemainingAsync("alice"));
        durable.SeedByteAccount("alice", 1000);
        Assert.Equal(50, await tracker.ObserveRemainingAsync("alice"));
        Assert.Equal(50, await cluster.ObserveAsync("alice"));
    }

    [Fact]
    public async Task DeleteThenObserve_ExposesDurableTopUp()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 100);
        await cluster.ApplyAsync("alice", "seed", 0, 50);
        durable.SeedByteAccount("alice", 1000);
        Assert.True(await tracker.DeleteAccountByteStateAsync("alice"));
        Assert.Equal(AccountByteKeys.Missing, await cluster.ObserveAsync("alice"));
        Assert.Equal(1000, await tracker.ObserveRemainingAsync("alice"));
    }

    [Fact]
    public async Task DeleteThenApply_InitializesRedisToCurrentMysql()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 100);
        await cluster.ApplyAsync("alice", "seed", 0, 50);
        durable.SeedByteAccount("alice", 1000);
        Assert.True(await tracker.DeleteAccountByteStateAsync("alice"));
        Assert.Equal(1000, await cluster.ApplyAsync("alice", "after-topup", 0, 1000));
        Assert.Equal(1000, await cluster.ObserveAsync("alice"));
    }

    [Fact]
    public async Task ConsumptionAfterTopUpInvalidation_BehavesNormally()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 100);
        await cluster.ApplyAsync("alice", "seed", 0, 50);
        durable.SeedByteAccount("alice", 1000);
        Assert.True(await tracker.DeleteAccountByteStateAsync("alice"));
        tracker.CreateSink("alice").ObserveCopied(100);
        await tracker.ReconcileAsync();
        Assert.Equal(900, durable.Remaining("alice"));
        Assert.Equal(900, await cluster.ObserveAsync("alice"));
        Assert.Equal(900, await tracker.ObserveRemainingAsync("alice"));
    }

    [Fact]
    public async Task StaleRedisAfterTopUp_NeverIncreasesEffectiveBeforeInvalidation()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 100);
        await cluster.ApplyAsync("alice", "seed", 0, 50);
        durable.SeedByteAccount("alice", 1000);
        Assert.Equal(50, await tracker.ObserveRemainingAsync("alice"));
        Assert.Equal(50, await cluster.ApplyAsync("alice", "later", 10, 1000));
        Assert.Equal(50, await tracker.ObserveRemainingAsync("alice"));
    }

    [Fact]
    public async Task ExplicitInvalidationThenObserve_ExposesNewDurableQuota()
    {
        var (tracker, durable, _) = Create();
        durable.SeedByteAccount("alice", 100);
        await tracker.DeleteAccountByteStateAsync("alice");
        durable.SeedByteAccount("alice", 1000);
        Assert.True(await tracker.DeleteAccountByteStateAsync("alice"));
        Assert.Equal(1000, await tracker.ObserveRemainingAsync("alice"));
    }

    [Fact]
    public async Task MissingRedis_NeverInitializesAboveCurrentMysql()
    {
        var store = new InMemoryAccountByteStore();
        Assert.Equal(AccountByteKeys.Missing, await store.ObserveAsync("alice"));
        Assert.Equal(400, await store.ApplyAsync("alice", "init", 100, 400));
        Assert.Equal(400, await store.ObserveAsync("alice"));
    }

    [Fact]
    public async Task ExhaustedAccount_TopUpStaysZeroUntilInvalidation()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 0);
        await cluster.ApplyAsync("alice", "seed", 0, 0);
        tracker.MarkExhausted("alice");
        Assert.Equal(0, await tracker.ObserveRemainingAsync("alice"));
        Assert.True(tracker.IsExhausted("alice"));

        durable.SeedByteAccount("alice", 1000);
        Assert.Equal(0, await tracker.ObserveRemainingAsync("alice"));
        Assert.True(tracker.IsExhausted("alice"));

        Assert.True(await tracker.DeleteAccountByteStateAsync("alice"));
        Assert.False(tracker.IsExhausted("alice"));
        Assert.Equal(1000, await tracker.ObserveRemainingAsync("alice"));
        Assert.Equal(AccountByteKeys.Missing, await cluster.ObserveAsync("alice"));
    }

    [Fact]
    public async Task DeleteWhenRedisUnavailable_DoesNotClearExhausted()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 0);
        await cluster.ApplyAsync("alice", "seed", 0, 0);
        tracker.MarkExhausted("alice");
        cluster.Unavailable = true;
        Assert.False(await tracker.DeleteAccountByteStateAsync("alice"));
        Assert.True(tracker.IsExhausted("alice"));
        Assert.Equal(0, cluster.Engine.Observe(InMemoryAccountByteStore.EncodingKey("alice")));
    }

    private static (AccountByteTracker Tracker, InMemoryAccountByteDurableStore Durable, InMemoryAccountByteStore Cluster) Create()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        var tracker = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        return (tracker, durable, cluster);
    }
}
