using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.History;
using VectorNNTPD.ReaderService.Email;
using VectorNNTPD.ReaderService.Moderation;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Session.Commands.Posting;
using VectorNNTPD.ReaderService.Tests.Email;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.Moderation;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Session;

public sealed class ModeratedPostCommandTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Y_OrdinaryUser_NoApproved_Returns240()
    {
        var outcome = await PostAsync("group.y", extraHeaders: "", user: MapNntpAuthenticationProvider.NormalUser);
        AssertAccepted(outcome);
    }

    [Theory]
    [InlineData("group.n")]
    [InlineData("group.x")]
    [InlineData("group.j")]
    public async Task Njx_NoApproved_Returns441(string group)
    {
        var outcome = await PostAsync(group, extraHeaders: "", user: MapNntpAuthenticationProvider.NormalUser);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task M_OrdinaryUser_NoApproved_TakesModerationPath()
    {
        var submission = new RecordingModerationSubmissionService();
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            submission: submission);
        Assert.Equal("240 Article received OK", outcome.Response);
        Assert.Equal(0, outcome.Queue.TryAdmitCalls);
        Assert.Equal(0, outcome.History.PeekCalls);
        Assert.Equal(0, outcome.History.RememberCalls);
        var recorded = Assert.Single(submission.Submissions);
        Assert.Equal("group.a", recorded.TargetModeratedGroup);
        Assert.Equal("moderator-a@example.com", recorded.ModeratorAddress);
        Assert.Equal(MapNntpAuthenticationProvider.NormalUser, recorded.AuthenticatedUsername);
        Assert.Equal("<ok@example.com>", recorded.MessageId);
        var text = Encoding.ASCII.GetString(recorded.ProtoArticle.Span);
        Assert.Contains("Message-ID: <ok@example.com>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Injection-Info:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Injection-Date:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Trace:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Path: .POSTED", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M_OrdinaryUser_DoesNotEvaluatePostFilter()
    {
        var filter = new CountingPostFilter();
        var submission = new RecordingModerationSubmissionService();
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            submission: submission,
            postFilter: filter);
        Assert.Equal("240 Article received OK", outcome.Response);
        Assert.Equal(0, filter.EvaluateCalls);
        Assert.Equal(0, outcome.Queue.TryAdmitCalls);
        Assert.Single(submission.Submissions);
    }

    [Fact]
    public async Task M_OrdinaryUser_WritesModeratedNews_NotAccepted()
    {
        var news = new RecordingNewsLogWriter();
        var submission = new RecordingModerationSubmissionService();
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            submission: submission,
            newsLog: news);
        Assert.Equal("240 Article received OK", outcome.Response);
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Moderated, evt.Disposition);
        Assert.NotEqual(NewsLogDisposition.Accepted, evt.Disposition);
        Assert.NotEqual((byte)'?', (byte)evt.Disposition);
        Assert.True(evt.MessageId.Span.SequenceEqual("<ok@example.com>"u8));
        var timestamp = evt.Timestamp == default
            ? new DateTimeOffset(2024, 8, 25, 13, 37, 41, 839, TimeSpan.Zero)
            : evt.Timestamp;
        var buffer = new byte[NewsLogLineFormatter.RequiredLength(in evt) + 16];
        var written = NewsLogLineFormatter.Write(buffer, in evt, timestamp);
        var line = Encoding.ASCII.GetString(buffer.AsSpan(0, written));
        Assert.Contains(" m ? <ok@example.com>", line, StringComparison.Ordinal);
        Assert.DoesNotContain(" + ", line, StringComparison.Ordinal);
        Assert.DoesNotContain(" " + evt.Size + " ?", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M_Unauthenticated_Approved_Returns441()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: null);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task M_NormalUser_Approved_Returns441()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.NormalUser);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task M_CorrectModerator_CorrectApproved_Returns240()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA);
        AssertAccepted(outcome);
        var text = Encoding.ASCII.GetString(outcome.Admitted!.Payload.Span);
        Assert.Contains("Approved: moderator-a@example.com", text, StringComparison.Ordinal);
        Assert.Contains("Injection-Info:", text, StringComparison.Ordinal);
        Assert.Contains("Injection-Date:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M_CorrectModerator_WrongApproved_Returns441()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-b@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task M_WrongModerator_CorrectApproved_Returns441()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorB);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task M_WrongModerator_WrongApproved_Returns441()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: other@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorB);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task M_MalformedApproved_Returns441()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: 1\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task M_ConflictingApproved_Returns441()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-a@example.com\r\nApproved: moderator-b@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task M_IdenticalApprovedDuplicates_AreAccepted()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-a@example.com\r\nApproved: Moderator-A@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA);
        AssertAccepted(outcome);
    }

    [Fact]
    public async Task M_ApprovedHeaderNameCase_IsIgnored()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "aPpRoVeD: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA);
        AssertAccepted(outcome);
    }

    [Fact]
    public async Task M_ApprovedWhitespace_IsIgnored()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved:  moderator-a@example.com  \r\n",
            user: MapNntpAuthenticationProvider.ModeratorA);
        AssertAccepted(outcome);
    }

    [Fact]
    public async Task CrossPost_YY_Returns240()
    {
        var outcome = await PostAsync(
            "group.y,group.y2",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser);
        AssertAccepted(outcome);
    }

    [Fact]
    public async Task CrossPost_YM_NoApproved_ForwardsLeftmostModerated()
    {
        var submission = new RecordingModerationSubmissionService();
        var outcome = await PostAsync(
            "group.y,group.a",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            submission: submission);
        Assert.Equal("240 Article received OK", outcome.Response);
        Assert.Equal(0, outcome.Queue.TryAdmitCalls);
        var recorded = Assert.Single(submission.Submissions);
        Assert.Equal("group.a", recorded.TargetModeratedGroup);
        Assert.Equal("moderator-a@example.com", recorded.ModeratorAddress);
    }

    [Fact]
    public async Task CrossPost_YM_AuthorizedModerator_Returns240()
    {
        var outcome = await PostAsync(
            "group.y,group.a",
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA);
        AssertAccepted(outcome);
    }

    [Fact]
    public async Task CrossPost_MM_SameModerator_Returns240()
    {
        var authorization = ModeratorTestSnapshot.Create(
            ("group.a", "shared@example.com", MapNntpAuthenticationProvider.ModeratorA),
            ("group.shared", "shared@example.com", MapNntpAuthenticationProvider.ModeratorA));
        var snapshot = Snapshot(
            Allowed("group.y"),
            Group("group.a", NewsgroupPostingStatus.Moderated),
            Group("group.shared", NewsgroupPostingStatus.Moderated));
        var outcome = await PostAsync(
            "group.a,group.shared",
            extraHeaders: "Approved: shared@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA,
            snapshot: snapshot,
            authorization: authorization);
        AssertAccepted(outcome);
    }

    [Fact]
    public async Task CrossPost_MM_DifferentModerators_OneApproved_Returns441()
    {
        var outcome = await PostAsync(
            "group.a,group.b",
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task CrossPost_MM_DifferentModerators_NoApproved_ForwardsLeftmostOnly()
    {
        var submission = new RecordingModerationSubmissionService();
        var outcome = await PostAsync(
            "group.a,group.b",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            submission: submission);
        Assert.Equal("240 Article received OK", outcome.Response);
        var recorded = Assert.Single(submission.Submissions);
        Assert.Equal("group.a", recorded.TargetModeratedGroup);
        Assert.Equal("moderator-a@example.com", recorded.ModeratorAddress);
        Assert.Equal(0, outcome.Queue.TryAdmitCalls);
    }

    [Theory]
    [InlineData("group.a,group.x")]
    [InlineData("group.a,group.j")]
    [InlineData("group.a,group.n")]
    public async Task CrossPost_MWithRejectedStatus_Returns441(string groups)
    {
        var outcome = await PostAsync(groups, extraHeaders: "", user: MapNntpAuthenticationProvider.ModeratorA);
        AssertRejected(outcome);
    }

    [Theory]
    [InlineData("group.a,group.n")]
    [InlineData("group.a,group.x")]
    [InlineData("group.a,group.j")]
    public async Task CrossPost_ApprovedDoesNotBypassRejectedStatus(string groups)
    {
        var outcome = await PostAsync(
            groups,
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task UnresolvedModerator_Returns441_WithoutSubmission()
    {
        var submission = new RecordingModerationSubmissionService();
        var outcome = await PostAsync(
            "group.orphan",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            snapshot: Snapshot(Group("group.orphan", NewsgroupPostingStatus.Moderated)),
            submission: submission);
        AssertRejected(outcome);
        Assert.Empty(submission.Submissions);
    }

    [Fact]
    public async Task ModerationDeliveryFailure_Returns441()
    {
        var submission = new RecordingModerationSubmissionService(ModerationSubmissionStatus.Failed, "smtp failed");
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            submission: submission);
        AssertRejected(outcome);
        Assert.Single(submission.Submissions);
        Assert.Equal(0, outcome.Queue.TryAdmitCalls);
        Assert.Equal(0, outcome.History.RememberCalls);
    }

    [Fact]
    public async Task UnavailableSubmission_Returns441()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            submission: UnavailableModerationSubmissionService.Instance);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task ModeratorReinject_WithoutApproved_IsModerationPathNotInjection()
    {
        var submission = new RecordingModerationSubmissionService();
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.ModeratorA,
            submission: submission);
        Assert.Equal("240 Article received OK", outcome.Response);
        Assert.Equal(0, outcome.Queue.TryAdmitCalls);
        Assert.Single(submission.Submissions);
    }

    [Fact]
    public async Task ModeratorReinject_WrongApproved_Returns441()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-b@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task ApprovedValueSplitAcrossPipeSegments_IsAuthorized()
    {
        var article = Article("group.a", "Approved: moderator-a@example.com\r\n") + ".\r\n";
        var outcome = await PostRawAsync(
            article,
            user: MapNntpAuthenticationProvider.ModeratorA,
            chunkSize: 3);
        AssertAccepted(outcome);
    }

    [Fact]
    public async Task ApprovedHeaderNameSplitAcrossPipeSegments_IsAuthorized()
    {
        var article = Article("group.a", "Approved: moderator-a@example.com\r\n") + ".\r\n";
        var outcome = await PostRawAsync(
            article,
            user: MapNntpAuthenticationProvider.ModeratorA,
            chunkSize: 1);
        AssertAccepted(outcome);
    }

    [Fact]
    public async Task LargeModeratedArticle_DrainsAndSubmits()
    {
        var submission = new RecordingModerationSubmissionService();
        var body = new string('Z', 8000) + "\r\n";
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            submission: submission,
            body: body);
        Assert.Equal("240 Article received OK", outcome.Response);
        var recorded = Assert.Single(submission.Submissions);
        Assert.Contains("ZZZZ", Encoding.ASCII.GetString(recorded.ProtoArticle.Span), StringComparison.Ordinal);
        Assert.Equal(0, outcome.Queue.TryAdmitCalls);
    }

    [Fact]
    public async Task DotStuffedModeratedArticle_PreservesStuffingOnSubmission()
    {
        var submission = new RecordingModerationSubmissionService();
        _ = await PostAsync(
            "group.a",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            submission: submission,
            body: "..dot\r\n");
        var recorded = Assert.Single(submission.Submissions);
        var proto = Encoding.ASCII.GetString(recorded.ProtoArticle.Span);
        Assert.Contains("..dot\r\n", proto, StringComparison.Ordinal);
        var destuffed = Encoding.ASCII.GetString(
            ModerationEmailComposer.DestuffProtoArticle(recorded.ProtoArticle.Span));
        Assert.Contains(".dot\r\n", destuffed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringModerationPath_DoesNotWriteFinalResponse()
    {
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(
            NewQueue(),
            new RecordingHistoryDb(),
            new StaticNewsgroupCatalogue(DefaultSnapshot()),
            StandardAuthorization(),
            new RecordingModerationSubmissionService(),
            MapNntpAuthenticationProvider.CreateStandard());
        using var cts = new CancellationTokenSource();
        var run = session.RunAsync(cts.Token);
        _ = await duplex.ReadClientLineAsync();
        await AuthenticateAsync(duplex, MapNntpAuthenticationProvider.NormalUser, MapNntpAuthenticationProvider.NormalUserPassword);
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(Article("group.a"));
        await cts.CancelAsync();
        await run.WaitAsync(Safety);
    }

    [Theory]
    [InlineData("group.n")]
    [InlineData("group.x")]
    [InlineData("group.j")]
    public async Task Njx_ApprovedDoesNotBypassProhibitedStatus(string group)
    {
        var outcome = await PostAsync(
            group,
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task FailedAuthinfoPass_DoesNotLeaveModeratorIdentity_ApprovedReturns441()
    {
        await using var duplex = new PostDuplex();
        var queue = new RecordingIngestionQueue(NewQueue());
        var history = new RecordingHistoryDb();
        var session = duplex.CreateSession(
            queue,
            history,
            new StaticNewsgroupCatalogue(DefaultSnapshot()),
            StandardAuthorization(),
            new RecordingModerationSubmissionService(ModerationSubmissionStatus.Unavailable, "unused"),
            MapNntpAuthenticationProvider.CreateStandard());
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("AUTHINFO USER " + MapNntpAuthenticationProvider.ModeratorA);
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS not-the-moderator-password");
        Assert.StartsWith("481 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Null(session.Authentication.Username);
        Assert.False(session.Authorization.PostingPermitted);

        // Existing seam: posting privilege without an AUTHINFO principal (same as unauthenticated POST tests).
        session.SetAuthorization(MapNntpAuthenticationProvider.PosterPrivileges);
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Null(session.Authentication.Username);

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            Article("group.a", extraHeaders: "Approved: moderator-a@example.com\r\n") + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.TryAdmitCalls);
        Assert.Equal(0, history.PeekCalls);
        Assert.Equal(0, history.RememberCalls);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task AuthenticatedModerator_FailedReAuthinfo_IsRejectedAndIdentityPreserved()
    {
        await using var duplex = new PostDuplex();
        var queue = new RecordingIngestionQueue(NewQueue());
        var history = new RecordingHistoryDb();
        var session = duplex.CreateSession(
            queue,
            history,
            new StaticNewsgroupCatalogue(DefaultSnapshot()),
            StandardAuthorization(),
            new RecordingModerationSubmissionService(ModerationSubmissionStatus.Unavailable, "unused"),
            MapNntpAuthenticationProvider.CreateStandard());
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await AuthenticateAsync(
            duplex,
            MapNntpAuthenticationProvider.ModeratorA,
            MapNntpAuthenticationProvider.ModeratorAPassword);

        await duplex.WriteClientLineAsync("AUTHINFO USER " + MapNntpAuthenticationProvider.ModeratorA);
        Assert.StartsWith("502 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS not-the-moderator-password");
        Assert.StartsWith("502 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(session.Authentication.IsAuthenticated);
        Assert.Equal(MapNntpAuthenticationProvider.ModeratorA, session.Authentication.Username);

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            Article("group.a", extraHeaders: "Approved: moderator-a@example.com\r\n") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.TryAdmitCalls);
        Assert.Equal(1, history.PeekCalls);
        Assert.Equal(1, history.RememberCalls);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task MessageId_LookingLikeModerator_DoesNotAuthorize()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: string.Empty,
            user: MapNntpAuthenticationProvider.NormalUser,
            messageId: "<moderator-a@example.com>");
        AssertRejected(outcome);
    }

    [Fact]
    public async Task ModeratorA_CannotApproveModeratorBGroup()
    {
        var outcome = await PostAsync(
            "group.b",
            extraHeaders: "Approved: moderator-b@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task AuthenticatedModerator_DoesNotGrantControlCancel()
    {
        await using var duplex = new PostDuplex();
        var queue = new RecordingIngestionQueue(NewQueue());
        var history = new RecordingHistoryDb();
        var session = duplex.CreateSession(
            queue,
            history,
            new StaticNewsgroupCatalogue(DefaultSnapshot()),
            StandardAuthorization(),
            new RecordingModerationSubmissionService(ModerationSubmissionStatus.Unavailable, "unused"),
            MapNntpAuthenticationProvider.CreateStandard());
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await AuthenticateAsync(duplex, MapNntpAuthenticationProvider.ModeratorA, PasswordFor(MapNntpAuthenticationProvider.ModeratorA));
        Assert.False(session.Authorization.ControlCancelPermitted);

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            Article(
                "group.a",
                extraHeaders: "Approved: moderator-a@example.com\r\nControl: cancel <victim@example.com>\r\n")
            + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.TryAdmitCalls);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Post_DoesNotQueryModeratorRepository()
    {
        var repository = new MemoryNntpModeratorRepository();
        repository.Add(new MemoryModeratorRecord
        {
            ModeratorId = 1,
            GroupPattern = "group.a",
            ModeratorAddress = "moderator-a@example.com",
            AccountName = MapNntpAuthenticationProvider.ModeratorA,
        });
        await using var catalogue = new ModeratorCatalogueService(
            repository,
            NullLogger<ModeratorCatalogueService>.Instance,
            TimeProvider.System,
            TimeSpan.FromHours(1));
        await catalogue.StartAsync(CancellationToken.None);
        Assert.Equal(1, repository.QueryCount);

        await using var duplex = new PostDuplex();
        var queue = new RecordingIngestionQueue(NewQueue());
        var history = new RecordingHistoryDb();
        var session = duplex.CreateSession(
            queue,
            history,
            new StaticNewsgroupCatalogue(DefaultSnapshot()),
            catalogue.Current,
            new RecordingModerationSubmissionService(ModerationSubmissionStatus.Accepted),
            MapNntpAuthenticationProvider.CreateStandard(),
            catalogue);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await AuthenticateAsync(duplex, MapNntpAuthenticationProvider.ModeratorA, PasswordFor(MapNntpAuthenticationProvider.ModeratorA));

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            Article("group.a", extraHeaders: "Approved: moderator-a@example.com\r\n") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            Article("group.a", extraHeaders: "Approved: moderator-a@example.com\r\n", messageId: "<second@example.com>") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());
        Assert.Equal(1, repository.QueryCount);

        await catalogue.StopAsync(CancellationToken.None);
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task From_CannotImpersonateModerator_NormalUser_Returns441()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.NormalUser,
            from: "moderator-a@example.com");
        AssertRejected(outcome);
    }

    [Fact]
    public async Task From_MatchingMailbox_SucceedsOnlyForAuthenticatedModerator()
    {
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA,
            from: "moderator-a@example.com");
        AssertAccepted(outcome);
    }

    [Fact]
    public async Task PgpAuthorities_DoNotAuthorizeOrdinaryModeratedPost()
    {
        var control = new ControlOptions
        {
            PgpAuthorities = new PgpAuthoritiesOptions
            {
                Authorities =
                [
                    new PgpAuthorityOptions
                    {
                        Name = "TEST-HIERARCHY",
                        Contact = "moderator-a@example.com",
                        Authorizations =
                        [
                            new PgpAuthorityAuthorizationOptions
                            {
                                From = "moderator-a@example.com",
                                Message = "newgroup",
                                Newsgroups = "group.a",
                                VerificationIdentity = "moderator-a@example.com",
                            },
                        ],
                    },
                ],
            },
        };
        Assert.Equal("moderator-a@example.com", control.PgpAuthorities.Authorities[0].Authorizations[0].From);
        Assert.True(new ControlOptionsValidator().Validate(null, control).Succeeded);

        var authorization = ModeratorTestSnapshot.Create(
            ("group.a", "moderator-a@example.com", MapNntpAuthenticationProvider.ModeratorA));
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.NormalUser,
            authorization: authorization);
        AssertRejected(outcome);
    }

    [Fact]
    public async Task SessionAuthentication_IsIsolated_ModeratorThenNormal()
    {
        await AssertIsolatedSessionsAsync(
            firstUser: MapNntpAuthenticationProvider.ModeratorA,
            firstExpected: "240 Article received OK",
            secondUser: MapNntpAuthenticationProvider.NormalUser,
            secondExpected: "441 Posting failed");
    }

    [Fact]
    public async Task SessionAuthentication_IsIsolated_NormalThenModerator()
    {
        await AssertIsolatedSessionsAsync(
            firstUser: MapNntpAuthenticationProvider.NormalUser,
            firstExpected: "441 Posting failed",
            secondUser: MapNntpAuthenticationProvider.ModeratorA,
            secondExpected: "240 Article received OK");
    }

    [Fact]
    public async Task MessageId_UnapprovedPreserved_ThenReinjectsSameId()
    {
        const string originalId = "<original@example.com>";
        var submission = new RecordingModerationSubmissionService();
        var unapproved = await PostAsync(
            "group.a",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            submission: submission,
            messageId: originalId);
        Assert.Equal("240 Article received OK", unapproved.Response);
        Assert.Equal(0, unapproved.Queue.TryAdmitCalls);
        Assert.Equal(0, unapproved.History.PeekCalls);
        var recorded = Assert.Single(submission.Submissions);
        Assert.Equal(originalId, recorded.MessageId);
        var proto = Encoding.ASCII.GetString(recorded.ProtoArticle.Span);
        Assert.Equal(1, CountMessageIdHeaders(proto));
        Assert.Contains("Message-ID: " + originalId, proto, StringComparison.Ordinal);

        var reinject = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA,
            messageId: originalId);
        AssertAccepted(reinject);
        Assert.Equal(originalId, reinject.Admitted!.MessageId);
        Assert.Equal([originalId], reinject.History.PeekedMessageIds);
        Assert.Equal([originalId], reinject.History.RememberedMessageIds);
        var injected = Encoding.ASCII.GetString(reinject.Admitted.Payload.Span);
        Assert.Equal(1, CountMessageIdHeaders(injected));
        Assert.Contains("Message-ID: " + originalId, injected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InnRoutingTemplate_UnapprovedUsesExpandedAddress_DoesNotAuthorize()
    {
        var authorization = ModeratorTestSnapshot.Create(
            ("fido7.*", "%s@fido7.org", string.Empty),
            ("perl.*", "news-moderator-%s@perl.org", string.Empty),
            ("*", "%s@moderators.isc.org", string.Empty));
        var snapshot = Snapshot(
            Group("fido7.some.group", NewsgroupPostingStatus.Moderated),
            Group("perl.foo.bar", NewsgroupPostingStatus.Moderated),
            Group("comp.test", NewsgroupPostingStatus.Moderated));
        var submission = new RecordingModerationSubmissionService();
        var unapproved = await PostAsync(
            "fido7.some.group",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            snapshot: snapshot,
            authorization: authorization,
            submission: submission);
        Assert.Equal("240 Article received OK", unapproved.Response);
        var recorded = Assert.Single(submission.Submissions);
        Assert.Equal("fido7-some-group@fido7.org", recorded.ModeratorAddress);
        Assert.Equal(0, unapproved.Queue.TryAdmitCalls);

        var perlSubmission = new RecordingModerationSubmissionService();
        var perl = await PostAsync(
            "perl.foo.bar",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            snapshot: snapshot,
            authorization: authorization,
            submission: perlSubmission);
        Assert.Equal("240 Article received OK", perl.Response);
        Assert.Equal("news-moderator-perl-foo-bar@perl.org", Assert.Single(perlSubmission.Submissions).ModeratorAddress);

        var catchAll = new RecordingModerationSubmissionService();
        _ = await PostAsync(
            "comp.test",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            snapshot: snapshot,
            authorization: authorization,
            submission: catchAll);
        Assert.Equal("comp-test@moderators.isc.org", Assert.Single(catchAll.Submissions).ModeratorAddress);

        var approved = await PostAsync(
            "fido7.some.group",
            extraHeaders: "Approved: fido7-some-group@fido7.org\r\n",
            user: MapNntpAuthenticationProvider.NormalUser,
            snapshot: snapshot,
            authorization: authorization);
        AssertRejected(approved);
    }

    [Fact]
    public async Task EmailQueue_UnapprovedInnRoute_Returns240WithoutSmtpOnPost()
    {
        using var harness = new EmailSpoolHarness();
        var submission = EmailModerationSubmissionServiceTests.Create(harness, enabled: true);
        var authorization = ModeratorTestSnapshot.Create(("fido7.*", "%s@fido7.org", string.Empty));
        var snapshot = Snapshot(Group("fido7.some.group", NewsgroupPostingStatus.Moderated));
        var outcome = await PostAsync(
            "fido7.some.group",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            snapshot: snapshot,
            authorization: authorization,
            submission: submission);
        Assert.Equal("240 Article received OK", outcome.Response);
        Assert.Equal(0, outcome.Queue.TryAdmitCalls);
        Assert.Single(harness.EmlFiles());
        var bytes = await File.ReadAllBytesAsync(harness.EmlFiles()[0]);
        Assert.True(EmailSpoolRecord.TryParse(bytes, out var item));
        Assert.Equal("fido7-some-group@fido7.org", item!.Recipients[0].Address);
        var encoded = Encoding.ASCII.GetString(item.EncodedMessage.Span);
        Assert.Contains("application/news-transmission; usage=moderate", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("Injection-Info:", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("Injection-Date:", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Trace:", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmailDisabled_Unapproved_Returns441()
    {
        using var harness = new EmailSpoolHarness(enabled: false);
        var submission = EmailModerationSubmissionServiceTests.Create(harness, enabled: false);
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            submission: submission);
        AssertRejected(outcome);
        Assert.Empty(harness.EmlFiles());
    }

    [Fact]
    public async Task EmailEnabled_AuthorizedReinject_DoesNotEnqueue()
    {
        using var harness = new EmailSpoolHarness();
        var submission = EmailModerationSubmissionServiceTests.Create(harness, enabled: true);
        var outcome = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA,
            submission: submission);
        AssertAccepted(outcome);
        Assert.Empty(harness.EmlFiles());
        Assert.Equal(1, outcome.Queue.TryAdmitCalls);
    }

    [Fact]
    public async Task EmailSpoolWriteFailure_Unapproved_Returns441()
    {
        var blocker = Path.Join(Path.GetTempPath(), "vectornntp-post-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(blocker, "x"u8.ToArray());
        try
        {
            var options = EmailModerationSubmissionServiceTests.EnabledOptions();
            options.Spool.Directory = blocker;
            var spool = new FilesystemEmailSpool(
                Microsoft.Extensions.Options.Options.Create(options),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FilesystemEmailSpool>.Instance);
            var submission = new EmailModerationSubmissionService(
                new EmailService(
                    spool,
                    new Rfc5322MessageEncoder(),
                    Microsoft.Extensions.Options.Options.Create(options),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailService>.Instance),
                new ModerationEmailComposer(Microsoft.Extensions.Options.Options.Create(options)),
                Microsoft.Extensions.Options.Options.Create(options));
            var outcome = await PostAsync(
                "group.a",
                extraHeaders: "",
                user: MapNntpAuthenticationProvider.NormalUser,
                submission: submission);
            AssertRejected(outcome);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task MessageId_SynthesizedOnce_ThenReinjectPreservesSameId()
    {
        var submission = new RecordingModerationSubmissionService();
        var unapproved = await PostAsync(
            "group.a",
            extraHeaders: "",
            user: MapNntpAuthenticationProvider.NormalUser,
            submission: submission,
            messageId: null);
        Assert.Equal("240 Article received OK", unapproved.Response);
        var recorded = Assert.Single(submission.Submissions);
        Assert.False(string.IsNullOrEmpty(recorded.MessageId));
        Assert.StartsWith("<", recorded.MessageId, StringComparison.Ordinal);
        Assert.EndsWith("@usenet.ninja>", recorded.MessageId, StringComparison.Ordinal);
        var proto = Encoding.ASCII.GetString(recorded.ProtoArticle.Span);
        Assert.Equal(1, CountMessageIdHeaders(proto));
        Assert.Contains("Message-ID: " + recorded.MessageId, proto, StringComparison.Ordinal);

        var reinject = await PostAsync(
            "group.a",
            extraHeaders: "Approved: moderator-a@example.com\r\n",
            user: MapNntpAuthenticationProvider.ModeratorA,
            messageId: recorded.MessageId);
        AssertAccepted(reinject);
        Assert.Equal(recorded.MessageId, reinject.Admitted!.MessageId);
        Assert.Equal([recorded.MessageId], reinject.History.PeekedMessageIds);
        var injected = Encoding.ASCII.GetString(reinject.Admitted.Payload.Span);
        Assert.Equal(1, CountMessageIdHeaders(injected));
        Assert.Contains("Message-ID: " + recorded.MessageId, injected, StringComparison.Ordinal);
    }

    private static void AssertAccepted(PostOutcome outcome)
    {
        Assert.Equal("240 Article received OK", outcome.Response);
        Assert.Equal(1, outcome.Queue.TryAdmitCalls);
        Assert.Equal(1, outcome.History.PeekCalls);
        Assert.Equal(1, outcome.History.RememberCalls);
        Assert.NotNull(outcome.Admitted);
    }

    private static void AssertRejected(PostOutcome outcome)
    {
        Assert.Equal("441 Posting failed", outcome.Response);
        Assert.Equal(0, outcome.Queue.TryAdmitCalls);
        Assert.Equal(0, outcome.History.PeekCalls);
        Assert.Equal(0, outcome.History.RememberCalls);
        Assert.Null(outcome.Admitted);
    }

    private static async Task AssertIsolatedSessionsAsync(
        string firstUser,
        string firstExpected,
        string secondUser,
        string secondExpected)
    {
        var authorization = StandardAuthorization();
        var catalogue = new StaticNewsgroupCatalogue(DefaultSnapshot());
        var provider = MapNntpAuthenticationProvider.CreateStandard();
        var firstQueue = new RecordingIngestionQueue(NewQueue());
        var secondQueue = new RecordingIngestionQueue(NewQueue());
        var firstHistory = new RecordingHistoryDb();
        var secondHistory = new RecordingHistoryDb();
        await using var firstDuplex = new PostDuplex();
        await using var secondDuplex = new PostDuplex();
        var firstSession = firstDuplex.CreateSession(
            firstQueue,
            firstHistory,
            catalogue,
            authorization,
            new RecordingModerationSubmissionService(ModerationSubmissionStatus.Unavailable, "unused"),
            provider);
        var secondSession = secondDuplex.CreateSession(
            secondQueue,
            secondHistory,
            catalogue,
            authorization,
            new RecordingModerationSubmissionService(ModerationSubmissionStatus.Unavailable, "unused"),
            provider);
        var firstRun = firstSession.RunAsync();
        var secondRun = secondSession.RunAsync();
        _ = await firstDuplex.ReadClientLineAsync();
        _ = await secondDuplex.ReadClientLineAsync();
        await AuthenticateAsync(firstDuplex, firstUser, PasswordFor(firstUser));
        await AuthenticateAsync(secondDuplex, secondUser, PasswordFor(secondUser));
        Assert.Equal(firstUser, firstSession.Authentication.Username);
        Assert.Equal(secondUser, secondSession.Authentication.Username);

        await firstDuplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await firstDuplex.ReadClientLineAsync());
        await firstDuplex.WriteClientAsync(
            Article("group.a", extraHeaders: "Approved: moderator-a@example.com\r\n") + ".\r\n");
        Assert.Equal(firstExpected, await firstDuplex.ReadClientLineAsync());

        await secondDuplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await secondDuplex.ReadClientLineAsync());
        await secondDuplex.WriteClientAsync(
            Article("group.a", extraHeaders: "Approved: moderator-a@example.com\r\n") + ".\r\n");
        Assert.Equal(secondExpected, await secondDuplex.ReadClientLineAsync());

        Assert.NotEqual(firstSession.Authentication.Username, secondSession.Authentication.Username);
        if (firstExpected.StartsWith("240", StringComparison.Ordinal))
        {
            Assert.Equal(1, firstQueue.TryAdmitCalls);
        }
        else
        {
            Assert.Equal(0, firstQueue.TryAdmitCalls);
            Assert.Equal(0, firstHistory.PeekCalls);
            Assert.Equal(0, firstHistory.RememberCalls);
        }

        if (secondExpected.StartsWith("240", StringComparison.Ordinal))
        {
            Assert.Equal(1, secondQueue.TryAdmitCalls);
        }
        else
        {
            Assert.Equal(0, secondQueue.TryAdmitCalls);
            Assert.Equal(0, secondHistory.PeekCalls);
            Assert.Equal(0, secondHistory.RememberCalls);
        }

        await firstDuplex.WriteClientLineAsync("QUIT");
        await secondDuplex.WriteClientLineAsync("QUIT");
        _ = await firstDuplex.ReadClientLineAsync();
        _ = await secondDuplex.ReadClientLineAsync();
        await firstRun;
        await secondRun;
    }

    private static async Task<PostOutcome> PostAsync(
        string newsgroups,
        string extraHeaders,
        string? user,
        IModerationSubmissionService? submission = null,
        NewsgroupSnapshot? snapshot = null,
        IModeratorAuthorization? authorization = null,
        string body = "body\r\n",
        string from = "poster@example.com",
        string? messageId = "<ok@example.com>",
        IPostFilter? postFilter = null,
        INewsLogWriter? newsLog = null) =>
        await PostRawAsync(
            Article(newsgroups, extraHeaders, body, from, messageId) + ".\r\n",
            user,
            submission,
            snapshot,
            authorization,
            postFilter: postFilter,
            newsLog: newsLog);

    private static async Task<PostOutcome> PostRawAsync(
        string stuffedArticle,
        string? user,
        IModerationSubmissionService? submission = null,
        NewsgroupSnapshot? snapshot = null,
        IModeratorAuthorization? authorization = null,
        int? chunkSize = null,
        IPostFilter? postFilter = null,
        INewsLogWriter? newsLog = null)
    {
        var queue = new RecordingIngestionQueue(NewQueue());
        var history = new RecordingHistoryDb();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(
            queue,
            history,
            new StaticNewsgroupCatalogue(snapshot ?? DefaultSnapshot()),
            authorization ?? StandardAuthorization(),
            submission ?? new RecordingModerationSubmissionService(ModerationSubmissionStatus.Unavailable, "unused"),
            user is null ? null : MapNntpAuthenticationProvider.CreateStandard(),
            postFilter: postFilter,
            newsLog: newsLog);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        if (user is not null)
        {
            await AuthenticateAsync(duplex, user, PasswordFor(user));
        }
        else
        {
            session.SetAuthorization(MapNntpAuthenticationProvider.PosterPrivileges);
        }

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        if (chunkSize is int size)
        {
            await duplex.WriteClientChunkedAsync(stuffedArticle, size);
        }
        else
        {
            await duplex.WriteClientAsync(stuffedArticle);
        }

        var response = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
        return new PostOutcome(response, queue, history, queue.Admitted.Count == 0 ? null : queue.Admitted[0]);
    }

    private static async Task AuthenticateAsync(PostDuplex duplex, string username, string password)
    {
        await duplex.WriteClientLineAsync("AUTHINFO USER " + username);
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS " + password);
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    private static string PasswordFor(string username) => username switch
    {
        MapNntpAuthenticationProvider.NormalUser => MapNntpAuthenticationProvider.NormalUserPassword,
        MapNntpAuthenticationProvider.ModeratorA => MapNntpAuthenticationProvider.ModeratorAPassword,
        MapNntpAuthenticationProvider.ModeratorB => MapNntpAuthenticationProvider.ModeratorBPassword,
        _ => throw new ArgumentOutOfRangeException(nameof(username)),
    };

    private static ArticleIngestionQueue NewQueue() =>
        new(new ArticleIngestionOptions { QueueCapacity = 8, MaxArticleBytes = NntpdOptions.DefaultMaxArticleSize });

    private static IModeratorAuthorization StandardAuthorization() =>
        ModeratorTestSnapshot.Create(
            ("group.a", "moderator-a@example.com", MapNntpAuthenticationProvider.ModeratorA),
            ("group.b", "moderator-b@example.com", MapNntpAuthenticationProvider.ModeratorB));

    private static NewsgroupSnapshot DefaultSnapshot() =>
        Snapshot(
            Allowed("group.y"),
            Allowed("group.y2"),
            Group("group.n", NewsgroupPostingStatus.Prohibited),
            Group("group.x", NewsgroupPostingStatus.NoPostingOrPeerArticles),
            Group("group.j", NewsgroupPostingStatus.PeerOnly),
            Group("group.a", NewsgroupPostingStatus.Moderated),
            Group("group.b", NewsgroupPostingStatus.Moderated),
            Group("group.orphan", NewsgroupPostingStatus.Moderated),
            Group("group.shared", NewsgroupPostingStatus.Moderated));

    private static NewsgroupSnapshot Snapshot(params NewsgroupDefinition[] definitions) =>
        NewsgroupSnapshot.Create(definitions);

    private static NewsgroupDefinition Allowed(string name) => Group(name, NewsgroupPostingStatus.Allowed);

    private static NewsgroupDefinition Group(string name, NewsgroupPostingStatus status) =>
        new(name, string.Empty, 2, 1, status);

    private static string Article(
        string newsgroups,
        string extraHeaders = "",
        string body = "body\r\n",
        string from = "poster@example.com",
        string? messageId = "<ok@example.com>")
    {
        var messageIdLine = messageId is null ? string.Empty : "Message-ID: " + messageId + "\r\n";
        return
            "Date: " + PostRfcDate.Format(DateTimeOffset.UtcNow) + "\r\n" +
            "From: " + from + "\r\n" +
            "Newsgroups: " + newsgroups + "\r\n" +
            "Subject: test\r\n" +
            messageIdLine +
            extraHeaders +
            "\r\n" +
            body;
    }

    private static int CountMessageIdHeaders(string article)
    {
        var count = 0;
        var index = 0;
        while (index < article.Length)
        {
            var found = article.IndexOf("Message-ID:", index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            count++;
            index = found + "Message-ID:".Length;
        }

        return count;
    }

    private sealed record PostOutcome(
        string Response,
        RecordingIngestionQueue Queue,
        RecordingHistoryDb History,
        InboundArticle? Admitted);

    private sealed class RecordingHistoryDb : IHistoryDb
    {
        public int PeekCalls { get; private set; }

        public int RememberCalls { get; private set; }

        public List<string> PeekedMessageIds { get; } = [];

        public List<string> RememberedMessageIds { get; } = [];

        public ValueTask<HistoryLookupResult> LookupAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            new(HistoryLookupResult.Unseen);

        public ValueTask<HistoryLookupResult> PeekAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default)
        {
            PeekCalls++;
            PeekedMessageIds.Add(Encoding.ASCII.GetString(messageId.Span));
            return new(HistoryLookupResult.Unseen);
        }

        public void Remember(ReadOnlyMemory<byte> messageId)
        {
            RememberCalls++;
            RememberedMessageIds.Add(Encoding.ASCII.GetString(messageId.Span));
        }

        public bool ContainsLocal(in HistoryDigest digest) => false;
    }

    private sealed class RecordingIngestionQueue : IArticleIngestionQueue
    {
        private readonly IArticleIngestionQueue _inner;

        public RecordingIngestionQueue(IArticleIngestionQueue inner)
        {
            _inner = inner;
        }

        public int TryAdmitCalls { get; private set; }

        public List<InboundArticle> Admitted { get; } = [];

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
            TryAdmitCalls++;
            var result = _inner.TryAdmit(article);
            if (result == ArticleEnqueueResult.Accepted)
            {
                Admitted.Add(article);
            }

            return result;
        }

        public bool TryEnqueue(InboundArticle article) => _inner.TryEnqueue(article);

        public void Complete() => _inner.Complete();

        public ValueTask<InboundArticle?> DequeueAsync(CancellationToken cancellationToken) =>
            _inner.DequeueAsync(cancellationToken);
    }

    private sealed class PostDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public NntpSession CreateSession(
            IArticleIngestionQueue queue,
            IHistoryDb historyDb,
            INewsgroupCatalogue catalogue,
            IModeratorAuthorization authorization,
            IModerationSubmissionService submission,
            INntpAuthenticationProvider? authenticationProvider,
            IModeratorCatalogue? moderatorCatalogue = null,
            IPostFilter? postFilter = null,
            INewsLogWriter? newsLog = null)
        {
            var connection = new PipeConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                authenticationProvider: authenticationProvider,
                articleIngestion: queue,
                historyDb: historyDb,
                postingTraceProtector: AesGcmPostingTraceProtector.Create(
                    new NntpdOptions { XTraceKey = TestHostFactory.TestXTraceKey }),
                newsgroupCatalogue: catalogue,
                moderatorCatalogue: moderatorCatalogue,
                moderatorAuthorization: authorization,
                moderationSubmission: submission,
                postFilter: postFilter,
                newsLog: newsLog);
        }

        public async Task WriteClientLineAsync(string line) => await WriteClientAsync(line + "\r\n");

        public async Task WriteClientAsync(string payload)
        {
            await _clientToServer.Writer.WriteAsync(Encoding.ASCII.GetBytes(payload));
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task WriteClientChunkedAsync(string payload, int chunkSize)
        {
            var bytes = Encoding.ASCII.GetBytes(payload);
            for (var i = 0; i < bytes.Length; i += chunkSize)
            {
                var length = Math.Min(chunkSize, bytes.Length - i);
                await _clientToServer.Writer.WriteAsync(bytes.AsMemory(i, length));
                await _clientToServer.Writer.FlushAsync();
            }
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
}
