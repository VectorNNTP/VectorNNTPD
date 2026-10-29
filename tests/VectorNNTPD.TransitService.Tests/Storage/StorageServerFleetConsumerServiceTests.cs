using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.RabbitMq;
using VectorNNTPD.TransitService.Storage;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.RabbitMq;
using VectorNNTPD.TransitService.Tests.TestDoubles;

namespace VectorNNTPD.TransitService.Tests.Storage;

public sealed class StorageServerFleetConsumerServiceTests
{
    [Fact]
    public async Task StartAsync_DeclaresEphemeralQueue_BoundToBroadcast()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var registry = new StorageServerRegistry();
        var options = Options.Create(CreateNntpdOptions(1));
        var consumer = new StorageServerFleetConsumerService(
            rabbit,
            options,
            registry,
            NullLogger<StorageServerFleetConsumerService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await consumer.StartAsync(CancellationToken.None);

        var connection = factory.LastConnection!;
        var channel = Assert.Single(connection.ManualAckChannels);
        Assert.Equal("cache.nntpd01.usenet.ninja", consumer.CurrentQueueName);
        Assert.Equal("cache.nntpd01.usenet.ninja", channel.ConsumedQueue);
        Assert.Contains(
            channel.Exchanges,
            static e => e.Name == CacheBroadcastTopology.ExchangeName && e.Type == "fanout" && e.Durable);
        var queue = Assert.Single(channel.Queues);
        Assert.Equal("cache.nntpd01.usenet.ninja", queue.Name);
        Assert.False(queue.Durable);
        Assert.True(queue.Exclusive);
        Assert.True(queue.AutoDelete);
        var binding = Assert.Single(channel.Bindings);
        Assert.Equal(queue.Name, binding.Queue);
        Assert.Equal(CacheBroadcastTopology.ExchangeName, binding.Exchange);

        await consumer.StopAsync(CancellationToken.None);
        Assert.Equal(1, channel.DisposeCount);
        Assert.Null(consumer.CurrentQueueName);
    }

    [Fact]
    public async Task IndependentNntpdInstances_UseDistinctQueues()
    {
        var factoryA = new FakeRabbitMqConnectionFactory();
        var factoryB = new FakeRabbitMqConnectionFactory();
        await using var rabbitA = CreateRabbitMq(factoryA);
        await using var rabbitB = CreateRabbitMq(factoryB);
        var consumerA = new StorageServerFleetConsumerService(
            rabbitA,
            Options.Create(CreateNntpdOptions(1)),
            new StorageServerRegistry(),
            NullLogger<StorageServerFleetConsumerService>.Instance);
        var consumerB = new StorageServerFleetConsumerService(
            rabbitB,
            Options.Create(CreateNntpdOptions(2)),
            new StorageServerRegistry(),
            NullLogger<StorageServerFleetConsumerService>.Instance);

        await rabbitA.StartAsync(CancellationToken.None);
        await rabbitB.StartAsync(CancellationToken.None);
        await consumerA.StartAsync(CancellationToken.None);
        await consumerB.StartAsync(CancellationToken.None);

        Assert.Equal("cache.nntpd01.usenet.ninja", consumerA.CurrentQueueName);
        Assert.Equal("cache.nntpd02.usenet.ninja", consumerB.CurrentQueueName);
        Assert.NotEqual(consumerA.CurrentQueueName, consumerB.CurrentQueueName);

        await consumerA.StopAsync(CancellationToken.None);
        await consumerB.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Delivery_UpdatesRegistry_AndAcks()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var registry = new StorageServerRegistry();
        var consumer = new StorageServerFleetConsumerService(
            rabbit,
            Options.Create(CreateNntpdOptions(1)),
            registry,
            NullLogger<StorageServerFleetConsumerService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await consumer.StartAsync(CancellationToken.None);

        var channel = Assert.Single(factory.LastConnection!.ManualAckChannels);
        var body = StorageServerAdvertisementWireProtocol.SerializeV1(
            new StorageServerAdvertisement(
                1,
                7,
                "cache07.usenet.ninja",
                1_000_000,
                250_000,
                750_000,
                DateTimeOffset.Parse("2026-09-29T12:00:00Z")));
        await channel.DeliverAsync(42, body);

        Assert.Contains(42UL, channel.Acks);
        Assert.True(registry.TryGet("cache07.usenet.ninja", out var entry));
        Assert.Equal(7, entry.ServerId);
        Assert.Equal(1_000_000, entry.TotalBytes);
        Assert.Equal(750_000, entry.AvailableBytes);

        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LifecycleDelivery_UpdatesSelection()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var registry = new StorageServerRegistry();
        var consumer = new StorageServerFleetConsumerService(
            rabbit,
            Options.Create(CreateNntpdOptions(1)),
            registry,
            NullLogger<StorageServerFleetConsumerService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await consumer.StartAsync(CancellationToken.None);
        var channel = Assert.Single(factory.LastConnection!.ManualAckChannels);
        var advertisement = StorageServerAdvertisementWireProtocol.SerializeV1(
            new StorageServerAdvertisement(
                1,
                7,
                "cache07.usenet.ninja",
                1_000,
                100,
                900,
                DateTimeOffset.Parse("2026-09-29T12:00:00Z"),
                1191));
        await channel.DeliverAsync(1, advertisement);
        var selected = Assert.Single(registry.GetActive(DateTimeOffset.Parse("2026-09-29T12:00:01Z")));
        Assert.Equal(900, selected.AvailableBytes);
        Assert.Equal(1191, selected.VatpPort);
        Assert.False(selected.IsDraining);

        var draining = StorageServerLifecycleWireProtocol.SerializeV1(
            new StorageServerLifecycleAnnouncement(
                1,
                7,
                "cache07.usenet.ninja",
                StorageServerLifecycleState.Draining,
                DateTimeOffset.Parse("2026-09-29T12:00:02Z"),
                1191));
        await channel.DeliverAsync(2, draining);
        Assert.Empty(registry.GetActive(DateTimeOffset.Parse("2026-09-29T12:00:02Z")));
        Assert.True(registry.TryGet("cache07.usenet.ninja", out var drained));
        Assert.True(drained.IsDraining);
        Assert.Equal(900, drained.AvailableBytes);
        Assert.Equal(1191, drained.VatpPort);

        var ready = """{"version":1,"serverId":7,"fqdn":"cache07.usenet.ninja","state":"Ready","timestamp":"2026-09-29T12:00:04.0000000Z","vatpPort":1191}"""u8.ToArray();
        await channel.DeliverAsync(3, ready);
        Assert.Contains(3UL, channel.Acks);
        Assert.Empty(registry.GetActive(DateTimeOffset.Parse("2026-09-29T12:00:04Z")));
        Assert.True(registry.TryGet("cache07.usenet.ninja", out drained));
        Assert.True(drained.IsDraining);
        Assert.Equal(900, drained.AvailableBytes);

        var invalid = """{"version":1,"serverId":7,"fqdn":"cache07.usenet.ninja","state":"Offline","timestamp":"2026-09-29T12:00:03.0000000Z","vatpPort":1191}"""u8.ToArray();
        await channel.DeliverAsync(4, invalid);
        Assert.Contains(4UL, channel.Acks);
        Assert.Empty(registry.GetActive(DateTimeOffset.Parse("2026-09-29T12:00:03Z")));

        await consumer.StopAsync(CancellationToken.None);
    }

    private static RabbitMqService CreateRabbitMq(FakeRabbitMqConnectionFactory factory)
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

    private static NntpdOptions CreateNntpdOptions(int serverId)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = serverId;
        return options;
    }
}
