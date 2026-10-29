using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.Hosting;
using VectorNNTPD.TransitService.Logging;
using VectorNNTPD.TransitService.RabbitMq;
using VectorNNTPD.TransitService.Storage;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;

namespace VectorNNTPD.TransitService.Tests.RabbitMq;

[Collection(SerilogCollection.Name)]
public sealed class RabbitMqTopologyServiceTests
{
    [Fact]
    public async Task StartAsync_DeclaresCacheFanoutExchanges_AndOverviewQueue_WithoutSharedRequestsQueue()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await topology.StartAsync(CancellationToken.None);

        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        var channel = Assert.Single(connection.TopologyChannels);
        Assert.Equal(1, channel.DisposeCount);
        AssertDeclaredTopology(connection, expectedPasses: 1);

        await topology.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeclareRequiredTopologyAsync_IsIdempotent_AndDoesNotDeleteOrPurge()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await topology.StartAsync(CancellationToken.None);
        await topology.DeclareRequiredTopologyAsync(CancellationToken.None);

        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        Assert.Equal(2, connection.TopologyChannels.Count);
        Assert.All(connection.TopologyChannels, static channel => Assert.Equal(1, channel.DisposeCount));
        AssertDeclaredTopology(connection, expectedPasses: 2);
        var channelMethods = typeof(IRabbitMqTopologyChannel).GetMethods().Select(static m => m.Name).ToHashSet();
        Assert.Contains(nameof(IRabbitMqTopologyChannel.ExchangeDeclareAsync), channelMethods);
        Assert.Contains(nameof(IRabbitMqTopologyChannel.QueueDeclareAsync), channelMethods);
        Assert.Contains(nameof(IRabbitMqTopologyChannel.QueueBindAsync), channelMethods);
        Assert.DoesNotContain("QueueDeleteAsync", channelMethods);
        Assert.DoesNotContain("QueuePurgeAsync", channelMethods);
        Assert.DoesNotContain("ExchangeDeleteAsync", channelMethods);
        Assert.DoesNotContain("BasicPublishAsync", channelMethods);
        Assert.DoesNotContain("BasicConsumeAsync", channelMethods);
    }

    [Fact]
    public async Task StartAsync_SecondCall_DoesNotRedeclare()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await topology.StartAsync(CancellationToken.None);
        await topology.StartAsync(CancellationToken.None);

        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        Assert.Single(connection.TopologyChannels);
    }

    [Fact]
    public async Task StartAsync_Fails_WhenRabbitMqHasNotStarted()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => topology.StartAsync(CancellationToken.None));

        Assert.Contains("not ready", ex.Message, StringComparison.Ordinal);
        Assert.Null(factory.LastConnection);
    }

    [Fact]
    public async Task StartAsync_Fails_WhenQueueDeclareIsRejected()
    {
        var factory = new FakeRabbitMqConnectionFactory
        {
            QueueDeclareException = new InvalidOperationException("PRECONDITION_FAILED - inequivalent arg 'x-queue-type' for queue 'overviewdb.queue'"),
        };
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => topology.StartAsync(CancellationToken.None));

        Assert.Contains("x-queue-type", ex.Message, StringComparison.Ordinal);
        Assert.Single(factory.LastConnection!.TopologyChannels);
        Assert.Equal(1, factory.LastConnection.TopologyChannels[0].DisposeCount);
    }

    [Fact]
    public async Task ApplicationServiceManager_TopologyFailure_PreventsStartup_AndRollsBackRabbitMq()
    {
        var factory = new FakeRabbitMqConnectionFactory
        {
            QueueDeclareException = new InvalidOperationException("classic queue cannot become quorum"),
        };
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);
        var manager = TestHostFactory.CreateServiceManager([rabbit, topology]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StartAsync(CancellationToken.None));

        Assert.Contains("quorum", ex.Message, StringComparison.Ordinal);
        Assert.Empty(manager.StartedServices);
        Assert.False(rabbit.IsReady);
        Assert.False(rabbit.TryGetCurrent(out _));
    }

    [Fact]
    public async Task Host_Start_DeclaresArticleRetrievalTopology_BeforeRunning()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpTransitHosting(includePlaceholderService: false);

        using var host = builder.Build();
        await host.StartAsync();

        Assert.Equal(ApplicationState.Running, host.Services.GetRequiredService<ApplicationLifecycle>().State);
        var factory = Assert.IsType<FakeRabbitMqConnectionFactory>(
            host.Services.GetRequiredService<IRabbitMqConnectionFactory>());
        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        AssertDeclaredTopology(connection, expectedPasses: 1);

        await host.StopAsync();
    }

    [Fact]
    public void Host_RegistersTopologyServiceOnce_ImmediatelyAfterRabbitMqConnection()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpTransitHosting(includePlaceholderService: false);

        using var host = builder.Build();
        var services = host.Services.GetServices<IApplicationService>().ToArray();

        // NntpDB and nntpsharedconfig are registered ahead of Cloudflare, Redis, and RabbitMQ.
        Assert.Equal(typeof(RabbitMqService), services[4].GetType());
        Assert.Equal(typeof(RabbitMqTopologyService), services[5].GetType());
        Assert.Equal(typeof(StorageServerFleetConsumerService), services[6].GetType());
        Assert.Equal(typeof(StorageArticleLookupService), services[7].GetType());
        Assert.Equal(typeof(VectorNNTPD.TransitService.RabbitMq.ArticleWork.ArticleWorkRpcService), services[8].GetType());
        Assert.Equal(1, services.Count(static s => s is RabbitMqTopologyService));
        Assert.Same(
            host.Services.GetRequiredService<RabbitMqTopologyService>(),
            services[5]);
    }

    private static RabbitMqService CreateRabbitMqService(FakeRabbitMqConnectionFactory factory)
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = 50;
        options.PoolReconnectMaxDelayMs = 50;
        return new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.TransitService:nntpd01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance);
    }

    private static void AssertDeclaredTopology(FakeRabbitMqConnection connection, int expectedPasses)
    {
        Assert.Empty(ArticleRetrievalTopology.Required);
        // Per pass: cache.requests + cache.broadcast exchanges; overviewdb.queue only.
        Assert.Equal(2 * expectedPasses, connection.ExchangeDeclarations.Count);
        Assert.Equal(1 * expectedPasses, connection.QueueDeclarations.Count);
        Assert.Empty(connection.BindingDeclarations);

        for (var pass = 0; pass < expectedPasses; pass++)
        {
            var exchangeOffset = pass * 2;
            var requests = connection.ExchangeDeclarations[exchangeOffset];
            Assert.Equal(CacheRequestsTopology.ExchangeName, requests.Name);
            Assert.Equal("fanout", requests.Type);
            Assert.True(requests.Durable);
            Assert.False(requests.AutoDelete);

            var broadcast = connection.ExchangeDeclarations[exchangeOffset + 1];
            Assert.Equal(CacheBroadcastTopology.ExchangeName, broadcast.Name);
            Assert.Equal("fanout", broadcast.Type);
            Assert.True(broadcast.Durable);
            Assert.False(broadcast.AutoDelete);

            var overview = connection.QueueDeclarations[pass];
            Assert.Equal(OverviewDbTopology.QueueName, overview.Name);
            Assert.True(overview.Durable);
            Assert.False(overview.Exclusive);
            Assert.False(overview.AutoDelete);
            AssertQuorumQueueArguments(overview.Arguments);
        }

        Assert.DoesNotContain(
            connection.QueueDeclarations,
            static queue => queue.Name == CacheFleetTopology.RequestsExchangeName);
        Assert.DoesNotContain(
            connection.QueueDeclarations,
            static queue =>
                queue.Name == CacheFleetTopology.RequestsExchangeName
                && queue.Durable
                && queue.Arguments is not null
                && queue.Arguments.TryGetValue("x-queue-type", out var type)
                && Equals(type, "quorum"));
        Assert.DoesNotContain(
            connection.ExchangeDeclarations,
            static exchange => exchange.Name is "backfiller.giganews" or "backfiller.eweka" or "backfiller.abavia");
    }

    private static void AssertQuorumQueueArguments(
        IReadOnlyDictionary<string, object?>? arguments)
    {
        Assert.NotNull(arguments);
        Assert.True(arguments.TryGetValue(
            RabbitMqArticleRetrievalEndpoints.QueueTypeArgumentName,
            out var queueType));
        Assert.Equal(RabbitMqArticleRetrievalEndpoints.QuorumQueueType, queueType);
        Assert.False(arguments.ContainsKey("x-message-ttl"));
        Assert.False(arguments.ContainsKey("x-expires"));
        Assert.Single(arguments);
    }
}
