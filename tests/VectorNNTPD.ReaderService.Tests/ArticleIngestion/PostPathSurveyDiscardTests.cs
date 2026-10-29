using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.ArticleIngestion.OverviewDb;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Storage;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.ArticleIngestion;

/// <summary>
/// After Path survey the ingestion worker discards the article and does not
/// persist or place it.
/// </summary>
public sealed class PostPathSurveyDiscardTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-04T00:00:00Z");

    [Theory]
    [InlineData(InboundArticleProducer.TakeThis)]
    [InlineData(InboundArticleProducer.IHave)]
    [InlineData(InboundArticleProducer.Post)]
    [InlineData(InboundArticleProducer.BackFiller)]
    public async Task AfterPathSurvey_DoesNotPersistSelectOrPlace(InboundArticleProducer producer)
    {
        var observed = await RunAsync(
            CanonicalArticleText.CreateQueued($"<discard-{producer}@example.test>", producer));

        Assert.Equal(0, observed.Registry.ActiveCalls);
        Assert.Equal(0, observed.Placement.Calls);
        Assert.Equal(1, observed.Paths.WriteCalls);
        Assert.Single(observed.Overview.Payloads);
        Assert.Equal(0, observed.Queue.Count);
    }

    [Fact]
    public async Task SubsequentArticle_IsProcessedAfterTheFirstIsDiscarded()
    {
        var first = CanonicalArticleText.CreateQueued("<discard-first@example.test>", InboundArticleProducer.TakeThis);
        var second = CanonicalArticleText.CreateQueued("<discard-second@example.test>", InboundArticleProducer.BackFiller);
        var observed = await RunAsync(first, second);

        Assert.Equal(0, observed.Registry.ActiveCalls);
        Assert.Equal(0, observed.Placement.Calls);
        Assert.Equal(2, observed.Paths.WriteCalls);
        Assert.Equal(2, observed.Overview.Payloads.Count);
        Assert.Equal(0, observed.Queue.Count);
    }

    private static async Task<Observed> RunAsync(params InboundArticle[] articles)
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        var placement = new CountingPlacement();
        var registry = new CountingRegistry(EligibleRegistry());
        var paths = new CountingPathSurveyWriter();
        var overview = new RecordingOverviewDbHandoffPublisher();
        var writer = new IncomingSpoolWriterService(
            queue,
            Options.Create(new NntpdOptions
            {
                ArticleIngestion = new ArticleIngestionOptions
                {
                    MinWorkers = 1,
                    MaxWorkers = 1,
                    OverviewDbMinPublisherWorkers = 1,
                    OverviewDbMaxPublisherWorkers = 1,
                    ScaleIntervalSeconds = 3600,
                },
            }),
            NullLogger<IncomingSpoolWriterService>.Instance,
            overviewHandoff: overview,
            pathSurvey: paths,
            placement: placement,
            placementRegistry: registry);

        await writer.StartAsync(CancellationToken.None);
        foreach (var article in articles)
        {
            Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(article, CancellationToken.None));
        }

        queue.Complete();
        // Confirms are recorded after PublishAsync returns, while the worker is still
        // running. Waiting here, before StopAsync, observes that signal. StopAsync's
        // own budget must not be the observation window: once it expires, StopAsync
        // abandons outstanding OverviewDB work and the confirm never arrives.
        using var confirmed = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await overview.WaitUntilConfirmedAsync(articles.Length, confirmed.Token);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await writer.StopAsync(stop.Token);
        return new Observed(queue, placement, registry, paths, overview);
    }

    private static StorageServerRegistry EligibleRegistry()
    {
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(
            new StorageServerAdvertisement(1, 1, "cache01.example", 10_000, 0, 10_000, Now, 563),
            Now);
        return registry;
    }

    private sealed record Observed(
        ArticleIngestionQueue Queue,
        CountingPlacement Placement,
        CountingRegistry Registry,
        CountingPathSurveyWriter Paths,
        RecordingOverviewDbHandoffPublisher Overview);

    private sealed class CountingPlacement : IArticlePlacementClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<ArticlePlacementResult> PlaceAsync(
            ArticleRecord record,
            StorageServerFleetEntry target,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(new ArticlePlacementResult(ArticlePlacementKind.Accepted));
        }
    }

    private sealed class CountingRegistry(StorageServerRegistry inner) : IStorageServerRegistry
    {
        private int _activeCalls;

        public int ActiveCalls => Volatile.Read(ref _activeCalls);

        public void ApplyAdvertisement(StorageServerAdvertisement advertisement, DateTimeOffset receivedAtUtc) =>
            inner.ApplyAdvertisement(advertisement, receivedAtUtc);

        public bool TryGet(string fqdn, out StorageServerFleetEntry entry) => inner.TryGet(fqdn, out entry);

        public IReadOnlyList<StorageServerFleetEntry> Snapshot() => inner.Snapshot();

        public IReadOnlyList<StorageServerFleetEntry> GetActive(DateTimeOffset utcNow)
        {
            Interlocked.Increment(ref _activeCalls);
            return inner.GetActive(utcNow);
        }

        public void ApplyLifecycle(StorageServerLifecycleAnnouncement announcement, DateTimeOffset receivedAtUtc) =>
            inner.ApplyLifecycle(announcement, receivedAtUtc);
    }
}
