using VectorNNTPD.ReaderService.PostFilter.Quota;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.PostFilter.Quota;

/// <summary>
/// Live EVAL of <see cref="PostFilterQuotaScripts"/> through
/// <see cref="RedisPostFilterQuotaStore"/> against the dedicated test Redis.
/// </summary>
public sealed class PostFilterQuotaRedisLuaIntegrationTests : IClassFixture<PostFilterQuotaRedisIntegrationFixture>
{
    private const long Now = 5_000;
    private const string BodyA = "0123456789abcdef";
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeMilliseconds(Now);
    private static readonly PostFilterQuotaWindows Windows = new(10_000, 1_000);

    private readonly PostFilterQuotaRedisIntegrationFixture _redis;

    public PostFilterQuotaRedisLuaIntegrationTests(PostFilterQuotaRedisIntegrationFixture redis)
    {
        _redis = redis;
    }

    [SessionStateRedisIntegrationFact]
    public async Task Reserve_AcceptThenCommit_WritesCurrentBucket()
    {
        var account = await _redis.CreateAccountAsync();
        var id = new PostFilterReservationId("n1", "a", 1);
        var ceilings = new PostFilterQuotaCeilings(10, 10_000, 0, 10, 10_000, 0);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, id, At, Windows, ceilings, 1, 40, 0, null));
        Assert.NotNull(await _redis.ReadQuotaFieldAsync(account, id.Field));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await _redis.Store.CommitAsync(account, id, At, Windows, ceilings));
        Assert.Null(await _redis.ReadQuotaFieldAsync(account, id.Field));
        Assert.Equal("1", await _redis.ReadQuotaFieldAsync(account, PostFilterQuotaKeys.CommittedMessagesField('L', 0)));
        Assert.Equal("40", await _redis.ReadQuotaFieldAsync(account, PostFilterQuotaKeys.CommittedBytesField('L', 0)));
        var ttl = await _redis.QuotaKeyTtlAsync(account);
        Assert.NotNull(ttl);
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Reserve_ExactCeiling_SecondDeniedWritesNothing()
    {
        var account = await _redis.CreateAccountAsync();
        var ceilings = new PostFilterQuotaCeilings(1, 0, 0, 0, 0, 0);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, Id(1), At, Windows, ceilings, 1, 1, 0, null));
        Assert.Equal(
            PostFilterQuotaReserveStatus.DeniedMessagesLong,
            await _redis.Store.ReserveAsync(account, Id(2), At, Windows, ceilings, 1, 1, 0, null));
        Assert.Null(await _redis.ReadQuotaFieldAsync(account, Id(2).Field));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Reserve_ConcurrentRaceForLastSlot()
    {
        var account = await _redis.CreateAccountAsync();
        var ceilings = new PostFilterQuotaCeilings(1, 0, 0, 0, 0, 0);
        var results = await Task.WhenAll(
            _redis.Store.ReserveAsync(account, new PostFilterReservationId("n1", "a", 1), At, Windows, ceilings, 1, 1, 0, null).AsTask(),
            _redis.Store.ReserveAsync(account, new PostFilterReservationId("n2", "b", 1), At, Windows, ceilings, 1, 1, 0, null).AsTask());
        Assert.Equal(1, results.Count(static status => status == PostFilterQuotaReserveStatus.Accepted));
        Assert.Equal(1, results.Count(static status => status == PostFilterQuotaReserveStatus.DeniedMessagesLong));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Reserve_IdempotentAndConflict()
    {
        var account = await _redis.CreateAccountAsync();
        var id = Id(1);
        var ceilings = new PostFilterQuotaCeilings(10, 0, 0, 0, 0, 0);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, id, At, Windows, ceilings, 1, 10, 0, null));
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, id, At, Windows, ceilings, 1, 10, 0, null));
        Assert.Equal(
            PostFilterQuotaReserveStatus.Conflict,
            await _redis.Store.ReserveAsync(account, id, At, Windows, ceilings, 1, 20, 0, null));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Commit_DuplicateAndAfterRelease_AreNoop()
    {
        var account = await _redis.CreateAccountAsync();
        var ceilings = new PostFilterQuotaCeilings(10, 0, 0, 0, 0, 0);
        var id = Id(1);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, id, At, Windows, ceilings, 1, 1, 0, null));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await _redis.Store.CommitAsync(account, id, At, Windows, ceilings));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Noop,
            await _redis.Store.CommitAsync(account, id, At, Windows, ceilings));
        Assert.Equal("1", await _redis.ReadQuotaFieldAsync(account, PostFilterQuotaKeys.CommittedMessagesField('L', 0)));

        var released = Id(2);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, released, At, Windows, ceilings, 1, 1, 0, null));
        Assert.Equal(
            PostFilterQuotaReleaseStatus.Released,
            await _redis.Store.ReleaseAsync(account, released, At));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Noop,
            await _redis.Store.CommitAsync(account, released, At, Windows, ceilings));
        Assert.Equal("1", await _redis.ReadQuotaFieldAsync(account, PostFilterQuotaKeys.CommittedMessagesField('L', 0)));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Commit_AfterExpiry_CreatesNoUsage()
    {
        var account = await _redis.CreateAccountAsync();
        var ceilings = new PostFilterQuotaCeilings(10, 0, 0, 0, 0, 0);
        var id = Id(1);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, id, At, Windows, ceilings, 1, 1, 0, null));
        var expired = DateTimeOffset.FromUnixTimeMilliseconds(Now + 10_000);
        Assert.Equal(
            PostFilterQuotaCommitStatus.Noop,
            await _redis.Store.CommitAsync(account, id, expired, Windows, ceilings));
        Assert.Null(await _redis.ReadQuotaFieldAsync(account, PostFilterQuotaKeys.CommittedMessagesField('L', 1)));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Release_StaleDoesNotTouchNewer()
    {
        var account = await _redis.CreateAccountAsync();
        var ceilings = new PostFilterQuotaCeilings(10, 0, 0, 0, 0, 0);
        var oldId = Id(1);
        var newId = Id(2);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, oldId, At, Windows, ceilings, 1, 1, 0, null));
        Assert.Equal(PostFilterQuotaReleaseStatus.Released, await _redis.Store.ReleaseAsync(account, oldId, At));
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, newId, At, Windows, ceilings, 1, 1, 0, null));
        Assert.Equal(PostFilterQuotaReleaseStatus.Noop, await _redis.Store.ReleaseAsync(account, oldId, At));
        Assert.NotNull(await _redis.ReadQuotaFieldAsync(account, newId.Field));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task BucketBoundary_LiveReservationCountsInNewBucket()
    {
        var account = await _redis.CreateAccountAsync();
        var ceilings = new PostFilterQuotaCeilings(10, 0, 0, 0, 0, 0);
        await _redis.SeedQuotaAsync(account, PostFilterQuotaKeys.CommittedMessagesField('L', 0), "9");
        var straddler = new PostFilterReservationId("n1", "a", 1);
        var late = DateTimeOffset.FromUnixTimeMilliseconds(9_999);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, straddler, late, Windows, ceilings, 1, 1, 0, null));
        var after = DateTimeOffset.FromUnixTimeMilliseconds(10_002);
        var accepted = 0;
        for (var g = 1; g <= 12; g++)
        {
            var status = await _redis.Store.ReserveAsync(
                account,
                new PostFilterReservationId("n2", "b", g),
                after,
                Windows,
                ceilings,
                1,
                1,
                0,
                null);
            if (status == PostFilterQuotaReserveStatus.Accepted)
            {
                accepted++;
            }
        }

        Assert.Equal(9, accepted);
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await _redis.Store.CommitAsync(account, straddler, after, Windows, ceilings));
        Assert.Null(await _redis.ReadQuotaFieldAsync(account, PostFilterQuotaKeys.CommittedMessagesField('L', 0)));
        Assert.Equal("1", await _redis.ReadQuotaFieldAsync(account, PostFilterQuotaKeys.CommittedMessagesField('L', 1)));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Multipost_SameHashRacesAcrossNodes()
    {
        var account = await _redis.CreateAccountAsync();
        var ceilings = new PostFilterQuotaCeilings(0, 0, 1, 0, 0, 0);
        var results = await Task.WhenAll(
            _redis.Store.ReserveAsync(
                account, new PostFilterReservationId("n1", "a", 1), At, Windows, ceilings, 1, 1, 1, BodyA).AsTask(),
            _redis.Store.ReserveAsync(
                account, new PostFilterReservationId("n2", "b", 1), At, Windows, ceilings, 1, 1, 1, BodyA).AsTask());
        Assert.Equal(1, results.Count(static status => status == PostFilterQuotaReserveStatus.Accepted));
        Assert.Equal(1, results.Count(static status => status == PostFilterQuotaReserveStatus.DeniedIdenticalLong));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task AccountsAreIsolated()
    {
        var alice = await _redis.CreateAccountAsync();
        var bob = await _redis.CreateAccountAsync();
        var ceilings = new PostFilterQuotaCeilings(1, 0, 0, 0, 0, 0);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(alice, Id(1), At, Windows, ceilings, 1, 1, 0, null));
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(bob, Id(1), At, Windows, ceilings, 1, 1, 0, null));
        await _redis.DeleteKeysAsync(alice);
        await _redis.DeleteKeysAsync(bob);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Reserve_ConcurrentRaceForLastByteSlot()
    {
        var account = await _redis.CreateAccountAsync();
        var ceilings = new PostFilterQuotaCeilings(0, 40, 0, 0, 0, 0);
        var results = await Task.WhenAll(
            _redis.Store.ReserveAsync(
                account, new PostFilterReservationId("n1", "a", 1), At, Windows, ceilings, 1, 40, 0, null).AsTask(),
            _redis.Store.ReserveAsync(
                account, new PostFilterReservationId("n2", "b", 1), At, Windows, ceilings, 1, 40, 0, null).AsTask());
        Assert.Equal(1, results.Count(static status => status == PostFilterQuotaReserveStatus.Accepted));
        Assert.Equal(1, results.Count(static status => status == PostFilterQuotaReserveStatus.DeniedBytesLong));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task ReserveOnNodeA_CommitOnNodeB()
    {
        var account = await _redis.CreateAccountAsync();
        var token = new PostFilterReservationId("nodeA", "incA", 1);
        var ceilings = new PostFilterQuotaCeilings(10, 0, 0, 0, 0, 0);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, token, At, Windows, ceilings, 1, 1, 0, null));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await _redis.Store.CommitAsync(account, token, At, Windows, ceilings));
        Assert.Equal("1", await _redis.ReadQuotaFieldAsync(account, PostFilterQuotaKeys.CommittedMessagesField('L', 0)));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task ReserveOnNodeA_ReleaseOnNodeB()
    {
        var account = await _redis.CreateAccountAsync();
        var token = new PostFilterReservationId("nodeA", "incA", 1);
        var ceilings = new PostFilterQuotaCeilings(10, 0, 0, 0, 0, 0);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, token, At, Windows, ceilings, 1, 1, 0, null));
        Assert.Equal(PostFilterQuotaReleaseStatus.Released, await _redis.Store.ReleaseAsync(account, token, At));
        Assert.Null(await _redis.ReadQuotaFieldAsync(account, token.Field));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Noop,
            await _redis.Store.CommitAsync(account, token, At, Windows, ceilings));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task SameAccount_VisibleAcrossNodeIdentities()
    {
        var account = await _redis.CreateAccountAsync();
        var ceilings = new PostFilterQuotaCeilings(1, 0, 0, 0, 0, 0);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(
                account, new PostFilterReservationId("n1", "a", 1), At, Windows, ceilings, 1, 1, 0, null));
        Assert.Equal(
            PostFilterQuotaReserveStatus.DeniedMessagesLong,
            await _redis.Store.ReserveAsync(
                account, new PostFilterReservationId("n2", "b", 1), At, Windows, ceilings, 1, 1, 0, null));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Commit_DisabledDimensionsAreNotWritten()
    {
        var account = await _redis.CreateAccountAsync();
        var id = Id(1);
        var reserveCeilings = new PostFilterQuotaCeilings(10, 10_000, 2, 10, 10_000, 2);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, id, At, Windows, reserveCeilings, 1, 40, 1, BodyA));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await _redis.Store.CommitAsync(account, id, At, Windows, new PostFilterQuotaCeilings(10, 0, 0, 0, 0, 0)));
        Assert.Equal("1", await _redis.ReadQuotaFieldAsync(account, PostFilterQuotaKeys.CommittedMessagesField('L', 0)));
        Assert.Null(await _redis.ReadQuotaFieldAsync(account, PostFilterQuotaKeys.CommittedBytesField('L', 0)));
        Assert.Null(await _redis.ReadQuotaFieldAsync(account, PostFilterQuotaKeys.CommittedMessagesField('S', 0)));
        Assert.Null(await _redis.ReadMultipostFieldAsync(account, PostFilterQuotaKeys.MultipostField(BodyA, 'L', 0)));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task QuotaKeyTtl_IsIdleGarbageCollection_NotWindowExpiry()
    {
        var account = await _redis.CreateAccountAsync();
        var ceilings = new PostFilterQuotaCeilings(10, 0, 0, 0, 0, 0);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, Id(1), At, Windows, ceilings, 1, 1, 0, null));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await _redis.Store.CommitAsync(account, Id(1), At, Windows, ceilings));

        var ttl = await _redis.QuotaKeyTtlAsync(account);
        Assert.NotNull(ttl);
        var idleMs = Math.Max(Windows.LongWindowMs, Windows.ShortWindowMs)
            + (long)PostFilterQuotaDefaults.ReservationTtl.TotalMilliseconds
            + PostFilterQuotaDefaults.IdleTtlSkewMs;
        var remainingLongBucketMs = Windows.LongWindowMs - Now;
        Assert.True(ttl.Value.TotalMilliseconds > remainingLongBucketMs);
        Assert.InRange(ttl.Value.TotalMilliseconds, idleMs - 2_000, idleMs);
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task DerivedHold_StaysLiveThroughSaOperationTimeout_ThenExpires()
    {
        var account = await _redis.CreateAccountAsync();
        var id = Id(1);
        var ceilings = new PostFilterQuotaCeilings(1, 0, 0, 0, 0, 0);
        var hold = PostFilterQuotaDefaults.HoldMilliseconds(true, TimeSpan.FromSeconds(30));
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, id, At, Windows, ceilings, 1, 1, 0, null, reservationTtlMs: hold));
        var duringSa = DateTimeOffset.FromUnixTimeMilliseconds(Now + 30_000);
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await _redis.Store.CommitAsync(account, id, duringSa, Windows, ceilings));
        Assert.Equal("1", await _redis.ReadQuotaFieldAsync(account, PostFilterQuotaKeys.CommittedMessagesField('L', 3)));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task ExpiredReservation_DoesNotConsumeQuotaForLaterPost()
    {
        var account = await _redis.CreateAccountAsync();
        var first = Id(1);
        var second = Id(2);
        var ceilings = new PostFilterQuotaCeilings(1, 0, 0, 0, 0, 0);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, first, At, Windows, ceilings, 1, 1, 0, null));
        var expired = DateTimeOffset.FromUnixTimeMilliseconds(Now + 10_000);
        Assert.Equal(
            PostFilterQuotaCommitStatus.Noop,
            await _redis.Store.CommitAsync(account, first, expired, Windows, ceilings));
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await _redis.Store.ReserveAsync(account, second, expired, Windows, ceilings, 1, 1, 0, null));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await _redis.Store.CommitAsync(account, second, expired, Windows, ceilings));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Commit_StaleGeneration_IsNoop()
    {
        var account = await _redis.CreateAccountAsync();
        var id = Id(1);
        var ceilings = new PostFilterQuotaCeilings(10, 0, 0, 0, 0, 0);
        await _redis.SeedQuotaAsync(
            account,
            id.Field,
            PostFilterQuotaKeys.PackReservation(Now + 10_000, 99, 1, 1, 0, string.Empty));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Noop,
            await _redis.Store.CommitAsync(account, id, At, Windows, ceilings));
        Assert.NotNull(await _redis.ReadQuotaFieldAsync(account, id.Field));
        await _redis.DeleteKeysAsync(account);
    }

    private static PostFilterReservationId Id(long generation) => new("n1", "a", generation);
}
