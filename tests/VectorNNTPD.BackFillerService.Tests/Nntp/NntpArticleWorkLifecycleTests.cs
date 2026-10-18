using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;


namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// Provider registry and Article Work consumer lifecycle under configuration snapshots and capacity signals.
    /// </summary>

    public sealed class NntpArticleWorkLifecycleTests
    {
        /// <summary>
        /// MaxSessions zero removes pool, usable capacity, and consumer sessions without connecting.
        /// </summary>

        [Fact]

        public async Task MaxSessions_zero_has_no_pool_no_capacity_and_no_consumer()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            var capacity = new BackboneUsableCapacityState();
            await using var registry = CreateRegistry(catalog, transport, capacity);
            await using var consumer = await StartConsumerAsync(catalog, capacity);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 0)], CancellationToken.None);

            Assert.False(registry.TryGetPool("Giganews", out _));
            Assert.False(capacity.HasUsableCapacityForBackbone("Giganews"));
            Assert.Empty(consumer.Service.Sessions);
            Assert.Empty(transport.ConnectAttempts);
        }

        /// <summary>
        /// Positive MaxSessions eagerly opens sessions and publishes usable capacity before any delivery work.
        /// </summary>

        [Fact]

        public async Task MaxSessions_n_eagerly_connects_without_rabbitmq_work()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(transport, count: 3);
            var capacity = new BackboneUsableCapacityState();
            await using var registry = CreateRegistry(catalog, transport, capacity);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 3)], CancellationToken.None);

            Assert.True(registry.TryGetPool("Giganews", out var pool));
            Assert.Equal(3, pool.ActiveSessionCount);
            Assert.Equal(3, transport.ConnectAttempts.Count);
            Assert.True(capacity.HasUsableCapacityForBackbone("Giganews"));
            Assert.Equal(3, capacity.GetUsableCapacityForBackbone("Giganews"));
        }

        /// <summary>
        /// Consumers exist only for configured provider backbones present in the catalog snapshot.
        /// </summary>

        [Fact]

        public async Task Topology_backbone_without_a_database_row_never_gets_a_consumer()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(transport, count: 1);
            var capacity = new BackboneUsableCapacityState();
            await using var registry = CreateRegistry(catalog, transport, capacity);
            await using var consumer = await StartConsumerAsync(catalog, capacity);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 1)], CancellationToken.None);
            await WaitUntilAsync(() => consumer.Service.Sessions.Count == 1);

            Assert.DoesNotContain(consumer.Service.Sessions, static session => session.Backbone == "Abavia");
            Assert.DoesNotContain(
                BackFillerRabbitMqTopology.ProviderBackbones.Where(static name => name != "Giganews"),
                name => consumer.Service.Sessions.Any(session => session.Backbone == name));
            Assert.Equal("Giganews", Assert.Single(consumer.Service.Sessions).Backbone);
        }

        /// <summary>
        /// Failed connects leave zero usable capacity and no consumer sessions despite configuration.
        /// </summary>

        [Fact]

        public async Task Configured_provider_with_zero_active_sessions_has_no_consumer()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory
            {
                ConnectException = new IOException("down"),
            };
            var capacity = new BackboneUsableCapacityState();
            await using var registry = CreateRegistry(catalog, transport, capacity);
            await using var consumer = await StartConsumerAsync(catalog, capacity);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 2)], CancellationToken.None);

            Assert.True(catalog.TryGetProvider("Giganews", out _));
            Assert.False(capacity.HasUsableCapacityForBackbone("Giganews"));
            Assert.Empty(consumer.Service.Sessions);
        }

        /// <summary>
        /// Capacity snapshot scales consumer sessions to the desired per-backbone slot count.
        /// </summary>

        [Fact]

        public async Task Active_capacity_starts_consumers_matching_desired_slot_count()
        {
            var catalog = new StaticBackFillerProviderCatalog([CreateProvider("Giganews", maxSessions: 3)]);
            var capacity = new BackboneUsableCapacityState();
            await using var consumer = await StartConsumerAsync(catalog, capacity);
            Assert.Empty(consumer.Service.Sessions);

            capacity.PublishSnapshot(new Dictionary<string, int> { ["Giganews"] = 1 });
            await WaitUntilAsync(() => consumer.Service.Sessions.Count == 3);

            Assert.Equal(new[] { 1, 2, 3 }, consumer.Service.Sessions.Select(static s => s.ConnectionNumber).OrderBy(static n => n));
        }

        /// <summary>
        /// Empty capacity snapshot retires all consumer sessions.
        /// </summary>

        [Fact]

        public async Task Loss_of_all_active_sessions_retires_consumers_after_capacity_publish()
        {
            var catalog = new StaticBackFillerProviderCatalog([CreateProvider("Giganews", maxSessions: 2)]);
            var capacity = new BackboneUsableCapacityState();
            capacity.PublishSnapshot(new Dictionary<string, int> { ["Giganews"] = 2 });
            await using var consumer = await StartConsumerAsync(catalog, capacity);
            Assert.Equal(2, consumer.Service.Sessions.Count);

            capacity.PublishSnapshot(new Dictionary<string, int>());
            await WaitUntilAsync(() => consumer.Service.Sessions.Count == 0);
        }

        /// <summary>
        /// Retiring a leased session triggers replenishment back toward MaxSessions.
        /// </summary>

        [Fact]

        public async Task Session_loss_replenishes_toward_max_sessions()
        {
            var factory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 2);
            await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 1);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            Assert.Equal(1, pool.ActiveSessionCount);

            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 1);
            await using (var lease = await pool.AcquireAsync(CancellationToken.None))
            {
                lease.Retire();
            }

            await WaitUntilAsync(() => pool.ActiveSessionCount == 1 && factory.ConnectAttempts.Count >= 2);
            Assert.Equal(1, pool.ActiveSessionCount);
        }

        /// <summary>
        /// Eager sessions remain connected with zero active leases when no work arrives.
        /// </summary>

        [Fact]

        public async Task Idle_connected_provider_stays_connected_without_article_work()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(transport, count: 2);
            var capacity = new BackboneUsableCapacityState();
            await using var registry = CreateRegistry(catalog, transport, capacity);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 2)], CancellationToken.None);
            Assert.True(registry.TryGetPool("Giganews", out var pool));
            Assert.Equal(2, pool.ActiveSessionCount);
            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(2, transport.ConnectAttempts.Count);
        }

        /// <summary>
        /// Removing a provider from the snapshot retires pool and consumers and clears capacity.
        /// </summary>

        [Fact]

        public async Task Provider_removal_retires_pool_and_consumers()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(transport, count: 1);
            var capacity = new BackboneUsableCapacityState();
            await using var consumer = await StartConsumerAsync(catalog, capacity);
            await using var registry = CreateRegistry(catalog, transport, capacity, consumer.Service);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 1)], CancellationToken.None);
            await WaitUntilAsync(() => consumer.Service.Sessions.Count == 1);

            await registry.ApplySnapshotAsync([], CancellationToken.None);
            await WaitUntilAsync(() => consumer.Service.Sessions.Count == 0 && !registry.TryGetPool("Giganews", out _));
            Assert.False(capacity.HasUsableCapacityForBackbone("Giganews"));
        }

        /// <summary>
        /// Shrinking MaxSessions retires excess sessions while keeping the pool and lowest connection numbers.
        /// </summary>

        [Fact]

        public async Task Provider_shrink_retains_consumers_up_to_new_max_and_keeps_the_pool()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(transport, count: 4);
            var capacity = new BackboneUsableCapacityState();
            await using var consumer = await StartConsumerAsync(catalog, capacity);
            await using var registry = CreateRegistry(catalog, transport, capacity, consumer.Service);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 4)], CancellationToken.None);
            await WaitUntilAsync(() => consumer.Service.Sessions.Count == 4);
            Assert.True(registry.TryGetPool("Giganews", out var pool));
            var retained = consumer.Service.Sessions
                .Where(static session => session.ConnectionNumber <= 2)
                .OrderBy(static session => session.ConnectionNumber)
                .ToArray();
            Assert.Equal(new[] { 1, 2 }, retained.Select(static session => session.ConnectionNumber));
            Assert.Equal(4, capacity.GetUsableCapacityForBackbone("Giganews"));

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 2)], CancellationToken.None);

            Assert.True(registry.TryGetPool("Giganews", out var after));
            Assert.Same(pool, after);
            Assert.Equal(2, after.LiveSessionCount);
            Assert.Equal(2, after.ActiveSessionCount);
            Assert.True(capacity.HasUsableCapacityForBackbone("Giganews"));
            Assert.Equal(2, capacity.GetUsableCapacityForBackbone("Giganews"));
            Assert.Equal(2, consumer.Service.Sessions.Count);
            Assert.Equal(
                new[] { 1, 2 },
                consumer.Service.Sessions.Select(static session => session.ConnectionNumber).OrderBy(static n => n));
            Assert.Same(retained[0], consumer.Service.Sessions.Single(static session => session.ConnectionNumber == 1));
            Assert.Same(retained[1], consumer.Service.Sessions.Single(static session => session.ConnectionNumber == 2));
            Assert.All(consumer.Service.Sessions, static session => Assert.Equal(ArticleWorkConsumerState.Running, session.State));
            Assert.Equal(4, transport.ConnectAttempts.Count);
        }

        /// <summary>
        /// Consumer reconcile exceptions are logged without breaking registry operation.
        /// </summary>

        [Fact]

        public async Task Unexpected_consumer_reconcile_failure_is_logged_and_the_registry_stays_usable()
        {
            await AssertReconcileFailureIsObserved(new NotSupportedException("reconcile boom"), "reconcile boom");
        }

        /// <summary>
        /// Canceled consumer reconcile is logged and does not propagate to callers.
        /// </summary>

        [Fact]

        public async Task Cancelled_consumer_reconcile_is_logged_and_does_not_escape()
        {
            await AssertReconcileFailureIsObserved(
                new OperationCanceledException("reconcile canceled"),
                "reconcile canceled");
        }


        /// <summary>
        /// Applies a one-session snapshot, acquires a lease, and checks that <paramref name="failure"/> is logged while one session remains.
        /// </summary>
        /// <param name="failure">Exception the scripted consumer returns from reconcile.</param>
        /// <param name="expectedText">Text that must appear in the registry log.</param>
        private static async Task AssertReconcileFailureIsObserved(Exception failure, string expectedText)
        {
            var logger = new CollectingLogger<NntpProviderRegistry>();
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(transport, count: 2);
            var capacity = new BackboneUsableCapacityState();
            var consumers = new ScriptedConsumers { ReconcileException = failure };
            await using var registry = CreateRegistry(catalog, transport, capacity, consumers, logger);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 1)], CancellationToken.None);
            Assert.True(registry.TryGetPool("Giganews", out var pool));
            consumers.IsRunning = true;
            await using (var lease = await pool.AcquireAsync(CancellationToken.None))
            {
                lease.Retire();
            }

            await WaitUntilAsync(() =>
                logger.Messages.Any(message =>
                    message.Contains(expectedText, StringComparison.Ordinal)
                    && message.Contains("consumers", StringComparison.Ordinal))
                && pool.ActiveSessionCount == 1);
            Assert.True(consumers.ReconcileCalls >= 1);
        }


        /// <summary>
        /// Starts a fake RabbitMQ connection and an article-work consumer bound to the supplied catalog and capacity.
        /// </summary>
        /// <param name="catalog">Provider catalog the consumer reconciles against.</param>
        /// <param name="capacity">Usable-capacity signal the registry publishes and the consumer reads.</param>
        private static async Task<ConsumerHarness> StartConsumerAsync(
            IBackFillerProviderCatalog catalog,
            IBackboneUsableCapacityProvider capacity)
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var service = new ArticleWorkConsumerService(
                connections,
                RabbitMqServiceTests.CreateFastRuntime(),
                new DeferredArticleWorkHandler(),
                new RecordingArticleWorkResponsePublisher(),
                NullLogger<ArticleWorkConsumerService>.Instance,
                catalog,
                capacity);
            await service.StartAsync(CancellationToken.None);
            return new ConsumerHarness(service, connections);
        }

        /// <summary>
        /// Disposes consumer service and RabbitMQ connections together after a test.
        /// </summary>

        private sealed class ConsumerHarness(
            ArticleWorkConsumerService service,
            RabbitMqService connections) : IAsyncDisposable
        {
            /// <summary>Started consumer service the test inspects and the harness disposes first.</summary>
            public ArticleWorkConsumerService Service { get; } = service;

            /// <summary>Disposes the consumer, then the fake broker connection this harness started.</summary>
            public async ValueTask DisposeAsync()
            {
                await Service.DisposeAsync();
                await connections.DisposeAsync();
            }
        }

        /// <summary>
        /// Constructs NntpProviderRegistry wired to catalog, transport, capacity, and optional consumer reconciliation.
        /// </summary>

        private static NntpProviderRegistry CreateRegistry(
            ProviderConfigurationCatalog catalog,
            ScriptedNntpTransportFactory transport,
            BackboneUsableCapacityState capacity,
            IArticleWorkConsumerReconciliation? consumers = null,
            ILogger<NntpProviderRegistry>? logger = null)
        {
            return new NntpProviderRegistry(
                catalog,
                transport,
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                TimeSpan.FromSeconds(2),
                logger ?? NullLogger<NntpProviderRegistry>.Instance,
                capacity,
                consumers);
        }

        /// <summary>
        /// Test double for IArticleWorkConsumerReconciliation that can throw on ReconcileAsync.
        /// </summary>

        private sealed class ScriptedConsumers : IArticleWorkConsumerReconciliation
        {
            /// <summary>When false, the registry treats consumers as stopped and does not call reconcile.</summary>
            public bool IsRunning { get; set; }

            /// <summary>Exception returned from the next reconcile. Null completes successfully.</summary>
            public Exception? ReconcileException { get; init; }

            /// <summary>Number of reconcile calls the registry has made.</summary>
            public int ReconcileCalls { get; private set; }

            /// <summary>Counts the call, then completes or returns <see cref="ReconcileException"/>.</summary>
            /// <param name="cancellationToken">Ignored. Cancellation is simulated by <see cref="ReconcileException"/>.</param>
            public Task ReconcileAsync(CancellationToken cancellationToken)
            {
                ReconcileCalls++;
                return ReconcileException is null
                    ? Task.CompletedTask
                    : Task.FromException(ReconcileException);
            }


            /// <summary>Accepts retirement without changing consumer state. These tests observe reconcile, not retirement.</summary>
            /// <param name="backbone">Backbone the registry is retiring.</param>
            /// <param name="retainConnectionCount">Connections the registry asked to keep.</param>
            /// <param name="cancellationToken">Ignored.</param>
            public Task RetireCapacityAsync(
                string backbone,
                int retainConnectionCount,
                CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }

        /// <summary>
        /// Builds a BackFillerProviderDefinition with the requested MaxSessions for lifecycle tests.
        /// </summary>

        private static BackFillerProviderDefinition CreateProvider(string backbone, int maxSessions) =>
            new(backbone, "news.example.test", 119, false, "nntp-user", "p", 0, maxSessions);


        /// <summary>Polls every 10 milliseconds until <paramref name="condition"/> is true, or cancels after five seconds.</summary>
        /// <param name="condition">Condition the test is waiting to observe.</param>
        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(10, timeout.Token);
            }
        }
    }
}
