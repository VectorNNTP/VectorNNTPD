using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.Redis;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.AccountBytes;

public sealed class AccountByteLuaStaticAuditTests
{
    [Fact]
    public async Task ProductionStore_DispatchesExactAccountByteScripts()
    {
        var redis = new RecordingRedis();
        var store = new RedisAccountByteStore(redis);
        _ = await store.ApplyAsync("alice", "batch-a", 10, 90);
        Assert.Same(AccountByteScripts.Apply, redis.LastScript);
        _ = await store.ObserveAsync("alice");
        Assert.Same(AccountByteScripts.Observe, redis.LastScript);
        _ = await store.DeleteAsync("alice");
        Assert.Same(AccountByteScripts.Delete, redis.LastScript);
    }

    [Fact]
    public void Scripts_NeverSetTtl_NeverIncrement_AndMarkBatchIds()
    {
        foreach (var script in AllScripts())
        {
            Assert.DoesNotContain("EXPIRE", script, StringComparison.Ordinal);
            Assert.DoesNotContain("PEXPIRE", script, StringComparison.Ordinal);
            Assert.DoesNotContain("PERSIST", script, StringComparison.Ordinal);
            Assert.DoesNotContain("INCR", script, StringComparison.Ordinal);
            Assert.DoesNotContain("INCRBY", script, StringComparison.Ordinal);
            Assert.DoesNotContain("EVAL", script, StringComparison.Ordinal);
        }

        Assert.Contains("HEXISTS", AccountByteScripts.Apply, StringComparison.Ordinal);
        Assert.Contains("b:' .. batchId", AccountByteScripts.Apply, StringComparison.Ordinal);
        Assert.Contains("if next > mysql then", AccountByteScripts.Apply, StringComparison.Ordinal);
        Assert.Contains("HLEN", AccountByteScripts.Apply, StringComparison.Ordinal);
        Assert.Contains("HDEL", AccountByteScripts.Apply, StringComparison.Ordinal);
        Assert.DoesNotContain("current - consumed", AccountByteScripts.Apply, StringComparison.Ordinal);
        Assert.Contains("HGET", AccountByteScripts.Observe, StringComparison.Ordinal);
        Assert.Contains("return -1", AccountByteScripts.Observe, StringComparison.Ordinal);
        Assert.DoesNotContain("HSET", AccountByteScripts.Observe, StringComparison.Ordinal);
        Assert.Contains("DEL", AccountByteScripts.Delete, StringComparison.Ordinal);
        Assert.DoesNotContain("HSET", AccountByteScripts.Delete, StringComparison.Ordinal);
        Assert.DoesNotContain("INCR", AccountByteScripts.Delete, StringComparison.Ordinal);
    }

    [Fact]
    public void Scripts_UseOnlyHashCommands()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "EXISTS", "HGET", "HSET", "HEXISTS", "HLEN", "HKEYS", "HDEL", "DEL",
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
    public async Task FakeRedis_DispatchesAccountByteScriptsToEngine()
    {
        var redis = new FakeRedisService();
        var store = new RedisAccountByteStore(redis);
        Assert.Equal(900, await store.ApplyAsync("alice", "A", 100, 900));
        Assert.Equal(800, await store.ApplyAsync("alice", "B", 100, 800));
        Assert.Equal(800, await store.ObserveAsync("alice"));
        redis.Database.AccountByteEngine.Write(
            System.Text.Encoding.UTF8.GetString(AccountByteKeys.Create("alice")),
            50);
        Assert.Equal(50, await store.ApplyAsync("alice", "C", 10, 200));
    }

    [Fact]
    public void Engine_AndLua_ShareMissingSentinel()
    {
        Assert.Equal(-1, AccountByteKeys.Missing);
        Assert.Contains("return -1", AccountByteScripts.Observe, StringComparison.Ordinal);
    }

    [Fact]
    public void Writer_CountsOnlyAfterAdvance_AndNeverTouchesStores()
    {
        var source = File.ReadAllText(
            Path.Join(FindRepositoryRoot(), "src", "VectorNNTPD.ReaderService", "Session", "CommandProcessor", "NntpResponseWriter.cs"));
        Assert.Contains("_output.Advance(toCopy);", source, StringComparison.Ordinal);
        Assert.Contains("ObserveCopied(toCopy)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ObserveCopied(request.Payload.Length)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IAccountByteStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("INntpDbConnection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deflate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ScriptEvaluate", source, StringComparison.Ordinal);
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

    private static IEnumerable<string> AllScripts()
    {
        yield return AccountByteScripts.Apply;
        yield return AccountByteScripts.Observe;
        yield return AccountByteScripts.Delete;
    }

    private static IEnumerable<string> EnumerateRedisCalls(string script)
    {
        const string prefix = "redis.call('";
        var start = 0;
        while (true)
        {
            var at = script.IndexOf(prefix, start, StringComparison.Ordinal);
            if (at < 0)
            {
                yield break;
            }

            var commandStart = at + prefix.Length;
            var commandEnd = script.IndexOf('\'', commandStart);
            Assert.True(commandEnd > commandStart);
            yield return script[commandStart..commandEnd];
            start = commandEnd + 1;
        }
    }

    private sealed class RecordingRedis : IRedisService
    {
        public string? LastScript { get; private set; }

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
                return ValueTask.FromResult(0L);
            }
        }
    }
}
