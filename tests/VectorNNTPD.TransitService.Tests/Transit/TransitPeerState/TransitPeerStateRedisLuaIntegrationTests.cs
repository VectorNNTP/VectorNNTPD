using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Transit;

/// <summary>
/// Live EVAL of <see cref="TransitPeerStateScripts"/> through
/// <see cref="RedisTransitPeerStateStore"/> against the dedicated test Redis.
/// </summary>
public sealed class TransitPeerStateRedisLuaIntegrationTests : IClassFixture<TransitPeerStateRedisIntegrationFixture>
{
    private const string O1 = "nntpd01:a";
    private const string O2 = "nntpd02:b";
    private const long Now = 10_000;
    private const long Lease = 30_000;
    private readonly TransitPeerStateRedisIntegrationFixture _redis;

    public TransitPeerStateRedisLuaIntegrationTests(TransitPeerStateRedisIntegrationFixture redis)
    {
        _redis = redis;
    }

    [TransitPeerStateRedisIntegrationFact]
    public void ConnectsToAuthorizedTestRedis()
    {
        Assert.True(_redis.IsConfigured);
        Assert.Equal(TransitPeerStateRedisIntegration.AuthorizedEndpoint, _redis.Endpoint);
        Assert.False(string.IsNullOrWhiteSpace(_redis.ServerVersion));
    }

    [TransitPeerStateRedisIntegrationFact]
    public async Task TryAdmit_WritesHashedOwnership_WithoutNativeTtl()
    {
        var peer = await _redis.CreateIdentifierAsync();
        var engine = new TransitPeerStateEngine();
        Assert.Equal(TransitPeerStateEngine.Accepted, engine.TryAdmit("k", O1, 4, Now, Lease, 1));
        Assert.True((await _redis.TryAdmitAsync(peer, O1, 4, 1, Now, Lease)).Accepted);
        AssertOwnership(await _redis.ReadAsync(peer, O1), Now + Lease, 1, 1);
        Assert.Null(await _redis.KeyTimeToLiveAsync(peer));
        await _redis.DeleteKeyAsync(peer);
    }

    [TransitPeerStateRedisIntegrationFact]
    public async Task ConcurrentAdmissions_EnforceExactClusterLimit()
    {
        var peer = await _redis.CreateIdentifierAsync();
        var admitted = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            if ((await _redis.TryAdmitAsync(peer, "owner:" + i, 5, 1, Now, Lease)).Accepted)
            {
                Interlocked.Increment(ref admitted);
            }
        }));
        Assert.Equal(5, admitted);
        Assert.Equal(5, await _redis.CountAsync(peer));
        await _redis.DeleteKeyAsync(peer);
    }

    [TransitPeerStateRedisIntegrationFact]
    public async Task SamePeerAcrossOwners_SharesLimit_DifferentPeersAreIsolated()
    {
        var peer = await _redis.CreateIdentifierAsync();
        var other = await _redis.CreateIdentifierAsync();
        Assert.True((await _redis.TryAdmitAsync(peer, O1, 1, 1, Now, Lease)).Accepted);
        Assert.False((await _redis.TryAdmitAsync(peer, O2, 1, 1, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(other, O2, 1, 1, Now, Lease)).Accepted);
        Assert.Equal(1, await _redis.CountAsync(peer));
        Assert.Equal(1, await _redis.CountAsync(other));
        await _redis.DeleteKeyAsync(peer);
        await _redis.DeleteKeyAsync(other);
    }

    [TransitPeerStateRedisIntegrationFact]
    public async Task GenerationReplacement_ReplacesCountAndIgnoresStaleRelease()
    {
        var peer = await _redis.CreateIdentifierAsync();
        await _redis.SeedAsync(peer, O1, 30_000, 1, 7);
        var engine = new TransitPeerStateEngine();
        engine.WriteOwnership("k", O1, 30_000, 1, 7);
        Assert.Equal(TransitPeerStateEngine.Accepted, engine.TryAdmit("k", O1, 20, 0, Lease, 2));
        Assert.True((await _redis.TryAdmitAsync(peer, O1, 20, 2, 0, Lease)).Accepted);
        AssertOwnership(await _redis.ReadAsync(peer, O1), Lease, 2, 1);

        await _redis.ReleaseAsync(peer, O1, 1);
        AssertOwnership(await _redis.ReadAsync(peer, O1), Lease, 2, 1);

        await _redis.ReleaseAsync(peer, O1, 2);
        Assert.Null(await _redis.ReadAsync(peer, O1));
        await _redis.DeleteKeyAsync(peer);
    }

    [TransitPeerStateRedisIntegrationFact]
    public async Task StaleRenewal_DoesNotRecreateOrExtend()
    {
        var peer = await _redis.CreateIdentifierAsync();
        Assert.Equal(TransitPeerStateRenewStatus.Lost, await _redis.RenewAsync(peer, O1, 1, Now, Lease));
        Assert.Null(await _redis.ReadAsync(peer, O1));

        Assert.True((await _redis.TryAdmitAsync(peer, O1, 4, 2, Now, Lease)).Accepted);
        Assert.Equal(TransitPeerStateRenewStatus.Lost, await _redis.RenewAsync(peer, O1, 1, Now, Lease));
        AssertOwnership(await _redis.ReadAsync(peer, O1), Now + Lease, 2, 1);
        Assert.Equal(TransitPeerStateRenewStatus.Renewed, await _redis.RenewAsync(peer, O1, 2, Now + 5_000, Lease));
        AssertOwnership(await _redis.ReadAsync(peer, O1), Now + 5_000 + Lease, 2, 1);
        await _redis.DeleteKeyAsync(peer);
    }

    [TransitPeerStateRedisIntegrationFact]
    public async Task PruneExpired_FreesCapacity()
    {
        var peer = await _redis.CreateIdentifierAsync();
        await _redis.SeedAsync(peer, O2, Now, 1, 4);
        Assert.True((await _redis.TryAdmitAsync(peer, O1, 1, 1, Now, Lease)).Accepted);
        Assert.Null(await _redis.ReadAsync(peer, O2));
        AssertOwnership(await _redis.ReadAsync(peer, O1), Now + Lease, 1, 1);
        await _redis.DeleteKeyAsync(peer);
    }

    [TransitPeerStateRedisIntegrationFact]
    public async Task MaxZero_RejectsWithoutMutation()
    {
        var peer = await _redis.CreateIdentifierAsync();
        Assert.False((await _redis.TryAdmitAsync(peer, O1, 0, 1, Now, Lease)).Accepted);
        Assert.Null(await _redis.ReadAsync(peer, O1));
        Assert.Equal(0, await _redis.FieldCountAsync(peer));
        await _redis.DeleteKeyAsync(peer);
    }

    [TransitPeerStateRedisIntegrationFact]
    public async Task ReleaseOwner_RemovesOnlyThisOwner()
    {
        var peer = await _redis.CreateIdentifierAsync();
        var other = await _redis.CreateIdentifierAsync();
        Assert.True((await _redis.TryAdmitAsync(peer, O1, 4, 1, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(peer, O2, 4, 1, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(other, O1, 4, 1, Now, Lease)).Accepted);
        await _redis.ReleaseOwnerAsync(peer, O1);
        Assert.Null(await _redis.ReadAsync(peer, O1));
        Assert.NotNull(await _redis.ReadAsync(peer, O2));
        Assert.NotNull(await _redis.ReadAsync(other, O1));
        await _redis.DeleteKeyAsync(peer);
        await _redis.DeleteKeyAsync(other);
    }

    [TransitPeerStateRedisIntegrationFact]
    public async Task Cleanup_DeletesOnlyTrackedKeys()
    {
        var peer = await _redis.CreateIdentifierAsync();
        Assert.True((await _redis.TryAdmitAsync(peer, O1, 4, 1, Now, Lease)).Accepted);
        await _redis.DeleteKeyAsync(peer);
        Assert.Empty(await _redis.RemainingTestKeysAsync());
    }

    private static void AssertOwnership(Ownership? ownership, long expiry, long generation, int count)
    {
        Assert.NotNull(ownership);
        var value = ownership.Value;
        Assert.Equal(expiry, value.ExpiryUnixMs);
        Assert.Equal(generation, value.Generation);
        Assert.Equal(count, value.Count);
    }
}
