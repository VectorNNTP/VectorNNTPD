using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.AccountBytes;

public sealed class AccountByteDurableStoreTests
{
    [Fact]
    public void ConsumeResult_RejectsNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AccountByteConsumeResult(AccountByteConsumeStatus.Consumed, remaining: -1, consumed: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AccountByteConsumeResult(AccountByteConsumeStatus.Consumed, remaining: 0, consumed: -1));
    }

    [Theory]
    [InlineData(1000, 100, 900, 100)]
    [InlineData(1000, 1000, 0, 1000)]
    [InlineData(1000, 1500, 0, 1000)]
    [InlineData(0, 50, 0, 0)]
    public async Task InMemory_Consume_ClampsAtZero(
        long start,
        long bytes,
        long remaining,
        long consumed)
    {
        var store = new InMemoryAccountByteDurableStore();
        store.SeedByteAccount("alice", start);
        var result = await store.ConsumeAsync("alice", bytes);
        Assert.Equal(AccountByteConsumeStatus.Consumed, result.Status);
        Assert.Equal(remaining, result.Remaining);
        Assert.Equal(consumed, result.Consumed);
        Assert.True(result.Remaining >= 0);
        Assert.True(result.Consumed >= 0);
    }

    [Fact]
    public async Task InMemory_Consume_RejectsNegativeBytes()
    {
        var store = new InMemoryAccountByteDurableStore();
        store.SeedByteAccount("alice", 10);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ConsumeAsync("alice", -1).AsTask());
    }

    [Fact]
    public async Task InMemory_MissingAccount_DoesNotConsume()
    {
        var store = new InMemoryAccountByteDurableStore();
        store.SeedByteAccount("rate", 100);
        var consumed = await store.ConsumeAsync("rate", 10);
        Assert.Equal(AccountByteConsumeStatus.Consumed, consumed.Status);
        Assert.Equal(90, consumed.Remaining);
        Assert.Equal(AccountByteConsumeStatus.AccountNotFound, (await store.ConsumeAsync("ghost", 10)).Status);
        Assert.Equal(AccountByteConsumeStatus.Consumed, (await store.QueryRemainingAsync("rate")).Status);
        Assert.Equal(AccountByteConsumeStatus.AccountNotFound, (await store.QueryRemainingAsync("ghost")).Status);
    }

    [Fact]
    public async Task InMemory_ConcurrentDecrements_NeverNegative()
    {
        var store = new InMemoryAccountByteDurableStore();
        store.SeedByteAccount("alice", 1000);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.ConsumeAsync("alice", 80).AsTask()));
        Assert.Equal(0, store.Remaining("alice"));
    }

    [Fact]
    public async Task FakeNntpDb_ConsumeAndQuery_MatchInMemorySemantics()
    {
        var factory = new FakeNntpDbConnectionFactory();
        factory.Users["alice"] = MemoryNntpUserRecordStore.Create("alice", "pw", byteLimit: 1000);
        factory.Users["rate"] = MemoryNntpUserRecordStore.Create("rate", "pw", rateLimitBps: 2_400, byteLimit: 50);
        await using var connection = (FakeNntpDbConnection)await factory.OpenAsync("Server=x;Database=y", CancellationToken.None);

        var consumed = await connection.ConsumeAccountBytesAsync("alice", 100, CancellationToken.None);
        Assert.Equal(900, consumed.Remaining);
        Assert.Equal(100, consumed.Consumed);
        Assert.Equal(900, (await connection.QueryAccountByteRemainingAsync("alice", CancellationToken.None)).Remaining);

        var over = await connection.ConsumeAccountBytesAsync("alice", 5000, CancellationToken.None);
        Assert.Equal(0, over.Remaining);
        Assert.Equal(900, over.Consumed);

        var zero = await connection.ConsumeAccountBytesAsync("alice", 10, CancellationToken.None);
        Assert.Equal(0, zero.Remaining);
        Assert.Equal(0, zero.Consumed);

        var rate = await connection.ConsumeAccountBytesAsync("rate", 1, CancellationToken.None);
        Assert.Equal(AccountByteConsumeStatus.Consumed, rate.Status);
        Assert.Equal(49, rate.Remaining);
        Assert.Equal(
            AccountByteConsumeStatus.AccountNotFound,
            (await connection.ConsumeAccountBytesAsync("ghost", 1, CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData(-5L, 0L)]
    [InlineData(0L, 0L)]
    [InlineData(10L, 10L)]
    public void ConvertByteLimit_NeverNegative(long input, long expected)
    {
        Assert.Equal(expected, MySqlNntpDbConnection.ConvertByteLimit(input));
    }

    [Fact]
    public void ConvertByteLimit_SaturatesUnsignedOverflow()
    {
        Assert.Equal(long.MaxValue, MySqlNntpDbConnection.ConvertByteLimit(ulong.MaxValue));
        Assert.Equal(0, MySqlNntpDbConnection.ConvertByteLimit(null));
    }

    [Fact]
    public void ConsumeSql_UsesCaseClamp_NotUncheckedSubtract()
    {
        Assert.Contains("WHEN account_byte_limit > @bytes THEN account_byte_limit - @bytes", NntpUserQueries.ConsumeAccountBytes, StringComparison.Ordinal);
        Assert.Contains("ELSE 0 END", NntpUserQueries.ConsumeAccountBytes, StringComparison.Ordinal);
        Assert.DoesNotContain("account_type", NntpUserQueries.ConsumeAccountBytes, StringComparison.Ordinal);
        Assert.DoesNotContain("account_byte_limit = account_byte_limit - @bytes", NntpUserQueries.ConsumeAccountBytes, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", NntpUserQueries.SelectByteQuotaForUpdate, StringComparison.Ordinal);
    }
}
