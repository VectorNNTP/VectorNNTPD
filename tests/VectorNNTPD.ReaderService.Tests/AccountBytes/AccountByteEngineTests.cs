using VectorNNTPD.ReaderService.SessionState.BytesAccounting;

namespace VectorNNTPD.ReaderService.Tests.AccountBytes;

public sealed class AccountByteEngineTests
{
    [Fact]
    public void Apply_MissingKey_InitializesFromMysqlAfter_WithoutSubtractingConsumed()
    {
        var engine = new AccountByteEngine();
        Assert.Equal(900, engine.Apply("k", "batch-a", consumed: 100, mysqlRemainingAfter: 900));
        Assert.Equal(900, engine.Observe("k"));
        Assert.True(engine.WasApplied("k", "batch-a"));
    }

    [Theory]
    [InlineData(1000, 100, 900, 900)]
    [InlineData(1000, 1000, 0, 0)]
    [InlineData(1000, 1500, 0, 0)]
    [InlineData(0, 50, 0, 0)]
    public void Apply_ExistingKey_FloorsToMysqlAndNeverGoesNegative(
        long start,
        long consumed,
        long mysqlAfter,
        long expected)
    {
        var engine = new AccountByteEngine();
        engine.Write("k", start);
        Assert.Equal(expected, engine.Apply("k", "b1", consumed, mysqlAfter));
        Assert.True(engine.Observe("k") >= 0);
    }

    [Fact]
    public void Apply_StaleHighRedis_FloorsToMysqlRemaining()
    {
        var engine = new AccountByteEngine();
        engine.Write("k", 8_000);
        Assert.Equal(100, engine.Apply("k", "b1", consumed: 100, mysqlRemainingAfter: 100));
    }

    [Fact]
    public void Apply_RedisBelowMysql_NeverIncreases()
    {
        var engine = new AccountByteEngine();
        engine.Write("k", 50);
        Assert.Equal(50, engine.Apply("k", "b1", consumed: 10, mysqlRemainingAfter: 200));
    }

    [Fact]
    public void Apply_SameBatchTwice_DoesNotChangeRemaining()
    {
        var engine = new AccountByteEngine();
        engine.Write("k", 1000);
        Assert.Equal(900, engine.Apply("k", "batch-a", 100, 900));
        Assert.Equal(900, engine.Apply("k", "batch-a", 100, 900));
    }

    [Fact]
    public void Apply_SameBatchTenTimes_DoesNotChangeRemaining()
    {
        var engine = new AccountByteEngine();
        engine.Write("k", 1000);
        Assert.Equal(900, engine.Apply("k", "batch-a", 100, 900));
        for (var i = 0; i < 9; i++)
        {
            Assert.Equal(900, engine.Apply("k", "batch-a", 100, 900));
        }
    }

    [Fact]
    public void Apply_BatchAThenB_FloorsToLowestMysqlAfter()
    {
        var engine = new AccountByteEngine();
        engine.Write("k", 1000);
        Assert.Equal(900, engine.Apply("k", "A", 100, 900));
        Assert.Equal(800, engine.Apply("k", "B", 100, 800));
    }

    [Fact]
    public void Apply_BatchBThenA_DoesNotRaiseAndDoesNotDoubleCount()
    {
        var engine = new AccountByteEngine();
        engine.Write("k", 1000);
        Assert.Equal(800, engine.Apply("k", "B", 100, 800));
        Assert.Equal(800, engine.Apply("k", "A", 100, 900));
    }

    [Fact]
    public void Apply_DuplicateAfterOppositeOrder_Unchanged()
    {
        var engine = new AccountByteEngine();
        engine.Write("k", 1000);
        Assert.Equal(800, engine.Apply("k", "B", 100, 800));
        Assert.Equal(800, engine.Apply("k", "A", 100, 900));
        Assert.Equal(800, engine.Apply("k", "A", 100, 900));
        Assert.Equal(800, engine.Apply("k", "B", 100, 800));
    }

    [Fact]
    public void Apply_MissingKey_RepeatedSameBatch_StaysAtMysqlAfter()
    {
        var engine = new AccountByteEngine();
        Assert.Equal(900, engine.Apply("k", "batch-a", 100, 900));
        Assert.Equal(900, engine.Apply("k", "batch-a", 100, 900));
        Assert.Equal(900, engine.Observe("k"));
    }

    [Fact]
    public void Apply_NegativeInputs_ClampToZero()
    {
        var engine = new AccountByteEngine();
        Assert.Equal(0, engine.Apply("k", "b1", consumed: -5, mysqlRemainingAfter: -9));
        Assert.Equal(0, engine.Observe("k"));
    }

    [Fact]
    public void Apply_RequiresBatchId()
    {
        var engine = new AccountByteEngine();
        Assert.Throws<ArgumentException>(() => engine.Apply("k", "", 1, 1));
        Assert.Throws<ArgumentException>(() => engine.Apply("k", " ", 1, 1));
    }

    [Fact]
    public void Observe_Missing_ReturnsSentinel()
    {
        var engine = new AccountByteEngine();
        Assert.Equal(AccountByteKeys.Missing, engine.Observe("missing"));
    }

    [Fact]
    public void ConcurrentApply_NeverNegativeAndNeverIncreases()
    {
        var engine = new AccountByteEngine();
        engine.Write("k", 1_000);
        Parallel.For(0, 20, i => engine.Apply("k", "n" + i, 30, mysqlRemainingAfter: 400));
        Assert.Equal(400, engine.Observe("k"));
    }

    [Fact]
    public void Apply_EvictedBatchMark_RetryStillDoesNotChangeRemaining()
    {
        var engine = new AccountByteEngine();
        engine.Write("k", 10_000);
        Assert.Equal(9_000, engine.Apply("k", "oldest", 1_000, 9_000));
        var remaining = 9_000L;
        for (var i = 0; i < AccountByteBatchId.MaxRetainedMarks; i++)
        {
            remaining -= 1;
            Assert.Equal(remaining, engine.Apply("k", "n" + i, 1, remaining));
        }

        Assert.False(engine.WasApplied("k", "oldest"));
        Assert.Equal(remaining, engine.Apply("k", "oldest", 1_000, 9_000));
        Assert.Equal(remaining, engine.Observe("k"));
    }

    [Fact]
    public void Keys_AreAccountSha256_WithoutTtlNamespace()
    {
        var key = System.Text.Encoding.ASCII.GetString(AccountByteKeys.Create("alice"));
        Assert.StartsWith("nntpd:bytes:", key, StringComparison.Ordinal);
        Assert.Equal(12 + 64, key.Length);
        Assert.NotEqual(AccountByteKeys.Create("alice"), AccountByteKeys.Create("bob"));
        Assert.Equal(AccountByteKeys.Create("alice"), AccountByteKeys.Create("alice"));
    }
}
