using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Storage;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.RabbitMq;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Storage;

public sealed class StorageArticleLookupServiceTests
{
    private static readonly ArticleId ArticleX = ArticleId.FromMessageId("<article-x@example.com>"u8);

    [Fact]
    public async Task MultipleOwners_FirstPositiveCompletes_AndDuplicateIsIgnored()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var time = new FakeTimeProvider();
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            time);

        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var lookupTask = service.LookupAsync(ArticleX, CancellationToken.None);
        var publication = await WaitForPublicationAsync(factory);
        Assert.Equal(CacheFleetTopology.RequestsExchangeName, publication.Exchange);
        Assert.Equal(string.Empty, publication.RoutingKey);
        Assert.Equal(CacheFleetTopology.LookupExpirationMilliseconds, publication.Expiration);
        Assert.NotNull(service.CurrentReplyTo);
        Assert.Equal(service.CurrentReplyTo, publication.ReplyTo);

        Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(publication.Body, out var request, out _));
        Assert.NotNull(request);

        var first = CreateResponse(request!, 2, "cache02.usenet.ninja");
        var second = CreateResponse(request!, 3, "cache03.usenet.ninja");
        var consume = factory.LastConnection!.RpcChannels.First(static c => c.ConsumedQueue is not null);
        await consume.DeliverAsync(publication.CorrelationId, StorageArticleLookupWireProtocol.SerializeResponseV1(first));
        await consume.DeliverAsync(publication.CorrelationId, StorageArticleLookupWireProtocol.SerializeResponseV1(second));
        await consume.DeliverAsync(publication.CorrelationId, StorageArticleLookupWireProtocol.SerializeResponseV1(first));

        var result = await lookupTask;
        Assert.Equal(StorageArticleLookupOutcome.Found, result.Outcome);
        Assert.Equal("cache02.usenet.ninja", result.Fqdn);
        Assert.Equal(2, result.ServerId);
        Assert.Equal(1, service.OutstandingCorrelations);
        Assert.NotNull(result.Alternates);
        Assert.False(service.TryDispatchResponse(
            publication.CorrelationId,
            CreateResponse(request!, 4, "cache04.usenet.ninja")));

        var alternate = await result.Alternates!.WaitForAlternateAsync(CancellationToken.None);
        Assert.True(alternate.HasValue);
        var alternateCopy = alternate.GetValueOrDefault();
        Assert.Equal(3, alternateCopy.ServerId);
        Assert.Equal("cache03.usenet.ninja", alternateCopy.Fqdn);

        time.Advance(CacheFleetTopology.LookupTimeout);
        await result.Alternates.WhenClosed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, service.OutstandingCorrelations);
        Assert.False(service.TryDispatchResponse(
            publication.CorrelationId,
            CreateResponse(request!, 4, "cache04.usenet.ninja")));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TwoPresentCopies_AreRetainedByExistingFanout()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var time = new FakeTimeProvider();
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            time);

        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var lookupTask = service.LookupAsync(ArticleX, CancellationToken.None);
        var publication = await WaitForPublicationAsync(factory);
        Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(publication.Body, out var request, out _));
        Assert.NotNull(request);

        var consume = factory.LastConnection!.RpcChannels.First(static c => c.ConsumedQueue is not null);
        await consume.DeliverAsync(
            publication.CorrelationId,
            StorageArticleLookupWireProtocol.SerializeResponseV1(CreateResponse(request!, 2, "cache02.usenet.ninja")));
        await consume.DeliverAsync(
            publication.CorrelationId,
            StorageArticleLookupWireProtocol.SerializeResponseV1(CreateResponse(request!, 3, "cache03.usenet.ninja")));

        var result = await lookupTask;
        Assert.Equal(StorageArticleLookupOutcome.Found, result.Outcome);
        Assert.Equal(2, result.ServerId);
        Assert.Equal("cache02.usenet.ninja", result.Fqdn);
        Assert.NotNull(result.Alternates);
        var alternate = await result.Alternates!.WaitForAlternateAsync(CancellationToken.None);
        Assert.True(alternate.HasValue);
        var alternateCopy = alternate.GetValueOrDefault();
        Assert.Equal(3, alternateCopy.ServerId);
        Assert.Equal("cache03.usenet.ninja", alternateCopy.Fqdn);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NobodyOwns_TimesOutCleanly()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var time = new FakeTimeProvider();
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            time);

        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var lookupTask = service.LookupAsync(ArticleX, CancellationToken.None);
        _ = await WaitForPublicationAsync(factory);
        for (var attempt = 0; attempt < 3 && !lookupTask.IsCompleted; attempt++)
        {
            time.Advance(CacheFleetTopology.LookupTimeout);
            await Task.Yield();
        }

        var result = await lookupTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StorageArticleLookupOutcome.NotFound, result.Outcome);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LateResponse_AfterDeadline_IsIgnored()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var time = new FakeTimeProvider();
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            time);

        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var lookupTask = service.LookupAsync(ArticleX, CancellationToken.None);
        var publication = await WaitForPublicationAsync(factory);
        Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(publication.Body, out var request, out _));
        var consume = factory.LastConnection!.RpcChannels.First(static c => c.ConsumedQueue is not null);
        await consume.DeliverAsync(
            publication.CorrelationId,
            StorageArticleLookupWireProtocol.SerializeResponseV1(CreateResponse(request!, 1, "cache01.usenet.ninja")));
        var result = await lookupTask;
        Assert.Equal(StorageArticleLookupOutcome.Found, result.Outcome);
        Assert.NotNull(result.Alternates);

        time.Advance(CacheFleetTopology.LookupTimeout);
        await result.Alternates!.WhenClosed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, service.OutstandingCorrelations);
        Assert.False(service.TryDispatchResponse(
            publication.CorrelationId,
            CreateResponse(request!, 2, "cache02.usenet.ninja")));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AlternateWait_CompletesWhenOriginalWindowClosesWithoutSecondCandidate()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var time = new FakeTimeProvider();
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            time);

        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var lookupTask = service.LookupAsync(ArticleX, CancellationToken.None);
        var publication = await WaitForPublicationAsync(factory);
        Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(publication.Body, out var request, out _));
        var consume = factory.LastConnection!.RpcChannels.First(static c => c.ConsumedQueue is not null);
        await consume.DeliverAsync(
            publication.CorrelationId,
            StorageArticleLookupWireProtocol.SerializeResponseV1(CreateResponse(request!, 1, "cache01.usenet.ninja")));
        var result = await lookupTask;
        Assert.NotNull(result.Alternates);

        var alternateTask = result.Alternates!.WaitForAlternateAsync(CancellationToken.None).AsTask();
        Assert.False(alternateTask.IsCompleted);
        time.Advance(CacheFleetTopology.LookupTimeout);
        var alternate = await alternateTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(alternate);
        Assert.Equal(0, service.OutstandingCorrelations);
        Assert.False(service.TryDispatchResponse(
            publication.CorrelationId,
            CreateResponse(request!, 2, "cache02.usenet.ninja")));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentLookups_CorrelateIndependently()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance);
        var articleA = ArticleId.FromMessageId("<a@example.com>"u8);
        var articleB = ArticleId.FromMessageId("<b@example.com>"u8);

        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var lookupA = service.LookupAsync(articleA, CancellationToken.None);
        var lookupB = service.LookupAsync(articleB, CancellationToken.None);
        var publications = await WaitForPublicationCountAsync(factory, 2);
        var requestA = publications.Single(p =>
            StorageArticleLookupWireProtocol.TryParseRequestV1(p.Body, out var r, out _)
            && r!.ArticleId == articleA);
        var requestB = publications.Single(p =>
            StorageArticleLookupWireProtocol.TryParseRequestV1(p.Body, out var r, out _)
            && r!.ArticleId == articleB);
        Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(requestA.Body, out var parsedA, out _));
        Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(requestB.Body, out var parsedB, out _));
        var consume = factory.LastConnection!.RpcChannels.First(static c => c.ConsumedQueue is not null);

        await consume.DeliverAsync(
            requestB.CorrelationId,
            StorageArticleLookupWireProtocol.SerializeResponseV1(
                CreateResponse(parsedB!, 3, "cache03.usenet.ninja")));
        await consume.DeliverAsync(
            requestA.CorrelationId,
            StorageArticleLookupWireProtocol.SerializeResponseV1(
                CreateResponse(parsedA!, 1, "cache01.usenet.ninja")));

        var resultA = await lookupA;
        var resultB = await lookupB;
        Assert.Equal("cache01.usenet.ninja", resultA.Fqdn);
        Assert.Equal("cache03.usenet.ninja", resultB.Fqdn);
        Assert.NotEqual(resultA.RequestId, resultB.RequestId);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DuplicateServerId_AndSameEndpoint_AreNotSecondCandidates()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var time = new FakeTimeProvider();
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            time);
        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var lookupTask = service.LookupAsync(ArticleX, CancellationToken.None);
        var publication = await WaitForPublicationAsync(factory);
        Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(publication.Body, out var request, out _));
        var first = CreateResponse(request!, 2, "cache02.usenet.ninja");
        var consume = factory.LastConnection!.RpcChannels.First(static c => c.ConsumedQueue is not null);
        await consume.DeliverAsync(publication.CorrelationId, StorageArticleLookupWireProtocol.SerializeResponseV1(first));
        var result = await lookupTask;

        var duplicateServer = new StorageArticleLookupResponse(
            1,
            request!.RequestId,
            2,
            "cache02.usenet.ninja",
            request.ArticleId,
            1192);
        var sameEndpoint = new StorageArticleLookupResponse(
            1,
            request.RequestId,
            9,
            first.Fqdn,
            request.ArticleId,
            first.VatpPort);
        Assert.False(service.TryDispatchResponse(publication.CorrelationId, duplicateServer));
        Assert.False(service.TryDispatchResponse(publication.CorrelationId, sameEndpoint));

        time.Advance(CacheFleetTopology.LookupTimeout);
        await result.Alternates!.WhenClosed.WaitAsync(TimeSpan.FromSeconds(2));
        var alternate = await result.Alternates.WaitForAlternateAsync(CancellationToken.None);
        Assert.Null(alternate);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InvalidCandidateResponses_AreIgnoredUntilAValidOneArrives()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            new FakeTimeProvider());
        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var lookupTask = service.LookupAsync(ArticleX, CancellationToken.None);
        var publication = await WaitForPublicationAsync(factory);
        Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(publication.Body, out var request, out _));
        var otherArticle = ArticleId.FromMessageId("<other@example.com>"u8);
        Assert.False(service.TryDispatchResponse(
            publication.CorrelationId,
            CreateResponse(request!, 0, "cache00.usenet.ninja")));
        Assert.False(service.TryDispatchResponse(
            publication.CorrelationId,
            CreateResponse(request!, 256, "cache256.usenet.ninja")));
        Assert.False(service.TryDispatchResponse(
            publication.CorrelationId,
            new StorageArticleLookupResponse(
                1,
                Guid.NewGuid(),
                4,
                "cache04.usenet.ninja",
                request!.ArticleId,
                1191)));
        Assert.False(service.TryDispatchResponse(
            publication.CorrelationId,
            new StorageArticleLookupResponse(
                1,
                request.RequestId,
                5,
                "cache05.usenet.ninja",
                otherArticle,
                1191)));
        Assert.False(service.TryDispatchResponse(
            publication.CorrelationId,
            new StorageArticleLookupResponse(
                1,
                request.RequestId,
                6,
                "cache06.usenet.ninja",
                request.ArticleId,
                0)));
        Assert.False(lookupTask.IsCompleted);

        Assert.True(service.TryDispatchResponse(
            publication.CorrelationId,
            CreateResponse(request, 7, "cache07.usenet.ninja")));
        var result = await lookupTask;
        Assert.Equal(7, result.ServerId);
        Assert.Equal(request.RequestId, result.RequestId);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EmptyRegistry_DoesNotPublish_AndReturnsExistingMiss()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            storageServers: new StorageServerRegistry());

        var result = await service.LookupAsync(ArticleX, CancellationToken.None);

        Assert.Equal(StorageArticleLookupOutcome.NotFound, result.Outcome);
        Assert.Equal("Storage article lookup timed out with no positive response.", result.Error);
        Assert.Null(factory.LastConnection);
        Assert.Null(service.CurrentReplyTo);
    }

    [Fact]
    public async Task RegisteredStorageServer_StillPublishesFleetLookup()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var time = new FakeTimeProvider();
        var registry = new StorageServerRegistry();
        var now = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
        registry.ApplyAdvertisement(
            new StorageServerAdvertisement(1, 1, "cache01.usenet.ninja", 1000, 100, 900, now, 1191),
            now);
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            time,
            registry);

        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var lookupTask = service.LookupAsync(ArticleX, CancellationToken.None);
        var publication = await WaitForPublicationAsync(factory);
        Assert.Equal(CacheFleetTopology.RequestsExchangeName, publication.Exchange);
        Assert.Equal(string.Empty, publication.RoutingKey);

        for (var attempt = 0; attempt < 3 && !lookupTask.IsCompleted; attempt++)
        {
            time.Advance(CacheFleetTopology.LookupTimeout);
            await Task.Yield();
        }

        var result = await lookupTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StorageArticleLookupOutcome.NotFound, result.Outcome);
        Assert.Equal("Storage article lookup timed out with no positive response.", result.Error);
        await service.StopAsync(CancellationToken.None);
    }

    private static StorageArticleLookupResponse CreateResponse(
        StorageArticleLookupRequest request,
        int serverId,
        string fqdn) =>
        new(
            1,
            request.RequestId,
            serverId,
            fqdn,
            request.ArticleId,
            1191);

    private static RabbitMqService CreateRabbitMq(FakeRabbitMqConnectionFactory factory)
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = 50;
        options.PoolReconnectMaxDelayMs = 50;
        return new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.ReaderService:nntpd01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance);
    }

    private static NntpdOptions CreateOptions()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = 1;
        return options;
    }

    private static async Task<FakeRabbitMqRpcPublication> WaitForPublicationAsync(FakeRabbitMqConnectionFactory factory)
    {
        var publications = await WaitForPublicationCountAsync(factory, 1);
        return publications[0];
    }

    private static async Task<List<FakeRabbitMqRpcPublication>> WaitForPublicationCountAsync(
        FakeRabbitMqConnectionFactory factory,
        int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var connection = factory.LastConnection;
            if (connection is not null)
            {
                var publications = connection.RpcChannels
                    .SelectMany(static channel => channel.Publications)
                    .Where(static p => p.Exchange == CacheFleetTopology.RequestsExchangeName)
                    .ToList();
                if (publications.Count >= count)
                {
                    return publications;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Expected {count} cache.requests publications.");
    }
}
