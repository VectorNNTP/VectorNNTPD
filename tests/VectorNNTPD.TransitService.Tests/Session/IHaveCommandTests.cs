using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.PostFilter;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.Newsgroups;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;

namespace VectorNNTPD.TransitService.Tests.Session;

public sealed class IHaveCommandTests
{
    private static NntpAuthorization TransitAuth { get; } = new(
        isAuthenticated: true,
        authorizedReader: false,
        authorizedTransit: true,
        postingPermitted: false,
        streamingPermitted: true);

    [Fact]
    public async Task ValidIhave_AcceptsArticle_EnqueuesWire_AndReturns235()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <want@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(CanonicalArticleText.Stuffed("<want@example.com>", ".body\r\n") + ".\r\n");
        Assert.Equal("235 Article transferred OK", await duplex.ReadClientLineAsync());

        using var dequeueCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        Assert.NotNull(inbound);
        Assert.Equal("<want@example.com>", inbound!.MessageId);
        Assert.Equal(InboundArticleProducer.IHave, inbound.Producer);
        Assert.Equal(ArticleParseStatus.CanonicalV1, inbound.Record.ParseStatus);
        Assert.True(inbound.Payload.Equals(inbound.Record.ArtData));
        var text = Encoding.ASCII.GetString(inbound.Record.ArtData.Span);
        Assert.Contains("\r\n\r\n..body\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n\r\n.body\r\n", text, StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task ValidIhave_UnknownNewsgroup_WantTrash_Returns235()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <ihave-wanttrash@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            CanonicalArticleText.Stuffed("<ihave-wanttrash@example.com>", newsgroups: "unknown.un.carried") + ".\r\n");
        Assert.Equal("235 Article transferred OK", await duplex.ReadClientLineAsync());

        using var dequeueCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        Assert.Equal("<ihave-wanttrash@example.com>", inbound!.MessageId);
        Assert.Equal(ArticleParseStatus.CanonicalV1, inbound.Record.ParseStatus);
        Assert.Equal("unknown.un.carried", Encoding.ASCII.GetString(inbound.Record.Newsgroups));
        Assert.Contains("Newsgroups: unknown.un.carried"u8, inbound.Payload.Span);
        Assert.DoesNotContain("junk"u8, inbound.Record.Newsgroups);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task ValidIhave_DoesNotEvaluatePostFilter()
    {
        var filter = new CountingPostFilter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, postFilter: filter);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <want-pf@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(CanonicalArticleText.Stuffed("<want-pf@example.com>") + ".\r\n");
        Assert.Equal("235 Article transferred OK", await duplex.ReadClientLineAsync());
        Assert.Equal(0, filter.EvaluateCalls);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task HistoryHit_Returns435_AndDoesNotReadArticleBytes()
    {
        var redis = new FakeRedisService();
        var history = new HistoryDb(
            redis,
            TimeSpan.FromHours(2),
            NullLogger<HistoryDb>.Instance,
            TimeProvider.System,
            new HistoryWriteQueue());
        history.Remember("<have@example.com>"u8.ToArray());
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 2 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, history, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <have@example.com>");
        Assert.Equal("435 Article not wanted", await duplex.ReadClientLineAsync());
        Assert.Empty(news.Events);
        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task RedisUnavailable_Returns436()
    {
        var redis = new FakeRedisService { IsUnavailable = true };
        var history = new HistoryDb(
            redis,
            TimeSpan.FromHours(2),
            NullLogger<HistoryDb>.Instance,
            TimeProvider.System,
            new HistoryWriteQueue());
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 2 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <later@example.com>");
        Assert.Equal("436 Transfer not possible; try again later", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task MalformedMessageId_Is501()
    {
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(new ArticleIngestionQueue(new ArticleIngestionOptions()));
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE not-an-id");
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task QueueUnavailable_Returns436BeforeArticle()
    {
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(DisabledArticleIngestionQueue.Instance);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <q@example.com>");
        Assert.Equal("436 Transfer not possible; try again later", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task ArticleLargerThanQueueBudget_Returns437()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions { MaxArticleBytes = 1024 },
            transitQueueMemoryLimit: 16);
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <budget@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(CanonicalArticleText.Stuffed("<budget@example.com>") + ".\r\n");
        Assert.Equal("437 Transfer rejected; do not retry", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.QueuedBytes);
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Rejected, evt.Disposition);
        Assert.Equal(IngressNewsReasons.QueueCapacityExceeded, Encoding.ASCII.GetString(evt.Reason.Span));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task BudgetExhaustedBefore335_Returns436Immediately_AndDoesNotWait()
    {
        var held = FillArticle("<held@ex.com>");
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions(),
            transitQueueMemoryLimit: held.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(held));
        Assert.False(queue.TryProbeCapacity());

        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var response = duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("IHAVE <nowait@example.com>");
        Assert.Equal("436 Transfer not possible; try again later", await response.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(held.Payload.Length, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task PostReceiveCapacityFailure_Returns436_Not235_AndReleasesOwnership()
    {
        var held = FillArticle("<held@ex.com>");
        var incoming = CanonicalArticleText.CreateQueued("<late@example.com>", InboundArticleProducer.IHave);
        Assert.True(held.Payload.Length < incoming.Payload.Length);
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions { MaxArticleBytes = 64 * 1024 },
            transitQueueMemoryLimit: incoming.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(held));
        Assert.True(queue.TryProbeCapacity());

        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <late@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        var article = duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(CanonicalArticleText.Stuffed("<late@example.com>") + ".\r\n");
        Assert.Equal("436 Transfer failed; try again later", await article.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(held.Payload.Length, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);

        Assert.Equal("<held@ex.com>", (await queue.DequeueAsync(CancellationToken.None))!.MessageId);
        Assert.Equal(0, queue.QueuedBytes);

        await duplex.WriteClientLineAsync("IHAVE <next@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(CanonicalArticleText.Stuffed("<next@example.com>") + ".\r\n");
        Assert.Equal("235 Article transferred OK", await duplex.ReadClientLineAsync());
        Assert.Equal("<next@example.com>", (await queue.DequeueAsync(CancellationToken.None))!.MessageId);
        Assert.Equal(0, queue.QueuedBytes);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task QueueCompleteAfter335_Returns436_AndLeavesAccounting()
    {
        var sample = FillArticle("<shut@example.com>");
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions(),
            transitQueueMemoryLimit: sample.Payload.Length);
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <shut@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        queue.Complete();
        await duplex.WriteClientAsync(CanonicalArticleText.Stuffed("<shut@example.com>") + ".\r\n");
        Assert.Equal("436 Transfer failed; try again later", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public void BudgetExhausted_WarningIsRateLimited()
    {
        IHaveAdmissionLog.ResetForTests();
        IHaveAdmissionLog.WarningInterval = TimeSpan.FromHours(1);
        var recording = new RecordingLogger();
        var held = FillArticle("<held@ex.com>");
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions(),
            transitQueueMemoryLimit: held.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(held));

        IHaveAdmissionLog.BudgetExhausted(recording, queue);
        IHaveAdmissionLog.BudgetExhausted(recording, queue);
        var first = Assert.Single(recording.Warnings);
        Assert.Contains("TransitQueueMemoryLimit exhausted", first, StringComparison.Ordinal);
        Assert.Contains($"{held.Payload.Length}/{held.Payload.Length}", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Subject:", first, StringComparison.Ordinal);

        IHaveAdmissionLog.ResetForTests();
        IHaveAdmissionLog.BudgetExhausted(recording, queue);
        Assert.Equal(2, recording.Warnings.Count);
        IHaveAdmissionLog.ResetForTests();
    }

    [Fact]
    public async Task TooLarge_Returns437_AndLeavesNextCommand()
    {
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions { QueueCapacity = 2, MaxArticleBytes = 16 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <big@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync("Subject: big\r\n\r\n" + new string('Z', 64) + "\r\n.\r\nDATE\r\n");
        Assert.Equal("437 Transfer rejected; do not retry", await duplex.ReadClientLineAsync());
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TooLarge_WritesRejectedNewsWith437()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions { QueueCapacity = 2, MaxArticleBytes = 16 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <big-news@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync("Subject: big\r\n\r\n" + new string('Z', 64) + "\r\n.\r\n");
        Assert.Equal("437 Transfer rejected; do not retry", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Rejected, evt.Disposition);
        Assert.Equal(437, evt.ResponseCode);
        Assert.Equal(IngressNewsReasons.ArticleTooLarge, Encoding.ASCII.GetString(evt.Reason.Span));
        Assert.DoesNotContain("437", Encoding.ASCII.GetString(evt.Reason.Span), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task NewsWriterFailure_On437_DoesNotChangeRejectionResponse()
    {
        var news = new ThrowingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <news-fail-437@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync("Subject: hi\r\n\r\n..body\r\n.\r\n");
        Assert.Equal("437 Transfer rejected; do not retry", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        Assert.Equal(1, news.WriteCalls);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Capabilities_AdvertisesIhave()
    {
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(new ArticleIngestionQueue(new ArticleIngestionOptions()));
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("CAPABILITIES");
        var lines = new List<string>();
        string line;
        do
        {
            line = await duplex.ReadClientLineAsync();
            lines.Add(line);
        }
        while (line != ".");

        Assert.Contains("IHAVE", lines);
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task IncompleteArticleAfter335_Returns437_AndDoesNotEnqueue()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <incomplete@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync("Subject: hi\r\n\r\n..body\r\n.\r\n");
        Assert.Equal("437 Transfer rejected; do not retry", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Rejected, evt.Disposition);
        Assert.Equal(437, evt.ResponseCode);
        var incompleteReason = Encoding.ASCII.GetString(evt.Reason.Span);
        Assert.DoesNotContain("rejected article record", incompleteReason, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(incompleteReason));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task WantTrashFalse_UnknownGroup_Returns437_AndWritesRejectedNews()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(
            queue,
            newsLog: news,
            transit: new TransitOptions { WantTrash = false, LogTrash = false },
            catalogue: CarriedCatalogue("alt.test"));
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <ihave-notrash@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            CanonicalArticleText.Stuffed("<ihave-notrash@example.com>", newsgroups: "unknown.un.carried") + ".\r\n");
        Assert.Equal("437 Transfer rejected; do not retry", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Rejected, evt.Disposition);
        Assert.Equal(437, evt.ResponseCode);
        Assert.Equal(
            IngressNewsReasons.WithGroups(IngressNewsReasons.NewsgroupNotCarried, ["unknown.un.carried"]),
            Encoding.ASCII.GetString(evt.Reason.Span));
        Assert.True(evt.MessageId.Span.SequenceEqual("<ihave-notrash@example.com>"u8));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task InvalidMessageIdAfter335_WritesRejectedNewsWithParseReason()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <bad-mid@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            CanonicalArticleText.Stuffed("not-a-message-id") + ".\r\n");
        Assert.Equal("437 Transfer rejected; do not retry", await duplex.ReadClientLineAsync());
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Rejected, evt.Disposition);
        Assert.Equal(437, evt.ResponseCode);
        Assert.Equal(IngressNewsReasons.MessageIdInvalid, Encoding.ASCII.GetString(evt.Reason.Span));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task InvalidDateAfter335_WritesRejectedNewsWithParseReason()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <bad-date@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        var article = CanonicalArticleText.Destuffed("<bad-date@example.com>")
            .Replace(CanonicalArticleText.Date, "not-a-date", StringComparison.Ordinal);
        await duplex.WriteClientAsync(article.Replace("\r\n.", "\r\n..", StringComparison.Ordinal) + ".\r\n");
        Assert.Equal("437 Transfer rejected; do not retry", await duplex.ReadClientLineAsync());
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Rejected, evt.Disposition);
        Assert.Equal(IngressNewsReasons.DateInvalid, Encoding.ASCII.GetString(evt.Reason.Span));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task YEncValidationFailureAfter335_WritesRejectedNewsWithYEncReason()
    {
        await AssertYEncRejectionAsync(
            "<yenc-meta@example.com>",
            "=ybegin line=128 size=abc name=t.bin\r\n.\r\n=yend size=1 crc32=00000000\r\n");
    }

    [Fact]
    public async Task YEncCrcMismatchAfter335_WritesRejectedNewsWithYEncReason()
    {
        await AssertYEncRejectionAsync(
            "<yenc-crc@example.com>",
            "=ybegin line=128 size=1 name=t.bin\r\nk\r\n=yend size=1 crc32=00000000\r\n");
    }

    [Fact]
    public async Task NamedPeer_YEncReject_NewsLineHasInboundPeerSizeAndOutboundPlaceholder()
    {
        const string commandId = "<yenc-peer@example.com>";
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: false,
            authorizedTransit: true,
            postingPermitted: false,
            streamingPermitted: true,
            transitPeerName: "BlueWorldHosting"));
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE " + commandId);
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(
            CanonicalArticleText.Stuffed(
                commandId,
                "=ybegin line=128 size=1 name=t.bin\r\nk\r\n=yend size=1 crc32=00000000\r\n") + ".\r\n");
        Assert.Equal("437 Transfer rejected; do not retry", await duplex.ReadClientLineAsync());
        var evt = Assert.Single(news.Events);
        Assert.True(evt.Feed.Span.SequenceEqual("BlueWorldHosting"u8));
        Assert.True(evt.Size > 0);
        var rendered = FormatNews(in evt);
        Assert.Equal(
            FormatNews(new NewsLogEvent(
                NewsLogDisposition.Rejected,
                Encoding.ASCII.GetBytes(commandId),
                "BlueWorldHosting"u8.ToArray(),
                timestamp: evt.Timestamp,
                responseCode: 437,
                reason: "yEncoding invalid"u8.ToArray(),
                size: evt.Size)),
            rendered);
        Assert.Contains("- BlueWorldHosting " + commandId + " " + evt.Size + " yEncoding invalid", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("- ? ", rendered, StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task CommandMessageIdNeedNotMatchArticleMessageId()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <command@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(CanonicalArticleText.Stuffed("<article@example.com>") + ".\r\n");
        Assert.Equal("235 Article transferred OK", await duplex.ReadClientLineAsync());

        using var dequeueTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var inbound = await queue.DequeueAsync(dequeueTimeout.Token);
        Assert.Equal("<command@example.com>", inbound!.MessageId);
        Assert.True(inbound.Record.MessageId.SequenceEqual("<article@example.com>"u8));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    private static InboundArticle FillArticle(string messageId) =>
        CanonicalArticleText.CreateQueued(messageId, InboundArticleProducer.IHave);

    private static INewsgroupCatalogue CarriedCatalogue(string group) =>
        new StaticNewsgroupCatalogue(
            NewsgroupSnapshot.Create(
                [new NewsgroupDefinition(group, string.Empty, 2, 1, NewsgroupPostingStatus.Allowed)]));

    private static string FormatNews(in NewsLogEvent evt)
    {
        var timestamp = evt.Timestamp == default
            ? new DateTimeOffset(2024, 8, 25, 13, 37, 54, 638, TimeSpan.Zero)
            : evt.Timestamp;
        Span<byte> buffer = stackalloc byte[NewsLogLineFormatter.RequiredLength(in evt) + 16];
        var written = NewsLogLineFormatter.Write(buffer, in evt, timestamp);
        return Encoding.ASCII.GetString(buffer[..written]);
    }

    private static async Task AssertYEncRejectionAsync(string commandId, string body)
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE " + commandId);
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(CanonicalArticleText.Stuffed(commandId, body) + ".\r\n");
        Assert.Equal("437 Transfer rejected; do not retry", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Rejected, evt.Disposition);
        Assert.Equal(437, evt.ResponseCode);
        Assert.Equal(IngressNewsReasons.YEncodingInvalid, Encoding.ASCII.GetString(evt.Reason.Span));
        var rendered = FormatNews(in evt);
        Assert.EndsWith(
            "- ? " + commandId + " " + evt.Size + " yEncoding invalid\n",
            rendered,
            StringComparison.Ordinal);
        Assert.DoesNotContain("437", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("YEncDecodingFailed", rendered, StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class IHaveDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();

        public static Task<IHaveDuplex> CreateAsync() => Task.FromResult(new IHaveDuplex());

        public NntpSession CreateSession(
            IArticleIngestionQueue queue,
            IHistoryDb? history = null,
            IPostFilter? postFilter = null,
            INewsLogWriter? newsLog = null,
            TransitOptions? transit = null,
            INewsgroupCatalogue? catalogue = null)
        {
            var connection = new PipeConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                historyDb: history,
                postFilter: postFilter,
                newsgroupCatalogue: catalogue,
                newsLog: newsLog,
                transit: transit);
        }

        public async Task WriteClientLineAsync(string line)
        {
            await _clientToServer.Writer.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task WriteClientAsync(string payload)
        {
            await _clientToServer.Writer.WriteAsync(Encoding.ASCII.GetBytes(payload));
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

        public System.Net.EndPoint? LocalEndPoint => null;

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
