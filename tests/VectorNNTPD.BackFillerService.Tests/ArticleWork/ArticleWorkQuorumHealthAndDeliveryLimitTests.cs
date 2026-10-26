using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Quorum membership readiness and finite delivery-limit settlement for Article Work (#195).
    /// </summary>
    public sealed class ArticleWorkQuorumHealthAndDeliveryLimitTests
    {
        /// <summary>Structured log event id for quorum membership not ready.</summary>
        private const int QuorumMembershipNotReadyEventId = 5325;

        /// <summary>Structured log event id for quorum membership healthy.</summary>
        private const int QuorumMembershipHealthyEventId = 5326;

        /// <summary>
        /// One configured host declares classic topology without quorum or delivery-limit arguments.
        /// </summary>
        [Fact]
        public async Task One_host_declares_classic_topology()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var runtime = CreateRuntime(hostCount: 1, deliveryLimit: 7);

            await BackFillerArticleWorkTopology.DeclareProviderEndpointAsync(
                channel,
                "Giganews",
                runtime,
                CancellationToken.None);

            var declaration = Assert.Single(channel.QueueDeclarations);
            Assert.Equal(
                BackFillerArticleWorkTopology.ClassicQueueType,
                declaration.Arguments![BackFillerArticleWorkTopology.QueueTypeArgumentName]);
            Assert.False(declaration.Arguments.ContainsKey(BackFillerArticleWorkTopology.QuorumInitialGroupSizeArgumentName));
            Assert.False(declaration.Arguments.ContainsKey(BackFillerArticleWorkTopology.DeliveryLimitArgumentName));
            Assert.Equal(ArticleWorkQueueKind.Classic, runtime.RabbitMq.QueueTopology.Kind);
            Assert.Equal(1, runtime.RabbitMq.QueueTopology.ConfiguredHostCount);
            Assert.Null(runtime.RabbitMq.QueueTopology.QuorumInitialGroupSize);
            Assert.False(runtime.RabbitMq.QueueTopology.RequiresQuorumHealth);
        }

        /// <summary>
        /// Two configured hosts declare quorum topology with initial group size 2 and delivery limit.
        /// </summary>
        [Fact]
        public async Task Two_hosts_declare_quorum_topology_with_group_size_2()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var runtime = CreateRuntime(hostCount: 2, deliveryLimit: 7);

            await BackFillerArticleWorkTopology.DeclareProviderEndpointAsync(
                channel,
                "Giganews",
                runtime,
                CancellationToken.None);

            var declaration = Assert.Single(channel.QueueDeclarations);
            Assert.Equal(
                BackFillerArticleWorkTopology.QuorumQueueType,
                declaration.Arguments![BackFillerArticleWorkTopology.QueueTypeArgumentName]);
            Assert.Equal(2L, declaration.Arguments[BackFillerArticleWorkTopology.QuorumInitialGroupSizeArgumentName]);
            Assert.Equal(7L, declaration.Arguments[BackFillerArticleWorkTopology.DeliveryLimitArgumentName]);
            Assert.Equal(2, runtime.RabbitMq.QueueTopology.QuorumInitialGroupSize);
        }

        /// <summary>
        /// Three configured hosts declare quorum topology with initial group size 3.
        /// </summary>
        [Fact]
        public async Task Three_hosts_declare_quorum_topology_with_group_size_3()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var runtime = CreateRuntime(hostCount: 3, deliveryLimit: 10);

            await BackFillerArticleWorkTopology.DeclareProviderEndpointAsync(
                channel,
                "Giganews",
                runtime,
                CancellationToken.None);

            var declaration = Assert.Single(channel.QueueDeclarations);
            Assert.Equal(3L, declaration.Arguments![BackFillerArticleWorkTopology.QuorumInitialGroupSizeArgumentName]);
            Assert.Equal(3, runtime.RabbitMq.QueueTopology.ConfiguredHostCount);
        }

        /// <summary>
        /// A larger supported host count maps to the same initial group size without clamping.
        /// </summary>
        [Fact]
        public async Task Seven_hosts_declare_quorum_topology_with_group_size_7()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var runtime = CreateRuntime(hostCount: 7, deliveryLimit: 10);

            await BackFillerArticleWorkTopology.DeclareProviderEndpointAsync(
                channel,
                "Giganews",
                runtime,
                CancellationToken.None);

            var declaration = Assert.Single(channel.QueueDeclarations);
            Assert.Equal(7L, declaration.Arguments![BackFillerArticleWorkTopology.QuorumInitialGroupSizeArgumentName]);
            Assert.Equal(7, runtime.RabbitMq.QueueTopology.QuorumInitialGroupSize);
        }

        /// <summary>
        /// Configured host count (not live reachability) remains the topology source when a host is unavailable.
        /// </summary>
        [Fact]
        public void Unavailable_configured_host_does_not_change_queue_type_or_group_size()
        {
            var rabbit = BackFillerTestOptions.CreateValidRabbitMqWithHostCount(3);
            // Simulate an outage: configuration still lists three hosts; topology must not shrink.
            var topology = ArticleWorkQueueTopology.FromConfiguredHosts(rabbit.Hosts);
            Assert.Equal(ArticleWorkQueueKind.Quorum, topology.Kind);
            Assert.Equal(3, topology.ConfiguredHostCount);
            Assert.Equal(3, topology.QuorumInitialGroupSize);
            Assert.True(topology.RequiresQuorumHealth);
        }

        /// <summary>
        /// First delivery (no <c>x-delivery-count</c>) may requeue within the configured limit.
        /// </summary>
        [Fact]
        public async Task First_delivery_without_count_requeues_on_retryable_failure()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Cancelled },
                new RecordingArticleWorkResponsePublisher(),
                maxPayloadBytes: 1024,
                deliveryLimit: 3);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// The final permitted retry (count == limit - 1) still requeues.
        /// </summary>
        [Fact]
        public async Task Final_permitted_retry_still_requeues()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Cancelled },
                new RecordingArticleWorkResponsePublisher(),
                maxPayloadBytes: 1024,
                deliveryLimit: 5);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Create(ArticleWorkTestDeliveries.CanonicalRequestJson, deliveryCount: 4),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// Single-host classic readiness does not call the quorum Management probe.
        /// </summary>
        [Fact]
        public async Task Single_host_readiness_does_not_call_quorum_management_probe()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);

            var probe = new ControllableArticleWorkQuorumHealthProbe
            {
                Next = Deficient(required: 3, observed: 1, online: 1),
            };
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Giganews", 1)]),
                CreateCapacity([("Giganews", 1)]),
                quorumHealth: probe,
                hostCount: 1);

            await consumer.StartAsync(CancellationToken.None);

            var session = Assert.Single(consumer.Sessions);
            Assert.Equal(ArticleWorkConsumerState.Running, session.State);
            Assert.Equal(0, probe.InspectCount);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Successful declaration with insufficient membership leaves the backbone unready.
        /// </summary>
        [Fact]
        public async Task Insufficient_membership_does_not_start_consumer()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);

            var probe = new ControllableArticleWorkQuorumHealthProbe
            {
                Next = Deficient(required: 3, observed: 1, online: 1),
            };
            var logger = new CollectingLogger<ArticleWorkConsumerService>();
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Giganews", 1)]),
                CreateCapacity([("Giganews", 1)]),
                logger,
                probe,
                hostCount: 3);

            await consumer.StartAsync(CancellationToken.None);

            Assert.Empty(consumer.Sessions);
            Assert.Equal(1, probe.InspectCount);
            Assert.Contains(
                logger.Entries,
                static entry => entry.EventId.Id == QuorumMembershipNotReadyEventId
                                && entry.Message.Contains("InsufficientMembers", StringComparison.Ordinal));

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Sufficient configured and online membership allows readiness after declaration.
        /// </summary>
        [Fact]
        public async Task Healthy_membership_allows_consumer_start()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);

            var probe = new ControllableArticleWorkQuorumHealthProbe
            {
                Next = Healthy(required: 3, observed: 3, online: 3),
            };
            var logger = new CollectingLogger<ArticleWorkConsumerService>();
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Giganews", 1)]),
                CreateCapacity([("Giganews", 1)]),
                logger,
                probe,
                hostCount: 3);

            await consumer.StartAsync(CancellationToken.None);

            var session = Assert.Single(consumer.Sessions);
            Assert.Equal(ArticleWorkConsumerState.Running, session.State);
            Assert.Contains(
                logger.Entries,
                static entry => entry.EventId.Id == QuorumMembershipHealthyEventId);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Unverifiable membership keeps the backbone unready and does not claim false readiness.
        /// </summary>
        [Fact]
        public async Task Unverifiable_membership_keeps_backbone_unready()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);

            var probe = new ControllableArticleWorkQuorumHealthProbe
            {
                Next = new ArticleWorkQuorumHealthResult(
                    ArticleWorkQuorumHealthKind.Unverifiable,
                    RequiredMembers: 3,
                    ObservedMembers: null,
                    OnlineMembers: null,
                    Leader: null,
                    Reason: "Management API request timed out."),
            };
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Giganews", 1)]),
                CreateCapacity([("Giganews", 1)]),
                quorumHealth: probe,
                hostCount: 3);

            await consumer.StartAsync(CancellationToken.None);

            Assert.Empty(consumer.Sessions);
            Assert.Equal(1, probe.InspectCount);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// A later healthy membership observation makes the backbone ready through reconciliation.
        /// </summary>
        [Fact]
        public async Task Later_healthy_membership_allows_readiness_via_reconcile()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);

            var probe = new ControllableArticleWorkQuorumHealthProbe
            {
                Next = Deficient(required: 3, observed: 1, online: 1),
            };
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Giganews", 1)]),
                CreateCapacity([("Giganews", 1)]),
                quorumHealth: probe,
                hostCount: 3);

            await consumer.StartAsync(CancellationToken.None);
            Assert.Empty(consumer.Sessions);
            Assert.True(consumer.ConsecutiveInfrastructureUnavailable >= 1);

            await consumer.ReconcileAsync(CancellationToken.None);
            Assert.True(consumer.ConsecutiveInfrastructureUnavailable >= 2);

            probe.Next = Healthy(required: 3, observed: 3, online: 3);
            await consumer.ReconcileAsync(CancellationToken.None);

            var session = Assert.Single(consumer.Sessions);
            Assert.Equal(ArticleWorkConsumerState.Running, session.State);
            Assert.Equal(0, consumer.ConsecutiveInfrastructureUnavailable);
            Assert.True(probe.InspectCount >= 3);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// A healthy running consumer is kept when membership remains healthy on reconcile.
        /// </summary>
        [Fact]
        public async Task Healthy_consumer_is_not_recreated_when_membership_stays_healthy()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);

            var probe = new ControllableArticleWorkQuorumHealthProbe
            {
                Next = Healthy(required: 3, observed: 3, online: 3),
            };
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Giganews", 1)]),
                CreateCapacity([("Giganews", 1)]),
                quorumHealth: probe,
                hostCount: 3);

            await consumer.StartAsync(CancellationToken.None);
            var original = Assert.Single(consumer.Sessions);

            await consumer.ReconcileAsync(CancellationToken.None);

            var after = Assert.Single(consumer.Sessions);
            Assert.Same(original, after);
            Assert.Equal(ArticleWorkConsumerState.Running, after.State);

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Within the delivery limit, retryable failures still requeue.
        /// </summary>
        [Fact]
        public async Task Retryable_failure_requeues_within_delivery_limit()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Cancelled },
                new RecordingArticleWorkResponsePublisher(),
                maxPayloadBytes: 1024,
                deliveryLimit: 10);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Create(ArticleWorkTestDeliveries.CanonicalRequestJson, deliveryCount: 3),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// When delivery count reaches the configured limit, work is NACKed without requeue.
        /// </summary>
        [Fact]
        public async Task Exhausted_delivery_limit_nacks_without_requeue()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Cancelled },
                new RecordingArticleWorkResponsePublisher(),
                maxPayloadBytes: 1024,
                deliveryLimit: 5);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Create(ArticleWorkTestDeliveries.CanonicalRequestJson, deliveryCount: 5),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
        }

        /// <summary>
        /// Cancellation that has exhausted the delivery limit does not bypass the finite policy.
        /// </summary>
        [Fact]
        public async Task Cancelled_delivery_at_limit_does_not_requeue()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Success },
                new RecordingArticleWorkResponsePublisher(),
                maxPayloadBytes: 1024,
                deliveryLimit: 4);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Create(ArticleWorkTestDeliveries.CanonicalRequestJson, deliveryCount: 4),
                "Giganews",
                channel,
                static () => true,
                cts.Token);

            Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
        }

        /// <summary>
        /// Successful processing still ACKs when delivery count is high.
        /// </summary>
        [Fact]
        public async Task Successful_processing_still_acks_at_high_delivery_count()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true };
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Success },
                publisher,
                maxPayloadBytes: 1024,
                deliveryLimit: 3);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Create(ArticleWorkTestDeliveries.CanonicalRequestJson, deliveryCount: 3),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Single(publisher.Published);
            var settlement = Assert.Single(channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
        }

        /// <summary>
        /// Publisher failure at the limit settles without requeue so confirm loss cannot bypass the cap.
        /// </summary>
        [Fact]
        public async Task Publisher_failure_at_limit_does_not_requeue()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var publisher = new RecordingArticleWorkResponsePublisher
            {
                CompletesSuccessPublication = true,
                PublishException = new InvalidOperationException("confirm failed"),
            };
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Success },
                publisher,
                maxPayloadBytes: 1024,
                deliveryLimit: 2);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Create(ArticleWorkTestDeliveries.CanonicalRequestJson, deliveryCount: 2),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
        }

        /// <summary>
        /// On quorum topology, a redelivery missing <c>x-delivery-count</c> is treated as exhausted so missing metadata cannot disable the cap.
        /// </summary>
        [Fact]
        public async Task Quorum_redelivery_without_delivery_count_does_not_requeue()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Cancelled },
                new RecordingArticleWorkResponsePublisher(),
                maxPayloadBytes: 1024,
                deliveryLimit: 5,
                treatMissingRedeliveryCountAsExhausted: true);

            var delivery = ArticleWorkTestDeliveries.Create(ArticleWorkTestDeliveries.CanonicalRequestJson) with
            {
                Redelivered = true,
                DeliveryCount = null,
            };

            var outcome = await pipeline.ProcessAsync(
                delivery,
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
        }

        /// <summary>
        /// Classic queues lack <c>x-delivery-count</c>; missing count on redelivery must not force discard.
        /// </summary>
        [Fact]
        public async Task Classic_redelivery_without_delivery_count_still_requeues()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Cancelled },
                new RecordingArticleWorkResponsePublisher(),
                maxPayloadBytes: 1024,
                deliveryLimit: 5,
                treatMissingRedeliveryCountAsExhausted: false);

            var delivery = ArticleWorkTestDeliveries.Create(ArticleWorkTestDeliveries.CanonicalRequestJson) with
            {
                Redelivered = true,
                DeliveryCount = null,
            };

            var outcome = await pipeline.ProcessAsync(
                delivery,
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// Cancellation below the limit still requeues.
        /// </summary>
        [Fact]
        public async Task Cancelled_delivery_below_limit_still_requeues()
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(
                new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Success },
                new RecordingArticleWorkResponsePublisher(),
                maxPayloadBytes: 1024,
                deliveryLimit: 4);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Create(ArticleWorkTestDeliveries.CanonicalRequestJson, deliveryCount: 2),
                "Giganews",
                channel,
                static () => true,
                cts.Token);

            Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// Insufficient online members is distinct from unverifiable health and blocks readiness.
        /// </summary>
        [Fact]
        public async Task Insufficient_online_members_blocks_readiness()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);

            var probe = new ControllableArticleWorkQuorumHealthProbe
            {
                Next = new ArticleWorkQuorumHealthResult(
                    ArticleWorkQuorumHealthKind.InsufficientOnlineMembers,
                    RequiredMembers: 3,
                    ObservedMembers: 3,
                    OnlineMembers: 1,
                    Leader: "rabbit@node-a",
                    Reason: "Online quorum membership is below the required availability floor."),
            };
            var logger = new CollectingLogger<ArticleWorkConsumerService>();
            var consumer = CreateConsumer(
                connections,
                CreateCatalog([("Giganews", 1)]),
                CreateCapacity([("Giganews", 1)]),
                logger,
                probe,
                hostCount: 3);

            await consumer.StartAsync(CancellationToken.None);

            Assert.Empty(consumer.Sessions);
            Assert.Contains(
                logger.Entries,
                static entry => entry.EventId.Id == QuorumMembershipNotReadyEventId
                                && entry.Message.Contains("InsufficientOnlineMembers", StringComparison.Ordinal));

            await consumer.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// Effective count resolution treats first delivery and redelivery-without-count distinctly.
        /// </summary>
        [Fact]
        public void Effective_delivery_count_is_conservative_for_redelivery_without_header()
        {
            Assert.Equal(0, ArticleWorkDispositionPlanner.ResolveEffectiveDeliveryCount(null, redelivered: false, deliveryLimit: 10));
            Assert.Equal(10, ArticleWorkDispositionPlanner.ResolveEffectiveDeliveryCount(null, redelivered: true, deliveryLimit: 10));
            Assert.Equal(4, ArticleWorkDispositionPlanner.ResolveEffectiveDeliveryCount(4, redelivered: true, deliveryLimit: 10));
        }

        /// <summary>
        /// Management URI builder encodes vhost and queue without credentials.
        /// </summary>
        [Fact]
        public void Management_queue_uri_encodes_vhost_and_queue()
        {
            var uri = ArticleWorkQuorumManagementHealthProbe.BuildQueueUri(
                "http://198.18.0.3:15672",
                "/",
                "backfiller.Giganews");

            Assert.Equal(
                "http://198.18.0.3:15672/api/queues/%2F/backfiller.Giganews",
                uri.AbsoluteUri);
        }

        /// <summary>
        /// Management probe maps HTTP auth failure to Unverifiable, not insufficient membership.
        /// </summary>
        [Fact]
        public async Task Management_probe_auth_failure_is_unverifiable()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.Unauthorized, "{}");
            var (probe, lifetime) = CreateManagementProbe(handler, baseUrl: "http://127.0.0.1:15672");
            using (lifetime)
            {
                var result = await probe.InspectAsync("backfiller.Giganews", requiredMembers: 3, CancellationToken.None);

                Assert.Equal(ArticleWorkQuorumHealthKind.Unverifiable, result.Kind);
                Assert.Contains("401", result.Reason, StringComparison.Ordinal);
                Assert.Null(result.ObservedMembers);
            }
        }

        /// <summary>
        /// Management probe maps a healthy queue document to Healthy when membership policy is met.
        /// </summary>
        [Fact]
        public async Task Management_probe_parses_healthy_membership_document()
        {
            var body = """{"members":["a","b","c"],"online":["a","b","c"],"leader":"a"}""";
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, body);
            var (probe, lifetime) = CreateManagementProbe(handler, baseUrl: "http://127.0.0.1:15672");
            using (lifetime)
            {
                var result = await probe.InspectAsync("backfiller.Giganews", requiredMembers: 3, CancellationToken.None);

                Assert.Equal(ArticleWorkQuorumHealthKind.Healthy, result.Kind);
                Assert.Equal(3, result.ObservedMembers);
                Assert.Equal(3, result.OnlineMembers);
                Assert.Equal("a", result.Leader);
            }
        }

        /// <summary>
        /// Missing leader in an otherwise complete document is Unverifiable.
        /// </summary>
        [Fact]
        public async Task Management_probe_missing_leader_is_unverifiable()
        {
            var body = """{"members":["a","b","c"],"online":["a","b","c"]}""";
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, body);
            var (probe, lifetime) = CreateManagementProbe(handler, baseUrl: "http://127.0.0.1:15672");
            using (lifetime)
            {
                var result = await probe.InspectAsync("backfiller.Giganews", requiredMembers: 3, CancellationToken.None);

                Assert.Equal(ArticleWorkQuorumHealthKind.Unverifiable, result.Kind);
                Assert.Contains("leader", result.Reason, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Builds a healthy quorum probe result for stubbed management responses.</summary>
        private static ArticleWorkQuorumHealthResult Healthy(int required, int observed, int online) =>
            new(
                ArticleWorkQuorumHealthKind.Healthy,
                required,
                observed,
                online,
                Leader: "rabbit@node-a",
                Reason: "Quorum membership and online availability meet the configured policy.");

        /// <summary>Builds an insufficient-members quorum probe result for stubbed management responses.</summary>
        private static ArticleWorkQuorumHealthResult Deficient(int required, int observed, int online) =>
            new(
                ArticleWorkQuorumHealthKind.InsufficientMembers,
                required,
                observed,
                online,
                Leader: "rabbit@node-a",
                Reason: "Configured quorum membership is below the required group size. Operator membership growth is required for pre-existing queues.");

        /// <summary>Creates runtime options with the given RabbitMQ host count and Article Work delivery limit.</summary>
        private static BackFillerRuntimeOptions CreateRuntime(int hostCount, int deliveryLimit)
        {
            var rabbit = BackFillerTestOptions.CreateValidRabbitMqWithHostCount(hostCount);
            rabbit.ArticleWorkDeliveryLimit = deliveryLimit;
            return BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb(),
                BackFillerTestOptions.CreateValidAcme(),
                rabbit);
        }

        /// <summary>Constructs a consumer with deferred handler, recording publisher, and optional quorum probe.</summary>
        private static ArticleWorkConsumerService CreateConsumer(
            IRabbitMqService connections,
            IBackFillerProviderCatalog catalog,
            BackboneUsableCapacityState capacity,
            ILogger<ArticleWorkConsumerService>? logger = null,
            IArticleWorkQuorumHealthProbe? quorumHealth = null,
            int hostCount = 3) =>
            new(
                connections,
                RabbitMqServiceTests.CreateFastRuntime(hostCount),
                new DeferredArticleWorkHandler(),
                new RecordingArticleWorkResponsePublisher(),
                logger ?? NullLogger<ArticleWorkConsumerService>.Instance,
                catalog,
                capacity,
                quorumHealth: quorumHealth);

        /// <summary>Builds a static provider catalog from backbone and max-session pairs.</summary>
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

        /// <summary>Publishes a usable-capacity snapshot omitting backbones with zero active sessions.</summary>
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

        /// <summary>Wires a management health probe to a single-handler HTTP client factory.</summary>
        /// <returns>Probe and disposable factory lifetime.</returns>
        private static (ArticleWorkQuorumManagementHealthProbe Probe, IDisposable Lifetime) CreateManagementProbe(
            HttpMessageHandler handler,
            string baseUrl)
        {
            var options = Options.Create(new RabbitMqOptions
            {
                Hosts = ["127.0.0.1"],
                Username = "guest",
                Password = "guest",
                EnableSsl = false,
                VirtualHost = "/",
                Management = new RabbitMqManagementOptions
                {
                    BaseUrl = baseUrl,
                    RequestTimeoutSeconds = 5,
                },
            });
            var factory = new SingleHandlerHttpClientFactory(handler);
            return (new ArticleWorkQuorumManagementHealthProbe(factory, options), factory);
        }

        /// <summary>Controllable Management stand-in for membership readiness tests.</summary>
        private sealed class ControllableArticleWorkQuorumHealthProbe : IArticleWorkQuorumHealthProbe
        {
            /// <summary>Result returned from the next <see cref="InspectAsync"/> call.</summary>
            internal ArticleWorkQuorumHealthResult Next { get; set; }

            /// <summary>Number of completed inspect calls.</summary>
            internal int InspectCount;

            /// <inheritdoc />
            public Task<ArticleWorkQuorumHealthResult> InspectAsync(
                string queueName,
                int requiredMembers,
                CancellationToken cancellationToken)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
                Interlocked.Increment(ref InspectCount);
                return Task.FromResult(Next);
            }
        }

        /// <summary>Returns one shared <see cref="HttpClient"/> backed by the injected handler.</summary>
        private sealed class SingleHandlerHttpClientFactory : IHttpClientFactory, IDisposable
        {
            /// <summary>Client disposed with the factory.</summary>
            private readonly HttpClient _client;

            /// <summary>Creates a client that owns <paramref name="handler"/>.</summary>
            /// <param name="handler">Handler used for all management API requests.</param>
            public SingleHandlerHttpClientFactory(HttpMessageHandler handler) =>
                _client = new HttpClient(handler, disposeHandler: true);

            /// <inheritdoc />
            public HttpClient CreateClient(string name) => _client;

            /// <summary>Disposes the shared client.</summary>
            public void Dispose() => _client.Dispose();
        }

        /// <summary>Returns a fixed status code and JSON body for every HTTP request.</summary>
        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            /// <summary>HTTP status returned from <see cref="SendAsync"/>.</summary>
            private readonly HttpStatusCode _statusCode;

            /// <summary>UTF-8 response body bytes.</summary>
            private readonly byte[] _bodyBytes;

            /// <summary>Captures the scripted management API response.</summary>
            /// <param name="statusCode">HTTP status for every request.</param>
            /// <param name="body">Response body text encoded as UTF-8.</param>
            public StubHttpMessageHandler(HttpStatusCode statusCode, string body)
            {
                _statusCode = statusCode;
                _bodyBytes = Encoding.UTF8.GetBytes(body);
            }

            /// <inheritdoc />
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                await Task.CompletedTask.ConfigureAwait(false);
                // Returned to HttpClient; InspectAsync disposes the response with using.
                return new HttpResponseMessage(_statusCode)
                {
                    Content = new ByteArrayContent(_bodyBytes)
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                    },
                };
            }
        }
    }
}
