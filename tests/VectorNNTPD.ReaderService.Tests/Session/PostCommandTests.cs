using System.IO.Hashing;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.History;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Session.Commands.Posting;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.Session;

public sealed class PostCommandTests
{
    private static readonly NntpAuthorization Poster = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: true,
        streamingPermitted: false);

    private static readonly NntpAuthorization ReaderNoPost = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: false,
        streamingPermitted: false);

    private static readonly NntpAuthorization Newsmaster = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: true,
        streamingPermitted: false,
        controlCancelPermitted: true);

    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Unauthenticated_Returns480_AndDoesNotReadArticle()
    {
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task PostingProhibited_Returns440_AndDoesNotReadArticle()
    {
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(authorization: ReaderNoPost);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("440 Posting not permitted", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task OrdinaryPoster_ControlHeader_Returns441_AndDoesNotEnqueue()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            ValidArticle(extraHeaders: "Control: cancel <victim@example.com>\r\n") + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task Newsmaster_WellFormedCancel_Returns240_AndKeepsDistinctMessageId()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Newsmaster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            ValidArticle(
                messageId: "<cancel-article@example.com>",
                extraHeaders: "Control: cancel <original@example.com>\r\n") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        using var cts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(cts.Token);
        Assert.NotNull(inbound);
        Assert.Equal("<cancel-article@example.com>", inbound!.MessageId);
        var text = Encoding.ASCII.GetString(inbound.Payload.Span);
        Assert.Contains("Control: cancel <original@example.com>", text, StringComparison.Ordinal);
        Assert.Contains("Message-ID: <cancel-article@example.com>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Message-ID: <original@example.com>", text, StringComparison.Ordinal);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task Newsmaster_OtherControlVerb_Returns441()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Newsmaster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(ValidArticle(extraHeaders: "Control: newgroup misc.test\r\n") + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task AuthenticatedOrdinaryUser_CannotSubmitControl()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, authenticationProvider: new DualAuthProvider());
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await AuthenticateAsync(duplex, "poster", "poster-secret");
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            ValidArticle(extraHeaders: "Control: cancel <victim@example.com>\r\n") + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(ValidArticle(extraHeaders: "Control: newgroup misc.test\r\n") + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task AuthenticatedOrdinaryUser_XTraceContainsSessionUsername()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, authenticationProvider: new DualAuthProvider());
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await AuthenticateAsync(duplex, "poster+tag/name", "poster-secret");
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            ValidArticle(extraHeaders: "X-Authenticated-User: spoofed\r\n") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        using var timeoutCts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(timeoutCts.Token);
        var text = Encoding.ASCII.GetString(inbound!.Payload.Span);
        var xtrace = RequireHeader(text, "X-Trace");
        Assert.DoesNotContain("poster+tag/name", text, StringComparison.Ordinal);
        Assert.DoesNotContain("spoofed", xtrace, StringComparison.Ordinal);
        Assert.True(session.PostingTraceProtector!.TryUnprotect(xtrace, out var recovered));
        Assert.Equal("poster+tag/name", recovered.AuthenticatedUsername);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task AuthenticatedNewsmaster_CancelXTraceContainsUsername_OtherControlRejected()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, authenticationProvider: new DualAuthProvider());
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await AuthenticateAsync(duplex, "newsmaster", "unit-test-newsmaster-password");
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            ValidArticle(
                messageId: "<cancel-article@example.com>",
                extraHeaders: "Control: cancel <original@example.com>\r\n") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        using var dequeueCts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        var text = Encoding.ASCII.GetString(inbound!.Payload.Span);
        var xtrace = RequireHeader(text, "X-Trace");
        Assert.DoesNotContain("newsmaster", text.Replace("X-Trace: " + xtrace, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(session.PostingTraceProtector!.TryUnprotect(xtrace, out var recovered));
        Assert.Equal("newsmaster", recovered.AuthenticatedUsername);

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(ValidArticle(extraHeaders: "Control: newgroup misc.test\r\n") + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(ValidArticle(extraHeaders: "Control: cancel not-an-id\r\n") + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            ValidArticle(extraHeaders: "Control: cancel <a@example.com> extra\r\n") + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task ExtraArgument_Is501()
    {
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(authorization: Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST extra");
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task ValidArticle_Returns240_AndEnqueuesNormalizedArticle()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(ValidArticle(messageId: "<keep@example.com>") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        using var cts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(cts.Token);
        Assert.NotNull(inbound);
        Assert.Equal("<keep@example.com>", inbound!.MessageId);
        Assert.Equal(InboundArticleProducer.Post, inbound.Producer);
        var text = Encoding.ASCII.GetString(inbound.Payload.Span);
        Assert.Contains("Message-ID: <keep@example.com>", text, StringComparison.Ordinal);
        Assert.Equal(ArticleParseStatus.CanonicalV1, inbound.Record.ParseStatus);
        Assert.True(inbound.Payload.Equals(inbound.Record.ArtData));
        Assert.Equal(inbound.Record.ArtData.Length, inbound.Record.ArtSize);
        Assert.True(inbound.Record.ArtLines >= 0);
        Assert.NotEqual(ArticleType.None, inbound.Record.ArtType);
        Assert.True(inbound.Record.MessageId.SequenceEqual("<keep@example.com>"u8));
        Assert.True(inbound.Record.Fields.MessageId.IsPresent);
        Assert.True(inbound.Record.Fields.Newsgroups.IsPresent);
        Assert.Equal(ArticleId.FromMessageId("<keep@example.com>"u8), inbound.Record.ArtId);
        Assert.Equal(XxHash3.HashToUInt64(inbound.Record.ArtData.Span), inbound.Record.ArtHash);
        Assert.Contains("news.usenet.ninja!", text, StringComparison.Ordinal);
        Assert.Contains(".POSTED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Path: client.path", text, StringComparison.Ordinal);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task RejectedArticle_Returns441_AndDoesNotEnqueue()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(ValidArticle(includeSubject: false) + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task CannotBePipelined_SecondCommandIsNotExecutedAsCommand()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync("POST\r\nDATE\r\n.\r\n");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task ConfigurableLimit_RejectsDuringReceive()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster, maxArticleSize: 256);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(PaddedArticle(300) + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task ReceiveSizeRejection_WritesRejectedNews()
    {
        var news = new RecordingNewsLogWriter();
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster, maxArticleSize: 256, newsLog: news);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(PaddedArticle(300) + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Rejected, evt.Disposition);
        Assert.Equal(441, evt.ResponseCode);
        Assert.Equal(IngressNewsReasons.ArticleTooLarge, Encoding.ASCII.GetString(evt.Reason.Span));

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task NewsWriterFailure_On441_DoesNotChangeRejectionResponse()
    {
        var news = new ThrowingNewsLogWriter();
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster, maxArticleSize: 256, newsLog: news);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(PaddedArticle(300) + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        Assert.Equal(1, news.WriteCalls);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task ExactConfiguredLimit_IsAccepted()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster, maxArticleSize: 2048);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var destuffed = PaddedArticle(2048);
        Assert.Equal(2048, Encoding.ASCII.GetByteCount(destuffed));
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(destuffed + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);
        using var dequeueCts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        Assert.Equal(ArticleParseStatus.CanonicalV1, inbound!.Record.ParseStatus);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task DefaultFiveMibPlusOne_IsRejected()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(PaddedArticle(NntpdOptions.DefaultMaxArticleSize + 1) + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task DefaultFiveMibExact_IsRejectedByArticleRecordBecauseCompleteArticleExceedsCommonLimit()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var destuffed = PaddedArticle(NntpdOptions.DefaultMaxArticleSize);
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(destuffed + ".\r\n");
        // Receive accepts destuffed client size == MaxArticleSize. POST then injects
        // server headers and the factory applies Common MaxArticleBytes to the
        // destuffed complete article, which exceeds 5 MiB.
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task NearDefaultFiveMib_WithRoomForServerHeaders_IsAccepted()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var destuffed = PaddedArticle(NntpdOptions.DefaultMaxArticleSize - 4096);
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(destuffed + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);
        using var dequeueCts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        Assert.Equal(ArticleParseStatus.CanonicalV1, inbound!.Record.ParseStatus);
        Assert.True(inbound.Payload.Equals(inbound.Record.ArtData));

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task MissingMessageId_IsSynthesizedAndStableOnAcceptedArticle()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle(messageId: null) + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        using var dequeueCts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        Assert.Matches(@"^<[0-9a-f]{32}@usenet\.ninja>$", inbound!.MessageId);
        var text = Encoding.ASCII.GetString(inbound.Payload.Span);
        Assert.Contains("Message-ID: " + inbound.MessageId, text, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(text, "Message-ID:"));

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task DuplicateMessageId_Returns441_WithoutSecondAdmit()
    {
        var redis = new FakeRedisService();
        var history = new HistoryDb(
            redis,
            TimeSpan.FromHours(2),
            NullLogger<HistoryDb>.Instance,
            TimeProvider.System,
            new HistoryWriteQueue());
        history.Remember("<dup@example.com>"u8.ToArray());
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster, historyDb: history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle(messageId: "<dup@example.com>") + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task ForgedServerHeaders_AreReplaced_AndDateIsPreserved()
    {
        var queue = NewQueue();
        var clock = new ControllableTimeProvider();
        clock.Advance(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero) - DateTimeOffset.UnixEpoch);
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster, timeProvider: clock);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var clientDate = "24 Sep 2026 08:00:00 +0000";
        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(
            ValidArticle(
                date: clientDate,
                extraHeaders:
                    "Path: evil.path\r\n" +
                    "Injection-Date: forged-date\r\n" +
                    "Injection-Info: forged-info\r\n" +
                    "NNTP-Posting-Date: forged-nntp-date\r\n" +
                    "NNTP-Posting-Host: 203.0.113.9\r\n" +
                    "X-Trace: forged-trace\r\n" +
                    "Xref: other.server group:1\r\n") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        using var dequeueCts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        var text = Encoding.ASCII.GetString(inbound!.Payload.Span);
        var expectedInjection = PostRfcDate.Format(clock.GetUtcNow());
        Assert.Contains("Date: Thu, 24 Sep 2026 08:00:00 +0000", text, StringComparison.Ordinal);
        Assert.Contains("Injection-Date: " + expectedInjection, text, StringComparison.Ordinal);
        Assert.DoesNotContain("NNTP-Posting-Date", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NNTP-Posting-Host", text, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", text, StringComparison.Ordinal);
        Assert.DoesNotContain("posting-host", text, StringComparison.Ordinal);
        Assert.Contains(
            "Injection-Info: nntpd01.usenet.ninja; logging-data=\"<ok@example.com>\"; mail-complaints-to=\"abuse@usenet.ninja\"",
            text,
            StringComparison.Ordinal);
        var xtrace = RequireHeader(text, "X-Trace");
        Assert.StartsWith(AesGcmPostingTraceProtector.TokenPrefix, xtrace, StringComparison.Ordinal);
        Assert.DoesNotContain("nntpd01.usenet.ninja 20260925120000", text, StringComparison.Ordinal);
        Assert.True(session.PostingTraceProtector!.TryUnprotect(xtrace, out var recovered));
        Assert.Equal(IPAddress.Loopback, recovered.Address);
        Assert.Equal(119, recovered.Port);
        Assert.Equal(clock.GetUtcNow(), recovered.InjectedAtUtc);
        Assert.Null(recovered.AuthenticatedUsername);
        Assert.Contains("news.usenet.ninja!", text, StringComparison.Ordinal);
        Assert.Contains(".POSTED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("forged", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Xref:", text, StringComparison.Ordinal);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task Ipv6Peer_IsProtectedInsideXTraceOnly()
    {
        var queue = NewQueue();
        var peer = IPAddress.Parse("2001:db8::10");
        await using var duplex = new PostDuplex(peer);
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle() + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        using var dequeueCts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        var text = Encoding.ASCII.GetString(inbound!.Payload.Span);
        Assert.DoesNotContain("2001:db8::10", text, StringComparison.Ordinal);
        Assert.DoesNotContain("posting-host", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NNTP-Posting-Host", text, StringComparison.Ordinal);
        var xtrace = RequireHeader(text, "X-Trace");
        Assert.True(session.PostingTraceProtector!.TryUnprotect(xtrace, out var recovered));
        Assert.Equal(peer, recovered.Address);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task TwoPosts_ProduceDistinctXTraceTokens()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle(messageId: "<one@example.com>") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle(messageId: "<two@example.com>") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        using var timeoutCts = new CancellationTokenSource(Safety);
        var first = await queue.DequeueAsync(timeoutCts.Token);
        var second = await queue.DequeueAsync(timeoutCts.Token);
        var left = RequireHeader(Encoding.ASCII.GetString(first!.Payload.Span), "X-Trace");
        var right = RequireHeader(Encoding.ASCII.GetString(second!.Payload.Span), "X-Trace");
        Assert.NotEqual(left, right);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task MissingTraceProtector_Returns441()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster, includeTraceProtector: false);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle() + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task EmptyBody_IsAccepted()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle(body: string.Empty) + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task DotLeadingBody_IsRestuffedOnce_WithoutTerminator()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle(body: "..hidden\r\n") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        using var dequeueCts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        Assert.Equal(ArticleParseStatus.CanonicalV1, inbound!.Record.ParseStatus);
        Assert.True(inbound.Payload.Equals(inbound.Record.ArtData));
        var text = Encoding.ASCII.GetString(inbound.Record.ArtData.Span);
        Assert.Contains("\r\n\r\n..hidden\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n\r\n.hidden\r\n", text, StringComparison.Ordinal);
        Assert.False(inbound.Payload.Span.EndsWith(".\r\n"u8));

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task LinesHeader_DoesNotControlAcceptance()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle(extraHeaders: "Lines: 9999\r\n", body: "one\r\n") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        using var dequeueCts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        Assert.True(inbound!.Payload.Length < 2048);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task ConfiguredMailComplaintsTo_IsEmittedOnInjectionInfo()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster, mailComplaintsTo: "ops@usenet.ninja");
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle() + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        using var dequeueCts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        var text = Encoding.ASCII.GetString(inbound!.Payload.Span);
        Assert.Contains("mail-complaints-to=\"ops@usenet.ninja\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("mail-complaints-to=\"abuse@usenet.ninja\"", text, StringComparison.Ordinal);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task MultipleDotLeadingLines_AreEachStuffedOnce()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle(body: "normal\r\n..\r\n...\r\n....\r\n") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());

        using var dequeueCts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        Assert.Equal(ArticleParseStatus.CanonicalV1, inbound!.Record.ParseStatus);
        var text = Encoding.ASCII.GetString(inbound.Record.ArtData.Span);
        var body = text[(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        Assert.Equal("normal\r\n..\r\n...\r\n....\r\n", body);
        Assert.False(inbound.Payload.Span.EndsWith("\r\n.\r\n"u8));

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task CompletedQueue_Returns441_Without240()
    {
        var queue = NewQueue();
        queue.Complete();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle() + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task DisconnectBeforeAdmission_DoesNotReturn240_AndDoesNotEnqueue()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.CompleteClientAsync();
        await run.WaitAsync(Safety);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task SuccessfulPost_CallsTryAdmitOnce_AndDoesNotCallEnqueueAsync()
    {
        var inner = NewQueue();
        var queue = new RecordingIngestionQueue(inner);
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        Assert.Same(queue, session.ArticleIngestion);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle(messageId: "<once@example.com>") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.TryAdmitCalls);
        Assert.Equal(0, queue.EnqueueAsyncCalls);
        var admitted = Assert.Single(queue.Admitted);
        Assert.Equal("<once@example.com>", admitted.MessageId);
        Assert.Equal(InboundArticleProducer.Post, admitted.Producer);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task DisabledQueue_Returns441_Without240()
    {
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(DisabledArticleIngestionQueue.Instance, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle() + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task QueueBudgetExhausted_Returns441()
    {
        var held = CanonicalArticleText.CreateQueued("<held@example.com>", InboundArticleProducer.IHave);
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions { QueueCapacity = 8, MaxArticleBytes = 64 * 1024 },
            transitQueueMemoryLimit: held.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(held));

        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle() + ".\r\n");
        Assert.Equal("441 Posting failed", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task QueuedRepresentation_MatchesIhaveStuffedWireContract()
    {
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("POST");
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(ValidArticle(body: "plain\r\n..dot\r\n") + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);

        using var dequeueCts = new CancellationTokenSource(Safety);
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        Assert.Equal(InboundArticleProducer.Post, inbound!.Producer);
        Assert.Equal(ArticleParseStatus.CanonicalV1, inbound.Record.ParseStatus);
        Assert.True(inbound.Payload.Equals(inbound.Record.ArtData));
        Assert.False(inbound.Payload.Span.EndsWith("\r\n.\r\n"u8));
        var text = Encoding.ASCII.GetString(inbound.Record.ArtData.Span);
        var body = text[(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        Assert.Equal("plain\r\n..dot\r\n", body);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task ActivePostReceive_IsNotKilledByIdleTimeout()
    {
        var clock = new ControllableTimeProvider();
        clock.Advance(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch);
        var queue = NewQueue();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(queue, Poster, timeProvider: clock, idle: Idle);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);

        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        Assert.True(session.CommandWorkForTests > 0);

        clock.Advance(Idle);
        await Task.Delay(20);
        Assert.False(run.IsCompleted);
        Assert.Equal(TcpDisconnectReason.Unspecified, session.CloseReasonForTests);

        await duplex.WriteClientAsync(ValidArticle(date: PostRfcDate.Format(clock.GetUtcNow())) + ".\r\n");
        Assert.Equal("240 Article received OK", await duplex.ReadClientLineAsync());
        await WaitUntilAsync(() => session.CommandWorkForTests == 0, Safety);
        await WaitUntilAsync(() => clock.HasScheduledTimers, Safety);

        clock.Advance(Idle);
        await run.WaitAsync(Safety);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
    }

    [Fact]
    public async Task IdleConnection_BeforePost_StillTimesOut()
    {
        var clock = new ControllableTimeProvider();
        await using var duplex = new PostDuplex();
        var session = duplex.CreateSession(authorization: Poster, timeProvider: clock, idle: Idle);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await WaitUntilAsync(() => clock.HasScheduledTimers, Safety);

        clock.Advance(Idle);
        await run.WaitAsync(Safety);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
    }

    private static ArticleIngestionQueue NewQueue() =>
        new(new ArticleIngestionOptions { QueueCapacity = 8, MaxArticleBytes = NntpdOptions.DefaultMaxArticleSize });

    private static async Task QuitAsync(PostDuplex duplex, Task run)
    {
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static async Task AuthenticateAsync(PostDuplex duplex, string username, string password)
    {
        await duplex.WriteClientLineAsync("AUTHINFO USER " + username);
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS " + password);
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    private sealed class DualAuthProvider : INntpAuthenticationProvider
    {
        public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (password == "poster-secret"
                && (username == "poster" || username == "poster+tag/name"))
            {
                return ValueTask.FromResult(NntpAuthenticationResult.Success(username, Poster));
            }

            if (username == "newsmaster" && password == "unit-test-newsmaster-password")
            {
                return ValueTask.FromResult(NntpAuthenticationResult.Success(username, Newsmaster));
            }

            return ValueTask.FromResult(NntpAuthenticationResult.Failed);
        }
    }

    private static string ValidArticle(
        string? date = null,
        string? messageId = "<ok@example.com>",
        string extraHeaders = "",
        string body = "body\r\n",
        bool includeDate = true,
        bool includeSubject = true)
    {
        date ??= PostRfcDate.Format(DateTimeOffset.UtcNow);
        var sb = new StringBuilder();
        if (includeDate)
        {
            sb.Append("Date: ").Append(date).Append("\r\n");
        }
        sb.Append("From: poster@example.com\r\n");
        sb.Append("Newsgroups: misc.test\r\n");
        if (includeSubject)
        {
            sb.Append("Subject: test\r\n");
        }
        if (messageId is not null)
        {
            sb.Append("Message-ID: ").Append(messageId).Append("\r\n");
        }

        sb.Append(extraHeaders);
        sb.Append("\r\n");
        sb.Append(body);
        return sb.ToString();
    }

    private static string PaddedArticle(int destuffedBytes)
    {
        var prefix = ValidArticle(body: string.Empty);
        var needed = destuffedBytes - Encoding.ASCII.GetByteCount(prefix);
        Assert.True(needed >= 2);

        // Common parser rejects physical lines over 1024 bytes. Keep body lines
        // at 80 bytes so size-limit tests isolate destuffed size, not line length.
        const int maxContent = 78;
        var sb = new StringBuilder(prefix, destuffedBytes);
        var remaining = needed;
        while (remaining > 0)
        {
            var content = Math.Min(maxContent, remaining - 2);
            if (remaining > 80 && remaining - (content + 2) == 1)
            {
                content--;
            }

            sb.Append('Z', content);
            sb.Append("\r\n");
            remaining -= content + 2;
        }

        var article = sb.ToString();
        Assert.Equal(destuffedBytes, Encoding.ASCII.GetByteCount(article));
        return article;
    }

    private sealed class RecordingIngestionQueue : IArticleIngestionQueue
    {
        private readonly IArticleIngestionQueue _inner;

        public RecordingIngestionQueue(IArticleIngestionQueue inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        public int TryAdmitCalls { get; private set; }

        public int EnqueueAsyncCalls { get; private set; }

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
            CancellationToken cancellationToken)
        {
            EnqueueAsyncCalls++;
            return _inner.EnqueueAsync(article, cancellationToken);
        }

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

    private static string RequireHeader(string article, string name)
    {
        var prefix = name + ": ";
        var start = article.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0, "missing header " + name);
        var end = article.IndexOf("\r\n", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return article[(start + prefix.Length)..end];
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private sealed class PostDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());
        private readonly IPAddress _address;

        public PostDuplex(IPAddress? address = null)
        {
            _address = address ?? IPAddress.Loopback;
        }

        public NntpSession CreateSession(
            IArticleIngestionQueue? queue = null,
            NntpAuthorization? authorization = null,
            IHistoryDb? historyDb = null,
            int? maxArticleSize = null,
            TimeProvider? timeProvider = null,
            TimeSpan? idle = null,
            string? mailComplaintsTo = null,
            bool includeTraceProtector = true,
            IPostingTraceProtector? postingTraceProtector = null,
            INntpAuthenticationProvider? authenticationProvider = null,
            INewsLogWriter? newsLog = null,
            IPostFilter? postFilter = null)
        {
            var connection = new PipeConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(_address, 119)));
            var protector = includeTraceProtector
                ? postingTraceProtector ?? AesGcmPostingTraceProtector.Create(
                    new NntpdOptions { XTraceKey = TestHostFactory.TestXTraceKey })
                : null;
            var session = new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                authenticationProvider: authenticationProvider,
                articleIngestion: queue,
                historyDb: historyDb,
                commandIdleTimeout: idle,
                timeProvider: timeProvider,
                maxArticleSize: maxArticleSize,
                mailComplaintsTo: mailComplaintsTo,
                postingTraceProtector: protector,
                postFilter: postFilter,
                newsLog: newsLog);
            if (authorization is not null)
            {
                session.SetAuthorization(authorization);
            }

            return session;
        }

        public async Task WriteClientLineAsync(string line)
        {
            await WriteClientAsync(line + "\r\n");
        }

        public async Task WriteClientAsync(string payload)
        {
            await _clientToServer.Writer.WriteAsync(Encoding.ASCII.GetBytes(payload));
            await _clientToServer.Writer.FlushAsync();
        }

        public Task CompleteClientAsync() => _clientToServer.Writer.CompleteAsync().AsTask();

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
