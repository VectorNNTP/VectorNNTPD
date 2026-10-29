using System.Reflection;
using System.Text;
using VectorNNTPD.ReaderService.PostFilter.Quota;
using VectorNNTPD.ReaderService.SessionState;

namespace VectorNNTPD.ReaderService.Tests.PostFilter.Quota;

/// <summary>
/// In-memory algorithm contracts for accept-quota. Does not require live Redis.
/// </summary>
public sealed class PostFilterQuotaEngineTests
{
    private const long Now = 5_000;
    private const long Ttl = 10_000;
    private const string BodyA = "0123456789abcdef";
    private const string BodyB = "fedcba9876543210";

    private static readonly PostFilterQuotaWindows Windows = new(10_000, 1_000);

    [Fact]
    public void AccountHash_MatchesSessionAdmissionConvention()
    {
        const string account = "alice";
        var session = Encoding.ASCII.GetString(SessionStateKeys.CreateSession(account));
        var quota = Encoding.ASCII.GetString(PostFilterQuotaKeys.CreateQuota(account));
        var multipost = Encoding.ASCII.GetString(PostFilterQuotaKeys.CreateMultipost(account));
        Assert.StartsWith("nntpd:sess:", session, StringComparison.Ordinal);
        Assert.StartsWith("nntpd:pf:q:", quota, StringComparison.Ordinal);
        Assert.StartsWith("nntpd:pf:m:", multipost, StringComparison.Ordinal);
        Assert.Equal(session["nntpd:sess:".Length..], quota["nntpd:pf:q:".Length..]);
        Assert.Equal(quota["nntpd:pf:q:".Length..], multipost["nntpd:pf:m:".Length..]);
    }

    [Fact]
    public void Reserve_OneAccepted()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id));
        Assert.True(engine.HasLiveReservation(Q("acct"), id, Now));
    }

    [Fact]
    public void Reserve_DeniedAtExactMessageCeiling()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Messages(maxL: 1);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", Id("n1", "a", 1), ceilings: ceilings));
        Assert.Equal(
            PostFilterQuotaReserveCodes.DeniedMessagesLong,
            Reserve(engine, "acct", Id("n1", "a", 2), ceilings: ceilings));
        Assert.Equal(1, engine.ReservationFieldCount(Q("acct")));
    }

    [Fact]
    public async Task Reserve_TwoConcurrentRaceForLastSlot()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Messages(maxL: 1);
        var accepted = 0;
        await Task.WhenAll(
            Task.Run(() => Try(engine, "acct", Id("n1", "a", 1), ceilings, ref accepted)),
            Task.Run(() => Try(engine, "acct", Id("n1", "a", 2), ceilings, ref accepted)));
        Assert.Equal(1, accepted);
        Assert.Equal(1, engine.ReservationFieldCount(Q("acct")));
    }

    [Fact]
    public async Task Reserve_RaceAcrossNodeIds()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Messages(maxL: 1);
        var accepted = 0;
        await Task.WhenAll(
            Task.Run(() => Try(engine, "acct", Id("n1", "a", 1), ceilings, ref accepted)),
            Task.Run(() => Try(engine, "acct", Id("n2", "a", 1), ceilings, ref accepted)));
        Assert.Equal(1, accepted);
    }

    [Fact]
    public async Task Reserve_RaceAcrossIncarnations()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Messages(maxL: 1);
        var accepted = 0;
        await Task.WhenAll(
            Task.Run(() => Try(engine, "acct", Id("n1", "inc1", 1), ceilings, ref accepted)),
            Task.Run(() => Try(engine, "acct", Id("n1", "inc2", 1), ceilings, ref accepted)));
        Assert.Equal(1, accepted);
    }

    [Fact]
    public void Reserve_MultipleConcurrentFromOneProcess()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Messages(maxL: 3);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", Id("n1", "a", 1), ceilings: ceilings));
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", Id("n1", "a", 2), ceilings: ceilings));
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", Id("n1", "a", 3), ceilings: ceilings));
        Assert.Equal(
            PostFilterQuotaReserveCodes.DeniedMessagesLong,
            Reserve(engine, "acct", Id("n1", "a", 4), ceilings: ceilings));
        Assert.Equal(3, engine.ReservationFieldCount(Q("acct")));
    }

    [Fact]
    public void Reserve_MessageCeiling()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Messages(maxL: 2);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", Id("n1", "a", 1), ceilings: ceilings));
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", Id("n1", "a", 2), ceilings: ceilings));
        Assert.Equal(
            PostFilterQuotaReserveCodes.DeniedMessagesLong,
            Reserve(engine, "acct", Id("n1", "a", 3), ceilings: ceilings));
    }

    [Fact]
    public void Reserve_ByteCeiling()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Bytes(maxL: 100);
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", Id("n1", "a", 1), ceilings: ceilings, bytes: 60));
        Assert.Equal(
            PostFilterQuotaReserveCodes.DeniedBytesLong,
            Reserve(engine, "acct", Id("n1", "a", 2), ceilings: ceilings, bytes: 50));
    }

    [Fact]
    public void Reserve_MultipostCeiling()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Identical(maxL: 1);
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", Id("n1", "a", 1), ceilings: ceilings, mpUnits: 1, bodyHex: BodyA));
        Assert.Equal(
            PostFilterQuotaReserveCodes.DeniedIdenticalLong,
            Reserve(engine, "acct", Id("n1", "a", 2), ceilings: ceilings, mpUnits: 1, bodyHex: BodyA));
    }

    [Fact]
    public void Reserve_LongAndShortSimultaneously()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = new PostFilterQuotaCeilings(10, 0, 0, 1, 0, 0);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", Id("n1", "a", 1), ceilings: ceilings));
        Assert.Equal(
            PostFilterQuotaReserveCodes.DeniedMessagesShort,
            Reserve(engine, "acct", Id("n1", "a", 2), ceilings: ceilings));
    }

    [Fact]
    public void Reserve_DisabledDimensionsAreSkipped()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = new PostFilterQuotaCeilings(0, 0, 0, 0, 0, 0);
        for (var i = 1; i <= 20; i++)
        {
            Assert.Equal(
                PostFilterQuotaReserveCodes.Accept,
                Reserve(engine, "acct", Id("n1", "a", i), ceilings: ceilings));
        }
    }

    [Fact]
    public void Reserve_DenialOrder_IsDeterministic()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = new PostFilterQuotaCeilings(1, 10, 1, 1, 10, 1);
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", Id("n1", "a", 1), ceilings: ceilings, bytes: 10, mpUnits: 1, bodyHex: BodyA));
        Assert.Equal(
            PostFilterQuotaReserveCodes.DeniedMessagesLong,
            Reserve(engine, "acct", Id("n1", "a", 2), ceilings: ceilings, bytes: 10, mpUnits: 1, bodyHex: BodyA));
    }

    [Fact]
    public void Reserve_IdempotentSameToken()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id));
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id));
        Assert.Equal(1, engine.ReservationFieldCount(Q("acct")));
    }

    [Fact]
    public void Reserve_ConflictDifferentUnits()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id, bytes: 10));
        Assert.Equal(PostFilterQuotaReserveCodes.Conflict, Reserve(engine, "acct", id, bytes: 20));
        Assert.Equal(1, engine.ReservationFieldCount(Q("acct")));
    }

    [Fact]
    public void Reserve_ConflictDifferentBodyHash()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        var ceilings = Identical(maxL: 5);
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", id, ceilings: ceilings, mpUnits: 1, bodyHex: BodyA));
        Assert.Equal(
            PostFilterQuotaReserveCodes.Conflict,
            Reserve(engine, "acct", id, ceilings: ceilings, mpUnits: 1, bodyHex: BodyB));
    }

    [Fact]
    public void Release_SuccessAndDuplicate()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id));
        Assert.Equal(PostFilterQuotaReleaseCodes.Released, engine.Release(Q("acct"), M("acct"), id, Now));
        Assert.Equal(PostFilterQuotaReleaseCodes.Noop, engine.Release(Q("acct"), M("acct"), id, Now));
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", Id("n1", "a", 2)));
    }

    [Fact]
    public void Release_AfterCommit_IsNoop()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        var ceilings = Messages(maxL: 10);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id, ceilings: ceilings));
        Assert.Equal(PostFilterQuotaCommitCodes.Committed, Commit(engine, "acct", id, ceilings));
        Assert.Equal(PostFilterQuotaReleaseCodes.Noop, engine.Release(Q("acct"), M("acct"), id, Now));
        Assert.Equal(1, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now)));
    }

    [Fact]
    public void Release_AfterExpiry_IsNoop()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id));
        Assert.Equal(PostFilterQuotaReleaseCodes.Noop, engine.Release(Q("acct"), M("acct"), id, Now + Ttl));
        Assert.False(engine.HasReservationField(Q("acct"), id));
    }

    [Fact]
    public void Release_StaleCannotAffectNewerReservation()
    {
        var engine = new PostFilterQuotaEngine();
        var oldId = Id("n1", "a", 1);
        var newId = Id("n1", "a", 2);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", oldId));
        Assert.Equal(PostFilterQuotaReleaseCodes.Released, engine.Release(Q("acct"), M("acct"), oldId, Now));
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", newId));
        Assert.Equal(PostFilterQuotaReleaseCodes.Noop, engine.Release(Q("acct"), M("acct"), oldId, Now));
        Assert.True(engine.HasLiveReservation(Q("acct"), newId, Now));
    }

    [Fact]
    public void Commit_Success()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        var ceilings = new PostFilterQuotaCeilings(10, 10_000, 0, 10, 10_000, 0);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id, ceilings: ceilings, bytes: 40));
        Assert.Equal(PostFilterQuotaCommitCodes.Committed, Commit(engine, "acct", id, ceilings));
        Assert.False(engine.HasReservationField(Q("acct"), id));
        Assert.Equal(1, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now)));
        Assert.Equal(40, engine.CommittedBytes(Q("acct"), 'L', Windows.BucketId('L', Now)));
    }

    [Fact]
    public void Commit_Duplicate_DoesNotDoubleCount()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        var ceilings = Messages(maxL: 10);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id, ceilings: ceilings));
        Assert.Equal(PostFilterQuotaCommitCodes.Committed, Commit(engine, "acct", id, ceilings));
        Assert.Equal(PostFilterQuotaCommitCodes.Noop, Commit(engine, "acct", id, ceilings));
        Assert.Equal(1, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now)));
    }

    [Fact]
    public void Commit_AfterRelease_IsNoop()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        var ceilings = Messages(maxL: 10);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id, ceilings: ceilings));
        Assert.Equal(PostFilterQuotaReleaseCodes.Released, engine.Release(Q("acct"), M("acct"), id, Now));
        Assert.Equal(PostFilterQuotaCommitCodes.Noop, Commit(engine, "acct", id, ceilings));
        Assert.Equal(0, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now)));
    }

    [Fact]
    public void Commit_AfterExpiry_IsNoop()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        var ceilings = Messages(maxL: 10);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id, ceilings: ceilings));
        Assert.Equal(PostFilterQuotaCommitCodes.Noop, Commit(engine, "acct", id, ceilings, Now + Ttl));
        Assert.Equal(0, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now + Ttl)));
    }

    [Fact]
    public void DerivedHold_StaysLiveThroughSaOperationTimeout_ThenExpires()
    {
        var hold = PostFilterQuotaDefaults.HoldMilliseconds(true, TimeSpan.FromSeconds(30));
        Assert.Equal(31_000, hold);
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        var ceilings = Messages(maxL: 1);
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            engine.Reserve(Q("acct"), M("acct"), id, Now, hold, Windows, ceilings, 1, 1, 0, string.Empty));
        Assert.Equal(
            PostFilterQuotaCommitCodes.Committed,
            engine.Commit(Q("acct"), M("acct"), id, Now + 30_000, Windows, ceilings));
        Assert.Equal(1, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now + 30_000)));
    }

    [Fact]
    public void ExpiredReservation_DoesNotConsumeQuotaForLaterPost()
    {
        var engine = new PostFilterQuotaEngine();
        var first = Id("n1", "a", 1);
        var second = Id("n1", "a", 2);
        var ceilings = Messages(maxL: 1);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", first, ceilings: ceilings));
        Assert.Equal(PostFilterQuotaCommitCodes.Noop, Commit(engine, "acct", first, ceilings, Now + Ttl));
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", second, nowMs: Now + Ttl, ceilings: ceilings));
        Assert.Equal(PostFilterQuotaCommitCodes.Committed, Commit(engine, "acct", second, ceilings, Now + Ttl));
        Assert.Equal(1, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now + Ttl)));
    }

    [Fact]
    public void Commit_StaleGeneration_IsNoop()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        var ceilings = Messages(maxL: 10);
        engine.SeedReservation(Q("acct"), id, Now + Ttl, storedGeneration: 99, 1, 10, 0, string.Empty);
        Assert.Equal(PostFilterQuotaCommitCodes.Noop, Commit(engine, "acct", id, ceilings));
        Assert.True(engine.HasReservationField(Q("acct"), id));
        Assert.Equal(0, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now)));
    }

    [Fact]
    public void Commit_DoesNotAcceptCallerSuppliedUnits()
    {
        var method = typeof(IPostFilterQuotaStore).GetMethod(nameof(IPostFilterQuotaStore.CommitAsync));
        Assert.NotNull(method);
        foreach (var parameter in method.GetParameters())
        {
            Assert.False(
                parameter.Name is "messages" or "bytes" or "mpUnits" or "bodyHex",
                parameter.Name);
        }

        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        var ceilings = new PostFilterQuotaCeilings(10, 10_000, 0, 10, 10_000, 0);
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", id, ceilings: ceilings, bytes: 7));
        Assert.Equal(PostFilterQuotaCommitCodes.Committed, Commit(engine, "acct", id, ceilings));
        Assert.Equal(7, engine.CommittedBytes(Q("acct"), 'L', Windows.BucketId('L', Now)));
    }

    [Fact]
    public void BucketBoundary_ReservationRemainsEffective_AndCommitChargesCurrent()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Messages(maxL: 10);
        var id = Id("n1", "a", 1);
        engine.SeedCommitted(Q("acct"), PostFilterQuotaKeys.CommittedMessagesField('L', 0), 9);
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", id, nowMs: 9_999, ceilings: ceilings));
        Assert.Equal(
            PostFilterQuotaReserveCodes.DeniedMessagesLong,
            Reserve(engine, "acct", Id("n1", "a", 2), nowMs: 9_999, ceilings: ceilings));

        var after = 10_002;
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", Id("n2", "b", 1), nowMs: after, ceilings: ceilings));
        var accepted = 1;
        for (var g = 2; g <= 12; g++)
        {
            if (Reserve(engine, "acct", Id("n2", "b", g), nowMs: after, ceilings: ceilings)
                == PostFilterQuotaReserveCodes.Accept)
            {
                accepted++;
            }
        }

        Assert.Equal(9, accepted);
        Assert.Equal(PostFilterQuotaCommitCodes.Committed, Commit(engine, "acct", id, ceilings, after));
        Assert.Equal(0, engine.CommittedMessages(Q("acct"), 'L', 0));
        Assert.Equal(1, engine.CommittedMessages(Q("acct"), 'L', 1));
    }

    [Fact]
    public void Multipost_DifferentHashesIndependent()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Identical(maxL: 1);
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", Id("n1", "a", 1), ceilings: ceilings, mpUnits: 1, bodyHex: BodyA));
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", Id("n1", "a", 2), ceilings: ceilings, mpUnits: 1, bodyHex: BodyB));
    }

    [Fact]
    public async Task Multipost_SameHashRacesAcrossNodes()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Identical(maxL: 1);
        var accepted = 0;
        await Task.WhenAll(
            Task.Run(() => TryMp(engine, "acct", Id("n1", "a", 1), ceilings, BodyA, ref accepted)),
            Task.Run(() => TryMp(engine, "acct", Id("n2", "b", 1), ceilings, BodyA, ref accepted)));
        Assert.Equal(1, accepted);
    }

    [Fact]
    public void ExpiredReservations_ArePruned()
    {
        var engine = new PostFilterQuotaEngine();
        var first = Id("n1", "a", 1);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", first));
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", Id("n1", "a", 2), nowMs: Now + Ttl));
        Assert.False(engine.HasReservationField(Q("acct"), first));
        Assert.Equal(1, engine.ReservationFieldCount(Q("acct")));
    }

    [Fact]
    public void MultipleExpiredReservations_ArePruned()
    {
        var engine = new PostFilterQuotaEngine();
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", Id("n1", "a", 1)));
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", Id("n1", "a", 2)));
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", Id("n1", "a", 3), nowMs: Now + Ttl));
        Assert.Equal(1, engine.ReservationFieldCount(Q("acct")));
    }

    [Fact]
    public void CrashBeforeCommit_ExpiresThenUnderCounts()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        var ceilings = Messages(maxL: 10);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id, ceilings: ceilings));
        Assert.Equal(PostFilterQuotaCommitCodes.Noop, Commit(engine, "acct", id, ceilings, Now + Ttl));
        Assert.Equal(0, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now + Ttl)));
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", Id("n1", "a", 2), nowMs: Now + Ttl, ceilings: ceilings));
    }

    [Fact]
    public void CrashBeforeRelease_ExpiresAndRecovers()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Messages(maxL: 1);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", Id("n1", "a", 1), ceilings: ceilings));
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", Id("n1", "a", 2), nowMs: Now + Ttl, ceilings: ceilings));
    }

    [Fact]
    public void RepeatedWindows_RemainBounded()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Messages(maxL: 5);
        for (var window = 0; window < 8; window++)
        {
            var now = (window * Windows.LongWindowMs) + 100;
            var id = Id("n1", "a", window + 1);
            Assert.Equal(
                PostFilterQuotaReserveCodes.Accept,
                Reserve(engine, "acct", id, nowMs: now, ceilings: ceilings));
            Assert.Equal(PostFilterQuotaCommitCodes.Committed, Commit(engine, "acct", id, ceilings, now));
        }

        Assert.Equal(1, engine.CommittedMessages(Q("acct"), 'L', 7));
        Assert.Equal(0, engine.CommittedMessages(Q("acct"), 'L', 0));
        Assert.True(engine.QuotaFieldCount(Q("acct")) <= 4);
    }

    [Fact]
    public void WindowExpiry_DropsEndedCommittedUsage()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Messages(maxL: 1);
        var id = Id("n1", "a", 1);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id, ceilings: ceilings));
        Assert.Equal(PostFilterQuotaCommitCodes.Committed, Commit(engine, "acct", id, ceilings));
        Assert.Equal(
            PostFilterQuotaReserveCodes.DeniedMessagesLong,
            Reserve(engine, "acct", Id("n1", "a", 2), ceilings: ceilings));
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", Id("n1", "a", 3), nowMs: 10_000, ceilings: ceilings));
    }

    [Fact]
    public void ConcurrentReserveAndCommit()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Messages(maxL: 10);
        var id = Id("n1", "a", 1);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id, ceilings: ceilings));
        Parallel.Invoke(
            () => Assert.Equal(PostFilterQuotaCommitCodes.Committed, Commit(engine, "acct", id, ceilings)),
            () => Reserve(engine, "acct", Id("n1", "a", 2), ceilings: ceilings));
        var committed = engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now));
        var reserved = engine.ReservationFieldCount(Q("acct"));
        Assert.Equal(1, committed);
        Assert.True(reserved is 0 or 1);
        Assert.True(committed + reserved <= 2);
    }

    [Fact]
    public void ConcurrentReserveAndRelease()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", id));
        Parallel.Invoke(
            () => engine.Release(Q("acct"), M("acct"), id, Now),
            () => Reserve(engine, "acct", Id("n1", "a", 2)));
        Assert.True(engine.ReservationFieldCount(Q("acct")) <= 1);
    }

    [Fact]
    public void ConcurrentStaleReleaseAndNewReserve()
    {
        var engine = new PostFilterQuotaEngine();
        var stale = Id("n1", "old", 1);
        var fresh = Id("n1", "new", 1);
        Assert.Equal(PostFilterQuotaReserveCodes.Accept, Reserve(engine, "acct", stale));
        Assert.Equal(PostFilterQuotaReleaseCodes.Released, engine.Release(Q("acct"), M("acct"), stale, Now));
        Parallel.Invoke(
            () => engine.Release(Q("acct"), M("acct"), stale, Now),
            () => Reserve(engine, "acct", fresh));
        Assert.True(engine.HasLiveReservation(Q("acct"), fresh, Now));
    }

    [Fact]
    public void AccountsAreIsolated()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Messages(maxL: 1);
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "alice", Id("n1", "a", 1), ceilings: ceilings));
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "bob", Id("n1", "a", 1), ceilings: ceilings));
        Assert.Equal(
            PostFilterQuotaReserveCodes.DeniedMessagesLong,
            Reserve(engine, "alice", Id("n1", "a", 2), ceilings: ceilings));
    }

    [Fact]
    public void SameAccount_VisibleAcrossNodeIdentities()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Messages(maxL: 1);
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", Id("n1", "a", 1), ceilings: ceilings));
        Assert.Equal(
            PostFilterQuotaReserveCodes.DeniedMessagesLong,
            Reserve(engine, "acct", Id("n2", "b", 1), ceilings: ceilings));
    }

    [Fact]
    public async Task Reserve_TwoConcurrentRaceForLastByteSlot()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = Bytes(maxL: 40);
        var accepted = 0;
        await Task.WhenAll(
            Task.Run(() => Try(engine, "acct", Id("n1", "a", 1), ceilings, ref accepted, bytes: 40)),
            Task.Run(() => Try(engine, "acct", Id("n2", "b", 1), ceilings, ref accepted, bytes: 40)));
        Assert.Equal(1, accepted);
    }

    [Fact]
    public void Commit_DisabledCeilingsWriteNoCommittedFields()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        var reserveCeilings = new PostFilterQuotaCeilings(10, 10_000, 2, 10, 10_000, 2);
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", id, ceilings: reserveCeilings, bytes: 40, mpUnits: 1, bodyHex: BodyA));
        Assert.Equal(
            PostFilterQuotaCommitCodes.Committed,
            Commit(engine, "acct", id, new PostFilterQuotaCeilings(0, 0, 0, 0, 0, 0)));
        Assert.False(engine.HasReservationField(Q("acct"), id));
        Assert.Equal(0, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now)));
        Assert.Equal(0, engine.CommittedBytes(Q("acct"), 'L', Windows.BucketId('L', Now)));
        Assert.Equal(0, engine.CommittedIdentical(M("acct"), BodyA, 'L', Windows.BucketId('L', Now)));
        Assert.Equal(0, engine.QuotaFieldCount(Q("acct")));
        Assert.Equal(0, engine.MultipostFieldCount(M("acct")));
    }

    [Fact]
    public void Commit_CeilingMagnitudeDoesNotChangeReservedUnits()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(
                engine,
                "acct",
                id,
                ceilings: new PostFilterQuotaCeilings(10, 10_000, 0, 10, 10_000, 0),
                messages: 2,
                bytes: 40));
        Assert.Equal(
            PostFilterQuotaCommitCodes.Committed,
            Commit(engine, "acct", id, new PostFilterQuotaCeilings(1, 1, 0, 1, 1, 0)));
        Assert.Equal(2, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now)));
        Assert.Equal(40, engine.CommittedBytes(Q("acct"), 'L', Windows.BucketId('L', Now)));
        Assert.Equal(2, engine.CommittedMessages(Q("acct"), 'S', Windows.BucketId('S', Now)));
        Assert.Equal(40, engine.CommittedBytes(Q("acct"), 'S', Windows.BucketId('S', Now)));
    }

    [Fact]
    public void Commit_MissingReservation_CannotInventUsageViaCeilings()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        Assert.Equal(
            PostFilterQuotaCommitCodes.Noop,
            Commit(engine, "acct", id, new PostFilterQuotaCeilings(999, 999_999, 999, 999, 999_999, 999)));
        Assert.Equal(0, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now)));
        Assert.Equal(0, engine.CommittedBytes(Q("acct"), 'L', Windows.BucketId('L', Now)));
        Assert.Equal(0, engine.QuotaFieldCount(Q("acct")));
    }

    [Fact]
    public void Commit_ReservationBodyWithSeparator_StillReadsUnits()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        const string body = "ab|cd|ef";
        engine.SeedReservation(Q("acct"), id, Now + Ttl, 1, 1, 9, 1, body);
        Assert.Equal(
            PostFilterQuotaCommitCodes.Committed,
            Commit(engine, "acct", id, new PostFilterQuotaCeilings(10, 10_000, 2, 0, 0, 0)));
        Assert.Equal(1, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now)));
        Assert.Equal(9, engine.CommittedBytes(Q("acct"), 'L', Windows.BucketId('L', Now)));
        Assert.Equal(1, engine.CommittedIdentical(M("acct"), body, 'L', Windows.BucketId('L', Now)));
    }

    [Fact]
    public void Commit_EmptyBodyMultipost_DoesNotWriteIdenticalField()
    {
        var engine = new PostFilterQuotaEngine();
        var id = Id("n1", "a", 1);
        engine.SeedReservation(Q("acct"), id, Now + Ttl, 1, 1, 1, 1, string.Empty);
        Assert.Equal(
            PostFilterQuotaCommitCodes.Committed,
            Commit(engine, "acct", id, new PostFilterQuotaCeilings(10, 0, 2, 0, 0, 0)));
        Assert.Equal(0, engine.MultipostFieldCount(M("acct")));
        Assert.Equal(1, engine.CommittedMessages(Q("acct"), 'L', Windows.BucketId('L', Now)));
    }

    [Fact]
    public void IdleKeyTtl_OutlivesCurrentBucketsAndReservation()
    {
        var reservationTtl = (long)PostFilterQuotaDefaults.ReservationTtl.TotalMilliseconds;
        (long LongMs, long ShortMs, long NowMs)[] cases =
        [
            (10_000, 1_000, 0),
            (10_000, 1_000, 5_000),
            (10_000, 1_000, 9_999),
            (86_400_000, 600_000, 0),
            (86_400_000, 600_000, 86_399_999),
            (1, 1, 0),
            (1_000, 10_000, 7_500),
        ];

        foreach (var (longMs, shortMs, nowMs) in cases)
        {
            var windows = new PostFilterQuotaWindows(longMs, shortMs);
            var idle = Math.Max(longMs, shortMs) + reservationTtl + PostFilterQuotaDefaults.IdleTtlSkewMs;
            var keyExpiry = nowMs + idle;
            foreach (var window in new[] { PostFilterQuotaKeys.LongWindow, PostFilterQuotaKeys.ShortWindow })
            {
                var bucketEnd = (windows.BucketId(window, nowMs) + 1) * windows.WindowMs(window);
                Assert.True(
                    keyExpiry >= bucketEnd + reservationTtl,
                    $"window {window} L={longMs} S={shortMs} now={nowMs}");
            }

            Assert.True(keyExpiry > nowMs + reservationTtl);
        }
    }

    [Fact]
    public void Commit_WritesIdenticalOnlyWhenEnabled()
    {
        var engine = new PostFilterQuotaEngine();
        var ceilings = new PostFilterQuotaCeilings(10, 0, 2, 10, 0, 0);
        var id = Id("n1", "a", 1);
        Assert.Equal(
            PostFilterQuotaReserveCodes.Accept,
            Reserve(engine, "acct", id, ceilings: ceilings, mpUnits: 1, bodyHex: BodyA));
        Assert.Equal(PostFilterQuotaCommitCodes.Committed, Commit(engine, "acct", id, ceilings));
        Assert.Equal(1, engine.CommittedIdentical(M("acct"), BodyA, 'L', Windows.BucketId('L', Now)));
        Assert.Equal(0, engine.CommittedIdentical(M("acct"), BodyA, 'S', Windows.BucketId('S', Now)));
    }

    [Fact]
    public async Task InMemoryStore_MapsCodesAndUnavailable()
    {
        var store = new InMemoryPostFilterQuotaStore();
        var id = Id("n1", "a", 1);
        var ceilings = Messages(maxL: 1);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(Now);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await store.ReserveAsync("acct", id, now, Windows, ceilings, 1, 1, 0, null));
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await store.CommitAsync("acct", id, now, Windows, ceilings));
        store.Unavailable = true;
        Assert.Equal(
            PostFilterQuotaReserveStatus.Unavailable,
            await store.ReserveAsync("acct", Id("n1", "a", 2), now, Windows, ceilings, 1, 1, 0, null));
    }

    private static PostFilterReservationId Id(string node, string incarnation, long generation) =>
        new(node, incarnation, generation);

    private static string Q(string account) => InMemoryPostFilterQuotaStore.QuotaKey(account);

    private static string M(string account) => InMemoryPostFilterQuotaStore.MultipostKey(account);

    private static PostFilterQuotaCeilings Messages(long maxL, long maxS = 0) =>
        new(maxL, 0, 0, maxS, 0, 0);

    private static PostFilterQuotaCeilings Bytes(long maxL) => new(0, maxL, 0, 0, 0, 0);

    private static PostFilterQuotaCeilings Identical(long maxL) => new(0, 0, maxL, 0, 0, 0);

    private static long Reserve(
        PostFilterQuotaEngine engine,
        string account,
        PostFilterReservationId id,
        long nowMs = Now,
        PostFilterQuotaCeilings? ceilings = null,
        long messages = 1,
        long bytes = 1,
        int mpUnits = 0,
        string bodyHex = "") =>
        engine.Reserve(
            Q(account),
            M(account),
            id,
            nowMs,
            Ttl,
            Windows,
            ceilings ?? Messages(10),
            messages,
            bytes,
            mpUnits,
            bodyHex);

    private static long Commit(
        PostFilterQuotaEngine engine,
        string account,
        PostFilterReservationId id,
        PostFilterQuotaCeilings ceilings,
        long nowMs = Now) =>
        engine.Commit(Q(account), M(account), id, nowMs, Windows, ceilings);

    private static void Try(
        PostFilterQuotaEngine engine,
        string account,
        PostFilterReservationId id,
        PostFilterQuotaCeilings ceilings,
        ref int accepted,
        long bytes = 1)
    {
        if (Reserve(engine, account, id, ceilings: ceilings, bytes: bytes) == PostFilterQuotaReserveCodes.Accept)
        {
            Interlocked.Increment(ref accepted);
        }
    }

    private static void TryMp(
        PostFilterQuotaEngine engine,
        string account,
        PostFilterReservationId id,
        PostFilterQuotaCeilings ceilings,
        string body,
        ref int accepted)
    {
        if (Reserve(engine, account, id, ceilings: ceilings, mpUnits: 1, bodyHex: body)
            == PostFilterQuotaReserveCodes.Accept)
        {
            Interlocked.Increment(ref accepted);
        }
    }
}
