using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Newsgroups;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.TransitService.Tests.ArticleIngestion;

/// <summary>Post-queue news disposition and INN <c>news</c> writer contracts.</summary>
public sealed class IncomingSpoolNewsLogTests
{
    [Fact]
    public async Task NormalAcceptedArticle_ProducesPlus()
    {
        var news = new RecordingNewsLogWriter();
        await RunTakeThisAsync(
            CanonicalArticleText.Destuffed("<plus@example.com>", newsgroups: "alt.test"),
            "<plus@example.com>",
            news,
            Catalogue("alt.test"));

        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Accepted, evt.Disposition);
        Assert.True(evt.MessageId.Span.SequenceEqual("<plus@example.com>"u8));
        Assert.True(evt.Feed.IsEmpty);
        Assert.True(evt.Sites.IsEmpty);
        Assert.True(evt.Size > 0);
    }

    [Fact]
    public async Task AcceptedJunkArticle_ProducesJ()
    {
        var news = new RecordingNewsLogWriter();
        await RunTakeThisAsync(
            CanonicalArticleText.Destuffed("<junk@example.com>", newsgroups: "unknown.un.carried"),
            "<junk@example.com>",
            news,
            Catalogue("alt.test"),
            wantTrash: true,
            logTrash: true);

        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Junk, evt.Disposition);
        Assert.Equal(Uncarried("unknown.un.carried"), Encoding.ASCII.GetString(evt.Reason.Span));
    }

    [Fact]
    public async Task PeerOnlyCatalogueHit_ProducesJWithPeerOnlyReason()
    {
        var news = new RecordingNewsLogWriter();
        await RunTakeThisAsync(
            CanonicalArticleText.Destuffed("<peeronly@example.com>", newsgroups: "junk.local"),
            "<peeronly@example.com>",
            news,
            CatalogueWith(("junk.local", NewsgroupPostingStatus.PeerOnly)),
            wantTrash: false,
            logTrash: true);

        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Junk, evt.Disposition);
        Assert.Equal(PeerOnlyReason("junk.local"), Encoding.ASCII.GetString(evt.Reason.Span));
    }

    [Fact]
    public async Task NamedInboundFeed_IsCopiedOntoAcceptedEvent_AndFormattedWithoutSession()
    {
        var news = new RecordingNewsLogWriter();
        var inbound = CanonicalArticleText.CreateQueued(
            "<plus@example.com>",
            InboundArticleProducer.TakeThis,
            feed: "BlueWorldHosting"u8.ToArray());
        await RunWorkerAsync(inbound, news, Catalogue("alt.test"));
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Accepted, evt.Disposition);
        Assert.True(evt.Feed.Span.SequenceEqual("BlueWorldHosting"u8));
        Assert.Equal(inbound.Payload.Length, evt.Size);
        var line = FormatNews(in evt);
        Assert.Contains(" + BlueWorldHosting <plus@example.com> " + evt.Size + " ?", line, StringComparison.Ordinal);
        Assert.DoesNotContain(" + ? ", line, StringComparison.Ordinal);
        Assert.EndsWith(" ?\n", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedInboundFeed_Junk_KeepsPeerAndOmitsOutboundSites()
    {
        var news = new RecordingNewsLogWriter();
        var inbound = CanonicalArticleText.CreateQueued(
            "<junk@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "unknown.un.carried",
            feed: "BlueWorldHosting"u8.ToArray());
        await RunWorkerAsync(
            inbound,
            news,
            Catalogue("alt.test"),
            wantTrash: true,
            logTrash: true);
        var evt = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Junk, evt.Disposition);
        Assert.True(evt.Feed.Span.SequenceEqual("BlueWorldHosting"u8));
        var line = FormatNews(in evt);
        Assert.Contains(" j BlueWorldHosting <junk@example.com> " + evt.Size + " " + Uncarried("unknown.un.carried"), line, StringComparison.Ordinal);
        Assert.DoesNotContain("?", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WantTrash_DoesNotRewriteOriginalNewsgroupsHeader()
    {
        var news = new RecordingNewsLogWriter();
        const string groups = "unknown.un.carried";
        await RunTakeThisAsync(
            CanonicalArticleText.Destuffed("<keep-ng@example.com>", newsgroups: groups),
            "<keep-ng@example.com>",
            news,
            Catalogue("alt.test"));

        var keep = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Junk, keep.Disposition);
        Assert.Equal(Uncarried(groups), Encoding.ASCII.GetString(keep.Reason.Span));
    }

    [Fact]
    public async Task WantTrash_AppliesToIhaveUnknownGroup()
    {
        var news = new RecordingNewsLogWriter();
        var inbound = CanonicalArticleText.CreateQueued(
            "<ihave-junk@example.com>",
            InboundArticleProducer.IHave,
            newsgroups: "not.carried.here");
        await RunWorkerAsync(
            inbound,
            news,
            Catalogue("alt.test"),
            wantTrash: true,
            logTrash: true);

        var ihaveJunk = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Junk, ihaveJunk.Disposition);
        Assert.Equal(Uncarried("not.carried.here"), Encoding.ASCII.GetString(ihaveJunk.Reason.Span));
        Assert.Contains("Newsgroups: not.carried.here"u8, inbound.Payload.Span);
    }

    [Fact]
    public async Task LogTrashFalse_SuppressesJunkLine_WithoutRejecting()
    {
        var news = new RecordingNewsLogWriter();
        await RunTakeThisAsync(
            CanonicalArticleText.Destuffed("<quiet-junk@example.com>", newsgroups: "unknown.group"),
            "<quiet-junk@example.com>",
            news,
            Catalogue("alt.test"),
            wantTrash: true,
            logTrash: false);

        Assert.Empty(news.Events);
        Assert.Equal(0, news.WriteCalls);
    }

    [Fact]
    public async Task LogTrashFalse_StillLogsNormalAcceptedArticles()
    {
        var news = new RecordingNewsLogWriter();
        await RunTakeThisAsync(
            CanonicalArticleText.Destuffed("<still-plus@example.com>", newsgroups: "alt.test"),
            "<still-plus@example.com>",
            news,
            Catalogue("alt.test"),
            wantTrash: true,
            logTrash: false);

        var stillPlus = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Accepted, stillPlus.Disposition);
        Assert.True(stillPlus.Reason.IsEmpty);
    }

    [Fact]
    public async Task MessageId_IsActualArticleMessageId_NotArticleId()
    {
        const string id = "<AbC-Preserve@Example.COM>";
        var news = new RecordingNewsLogWriter();
        var created = CreateRecord(CanonicalArticleText.Destuffed(id, newsgroups: "alt.test"));
        await RunWorkerAsync(
            ArticleRecordIngress.CreateQueued(id, created, Identity(), DateTimeOffset.UtcNow, InboundArticleProducer.TakeThis),
            news,
            Catalogue("alt.test"));

        Assert.True(Assert.Single(news.Events).MessageId.Span.SequenceEqual(Encoding.ASCII.GetBytes(id)));
        Assert.True(created.MessageId.SequenceEqual(Encoding.ASCII.GetBytes(id)));
        Span<byte> artId = stackalloc byte[ArticleId.Length];
        created.ArtId.CopyTo(artId);
        Assert.False(news.Events[0].MessageId.Span.SequenceEqual(artId));
    }

    [Fact]
    public async Task TakeThisCanonicalRecord_IsNotReparsedToGenerateNews()
    {
        var news = new RecordingNewsLogWriter();
        var created = CreateRecord(CanonicalArticleText.Destuffed("<noreparse@example.com>"));
        var inbound = ArticleRecordIngress.CreateQueued(
            "<noreparse@example.com>",
            created,
            Identity(),
            DateTimeOffset.UtcNow,
            InboundArticleProducer.TakeThis);
        await RunWorkerAsync(inbound, news, Catalogue("alt.test"));

        Assert.True(inbound.Record.ArtData.Equals(created.ArtData));
        Assert.True(news.Events[0].MessageId.Span.SequenceEqual(created.MessageId));
        Assert.True(
            news.Events[0].MessageId.Span.SequenceEqual(
                inbound.Record.ArtData.Span.Slice(
                    inbound.Record.Fields.MessageId.Offset,
                    inbound.Record.Fields.MessageId.Length)));
    }

    [Fact]
    public async Task Post_IsNeverClassifiedAsWantTrashJunk()
    {
        var news = new RecordingNewsLogWriter();
        var created = CreateRecord(CanonicalArticleText.Destuffed("<post@example.com>", newsgroups: "unknown.group"));
        await RunWorkerAsync(
            ArticleRecordIngress.CreateQueued(
                "<post@example.com>",
                created,
                Identity(),
                DateTimeOffset.UtcNow,
                InboundArticleProducer.Post),
            news,
            Catalogue("alt.test"),
            wantTrash: true,
            logTrash: true);

        var post = Assert.Single(news.Events);
        Assert.Equal(NewsLogDisposition.Accepted, post.Disposition);
        Assert.NotEqual(NewsLogDisposition.Junk, post.Disposition);
        Assert.True(post.Reason.IsEmpty);
    }

    [Fact]
    public async Task NewsEvent_IsProducedAfterEnqueue_NotDuringReceiveAdmission()
    {
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var created = CreateRecord(CanonicalArticleText.Destuffed("<post-queue@example.com>"));
        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await queue.EnqueueAsync(
                ArticleRecordIngress.CreateQueued(
                    "<post-queue@example.com>",
                    created,
                    Identity(),
                    DateTimeOffset.UtcNow,
                    InboundArticleProducer.TakeThis),
                CancellationToken.None));
        Assert.Equal(0, news.WriteCalls);

        var writer = CreateWriter(queue, news, Catalogue("alt.test"));
        await writer.StartAsync(CancellationToken.None);
        queue.Complete();
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(1, news.WriteCalls);
        Assert.Equal(1, news.FlushCalls);
        Assert.Equal(NewsLogDisposition.Accepted, Assert.Single(news.Events).Disposition);
    }

    [Fact]
    public async Task Shutdown_FlushesPendingNewsEntries()
    {
        using var dir = new TempDir();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var configuration = NewsTestConfiguration.Create(dir.Path, rollingInterval: "Infinite");
        using var news = new SerilogNewsLogWriter(configuration);
        var writer = new IncomingSpoolWriterService(
            queue,
            Options.Create(new NntpdOptions
            {
                LogDir = dir.Path,
                Transit = new TransitOptions { WantTrash = true, LogTrash = true },
            }),
            NullLogger<IncomingSpoolWriterService>.Instance,
            newsLog: news,
            catalogue: Catalogue("alt.test"));
        await writer.StartAsync(CancellationToken.None);
        var created = CreateRecord(CanonicalArticleText.Destuffed("<shutdown@example.com>"));
        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await queue.EnqueueAsync(
                ArticleRecordIngress.CreateQueued(
                    "<shutdown@example.com>",
                    created,
                    Identity(),
                    DateTimeOffset.UtcNow,
                    InboundArticleProducer.TakeThis),
                CancellationToken.None));
        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
        news.Dispose();

        var text = NewsTestConfiguration.ReadNewsFile(dir.Path);
        Assert.Contains("<shutdown@example.com>", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewsWriterFailure_DoesNotReprocessOrSkipPersister()
    {
        var throwing = new ThrowingNewsLogWriter();
        await RunTakeThisAsync(
            CanonicalArticleText.Destuffed("<fail-news@example.com>"),
            "<fail-news@example.com>",
            throwing,
            Catalogue("alt.test"));

        Assert.Equal(1, throwing.WriteCalls);
    }

    [Fact]
    public void Classify_CarriedNonJunkGroup_IsAccepted()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<carried@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "alt.test");
        Assert.Equal(
            NewsLogDisposition.Accepted,
            IngressNewsDisposition.Classify(inbound, TrashOn(), Catalogue("alt.test")));
    }

    [Fact]
    public void Classify_UnknownGroup_WantTrash_IsJunk()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<unknown@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "unknown.group");
        Assert.Equal(
            NewsLogDisposition.Junk,
            IngressNewsDisposition.Classify(inbound, TrashOn(), Catalogue("alt.test"), out var reason));
        Assert.Equal(Uncarried("unknown.group"), reason);
    }

    [Fact]
    public void Classify_MultipleGroups_OneCarriedNonJunk_IsAccepted()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<mixed@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "unknown.group,alt.test,other.unknown");
        Assert.Equal(
            NewsLogDisposition.Accepted,
            IngressNewsDisposition.Classify(inbound, TrashOn(), Catalogue("alt.test"), out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void Classify_MixedCarriedAndPeerOnly_IsAccepted()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<carried-peer@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "alt.test,junk.local");
        Assert.Equal(
            NewsLogDisposition.Accepted,
            IngressNewsDisposition.Classify(
                inbound,
                TrashOn(),
                CatalogueWith(
                    ("alt.test", NewsgroupPostingStatus.Allowed),
                    ("junk.local", NewsgroupPostingStatus.PeerOnly)),
                out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void Classify_MixedUnknownAndPeerOnly_ReportsOnlyPeerOnlyGroups()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<unknown-peer@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "unknown.group,junk.local,other.unknown");
        Assert.Equal(
            NewsLogDisposition.Junk,
            IngressNewsDisposition.Classify(
                inbound,
                TrashOn(),
                CatalogueWith(("junk.local", NewsgroupPostingStatus.PeerOnly)),
                out var reason));
        Assert.Equal(PeerOnlyReason("junk.local"), reason);
        Assert.DoesNotContain("unknown.group", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("other.unknown", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_MultiplePeerOnlyGroups_ReportsHeaderOrder()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<multi-peer@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "junk.beta, junk.alpha");
        Assert.Equal(
            NewsLogDisposition.Junk,
            IngressNewsDisposition.Classify(
                inbound,
                new TransitOptions { WantTrash = false, LogTrash = true },
                CatalogueWith(
                    ("junk.alpha", NewsgroupPostingStatus.PeerOnly),
                    ("junk.beta", NewsgroupPostingStatus.PeerOnly)),
                out var reason));
        Assert.Equal(PeerOnlyReason("junk.beta", "junk.alpha"), reason);
    }

    [Fact]
    public void Classify_MultipleUnknownGroups_WantTrash_IsJunk()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<multi-unknown@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "unknown.one,unknown.two");
        Assert.Equal(
            NewsLogDisposition.Junk,
            IngressNewsDisposition.Classify(inbound, TrashOn(), Catalogue("alt.test"), out var reason));
        Assert.Equal(Uncarried("unknown.one", "unknown.two"), reason);
        Assert.DoesNotContain("alt.test", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_PeerOnlyJunkGroup_IsJunk()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<peeronly@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "junk.local");
        Assert.Equal(
            NewsLogDisposition.Junk,
            IngressNewsDisposition.Classify(
                inbound,
                new TransitOptions { WantTrash = false, LogTrash = true },
                CatalogueWith(("junk.local", NewsgroupPostingStatus.PeerOnly)),
                out var reason));
        Assert.Equal(PeerOnlyReason("junk.local"), reason);
    }

    [Fact]
    public void Classify_WantTrashFalse_UnknownGroup_IsAccepted()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<notrash@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "unknown.group");
        Assert.Equal(
            NewsLogDisposition.Accepted,
            IngressNewsDisposition.Classify(
                inbound,
                new TransitOptions { WantTrash = false, LogTrash = true },
                Catalogue("alt.test"),
                out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void TryCreateEvent_WantTrashUnknown_CarriesNewsgroupNotCarried()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<try-unknown@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "unknown.one,unknown.two");
        Assert.True(
            IngressNewsDisposition.TryCreateEvent(
                inbound,
                TrashOn(),
                Catalogue("alt.test"),
                new DateTimeOffset(2024, 1, 5, 0, 0, 0, 0, TimeSpan.Zero),
                out var evt));
        Assert.Equal(NewsLogDisposition.Junk, evt.Disposition);
        Assert.Equal(Uncarried("unknown.one", "unknown.two"), Encoding.ASCII.GetString(evt.Reason.Span));
        Assert.Equal(
            $"Jan  5 00:00:00.000 j ? <try-unknown@example.com> {evt.Size} newsgroup not carried: unknown.one, unknown.two\n",
            FormatNews(in evt));
    }

    [Fact]
    public void TryCreateEvent_PeerOnly_CarriesPeerOnlyReason()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<try-peer@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "junk.local");
        Assert.True(
            IngressNewsDisposition.TryCreateEvent(
                inbound,
                new TransitOptions { WantTrash = false, LogTrash = true },
                CatalogueWith(("junk.local", NewsgroupPostingStatus.PeerOnly)),
                new DateTimeOffset(2024, 1, 5, 0, 0, 0, 0, TimeSpan.Zero),
                out var evt));
        Assert.Equal(NewsLogDisposition.Junk, evt.Disposition);
        Assert.Equal(PeerOnlyReason("junk.local"), Encoding.ASCII.GetString(evt.Reason.Span));
        Assert.Equal(
            $"Jan  5 00:00:00.000 j ? <try-peer@example.com> {evt.Size} peer-only: junk.local\n",
            FormatNews(in evt));
    }

    [Fact]
    public void IsUncarriedWantTrashRejection_WantTrashFalse_UnknownGroup_IsTrue()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<uncarried@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "unknown.group");
        Assert.True(
            IngressNewsDisposition.IsUncarriedWantTrashRejection(
                inbound,
                new TransitOptions { WantTrash = false, LogTrash = false },
                Catalogue("alt.test"),
                out var reason));
        Assert.Equal(Uncarried("unknown.group"), reason);
    }

    [Fact]
    public void IsUncarriedWantTrashRejection_MultipleUnknown_ReportsHeaderOrder()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<multi-uncarried@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "unknown.two,unknown.one");
        Assert.True(
            IngressNewsDisposition.IsUncarriedWantTrashRejection(
                inbound,
                new TransitOptions { WantTrash = false, LogTrash = false },
                Catalogue("alt.test"),
                out var reason));
        Assert.Equal(Uncarried("unknown.two", "unknown.one"), reason);
    }

    [Fact]
    public void IsUncarriedWantTrashRejection_WantTrashTrue_IsFalse()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<trash-on@example.com>",
            InboundArticleProducer.IHave,
            newsgroups: "unknown.group");
        Assert.False(
            IngressNewsDisposition.IsUncarriedWantTrashRejection(
                inbound,
                new TransitOptions { WantTrash = true },
                Catalogue("alt.test")));
    }

    [Fact]
    public void IsUncarriedWantTrashRejection_PeerOnly_IsFalse()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<peeronly-reject@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "junk.local");
        Assert.False(
            IngressNewsDisposition.IsUncarriedWantTrashRejection(
                inbound,
                new TransitOptions { WantTrash = false },
                CatalogueWith(("junk.local", NewsgroupPostingStatus.PeerOnly))));
    }

    [Fact]
    public void IsUncarriedWantTrashRejection_PostOrMissingCatalogue_IsFalse()
    {
        var inbound = CanonicalArticleText.CreateQueued(
            "<post-uncarried@example.com>",
            InboundArticleProducer.Post,
            newsgroups: "unknown.group");
        Assert.False(
            IngressNewsDisposition.IsUncarriedWantTrashRejection(
                inbound,
                new TransitOptions { WantTrash = false },
                Catalogue("alt.test")));
        Assert.False(
            IngressNewsDisposition.IsUncarriedWantTrashRejection(
                CanonicalArticleText.CreateQueued(
                    "<nocat-uncarried@example.com>",
                    InboundArticleProducer.TakeThis,
                    newsgroups: "unknown.group"),
                new TransitOptions { WantTrash = false },
                catalogue: null));
    }

    [Fact]
    public void Classify_WithoutCatalogue_IsAcceptedRatherThanInventedJunk()
    {
        var created = CreateRecord(CanonicalArticleText.Destuffed("<nocat@example.com>", newsgroups: "unknown.group"));
        var inbound = ArticleRecordIngress.CreateQueued(
            "<nocat@example.com>",
            created,
            Identity(),
            DateTimeOffset.UtcNow,
            InboundArticleProducer.TakeThis);
        Assert.Equal(
            NewsLogDisposition.Accepted,
            IngressNewsDisposition.Classify(inbound, new TransitOptions { WantTrash = true }, catalogue: null));
    }

    private static async Task RunTakeThisAsync(
        string destuffed,
        string messageId,
        INewsLogWriter news,
        INewsgroupCatalogue catalogue,
        bool wantTrash = true,
        bool logTrash = true)
    {
        var created = CreateRecord(destuffed);
        await RunWorkerAsync(
            ArticleRecordIngress.CreateQueued(
                messageId,
                created,
                Identity(),
                DateTimeOffset.UtcNow,
                InboundArticleProducer.TakeThis),
            news,
            catalogue,
            wantTrash,
            logTrash);
    }

    private static async Task RunWorkerAsync(
        InboundArticle inbound,
        INewsLogWriter news,
        INewsgroupCatalogue? catalogue,
        bool wantTrash = true,
        bool logTrash = true)
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = CreateWriter(queue, news, catalogue, wantTrash, logTrash);
        await writer.StartAsync(CancellationToken.None);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(inbound, CancellationToken.None));
        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
    }

    private static IncomingSpoolWriterService CreateWriter(
        IArticleIngestionQueue queue,
        INewsLogWriter news,
        INewsgroupCatalogue? catalogue,
        bool wantTrash = true,
        bool logTrash = true) =>
        new(
            queue,
            Options.Create(new NntpdOptions
            {
                ArticleIngestion = new ArticleIngestionOptions(),
                Transit = new TransitOptions { WantTrash = wantTrash, LogTrash = logTrash },
            }),
            NullLogger<IncomingSpoolWriterService>.Instance,
            newsLog: news,
            catalogue: catalogue);

    private static ArticleRecord CreateRecord(string destuffed)
    {
        var created = ArticleRecordIngress.TryCreateFromDestuffed(
            new NntpArticleParser("nntpd01.usenet.ninja"),
            Encoding.ASCII.GetBytes(destuffed));
        Assert.True(created.IsAccepted);
        return created.Record;
    }

    private static string FormatNews(in NewsLogEvent evt)
    {
        var timestamp = evt.Timestamp == default
            ? new DateTimeOffset(2024, 1, 5, 0, 0, 0, 0, TimeSpan.Zero)
            : evt.Timestamp;
        var buffer = new byte[NewsLogLineFormatter.RequiredLength(in evt) + 16];
        var written = NewsLogLineFormatter.Write(buffer, in evt, timestamp);
        return Encoding.ASCII.GetString(buffer.AsSpan(0, written));
    }

    private static string Uncarried(params string[] groups) =>
        IngressNewsReasons.WithGroups(IngressNewsReasons.NewsgroupNotCarried, groups);

    private static string PeerOnlyReason(params string[] groups) =>
        IngressNewsReasons.WithGroups(IngressNewsReasons.PeerOnly, groups);

    private static TransitOptions TrashOn() => new() { WantTrash = true, LogTrash = true };

    private static INewsgroupCatalogue Catalogue(params string[] groups) =>
        CatalogueWith(groups.Select(static name => (name, NewsgroupPostingStatus.Allowed)).ToArray());

    private static INewsgroupCatalogue CatalogueWith(params (string Name, NewsgroupPostingStatus Status)[] groups) =>
        new StaticNewsgroupCatalogue(
            NewsgroupSnapshot.Create(
                groups.Select(static g =>
                    new NewsgroupDefinition(g.Name, string.Empty, 2, 1, g.Status)).ToArray()));

    private static ConnectionClientIdentity Identity() =>
        ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119));

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "vectornntp-news-stop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            // codeql[cs/catch-of-all-exceptions] Best-effort test cleanup must tolerate any filesystem or disposal failure.
            catch (Exception ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
                // best-effort
            }
        }
    }
}
