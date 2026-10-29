using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.ArticleIngestion.OverviewDb;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.ArticleIngestion;

/// <summary>
/// Demonstrates that article workers are not serialized behind OverviewDB
/// publisher-confirm latency after the intermediate work-queue handoff, and that
/// the publisher stage itself pipelines confirms asynchronously.
/// </summary>
public sealed class IngestionThroughputCeilingTests
{
    private static readonly TimeSpan ConfirmLatency = TimeSpan.FromMilliseconds(18);

    [Fact]
    public async Task ArticlePersistPath_IsNotSerializedBehindPublisherConfirmLatency()
    {
        const int articleCount = 32;
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions { QueueCapacity = articleCount + 8 },
            transitQueueMemoryLimit: 64 * 1024 * 1024);
        var overview = new RecordingOverviewDbHandoffPublisher
        {
            ConfirmDelay = ConfirmLatency,
            OutstandingWindow = 100,
        };
        using var done = new CountdownEvent(articleCount);
        var paths = new CountingPathSurvey(done);

        var writer = new IncomingSpoolWriterService(
            queue,
            Options.Create(new NntpdOptions
            {
                ArticleIngestion = new ArticleIngestionOptions
                {
                    MinWorkers = 4,
                    MaxWorkers = 4,
                    OverviewDbMinPublisherWorkers = 1,
                    OverviewDbMaxPublisherWorkers = 1,
                    OverviewDbPublisherBatchSize = 100,
                    ScaleIntervalSeconds = 3600,
                },
                Transit = new TransitOptions { WantTrash = true, LogTrash = true },
            }),
            NullLogger<IncomingSpoolWriterService>.Instance,
            catalogue: new StaticNewsgroupCatalogue(
                NewsgroupSnapshot.Create(
                [
                    new NewsgroupDefinition("alt.test", string.Empty, 2, 1, NewsgroupPostingStatus.Allowed),
                ])),
            overviewHandoff: overview,
            pathSurvey: paths);

        await writer.StartAsync(CancellationToken.None);
        for (var i = 0; i < articleCount; i++)
        {
            var inbound = CanonicalArticleText.CreateQueued(
                $"<throughput-decouple-{i}@example.test>",
                InboundArticleProducer.TakeThis,
                body: "throughput-body\r\n",
                newsgroups: "alt.test");
            Assert.Equal(
                ArticleEnqueueResult.Accepted,
                await queue.EnqueueAsync(inbound, CancellationToken.None));
        }

        var sw = Stopwatch.StartNew();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for Path survey.");
        sw.Stop();

        Assert.True(
            sw.Elapsed < TimeSpan.FromMilliseconds(articleCount * ConfirmLatency.TotalMilliseconds / 2),
            $"Article persist took {sw.Elapsed.TotalMilliseconds:F0} ms; expected well under serial confirm ceiling.");

        using var confirmWait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (overview.Payloads.Count < articleCount)
        {
            confirmWait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, confirmWait.Token);
        }

        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
        Assert.Equal(articleCount, overview.Payloads.Count);
        Assert.Equal(articleCount, paths.WriteCalls);
    }

    [Fact]
    public async Task PublisherStage_PipelinesConfirms_WithOutstandingWindow()
    {
        var work = new OverviewDbWorkQueue(memoryLimitBytes: 16 * 1024 * 1024);
        var overview = new RecordingOverviewDbHandoffPublisher
        {
            ConfirmDelay = ConfirmLatency,
            OutstandingWindow = 100,
        };
        var options = new ArticleIngestionOptions
        {
            OverviewDbMinPublisherWorkers = 1,
            OverviewDbMaxPublisherWorkers = 1,
            OverviewDbPublisherBatchSize = 100,
            OverviewDbPublisherShutdownSeconds = 5,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new OverviewDbPublisherPool(work, overview, options, NullLogger.Instance);
        var run = pool.RunAsync(cts.Token);

        const int count = 40;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(
                ArticleEnqueueResult.Accepted,
                await work.EnqueueAsync(
                    new OverviewDbWorkItem([(byte)i], $"<pipe-{i}@test>"),
                    CancellationToken.None));
        }

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (overview.Payloads.Count < count)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, wait.Token);
        }

        sw.Stop();
        Assert.True(
            sw.Elapsed < TimeSpan.FromMilliseconds(count * ConfirmLatency.TotalMilliseconds / 2),
            $"Publisher stage took {sw.Elapsed.TotalMilliseconds:F0} ms; expected pipelined throughput.");

        work.Complete();
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class CountingPathSurvey(CountdownEvent done) : IPathSurveyWriter
    {
        private int _writeCalls;

        public int WriteCalls => Volatile.Read(ref _writeCalls);

        public void Write(ReadOnlySpan<byte> canonicalPath)
        {
            Interlocked.Increment(ref _writeCalls);
            done.Signal();
        }

        public void Flush()
        {
        }
    }
}
