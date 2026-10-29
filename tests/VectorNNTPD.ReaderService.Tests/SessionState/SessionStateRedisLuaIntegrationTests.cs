using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.SessionState;

/// <summary>
/// Live EVAL of <see cref="SessionStateScripts"/> through
/// <see cref="RedisSessionStateStore"/> against the dedicated test Redis.
/// </summary>
public sealed class SessionStateRedisLuaIntegrationTests : IClassFixture<SessionStateRedisIntegrationFixture>
{
    private const string O1 = "nntpd01:a";
    private const string O2 = "nntpd02:b";
    private const string IpA = "192.0.2.10";
    private const string IpB = "198.51.100.20";
    private const string Ip6 = "2001:db8::10";
    private const long Now = 10_000;
    private const long Lease = 30_000;
    private readonly SessionStateRedisIntegrationFixture _redis;

    public SessionStateRedisLuaIntegrationTests(SessionStateRedisIntegrationFixture redis)
    {
        _redis = redis;
    }

    [SessionStateRedisIntegrationFact]
    public void ConnectsToAuthorizedTestRedis()
    {
        Assert.True(_redis.IsConfigured);
        Assert.Equal(SessionStateRedisIntegration.AuthorizedEndpoint, _redis.Endpoint);
        Assert.False(string.IsNullOrWhiteSpace(_redis.ServerVersion));
    }

    [SessionStateRedisIntegrationFact]
    public async Task TryAdmit_NewSessionAndSource_WritesBothHashes()
    {
        var account = await _redis.CreateAccountAsync();
        var engine = new SessionStateEngine();
        Assert.Equal(
            SessionStateEngine.AcceptedNew,
            engine.TryAdmitStatus("src", "sess", IpA, O1, 10, 4, Now, Lease, 1, 1));
        var result = await _redis.TryAdmitAsync(account, IpA, O1, 10, 4, 1, 1, Now, Lease);
        Assert.Equal(SessionStateAdmitStatus.AcceptedNew, result.Status);
        var session = await _redis.ReadSessionAsync(account, O1);
        var source = await _redis.ReadSourceAsync(account, IpA, O1);
        AssertOwnership(session, Now + Lease, 1, 1);
        AssertOwnership(source, Now + Lease, 1, 1);
        Assert.Null(await _redis.KeyTimeToLiveAsync(account, session: true));
        Assert.Null(await _redis.KeyTimeToLiveAsync(account, session: false));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task TryAdmit_SecondSessionSameSource_IncrementsSessionKeepsOneIp()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        AssertOwnership(await _redis.ReadSessionAsync(account, O1), Now + Lease, 1, 2);
        AssertOwnership(await _redis.ReadSourceAsync(account, IpA, O1), Now + Lease, 1, 2);
        Assert.Equal(2, await _redis.SessionCountAsync(account));
        Assert.Equal(1, await _redis.DistinctSourceCountAsync(account));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task TryAdmit_NewSource_AddsSecondSourceField()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(account, IpB, O1, 10, 4, 1, 3, Now, Lease)).Accepted);
        AssertOwnership(await _redis.ReadSessionAsync(account, O1), Now + Lease, 1, 2);
        Assert.NotNull(await _redis.ReadSourceAsync(account, IpA, O1));
        Assert.NotNull(await _redis.ReadSourceAsync(account, IpB, O1));
        Assert.Equal(2, await _redis.DistinctSourceCountAsync(account));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task TryAdmit_SessionLimit_AcceptsExactlyN()
    {
        var account = await _redis.CreateAccountAsync();
        for (var i = 0; i < 3; i++)
        {
            Assert.True((await _redis.TryAdmitAsync(account, IpA, "owner:" + i, 3, 4, 1, 1, Now, Lease)).Accepted);
        }

        Assert.Equal(
            SessionStateAdmitStatus.RejectedSessionLimit,
            (await _redis.TryAdmitAsync(account, IpA, "owner:3", 3, 4, 1, 1, Now, Lease)).Status);
        Assert.Equal(3, await _redis.SessionCountAsync(account));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task TryAdmit_SourceLimit_AcceptsExactlyNDistinctIps()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(account, "203.0.113.1", O1, 20, 2, 1, 1, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(account, "203.0.113.2", O1, 20, 2, 1, 2, Now, Lease)).Accepted);
        Assert.Equal(
            SessionStateAdmitStatus.RejectedSourceLimit,
            (await _redis.TryAdmitAsync(account, "203.0.113.3", O1, 20, 2, 1, 3, Now, Lease)).Status);
        Assert.Equal(2, await _redis.SessionCountAsync(account));
        Assert.Equal(2, await _redis.DistinctSourceCountAsync(account));
        Assert.Null(await _redis.ReadSourceAsync(account, "203.0.113.3", O1));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task TryAdmit_BothLimitsViolated_IsSessionLimitFirst()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O1, 1, 1, 1, 1, Now, Lease)).Accepted);
        var result = await _redis.TryAdmitAsync(account, IpB, O2, 1, 1, 1, 1, Now, Lease);
        Assert.Equal(SessionStateAdmitStatus.RejectedSessionLimit, result.Status);
        Assert.Equal(1, await _redis.SessionCountAsync(account));
        Assert.Null(await _redis.ReadSessionAsync(account, O2));
        Assert.Null(await _redis.ReadSourceAsync(account, IpB, O2));
        Assert.NotNull(await _redis.ReadSessionAsync(account, O1));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task TryAdmit_GenerationReplacement_ReplacesCountAndIgnoresStaleRelease()
    {
        var account = await _redis.CreateAccountAsync();
        await _redis.SeedSessionAsync(account, O1, 30_000, 1, 7);
        await _redis.SeedSourceAsync(account, IpA, O1, 30_000, 1, 7);
        var engine = new SessionStateEngine();
        engine.WriteOwnership("sess", O1, 30_000, 1, 7);
        engine.WriteOwnership("src", SessionStateKeys.SourceField(IpA, O1), 30_000, 1, 7);
        Assert.Equal(SessionStateEngine.AcceptedExisting, engine.TryAdmitStatus("src", "sess", IpA, O1, 20, 4, 0, Lease, 2, 2));

        Assert.True((await _redis.TryAdmitAsync(account, IpA, O1, 20, 4, 2, 2, 0, Lease)).Accepted);
        AssertOwnership(await _redis.ReadSessionAsync(account, O1), Lease, 2, 1);
        AssertOwnership(await _redis.ReadSourceAsync(account, IpA, O1), Lease, 2, 1);

        await _redis.ReleaseAsync(account, IpA, O1, 1, 1);
        AssertOwnership(await _redis.ReadSessionAsync(account, O1), Lease, 2, 1);
        AssertOwnership(await _redis.ReadSourceAsync(account, IpA, O1), Lease, 2, 1);

        await _redis.ReleaseAsync(account, IpA, O1, 2, 2);
        Assert.Null(await _redis.ReadSessionAsync(account, O1));
        Assert.Null(await _redis.ReadSourceAsync(account, IpA, O1));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Release_SameGeneration_DecrementsThenRemoves()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        await _redis.ReleaseAsync(account, IpA, O1, 1, 1);
        AssertOwnership(await _redis.ReadSessionAsync(account, O1), Now + Lease, 1, 1);
        await _redis.ReleaseAsync(account, IpA, O1, 1, 1);
        Assert.Null(await _redis.ReadSessionAsync(account, O1));
        Assert.Null(await _redis.ReadSourceAsync(account, IpA, O1));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Release_DifferentOwner_LeavesPeer()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O2, 10, 4, 9, 9, Now, Lease)).Accepted);
        await _redis.ReleaseAsync(account, IpA, O1, 1, 1);
        Assert.Null(await _redis.ReadSessionAsync(account, O1));
        AssertOwnership(await _redis.ReadSessionAsync(account, O2), Now + Lease, 9, 1);
        AssertOwnership(await _redis.ReadSourceAsync(account, IpA, O2), Now + Lease, 9, 1);
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Release_SourceField_LeavesOtherOwnerAndOtherIp()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O2, 10, 4, 9, 9, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(account, IpB, O1, 10, 4, 1, 3, Now, Lease)).Accepted);
        await _redis.ReleaseAsync(account, IpA, O1, 1, 1);
        Assert.Null(await _redis.ReadSourceAsync(account, IpA, O1));
        Assert.NotNull(await _redis.ReadSourceAsync(account, IpA, O2));
        Assert.NotNull(await _redis.ReadSourceAsync(account, IpB, O1));
        Assert.Equal("192.0.2.10\u001fnntpd02:b", SessionStateKeys.SourceField(IpA, O2));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Renew_Valid_ExtendsExpiryUsingSuppliedNow()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        Assert.Equal(SessionStateRenewStatus.Renewed, (await _redis.RenewAsync(account, O1, 1, [(IpA, 1)], 20_000, Lease)).Status);
        AssertOwnership(await _redis.ReadSessionAsync(account, O1), 50_000, 1, 1);
        AssertOwnership(await _redis.ReadSourceAsync(account, IpA, O1), 50_000, 1, 1);
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Renew_WrongGeneration_IsLostAndLeavesG2()
    {
        var account = await _redis.CreateAccountAsync();
        await _redis.SeedSessionAsync(account, O1, 30_000, 2, 1);
        await _redis.SeedSourceAsync(account, IpA, O1, 30_000, 2, 1);
        Assert.Equal(SessionStateRenewStatus.Lost, (await _redis.RenewAsync(account, O1, 1, [(IpA, 1)], Now, Lease)).Status);
        AssertOwnership(await _redis.ReadSessionAsync(account, O1), 30_000, 2, 1);
        AssertOwnership(await _redis.ReadSourceAsync(account, IpA, O1), 30_000, 2, 1);
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Renew_Missing_IsLostAndDoesNotCreate()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.Equal(SessionStateRenewStatus.Lost, (await _redis.RenewAsync(account, O1, 1, [(IpA, 1)], Now, Lease)).Status);
        Assert.Null(await _redis.ReadSessionAsync(account, O1));
        Assert.Null(await _redis.ReadSourceAsync(account, IpA, O1));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Renew_Expired_IsLostAndDoesNotRecreate()
    {
        var account = await _redis.CreateAccountAsync();
        await _redis.SeedSessionAsync(account, O1, 1_000, 1, 1);
        await _redis.SeedSourceAsync(account, IpA, O1, 1_000, 1, 1);
        Assert.Equal(SessionStateRenewStatus.Lost, (await _redis.RenewAsync(account, O1, 1, [(IpA, 1)], 2_000, Lease)).Status);
        AssertOwnership(await _redis.ReadSessionAsync(account, O1), 1_000, 1, 1);
        AssertOwnership(await _redis.ReadSourceAsync(account, IpA, O1), 1_000, 1, 1);
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Renew_OneInvalidField_IsAllOrNothing()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(account, IpB, O1, 10, 4, 1, 3, Now, Lease)).Accepted);
        var before = await _redis.ReadSessionAsync(account, O1);
        Assert.Equal(
            SessionStateRenewStatus.Lost,
            (await _redis.RenewAsync(account, O1, 1, [(IpA, 1), (IpB, 99)], 20_000, Lease)).Status);
        Assert.Equal(before, await _redis.ReadSessionAsync(account, O1));
        AssertOwnership(await _redis.ReadSourceAsync(account, IpA, O1), Now + Lease, 1, 1);
        AssertOwnership(await _redis.ReadSourceAsync(account, IpB, O1), Now + Lease, 3, 1);
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task ReleaseOwner_RemovesOnlyThatOwnerAcrossAccountsAndIps()
    {
        var alice = await _redis.CreateAccountAsync();
        var bob = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(alice, IpA, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(alice, IpB, O1, 10, 4, 1, 3, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(alice, IpA, O2, 10, 4, 9, 9, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(bob, IpA, O1, 10, 4, 2, 2, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(bob, IpA, O2, 10, 4, 8, 8, Now, Lease)).Accepted);
        await _redis.ReleaseOwnerAsync(alice, O1);
        await _redis.ReleaseOwnerAsync(bob, O1);
        Assert.Null(await _redis.ReadSessionAsync(alice, O1));
        Assert.Null(await _redis.ReadSourceAsync(alice, IpA, O1));
        Assert.Null(await _redis.ReadSourceAsync(alice, IpB, O1));
        Assert.NotNull(await _redis.ReadSessionAsync(alice, O2));
        Assert.NotNull(await _redis.ReadSourceAsync(alice, IpA, O2));
        Assert.Null(await _redis.ReadSessionAsync(bob, O1));
        Assert.NotNull(await _redis.ReadSessionAsync(bob, O2));
        await _redis.DeleteKeysAsync(alice);
        await _redis.DeleteKeysAsync(bob);
    }

    [SessionStateRedisIntegrationFact]
    public async Task ConcurrentTryAdmit_SessionLimit_AcceptsExactlyTen()
    {
        var account = await _redis.CreateAccountAsync();
        var tasks = Enumerable.Range(0, 20)
            .Select(i => _redis.TryAdmitAsync(account, IpA, "node:" + i, 10, 0, 1, 0, Now, Lease).AsTask());
        var results = await Task.WhenAll(tasks);
        Assert.Equal(10, results.Count(static result => result.Accepted));
        Assert.Equal(10, results.Count(static result => result.Status == SessionStateAdmitStatus.RejectedSessionLimit));
        Assert.Equal(10, await _redis.SessionCountAsync(account));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task ConcurrentTryAdmit_SourceLimit_AcceptsExactlyTenDistinctIps()
    {
        var account = await _redis.CreateAccountAsync();
        var tasks = Enumerable.Range(1, 20)
            .Select(i => _redis.TryAdmitAsync(account, "203.0.113." + i, "node:" + i, 0, 10, 0, 1, Now, Lease).AsTask());
        var results = await Task.WhenAll(tasks);
        Assert.Equal(10, results.Count(static result => result.Accepted));
        Assert.Equal(10, results.Count(static result => result.Status == SessionStateAdmitStatus.RejectedSourceLimit));
        Assert.Equal(10, await _redis.DistinctSourceCountAsync(account));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task ConcurrentTryAdmit_BothLimits_SessionLimitWinsForUniqueIps()
    {
        var account = await _redis.CreateAccountAsync();
        var tasks = Enumerable.Range(1, 20)
            .Select(i => _redis.TryAdmitAsync(account, "203.0.113." + i, "node:" + i, 6, 6, 1, 1, Now, Lease).AsTask());
        var results = await Task.WhenAll(tasks);
        Assert.Equal(6, results.Count(static result => result.Accepted));
        Assert.Equal(6, await _redis.SessionCountAsync(account));
        Assert.Equal(6, await _redis.DistinctSourceCountAsync(account));
        Assert.DoesNotContain(results, static result => result.Status == SessionStateAdmitStatus.RejectedSourceLimit);
        Assert.Equal(14, results.Count(static result => result.Status == SessionStateAdmitStatus.RejectedSessionLimit));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task TryAdmit_PrunesExpiredWithoutConsumingCapacity()
    {
        var account = await _redis.CreateAccountAsync();
        await _redis.SeedSessionAsync(account, O1, 1_000, 1, 7);
        await _redis.SeedSourceAsync(account, IpA, O1, 1_000, 1, 7);
        await _redis.SeedSessionAsync(account, O2, 80_000, 9, 1);
        await _redis.SeedSourceAsync(account, IpB, O2, 80_000, 9, 1);
        var engine = new SessionStateEngine();
        engine.WriteOwnership("sess", O1, 1_000, 1, 7);
        engine.WriteOwnership("src", SessionStateKeys.SourceField(IpA, O1), 1_000, 1, 7);
        engine.WriteOwnership("sess", O2, 80_000, 9, 1);
        engine.WriteOwnership("src", SessionStateKeys.SourceField(IpB, O2), 80_000, 9, 1);
        Assert.Equal(SessionStateEngine.AcceptedNew, engine.TryAdmitStatus("src", "sess", IpA, "O3", 2, 4, 2_000, Lease, 1, 1));

        Assert.True((await _redis.TryAdmitAsync(account, IpA, "nntpd03:c", 2, 4, 1, 1, 2_000, Lease)).Accepted);
        Assert.Null(await _redis.ReadSessionAsync(account, O1));
        Assert.Null(await _redis.ReadSourceAsync(account, IpA, O1));
        AssertOwnership(await _redis.ReadSessionAsync(account, O2), 80_000, 9, 1);
        Assert.NotNull(await _redis.ReadSessionAsync(account, "nntpd03:c"));
        Assert.Equal(2, await _redis.SessionCountAsync(account));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task SourceIdentity_MappedIpv6SharesIpv4_NativeIpv6IsIndependent()
    {
        var account = await _redis.CreateAccountAsync();
        var mapped = SessionStateRedisIntegrationFixture.Format("::ffff:192.0.2.10");
        var v4 = SessionStateRedisIntegrationFixture.Format("192.0.2.10");
        var v6 = SessionStateRedisIntegrationFixture.Format("2001:db8::10");
        Assert.Equal(IpA, mapped);
        Assert.Equal(IpA, v4);
        Assert.Equal(Ip6, v6);
        Assert.True((await _redis.TryAdmitAsync(account, mapped, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(account, v4, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(account, v6, O1, 10, 4, 1, 3, Now, Lease)).Accepted);
        Assert.Equal(3, await _redis.SessionCountAsync(account));
        Assert.Equal(2, await _redis.DistinctSourceCountAsync(account));
        AssertOwnership(await _redis.ReadSourceAsync(account, IpA, O1), Now + Lease, 1, 2);
        Assert.NotNull(await _redis.ReadSourceAsync(account, Ip6, O1));
        Assert.Null(await _redis.ReadSourceAsync(account, "::ffff:192.0.2.10", O1));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task RenewAndApply_FloorsBytesEvenWhenRenewIsLost_AndReplayIsIdempotent()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(Now);
        var ttl = TimeSpan.FromMilliseconds(Lease);
        var applied = await _redis.Store.RenewAndApplyAsync(
            account,
            O1,
            1,
            [(IpA, 1)],
            now,
            ttl,
            "live-batch-1",
            consumed: 25,
            mysqlRemainingAfter: 50_000);
        Assert.Equal(SessionStateRenewStatus.Renewed, applied.Renew);
        Assert.Equal(50_000, applied.Remaining);
        AssertOwnership(await _redis.ReadSessionAsync(account, O1), Now + Lease, 1, 1);

        await _redis.Store.ReleaseOwnerAsync(account, O1);
        var lost = await _redis.Store.RenewAndApplyAsync(
            account,
            O1,
            1,
            [(IpA, 1)],
            now,
            ttl,
            "live-batch-2",
            consumed: 25,
            mysqlRemainingAfter: 800);
        Assert.Equal(SessionStateRenewStatus.Lost, lost.Renew);
        Assert.Equal(800, lost.Remaining);

        var replay = await _redis.Store.RenewAndApplyAsync(
            account,
            O1,
            1,
            [(IpA, 1)],
            now,
            ttl,
            "live-batch-2",
            consumed: 25,
            mysqlRemainingAfter: 800);
        Assert.Equal(SessionStateRenewStatus.Lost, replay.Renew);
        Assert.Equal(800, replay.Remaining);
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Cleanup_RemovesOnlyThisSuiteAccounts()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(account, IpA, O1, 10, 4, 1, 1, Now, Lease)).Accepted);
        await _redis.DeleteKeysAsync(account);
        Assert.Null(await _redis.ReadSessionAsync(account, O1));
        Assert.Null(await _redis.ReadSourceAsync(account, IpA, O1));
    }

    private static void AssertOwnership(Ownership? actual, long expiryUnixMs, long generation, int count)
    {
        Assert.NotNull(actual);
        Assert.Equal(expiryUnixMs, actual.Value.ExpiryUnixMs);
        Assert.Equal(generation, actual.Value.Generation);
        Assert.Equal(count, actual.Value.Count);
    }
}
