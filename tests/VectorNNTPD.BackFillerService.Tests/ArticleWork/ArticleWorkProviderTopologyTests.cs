using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.RabbitMq;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;


namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Provider RabbitMQ topology: quorum fanout exchange/queue per backbone, declare ordering, capacity-driven consumer lifecycle.
    /// </summary>
    public sealed class ArticleWorkProviderTopologyTests
    {
        /// <summary>
        /// Usable capacity starts consumers only after a dedicated topology channel declares exchange, durable quorum queue, and bind, then disposes.
        /// </summary>
        [Fact]
        public async Task Active_backbone_declares_quorum_fanout_topology_before_consumers()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Giganews", 1)]),
                CreateCapacity([("Giganews", 1)]));

            await consumer.StartAsync(CancellationToken.None);

            Assert.Single(consumer.Sessions);
            Assert.Equal(2, factory.LastConnection!.Channels.Count);
            var topology = factory.LastConnection.Channels[0];
            Assert.Equal(0, topology.ConsumeCount);
            Assert.Equal(("backfiller.giganews", "fanout", true, false), Assert.Single(topology.ExchangeDeclarations));
            var queue = Assert.Single(topology.QueueDeclarations);
            Assert.Equal("backfiller.giganews", queue.Name);
            Assert.True(queue.Durable);
            Assert.False(queue.Exclusive);
            Assert.False(queue.AutoDelete);
            Assert.NotNull(queue.Arguments);
            Assert.True(queue.Arguments.TryGetValue(BackFillerArticleWorkTopology.QueueTypeArgumentName, out var queueType));
            Assert.Equal(BackFillerArticleWorkTopology.QuorumQueueType, queueType);
            Assert.False(queue.Arguments.ContainsKey("x-message-ttl"));
            Assert.Single(queue.Arguments);
            Assert.Equal(
                ("backfiller.giganews", "backfiller.giganews", "backfiller.giganews"),
                Assert.Single(topology.BindingDeclarations));
            Assert.Equal(1, topology.DisposeCount);
            Assert.Equal("backfiller.giganews", factory.LastConnection.Channels[1].LastQueue);
            Assert.Equal(1, factory.LastConnection.Channels[1].ConsumeCount);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Queue declare precondition failure leaves zero running sessions and no BasicConsume on any channel.
        /// </summary>
        [Fact]
        public async Task Topology_declare_failure_does_not_start_consumers()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            factory.LastConnection!.NextChannelQueueDeclareException = new InvalidOperationException(
                "PRECONDITION_FAILED - inequivalent arg 'x-queue-type'");
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Giganews", 1)]),
                CreateCapacity([("Giganews", 1)]));

            await consumer.StartAsync(CancellationToken.None);

            Assert.Empty(consumer.Sessions);
            Assert.DoesNotContain(factory.LastConnection.Channels, static channel => channel.ConsumeCount > 0);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Catalog entries without positive usable capacity do not open channels or sessions.
        /// </summary>
        [Fact]
        public async Task Inactive_backbone_does_not_declare_or_consume()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Giganews", 2), ("Eweka", 2)]),
                CreateCapacity([]));

            await consumer.StartAsync(CancellationToken.None);

            Assert.Empty(consumer.Sessions);
            Assert.Empty(factory.LastConnection!.Channels);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Capacity drop to zero retires consume sessions but does not delete exchanges/queues via manual-ack channel APIs.
        /// </summary>
        [Fact]
        public async Task Losing_capacity_retires_consumers_without_deleting_topology()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var capacity = CreateCapacity([("Giganews", 1)]);
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Giganews", 1)]),
                capacity);

            await consumer.StartAsync(CancellationToken.None);
            Assert.Single(consumer.Sessions);
            var topology = factory.LastConnection!.Channels[0];
            Assert.Single(topology.ExchangeDeclarations);
            Assert.Single(topology.QueueDeclarations);
            Assert.Single(topology.BindingDeclarations);

            capacity.PublishSnapshot(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.Sessions.Count == 0,
                TimeSpan.FromSeconds(2));

            Assert.Empty(consumer.Sessions);
            Assert.Single(topology.ExchangeDeclarations);
            Assert.Single(topology.QueueDeclarations);
            Assert.Single(topology.BindingDeclarations);
            Assert.DoesNotContain(
                typeof(IRabbitMqManualAckChannel).GetMethods().Select(static m => m.Name),
                static name => name.Contains("Delete", StringComparison.Ordinal));

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Capacity restored after zero re-runs declare on a fresh channel and starts consumers; prior declaration counts increase idempotently.
        /// </summary>
        [Fact]
        public async Task Reactivating_backbone_redeclares_idempotently_and_starts_consumers()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var capacity = CreateCapacity([("Giganews", 1)]);
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Giganews", 1)]),
                capacity);

            await consumer.StartAsync(CancellationToken.None);
            Assert.Single(consumer.Sessions);

            capacity.PublishSnapshot(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.Sessions.Count == 0,
                TimeSpan.FromSeconds(2));

            var declarationsBefore = factory.LastConnection!.Channels
                .Sum(static channel => channel.ExchangeDeclarations.Count);

            capacity.PublishSnapshot(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Giganews"] = 1,
            });
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => consumer.Sessions.Count == 1,
                TimeSpan.FromSeconds(2));

            Assert.Single(consumer.Sessions);
            var declarationsAfter = factory.LastConnection.Channels
                .Sum(static channel => channel.ExchangeDeclarations.Count);
            Assert.True(declarationsAfter > declarationsBefore);
            Assert.Contains(
                factory.LastConnection.Channels,
                static channel => channel.ExchangeDeclarations.Any(static e => e.Name == "backfiller.giganews"));
            Assert.All(
                factory.LastConnection.Channels.SelectMany(static c => c.QueueDeclarations),
                static queue =>
                {
                    Assert.NotNull(queue.Arguments);
                    Assert.True(queue.Arguments.TryGetValue(
                        BackFillerArticleWorkTopology.QueueTypeArgumentName,
                        out var queueType));
                    Assert.Equal(BackFillerArticleWorkTopology.QuorumQueueType, queueType);
                });

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Only backbones with positive usable capacity get provider endpoint topology; inactive catalog entries are skipped.
        /// </summary>
        [Fact]
        public async Task Only_usable_backbones_are_declared()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Giganews", 1), ("Eweka", 1), ("Abavia", 1)]),
                CreateCapacity([("Giganews", 1), ("Eweka", 1)]));

            await consumer.StartAsync(CancellationToken.None);

            Assert.Equal(2, consumer.Sessions.Count);
            var declared = factory.LastConnection!.Channels
                .SelectMany(static channel => channel.ExchangeDeclarations.Select(static e => e.Name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(["backfiller.eweka", "backfiller.giganews"], declared);
            Assert.DoesNotContain("backfiller.abavia", declared);
            Assert.DoesNotContain("cache.requests", declared);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Repeated <see cref="BackFillerArticleWorkTopology.DeclareProviderEndpointAsync"/> on one channel records a single declare triple with quorum queue arguments.
        /// </summary>
        [Fact]
        public async Task DeclareProviderEndpoint_is_idempotent_on_same_channel()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);

            await BackFillerArticleWorkTopology.DeclareProviderEndpointAsync(channel, "Giganews", CancellationToken.None);

            Assert.Single(channel.ExchangeDeclarations);
            Assert.Single(channel.QueueDeclarations);
            Assert.Single(channel.BindingDeclarations);
            Assert.All(channel.QueueDeclarations, static q => Assert.True(q.Durable));
            Assert.All(channel.QueueDeclarations, static q =>
            {
                Assert.NotNull(q.Arguments);
                Assert.True(q.Arguments.TryGetValue(BackFillerArticleWorkTopology.QueueTypeArgumentName, out var type));
                Assert.Equal(BackFillerArticleWorkTopology.QuorumQueueType, type);
            });
        }

        /// <summary>
        /// Consumer service with deferred handler and recording publisher for topology-only assertions.
        /// </summary>
        /// <param name="connections">Started RabbitMQ service.</param>
        /// <param name="catalog">Provider definitions driving queue names.</param>
        /// <param name="capacity">Usable capacity state consumed at start.</param>
        /// <returns>New <see cref="ArticleWorkConsumerService"/>.</returns>
        private static ArticleWorkConsumerService CreateConsumer(
            IRabbitMqService connections,
            IBackFillerProviderCatalog catalog,
            BackboneUsableCapacityState capacity) =>
            new(
                connections,
                RabbitMqServiceTests.CreateFastRuntime(),
                new DeferredArticleWorkHandler(),
                new RecordingArticleWorkResponsePublisher(),
                NullLogger<ArticleWorkConsumerService>.Instance,
                catalog,
                capacity);

        /// <summary>
        /// Builds a static catalog from backbone/max-session pairs with fixed loopback NNTP endpoints.
        /// </summary>
        /// <param name="providers">Backbone names and session ceilings.</param>
        /// <returns>Catalog passed to the consumer service.</returns>
        private static StaticBackFillerProviderCatalog CreateCatalog(
            IReadOnlyList<(string Backbone, int MaxSessions)> providers) =>
            new(
            [
                ..providers.Select(static p => new BackFillerProviderDefinition(
                    p.Backbone,
                    "127.0.0.1",
                    119,
                    false,
                    "nntp-user",
                    "p",
                    0,
                    p.MaxSessions)),
            ]);

        /// <summary>
        /// Creates capacity state, publishing a snapshot when at least one backbone has active slots above zero.
        /// </summary>
        /// <param name="capacities">Backbone/active-slot pairs.</param>
        /// <returns>Initialized <see cref="BackboneUsableCapacityState"/>.</returns>
        private static BackboneUsableCapacityState CreateCapacity(
            IReadOnlyList<(string Backbone, int Active)> capacities)
        {
            var state = new BackboneUsableCapacityState();
            var snapshot = capacities
                .Where(static pair => pair.Active > 0)
                .ToDictionary(
                    static pair => pair.Backbone,
                    static pair => pair.Active,
                    StringComparer.OrdinalIgnoreCase);
            if (snapshot.Count > 0)
            {
                state.PublishSnapshot(snapshot);
            }

            return state;
        }
    }
}
