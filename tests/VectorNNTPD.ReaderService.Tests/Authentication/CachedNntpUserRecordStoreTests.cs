using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.Redis;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Authentication;

public sealed class CachedNntpUserRecordStoreTests
{
    private const string SecretPassword = "super-secret-password-xyz";
    private const string CustomerId = "22222222-2222-2222-2222-222222222222";
    private static readonly byte[] ScramSalt = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] ScramStoredKey = [9, 10, 11, 12, 13, 14, 15, 16];
    private static readonly byte[] ScramServerKey = [17, 18, 19, 20, 21, 22, 23, 24];

    [Fact]
    public async Task CacheMiss_SelectsMySql_AndPopulatesRedisWithTenSecondTtl()
    {
        var inner = new RecordingUserRecordStore();
        var record = CreateRecord("alice");
        inner.Add(record);
        var redis = new FakeRedisService();
        var logger = new CollectingLogger<CachedNntpUserRecordStore>();
        var cache = new CachedNntpUserRecordStore(inner, redis, logger);

        var loaded = await cache.TryGetUserAsync("alice");

        AssertEqualRecord(record, loaded);
        Assert.Equal(1, inner.LookupsFor("alice"));
        Assert.Equal(1, redis.Database.SetCount);
        Assert.Equal(TimeSpan.FromSeconds(10), redis.Database.LastSetExpiry);
        Assert.True(redis.Database.TryGetStored(AccountCacheKeys.Create("alice"), out var payload, out var expiry));
        Assert.True(NntpAccountCacheCodec.TryDecode(payload, "alice", out var decoded));
        AssertEqualRecord(record, decoded);
        Assert.True(expiry > DateTimeOffset.UtcNow.AddSeconds(8));
        Assert.True(expiry <= DateTimeOffset.UtcNow.AddSeconds(11));
        AssertNoSecrets(logger);
    }

    [Fact]
    public async Task CacheHit_DoesNotCallMySql()
    {
        var inner = new RecordingUserRecordStore();
        inner.Add(CreateRecord("alice"));
        var redis = new FakeRedisService();
        var logger = new CollectingLogger<CachedNntpUserRecordStore>();
        redis.Database.Seed(AccountCacheKeys.Create("alice"), NntpAccountCacheCodec.Encode(CreateRecord("alice")));
        var cache = new CachedNntpUserRecordStore(inner, redis, logger);

        var loaded = await cache.TryGetUserAsync("alice");

        AssertEqualRecord(CreateRecord("alice"), loaded);
        Assert.Equal(0, inner.LookupCount);
        Assert.Equal(0, redis.Database.SetCount);
        Assert.Equal(1, redis.Database.GetCount);
        AssertNoSecrets(logger);
    }

    [Fact]
    public async Task ConcurrentColdCache_SingleFlight_OneMySqlSelect()
    {
        var inner = new RecordingUserRecordStore();
        var record = CreateRecord("alice");
        inner.Add(record);
        inner.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var redis = new FakeRedisService();
        var logger = new CollectingLogger<CachedNntpUserRecordStore>();
        var cache = new CachedNntpUserRecordStore(inner, redis, logger);

        var lookups = new Task<NntpUserRecord?>[40];
        for (var i = 0; i < lookups.Length; i++)
        {
            lookups[i] = cache.TryGetUserAsync("alice").AsTask();
        }

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await inner.WaitStartedAsync("alice", safety.Token);
        Assert.Equal(1, inner.LookupsFor("alice"));
        Assert.Equal(1, cache.InFlightCount);
        inner.Block.SetResult();
        var results = await Task.WhenAll(lookups);

        Assert.Equal(1, inner.LookupsFor("alice"));
        Assert.Equal(1, redis.Database.SetCount);
        Assert.Equal(0, cache.InFlightCount);
        foreach (var loaded in results)
        {
            AssertEqualRecord(record, loaded);
        }

        AssertNoSecrets(logger);
    }

    [Fact]
    public async Task ConcurrentDifferentAccounts_DoNotShareOneGlobalLock()
    {
        var inner = new RecordingUserRecordStore();
        inner.Add(CreateRecord("alice"));
        inner.Add(CreateRecord("bob", rateLimitBps: 240, byteLimit: 99));
        inner.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var redis = new FakeRedisService();
        var cache = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);

        var alice = new Task<NntpUserRecord?>[20];
        var bob = new Task<NntpUserRecord?>[20];
        for (var i = 0; i < 20; i++)
        {
            alice[i] = cache.TryGetUserAsync("alice").AsTask();
            bob[i] = cache.TryGetUserAsync("bob").AsTask();
        }

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await inner.WaitStartedAsync("alice", safety.Token);
        await inner.WaitStartedAsync("bob", safety.Token);
        Assert.Equal(1, inner.LookupsFor("alice"));
        Assert.Equal(1, inner.LookupsFor("bob"));
        inner.Block.SetResult();
        var aliceResults = await Task.WhenAll(alice);
        var bobResults = await Task.WhenAll(bob);

        Assert.Equal(1, inner.LookupsFor("alice"));
        Assert.Equal(1, inner.LookupsFor("bob"));
        Assert.Equal(0, cache.InFlightCount);
        foreach (var loaded in aliceResults)
        {
            AssertEqualRecord(CreateRecord("alice"), loaded);
        }

        foreach (var loaded in bobResults)
        {
            AssertEqualRecord(CreateRecord("bob", rateLimitBps: 240, byteLimit: 99), loaded);
        }
    }

    [Fact]
    public async Task RedisGetFailure_FallsBackToMySql_AndDoesNotTransientFailAuth()
    {
        var inner = new RecordingUserRecordStore();
        inner.Add(CreateRecord("alice"));
        var redis = new FakeRedisService();
        redis.Database.GetException = new RedisUnavailableException("get down");
        var cache = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);
        var validator = new MySqlNntpCredentialValidator(cache, NullLogger<MySqlNntpCredentialValidator>.Instance);

        var result = await validator.ValidatePasswordAsync(
            "AUTHINFO PASS",
            "alice",
            SecretPassword,
            IPAddress.Loopback);

        Assert.True(result.Succeeded);
        Assert.NotEqual(NntpAuthenticationFailureKind.TransientFailure, result.Failure);
        Assert.Equal(1, inner.LookupsFor("alice"));
        Assert.False(redis.IsUnavailable);
    }

    [Fact]
    public async Task RedisSetFailure_ReturnsMySqlRecord_AndDoesNotTransientFailAuth()
    {
        var inner = new RecordingUserRecordStore();
        inner.Add(CreateRecord("alice"));
        var redis = new FakeRedisService();
        redis.Database.SetException = new RedisUnavailableException("set down");
        var cache = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);
        var validator = new MySqlNntpCredentialValidator(cache, NullLogger<MySqlNntpCredentialValidator>.Instance);

        var result = await validator.ValidatePasswordAsync(
            "AUTHINFO PASS",
            "alice",
            SecretPassword,
            IPAddress.Loopback);

        Assert.True(result.Succeeded);
        Assert.NotEqual(NntpAuthenticationFailureKind.TransientFailure, result.Failure);
        Assert.Equal(1, inner.LookupsFor("alice"));
        Assert.False(redis.IsUnavailable);
    }

    [Fact]
    public async Task RedisUnavailable_FallsBackToMySql()
    {
        var inner = new RecordingUserRecordStore();
        inner.Add(CreateRecord("alice"));
        var redis = new FakeRedisService { IsUnavailable = true };
        var cache = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);

        var loaded = await cache.TryGetUserAsync("alice");

        AssertEqualRecord(CreateRecord("alice"), loaded);
        Assert.Equal(1, inner.LookupsFor("alice"));
        Assert.Equal(0, redis.Database.GetCount);
        Assert.Equal(0, redis.Database.SetCount);
    }

    [Fact]
    public async Task MySqlFailureAfterMiss_PreservesExistingFailure()
    {
        var inner = new RecordingUserRecordStore
        {
            Exception = new NntpDbUnavailableException("nntpusers down"),
        };
        var redis = new FakeRedisService();
        var cache = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);
        var validator = new MySqlNntpCredentialValidator(cache, NullLogger<MySqlNntpCredentialValidator>.Instance);

        await Assert.ThrowsAsync<NntpDbUnavailableException>(() => cache.TryGetUserAsync("alice").AsTask());
        var result = await validator.ValidatePasswordAsync(
            "AUTHINFO PASS",
            "alice",
            SecretPassword,
            IPAddress.Loopback);

        Assert.False(result.Succeeded);
        Assert.Equal(NntpAuthenticationFailureKind.TransientFailure, result.Failure);
        Assert.Equal(0, redis.Database.SetCount);
        Assert.Equal(0, cache.InFlightCount);
    }

    [Fact]
    public void Codec_PreservesAllPolicyAndCredentialFields()
    {
        var record = new NntpUserRecord(
            "alice",
            SecretPassword,
            allowAuthPlain: false,
            allowAuthScram256: true,
            ScramSalt,
            scramIterations: 4096,
            ScramStoredKey,
            ScramServerKey,
            rateLimitBps: 10_000_000,
            byteLimit: long.MaxValue,
            sessionLimit: 40,
            srcIpLimit: 2,
            isEnabled: true,
            CustomerId);

        var payload = NntpAccountCacheCodec.Encode(record);
        Assert.True(NntpAccountCacheCodec.TryDecode(payload, "alice", out var decoded));
        AssertEqualRecord(record, decoded);
        Assert.False(NntpAccountCacheCodec.TryDecode(payload, "bob", out _));
        Assert.False(NntpAccountCacheCodec.TryDecode([0x02], "alice", out _));

        foreach (var (rateBps, byteLimit) in new (int Rate, long Bytes)[]
                 {
                     (0, 0),
                     (1, 1),
                     (7, 0),
                     (8, 8),
                     (240, 10_000_000_000),
                     (int.MaxValue, long.MaxValue),
                 })
        {
            var limited = CreateRecord("alice", rateLimitBps: rateBps, byteLimit: byteLimit);
            var encoded = NntpAccountCacheCodec.Encode(limited);
            Assert.True(NntpAccountCacheCodec.TryDecode(encoded, "alice", out var roundTrip));
            AssertEqualRecord(limited, roundTrip);
        }
    }

    [Fact]
    public void RedisKey_IsSha256Hex_AndDoesNotContainAccountName()
    {
        var key = Encoding.ASCII.GetString(AccountCacheKeys.Create("alice"));
        var hash = AccountCacheKeys.HashHex("alice");
        Assert.Equal("nntpd:account:" + hash, key);
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain("alice", key, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public async Task RedisGet_DoesNotExtendTtl()
    {
        var inner = new RecordingUserRecordStore();
        inner.Add(CreateRecord("alice"));
        var redis = new FakeRedisService();
        var cache = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);

        AssertEqualRecord(CreateRecord("alice"), await cache.TryGetUserAsync("alice"));
        Assert.True(redis.Database.TryGetStored(AccountCacheKeys.Create("alice"), out _, out var expiryAfterSet));
        Assert.Equal(1, redis.Database.SetCount);
        var lastExpiry = redis.Database.LastSetExpiry;

        AssertEqualRecord(CreateRecord("alice"), await cache.TryGetUserAsync("alice"));
        Assert.Equal(1, inner.LookupsFor("alice"));
        Assert.Equal(1, redis.Database.SetCount);
        Assert.Equal(lastExpiry, redis.Database.LastSetExpiry);
        Assert.True(redis.Database.TryGetStored(AccountCacheKeys.Create("alice"), out _, out var expiryAfterGet));
        Assert.Equal(expiryAfterSet, expiryAfterGet);
    }

    [Fact]
    public async Task RepeatedAuth_DoesNotBypassRedisExpiry()
    {
        var inner = new RecordingUserRecordStore();
        inner.Add(CreateRecord("alice"));
        var redis = new FakeRedisService();
        var store = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);
        var validator = new MySqlNntpCredentialValidator(store, NullLogger<MySqlNntpCredentialValidator>.Instance);

        for (var i = 0; i < 5; i++)
        {
            var result = await validator.ValidatePasswordAsync(
                "AUTHINFO PASS",
                "alice",
                SecretPassword,
                IPAddress.Loopback);
            Assert.True(result.Succeeded);
        }

        Assert.Equal(1, inner.LookupsFor("alice"));
        Assert.Equal(1, redis.Database.SetCount);
        redis.Database.Expire(AccountCacheKeys.Create("alice"));

        var afterExpiry = await validator.ValidatePasswordAsync(
            "AUTHINFO PASS",
            "alice",
            SecretPassword,
            IPAddress.Loopback);
        Assert.True(afterExpiry.Succeeded);
        Assert.Equal(2, inner.LookupsFor("alice"));
        Assert.Equal(2, redis.Database.SetCount);
    }

    [Fact]
    public async Task RepeatedCacheHits_StillVerifyPasswordEachTime()
    {
        var inner = new RecordingUserRecordStore();
        inner.Add(CreateRecord("alice"));
        var redis = new FakeRedisService();
        var store = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);
        var validator = new MySqlNntpCredentialValidator(store, NullLogger<MySqlNntpCredentialValidator>.Instance);

        Assert.True((await validator.ValidatePasswordAsync(
            "AUTHINFO PASS",
            "alice",
            SecretPassword,
            IPAddress.Loopback)).Succeeded);
        Assert.True((await validator.ValidatePasswordAsync(
            "AUTHINFO PASS",
            "alice",
            SecretPassword,
            IPAddress.Loopback)).Succeeded);
        var rejected = await validator.ValidatePasswordAsync(
            "AUTHINFO PASS",
            "alice",
            "wrong-password",
            IPAddress.Loopback);

        Assert.False(rejected.Succeeded);
        Assert.Equal(NntpAuthenticationFailureKind.InvalidCredentials, rejected.Failure);
        Assert.Equal(1, inner.LookupsFor("alice"));
    }

    [Fact]
    public async Task CachedZeroByteLimit_StillAuthenticates_AndDoesNotMutateCache()
    {
        var inner = new RecordingUserRecordStore();
        var record = CreateRecord("alice", rateLimitBps: 0, byteLimit: 0);
        inner.Add(record);
        var redis = new FakeRedisService();
        var cache = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);
        var validator = new MySqlNntpCredentialValidator(cache, NullLogger<MySqlNntpCredentialValidator>.Instance);

        var result = await validator.ValidatePasswordAsync(
            "AUTHINFO PASS",
            "alice",
            SecretPassword,
            IPAddress.Loopback);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Policy!.ByteLimit);
        Assert.Equal(0, result.Policy.RateLimitBps);
        Assert.True(redis.Database.TryGetStored(AccountCacheKeys.Create("alice"), out var payload, out _));
        Assert.True(NntpAccountCacheCodec.TryDecode(payload, "alice", out var cached));
        Assert.Equal(0, cached!.ByteLimit);
        Assert.True(payload.AsSpan().SequenceEqual(NntpAccountCacheCodec.Encode(record)));
    }

    [Fact]
    public async Task SingleFlight_CancelledWaiter_DoesNotAbortSharedLookup()
    {
        var inner = new RecordingUserRecordStore();
        var record = CreateRecord("alice");
        inner.Add(record);
        inner.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var redis = new FakeRedisService();
        var cache = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);

        var winner = cache.TryGetUserAsync("alice").AsTask();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await inner.WaitStartedAsync("alice", safety.Token);

        using var waiterCts = new CancellationTokenSource();
        var cancelled = cache.TryGetUserAsync("alice", waiterCts.Token).AsTask();
        var kept = cache.TryGetUserAsync("alice").AsTask();
        waiterCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, inner.LookupsFor("alice"));
        Assert.Equal(1, cache.InFlightCount);

        inner.Block.SetResult();
        AssertEqualRecord(record, await winner);
        AssertEqualRecord(record, await kept);
        Assert.Equal(1, inner.LookupsFor("alice"));
        Assert.Equal(0, cache.InFlightCount);
    }

    [Fact]
    public async Task FailedSharedLookup_RemovesGate_ForAllWaiters()
    {
        var inner = new RecordingUserRecordStore
        {
            Exception = new NntpDbUnavailableException("nntpusers down"),
            Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var redis = new FakeRedisService();
        var cache = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);

        var first = cache.TryGetUserAsync("alice").AsTask();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await inner.WaitStartedAsync("alice", safety.Token);
        var second = cache.TryGetUserAsync("alice").AsTask();
        Assert.Equal(1, cache.InFlightCount);
        inner.Block.SetResult();

        await Assert.ThrowsAsync<NntpDbUnavailableException>(() => first);
        await Assert.ThrowsAsync<NntpDbUnavailableException>(() => second);
        Assert.Equal(0, cache.InFlightCount);
        Assert.Equal(1, inner.LookupsFor("alice"));
        Assert.Equal(0, redis.Database.SetCount);
    }

    [Fact]
    public async Task CacheHit_StillRequiresPasswordVerification()
    {
        var inner = new RecordingUserRecordStore();
        inner.Add(CreateRecord("alice"));
        var redis = new FakeRedisService();
        redis.Database.Seed(AccountCacheKeys.Create("alice"), NntpAccountCacheCodec.Encode(CreateRecord("alice")));
        var cache = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);
        var validator = new MySqlNntpCredentialValidator(cache, NullLogger<MySqlNntpCredentialValidator>.Instance);

        var result = await validator.ValidatePasswordAsync(
            "AUTHINFO PASS",
            "alice",
            "wrong-password",
            IPAddress.Loopback);

        Assert.False(result.Succeeded);
        Assert.Equal(NntpAuthenticationFailureKind.InvalidCredentials, result.Failure);
        Assert.Equal(0, inner.LookupCount);
    }

    [Fact]
    public async Task MissingAccount_IsNotCached()
    {
        var inner = new RecordingUserRecordStore();
        var redis = new FakeRedisService();
        var cache = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);

        Assert.Null(await cache.TryGetUserAsync("missing"));
        Assert.Null(await cache.TryGetUserAsync("missing"));
        Assert.Equal(2, inner.LookupsFor("missing"));
        Assert.Equal(0, redis.Database.SetCount);
        Assert.False(redis.Database.Contains(AccountCacheKeys.Create("missing")));
    }

    [Fact]
    public async Task ExpiredCache_ReturnsToMySql()
    {
        var inner = new RecordingUserRecordStore();
        inner.Add(CreateRecord("alice"));
        var redis = new FakeRedisService();
        var cache = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);

        AssertEqualRecord(CreateRecord("alice"), await cache.TryGetUserAsync("alice"));
        Assert.Equal(1, inner.LookupsFor("alice"));
        redis.Database.Expire(AccountCacheKeys.Create("alice"));

        AssertEqualRecord(CreateRecord("alice"), await cache.TryGetUserAsync("alice"));
        Assert.Equal(2, inner.LookupsFor("alice"));
        Assert.Equal(2, redis.Database.SetCount);
    }

    [Fact]
    public async Task CorruptPayload_IsTreatedAsMiss()
    {
        var inner = new RecordingUserRecordStore();
        inner.Add(CreateRecord("alice"));
        var redis = new FakeRedisService();
        redis.Database.Seed(AccountCacheKeys.Create("alice"), "not-a-cache-record"u8.ToArray());
        var cache = new CachedNntpUserRecordStore(
            inner,
            redis,
            NullLogger<CachedNntpUserRecordStore>.Instance);

        AssertEqualRecord(CreateRecord("alice"), await cache.TryGetUserAsync("alice"));
        Assert.Equal(1, inner.LookupsFor("alice"));
    }

    private static NntpUserRecord CreateRecord(
        string name,
        int rateLimitBps = 240,
        long byteLimit = 10_000_000_000) =>
        new(
            name,
            SecretPassword,
            allowAuthPlain: true,
            allowAuthScram256: true,
            ScramSalt,
            scramIterations: 4096,
            ScramStoredKey,
            ScramServerKey,
            rateLimitBps,
            byteLimit,
            sessionLimit: 40,
            srcIpLimit: 2,
            isEnabled: true,
            CustomerId);

    private static void AssertEqualRecord(NntpUserRecord expected, NntpUserRecord? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.AccountName, actual.AccountName);
        Assert.Equal(expected.AccountPassword, actual.AccountPassword);
        Assert.Equal(expected.AllowAuthPlain, actual.AllowAuthPlain);
        Assert.Equal(expected.AllowAuthScram256, actual.AllowAuthScram256);
        Assert.True(expected.ScramSalt.Span.SequenceEqual(actual.ScramSalt.Span));
        Assert.Equal(expected.ScramIterations, actual.ScramIterations);
        Assert.True(expected.ScramStoredKey.Span.SequenceEqual(actual.ScramStoredKey.Span));
        Assert.True(expected.ScramServerKey.Span.SequenceEqual(actual.ScramServerKey.Span));
        Assert.Equal(expected.RateLimitBps, actual.RateLimitBps);
        Assert.Equal(expected.ByteLimit, actual.ByteLimit);
        Assert.Equal(expected.SessionLimit, actual.SessionLimit);
        Assert.Equal(expected.SrcIpLimit, actual.SrcIpLimit);
        Assert.Equal(expected.IsEnabled, actual.IsEnabled);
        Assert.Equal(expected.CustomerId, actual.CustomerId);
        Assert.Equal(expected.AllowedArtTypes, actual.AllowedArtTypes);
    }

    private static void AssertNoSecrets(CollectingLogger<CachedNntpUserRecordStore> logger)
    {
        var text = string.Join('\n', logger.Messages);
        Assert.DoesNotContain(SecretPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(ScramSalt), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(ScramStoredKey), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(ScramServerKey), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Encoding.UTF8.GetString(ScramSalt), text, StringComparison.Ordinal);
        Assert.DoesNotContain("nntpd:account:", text, StringComparison.Ordinal);
    }

    private sealed class RecordingUserRecordStore : INntpUserRecordStore
    {
        private readonly ConcurrentDictionary<string, NntpUserRecord> _users = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _lookups = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);

        public TaskCompletionSource? Block { get; set; }

        public Exception? Exception { get; set; }

        public int LookupCount => _lookups.Values.Sum();

        public void Add(NntpUserRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            _users[record.AccountName] = record;
        }

        public int LookupsFor(string accountName) =>
            _lookups.TryGetValue(accountName, out var count) ? count : 0;

        public Task WaitStartedAsync(string accountName, CancellationToken cancellationToken) =>
            _started.GetOrAdd(
                    accountName,
                    static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task
                .WaitAsync(cancellationToken);

        public async ValueTask<NntpUserRecord?> TryGetUserAsync(
            string accountName,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            _lookups.AddOrUpdate(accountName, 1, static (_, count) => count + 1);
            _started.GetOrAdd(
                    accountName,
                    static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
            if (Block is not null)
            {
                await Block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Exception is not null)
            {
                throw Exception;
            }

            return _users.TryGetValue(accountName, out var record) ? record : null;
        }
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
