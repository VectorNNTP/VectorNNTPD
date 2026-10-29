using VectorNNTPD.TransitService.Redis;
using VectorNNTPD.TransitService.Transit;
using VectorNNTPD.TransitService.Tests.TestDoubles;

namespace VectorNNTPD.TransitService.Tests.Transit;

/// <summary>
/// Static Lua/C# TransitPeerState parity. These tests do not execute live Redis.
/// FakeRedis dispatches EVAL to <see cref="TransitPeerStateEngine"/>.
/// </summary>
public sealed class TransitPeerStateLuaStaticAuditTests
{
    [Fact]
    public async Task ProductionStore_DispatchesExactTransitPeerStateScripts()
    {
        var redis = new RecordingRedis();
        var store = new RedisTransitPeerStateStore(redis);
        var now = DateTimeOffset.UnixEpoch;
        var ttl = TimeSpan.FromSeconds(30);

        _ = await store.TryAdmitAsync("peer-a", "nntpd01:a", 10, 1, now, ttl);
        Assert.Same(TransitPeerStateScripts.TryAdmit, redis.LastScript);
        Assert.Equal(1, redis.LastKeyCount);

        await store.ReleaseAsync("peer-a", "nntpd01:a", 1);
        Assert.Same(TransitPeerStateScripts.Release, redis.LastScript);

        _ = await store.RenewAsync("peer-a", "nntpd01:a", 1, now, ttl);
        Assert.Same(TransitPeerStateScripts.Renew, redis.LastScript);

        await store.ReleaseOwnerAsync("peer-a", "nntpd01:a");
        Assert.Same(TransitPeerStateScripts.ReleaseOwner, redis.LastScript);
    }

    [Fact]
    public void Scripts_NeverSetNativeKeyTtl()
    {
        foreach (var script in AllScripts())
        {
            Assert.DoesNotContain("EXPIRE", script, StringComparison.Ordinal);
            Assert.DoesNotContain("PEXPIRE", script, StringComparison.Ordinal);
            Assert.DoesNotContain("PERSIST", script, StringComparison.Ordinal);
            Assert.DoesNotContain("SET ", script, StringComparison.Ordinal);
            Assert.DoesNotContain("GET ", script, StringComparison.Ordinal);
            Assert.DoesNotContain("EVAL", script, StringComparison.Ordinal);
            Assert.DoesNotContain("redis.call('KEYS'", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Scripts_UseOnlyDocumentedRedisCommands()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "HGETALL", "HGET", "HSET", "HDEL", "HLEN", "DEL",
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
    public void TryAdmit_PrunesThenRejectsMaxZeroThenLimitBeforeIncrement()
    {
        var script = TransitPeerStateScripts.TryAdmit;
        var prune = script.IndexOf("prune(key)", StringComparison.Ordinal);
        var maxZero = script.IndexOf("if maxIncoming == nil or maxIncoming <= 0 then", StringComparison.Ordinal);
        var rejectLimit = script.IndexOf("if total >= maxIncoming then", StringComparison.Ordinal);
        var increment = script.IndexOf("increment(key, owner, gen)", StringComparison.Ordinal);
        Assert.True(prune > 0 && prune < maxZero);
        Assert.True(maxZero > 0 && maxZero < rejectLimit);
        Assert.True(rejectLimit > 0 && rejectLimit < increment);
        Assert.Contains("return 0", script, StringComparison.Ordinal);
        Assert.Contains("return 1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Renew_DoesNotRecreateMissingOwnership()
    {
        var script = TransitPeerStateScripts.Renew;
        Assert.Contains("if not cur then", script, StringComparison.Ordinal);
        Assert.Contains("return 0", script, StringComparison.Ordinal);
        var missing = script.IndexOf("if not cur then", StringComparison.Ordinal);
        var write = script.IndexOf("redis.call('HSET'", StringComparison.Ordinal);
        Assert.True(missing > 0 && missing < write);
        Assert.Contains("storedGen ~= gen or exp == nil or exp <= now", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_RequiresGenerationMatch()
    {
        var script = TransitPeerStateScripts.Release;
        Assert.Contains("if storedGen == gen then", script, StringComparison.Ordinal);
        Assert.Contains("redis.call('HDEL', key, owner)", script, StringComparison.Ordinal);
        Assert.Contains("if redis.call('HLEN', key) == 0 then", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseOwner_DeletesOnlyThisOwnerField()
    {
        var script = TransitPeerStateScripts.ReleaseOwner;
        Assert.Contains("redis.call('HDEL', key, owner)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("HGETALL", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineAndFakeRedisEval_AreSemanticallyIdentical()
    {
        var engine = new TransitPeerStateEngine();
        var redis = new FakeRedisService();
        var store = new RedisTransitPeerStateStore(redis);
        const string peer = "parity-peer";
        const string owner = "nntpd01:a";
        var now = DateTimeOffset.FromUnixTimeMilliseconds(10_000);
        var ttl = TimeSpan.FromMilliseconds(30_000);
        Assert.Equal(
            TransitPeerStateEngine.Accepted,
            engine.TryAdmit("k", owner, 2, 10_000, 30_000, 1));
        Assert.True((await store.TryAdmitAsync(peer, owner, 2, 1, now, ttl)).Accepted);
        AssertOwnership(engine, "k", owner, 40_000, 1, 1);
        AssertOwnership(redis.Database.TransitPeerStateEngine, EncodingKey(peer), owner, 40_000, 1, 1);

        Assert.Equal(TransitPeerStateEngine.Accepted, engine.TryAdmit("k", owner, 2, 10_000, 30_000, 1));
        Assert.True((await store.TryAdmitAsync(peer, owner, 2, 1, now, ttl)).Accepted);

        Assert.Equal(TransitPeerStateEngine.Rejected, engine.TryAdmit("k", owner, 2, 10_000, 30_000, 1));
        Assert.False((await store.TryAdmitAsync(peer, owner, 2, 1, now, ttl)).Accepted);

        Assert.Equal(TransitPeerStateEngine.Rejected, engine.TryAdmit("k", owner, 0, 10_000, 30_000, 1));
        Assert.False((await store.TryAdmitAsync(peer, owner, 0, 1, now, ttl)).Accepted);

        _ = engine.Release("k", owner, 1);
        await store.ReleaseAsync(peer, owner, 1);

        engine.WriteOwnership("k", owner, 40_000, 1, 1);
        redis.Database.TransitPeerStateEngine.WriteOwnership(EncodingKey(peer), owner, 40_000, 1, 1);
        Assert.Equal(1, engine.Renew("k", owner, 1, 15_000, 30_000));
        Assert.Equal(TransitPeerStateRenewStatus.Renewed, await store.RenewAsync(peer, owner, 1, DateTimeOffset.FromUnixTimeMilliseconds(15_000), ttl));
        Assert.Equal(0, engine.Renew("k", owner, 9, 15_000, 30_000));
        Assert.Equal(TransitPeerStateRenewStatus.Lost, await store.RenewAsync(peer, owner, 9, DateTimeOffset.FromUnixTimeMilliseconds(15_000), ttl));

        _ = engine.ReleaseOwner("k", owner);
        await store.ReleaseOwnerAsync(peer, owner);
        Assert.False(engine.TryGetOwnership("k", owner, out _, out _, out _));
        Assert.False(redis.Database.TransitPeerStateEngine.TryGetOwnership(EncodingKey(peer), owner, out _, out _, out _));
    }

    private static void AssertOwnership(
        TransitPeerStateEngine engine,
        string key,
        string owner,
        long expiry,
        long generation,
        int count)
    {
        Assert.True(engine.TryGetOwnership(key, owner, out var storedExpiry, out var storedGeneration, out var storedCount));
        Assert.Equal(expiry, storedExpiry);
        Assert.Equal(generation, storedGeneration);
        Assert.Equal(count, storedCount);
    }

    private static string EncodingKey(string identifier) =>
        System.Text.Encoding.UTF8.GetString(TransitPeerStateKeys.Create(identifier));

    private static IEnumerable<string> AllScripts() =>
    [
        TransitPeerStateScripts.TryAdmit,
        TransitPeerStateScripts.Release,
        TransitPeerStateScripts.Renew,
        TransitPeerStateScripts.ReleaseOwner,
    ];

    private static IEnumerable<string> EnumerateRedisCalls(string script)
    {
        const string prefix = "redis.call('";
        var index = 0;
        while (true)
        {
            var start = script.IndexOf(prefix, index, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            start += prefix.Length;
            var end = script.IndexOf('\'', start);
            yield return script[start..end];
            index = end + 1;
        }
    }

    private sealed class RecordingRedis : IRedisService
    {
        public string? LastScript { get; private set; }

        public int LastKeyCount { get; private set; }

        public IRedisDatabase Database => new RecordingDatabase(this);

        public bool IsUnavailable => false;

        public bool TryBeginOperation(out bool isRecoveryProbe)
        {
            isRecoveryProbe = false;
            return true;
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

        private sealed class RecordingDatabase : IRedisDatabase
        {
            private readonly RecordingRedis _owner;

            public RecordingDatabase(RecordingRedis owner)
            {
                _owner = owner;
            }

            public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(TimeSpan.FromMilliseconds(1));

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

            public ValueTask<long> ScriptEvaluateAsync(
                string script,
                ReadOnlyMemory<byte>[] keys,
                ReadOnlyMemory<byte>[] values,
                CancellationToken cancellationToken = default)
            {
                _owner.LastScript = script;
                _owner.LastKeyCount = keys.Length;
                return ValueTask.FromResult(1L);
            }
        }
    }
}
