using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.ArticleIngestion;

/// <summary>Post-queue Path-survey capture from canonical ArticleRecord.Path.</summary>
public sealed class IncomingSpoolPathSurveyTests
{
    [Fact]
    public async Task CanonicalArticleRecordPath_IsWritten_WithoutSynthesis()
    {
        var paths = new RecordingPathSurveyWriter();
        var news = new RecordingNewsLogWriter();
        var inbound = CanonicalArticleText.CreateQueued(
            "<path@example.com>",
            InboundArticleProducer.TakeThis);
        await RunWorkerAsync(inbound, news, paths, Catalogue("alt.test"));

        var observed = Assert.Single(paths.Paths);
        Assert.True(observed.AsSpan().SequenceEqual(inbound.Record.Path));
        var line = Encoding.Latin1.GetString(FormatPath(observed));
        Assert.Equal("Path: " + Encoding.Latin1.GetString(inbound.Record.Path) + "\n", line);
        Assert.DoesNotContain("giganews", line, StringComparison.Ordinal);
        Assert.StartsWith("Path: ", line, StringComparison.Ordinal);
        Assert.Equal(1, news.WriteCalls);
        Assert.Equal(NewsLogDisposition.Accepted, Assert.Single(news.Events).Disposition);
    }

    [Fact]
    public async Task MultipleArticles_ProduceSequentialPathRecords()
    {
        var paths = new RecordingPathSurveyWriter();
        var first = CanonicalArticleText.CreateQueued("<one@example.com>", InboundArticleProducer.TakeThis);
        var second = CanonicalArticleText.CreateQueued("<two@example.com>", InboundArticleProducer.TakeThis);
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = CreateWriter(queue, new RecordingNewsLogWriter(), paths, Catalogue("alt.test"));
        await writer.StartAsync(CancellationToken.None);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(first, CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(second, CancellationToken.None));
        queue.Complete();
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(2, paths.WriteCalls);
        Assert.True(paths.Paths[0].AsSpan().SequenceEqual(first.Record.Path));
        Assert.True(paths.Paths[1].AsSpan().SequenceEqual(second.Record.Path));
        Assert.Equal(1, paths.FlushCalls);
    }

    [Fact]
    public async Task JunkWithLogTrashFalse_StillWritesPath_AndOmitsNews()
    {
        var paths = new RecordingPathSurveyWriter();
        var news = new RecordingNewsLogWriter();
        var inbound = CanonicalArticleText.CreateQueued(
            "<junk-path@example.com>",
            InboundArticleProducer.TakeThis,
            newsgroups: "unknown.un.carried");
        await RunWorkerAsync(
            inbound,
            news,
            paths,
            Catalogue("alt.test"),
            wantTrash: true,
            logTrash: false);

        Assert.Equal(0, news.WriteCalls);
        var observed = Assert.Single(paths.Paths);
        Assert.True(observed.AsSpan().SequenceEqual(inbound.Record.Path));
    }

    [Fact]
    public async Task PathWriterFailure_DoesNotSkipNewsOrPersister()
    {
        var throwing = new ThrowingPathSurveyWriter();
        var news = new RecordingNewsLogWriter();
        await RunTakeThisAsync(
            CanonicalArticleText.Destuffed("<fail-path@example.com>"),
            "<fail-path@example.com>",
            news,
            throwing,
            Catalogue("alt.test"));

        Assert.Equal(1, throwing.WriteCalls);
        Assert.Equal(1, news.WriteCalls);
    }

    [Fact]
    public async Task NewsWriterFailure_StillWritesPathAndPersists()
    {
        var throwingNews = new ThrowingNewsLogWriter();
        var paths = new RecordingPathSurveyWriter();
        var inbound = CanonicalArticleText.CreateQueued(
            "<fail-news-path@example.com>",
            InboundArticleProducer.TakeThis);
        await RunWorkerAsync(inbound, throwingNews, paths, Catalogue("alt.test"));

        Assert.Equal(1, throwingNews.WriteCalls);
        Assert.True(Assert.Single(paths.Paths).AsSpan().SequenceEqual(inbound.Record.Path));
    }

    [Fact]
    public async Task PathIsNotWrittenUntilAfterOverviewConfirm()
    {
        var paths = new RecordingPathSurveyWriter();
        var news = new RecordingNewsLogWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var created = CreateRecord(CanonicalArticleText.Destuffed("<pre-handoff@example.com>"));
        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await queue.EnqueueAsync(
                ArticleRecordIngress.CreateQueued(
                    "<pre-handoff@example.com>",
                    created,
                    Identity(),
                    DateTimeOffset.UtcNow,
                    InboundArticleProducer.TakeThis),
                CancellationToken.None));
        Assert.Equal(0, paths.WriteCalls);
        Assert.Equal(0, news.WriteCalls);

        var writer = CreateWriter(queue, news, paths, Catalogue("alt.test"));
        await writer.StartAsync(CancellationToken.None);
        queue.Complete();
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(1, paths.WriteCalls);
        Assert.Equal(1, news.WriteCalls);
    }

    [Fact]
    public async Task LargeSequentialInput_IsCountedWithoutRetainingTheStream()
    {
        var paths = new CountingPathSurveyWriter();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 64 });
        var writer = CreateWriter(queue, new RecordingNewsLogWriter(), paths, Catalogue("alt.test"));
        await writer.StartAsync(CancellationToken.None);
        const int count = 32;
        long expectedBytes = 0;
        for (var i = 0; i < count; i++)
        {
            var inbound = CanonicalArticleText.CreateQueued(
                $"<seq{i}@example.com>",
                InboundArticleProducer.TakeThis);
            expectedBytes += inbound.Record.Path.Length;
            Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(inbound, CancellationToken.None));
        }

        queue.Complete();
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(count, paths.WriteCalls);
        Assert.Equal(expectedBytes, paths.Bytes);
        Assert.Null(paths.GetType().GetProperty("Paths"));
    }

    [Fact]
    public async Task Shutdown_FlushesPathSurveyFile()
    {
        using var dir = new TempDir();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var configuration = PathSurveyTestConfiguration.Create(dir.Path, rollingInterval: "Infinite");
        using var paths = new SerilogPathSurveyWriter(configuration);
        var created = CreateRecord(CanonicalArticleText.Destuffed("<shutdown-path@example.com>"));
        var inbound = ArticleRecordIngress.CreateQueued(
            "<shutdown-path@example.com>",
            created,
            Identity(),
            DateTimeOffset.UtcNow,
            InboundArticleProducer.TakeThis);
        var writer = CreateWriter(queue, new RecordingNewsLogWriter(), paths, Catalogue("alt.test"));
        await writer.StartAsync(CancellationToken.None);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(inbound, CancellationToken.None));
        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
        paths.Dispose();

        var text = PathSurveyTestConfiguration.ReadInpathsFile(dir.Path);
        Assert.Equal("Path: " + Encoding.Latin1.GetString(inbound.Record.Path) + "\n", text);
    }

    private static async Task RunTakeThisAsync(
        string destuffed,
        string messageId,
        INewsLogWriter news,
        IPathSurveyWriter paths,
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
            paths,
            catalogue,
            wantTrash,
            logTrash);
    }

    private static async Task RunWorkerAsync(
        InboundArticle inbound,
        INewsLogWriter news,
        IPathSurveyWriter paths,
        INewsgroupCatalogue? catalogue,
        bool wantTrash = true,
        bool logTrash = true)
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = CreateWriter(queue, news, paths, catalogue, wantTrash, logTrash);
        await writer.StartAsync(CancellationToken.None);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(inbound, CancellationToken.None));
        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
    }

    private static IncomingSpoolWriterService CreateWriter(
        IArticleIngestionQueue queue,
        INewsLogWriter news,
        IPathSurveyWriter paths,
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
            catalogue: catalogue,
            pathSurvey: paths);

    private static ArticleRecord CreateRecord(string destuffed)
    {
        var created = ArticleRecordIngress.TryCreateFromDestuffed(
            new NntpArticleParser("nntpd01.usenet.ninja"),
            Encoding.ASCII.GetBytes(destuffed));
        Assert.True(created.IsAccepted);
        return created.Record;
    }

    private static byte[] FormatPath(ReadOnlySpan<byte> path)
    {
        var buffer = new byte[PathSurveyLineFormatter.RequiredLength(path)];
        PathSurveyLineFormatter.Write(buffer, path);
        return buffer;
    }

    private static INewsgroupCatalogue Catalogue(params string[] groups) =>
        new StaticNewsgroupCatalogue(
            NewsgroupSnapshot.Create(
                groups.Select(static name =>
                    new NewsgroupDefinition(name, string.Empty, 2, 1, NewsgroupPostingStatus.Allowed)).ToArray()));

    private static ConnectionClientIdentity Identity() =>
        ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119));

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "vectornntp-inpaths-stop-" + Guid.NewGuid().ToString("N"));
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
