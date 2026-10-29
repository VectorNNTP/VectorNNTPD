using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.PostFilter.Quota;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

public sealed class PostFilterEvaluatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(5_000);

    [Fact]
    public async Task DisabledGate_AcceptsWithoutReservation()
    {
        var quota = new RecordingQuotaStore();
        var filter = Create(PostFilterPolicySnapshot.Disabled, quota);
        var result = await filter.EvaluateAsync(Request());
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.Null(result.Lease);
        Assert.Empty(quota.Operations);
    }

    [Fact]
    public async Task ClosedGate_RejectsWithoutReservation()
    {
        var quota = new RecordingQuotaStore();
        var filter = Create(Compile(new PostFilterOptions { Gate = PostFilterGateState.Closed }), quota);
        var result = await filter.EvaluateAsync(Request());
        Assert.Equal(PostFilterDecision.Reject, result.Decision);
        Assert.Equal(PostFilterStage.Gate, result.Stage);
        Assert.Empty(quota.Operations);
    }

    [Fact]
    public async Task DeniedAccount_RejectsBeforeQuota()
    {
        var quota = new RecordingQuotaStore();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            DeniedAccounts = ["poster"],
        });
        var result = await Create(snapshot, quota).EvaluateAsync(Request());
        Assert.Equal(PostFilterDecision.Reject, result.Decision);
        Assert.Equal(PostFilterStage.Deny, result.Stage);
        Assert.Empty(quota.Operations);
    }

    [Fact]
    public async Task DeniedAccount_DoesNotMatchDifferentCase()
    {
        var quota = new RecordingQuotaStore();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            DeniedAccounts = ["Poster"],
        });
        var result = await Create(snapshot, quota).EvaluateAsync(Request(account: "poster"));
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.Equal(PostFilterStage.Complete, result.Stage);
    }

    [Fact]
    public async Task DeniedAccount_DoesNotTrimRequestUsername()
    {
        var quota = new RecordingQuotaStore();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            DeniedAccounts = ["poster"],
        });
        var result = await Create(snapshot, quota).EvaluateAsync(Request(account: " poster"));
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.Equal(PostFilterStage.Complete, result.Stage);
    }

    [Fact]
    public async Task AllowlistedAccount_MatchesHashedIdentity()
    {
        var quota = new RecordingQuotaStore();
        var sa = new RecordingSpamAssassin();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            AllowlistedAccounts = ["poster"],
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = new PostFilterSpamAssassinOptions
            {
                Enabled = true,
                OnFailure = PostFilterSpamOnFailure.Reject,
                Hosts = ["127.0.0.1"],
            },
        });
        var result = await Create(snapshot, quota, sa).EvaluateAsync(Request());
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.Equal(0, sa.Calls);
    }

    [Fact]
    public async Task DeniedAccount_DoesNotAffectOtherAccount()
    {
        var quota = new RecordingQuotaStore();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            DeniedAccounts = ["poster"],
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        var result = await Create(snapshot, quota).EvaluateAsync(Request(account: "other"));
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.Equal(PostFilterStage.Complete, result.Stage);
    }

    [Fact]
    public async Task ArtTypePolicy_RejectsYenc()
    {
        var quota = new RecordingQuotaStore();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            RejectArtTypes = ["YEncoded"],
        });
        var article = Article(type: ArticleType.YEncoded);
        var result = await Create(snapshot, quota).EvaluateAsync(Request(article));
        Assert.Equal(PostFilterDecision.Reject, result.Decision);
        Assert.Equal(PostFilterStage.ArtType, result.Stage);
        Assert.Empty(quota.Operations);
    }

    [Fact]
    public async Task Allowlisted_StillReserves_AndSkipsSpamAssassin()
    {
        var quota = new RecordingQuotaStore();
        var sa = new RecordingSpamAssassin();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            AllowlistedAccounts = ["poster"],
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = new PostFilterSpamAssassinOptions
            {
                Enabled = true,
                OnFailure = PostFilterSpamOnFailure.Reject,
                Hosts = ["127.0.0.1"],
            },
        });
        var result = await Create(snapshot, quota, sa).EvaluateAsync(Request());
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.NotNull(result.Lease);
        Assert.Contains("reserve", quota.Operations);
        Assert.Equal(0, sa.Calls);
    }

    [Fact]
    public async Task NonAllowlisted_InvokesSpamAssassin()
    {
        var sa = new RecordingSpamAssassin();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = new PostFilterSpamAssassinOptions
            {
                Enabled = true,
                OnFailure = PostFilterSpamOnFailure.Reject,
                Hosts = ["127.0.0.1"],
            },
        });
        var result = await Create(snapshot, new RecordingQuotaStore(), sa).EvaluateAsync(Request());
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.Equal(1, sa.Calls);
    }

    [Fact]
    public async Task SpamAssassinSpam_ReleasesAndRejects()
    {
        var quota = new RecordingQuotaStore();
        var sa = new RecordingSpamAssassin { Result = PostFilterSpamAssassinResult.Spam("spam") };
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = new PostFilterSpamAssassinOptions
            {
                Enabled = true,
                OnFailure = PostFilterSpamOnFailure.Reject,
                Hosts = ["127.0.0.1"],
            },
        });
        var result = await Create(snapshot, quota, sa).EvaluateAsync(Request());
        Assert.Equal(PostFilterDecision.Reject, result.Decision);
        Assert.Equal(PostFilterStage.SpamAssassin, result.Stage);
        Assert.Null(result.Lease);
        Assert.Equal(new[] { "reserve", "release" }, quota.Operations);
    }

    [Theory]
    [InlineData(PostFilterSpamOnFailure.Reject, PostFilterDecision.Reject)]
    [InlineData(PostFilterSpamOnFailure.Accept, PostFilterDecision.Accept)]
    public async Task SpamAssassinFailure_FollowsConfiguredPolicy(
        PostFilterSpamOnFailure onFailure,
        PostFilterDecision expected)
    {
        var quota = new RecordingQuotaStore();
        var sa = new RecordingSpamAssassin { Result = PostFilterSpamAssassinResult.Failed("down") };
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = new PostFilterSpamAssassinOptions
            {
                Enabled = true,
                OnFailure = onFailure,
                Hosts = ["127.0.0.1"],
            },
        });
        var result = await Create(snapshot, quota, sa).EvaluateAsync(Request());
        Assert.Equal(expected, result.Decision);
        if (expected == PostFilterDecision.Reject)
        {
            Assert.Equal(new[] { "reserve", "release" }, quota.Operations);
            Assert.Null(result.Lease);
        }
        else
        {
            Assert.Equal(new[] { "reserve" }, quota.Operations);
            Assert.NotNull(result.Lease);
        }
    }

    [Fact]
    public async Task QuotaMessageDenial_Is441PathReject()
    {
        var quota = new RecordingQuotaStore();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
        });
        var filter = Create(snapshot, quota);
        Assert.Equal(PostFilterDecision.Accept, (await filter.EvaluateAsync(Request(account: "poster"))).Decision);
        var denied = await filter.EvaluateAsync(Request(account: "poster"));
        Assert.Equal(PostFilterDecision.Reject, denied.Decision);
        Assert.Equal(PostFilterQuotaReserveStatus.DeniedMessagesLong, denied.QuotaStatus);
    }

    [Fact]
    public async Task QuotaIdenticalDenial_Is441PathReject()
    {
        var quota = new RecordingQuotaStore();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxIdenticalLong = 1 },
        });
        var filter = Create(snapshot, quota);
        Assert.Equal(PostFilterDecision.Accept, (await filter.EvaluateAsync(Request())).Decision);
        var denied = await filter.EvaluateAsync(Request());
        Assert.Equal(PostFilterDecision.Reject, denied.Decision);
        Assert.Equal(PostFilterQuotaReserveStatus.DeniedIdenticalLong, denied.QuotaStatus);
    }

    [Fact]
    public async Task RedisUnavailable_Rejects()
    {
        var quota = new RecordingQuotaStore { Unavailable = true };
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        var result = await Create(snapshot, quota).EvaluateAsync(Request());
        Assert.Equal(PostFilterDecision.Reject, result.Decision);
        Assert.Equal(PostFilterQuotaReserveStatus.Unavailable, result.QuotaStatus);
    }

    [Fact]
    public async Task CancellationAfterReserve_Releases()
    {
        var quota = new RecordingQuotaStore();
        using var cts = new CancellationTokenSource();
        var sa = new RecordingSpamAssassin { Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = new PostFilterSpamAssassinOptions
            {
                Enabled = true,
                OnFailure = PostFilterSpamOnFailure.Reject,
                Hosts = ["127.0.0.1"],
            },
        });
        var evaluate = Create(snapshot, quota, sa).EvaluateAsync(Request(), cts.Token).AsTask();
        await sa.BlockEntered.Task;
        await cts.CancelAsync();
        sa.Block.SetCanceled();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluate);
        Assert.Equal(new[] { "reserve", "release" }, quota.Operations);
        Assert.Equal(CancellationToken.None, quota.LastReleaseToken);
    }

    [Fact]
    public async Task ReserveCanceled_ReleasesWithNone()
    {
        var quota = new RecordingQuotaStore { CancelReserve = true };
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(snapshot, quota).EvaluateAsync(Request()).AsTask());
        Assert.Equal(new[] { "reserve", "release" }, quota.Operations);
        Assert.Equal(CancellationToken.None, quota.LastReleaseToken);
    }

    [Fact]
    public async Task SnapshotCapturedOnce_RefreshDuringSaDoesNotChangeWindows()
    {
        var first = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10, LongWindow = TimeSpan.FromHours(1) },
            SpamAssassin = new PostFilterSpamAssassinOptions
            {
                Enabled = true,
                OnFailure = PostFilterSpamOnFailure.Reject,
                Hosts = ["127.0.0.1"],
            },
        });
        var second = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Closed,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1, LongWindow = TimeSpan.FromMinutes(1) },
        });
        var source = new MutablePolicySource(first);
        var quota = new RecordingQuotaStore();
        var sa = new RecordingSpamAssassin { Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var filter = new PostFilterEvaluator(
            source,
            quota,
            new PostFilterReservationIdentity("n1", "inc"),
            sa,
            NullLogger<PostFilterEvaluator>.Instance);
        var evaluate = filter.EvaluateAsync(Request(), CancellationToken.None).AsTask();
        await sa.BlockEntered.Task;
        source.Current = second;
        sa.Block.SetResult();
        var result = await evaluate;
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.NotNull(result.Lease);
        Assert.Equal(first.Windows, quota.LastWindows);
        Assert.Equal(first.Ceilings, quota.LastCeilings);
        Assert.Equal(first.ReservationTtlMs, quota.LastReservationTtlMs);
        Assert.Equal(first.SpamAssassinHosts, sa.LastTarget.Hosts);
        Assert.Equal(first.SpamAssassinOperationTimeout, sa.LastTarget.OperationTimeout);
        Assert.Equal(1, sa.Calls);
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await result.Lease.CommitAsync(Now, CancellationToken.None));
        Assert.Equal(first.Windows, quota.LastCommitWindows);
        Assert.Equal(first.Ceilings, quota.LastCommitCeilings);
    }

    [Fact]
    public async Task ReserveCanceledAfterRedisWrite_Releases()
    {
        var redis = new FakeRedisService
        {
            Database =
            {
                ScriptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                BlockScript = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            },
        };
        var store = new RedisPostFilterQuotaStore(redis);
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
        });
        using var cts = new CancellationTokenSource();
        var evaluate = Create(snapshot, store).EvaluateAsync(Request(), cts.Token).AsTask();
        await redis.Database.ScriptStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        redis.Database.BlockScript!.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluate);
        Assert.Equal(
            PostFilterQuotaReserveStatus.Accepted,
            await store.ReserveAsync(
                "poster",
                new PostFilterReservationId("n2", "b", 1),
                Now,
                snapshot.Windows,
                snapshot.Ceilings,
                1,
                1,
                0,
                null));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("quota")]
    [InlineData("sa-skip")]
    [InlineData("sa-ham")]
    [InlineData("sa-reject")]
    public async Task EvaluateAsync_DoesNotMutateArticleRecordBytes(string path)
    {
        var article = Article();
        var before = Capture(article);
        var sa = new RecordingSpamAssassin();
        PostFilterPolicySnapshot snapshot;
        if (path == "disabled")
        {
            snapshot = PostFilterPolicySnapshot.Disabled;
        }
        else if (path == "sa-skip")
        {
            var options = new PostFilterOptions
            {
                Gate = PostFilterGateState.Active,
                Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
                SpamAssassin = new PostFilterSpamAssassinOptions
                {
                    Enabled = true,
                    OnFailure = PostFilterSpamOnFailure.Reject,
                    Hosts = ["127.0.0.1"],
                    MaxArticleSize = 1,
                    ExcludeArtTypes = [],
                },
            };
            snapshot = Compile(options);
        }
        else if (path is "sa-ham" or "sa-reject")
        {
            sa.Result = path == "sa-reject"
                ? PostFilterSpamAssassinResult.Spam("spam")
                : PostFilterSpamAssassinResult.Ham();
            snapshot = Compile(new PostFilterOptions
            {
                Gate = PostFilterGateState.Active,
                Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
                SpamAssassin = new PostFilterSpamAssassinOptions
                {
                    Enabled = true,
                    OnFailure = PostFilterSpamOnFailure.Reject,
                    Hosts = ["127.0.0.1"],
                    MaxArticleSize = 0,
                    ExcludeArtTypes = [],
                },
            });
        }
        else
        {
            snapshot = Compile(new PostFilterOptions
            {
                Gate = PostFilterGateState.Active,
                Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            });
        }

        var result = await Create(snapshot, new RecordingQuotaStore(), sa).EvaluateAsync(Request(article));
        if (path == "sa-reject")
        {
            Assert.Equal(PostFilterDecision.Reject, result.Decision);
        }
        else
        {
            Assert.Equal(PostFilterDecision.Accept, result.Decision);
        }

        AssertUnchanged(before, article);
    }

    [Fact]
    public async Task TwoNodes_ShareAccountQuota()
    {
        var engine = new PostFilterQuotaEngine();
        var shared = new InMemoryPostFilterQuotaStore(engine);
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
        });
        var a = Create(snapshot, shared, identity: new PostFilterReservationIdentity("n1", "incA"));
        var b = Create(snapshot, shared, identity: new PostFilterReservationIdentity("n2", "incB"));
        Assert.Equal(PostFilterDecision.Accept, (await a.EvaluateAsync(Request())).Decision);
        Assert.Equal(PostFilterDecision.Reject, (await b.EvaluateAsync(Request())).Decision);
    }

    [Fact]
    public void ArticleRecord_IsNotMutated()
    {
        var article = Article();
        var hash = article.ArtHash;
        var size = article.ArtSize;
        var type = article.ArtType;
        _ = PostFilterBodyHash.TryCompute(article);
        Assert.Equal(hash, article.ArtHash);
        Assert.Equal(size, article.ArtSize);
        Assert.Equal(type, article.ArtType);
    }

    [Fact]
    public void DisabledSpamAssassin_PreservesTenSecondReservationFloor()
    {
        var hold = PostFilterQuotaDefaults.HoldMilliseconds(false, TimeSpan.FromSeconds(30));
        Assert.Equal((long)PostFilterQuotaDefaults.ReservationTtl.TotalMilliseconds, hold);
        var snapshot = Compile(new PostFilterOptions { Gate = PostFilterGateState.Active });
        Assert.False(snapshot.SpamAssassinEnabled);
        Assert.Equal(hold, snapshot.ReservationTtlMs);
    }

    [Fact]
    public void EnabledSpamAssassin_ReservationHoldCoversDefaultOperationTimeout()
    {
        var sa = new PostFilterSpamAssassinOptions();
        var hold = PostFilterQuotaDefaults.HoldMilliseconds(true, sa.OperationTimeout);
        Assert.True(hold >= (long)sa.OperationTimeout.TotalMilliseconds);
        Assert.Equal(
            (long)sa.OperationTimeout.TotalMilliseconds + (long)PostFilterQuotaDefaults.ReservationHoldSkew.TotalMilliseconds,
            hold);
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            SpamAssassin = new PostFilterSpamAssassinOptions
            {
                Enabled = true,
                OnFailure = PostFilterSpamOnFailure.Reject,
                Hosts = ["127.0.0.1"],
            },
        });
        Assert.Equal(hold, snapshot.ReservationTtlMs);
        Assert.Equal(["127.0.0.1"], snapshot.SpamAssassinHosts);
        Assert.Equal(783, snapshot.SpamAssassinPort);
        Assert.Equal(sa.OperationTimeout, snapshot.SpamAssassinOperationTimeout);
        Assert.Equal(PostFilterSpamAssassinOptions.DefaultMaxArticleSize, snapshot.SpamAssassinMaxArticleSize);
        Assert.Equal(ArticleType.YEncoded, snapshot.SpamAssassinExcludeArtTypes);
        Assert.Equal(PostFilterSpamAssassinOptions.DefaultProtocolVersion, snapshot.SpamAssassinProtocolVersion);
        Assert.Equal(PostFilterSpamAssassinOptions.DefaultMaxConnections, snapshot.SpamAssassinMaxConnections);
        Assert.Equal(PostFilterSpamAssassinHostSelection.RoundRobin, snapshot.SpamAssassinHostSelection);
    }

    [Fact]
    public async Task EvaluateAsync_ReservationStaysLiveThroughConfiguredSaWait()
    {
        var quota = new InMemoryPostFilterQuotaStore();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
            SpamAssassin = new PostFilterSpamAssassinOptions
            {
                Enabled = true,
                OnFailure = PostFilterSpamOnFailure.Reject,
                Hosts = ["127.0.0.1"],
            },
        });
        var result = await Create(snapshot, quota).EvaluateAsync(Request());
        Assert.Equal(PostFilterDecision.Accept, result.Decision);
        Assert.NotNull(result.Lease);
        var duringSa = Now.Add(snapshot.SpamAssassinOperationTimeout);
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await result.Lease!.CommitAsync(duringSa, CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredReservation_DoesNotConsumeQuotaForLaterPost()
    {
        var quota = new InMemoryPostFilterQuotaStore();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
        });
        var first = await Create(snapshot, quota).EvaluateAsync(Request());
        Assert.Equal(PostFilterDecision.Accept, first.Decision);
        var expired = Now.AddMilliseconds(snapshot.ReservationTtlMs);
        Assert.Equal(
            PostFilterQuotaCommitStatus.Noop,
            await first.Lease!.CommitAsync(expired, CancellationToken.None));
        var second = await Create(snapshot, quota).EvaluateAsync(Request(now: expired));
        Assert.Equal(PostFilterDecision.Accept, second.Decision);
        Assert.Equal(
            PostFilterQuotaCommitStatus.Committed,
            await second.Lease!.CommitAsync(expired, CancellationToken.None));
        var third = await Create(snapshot, quota).EvaluateAsync(Request(now: expired));
        Assert.Equal(PostFilterDecision.Reject, third.Decision);
    }

    [Fact]
    public void Compiler_RejectsSpamAssassinOperationTimeoutAboveCap()
    {
        var options = new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            SpamAssassin = new PostFilterSpamAssassinOptions
            {
                Enabled = true,
                OnFailure = PostFilterSpamOnFailure.Reject,
                Hosts = ["127.0.0.1"],
                OperationTimeout = TimeSpan.FromMinutes(3),
            },
        };
        Assert.Throws<InvalidOperationException>(() => PostFilterPolicyCompiler.Compile(options));
    }

    [Fact]
    public void Compiler_RequiresOnFailureWhenSpamAssassinEnabled()
    {
        var options = new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            SpamAssassin = new PostFilterSpamAssassinOptions { Enabled = true, Hosts = ["127.0.0.1"] },
        };
        Assert.Throws<InvalidOperationException>(() => PostFilterPolicyCompiler.Compile(options));
    }

    private static PostFilterEvaluator Create(
        PostFilterPolicySnapshot snapshot,
        IPostFilterQuotaStore quota,
        IPostFilterSpamAssassin? sa = null,
        PostFilterReservationIdentity? identity = null) =>
        new(
            new StaticPostFilterPolicySource(snapshot),
            quota,
            identity ?? new PostFilterReservationIdentity("n1", "inc"),
            sa ?? new RecordingSpamAssassin(),
            NullLogger<PostFilterEvaluator>.Instance);

    private static PostFilterPolicySnapshot Compile(PostFilterOptions options) =>
        PostFilterPolicyCompiler.Compile(options);

    private static PostFilterRequest Request(
        ArticleRecord? article = null,
        string account = "poster",
        DateTimeOffset? now = null) =>
        new(
            article ?? Article(),
            account,
            ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)),
            accountPolicy: null,
            now ?? Now,
            ["misc.test"],
            now ?? Now);

    private static ArticleSnapshot Capture(in ArticleRecord article) =>
        new(
            article.ArtId,
            article.ArtHash,
            article.ArtType,
            article.ArtLines,
            article.ArtSize,
            article.CanonicalUtc,
            article.ParseStatus,
            article.Fields,
            article.ArtData.ToArray(),
            GetBuffer(article));

    private static void AssertUnchanged(ArticleSnapshot before, in ArticleRecord after)
    {
        Assert.Equal(before.ArtId, after.ArtId);
        Assert.Equal(before.ArtHash, after.ArtHash);
        Assert.Equal(before.ArtType, after.ArtType);
        Assert.Equal(before.ArtLines, after.ArtLines);
        Assert.Equal(before.ArtSize, after.ArtSize);
        Assert.Equal(before.CanonicalUtc, after.CanonicalUtc);
        Assert.Equal(before.ParseStatus, after.ParseStatus);
        Assert.Equal(before.Fields.MessageId, after.Fields.MessageId);
        Assert.Equal(before.Fields.Newsgroups, after.Fields.Newsgroups);
        Assert.Equal(before.Fields.Subject, after.Fields.Subject);
        Assert.Equal(before.Fields.From, after.Fields.From);
        Assert.Equal(before.Fields.Date, after.Fields.Date);
        Assert.Equal(before.Fields.References, after.Fields.References);
        Assert.Equal(before.Fields.Path, after.Fields.Path);
        Assert.True(before.Bytes.AsSpan().SequenceEqual(after.ArtData.Span));
        Assert.Same(before.Buffer, GetBuffer(after));
    }

    private static byte[] GetBuffer(in ArticleRecord article) =>
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(article.ArtData, out var segment)
            ? segment.Array!
            : throw new InvalidOperationException("ArtData is not an array segment.");

    private readonly record struct ArticleSnapshot(
        ArticleId ArtId,
        ulong ArtHash,
        ArticleType ArtType,
        int ArtLines,
        int ArtSize,
        DateTime CanonicalUtc,
        ArticleParseStatus ParseStatus,
        ArticleFieldTable Fields,
        byte[] Bytes,
        byte[] Buffer);

    private sealed class MutablePolicySource : IPostFilterPolicySource
    {
        public MutablePolicySource(PostFilterPolicySnapshot current) => Current = current;

        public PostFilterPolicySnapshot Current { get; set; }
    }

    private static ArticleRecord Article(ArticleType type = ArticleType.Default)
    {
        var bytes = "From: a@b\r\nNewsgroups: misc.test\r\nSubject: t\r\nMessage-ID: <h@example.com>\r\nDate: 1 Jan 2026 00:00:00 +0000\r\n\r\nbody\r\n"u8.ToArray();
        return new ArticleRecord(
            ArticleId.FromMessageId("<h@example.com>"u8),
            artHash: 1,
            artType: type,
            artLines: 1,
            canonicalUtc: Now.UtcDateTime,
            parseStatus: ArticleParseStatus.CanonicalV1,
            artData: bytes,
            fields: default);
    }

    private sealed class RecordingQuotaStore : IPostFilterQuotaStore
    {
        private readonly InMemoryPostFilterQuotaStore _inner = new();

        public bool Unavailable { get; set; }

        public bool CancelReserve { get; set; }

        public List<string> Operations { get; } = [];

        public CancellationToken LastReleaseToken { get; private set; }

        public PostFilterQuotaWindows LastWindows { get; private set; }

        public PostFilterQuotaCeilings LastCeilings { get; private set; }

        public PostFilterQuotaWindows LastCommitWindows { get; private set; }

        public PostFilterQuotaCeilings LastCommitCeilings { get; private set; }

        public long LastReservationTtlMs { get; private set; }

        public ValueTask<PostFilterQuotaReserveStatus> ReserveAsync(
            string accountName,
            PostFilterReservationId reservation,
            DateTimeOffset now,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings,
            long messages,
            long bytes,
            int mpUnits,
            string? bodyHex,
            CancellationToken cancellationToken = default,
            long reservationTtlMs = 0)
        {
            Operations.Add("reserve");
            LastWindows = windows;
            LastCeilings = ceilings;
            LastReservationTtlMs = reservationTtlMs;
            if (CancelReserve)
            {
                var reserved = _inner.ReserveAsync(
                    accountName,
                    reservation,
                    now,
                    windows,
                    ceilings,
                    messages,
                    bytes,
                    mpUnits,
                    bodyHex,
                    CancellationToken.None,
                    reservationTtlMs);
                if (!reserved.IsCompletedSuccessfully)
                {
                    reserved.GetAwaiter().GetResult();
                }

                throw new OperationCanceledException();
            }

            if (Unavailable)
            {
                return ValueTask.FromResult(PostFilterQuotaReserveStatus.Unavailable);
            }

            return _inner.ReserveAsync(
                accountName,
                reservation,
                now,
                windows,
                ceilings,
                messages,
                bytes,
                mpUnits,
                bodyHex,
                cancellationToken,
                reservationTtlMs);
        }

        public ValueTask<PostFilterQuotaCommitStatus> CommitAsync(
            string accountName,
            PostFilterReservationId reservation,
            DateTimeOffset now,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings,
            CancellationToken cancellationToken = default)
        {
            Operations.Add("commit");
            LastCommitWindows = windows;
            LastCommitCeilings = ceilings;
            return _inner.CommitAsync(accountName, reservation, now, windows, ceilings, cancellationToken);
        }

        public ValueTask<PostFilterQuotaReleaseStatus> ReleaseAsync(
            string accountName,
            PostFilterReservationId reservation,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Operations.Add("release");
            LastReleaseToken = cancellationToken;
            return _inner.ReleaseAsync(accountName, reservation, now, cancellationToken);
        }
    }

    private sealed class RecordingSpamAssassin : IPostFilterSpamAssassin
    {
        public int Calls { get; private set; }

        public PostFilterSpamAssassinResult Result { get; set; } = PostFilterSpamAssassinResult.Ham();

        public TaskCompletionSource? Block { get; set; }

        public TaskCompletionSource BlockEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PostFilterSpamAssassinTarget LastTarget { get; private set; }

        public async ValueTask<PostFilterSpamAssassinResult> CheckAsync(
            ArticleRecord article,
            string? accountName,
            PostFilterSpamAssassinTarget target,
            SpamdScanContext scanContext,
            CancellationToken cancellationToken = default)
        {
            _ = article;
            _ = accountName;
            _ = scanContext;
            LastTarget = target;
            Calls++;
            if (Block is not null)
            {
                BlockEntered.TrySetResult();
                await Block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Result;
        }
    }
}
