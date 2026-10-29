using VectorNNTPD.ReaderService.PostFilter.Quota;
using VectorNNTPD.ReaderService.Redis;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.PostFilter.Quota;

/// <summary>Static Lua/C# quota parity. These tests do not execute live Redis.</summary>
public sealed class PostFilterQuotaLuaStaticAuditTests
{
    [Fact]
    public async Task ProductionStore_DispatchesExactQuotaScripts()
    {
        var redis = new RecordingRedis();
        var store = new RedisPostFilterQuotaStore(redis);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(5_000);
        var windows = new PostFilterQuotaWindows(10_000, 1_000);
        var ceilings = new PostFilterQuotaCeilings(10, 0, 0, 10, 0, 0);
        var id = new PostFilterReservationId("n1", "a", 1);

        _ = await store.ReserveAsync("alice", id, now, windows, ceilings, 1, 1, 0, null);
        Assert.Same(PostFilterQuotaScripts.Reserve, redis.LastScript);

        _ = await store.CommitAsync("alice", id, now, windows, ceilings);
        Assert.Same(PostFilterQuotaScripts.Commit, redis.LastScript);

        _ = await store.ReleaseAsync("alice", id, now);
        Assert.Same(PostFilterQuotaScripts.Release, redis.LastScript);
    }

    [Fact]
    public void Scripts_UseSingleEval_NoClientGetIncr()
    {
        foreach (var script in AllScripts())
        {
            Assert.DoesNotContain("redis.call('GET'", script, StringComparison.Ordinal);
            Assert.DoesNotContain("redis.call('INCR'", script, StringComparison.Ordinal);
            Assert.DoesNotContain("redis.call('KEYS'", script, StringComparison.Ordinal);
            Assert.DoesNotContain("EVAL", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Scripts_UseOnlyDocumentedRedisCommands()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "HGETALL", "HGET", "HSET", "HDEL", "HLEN", "HINCRBY", "DEL", "EXISTS", "PEXPIRE",
        };
        foreach (var script in AllScripts())
        {
            foreach (var command in EnumerateRedisCalls(script))
            {
                Assert.Contains(command, allowed);
            }
        }
    }

    [Fact]
    public void Reserve_ChecksBeforeWrite_AndDenyWritesNothing()
    {
        var script = PostFilterQuotaScripts.Reserve;
        var hset = script.IndexOf("redis.call('HSET'", StringComparison.Ordinal);
        var deny = script.IndexOf("if maxML > 0", StringComparison.Ordinal);
        Assert.True(deny > 0 && deny < hset);
        Assert.Contains("return 1", script, StringComparison.Ordinal);
        Assert.Contains("return 7", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Commit_ReadsUnitsFromReservation_NotArgv()
    {
        Assert.Contains("rec.messages", PostFilterQuotaScripts.Commit, StringComparison.Ordinal);
        Assert.Contains("rec.bytes", PostFilterQuotaScripts.Commit, StringComparison.Ordinal);
        Assert.DoesNotContain("tonumber(ARGV[13])", PostFilterQuotaScripts.Commit, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_NeverIncrCommitted()
    {
        Assert.DoesNotContain("HINCRBY", PostFilterQuotaScripts.Release, StringComparison.Ordinal);
        Assert.DoesNotContain("c:m:", PostFilterQuotaScripts.Release, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreFailureMapping_IsUnavailable()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(5_000);
        var windows = new PostFilterQuotaWindows(10_000, 1_000);
        var ceilings = new PostFilterQuotaCeilings(10, 0, 0, 10, 0, 0);
        var id = new PostFilterReservationId("n1", "a", 1);

        var unavailable = new RecordingRedis { BeginSucceeds = false };
        var store = new RedisPostFilterQuotaStore(unavailable);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Unavailable,
            await store.ReserveAsync("alice", id, now, windows, ceilings, 1, 1, 0, null));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Unavailable,
            await store.CommitAsync("alice", id, now, windows, ceilings));

        var redisDown = new RecordingRedis { EvaluateException = new RedisUnavailableException("timeout") };
        store = new RedisPostFilterQuotaStore(redisDown);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Unavailable,
            await store.ReserveAsync("alice", id, now, windows, ceilings, 1, 1, 0, null));

        var canceled = new RecordingRedis { EvaluateException = new OperationCanceledException() };
        store = new RedisPostFilterQuotaStore(canceled);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReserveAsync("alice", id, now, windows, ceilings, 1, 1, 0, null).AsTask());
    }

    [Fact]
    public async Task ReserveCanceledAfterRedisWrite_IsStillReleasable()
    {
        var redis = new FakeRedisService
        {
            Database =
            {
                ScriptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                BlockScript = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            },
        };
        var store = new RedisPostFilterQuotaStore(redis);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(5_000);
        var windows = new PostFilterQuotaWindows(10_000, 1_000);
        var ceilings = new PostFilterQuotaCeilings(1, 0, 0, 0, 0, 0);
        var id = new PostFilterReservationId("n1", "a", 1);
        using var cts = new CancellationTokenSource();
        var reserve = store.ReserveAsync(
            "alice",
            id,
            now,
            windows,
            ceilings,
            1,
            1,
            0,
            null,
            cts.Token).AsTask();
        await redis.Database.ScriptStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        redis.Database.BlockScript!.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reserve);
        Assert.Equal(PostFilterQuotaReleaseStatus.Released, await store.ReleaseAsync("alice", id, now));
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await store.ReserveAsync(
                "alice",
                new PostFilterReservationId("n2", "b", 1),
                now,
                windows,
                ceilings,
                1,
                1,
                0,
                null));
    }

    [Fact]
    public async Task FakeRedis_DispatchesQuotaScriptsToEngine()
    {
        var redis = new FakeRedisService();
        var store = new RedisPostFilterQuotaStore(redis);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(5_000);
        var windows = new PostFilterQuotaWindows(10_000, 1_000);
        var ceilings = new PostFilterQuotaCeilings(1, 0, 0, 0, 0, 0);
        var first = new PostFilterReservationId("n1", "a", 1);
        var second = new PostFilterReservationId("n2", "b", 1);

        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await store.ReserveAsync("alice", first, now, windows, ceilings, 1, 1, 0, null));
        Assert.Equal(
            PostFilterQuotaReserveStatus.DeniedMessagesLong,
            await store.ReserveAsync("alice", second, now, windows, ceilings, 1, 1, 0, null));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await store.CommitAsync("alice", first, now, windows, ceilings));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Noop,
            await store.CommitAsync("alice", first, now, windows, ceilings));
    }

    private static IEnumerable<string> AllScripts()
    {
        yield return PostFilterQuotaScripts.Reserve;
        yield return PostFilterQuotaScripts.Commit;
        yield return PostFilterQuotaScripts.Release;
    }

    private static IEnumerable<string> EnumerateRedisCalls(string script)
    {
        const string prefix = "redis.call('";
        var start = 0;
        while (true)
        {
            var index = script.IndexOf(prefix, start, StringComparison.Ordinal);
            if (index < 0)
            {
                yield break;
            }

            var from = index + prefix.Length;
            var end = script.IndexOf('\'', from);
            yield return script[from..end];
            start = end + 1;
        }
    }

    private sealed class RecordingRedis : IRedisService, IRedisDatabase
    {
        public string? LastScript { get; private set; }

        public bool BeginSucceeds { get; set; } = true;

        public Exception? EvaluateException { get; set; }

        public IRedisDatabase Database => this;

        public bool IsUnavailable => !BeginSucceeds;

        public bool TryBeginOperation(out bool isRecoveryProbe)
        {
            isRecoveryProbe = false;
            return BeginSucceeds;
        }

        public void CompleteOperation(bool isRecoveryProbe, bool succeeded, Exception? exception = null)
        {
        }

        public void AbandonOperation(bool isRecoveryProbe)
        {
        }

        public ValueTask<bool> KeyExistsAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask<byte[]?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<byte[]?>(null);

        public ValueTask SetAsync(
            ReadOnlyMemory<byte> key,
            ReadOnlyMemory<byte> value,
            TimeSpan expiry,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(TimeSpan.FromMilliseconds(1));

        public ValueTask<long> ScriptEvaluateAsync(
            string script,
            ReadOnlyMemory<byte>[] keys,
            ReadOnlyMemory<byte>[] values,
            CancellationToken cancellationToken = default)
        {
            LastScript = script;
            if (EvaluateException is not null)
            {
                throw EvaluateException;
            }

            return ValueTask.FromResult(0L);
        }
    }
}
