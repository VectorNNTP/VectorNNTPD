using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Topology declaration isolation: one backbone's channel-closing declare failure must not block later backbones.
    /// </summary>
    public sealed class ArticleWorkTopologyDeclarationIsolationTests
    {
        /// <summary>
        /// AMQP reply code for <c>PRECONDITION_FAILED</c> (inequivalent queue arguments).
        /// </summary>
        private const ushort PreconditionFailedReplyCode = 406;

        /// <summary>
        /// Event id for <see cref="ArticleWorkLogMessages.ProviderTopologyDeclareFailed"/>.
        /// </summary>
        private const int ProviderTopologyDeclareFailedEventId = 5320;

        /// <summary>
        /// Alphabetically earlier backbone closes the topology channel; a later backbone still declares and gets a consumer.
        /// </summary>
        [Fact]
        public async Task First_declare_failure_does_not_block_later_backbone()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            ArmQueueDeclareFailure(
                factory,
                queue => queue.Equals("backfiller.abavia", StringComparison.Ordinal)
                    ? CreatePreconditionFailed("backfiller.abavia")
                    : null);

            var logger = new CollectingLogger<ArticleWorkConsumerService>();
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Abavia", 1), ("Giganews", 1)]),
                CreateCapacity([("Abavia", 1), ("Giganews", 1)]),
                logger);

            await consumer.StartAsync(CancellationToken.None);

            var session = Assert.Single(consumer.Sessions);
            Assert.Equal("Giganews", session.Backbone);
            Assert.Equal(ArticleWorkConsumerState.Running, session.State);
            Assert.Contains(
                factory.LastConnection!.Channels,
                static channel => channel.QueueDeclarations.Any(static q => q.Name == "backfiller.giganews"));
            Assert.DoesNotContain(
                factory.LastConnection.Channels.SelectMany(static c => c.QueueDeclarations),
                static q => q.Name == "backfiller.abavia");
            Assert.Contains(
                logger.Entries,
                static entry => entry.EventId.Id == ProviderTopologyDeclareFailedEventId
                                && entry.Message.Contains("backfiller.abavia", StringComparison.Ordinal)
                                && entry.Message.Contains($"replyCode={PreconditionFailedReplyCode}", StringComparison.Ordinal));

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// After a declare failure closes a channel, that channel is not reused for a later backbone.
        /// </summary>
        [Fact]
        public async Task Closed_topology_channel_is_not_reused_for_later_backbone()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            ArmQueueDeclareFailure(
                factory,
                queue => queue.Equals("backfiller.abavia", StringComparison.Ordinal)
                    ? CreatePreconditionFailed("backfiller.abavia")
                    : null);

            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Abavia", 1), ("Giganews", 1)]),
                CreateCapacity([("Abavia", 1), ("Giganews", 1)]));

            await consumer.StartAsync(CancellationToken.None);

            var topologyChannels = factory.LastConnection!.Channels
                .Where(static channel => channel.ConsumeCount == 0)
                .ToArray();
            Assert.True(topologyChannels.Length >= 2);
            var failed = Assert.Single(
                topologyChannels,
                static channel => channel.QueueDeclarations.Count == 0 && channel.DisposeCount == 1);
            Assert.False(failed.IsOpen);
            Assert.Contains(
                failed.ExchangeDeclarations,
                static exchange => exchange.Name == "backfiller.abavia");
            var succeeded = Assert.Single(
                topologyChannels,
                static channel => channel.QueueDeclarations.Any(static q => q.Name == "backfiller.giganews"));
            Assert.NotSame(failed, succeeded);
            Assert.Equal(1, succeeded.DisposeCount);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Multiple independently failing backbones do not prevent a later healthy backbone from declaring and consuming.
        /// </summary>
        [Fact]
        public async Task Multiple_declare_failures_do_not_block_a_later_healthy_backbone()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            ArmQueueDeclareFailure(
                factory,
                queue => queue is "backfiller.abavia" or "backfiller.eweka"
                    ? CreatePreconditionFailed(queue)
                    : null);

            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Abavia", 1), ("Eweka", 1), ("Giganews", 1)]),
                CreateCapacity([("Abavia", 1), ("Eweka", 1), ("Giganews", 1)]));

            await consumer.StartAsync(CancellationToken.None);

            var session = Assert.Single(consumer.Sessions);
            Assert.Equal("Giganews", session.Backbone);
            Assert.Contains(
                factory.LastConnection!.Channels,
                static channel => channel.QueueDeclarations.Any(static q => q.Name == "backfiller.giganews"));

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// A backbone that failed topology declaration is retried on a later reconcile and can start once the failure clears.
        /// </summary>
        [Fact]
        public async Task Failed_backbone_can_be_declared_on_a_later_reconcile_pass()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var failAbavia = true;
            ArmQueueDeclareFailure(
                factory,
                queue => failAbavia && queue.Equals("backfiller.abavia", StringComparison.Ordinal)
                    ? CreatePreconditionFailed("backfiller.abavia")
                    : null);

            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Abavia", 1), ("Giganews", 1)]),
                CreateCapacity([("Abavia", 1), ("Giganews", 1)]));

            await consumer.StartAsync(CancellationToken.None);
            Assert.Equal("Giganews", Assert.Single(consumer.Sessions).Backbone);

            failAbavia = false;
            await consumer.ReconcileAsync(CancellationToken.None);

            Assert.Equal(2, consumer.Sessions.Count);
            Assert.Contains(consumer.Sessions, static session => session.Backbone == "Abavia");
            Assert.Contains(consumer.Sessions, static session => session.Backbone == "Giganews");
            Assert.All(consumer.Sessions, static session => Assert.Equal(ArticleWorkConsumerState.Running, session.State));

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// An already-running healthy consumer is retained while another backbone's topology failure is isolated.
        /// </summary>
        [Fact]
        public async Task Running_consumer_is_retained_when_another_backbone_declare_fails()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var capacity = CreateCapacity([("Giganews", 1)]);
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Abavia", 1), ("Giganews", 1)]),
                capacity);

            await consumer.StartAsync(CancellationToken.None);
            var healthy = Assert.Single(consumer.Sessions);
            Assert.Equal("Giganews", healthy.Backbone);
            var healthyChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(healthy.Channel);

            ArmQueueDeclareFailure(
                factory,
                queue => queue.Equals("backfiller.abavia", StringComparison.Ordinal)
                    ? CreatePreconditionFailed("backfiller.abavia")
                    : null);
            capacity.PublishSnapshot(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Giganews"] = 1,
                ["Abavia"] = 1,
            });
            await consumer.ReconcileAsync(CancellationToken.None);

            Assert.Same(healthy, Assert.Single(consumer.Sessions));
            Assert.Equal(ArticleWorkConsumerState.Running, healthy.State);
            Assert.Same(healthyChannel, healthy.Channel);
            Assert.Equal(0, healthyChannel.DisposeCount);
            Assert.Contains(
                factory.LastConnection!.Channels,
                static channel => channel.ExchangeDeclarations.Any(static e => e.Name == "backfiller.abavia")
                                  && channel.QueueDeclarations.Count == 0);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Topology channels opened for the pass are disposed after success and after failure.
        /// </summary>
        [Fact]
        public async Task Topology_channels_are_disposed_after_success_and_failure()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            ArmQueueDeclareFailure(
                factory,
                queue => queue.Equals("backfiller.abavia", StringComparison.Ordinal)
                    ? CreatePreconditionFailed("backfiller.abavia")
                    : null);

            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Abavia", 1), ("Giganews", 1)]),
                CreateCapacity([("Abavia", 1), ("Giganews", 1)]));

            await consumer.StartAsync(CancellationToken.None);

            var topologyChannels = factory.LastConnection!.Channels
                .Where(static channel => channel.ConsumeCount == 0)
                .ToArray();
            Assert.All(topologyChannels, static channel => Assert.Equal(1, channel.DisposeCount));

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Cancellation during topology declaration stops further backbone attempts and propagates.
        /// </summary>
        [Fact]
        public async Task Cancellation_stops_further_topology_declarations()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            using var cts = new CancellationTokenSource();
            factory.LastConnection!.OnManualAckChannelCreated = _ => cts.Cancel();

            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Abavia", 1), ("Giganews", 1)]),
                CreateCapacity([("Abavia", 1), ("Giganews", 1)]));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => consumer.StartAsync(cts.Token));

            Assert.Empty(consumer.Sessions);
            Assert.True(factory.LastConnection.Channels.Count <= 1);
            Assert.DoesNotContain(
                factory.LastConnection.Channels.SelectMany(static c => c.QueueDeclarations),
                static q => q.Name == "backfiller.giganews");

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Successful declarations for several backbones share one open topology channel for the pass.
        /// </summary>
        [Fact]
        public async Task Successful_declarations_reuse_one_open_topology_channel()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Eweka", 1), ("Giganews", 1)]),
                CreateCapacity([("Eweka", 1), ("Giganews", 1)]));

            await consumer.StartAsync(CancellationToken.None);

            Assert.Equal(2, consumer.Sessions.Count);
            var topology = Assert.Single(
                factory.LastConnection!.Channels,
                static channel => channel.ConsumeCount == 0);
            Assert.Equal(2, topology.QueueDeclarations.Count);
            Assert.Equal(1, topology.DisposeCount);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        private static void ArmQueueDeclareFailure(
            FakeBackFillerRabbitMqConnectionFactory factory,
            Func<string, Exception?> failure)
        {
            factory.LastConnection!.OnManualAckChannelCreated = channel =>
            {
                channel.QueueDeclareFailure = failure;
            };
        }

        private static OperationInterruptedException CreatePreconditionFailed(string queue)
        {
            var shutdown = new ShutdownEventArgs(
                ShutdownInitiator.Peer,
                PreconditionFailedReplyCode,
                $"PRECONDITION_FAILED - inequivalent arg 'x-queue-type' for queue '{queue}'",
                cause: null!,
                CancellationToken.None);
            return new OperationInterruptedException(shutdown);
        }

        private static ArticleWorkConsumerService CreateConsumer(
            IRabbitMqService connections,
            IBackFillerProviderCatalog catalog,
            BackboneUsableCapacityState capacity,
            ILogger<ArticleWorkConsumerService>? logger = null) =>
            new(
                connections,
                RabbitMqServiceTests.CreateFastRuntime(),
                new DeferredArticleWorkHandler(),
                new RecordingArticleWorkResponsePublisher(),
                logger ?? NullLogger<ArticleWorkConsumerService>.Instance,
                catalog,
                capacity);

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
