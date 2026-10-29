using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.TransitService.Redis;
using VectorNNTPD.TransitService.Tests.TestDoubles;

namespace VectorNNTPD.TransitService.Tests.History;

public sealed class HistoryDbTests
{
    private static readonly byte[] MessageId = "<want@example.com>"u8.ToArray();
    private static readonly byte[] OtherId = "<other@example.com>"u8.ToArray();

    [Fact]
    public void HistoryTime_Default_IsTwoHours()
    {
        Assert.Equal(TimeSpan.FromHours(2), new VectorNNTPD.TransitService.Configuration.NntpdOptions().HistoryTime);
    }

    [Fact]
    public void IdenticalMessageIds_ProduceIdenticalKeys()
    {
        var left = HistoryDigest.FromMessageId(MessageId);
        var right = HistoryDigest.FromMessageId(MessageId);
        Assert.Equal(left, right);
        Assert.Equal(HistoryRedisKeys.Create(left), HistoryRedisKeys.Create(right));
    }

    [Fact]
    public void HistoryDigest_MatchesCommonArticleIdBytes()
    {
        var history = HistoryDigest.FromMessageId(MessageId);
        var articleId = VectorNNTPD.Common.Articles.ArticleId.FromMessageId(MessageId);
        Span<byte> historyBytes = stackalloc byte[HistoryDigest.Length];
        Span<byte> articleIdBytes = stackalloc byte[VectorNNTPD.Common.Articles.ArticleId.Length];
        history.CopyTo(historyBytes);
        articleId.CopyTo(articleIdBytes);
        Assert.True(historyBytes.SequenceEqual(articleIdBytes));
    }

    [Fact]
    public void DifferentMessageIds_ProduceDifferentKeys()
    {
        var left = HistoryRedisKeys.Create(HistoryDigest.FromMessageId(MessageId));
        var right = HistoryRedisKeys.Create(HistoryDigest.FromMessageId(OtherId));
        Assert.False(left.AsSpan().SequenceEqual(right));
    }

    [Fact]
    public void RedisKey_IsBinaryNamespacePlusDigest()
    {
        var digest = HistoryDigest.FromMessageId(MessageId);
        var key = HistoryRedisKeys.Create(digest);
        Assert.Equal(HistoryRedisKeys.KeyLength, key.Length);
        Assert.True(key.AsSpan().StartsWith(HistoryRedisKeys.NamespacePrefix));
        Assert.Equal("nntpd:hist:"u8.Length + HistoryDigest.Length, key.Length);

        var suffix = key.AsSpan(HistoryRedisKeys.NamespacePrefix.Length);
        var reconstructed = HistoryDigest.FromSpan(suffix);
        Assert.Equal(digest, reconstructed);
        Assert.False(suffix.StartsWith("nntpd:hist:"u8));
    }

    [Fact]
    public void Namespace_IsolatesHistoryKeys()
    {
        var key = HistoryRedisKeys.Create(HistoryDigest.FromMessageId(MessageId));
        Assert.StartsWith("nntpd:hist:", Encoding.ASCII.GetString(key[..HistoryRedisKeys.NamespacePrefix.Length]));
    }

    [Fact]
    public async Task MemoryMiss_RedisMiss_ReturnsUnseen_AndRecordsLocal()
    {
        var redis = new FakeRedisService();
        var history = Create(redis);

        var result = await history.LookupAsync(MessageId);

        Assert.Equal(HistoryLookupResult.Unseen, result);
        Assert.True(history.ContainsLocal(HistoryDigest.FromMessageId(MessageId)));
        Assert.Equal(1, redis.Database.KeyExistsCount);
    }

    [Fact]
    public async Task MemoryHit_DoesNotQueryRedis()
    {
        var redis = new FakeRedisService();
        var history = Create(redis);
        _ = await history.LookupAsync(MessageId);
        redis.Database.KeyExistsCount = 0;

        var result = await history.LookupAsync(MessageId);

        Assert.Equal(HistoryLookupResult.Seen, result);
        Assert.Equal(0, redis.Database.KeyExistsCount);
    }

    [Fact]
    public async Task MemoryMiss_RedisHit_ReturnsSeen_AndWarmsMemory()
    {
        var redis = new FakeRedisService();
        var digest = HistoryDigest.FromMessageId(MessageId);
        redis.Database.Seed(HistoryRedisKeys.Create(digest));
        var history = Create(redis);

        var result = await history.LookupAsync(MessageId);

        Assert.Equal(HistoryLookupResult.Seen, result);
        Assert.True(history.ContainsLocal(digest));
        Assert.Equal(0, redis.Database.SetCount);
    }

    [Fact]
    public async Task DoubleMiss_EnqueuesAsyncRedisWrite_WithoutWaiting()
    {
        var redis = new FakeRedisService();
        redis.Database.BlockSet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = Create(redis);

        var lookup = history.LookupAsync(MessageId);
        var completed = await Task.WhenAny(lookup.AsTask(), Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(lookup.AsTask(), completed);
        Assert.Equal(HistoryLookupResult.Unseen, await lookup);
        Assert.Equal(0, redis.Database.SetCount);

        redis.Database.BlockSet.SetResult();
        var writer = new HistoryWriteService(history, redis, NullLogger<HistoryWriteService>.Instance);
        await writer.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (redis.Database.SetCount == 0)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        Assert.True(redis.Database.Contains(HistoryRedisKeys.Create(HistoryDigest.FromMessageId(MessageId))));
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RedisLookupFailure_IsUnavailable_NotUnseen()
    {
        var redis = new FakeRedisService();
        redis.Database.ExistsException = new RedisUnavailableException("timeout");
        var history = Create(redis);

        var result = await history.LookupAsync(MessageId);

        Assert.Equal(HistoryLookupResult.Unavailable, result);
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(MessageId)));
    }

    [Fact]
    public async Task RedisLookupFailure_DoesNotPopulateLocal_AndDoesNotHammerRedis()
    {
        var redis = new FakeRedisService();
        redis.Database.ExistsException = new RedisUnavailableException("timeout");
        var history = Create(redis);

        Assert.Equal(HistoryLookupResult.Unavailable, await history.LookupAsync(MessageId));
        Assert.Equal(1, redis.Database.KeyExistsCount);
        Assert.True(redis.IsUnavailable);

        Assert.Equal(HistoryLookupResult.Unavailable, await history.LookupAsync(OtherId));
        Assert.Equal(1, redis.Database.KeyExistsCount);
        Assert.Equal(1, redis.BeginRejectCount);
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(MessageId)));
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(OtherId)));
    }

    [Fact]
    public async Task RedisRecovery_RestoresMissAndHit()
    {
        var redis = new FakeRedisService();
        redis.Database.ExistsException = new RedisUnavailableException("timeout");
        var history = Create(redis);
        Assert.Equal(HistoryLookupResult.Unavailable, await history.LookupAsync(MessageId));

        redis.Recover();
        redis.Database.ExistsException = null;
        Assert.Equal(HistoryLookupResult.Unseen, await history.LookupAsync(MessageId));
        Assert.True(history.ContainsLocal(HistoryDigest.FromMessageId(MessageId)));

        redis.Database.Seed(HistoryRedisKeys.Create(HistoryDigest.FromMessageId(OtherId)));
        Assert.Equal(HistoryLookupResult.Seen, await history.LookupAsync(OtherId));
    }

    [Fact]
    public async Task Writer_SetFailure_DoesNotChangeAlreadyIssuedUnseen()
    {
        var redis = new FakeRedisService();
        redis.Database.SetException = new RedisUnavailableException("write-fail");
        var history = Create(redis);
        Assert.Equal(HistoryLookupResult.Unseen, await history.LookupAsync(MessageId));
        Assert.True(history.ContainsLocal(HistoryDigest.FromMessageId(MessageId)));

        var writer = new HistoryWriteService(history, redis, NullLogger<HistoryWriteService>.Instance);
        await writer.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (redis.Database.SetCount == 0)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        await writer.StopAsync(CancellationToken.None);
        Assert.Equal(HistoryLookupResult.Seen, await history.LookupAsync(MessageId));
    }

    [Fact]
    public async Task LocalExpiry_MatchesConfiguredRetention()
    {
        var clock = new ManualTimeProvider();
        var redis = new FakeRedisService();
        var queue = new HistoryWriteQueue();
        var history = new HistoryDb(redis, TimeSpan.FromMinutes(5), NullLogger<HistoryDb>.Instance, clock, queue);
        _ = await history.LookupAsync(MessageId);
        Assert.True(history.ContainsLocal(HistoryDigest.FromMessageId(MessageId)));

        clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(MessageId)));
    }

    [Fact]
    public void WriteQueue_DropsWhenFull()
    {
        var queue = new HistoryWriteQueue(capacity: 1);
        var first = HistoryDigest.FromMessageId(MessageId);
        var second = HistoryDigest.FromMessageId(OtherId);
        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(second));
    }

    [Fact]
    public async Task Writer_Stop_DrainsOrCancelsWithoutThrowing()
    {
        var redis = new FakeRedisService();
        redis.Database.BlockSet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = Create(redis);
        _ = await history.LookupAsync(MessageId);
        var writer = new HistoryWriteService(history, redis, NullLogger<HistoryWriteService>.Instance);
        await writer.StartAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await writer.StopAsync(cts.Token);
        redis.Database.BlockSet.TrySetResult();
    }

    [Fact]
    public async Task Writer_RedisWriteFailure_IsObserved()
    {
        var redis = new FakeRedisService();
        redis.Database.SetException = new RedisUnavailableException("write-fail");
        var history = Create(redis);
        _ = await history.LookupAsync(MessageId);
        var writer = new HistoryWriteService(history, redis, NullLogger<HistoryWriteService>.Instance);
        await writer.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (redis.Database.SetCount == 0)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        await writer.StopAsync(CancellationToken.None);
        Assert.Equal(1, redis.Database.SetCount);
    }

    [Fact]
    public async Task Peek_Miss_DoesNotRecordLocal()
    {
        var redis = new FakeRedisService();
        var history = Create(redis);

        var result = await history.PeekAsync(MessageId);

        Assert.Equal(HistoryLookupResult.Unseen, result);
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(MessageId)));
        Assert.Equal(1, redis.Database.KeyExistsCount);
    }

    [Fact]
    public async Task Capture_CountsLookupsHitsMissesAndErrors()
    {
        var redis = new FakeRedisService();
        var history = Create(redis);

        Assert.Equal(HistoryLookupResult.Unseen, await history.LookupAsync(MessageId));
        Assert.Equal(HistoryLookupResult.Seen, await history.LookupAsync(MessageId));
        redis.IsUnavailable = true;
        Assert.Equal(HistoryLookupResult.Unavailable, await history.LookupAsync(OtherId));

        var snapshot = history.Capture();
        Assert.Equal(3, snapshot.Lookups);
        Assert.Equal(1, snapshot.Hits);
        Assert.Equal(1, snapshot.Misses);
        Assert.Equal(1, snapshot.Errors);
        Assert.True(snapshot.WaitTicks >= 0);
    }

    [Fact]
    public async Task Remember_RecordsLocalWithoutExists()
    {
        var redis = new FakeRedisService();
        var history = Create(redis);
        redis.Database.KeyExistsCount = 0;

        history.Remember(MessageId);

        Assert.True(history.ContainsLocal(HistoryDigest.FromMessageId(MessageId)));
        Assert.Equal(0, redis.Database.KeyExistsCount);
        var peek = await history.PeekAsync(MessageId);
        Assert.Equal(HistoryLookupResult.Seen, peek);
        Assert.Equal(0, redis.Database.KeyExistsCount);
    }

    private static HistoryDb Create(FakeRedisService redis) =>
        new(redis, TimeSpan.FromHours(2), NullLogger<HistoryDb>.Instance, TimeProvider.System, new HistoryWriteQueue());

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utc;

        public void Advance(TimeSpan delta) => _utc += delta;
    }
}
