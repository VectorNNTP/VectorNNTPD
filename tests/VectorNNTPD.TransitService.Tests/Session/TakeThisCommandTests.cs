using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Authentication;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.PostFilter;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Session.Framing;
using VectorNNTPD.TransitService.Newsgroups;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.Networking.Transport;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.TransitService.Tests.Transit;

namespace VectorNNTPD.TransitService.Tests.Session;

/// <summary>RFC 4644 TAKETHIS streaming / pipelining / ingestion contracts.</summary>
[Collection(nameof(TransportTestHostCollection))]
public sealed class TakeThisCommandTests
{
    private static NntpAuthorization TransitAuth { get; } = new(
        isAuthenticated: true,
        authorizedReader: false,
        authorizedTransit: true,
        postingPermitted: false,
        streamingPermitted: true);

    [Fact]
    public async Task TakeThis_ValidArticle_Returns239_AndEnqueues()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<article-one@example.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, CanonicalArticleText.Destuffed(id, "body\r\n")));
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());

        var article = await queue.DequeueAsync(CancellationToken.None);
        Assert.NotNull(article);
        Assert.Equal(id, article!.MessageId);
        Assert.Equal(InboundArticleProducer.TakeThis, article.Producer);
        Assert.Equal(ArticleParseStatus.CanonicalV1, article.Record.ParseStatus);
        Assert.Equal(ArticleParseStatus.CanonicalV1, article.Record.ParseStatus);
        Assert.True(article.Payload.Equals(article.Record.ArtData));
        Assert.Equal(article.Record.ArtData.Length, article.Record.ArtSize);
        Assert.Equal(1, article.Record.ArtLines);
        Assert.NotEqual(ArticleType.None, article.Record.ArtType);
        Assert.True(article.Record.MessageId.SequenceEqual(Encoding.ASCII.GetBytes(id)));
        Assert.True(article.Record.Fields.MessageId.IsPresent);
        Assert.True(article.Record.Fields.Newsgroups.IsPresent);
        Assert.True(article.Record.Fields.Date.IsPresent);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task WantTrashFalse_UnknownGroup_Returns439_AndWritesRejectedNews()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(
            queue,
            newsLog: news,
            transit: new TransitOptions { WantTrash = false, LogTrash = false },
            catalogue: new StaticNewsgroupCatalogue(
                NewsgroupSnapshot.Create(
                    [new NewsgroupDefinition("alt.test", string.Empty, 2, 1, NewsgroupPostingStatus.Allowed)])));
        session.SetAuthorization(TransitAuth);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<takethis-notrash@example.com>";
        await duplex.WriteClientAsync(
            BuildTakeThis(id, CanonicalArticleText.Destuffed(id, newsgroups: "unknown.un.carried")));
        Assert.Equal($"439 {id}", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Rejected, evt.Disposition);
        Assert.Equal(439, evt.ResponseCode);
        Assert.Equal(
            IngressNewsReasons.WithGroups(IngressNewsReasons.NewsgroupNotCarried, ["unknown.un.carried"]),
            Encoding.ASCII.GetString(evt.Reason.Span));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_TextOnlyCapability_RejectsYEncoded()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(TransitAuth);
        session.ApplySuccessfulAuthentication(
            "poster",
            TransitAuth,
            new NntpAccountPolicy("poster", 0, 0, 0, 0, "c", ArticleTypeCapabilities.TextOnly));

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<yenc-denied@example.com>";
        var decoded = new byte[] { 0x41 };
        var crc = VectorNNTPD.Common.Articles.YEnc.YEncCrc32.Compute(decoded);
        var encoded = unchecked((byte)(decoded[0] + 42));
        var body = $"=ybegin line=128 size=1 name=t.bin\r\n{(char)encoded}\r\n=yend size=1 crc32={crc:x8}\r\n";
        var article = CanonicalArticleText.Destuffed(id, body);
        await duplex.WriteClientAsync(BuildTakeThis(id, article));
        Assert.Equal($"439 {id}", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Rejected, evt.Disposition);
        Assert.Equal(IngressNewsReasons.ArticleTypeNotPermitted, Encoding.ASCII.GetString(evt.Reason.Span));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_ValidArticle_DoesNotEvaluatePostFilter()
    {
        var filter = new CountingPostFilter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, postFilter: filter);
        session.SetAuthorization(TransitAuth);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<article-pf@example.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, CanonicalArticleText.Destuffed(id, "body\r\n")));
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());
        Assert.Equal(0, filter.EvaluateCalls);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task StreamTakeThis_InvalidArticle_Returns439_AndDoesNotEnqueue()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        Assert.Equal(NntpReceiveStrategy.StreamDataPlane, session.ReceiveStrategy);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<dot@example.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, "..foo\r\nbar\r\n"));
        Assert.Equal($"439 {id}", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task StreamTakeThis_BenchHeaderSet_Returns439_AndDoesNotEnqueue()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<bench-00-000000000001@vectornntp.local>";
        await duplex.WriteClientAsync(
            BuildTakeThis(
                id,
                "From: benchmark@vectornntp.local\r\n" +
                "Subject: TAKETHIS benchmark\r\n" +
                "Message-ID: <bench-00-000000000001@vectornntp.local>\r\n" +
                "\r\n" +
                "1234567890abcdefghijklmnopqrstuvwxyz1234567890abcdefghijklmnopqrstuvwxyz12345678\r\n"));
        Assert.Equal($"439 {id}", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task StreamTakeThis_ValidDottedBody_QueuesDestuffedArtData()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<dot-valid@example.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, CanonicalArticleText.Destuffed(id, "..foo\r\nbar\r\n").Replace("..foo\r\n", "...foo\r\n", StringComparison.Ordinal)));
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());

        var article = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(ArticleParseStatus.CanonicalV1, article!.Record.ParseStatus);
        var text = Encoding.ASCII.GetString(article.Record.ArtData.Span);
        Assert.Contains("\r\n\r\n...foo\r\nbar\r\n", text, StringComparison.Ordinal);
        Assert.True(article.Payload.Equals(article.Record.ArtData));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_PipelinedThree_ResponsesOrdered_AllQueued()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var ids = new[] { "<a@ex.com>", "<b@ex.com>", "<c@ex.com>" };
        var payload = new StringBuilder();
        foreach (var id in ids)
        {
            payload.Append(BuildTakeThis(id, CanonicalArticleText.Destuffed(id, "x\r\n")));
        }

        await duplex.WriteClientAsync(payload.ToString());

        foreach (var id in ids)
        {
            Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());
        }

        for (var i = 0; i < ids.Length; i++)
        {
            var article = await queue.DequeueAsync(CancellationToken.None);
            Assert.Equal(ids[i], article!.MessageId);
        }

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_TwoPipelinedInOneWrite_IdleFlushPeekLeavesSecondArticleReadable()
    {
        // Session idle-flush peeks Connection.Input after each TAKETHIS. That peek must
        // AdvanceTo(Start, Start). AdvanceTo(Start, End) marks leftover examined, so the
        // next STREAM ReadAsync waits even though the second TAKETHIS is already buffered.
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string first = "<peek1@ex.com>";
        const string second = "<peek2@ex.com>";
        await duplex.WriteClientAsync(
            BuildTakeThis(first, CanonicalArticleText.Destuffed(first, "a\r\n")) +
            BuildTakeThis(second, CanonicalArticleText.Destuffed(second, "b\r\n")));

        Assert.Equal($"239 {first}", await duplex.ReadClientLineAsync());
        Assert.Equal($"239 {second}", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TakeThis_SlowOutboundFlush_DoesNotBlockArticleReceive()
    {
        // Pause after ~1 byte so the first 239 flush blocks until the client reads.
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        await using var duplex = await TakeThisDuplex.CreateAsync(pauseWriterThreshold: 1);
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var ids = new[] { "<p1@ex.com>", "<p2@ex.com>", "<p3@ex.com>" };
        var payload = new StringBuilder();
        foreach (var id in ids)
        {
            payload.Append(BuildTakeThis(id, CanonicalArticleText.Destuffed(id, "y\r\n")));
        }

        await duplex.WriteClientAsync(payload.ToString());

        // Articles must be accepted into the queue without the client reading any 239 yet.
        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (queue.Count < 3 && !waitCts.IsCancellationRequested)
        {
            await Task.Delay(5, waitCts.Token);
        }

        Assert.Equal(3, queue.Count);

        foreach (var id in ids)
        {
            Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());
        }

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_SlowSpoolWriter_DoesNotBlockEnqueue()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        var writer = new IncomingSpoolWriterService(
            queue,
            Options.Create(new NntpdOptions { ArticleIngestion = new ArticleIngestionOptions() }),
            NullLogger<IncomingSpoolWriterService>.Instance);

        await writer.StartAsync(CancellationToken.None);

        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var ids = new[] { "<s1@ex.com>", "<s2@ex.com>", "<s3@ex.com>" };
        foreach (var id in ids)
        {
            await duplex.WriteClientAsync(BuildTakeThis(id, CanonicalArticleText.Destuffed(id, "z\r\n")));
            Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());
        }

        queue.Complete();
        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await writer.StopAsync(drainCts.Token);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_QueueFull_AppliesBackpressureThenAccepts()
    {
        const string firstId = "<q1@ex.com>";
        const string secondId = "<q2@ex.com>";
        var firstPayload = CanonicalArticleText.Destuffed(firstId, "a\r\n");
        var firstRecord = ArticleRecordIngress.TryCreateFromDestuffed(
            new VectorNNTPD.Common.Articles.Parsing.NntpArticleParser("nntpd01.usenet.ninja"),
            Encoding.ASCII.GetBytes(firstPayload));
        Assert.True(firstRecord.IsAccepted);
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions(),
            firstRecord.Record.ArtSize);
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(BuildTakeThis(firstId, firstPayload));
        Assert.Equal($"239 {firstId}", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);

        // Second response must wait until capacity frees (enqueue backpressure).
        var secondResponse = duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(BuildTakeThis(secondId, CanonicalArticleText.Destuffed(secondId, "b\r\n")));
        await Task.Delay(80);
        Assert.False(secondResponse.IsCompleted);

        var first = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(firstId, first!.MessageId);

        Assert.Equal($"239 {secondId}", await secondResponse.WaitAsync(TimeSpan.FromSeconds(5)));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_DisconnectMidArticle_DoesNotEnqueue()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync("TAKETHIS <partial@ex.com>\r\nSubject: x\r\n\r\nno-terminator");
        await duplex.CompleteClientInputAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task TakeThis_InvalidMessageId_Returns501_AfterConsumingArticle()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(BuildTakeThis("not-a-msgid", "Subject: x\r\n\r\ny\r\n"));
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_WithoutTransitAuth_Returns480()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("TAKETHIS <x@ex.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_QueueUnavailable_Returns400AndCloses()
    {
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(DisabledArticleIngestionQueue.Instance);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(BuildTakeThis("<tmp@ex.com>", "Subject: t\r\n\r\nz\r\n"));
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TakeThis_TooLarge_Returns439()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
        {
            QueueCapacity = 4,
            MaxArticleBytes = 16,
        });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<big@ex.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, "Subject: oversized-payload-here\r\n\r\nbody\r\n"));
        Assert.Equal($"439 {id}", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_TooLarge_WritesRejectedNews()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
        {
            QueueCapacity = 4,
            MaxArticleBytes = 16,
        });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<big-news@ex.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, "Subject: oversized-payload-here\r\n\r\nbody\r\n"));
        Assert.Equal($"439 {id}", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Rejected, evt.Disposition);
        Assert.Equal(439, evt.ResponseCode);
        Assert.Equal(IngressNewsReasons.ArticleTooLarge, Encoding.ASCII.GetString(evt.Reason.Span));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_NewsWriterFailure_On439_DoesNotChangeRejectionResponse()
    {
        var news = new ThrowingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
        {
            QueueCapacity = 4,
            MaxArticleBytes = 16,
        });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, newsLog: news);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<news-fail-439@ex.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, "Subject: oversized-payload-here\r\n\r\nbody\r\n"));
        Assert.Equal($"439 {id}", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        Assert.Equal(1, news.WriteCalls);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_MixedAcceptReject_ResponseMessageIdsCorrelate()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
        {
            QueueCapacity = 4,
            MaxArticleBytes = 256,
        });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var ok = "<ok@ex.com>";
        var big = "<big2@ex.com>";
        var payload = BuildTakeThis(ok, CanonicalArticleText.Destuffed(ok, "x\r\n"))
                      + BuildTakeThis(big, "Subject: this-is-definitely-too-large\r\n\r\n" + new string('Z', 300) + "\r\n");
        await duplex.WriteClientAsync(payload);

        Assert.Equal($"239 {ok}", await duplex.ReadClientLineAsync());
        Assert.Equal($"439 {big}", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task DefaultUnspecified_UsesStreamDataPlaneRx()
    {
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(new ArticleIngestionQueue(new ArticleIngestionOptions()));
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);
        Assert.Equal(NntpReceiveStrategy.StreamDataPlane, session.ReceiveStrategy);
    }

    [Fact]
    public async Task ModeStream_KeepsStreamDataPlaneRx_WithoutSettingMode()
    {
        var source = System.Net.IPAddress.Parse("192.0.2.10");
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(
            new ArticleIngestionQueue(new ArticleIngestionOptions()),
            TransitTestPeers.ForAllowFrom(source),
            source);
        Assert.Equal(TransitTestPeers.DefaultPeerName, session.Authorization.TransitPeerName);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await duplex.ReadClientLineAsync());
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);
        Assert.Equal(NntpReceiveStrategy.StreamDataPlane, session.ReceiveStrategy);

        var id = "<stream-rx@ex.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, CanonicalArticleText.Destuffed(id, "body\r\n")));
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task ModeReader_OnTransit_Returns502_AndSessionStaysOnStreamDataPlane()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        // The Transit role never enters reader mode: MODE READER is unavailable and RX stays on the stream data plane.
        await duplex.WriteClientLineAsync("MODE READER");
        Assert.Equal("502 Command not available on this server", await duplex.ReadClientLineAsync());
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);
        Assert.Equal(NntpReceiveStrategy.StreamDataPlane, session.ReceiveStrategy);

        // TAKETHIS after the rejected MODE READER is still pre-consumed by the stream data plane.
        session.SetAuthorization(TransitAuth);
        Assert.Equal(NntpReceiveStrategy.StreamDataPlane, session.ReceiveStrategy);

        const string id = "<reader-path@ex.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, CanonicalArticleText.Destuffed(id, "reader\r\n")));
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());
        var article = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(id, article!.MessageId);
        Assert.Equal(ArticleParseStatus.CanonicalV1, article.Record.ParseStatus);
        Assert.Contains("reader\r\n", Encoding.ASCII.GetString(article.Record.ArtData.Span), StringComparison.Ordinal);

        const string stuffedId = "<reader-destuff@ex.com>";
        await duplex.WriteClientAsync(BuildTakeThis(stuffedId, CanonicalArticleText.Destuffed(stuffedId, ".foo\r\nbar\r\n").Replace(".foo\r\n", "..foo\r\n", StringComparison.Ordinal)));
        Assert.Equal($"239 {stuffedId}", await duplex.ReadClientLineAsync());
        var destuffed = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(ArticleParseStatus.CanonicalV1, destuffed!.Record.ParseStatus);
        Assert.Contains("\r\n\r\n..foo\r\nbar\r\n", Encoding.ASCII.GetString(destuffed.Record.ArtData.Span), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task ModeReader_OnTransit_Returns502_AndTakeThisWithoutTransit_DoesNotConsumeFollowingQuit()
    {
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(new ArticleIngestionQueue(new ArticleIngestionOptions()));
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE READER");
        Assert.Equal("502 Command not available on this server", await duplex.ReadClientLineAsync());
        Assert.Equal(NntpReceiveStrategy.StreamDataPlane, session.ReceiveStrategy);

        await duplex.WriteClientLineAsync("TAKETHIS <x@ex.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        Assert.StartsWith("205 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await run;
    }

    [Fact]
    public async Task StreamRx_CheckThenTakeThis_CommandsInsideArticleRemainPayload()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        Assert.Equal(NntpReceiveStrategy.StreamDataPlane, session.ReceiveStrategy);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var stored = "QUIT\r\nCHECK x\r\nTAKETHIS y\r\n";
        await duplex.WriteClientAsync(
            "CHECK <c@ex.com>\r\n" + BuildTakeThis("<in@body>", CanonicalArticleText.Destuffed("<in@body>", stored)) + "QUIT\r\n");

        Assert.Equal("238 <c@ex.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <in@body>", await duplex.ReadClientLineAsync());
        var article = await queue.DequeueAsync(CancellationToken.None);
        Assert.Contains(stored, Encoding.ASCII.GetString(article!.Record.ArtData.Span), StringComparison.Ordinal);
        Assert.StartsWith("205 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await run;
    }

    [Fact]
    public async Task ModeStream_Returns203_WithoutChangingMode()
    {
        var source = System.Net.IPAddress.Parse("192.0.2.10");
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(
            new ArticleIngestionQueue(new ArticleIngestionOptions()),
            TransitTestPeers.ForAllowFrom(source),
            source);
        Assert.Equal(TransitTestPeers.DefaultPeerName, session.Authorization.TransitPeerName);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);
        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await duplex.ReadClientLineAsync());
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Capabilities_AdvertisesStreaming()
    {
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(new ArticleIngestionQueue(new ArticleIngestionOptions()));
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

        Assert.Contains(lines, static l => l.Equals("STREAMING", StringComparison.Ordinal));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task SpoolWriter_DiscardsQueuedArticlesWithoutPersisting()
    {
        var options = new ArticleIngestionOptions { QueueCapacity = 4 };
        var queue = new ArticleIngestionQueue(options);
        var writer = new IncomingSpoolWriterService(
            queue,
            Options.Create(new NntpdOptions { ArticleIngestion = options }),
            NullLogger<IncomingSpoolWriterService>.Instance);
        await writer.StartAsync(CancellationToken.None);

        var article = CanonicalArticleText.CreateQueued("<spool@ex.com>", InboundArticleProducer.TakeThis);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(article, CancellationToken.None));

        queue.Complete();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await writer.StopAsync(cts.Token);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task SpoolWriter_ShutdownDrainsAlreadyQueuedArticles()
    {
        var options = new ArticleIngestionOptions { QueueCapacity = 8 };
        var queue = new ArticleIngestionQueue(options);
        var writer = new IncomingSpoolWriterService(
            queue,
            Options.Create(new NntpdOptions { ArticleIngestion = options }),
            NullLogger<IncomingSpoolWriterService>.Instance);
        await writer.StartAsync(CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            var article = CanonicalArticleText.CreateQueued($"<drain{i}@ex.com>", InboundArticleProducer.TakeThis);
            Assert.True(queue.TryEnqueue(article));
        }

        var stop = writer.StopAsync(CancellationToken.None);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task TakeThis_RealTcp_PipelinedWithoutWaitingForResponses()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 16 });
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        var session = new NntpSession(
            server,
            NullLogger<NntpSession>.Instance,
            articleIngestion: queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();

        _ = await ReadSocketLineAsync(client);

        var ids = new[] { "<tcp1@ex.com>", "<tcp2@ex.com>", "<tcp3@ex.com>" };
        var batch = new StringBuilder();
        foreach (var id in ids)
        {
            batch.Append(BuildTakeThis(id, CanonicalArticleText.Destuffed(id, "body\r\n")));
        }

        var bytes = Encoding.ASCII.GetBytes(batch.ToString());
        await client.SendAsync(bytes);

        foreach (var id in ids)
        {
            Assert.Equal($"239 {id}", await ReadSocketLineAsync(client));
        }

        for (var i = 0; i < ids.Length; i++)
        {
            var article = await queue.DequeueAsync(CancellationToken.None);
            Assert.Equal(ids[i], article!.MessageId);
        }

        await client.SendAsync("QUIT\r\n"u8.ToArray());
        _ = await ReadSocketLineAsync(client);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MultilineReader_Unit_UnstuffsAndTerminates()
    {
        var pipe = new Pipe();
        var wire = Encoding.ASCII.GetBytes("..leading\r\nplain\r\n.\r\n");
        await pipe.Writer.WriteAsync(wire);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        var result = await NntpMultilineDataReader
            .ReadArticleAsync(pipe.Reader, 1024, CancellationToken.None);

        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal(".leading\r\nplain\r\n", Encoding.ASCII.GetString(result.Payload.Span));
    }

    [Fact]
    public async Task TakeThis_UnknownNewsgroup_WantTrash_Returns239()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<wanttrash-take@example.com>";
        await duplex.WriteClientAsync(
            BuildTakeThis(id, CanonicalArticleText.Destuffed(id, "body\r\n", "unknown.un.carried")));
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());

        var article = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("unknown.un.carried", Encoding.ASCII.GetString(article!.Record.Newsgroups));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_NewsWriterFailure_DoesNotEmitSecondResponseOrRequeue()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        var throwing = new ThrowingNewsLogWriter();
        var writer = new IncomingSpoolWriterService(
            queue,
            Options.Create(new NntpdOptions
            {
                ArticleIngestion = new ArticleIngestionOptions(),
                Transit = new TransitOptions { WantTrash = true, LogTrash = true },
            }),
            NullLogger<IncomingSpoolWriterService>.Instance,
            newsLog: throwing,
            catalogue: new StaticNewsgroupCatalogue(
                NewsgroupSnapshot.Create(
                    [new NewsgroupDefinition("alt.test", string.Empty, 2, 1, NewsgroupPostingStatus.Allowed)])));
        await writer.StartAsync(CancellationToken.None);

        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<news-fail@example.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, CanonicalArticleText.Destuffed(id)));
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
        queue.Complete();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await writer.StopAsync(wait.Token);

        Assert.Equal(1, throwing.WriteCalls);
        Assert.Equal(0, queue.Count);
    }

    private static string BuildTakeThis(string messageId, string articleWithoutTerminator) =>
        $"TAKETHIS {messageId}\r\n{articleWithoutTerminator}.\r\n";

    private static async Task<string> ReadSocketLineAsync(Socket socket)
    {
        var buffer = new byte[1];
        var line = new StringBuilder();
        while (true)
        {
            var n = await socket.ReceiveAsync(buffer, SocketFlags.None);
            if (n == 0)
            {
                break;
            }

            line.Append((char)buffer[0]);
            if (line.Length >= 2 && line[^2] == '\r' && line[^1] == '\n')
            {
                return line.ToString(0, line.Length - 2);
            }
        }

        return line.ToString();
    }

    private sealed class TakeThisDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer;
        private readonly Pipe _serverToClient;

        private TakeThisDuplex(PipeOptions? outputOptions)
        {
            _clientToServer = new Pipe();
            _serverToClient = new Pipe(outputOptions ?? PipeOptions.Default);
        }

        public static Task<TakeThisDuplex> CreateAsync(long? pauseWriterThreshold = null)
        {
            PipeOptions? outputOptions = null;
            if (pauseWriterThreshold is { } pause)
            {
                outputOptions = new PipeOptions(
                    pauseWriterThreshold: pause,
                    resumeWriterThreshold: pause,
                    useSynchronizationContext: false);
            }

            return Task.FromResult(new TakeThisDuplex(outputOptions));
        }

        public NntpSession CreateSession(
            IArticleIngestionQueue queue,
            ITransitPeerAuthorization? transitPeers = null,
            System.Net.IPAddress? clientAddress = null,
            VectorNNTPD.TransitService.History.IHistoryDb? historyDb = null,
            IPostFilter? postFilter = null,
            INewsLogWriter? newsLog = null,
            TransitOptions? transit = null,
            INewsgroupCatalogue? catalogue = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(
                    new System.Net.IPEndPoint(clientAddress ?? System.Net.IPAddress.Loopback, 119)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                transitPeerAuthorization: transitPeers,
                historyDb: historyDb,
                postFilter: postFilter,
                newsgroupCatalogue: catalogue,
                newsLog: newsLog,
                transit: transit);
        }

        public async Task WriteClientLineAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task WriteClientAsync(string payload)
        {
            var bytes = Encoding.ASCII.GetBytes(payload);
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task CompleteClientInputAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
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

    private sealed class PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity) : INntpConnection
    {
        private readonly CancellationTokenSource _closed = new();
        private int _compressed;

        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
        public ConnectionClientIdentity ClientIdentity { get; } = identity;
        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public bool IsTls => false;
        public bool IsCompressed => Volatile.Read(ref _compressed) == 1;
        public bool IsCompleted => _closed.IsCancellationRequested;
        public long OutboundIdleVersion => 0;
        public CancellationToken ConnectionClosed => _closed.Token;

        public Task CompleteAsync(Exception? exception = null)
        {
            _closed.Cancel();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _closed.Dispose();
            return ValueTask.CompletedTask;
        }

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpgradeToTlsAsync(
            VectorNNTPD.Common.Networking.Certificates.ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _compressed, 1);
            return Task.CompletedTask;
        }

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipherSuite)
        {
            tlsVersion = string.Empty;
            cipherSuite = string.Empty;
            return false;
        }
    }
}
