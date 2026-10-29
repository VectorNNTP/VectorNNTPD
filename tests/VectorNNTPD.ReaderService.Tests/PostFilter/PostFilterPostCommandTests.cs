using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.History;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.PostFilter.Quota;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Session.Commands.Posting;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.PostFilter;

public sealed class PostFilterPostCommandTests
{
    private static readonly NntpAuthorization Poster = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: true,
        streamingPermitted: false);

    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ClosedGate_Returns441_AndDoesNotEnqueue()
    {
        var quota = new RecordingQuotaStore();
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        await PostAsync(duplex, duplex.CreateSession(queue, CreateFilter(Closed(), quota)), "441 Posting failed");
        Assert.Equal(0, queue.Count);
        Assert.Empty(quota.Operations);
    }

    [Fact]
    public async Task ClosedGate_WritesRejectedNewsWithPostFilterReason()
    {
        var news = new RecordingNewsLogWriter();
        var quota = new RecordingQuotaStore();
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        await PostAsync(
            duplex,
            duplex.CreateSession(queue, CreateFilter(Closed(), quota), newsLog: news),
            "441 Posting failed");
        Assert.Equal(0, queue.Count);
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Rejected, evt.Disposition);
        Assert.Equal(441, evt.ResponseCode);
        Assert.Equal("closed", Encoding.ASCII.GetString(evt.Reason.Span));
        var timestamp = evt.Timestamp == default
            ? new DateTimeOffset(2024, 8, 25, 13, 37, 54, 638, TimeSpan.Zero)
            : evt.Timestamp;
        var buffer = new byte[NewsLogLineFormatter.RequiredLength(in evt) + 16];
        var written = NewsLogLineFormatter.Write(buffer, in evt, timestamp);
        var line = Encoding.ASCII.GetString(buffer.AsSpan(0, written));
        Assert.EndsWith(" closed\n", line, StringComparison.Ordinal);
        Assert.DoesNotContain("441", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledGate_AcceptsAndEnqueues()
    {
        var quota = new RecordingQuotaStore();
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        await PostAsync(
            duplex,
            duplex.CreateSession(queue, CreateFilter(PostFilterPolicySnapshot.Disabled, quota)),
            "240 Article received OK");
        Assert.Equal(1, queue.Count);
        Assert.Empty(quota.Operations);
    }

    [Fact]
    public async Task DeniedAccount_Returns441()
    {
        var quota = new RecordingQuotaStore();
        var queue = NewQueue();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            DeniedAccounts = ["poster"],
        });
        await using var duplex = new PostDuplex();
        await PostAsync(duplex, duplex.CreateSession(queue, CreateFilter(snapshot, quota)), "441 Posting failed");
        Assert.Equal(0, queue.Count);
        Assert.Empty(quota.Operations);
    }

    [Fact]
    public async Task DeniedAccount_EnqueuesRejectionEvidence()
    {
        var quota = new RecordingQuotaStore();
        var evidence = new PostFilterRejectionEvidenceQueue();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            DeniedAccounts = ["poster"],
        });
        await using var duplex = new PostDuplex();
        await PostAsync(
            duplex,
            duplex.CreateSession(NewQueue(), CreateFilter(snapshot, quota), postFilterEvidence: evidence),
            "441 Posting failed");
        var row = Assert.Single(await DrainAsync(evidence));
        Assert.Equal(PostFilterStage.Deny, row.Stage);
        Assert.Equal("denied", row.Reason);
        Assert.Equal(PostFilterAccountIdentity.FromUsername("poster"), row.AccountName);
        Assert.Equal(IPAddress.Loopback, row.SourceAddress);
        Assert.Equal(ArticleType.Default, row.ArtType);
        Assert.NotNull(row.MessageId);
        Assert.True(row.ArticleSize > 0);
        Assert.NotNull(row.ArticlePayload);
        Assert.Null(row.SpamAssassinStatus);
    }

    [Fact]
    public async Task AcceptedPost_DoesNotEnqueueRejectionEvidence()
    {
        var evidence = new PostFilterRejectionEvidenceQueue();
        await using var duplex = new PostDuplex();
        await PostAsync(
            duplex,
            duplex.CreateSession(
                NewQueue(),
                CreateFilter(PostFilterPolicySnapshot.Disabled, new RecordingQuotaStore()),
                postFilterEvidence: evidence),
            "240 Article received OK");
        evidence.Complete();
        Assert.Null(await evidence.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task QueueFull_StillReturns441()
    {
        var evidence = new PostFilterRejectionEvidenceQueue(capacity: 1);
        Assert.True(evidence.TryEnqueue(new PostFilterRejectionEvidence(
            DateTimeOffset.UtcNow,
            1,
            "other",
            IPAddress.Loopback,
            ArticleType.Default,
            "<x@example.com>",
            1,
            PostFilterStage.Gate,
            "closed",
            null,
            null,
            null,
            null)));
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Closed,
        });
        await using var duplex = new PostDuplex();
        await PostAsync(
            duplex,
            duplex.CreateSession(
                NewQueue(),
                CreateFilter(snapshot, new RecordingQuotaStore()),
                postFilterEvidence: evidence),
            "441 Posting failed");
        Assert.Equal(1, evidence.Dropped);
    }

    [Fact]
    public async Task TextOnlyCapability_RejectsYEncoded_WithoutPostFilter()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, CreateFilter(PostFilterPolicySnapshot.Disabled, new RecordingQuotaStore()));
        session.ApplySuccessfulAuthentication(
            "poster",
            Poster,
            new NntpAccountPolicy("poster", 0, 0, 0, 0, "c", ArticleTypeCapabilities.TextOnly));
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(YEncodedArticle() + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task TextOnlyCapability_AllowsDefaultText()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, CreateFilter(PostFilterPolicySnapshot.Disabled, new RecordingQuotaStore()));
        session.ApplySuccessfulAuthentication(
            "poster",
            Poster,
            new NntpAccountPolicy("poster", 0, 0, 0, 0, "c", ArticleTypeCapabilities.TextOnly));
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(ValidArticle() + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task ArtTypePolicy_RejectsDefaultText()
    {
        var quota = new RecordingQuotaStore();
        var queue = NewQueue();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            RejectArtTypes = ["Default"],
        });
        await using var duplex = new PostDuplex();
        await PostAsync(duplex, duplex.CreateSession(queue, CreateFilter(snapshot, quota)), "441 Posting failed");
        Assert.Equal(0, queue.Count);
        Assert.Empty(quota.Operations);
    }

    [Fact]
    public async Task Allowlisted_StillConsumesQuota_AndSkipsSpamAssassin()
    {
        var quota = new RecordingQuotaStore();
        var sa = new RecordingSpamAssassin();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            AllowlistedAccounts = ["poster"],
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
            SpamAssassin = EnabledSpamAssassin(),
        });
        var filter = CreateFilter(snapshot, quota, sa);
        await using var first = new PostDuplex();
        await PostAsync(first, first.CreateSession(NewQueue(), filter), "240 Article received OK");
        await using var second = new PostDuplex();
        var queue = NewQueue();
        await PostAsync(second, second.CreateSession(queue, filter), "441 Posting failed");
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, sa.Calls);
        Assert.Contains("reserve", quota.Operations);
    }

    [Fact]
    public async Task NonAllowlisted_InvokesSpamAssassin()
    {
        var sa = new RecordingSpamAssassin();
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = EnabledSpamAssassin(),
        });
        await using var duplex = new PostDuplex();
        await PostAsync(
            duplex,
            duplex.CreateSession(NewQueue(), CreateFilter(snapshot, new RecordingQuotaStore(), sa)),
            "240 Article received OK");
        Assert.Equal(1, sa.Calls);
    }

    [Fact]
    public async Task SpamAssassinSpam_Returns441_AndReleases()
    {
        var quota = new RecordingQuotaStore();
        var sa = new RecordingSpamAssassin { Result = PostFilterSpamAssassinResult.Spam("spam") };
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = EnabledSpamAssassin(),
        });
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        await PostAsync(duplex, duplex.CreateSession(queue, CreateFilter(snapshot, quota, sa)), "441 Posting failed");
        Assert.Equal(0, queue.Count);
        Assert.Equal(new[] { "reserve", "release" }, quota.Operations);
    }

    [Theory]
    [InlineData(PostFilterSpamOnFailure.Reject, "441 Posting failed", false)]
    [InlineData(PostFilterSpamOnFailure.Accept, "240 Article received OK", true)]
    public async Task SpamAssassinFailure_FollowsConfiguredPolicy(
        PostFilterSpamOnFailure onFailure,
        string expected,
        bool enqueued)
    {
        var quota = new RecordingQuotaStore();
        var sa = new RecordingSpamAssassin { Result = PostFilterSpamAssassinResult.Failed("down") };
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = EnabledSpamAssassin(onFailure),
        });
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        await PostAsync(duplex, duplex.CreateSession(queue, CreateFilter(snapshot, quota, sa)), expected);
        Assert.Equal(enqueued ? 1 : 0, queue.Count);
        if (enqueued)
        {
            Assert.Equal(new[] { "reserve", "commit" }, quota.Operations);
        }
        else
        {
            Assert.Equal(new[] { "reserve", "release" }, quota.Operations);
        }
    }

    [Fact]
    public async Task QuotaMessageCeiling_Returns441()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
        });
        var quota = new RecordingQuotaStore();
        var filter = CreateFilter(snapshot, quota);
        await using var first = new PostDuplex();
        await PostAsync(first, first.CreateSession(NewQueue(), filter), "240 Article received OK");
        await using var second = new PostDuplex();
        var queue = NewQueue();
        await PostAsync(second, second.CreateSession(queue, filter), "441 Posting failed");
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task QuotaByteCeiling_Returns441()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxBytesLong = 1 },
        });
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        await PostAsync(
            duplex,
            duplex.CreateSession(queue, CreateFilter(snapshot, new RecordingQuotaStore())),
            "441 Posting failed");
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task QuotaIdenticalCeiling_Returns441()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxIdenticalLong = 1 },
        });
        var filter = CreateFilter(snapshot, new RecordingQuotaStore());
        await using var first = new PostDuplex();
        await PostAsync(first, first.CreateSession(NewQueue(), filter), "240 Article received OK");
        await using var second = new PostDuplex();
        var queue = NewQueue();
        await PostAsync(second, second.CreateSession(queue, filter), "441 Posting failed");
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task RedisUnavailable_Returns441()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        var quota = new RecordingQuotaStore { Unavailable = true };
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        await PostAsync(duplex, duplex.CreateSession(queue, CreateFilter(snapshot, quota)), "441 Posting failed");
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task ReserveHappensBeforeTryAdmit_AndCommitAfterAccept()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        var clock = new SequenceClock();
        var quota = new RecordingQuotaStore(clock);
        var queue = new RecordingIngestionQueue(NewQueue(), clock);
        await using var duplex = new PostDuplex();
        await PostAsync(duplex, duplex.CreateSession(queue, CreateFilter(snapshot, quota)), "240 Article received OK");
        Assert.Equal(new[] { "reserve", "commit" }, quota.Operations);
        Assert.Equal(1, queue.TryAdmitCalls);
        Assert.True(quota.ReserveSequence < queue.AdmitSequence);
        Assert.True(quota.CommitSequence > queue.AdmitSequence);
    }

    [Fact]
    public async Task ActiveGate_ReservesAndCommits()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        var quota = new RecordingQuotaStore();
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        await PostAsync(duplex, duplex.CreateSession(queue, CreateFilter(snapshot, quota)), "240 Article received OK");
        Assert.Equal(1, queue.Count);
        Assert.Equal(new[] { "reserve", "commit" }, quota.Operations);
    }

    [Fact]
    public async Task CommitNoop_StillQueuesAndReturns240()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        var quota = new RecordingQuotaStore { CommitNoop = true };
        var queue = NewQueue();
        var metrics = new PostFilterMetrics();
        await using var duplex = new PostDuplex();
        await PostAsync(
            duplex,
            duplex.CreateSession(queue, CreateFilter(snapshot, quota), metrics),
            "240 Article received OK");
        Assert.Equal(1, queue.Count);
        Assert.Equal(1, metrics.CommitNoop);
        Assert.DoesNotContain("release", quota.Operations);
    }

    [Fact]
    public async Task CommitUnavailable_AfterAdmit_StillQueuesAndReturns240_WithoutRelease()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        var quota = new RecordingQuotaStore { CommitUnavailable = true };
        var queue = NewQueue();
        var metrics = new PostFilterMetrics();
        await using var duplex = new PostDuplex();
        await PostAsync(
            duplex,
            duplex.CreateSession(queue, CreateFilter(snapshot, quota), metrics),
            "240 Article received OK");
        Assert.Equal(1, queue.Count);
        Assert.Equal(1, metrics.CommitUnavailable);
        Assert.Equal(0, metrics.CommitNoop);
        Assert.DoesNotContain("release", quota.Operations);
        Assert.Contains("commit", quota.Operations);
    }

    [Fact]
    public async Task TryAdmitFailure_Releases_AndDoesNotCommit()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        var quota = new RecordingQuotaStore();
        await using var duplex = new PostDuplex();
        await PostAsync(
            duplex,
            duplex.CreateSession(DisabledArticleIngestionQueue.Instance, CreateFilter(snapshot, quota)),
            "441 Posting failed");
        Assert.Equal(new[] { "reserve", "release" }, quota.Operations);
        Assert.DoesNotContain("commit", quota.Operations);
        Assert.Equal(CancellationToken.None, quota.LastReleaseToken);
    }

    [Fact]
    public async Task SuccessfulAdmit_CommitsReservationUnits()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10, MaxBytesLong = 100_000 },
        });
        var quota = new RecordingQuotaStore();
        await using var duplex = new PostDuplex();
        await PostAsync(duplex, duplex.CreateSession(NewQueue(), CreateFilter(snapshot, quota)), "240 Article received OK");
        Assert.Equal(1, quota.LastReservedMessages);
        Assert.True(quota.LastReservedBytes > 0);
        Assert.Equal(quota.LastReservedMessages, quota.LastCommittedMessages);
        Assert.Equal(quota.LastReservedBytes, quota.LastCommittedBytes);
        Assert.False(quota.CommitReceivedUnits);
    }

    [Fact]
    public async Task HistoryPeekUnavailable_Returns441_WithoutReserve()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        var quota = new RecordingQuotaStore();
        var history = new RecordingHistoryDb { PeekResult = HistoryLookupResult.Unavailable };
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        await PostAsync(
            duplex,
            duplex.CreateSession(queue, CreateFilter(snapshot, quota), historyDb: history),
            "441 Posting failed");
        Assert.Equal(0, queue.Count);
        Assert.Empty(quota.Operations);
        Assert.Equal(0, history.RememberCalls);
    }

    [Fact]
    public async Task CancellationAfterReservation_Releases_AndDoesNotEnqueue()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        var quota = new RecordingQuotaStore();
        var queue = NewQueue();
        var history = new RecordingHistoryDb();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(
            queue,
            CreateFilter(snapshot, quota),
            historyDb: history,
            cancelConnectionAfterAccept: true);
        session.ApplySuccessfulAuthentication("poster", Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(ValidArticle() + ".\r\n");
        await run.WaitAsync(Safety);
        Assert.Equal(0, queue.Count);
        Assert.Equal(new[] { "reserve", "release" }, quota.Operations);
        Assert.Equal(CancellationToken.None, quota.LastReleaseToken);
        Assert.Equal(0, history.RememberCalls);
    }

    [Fact]
    public async Task CommitThenRememberThen240()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        var clock = new SequenceClock();
        var quota = new RecordingQuotaStore(clock);
        var history = new RecordingHistoryDb(clock);
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        await PostAsync(
            duplex,
            duplex.CreateSession(queue, CreateFilter(snapshot, quota), historyDb: history),
            "240 Article received OK");
        Assert.Equal(1, history.RememberCalls);
        Assert.True(quota.CommitSequence < history.RememberSequence);
        Assert.DoesNotContain("release", quota.Operations);
    }

    [Fact]
    public async Task RememberFailureAfterCommit_DoesNotRelease()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        var quota = new RecordingQuotaStore();
        var history = new RecordingHistoryDb { ThrowOnRemember = true };
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, CreateFilter(snapshot, quota), historyDb: history);
        session.ApplySuccessfulAuthentication("poster", Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(ValidArticle() + ".\r\n");
        await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(Safety));
        Assert.Equal(1, queue.Count);
        Assert.Equal(new[] { "reserve", "commit" }, quota.Operations);
        Assert.DoesNotContain("release", quota.Operations);
    }

    [Fact]
    public async Task QueuedArticle_KeepsOriginalArtHashAndBytes()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        await PostAsync(
            duplex,
            duplex.CreateSession(queue, CreateFilter(snapshot, new RecordingQuotaStore())),
            "240 Article received OK");
        using var cts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(cts.Token);
        Assert.NotNull(inbound);
        var record = inbound!.Record;
        Assert.Equal(ArticleParseStatus.CanonicalV1, record.ParseStatus);
        var hash = record.ArtHash;
        var id = record.ArtId;
        var type = record.ArtType;
        var lines = record.ArtLines;
        var size = record.ArtSize;
        var fields = record.Fields;
        var data = record.ArtData.ToArray();
        Assert.True(record.ArtData.Equals(inbound.Payload));
        Assert.Equal(hash, record.ArtHash);
        Assert.Equal(id, record.ArtId);
        Assert.Equal(type, record.ArtType);
        Assert.Equal(lines, record.ArtLines);
        Assert.Equal(size, record.ArtSize);
        Assert.Equal(fields.MessageId, record.Fields.MessageId);
        Assert.True(data.AsSpan().SequenceEqual(record.ArtData.Span));
        Assert.True(record.ArtData.Span.SequenceEqual(inbound.Payload.Span));
    }

    [Fact]
    public async Task TwoNodes_ShareAccountQuota()
    {
        var snapshot = Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
        });
        var shared = new InMemoryPostFilterQuotaStore();
        var nodeA = CreateFilter(snapshot, shared, identity: new PostFilterReservationIdentity("n1", "incA"));
        var nodeB = CreateFilter(snapshot, shared, identity: new PostFilterReservationIdentity("n2", "incB"));
        await using var first = new PostDuplex();
        await PostAsync(first, first.CreateSession(NewQueue(), nodeA), "240 Article received OK");
        await using var second = new PostDuplex();
        var queue = NewQueue();
        await PostAsync(second, second.CreateSession(queue, nodeB), "441 Posting failed");
        Assert.Equal(0, queue.Count);
    }

    private static async Task PostAsync(PostDuplex duplex, NntpSession session, string expect)
    {
        session.ApplySuccessfulAuthentication("poster", Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(ValidArticle() + ".\r\n");
        Assert.Equal(expect, await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    private static IPostFilter CreateFilter(
        PostFilterPolicySnapshot snapshot,
        IPostFilterQuotaStore quota,
        IPostFilterSpamAssassin? sa = null,
        PostFilterReservationIdentity? identity = null) =>
        new PostFilterEvaluator(
            new StaticPostFilterPolicySource(snapshot),
            quota,
            identity ?? new PostFilterReservationIdentity("n1", "inc"),
            sa ?? new RecordingSpamAssassin(),
            NullLogger<PostFilterEvaluator>.Instance);

    private static PostFilterPolicySnapshot Compile(PostFilterOptions options) =>
        PostFilterPolicyCompiler.Compile(options);

    private static PostFilterPolicySnapshot Closed() =>
        Compile(new PostFilterOptions { Gate = PostFilterGateState.Closed });

    private static PostFilterSpamAssassinOptions EnabledSpamAssassin(
        PostFilterSpamOnFailure onFailure = PostFilterSpamOnFailure.Reject) =>
        new()
        {
            Enabled = true,
            OnFailure = onFailure,
            Hosts = ["127.0.0.1"],
        };

    private static ArticleIngestionQueue NewQueue() =>
        new(new ArticleIngestionOptions { QueueCapacity = 8, MaxArticleBytes = NntpdOptions.DefaultMaxArticleSize });

    private static string ValidArticle() =>
        "Date: " + PostRfcDate.Format(DateTimeOffset.UtcNow) + "\r\n"
        + "From: poster@example.com\r\n"
        + "Newsgroups: misc.test\r\n"
        + "Subject: test\r\n"
        + "Message-ID: <" + Guid.NewGuid().ToString("N") + "@example.com>\r\n"
        + "\r\n"
        + "body\r\n";

    private static string YEncodedArticle() =>
        "Date: " + PostRfcDate.Format(DateTimeOffset.UtcNow) + "\r\n"
        + "From: poster@example.com\r\n"
        + "Newsgroups: misc.test\r\n"
        + "Subject: test\r\n"
        + "Message-ID: <" + Guid.NewGuid().ToString("N") + "@example.com>\r\n"
        + "\r\n"
        + "=ybegin line=128 size=3 name=a.bin\r\n"
        + "abc\r\n"
        + "=yend size=3\r\n";

    private static async Task<List<PostFilterRejectionEvidence>> DrainAsync(
        PostFilterRejectionEvidenceQueue queue)
    {
        queue.Complete();
        var rows = new List<PostFilterRejectionEvidence>();
        while (await queue.DequeueAsync(CancellationToken.None) is { } row)
        {
            rows.Add(row);
        }

        return rows;
    }

    private sealed class PostDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public NntpSession CreateSession(
            IArticleIngestionQueue queue,
            IPostFilter filter,
            PostFilterMetrics? metrics = null,
            IHistoryDb? historyDb = null,
            bool cancelConnectionAfterAccept = false,
            IPostFilterRejectionEvidenceQueue? postFilterEvidence = null,
            INewsLogWriter? newsLog = null)
        {
            var connection = new PipeConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)));
            IPostFilter wired = filter;
            if (cancelConnectionAfterAccept)
            {
                wired = new CancelAfterAcceptFilter(filter, connection);
            }

            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                historyDb: historyDb,
                postingTraceProtector: AesGcmPostingTraceProtector.Create(
                    new NntpdOptions { XTraceKey = TestHostFactory.TestXTraceKey }),
                postFilter: wired,
                postFilterMetrics: metrics,
                postFilterEvidence: postFilterEvidence,
                newsLog: newsLog);
        }

        public async Task WriteClientLineAsync(string line) =>
            await WriteClientAsync(line + "\r\n");

        public async Task WriteClientAsync(string payload)
        {
            await _clientToServer.Writer.WriteAsync(Encoding.ASCII.GetBytes(payload));
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(Safety);
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }
    }

    private sealed class CancelAfterAcceptFilter : IPostFilter
    {
        private readonly IPostFilter _inner;
        private readonly INntpConnection _connection;

        public CancelAfterAcceptFilter(IPostFilter inner, INntpConnection connection)
        {
            _inner = inner;
            _connection = connection;
        }

        public async ValueTask<PostFilterResult> EvaluateAsync(
            PostFilterRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Decision == PostFilterDecision.Accept && result.Lease is not null)
            {
                await _connection.CompleteAsync().ConfigureAwait(false);
            }

            return result;
        }
    }

    private sealed class PipeConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();

        public PipeConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
        {
            Input = input;
            Output = output;
            ClientIdentity = identity;
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }

        public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

        public EndPoint? LocalEndPoint => null;

        public ConnectionClientIdentity ClientIdentity { get; }

        public bool IsTls => false;

        public bool IsCompressed => false;

        public CancellationToken ConnectionClosed => _cts.Token;

        public bool IsCompleted => _cts.IsCancellationRequested;

        public long OutboundIdleVersion => 0;

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CompleteAsync(Exception? exception = null)
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SequenceClock
    {
        private int _value;

        public int Next() => Interlocked.Increment(ref _value);
    }

    private sealed class RecordingIngestionQueue : IArticleIngestionQueue
    {
        private readonly IArticleIngestionQueue _inner;
        private readonly SequenceClock _clock;

        public RecordingIngestionQueue(IArticleIngestionQueue inner, SequenceClock clock)
        {
            _inner = inner;
            _clock = clock;
        }

        public int TryAdmitCalls { get; private set; }

        public int AdmitSequence { get; private set; }

        public long MemoryLimitBytes => _inner.MemoryLimitBytes;

        public long QueuedBytes => _inner.QueuedBytes;

        public long PeakQueuedBytes => _inner.PeakQueuedBytes;

        public int MaxArticleBytes => _inner.MaxArticleBytes;

        public int Count => _inner.Count;

        public int PeakCount => _inner.PeakCount;

        public bool IsAccepting => _inner.IsAccepting;

        public ValueTask<ArticleEnqueueResult> EnqueueAsync(
            InboundArticle article,
            CancellationToken cancellationToken) =>
            _inner.EnqueueAsync(article, cancellationToken);

        public bool TryProbeCapacity() => _inner.TryProbeCapacity();

        public ArticleEnqueueResult TryAdmit(InboundArticle article)
        {
            AdmitSequence = _clock.Next();
            TryAdmitCalls++;
            return _inner.TryAdmit(article);
        }

        public bool TryEnqueue(InboundArticle article) => _inner.TryEnqueue(article);

        public void Complete() => _inner.Complete();

        public ValueTask<InboundArticle?> DequeueAsync(CancellationToken cancellationToken) =>
            _inner.DequeueAsync(cancellationToken);
    }

    private sealed class RecordingQuotaStore : IPostFilterQuotaStore
    {
        private readonly InMemoryPostFilterQuotaStore _inner = new();
        private readonly SequenceClock? _clock;

        public RecordingQuotaStore(SequenceClock? clock = null)
        {
            _clock = clock;
        }

        public bool Unavailable { get; set; }

        public bool CommitNoop { get; set; }

        public bool CommitUnavailable { get; set; }

        public CancellationToken LastReleaseToken { get; private set; }

        public List<string> Operations { get; } = [];

        public int ReserveSequence { get; private set; }

        public int CommitSequence { get; private set; }

        public long LastReservedMessages { get; private set; }

        public long LastReservedBytes { get; private set; }

        public long LastCommittedMessages { get; private set; }

        public long LastCommittedBytes { get; private set; }

        public bool CommitReceivedUnits { get; private set; }

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
            ReserveSequence = _clock?.Next() ?? Operations.Count;
            LastReservedMessages = messages;
            LastReservedBytes = bytes;
            _ = reservationTtlMs;
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
            CommitSequence = _clock?.Next() ?? Operations.Count;
            LastCommittedMessages = LastReservedMessages;
            LastCommittedBytes = LastReservedBytes;
            if (CommitNoop)
            {
                return ValueTask.FromResult(PostFilterQuotaCommitStatus.Noop);
            }

            if (CommitUnavailable)
            {
                return ValueTask.FromResult(PostFilterQuotaCommitStatus.Unavailable);
            }

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

    private sealed class RecordingHistoryDb : IHistoryDb
    {
        private readonly SequenceClock? _clock;

        public RecordingHistoryDb(SequenceClock? clock = null)
        {
            _clock = clock;
        }

        public int RememberCalls { get; private set; }

        public int RememberSequence { get; private set; }

        public bool ThrowOnRemember { get; set; }

        public HistoryLookupResult PeekResult { get; set; } = HistoryLookupResult.Unseen;

        public ValueTask<HistoryLookupResult> LookupAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            new(HistoryLookupResult.Unseen);

        public ValueTask<HistoryLookupResult> PeekAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            new(PeekResult);

        public void Remember(ReadOnlyMemory<byte> messageId)
        {
            RememberCalls++;
            RememberSequence = _clock?.Next() ?? RememberCalls;
            if (ThrowOnRemember)
            {
                throw new InvalidOperationException("history remember failed");
            }
        }

        public bool ContainsLocal(in HistoryDigest digest) => false;
    }

    private sealed class RecordingSpamAssassin : IPostFilterSpamAssassin
    {
        public int Calls { get; private set; }

        public PostFilterSpamAssassinResult Result { get; set; } = PostFilterSpamAssassinResult.Ham();

        public ValueTask<PostFilterSpamAssassinResult> CheckAsync(
            ArticleRecord article,
            string? accountName,
            PostFilterSpamAssassinTarget target,
            SpamdScanContext scanContext,
            CancellationToken cancellationToken = default)
        {
            _ = article;
            _ = accountName;
            _ = target;
            _ = scanContext;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(Result);
        }
    }
}
