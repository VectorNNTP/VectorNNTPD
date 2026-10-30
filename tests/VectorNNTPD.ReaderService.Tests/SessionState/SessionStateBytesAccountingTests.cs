using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Core;
using VectorNNTPD.ReaderService.Hosting;
using VectorNNTPD.ReaderService.Logging;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.ReaderService.SessionState.RateLimiting;
namespace VectorNNTPD.ReaderService.Tests.SessionState;

[Collection(SerilogCollection.Name)]
public sealed class SessionStateBytesAccountingTests
{
    private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress V4B = IPAddress.Parse("198.51.100.20");
    private static readonly IPAddress V6A = IPAddress.Parse("2001:db8::10");

    [Fact]
    public void Host_RegistersExactlyOneSessionStateService_AndNoAccountByteService()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpReaderHosting(includePlaceholderService: false);
        using var host = builder.Build();
        var services = host.Services.GetServices<IApplicationService>().ToArray();
        Assert.Equal(1, services.Count(static s => s is SessionStateService));
        Assert.DoesNotContain(services, static s => s.GetType().Name == "AccountByteService");
        Assert.DoesNotContain(services, static s => s.GetType().Name is "RateLimitService" or "AccountRateService" or "RateScheduler");
        Assert.NotNull(host.Services.GetService<IAccountByteAccountant>());
        Assert.NotNull(host.Services.GetService<VectorNNTPD.ReaderService.SessionState.RateLimiting.IAccountRateAllocator>());
        Assert.Null(typeof(SessionStateService).Assembly.GetType("VectorNNTPD.ReaderService.AccountBytes.AccountByteService"));
        Assert.Null(typeof(SessionStateService).Assembly.GetType("VectorNNTPD.ReaderService.SessionState.RateLimiting.RateLimitService"));
    }

    [Fact]
    public async Task OneAccountThreeSessions_OneCombinedEvalPerCycle()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount("alice", 1_000_000);
        var bytes = new AccountByteTracker(
            durable,
            new RedisAccountByteStore(redis),
            NullLogger<AccountByteTracker>.Instance);
        var node = CreateNode(store, bytes);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s3", V6A));
        bytes.CreateSink("alice").ObserveCopied(100);
        bytes.CreateSink("alice").ObserveCopied(200);
        bytes.CreateSink("alice").ObserveCopied(300);
        var afterAdmit = redis.Database.ScriptEvaluateCount;

        await bytes.CommitDurableAsync();
        await node.RenewLeasesAsync();
        await bytes.ApplyCommittedAsync();

        Assert.Equal(afterAdmit + 1, redis.Database.ScriptEvaluateCount);
        Assert.Equal(1, durable.ConsumeCalls);
        Assert.Equal(999_400, durable.Remaining("alice"));
        Assert.False(bytes.TryGetCommittedBatch("alice", out _));
        Assert.Equal(999_400, await bytes.ObserveRemainingAsync("alice"));
    }

    [Fact]
    public async Task TwoAccounts_RemainIsolated_OneCombinedEvalEach()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount("alice", 10_000);
        durable.SeedByteAccount("bob", 20_000);
        var bytes = new AccountByteTracker(
            durable,
            new RedisAccountByteStore(redis),
            NullLogger<AccountByteTracker>.Instance);
        var node = CreateNode(store, bytes);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "a1", V4A, "alice"));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "b1", V6A, "bob"));
        bytes.CreateSink("alice").ObserveCopied(100);
        bytes.CreateSink("bob").ObserveCopied(250);
        var afterAdmit = redis.Database.ScriptEvaluateCount;

        await bytes.CommitDurableAsync();
        await node.RenewLeasesAsync();
        await bytes.ApplyCommittedAsync();

        Assert.Equal(afterAdmit + 2, redis.Database.ScriptEvaluateCount);
        Assert.Equal(9_900, durable.Remaining("alice"));
        Assert.Equal(19_750, durable.Remaining("bob"));
        Assert.Equal(9_900, await bytes.ObserveRemainingAsync("alice"));
        Assert.Equal(19_750, await bytes.ObserveRemainingAsync("bob"));
    }

    [Fact]
    public async Task CombinedEval_AppliesBytesWhenRenewIsLost()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount("alice", 5_000);
        var bytes = new AccountByteTracker(
            durable,
            new RedisAccountByteStore(redis),
            NullLogger<AccountByteTracker>.Instance);
        var node = CreateNode(store, bytes);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A));
        bytes.CreateSink("alice").ObserveCopied(40);
        await bytes.CommitDurableAsync();
        await node.ReleaseAllOwnershipAsync();

        await node.RenewLeasesAsync();
        await bytes.ApplyCommittedAsync();

        Assert.Equal(4_960, durable.Remaining("alice"));
        Assert.False(bytes.TryGetCommittedBatch("alice", out _));
        Assert.Equal(4_960, await bytes.ObserveRemainingAsync("alice"));
    }

    [Fact]
    public async Task CombinedEval_StaleGeneration_StillFloorsHighRedis_ThroughTracker()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var cluster = new RedisAccountByteStore(redis);
        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount("alice", 5_000);
        var bytes = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        var node = CreateNode(store, bytes);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A));
        Assert.Equal(50_000, await cluster.ApplyAsync("alice", "seed-high", 0, 50_000));
        bytes.CreateSink("alice").ObserveCopied(40);
        await bytes.CommitDurableAsync();
        Assert.True(bytes.TryGetCommittedBatch("alice", out _));
        Assert.Equal(4_960, durable.Remaining("alice"));
        Assert.Equal(50_000, await cluster.ObserveAsync("alice"));

        var sourceKey = System.Text.Encoding.UTF8.GetString(SessionStateKeys.CreateSource("alice"));
        Assert.True(
            redis.Database.SessionStateEngine.TryGetOwnership(
                sourceKey,
                SessionStateKeys.SourceField("192.0.2.10", node.OwnerId),
                out var expiry,
                out var generation,
                out var count));
        redis.Database.SessionStateEngine.WriteOwnership(
            sourceKey,
            SessionStateKeys.SourceField("192.0.2.10", node.OwnerId),
            expiry,
            generation + 1,
            count);

        var afterSetup = redis.Database.ScriptEvaluateCount;
        await node.RenewLeasesAsync();
        await bytes.ApplyCommittedAsync();

        Assert.Equal(afterSetup + 1, redis.Database.ScriptEvaluateCount);
        Assert.False(bytes.TryGetCommittedBatch("alice", out _));
        Assert.Equal(4_960, await cluster.ObserveAsync("alice"));
        Assert.Equal(4_960, await bytes.ObserveRemainingAsync("alice"));
        Assert.Equal(1, durable.ConsumeCalls);
    }

    [Fact]
    public async Task SessionStateService_LostRenew_StillCompletesApply()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var cluster = new RedisAccountByteStore(redis);
        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount("alice", 8_000);
        var bytes = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        var node = CreateNode(store, bytes);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A));
        Assert.Equal(99_000, await cluster.ApplyAsync("alice", "seed-high", 0, 99_000));
        bytes.CreateSink("alice").ObserveCopied(80);
        await node.ReleaseAllOwnershipAsync();
        var afterRelease = redis.Database.ScriptEvaluateCount;

        var clock = new ControllableTimeProvider();
        var service = new SessionStateService(
            node,
            bytes,
            NullLogger<SessionStateService>.Instance,
            clock,
            TimeSpan.FromSeconds(10));
        await service.StartAsync(CancellationToken.None);
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!clock.HasScheduledTimers)
            {
                wait.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            clock.Advance(TimeSpan.FromSeconds(10));
            while (durable.ConsumeCalls < 1 || bytes.TryGetCommittedBatch("alice", out _))
            {
                wait.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            Assert.Equal(afterRelease + 1, redis.Database.ScriptEvaluateCount);
            Assert.Equal(7_920, durable.Remaining("alice"));
            Assert.Equal(7_920, await cluster.ObserveAsync("alice"));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.False(bytes.TryGetCommittedBatch("alice", out _));
    }

    [Fact]
    public async Task EmptyCycle_NoPendingNoOwnership_ZeroRedis()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount("alice", 1_000);
        var bytes = new AccountByteTracker(
            durable,
            new RedisAccountByteStore(redis),
            NullLogger<AccountByteTracker>.Instance);
        var node = CreateNode(store, bytes);
        var before = redis.Database.ScriptEvaluateCount;

        await bytes.CommitDurableAsync();
        await node.RenewLeasesAsync();
        await bytes.ApplyCommittedAsync();

        Assert.Equal(before, redis.Database.ScriptEvaluateCount);
        Assert.Equal(0, durable.ConsumeCalls);
        Assert.Equal(1_000, durable.Remaining("alice"));
    }

    [Fact]
    public void PackedReturn_DistinguishesSuccessZeroFromFailureZero()
    {
        Assert.Equal(0, SessionStateBytePack.Encode(renewed: true, remaining: 0));
        Assert.Equal(-1, SessionStateBytePack.Encode(renewed: false, remaining: 0));

        var successZero = SessionStateBytePack.Decode(0);
        Assert.Equal(SessionStateRenewStatus.Renewed, successZero.Status);
        Assert.Equal(0, successZero.Remaining);

        var lostZero = SessionStateBytePack.Decode(-1);
        Assert.Equal(SessionStateRenewStatus.Lost, lostZero.Status);
        Assert.Equal(0, lostZero.Remaining);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(100L)]
    [InlineData(10_000_000_000_000L)]
    [InlineData(long.MaxValue)]
    public void PackedReturn_RoundTripsRemaining_ForSuccessAndLoss(long remaining)
    {
        var success = SessionStateBytePack.Decode(SessionStateBytePack.Encode(renewed: true, remaining));
        Assert.Equal(SessionStateRenewStatus.Renewed, success.Status);
        Assert.Equal(remaining, success.Remaining);

        var lost = SessionStateBytePack.Decode(SessionStateBytePack.Encode(renewed: false, remaining));
        Assert.Equal(SessionStateRenewStatus.Lost, lost.Status);
        Assert.Equal(remaining, lost.Remaining);
    }

    [Fact]
    public void PackedReturn_ClampsNegativeRemainingAndLostMaxValueIsMinValue()
    {
        Assert.Equal(0, SessionStateBytePack.Encode(renewed: true, remaining: -4));
        Assert.Equal(-1, SessionStateBytePack.Encode(renewed: false, remaining: -4));
        Assert.Equal(long.MinValue, SessionStateBytePack.Encode(renewed: false, remaining: long.MaxValue));
        var decoded = SessionStateBytePack.Decode(long.MinValue);
        Assert.Equal(SessionStateRenewStatus.Lost, decoded.Status);
        Assert.Equal(long.MaxValue, decoded.Remaining);
    }

    [Fact]
    public void CombinedScript_DoesNotSubtractConsumed()
    {
        Assert.DoesNotContain("consumed", SessionStateScripts.RenewAndApply, StringComparison.Ordinal);
        Assert.DoesNotContain("ARGV[7 + (ipCount * 2)]", SessionStateScripts.RenewAndApply, StringComparison.Ordinal);
        Assert.Contains("tonumber(ARGV[8 + (ipCount * 2)])", SessionStateScripts.RenewAndApply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ByteOnlyAccount_WithoutSessionOwnership_UsesApplyOnly()
    {
        var redis = new FakeRedisService();
        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount("alice", 8_000);
        var bytes = new AccountByteTracker(
            durable,
            new RedisAccountByteStore(redis),
            NullLogger<AccountByteTracker>.Instance);
        var node = CreateNode(new RedisSessionStateStore(redis), bytes);
        bytes.CreateSink("alice").ObserveCopied(80);
        var before = redis.Database.ScriptEvaluateCount;

        await bytes.CommitDurableAsync();
        await node.RenewLeasesAsync();
        await bytes.ApplyCommittedAsync();

        Assert.Equal(before + 1, redis.Database.ScriptEvaluateCount);
        Assert.Equal(7_920, durable.Remaining("alice"));
        Assert.Equal(7_920, await bytes.ObserveRemainingAsync("alice"));
    }

    [Fact]
    public async Task CombinedEval_DuplicateBatchIsIdempotent()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount("alice", 1_000);
        var cluster = new RedisAccountByteStore(redis);
        var bytes = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        var node = CreateNode(store, bytes);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A));
        bytes.CreateSink("alice").ObserveCopied(25);
        await bytes.CommitDurableAsync();
        Assert.True(bytes.TryGetCommittedBatch("alice", out var batch));

        var now = DateTimeOffset.UtcNow;
        var first = await store.RenewAndApplyAsync(
            "alice",
            node.OwnerId,
            sessionGeneration: 0,
            [],
            now,
            SessionStateDefaults.LeaseTtl,
            batch.BatchId,
            batch.Consumed,
            batch.MysqlRemaining);
        var replay = await store.RenewAndApplyAsync(
            "alice",
            node.OwnerId,
            sessionGeneration: 0,
            [],
            now,
            SessionStateDefaults.LeaseTtl,
            batch.BatchId,
            batch.Consumed,
            batch.MysqlRemaining);

        Assert.Equal(SessionStateRenewStatus.Renewed, first.Renew);
        Assert.Equal(first.Remaining, replay.Remaining);
        Assert.Equal(975, first.Remaining);
        Assert.Equal(1, durable.ConsumeCalls);
    }

    [Fact]
    public async Task SessionStateCycle_DoesNotProduceTwoRedisOpsPerOwnedAccount()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var durable = new InMemoryAccountByteDurableStore();
        durable.SeedByteAccount("alice", 50_000);
        var bytes = new AccountByteTracker(
            durable,
            new RedisAccountByteStore(redis),
            NullLogger<AccountByteTracker>.Instance);
        var node = CreateNode(store, bytes);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4B));
        bytes.CreateSink("alice").ObserveCopied(10);
        var service = new SessionStateService(
            node,
            bytes,
            NullLogger<SessionStateService>.Instance,
            TimeProvider.System,
            TimeSpan.FromHours(1));
        var afterAdmit = redis.Database.ScriptEvaluateCount;

        await service.StartAsync(CancellationToken.None);
        await bytes.CommitDurableAsync();
        await node.RenewLeasesAsync();
        await bytes.ApplyCommittedAsync();
        await service.StopAsync(CancellationToken.None);

        var cycleOps = redis.Database.ScriptEvaluateCount - afterAdmit;
        Assert.True(cycleOps < 4, $"expected fewer than 2N+final ops, observed {cycleOps}");
        Assert.True(cycleOps >= 1);
        Assert.Equal(49_990, durable.Remaining("alice"));
    }

    [Fact]
    public void CombinedScript_DoesNotEarlyReturnBeforeApply()
    {
        var script = SessionStateScripts.RenewAndApply;
        var applyFn = script.IndexOf("local function apply_bytes()", StringComparison.Ordinal);
        var firstReturn0 = script.IndexOf("return 0", StringComparison.Ordinal);
        var remainingReturn = script.IndexOf("return remaining", StringComparison.Ordinal);
        Assert.True(applyFn > 0);
        Assert.True(firstReturn0 > applyFn);
        Assert.True(remainingReturn > applyFn);
        Assert.Contains("return -remaining - 1", script, StringComparison.Ordinal);
        Assert.Contains("KEYS[3]", script, StringComparison.Ordinal);
        Assert.Contains("HEXISTS", script, StringComparison.Ordinal);
        Assert.Contains("if next > mysql then", script, StringComparison.Ordinal);
    }

    private static DistributedSessionStateTracker CreateNode(
        ISessionStateStore membership,
        IAccountByteAccountant bytes) =>
        new(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01",
            TimeProvider.System,
            bytes: bytes);

    private static ValueTask<SessionAdmissionResult> Admit(
        DistributedSessionStateTracker node,
        string sessionId,
        IPAddress ip,
        string account = "alice") =>
        node.TryAdmitAsync(account, sessionId, ip, sessionLimit: 5, srcIpLimit: 3);
}
