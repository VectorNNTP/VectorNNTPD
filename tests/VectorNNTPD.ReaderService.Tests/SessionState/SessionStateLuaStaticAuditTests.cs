using System.Net;
using VectorNNTPD.ReaderService.Redis;
using VectorNNTPD.ReaderService.SessionState;

namespace VectorNNTPD.ReaderService.Tests.SessionState;

/// <summary>
/// Static Lua/C# SessionState parity. These tests do not execute Redis.
/// FakeRedis dispatches EVAL to <see cref="SessionStateEngine"/>.
/// </summary>
public sealed class SessionStateLuaStaticAuditTests
{
    [Fact]
    public async Task ProductionStore_DispatchesExactSessionStateScripts()
    {
        var redis = new RecordingRedis();
        var store = new RedisSessionStateStore(redis);
        var now = DateTimeOffset.UnixEpoch;
        var ttl = TimeSpan.FromSeconds(30);

        _ = await store.TryAdmitAsync("alice", "192.0.2.10", "nntpd01:a", 10, 4, 1, 1, now, ttl);
        Assert.Same(SessionStateScripts.TryAdmit, redis.LastScript);

        await store.ReleaseAsync("alice", "192.0.2.10", "nntpd01:a", 1, 1);
        Assert.Same(SessionStateScripts.Release, redis.LastScript);

        _ = await store.RenewAsync("alice", "nntpd01:a", 1, [("192.0.2.10", 1)], now, ttl);
        Assert.Same(SessionStateScripts.Renew, redis.LastScript);

        _ = await store.RenewAndApplyAsync(
            "alice",
            "nntpd01:a",
            1,
            [("192.0.2.10", 1)],
            now,
            ttl,
            "batch-a",
            10,
            90);
        Assert.Same(SessionStateScripts.RenewAndApply, redis.LastScript);

        await store.ReleaseOwnerAsync("alice", "nntpd01:a");
        Assert.Same(SessionStateScripts.ReleaseOwner, redis.LastScript);
    }

    [Fact]
    public void Scripts_MutateOnlyInsideSingleEval_AndNeverSetKeyTtl()
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
            "EXISTS", "HEXISTS", "HKEYS",
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
    public void TryAdmit_ChecksLimitsBeforeAnyIncrement_AndSessionLimitFirst()
    {
        var script = SessionStateScripts.TryAdmit;
        var sessionReject = script.IndexOf("return 1", StringComparison.Ordinal);
        var sourceReject = script.IndexOf("return 0", StringComparison.Ordinal);
        var incrementSess = script.IndexOf("increment(sessKey, owner, sessionGen)", StringComparison.Ordinal);
        var incrementSrc = script.IndexOf("increment(srcKey, srcField, sourceGen)", StringComparison.Ordinal);
        var prune = script.IndexOf("prune(srcKey)", StringComparison.Ordinal);
        Assert.True(prune > 0 && prune < sessionReject);
        Assert.True(sessionReject > 0 && sessionReject < sourceReject);
        Assert.True(sourceReject > 0 && sourceReject < incrementSess);
        Assert.True(incrementSess > 0 && incrementSess < incrementSrc);
        Assert.Contains("if sessionLimit ~= nil and sessionLimit > 0 and sessionTotal >= sessionLimit then", script, StringComparison.Ordinal);
        Assert.Contains("if srcLimit ~= nil and srcLimit > 0 and not active[ip] then", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAdmit_IncrementReplacesOnGenerationMismatch()
    {
        var script = SessionStateScripts.TryAdmit;
        Assert.Contains("if storedGen and storedGen == gen then", script, StringComparison.Ordinal);
        Assert.Contains("tostring((count or 0) + 1)", script, StringComparison.Ordinal);
        Assert.Contains("tostring(expiry) .. '|' .. gen .. '|1'", script, StringComparison.Ordinal);

        var engine = new SessionStateEngine();
        engine.WriteOwnership("sess", "O1", 30_000, 1, 7);
        engine.WriteOwnership("src", SessionStateKeys.SourceField("192.0.2.10", "O1"), 30_000, 1, 7);
        Assert.Equal(
            SessionStateEngine.AcceptedExisting,
            engine.TryAdmitStatus("src", "sess", "192.0.2.10", "O1", 20, 4, 0, 30_000, 2, 2));
        Assert.True(engine.TryGetOwnership("sess", "O1", out _, out var generation, out var count));
        Assert.Equal(2, generation);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Release_IsGenerationAndOwnerScopedDecrement_NotReleaseOwner()
    {
        var script = SessionStateScripts.Release;
        Assert.Contains("if storedGen ~= gen then", script, StringComparison.Ordinal);
        Assert.Contains("decrement(sessKey, owner, sessionGen)", script, StringComparison.Ordinal);
        Assert.Contains("decrement(srcKey, srcField, sourceGen)", script, StringComparison.Ordinal);
        Assert.Contains("ip .. '\\31' .. owner", script, StringComparison.Ordinal);
        Assert.DoesNotContain("HDEL', sessKey, owner", script, StringComparison.Ordinal);
        Assert.Contains("return sessionTotal", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Renew_IsAllOrNothing_AndDoesNotRecreate()
    {
        var script = SessionStateScripts.Renew;
        var firstLost = script.IndexOf("return 0", StringComparison.Ordinal);
        var firstWrite = script.IndexOf("renew_field(sessKey", StringComparison.Ordinal);
        Assert.True(firstLost > 0 && firstWrite > firstLost);
        Assert.Contains("if storedGen ~= gen or exp == nil or exp <= now then", script, StringComparison.Ordinal);
        Assert.Contains("if sessionGen ~= '0' then", script, StringComparison.Ordinal);
        Assert.DoesNotContain("increment(", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseOwner_DeletesOnlyMatchingOwnerFields()
    {
        var script = SessionStateScripts.ReleaseOwner;
        Assert.Contains("redis.call('HDEL', sessKey, owner)", script, StringComparison.Ordinal);
        Assert.Contains("if fieldOwner == owner then", script, StringComparison.Ordinal);
        Assert.Contains("redis.call('HDEL', srcKey, unpack(owned))", script, StringComparison.Ordinal);
        Assert.DoesNotContain("generation", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceField_IsNormalizedIpUnitSeparatorOwner_WithNoLuaNormalization()
    {
        Assert.Equal('\u001f', SessionStateKeys.FieldSeparator);
        Assert.Equal("192.0.2.10\u001fnntpd01:a", SessionStateKeys.SourceField("192.0.2.10", "nntpd01:a"));
        foreach (var script in AllScripts())
        {
            Assert.DoesNotContain("ffff", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MapToIPv4", script, StringComparison.Ordinal);
            Assert.DoesNotContain("IPv4", script, StringComparison.Ordinal);
        }

        var mapped = IPAddress.Parse("::ffff:192.0.2.10");
        Assert.Equal("192.0.2.10", SourceAddressIdentity.Format(mapped));
        Assert.Equal("192.0.2.10", SourceAddressIdentity.Format(IPAddress.Parse("192.0.2.10")));
        Assert.Equal("2001:db8::10", SourceAddressIdentity.Format(IPAddress.Parse("2001:db8::10")));
        Assert.Equal(
            IPAddress.Parse("198.18.0.70").GetAddressBytes(),
            SourceAddressIdentity.ToNetworkBytes(IPAddress.Parse("198.18.0.70")));
        Assert.Equal(4, SourceAddressIdentity.ToNetworkBytes(IPAddress.Parse("198.18.0.70")).Length);
        Assert.Equal(
            IPAddress.Parse("2001:db8::1234").GetAddressBytes(),
            SourceAddressIdentity.ToNetworkBytes(IPAddress.Parse("2001:db8::1234")));
        Assert.Equal(16, SourceAddressIdentity.ToNetworkBytes(IPAddress.Parse("2001:db8::1234")).Length);
        Assert.Equal(
            IPAddress.Parse("198.18.0.70").GetAddressBytes(),
            SourceAddressIdentity.ToNetworkBytes(IPAddress.Parse("::ffff:198.18.0.70")));
        Assert.Equal(4, SourceAddressIdentity.ToNetworkBytes(IPAddress.Parse("::ffff:198.18.0.70")).Length);
    }

    [Fact]
    public void Engine_AndLuaRules_ShareReturnCodesAndValueFormat()
    {
        Assert.Equal(3, SessionStateEngine.AcceptedExisting);
        Assert.Equal(2, SessionStateEngine.AcceptedNew);
        Assert.Equal(1, SessionStateEngine.RejectedSessionLimit);
        Assert.Equal(0, SessionStateEngine.RejectedSourceLimit);
        Assert.Contains("return 3 + (sessionTotal * 4)", SessionStateScripts.TryAdmit, StringComparison.Ordinal);
        Assert.Contains("return 2 + (sessionTotal * 4)", SessionStateScripts.TryAdmit, StringComparison.Ordinal);
        Assert.Equal("1000|7|4", SessionStateKeys.Value(1000, 7, 4));
        Assert.True(SessionStateKeys.TrySplitValue("1000|7|4", out var expiry, out var generation, out var count));
        Assert.Equal(1000, expiry);
        Assert.Equal(7, generation);
        Assert.Equal(4, count);
    }

    [Fact]
    public void Engine_RejectedAdmit_DoesNotCreateOwnership_AfterExpiredPrune()
    {
        var engine = new SessionStateEngine();
        engine.WriteOwnership("sess", "O1", expiryUnixMs: 1_000, generation: 1, count: 7);
        engine.WriteOwnership("src", SessionStateKeys.SourceField("192.0.2.10", "O1"), 1_000, 1, 7);
        engine.WriteOwnership("sess", "O2", expiryUnixMs: 80_000, generation: 9, count: 1);
        Assert.Equal(
            SessionStateEngine.RejectedSessionLimit,
            engine.TryAdmitStatus("src", "sess", "198.51.100.20", "O3", sessionLimit: 1, srcIpLimit: 4, nowUnixMs: 2_000, 30_000, 1, 1));
        Assert.False(engine.TryGetOwnership("sess", "O1", out _, out _, out _));
        Assert.False(engine.HasSourceOwner("src", "192.0.2.10", "O1", 2_000));
        Assert.Equal(1, engine.OwnerSessionCount("sess", "O2", 2_000));
        Assert.False(engine.TryGetOwnership("sess", "O3", out _, out _, out _));
        Assert.False(engine.HasSourceOwner("src", "198.51.100.20", "O3", 2_000));
    }

    [Fact]
    public void Engine_RejectedSourceLimit_LeavesExistingSessionAndSourceUnchanged()
    {
        var engine = new SessionStateEngine();
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus("src", "sess", "192.0.2.10", "O1", 10, 1, 0, 30_000, 1, 1));
        Assert.Equal(
            SessionStateEngine.RejectedSourceLimit,
            engine.TryAdmitStatus("src", "sess", "198.51.100.20", "O2", 10, 1, 0, 30_000, 2, 2));
        Assert.Equal(1, engine.OwnerSessionCount("sess", "O1", 0));
        Assert.Equal(0, engine.OwnerSessionCount("sess", "O2", 0));
        Assert.True(engine.HasSourceOwner("src", "192.0.2.10", "O1", 0));
        Assert.False(engine.HasSourceOwner("src", "198.51.100.20", "O2", 0));
        Assert.Equal(["192.0.2.10"], engine.ActiveSourceIps("src", 0));
    }

    [Fact]
    public void Engine_RenewLost_DoesNotRecreateMissingOrExpiredOrWrongGeneration()
    {
        var engine = new SessionStateEngine();
        Assert.Equal(0, engine.Renew("src", "sess", "O1", 1, 0, 30_000, [("192.0.2.10", 1)]));
        Assert.False(engine.TryGetOwnership("sess", "O1", out _, out _, out _));

        engine.WriteOwnership("sess", "O1", 30_000, 2, 1);
        engine.WriteOwnership("src", SessionStateKeys.SourceField("192.0.2.10", "O1"), 30_000, 2, 1);
        Assert.Equal(0, engine.Renew("src", "sess", "O1", 1, 0, 30_000, [("192.0.2.10", 1)]));
        Assert.True(engine.TryGetOwnership("sess", "O1", out _, out var generation, out var count));
        Assert.Equal(2, generation);
        Assert.Equal(1, count);

        engine.WriteOwnership("sess", "O2", expiryUnixMs: 1_000, generation: 1, count: 1);
        engine.WriteOwnership("src", SessionStateKeys.SourceField("192.0.2.10", "O2"), 1_000, 1, 1);
        Assert.Equal(0, engine.Renew("src", "sess", "O2", 1, nowUnixMs: 2_000, 30_000, [("192.0.2.10", 1)]));
        Assert.True(engine.TryGetOwnership("sess", "O2", out var expiry, out generation, out count));
        Assert.Equal(1_000, expiry);
        Assert.Equal(1, generation);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task StoreFailureMapping_IsNotBroadened()
    {
        var now = DateTimeOffset.UnixEpoch;
        var ttl = TimeSpan.FromSeconds(30);

        var unavailable = new RecordingRedis { BeginSucceeds = false };
        var store = new RedisSessionStateStore(unavailable);
        Assert.Equal(
            SessionStateAdmitStatus.Unavailable,
            (await store.TryAdmitAsync("alice", "192.0.2.10", "O1", 1, 1, 1, 1, now, ttl)).Status);
        Assert.Equal(
            SessionStateRenewStatus.Unavailable,
            (await store.RenewAsync("alice", "O1", 1, [("192.0.2.10", 1)], now, ttl)).Status);

        var redisDown = new RecordingRedis { EvaluateException = new RedisUnavailableException("timeout") };
        store = new RedisSessionStateStore(redisDown);
        Assert.Equal(
            SessionStateAdmitStatus.Unavailable,
            (await store.TryAdmitAsync("alice", "192.0.2.10", "O1", 1, 1, 1, 1, now, ttl)).Status);

        var disposed = new RecordingRedis { EvaluateException = new ObjectDisposedException("mux") };
        store = new RedisSessionStateStore(disposed);
        Assert.Equal(
            SessionStateAdmitStatus.Unavailable,
            (await store.TryAdmitAsync("alice", "192.0.2.10", "O1", 1, 1, 1, 1, now, ttl)).Status);

        var canceled = new RecordingRedis { EvaluateException = new OperationCanceledException() };
        store = new RedisSessionStateStore(canceled);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.TryAdmitAsync("alice", "192.0.2.10", "O1", 1, 1, 1, 1, now, ttl).AsTask());
    }

    [Fact]
    public void FakeRedis_DispatchesScriptsToSessionStateEngine()
    {
        var source = File.ReadAllText(
            Path.Join(FindRepositoryRoot(), "tests", "VectorNNTPD.ReaderService.Tests", "TestDoubles", "FakeRedis.cs"));
        Assert.Contains("if (script == SessionStateScripts.TryAdmit)", source, StringComparison.Ordinal);
        Assert.Contains("return SessionStateEngine.TryAdmit(", source, StringComparison.Ordinal);
        Assert.Contains("return SessionStateEngine.Release(", source, StringComparison.Ordinal);
        Assert.Contains("return SessionStateEngine.Renew(", source, StringComparison.Ordinal);
        Assert.Contains("SessionStateScripts.RenewAndApply", source, StringComparison.Ordinal);
        Assert.Contains("AccountByteEngine.Apply(", source, StringComparison.Ordinal);
        Assert.Contains("return SessionStateEngine.ReleaseOwner(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionMultiplexer", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LeaseConstants_RemainCrashRecoveryNotIdleTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), SessionStateDefaults.LeaseTtl);
        Assert.Equal(TimeSpan.FromSeconds(10), SessionStateDefaults.RenewalPeriod);
        Assert.Equal(TimeSpan.FromSeconds(2), SessionStateDefaults.HotPathSkew);
    }

    private static IEnumerable<string> AllScripts()
    {
        yield return SessionStateScripts.TryAdmit;
        yield return SessionStateScripts.Release;
        yield return SessionStateScripts.Renew;
        yield return SessionStateScripts.RenewAndApply;
        yield return SessionStateScripts.ReleaseOwner;
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

    private sealed class RecordingRedis : IRedisService
    {
        public string? LastScript { get; private set; }

        public bool BeginSucceeds { get; set; } = true;

        public Exception? EvaluateException { get; set; }

        public IRedisDatabase Database => new RecordingDatabase(this);

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
                if (_owner.EvaluateException is { } fault)
                {
                    return ValueTask.FromException<long>(fault);
                }

                return ValueTask.FromResult(2L);
            }
        }
    }
}
