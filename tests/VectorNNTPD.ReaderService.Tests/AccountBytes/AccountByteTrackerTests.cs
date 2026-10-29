using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;

namespace VectorNNTPD.ReaderService.Tests.AccountBytes;

public sealed class AccountByteTrackerTests
{
    [Fact]
    public void Types_UseSessionStateBytesAccountingNamespace()
    {
        Assert.Equal("VectorNNTPD.ReaderService.SessionState.BytesAccounting", typeof(IAccountByteAccountant).Namespace);
        Assert.Equal("VectorNNTPD.ReaderService.SessionState.BytesAccounting", typeof(AccountByteTracker).Namespace);
        Assert.StartsWith("VectorNNTPD.ReaderService.SessionState", typeof(AccountByteTracker).Namespace);
        Assert.Null(typeof(SessionStateService).Assembly.GetType("VectorNNTPD.ReaderService.AccountBytes.AccountByteService"));
    }

    [Fact]
    public void OneSession_AccumulatesPending_WithoutStoreIo()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 10_000);
        tracker.CreateSink("alice").ObserveCopied(100);
        tracker.CreateSink("alice").ObserveCopied(50);
        Assert.Equal(150, tracker.PendingBytes("alice"));
        Assert.Equal(0, durable.ConsumeCalls);
        Assert.Equal(0, cluster.ApplyCalls);
    }

    [Fact]
    public void MultipleSessionsSameAccount_SharePending()
    {
        var (tracker, durable, _) = Create();
        durable.SeedByteAccount("alice", 10_000);
        tracker.CreateSink("alice").ObserveCopied(10);
        tracker.CreateSink("alice").ObserveCopied(20);
        Assert.Equal(30, tracker.PendingBytes("alice"));
    }

    [Fact]
    public void MultipleAccounts_AreIndependent()
    {
        var (tracker, durable, _) = Create();
        durable.SeedByteAccount("alice", 10_000);
        durable.SeedByteAccount("bob", 10_000);
        tracker.CreateSink("alice").ObserveCopied(7);
        tracker.CreateSink("bob").ObserveCopied(9);
        Assert.Equal(7, tracker.PendingBytes("alice"));
        Assert.Equal(9, tracker.PendingBytes("bob"));
    }

    [Fact]
    public void ConcurrentWriters_SumExactly()
    {
        var (tracker, durable, _) = Create();
        durable.SeedByteAccount("alice", 1_000_000);
        var sink = tracker.CreateSink("alice");
        Parallel.For(0, 100, _ => sink.ObserveCopied(3));
        Assert.Equal(300, tracker.PendingBytes("alice"));
    }

    [Fact]
    public void EmptySinkName_IsIgnored()
    {
        var (tracker, _, _) = Create();
        tracker.CreateSink("").ObserveCopied(50);
        tracker.CreateSink(" ").ObserveCopied(50);
        Assert.Equal(0, tracker.PendingBytes(""));
    }

    [Fact]
    public async Task EmptyBatch_DoesNoStoreWork()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 1000);
        await tracker.ReconcileAsync();
        Assert.Equal(0, durable.ConsumeCalls);
        Assert.Equal(0, cluster.ApplyCalls);
    }

    [Fact]
    public async Task Reconcile_OneAccountBatch_MysqlThenRedis()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 1000);
        tracker.CreateSink("alice").ObserveCopied(100);
        await tracker.ReconcileAsync();
        Assert.Equal(0, tracker.PendingBytes("alice"));
        Assert.Equal(900, durable.Remaining("alice"));
        Assert.Equal(900, await cluster.ObserveAsync("alice"));
        Assert.Equal(1, durable.ConsumeCalls);
        Assert.Equal(1, cluster.ApplyCalls);
    }

    [Fact]
    public async Task Reconcile_MultipleLocalSessions_OneAccountUpdate()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 1000);
        tracker.CreateSink("alice").ObserveCopied(40);
        tracker.CreateSink("alice").ObserveCopied(60);
        await tracker.ReconcileAsync();
        Assert.Equal(900, durable.Remaining("alice"));
        Assert.Equal(900, await cluster.ObserveAsync("alice"));
        Assert.Equal(1, durable.ConsumeCalls);
    }

    [Fact]
    public async Task Reconcile_DecrementLargerThanRemaining_StopsAtZero()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 1000);
        tracker.CreateSink("alice").ObserveCopied(1500);
        await tracker.ReconcileAsync();
        Assert.Equal(0, durable.Remaining("alice"));
        Assert.Equal(0, await cluster.ObserveAsync("alice"));
        Assert.True(tracker.IsExhausted("alice"));
    }

    [Fact]
    public async Task Reconcile_AlreadyZero_RemainsZero()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 0);
        tracker.CreateSink("alice").ObserveCopied(80);
        await tracker.ReconcileAsync();
        Assert.Equal(0, durable.Remaining("alice"));
        Assert.Equal(0, await cluster.ObserveAsync("alice"));
        Assert.True(tracker.IsExhausted("alice"));
    }

    [Fact]
    public async Task TwoNodes_ConsumeSameAccount_DurableReflectsBoth()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        durable.SeedByteAccount("alice", 8_000);
        var nodeA = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        var nodeB = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        nodeA.CreateSink("alice").ObserveCopied(300);
        nodeB.CreateSink("alice").ObserveCopied(500);
        await nodeA.ReconcileAsync();
        await nodeB.ReconcileAsync();
        Assert.Equal(7_200, durable.Remaining("alice"));
        Assert.Equal(7_200, await cluster.ObserveAsync("alice"));
    }

    [Fact]
    public async Task TwoNodes_LostRedisReplies_RetryDoesNotDoubleCharge()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        durable.SeedByteAccount("alice", 1000);
        var nodeA = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        var nodeB = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        nodeA.CreateSink("alice").ObserveCopied(100);
        nodeB.CreateSink("alice").ObserveCopied(100);
        cluster.HideNextApplyResults = 2;
        await nodeA.ReconcileAsync();
        await nodeB.ReconcileAsync();
        Assert.Equal(800, durable.Remaining("alice"));
        Assert.Equal(2, durable.ConsumeCalls);
        Assert.Equal(800, await cluster.ObserveAsync("alice"));
        await nodeA.ReconcileAsync();
        await nodeB.ReconcileAsync();
        await nodeA.ReconcileAsync();
        await nodeB.ReconcileAsync();
        Assert.Equal(2, durable.ConsumeCalls);
        Assert.Equal(800, durable.Remaining("alice"));
        Assert.Equal(800, await cluster.ObserveAsync("alice"));
        Assert.False(nodeA.MysqlCommitted("alice"));
        Assert.False(nodeB.MysqlCommitted("alice"));
    }

    [Fact]
    public async Task MysqlUnavailable_KeepsInFlight_DoesNotChargeRedis()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 1000);
        tracker.CreateSink("alice").ObserveCopied(100);
        durable.Unavailable = true;
        await tracker.ReconcileAsync();
        Assert.Equal(100, tracker.InFlightBytes("alice"));
        Assert.False(tracker.MysqlCommitted("alice"));
        Assert.Equal(1000, durable.Remaining("alice"));
        Assert.Equal(0, cluster.ApplyCalls);
        durable.Unavailable = false;
        await tracker.ReconcileAsync();
        Assert.Equal(900, durable.Remaining("alice"));
        Assert.Equal(900, await cluster.ObserveAsync("alice"));
    }

    [Fact]
    public async Task PendingDuringCommittedBatch_StaysPending_UntilApplyCompletes()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 1_000);
        tracker.CreateSink("alice").ObserveCopied(25);
        await tracker.CommitDurableAsync();
        Assert.True(tracker.MysqlCommitted("alice"));
        Assert.Equal(975, durable.Remaining("alice"));
        tracker.CreateSink("alice").ObserveCopied(25);
        await tracker.CommitDurableAsync();
        Assert.Equal(1, durable.ConsumeCalls);
        Assert.Equal(25, tracker.PendingBytes("alice"));
        Assert.Equal(25, tracker.InFlightBytes("alice"));
        await tracker.ApplyCommittedAsync();
        Assert.False(tracker.MysqlCommitted("alice"));
        Assert.Equal(25, tracker.PendingBytes("alice"));
        await tracker.ReconcileAsync();
        Assert.Equal(950, durable.Remaining("alice"));
        Assert.Equal(950, await cluster.ObserveAsync("alice"));
        Assert.Equal(0, tracker.PendingBytes("alice"));
        Assert.Equal(2, durable.ConsumeCalls);
    }

    [Fact]
    public async Task RedisSuccessWithLostResponse_RetriesSameBatch_DoesNotDoubleFloor()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 1000);
        tracker.CreateSink("alice").ObserveCopied(100);
        cluster.HideNextApplyResults = 1;
        await tracker.ReconcileAsync();
        Assert.Equal(900, durable.Remaining("alice"));
        Assert.Equal(1, durable.ConsumeCalls);
        Assert.True(tracker.MysqlCommitted("alice"));
        Assert.NotNull(tracker.CurrentBatchId("alice"));
        Assert.Equal(900, await cluster.ObserveAsync("alice"));
        var batchId = tracker.CurrentBatchId("alice");
        await tracker.ReconcileAsync();
        Assert.Equal(1, durable.ConsumeCalls);
        Assert.NotNull(batchId);
        Assert.Equal(900, await cluster.ObserveAsync("alice"));
        Assert.False(tracker.MysqlCommitted("alice"));
        Assert.Null(tracker.CurrentBatchId("alice"));
    }

    [Fact]
    public async Task RedisUnavailableAfterMysql_RetriesRedisOnly()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 1000);
        tracker.CreateSink("alice").ObserveCopied(100);
        cluster.Unavailable = true;
        await tracker.ReconcileAsync();
        Assert.Equal(900, durable.Remaining("alice"));
        Assert.Equal(1, durable.ConsumeCalls);
        Assert.True(tracker.MysqlCommitted("alice"));
        Assert.Equal(100, tracker.InFlightBytes("alice"));
        cluster.Unavailable = false;
        await tracker.ReconcileAsync();
        Assert.Equal(1, durable.ConsumeCalls);
        Assert.Equal(900, await cluster.ObserveAsync("alice"));
        Assert.False(tracker.MysqlCommitted("alice"));
        Assert.Equal(0, tracker.InFlightBytes("alice"));
    }

    [Fact]
    public async Task RestartAfterMysql_DoesNotReplayBatch()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        durable.SeedByteAccount("alice", 1000);
        var first = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        first.CreateSink("alice").ObserveCopied(100);
        cluster.Unavailable = true;
        await first.ReconcileAsync();
        Assert.Equal(900, durable.Remaining("alice"));

        var restarted = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        cluster.Unavailable = false;
        await restarted.ReconcileAsync();
        Assert.Equal(1, durable.ConsumeCalls);
        Assert.Equal(900, durable.Remaining("alice"));
        Assert.Equal(AccountByteKeys.Missing, await cluster.ObserveAsync("alice"));
    }

    [Fact]
    public async Task ObserveRemaining_PrefersMinOfRedisAndMysql()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 500);
        await cluster.ApplyAsync("alice", AccountByteBatchId.Create(), 0, 800);
        Assert.Equal(500, await tracker.ObserveRemainingAsync("alice"));
        cluster.Engine.Write(InMemoryAccountByteStore.EncodingKey("alice"), 200);
        Assert.Equal(200, await tracker.ObserveRemainingAsync("alice"));
    }

    [Fact]
    public async Task ObserveRemaining_MissingRedis_UsesMysql()
    {
        var (tracker, durable, _) = Create();
        durable.SeedByteAccount("alice", 321);
        Assert.Equal(321, await tracker.ObserveRemainingAsync("alice"));
    }

    [Fact]
    public async Task ObserveRemaining_SeededAccount_UsesMysql()
    {
        var (tracker, durable, _) = Create();
        durable.SeedByteAccount("alice", 999);
        Assert.Equal(999, await tracker.ObserveRemainingAsync("alice"));
    }

    [Fact]
    public async Task Consume_AppliesToEverySeededAccount()
    {
        var (tracker, durable, cluster) = Create();
        durable.SeedByteAccount("alice", 500);
        tracker.CreateSink("alice").ObserveCopied(40);
        await tracker.ReconcileAsync();
        Assert.Equal(460, durable.Remaining("alice"));
        Assert.Equal(0, tracker.InFlightBytes("alice"));
        Assert.Equal(1, cluster.ApplyCalls);
    }

    [Fact]
    public async Task MissingAccount_DropsInFlight()
    {
        var (tracker, _, cluster) = Create();
        tracker.CreateSink("ghost").ObserveCopied(10);
        await tracker.ReconcileAsync();
        Assert.Equal(0, tracker.InFlightBytes("ghost"));
        Assert.Equal(0, cluster.ApplyCalls);
    }

    [Fact]
    public void MarkExhausted_IsVisibleToAllLocalSinks()
    {
        var (tracker, durable, _) = Create();
        durable.SeedByteAccount("alice", 10);
        _ = tracker.CreateSink("alice");
        Assert.False(tracker.IsExhausted("alice"));
        tracker.MarkExhausted("alice");
        Assert.True(tracker.IsExhausted("alice"));
    }

    private static (AccountByteTracker Tracker, InMemoryAccountByteDurableStore Durable, InMemoryAccountByteStore Cluster) Create()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        var tracker = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        return (tracker, durable, cluster);
    }
}
