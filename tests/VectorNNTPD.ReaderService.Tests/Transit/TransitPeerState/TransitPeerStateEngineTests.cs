using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.Tests.Transit;

public sealed class TransitPeerStateEngineTests
{
    private const string Key = "tconn:peer-a";
    private const string Other = "tconn:peer-b";
    private const string O1 = "nntpd01:a";
    private const string O2 = "nntpd02:b";
    private const long Now = 10_000;
    private const long Lease = 30_000;

    [Fact]
    public void TryAdmit_FirstConnection_WritesCountOne()
    {
        var engine = new TransitPeerStateEngine();
        Assert.Equal(TransitPeerStateEngine.Accepted, engine.TryAdmit(Key, O1, 4, Now, Lease, 1));
        Assert.True(engine.TryGetOwnership(Key, O1, out var expiry, out var generation, out var count));
        Assert.Equal(Now + Lease, expiry);
        Assert.Equal(1, generation);
        Assert.Equal(1, count);
    }

    [Fact]
    public void TryAdmit_BelowLimit_IncrementsSameGeneration()
    {
        var engine = new TransitPeerStateEngine();
        Assert.Equal(TransitPeerStateEngine.Accepted, engine.TryAdmit(Key, O1, 3, Now, Lease, 1));
        Assert.Equal(TransitPeerStateEngine.Accepted, engine.TryAdmit(Key, O1, 3, Now, Lease, 1));
        Assert.True(engine.TryGetOwnership(Key, O1, out _, out var generation, out var count));
        Assert.Equal(1, generation);
        Assert.Equal(2, count);
    }

    [Fact]
    public void TryAdmit_ExactlyAtLimit_ThenOverLimit()
    {
        var engine = new TransitPeerStateEngine();
        Assert.Equal(TransitPeerStateEngine.Accepted, engine.TryAdmit(Key, O1, 2, Now, Lease, 1));
        Assert.Equal(TransitPeerStateEngine.Accepted, engine.TryAdmit(Key, O2, 2, Now, Lease, 1));
        Assert.Equal(TransitPeerStateEngine.Rejected, engine.TryAdmit(Key, O1, 2, Now, Lease, 1));
        Assert.Equal(2, engine.ActiveCount(Key, Now));
        Assert.Equal(1, engine.OwnerCount(Key, O1, Now));
    }

    [Fact]
    public void TryAdmit_MaxZero_RejectsWithoutMutation()
    {
        var engine = new TransitPeerStateEngine();
        Assert.Equal(TransitPeerStateEngine.Rejected, engine.TryAdmit(Key, O1, 0, Now, Lease, 1));
        Assert.Equal(TransitPeerStateEngine.Rejected, engine.TryAdmit(Key, O1, -1, Now, Lease, 1));
        Assert.False(engine.TryGetOwnership(Key, O1, out _, out _, out _));
        Assert.Equal(0, engine.ActiveCount(Key, Now));
    }

    [Fact]
    public void Release_DecrementsThenDeletesFieldAndHash()
    {
        var engine = new TransitPeerStateEngine();
        _ = engine.TryAdmit(Key, O1, 4, Now, Lease, 1);
        _ = engine.TryAdmit(Key, O1, 4, Now, Lease, 1);
        _ = engine.Release(Key, O1, 1);
        Assert.Equal(1, engine.OwnerCount(Key, O1, Now));
        _ = engine.Release(Key, O1, 1);
        Assert.False(engine.TryGetOwnership(Key, O1, out _, out _, out _));
        Assert.Empty(engine.OwnerFields(Key));
    }

    [Fact]
    public void Release_StaleGeneration_IsNoOp()
    {
        var engine = new TransitPeerStateEngine();
        _ = engine.TryAdmit(Key, O1, 4, Now, Lease, 2);
        _ = engine.Release(Key, O1, 1);
        Assert.Equal(1, engine.OwnerCount(Key, O1, Now));
    }

    [Fact]
    public void TryAdmit_NewGeneration_ReplacesStaleCount()
    {
        var engine = new TransitPeerStateEngine();
        engine.WriteOwnership(Key, O1, Now + Lease, 1, 7);
        Assert.Equal(TransitPeerStateEngine.Accepted, engine.TryAdmit(Key, O1, 20, Now, Lease, 2));
        Assert.True(engine.TryGetOwnership(Key, O1, out _, out var generation, out var count));
        Assert.Equal(2, generation);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Renew_ExtendsMatchingUnexpiredOwnership()
    {
        var engine = new TransitPeerStateEngine();
        _ = engine.TryAdmit(Key, O1, 4, Now, Lease, 1);
        Assert.Equal(1, engine.Renew(Key, O1, 1, Now + 5_000, Lease));
        Assert.True(engine.TryGetOwnership(Key, O1, out var expiry, out var generation, out var count));
        Assert.Equal(Now + 5_000 + Lease, expiry);
        Assert.Equal(1, generation);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Renew_MissingExpiredOrStale_DoesNotRecreate()
    {
        var engine = new TransitPeerStateEngine();
        Assert.Equal(0, engine.Renew(Key, O1, 1, Now, Lease));
        Assert.False(engine.TryGetOwnership(Key, O1, out _, out _, out _));

        engine.WriteOwnership(Key, O1, Now, 1, 2);
        Assert.Equal(0, engine.Renew(Key, O1, 1, Now, Lease));
        Assert.True(engine.TryGetOwnership(Key, O1, out var expired, out _, out var count));
        Assert.Equal(Now, expired);
        Assert.Equal(2, count);

        engine.WriteOwnership(Key, O1, Now + Lease, 2, 2);
        Assert.Equal(0, engine.Renew(Key, O1, 1, Now, Lease));
        Assert.True(engine.TryGetOwnership(Key, O1, out var still, out var generation, out _));
        Assert.Equal(Now + Lease, still);
        Assert.Equal(2, generation);
    }

    [Fact]
    public void TryAdmit_PrunesExpiredBeforeLimit()
    {
        var engine = new TransitPeerStateEngine();
        engine.WriteOwnership(Key, O2, Now, 1, 4);
        Assert.Equal(TransitPeerStateEngine.Accepted, engine.TryAdmit(Key, O1, 1, Now, Lease, 1));
        Assert.Equal(0, engine.OwnerCount(Key, O2, Now));
        Assert.Equal(1, engine.OwnerCount(Key, O1, Now));
    }

    [Fact]
    public void DifferentPeers_AreIsolated()
    {
        var engine = new TransitPeerStateEngine();
        Assert.Equal(TransitPeerStateEngine.Accepted, engine.TryAdmit(Key, O1, 1, Now, Lease, 1));
        Assert.Equal(TransitPeerStateEngine.Accepted, engine.TryAdmit(Other, O1, 1, Now, Lease, 1));
        Assert.Equal(TransitPeerStateEngine.Rejected, engine.TryAdmit(Key, O2, 1, Now, Lease, 1));
        Assert.Equal(1, engine.ActiveCount(Key, Now));
        Assert.Equal(1, engine.ActiveCount(Other, Now));
    }

    [Fact]
    public void ReleaseOwner_RemovesOnlyThisOwner()
    {
        var engine = new TransitPeerStateEngine();
        _ = engine.TryAdmit(Key, O1, 4, Now, Lease, 1);
        _ = engine.TryAdmit(Key, O2, 4, Now, Lease, 1);
        _ = engine.ReleaseOwner(Key, O1);
        Assert.Equal(0, engine.OwnerCount(Key, O1, Now));
        Assert.Equal(1, engine.OwnerCount(Key, O2, Now));
        _ = engine.ReleaseOwner(Other, O2);
        Assert.Equal(1, engine.OwnerCount(Key, O2, Now));
    }

    [Fact]
    public void Expiry_FreesCapacity()
    {
        var engine = new TransitPeerStateEngine();
        _ = engine.TryAdmit(Key, O1, 1, Now, Lease, 1);
        Assert.Equal(TransitPeerStateEngine.Rejected, engine.TryAdmit(Key, O2, 1, Now, Lease, 1));
        Assert.Equal(TransitPeerStateEngine.Accepted, engine.TryAdmit(Key, O2, 1, Now + Lease, Lease, 1));
        Assert.Equal(0, engine.OwnerCount(Key, O1, Now + Lease));
        Assert.Equal(1, engine.OwnerCount(Key, O2, Now + Lease));
    }
}
