using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// Article Work delivery pipeline integration: NNTP retrieval, retention, RabbitMQ settlement, and response publication.
    /// </summary>

    public sealed class NntpArticleWorkIntegrationTests
    {
        /// <summary>
        /// Scripted NNTP wire response for a valid multiline article used across success-path tests.
        /// </summary>

        private static readonly string ValidArticleResponse =
            ArticleWorkTestArticles.ArticleResponse(ArticleWorkTestArticles.Valid());

        /// <summary>
        /// Successful retrieval retains bytes and settles with requeue without ACK when no response publisher confirms.
        /// </summary>

        [Fact]
        public async Task Retrieved_article_reaches_success_without_ack()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(_ => ValidArticleResponse);
            factory.Enqueue(server);
            var (handler, publisher, pipeline, retention) = CreatePipeline(factory);
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, handler.LastKind);
            Assert.Equal(ArticleRetentionKind.Retained, handler.LastRetentionKind);
            var claimed = retention.TryClaimForDelivery(Encoding.ASCII.GetBytes(ArticleWorkTestDeliveries.CanonicalMessageId));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            Assert.Contains("body"u8, claimed.Claim!.ArticleBytes.Span);
            claimed.Claim.Dispose();
            Assert.Equal("backfiller.test", handler.LastFqdn);
            Assert.Equal(1190, handler.LastListenerPort);
            Assert.Empty(publisher.Published);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        }

        /// <summary>
        /// Confirmed publish path ACKs the delivery and emits a Success JSON response with listener metadata.
        /// </summary>

        [Fact]
        public async Task Retrieved_article_publishes_confirmed_success_and_acks()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(_ => ValidArticleResponse);
            factory.Enqueue(server);
            var rabbitFactory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(rabbitFactory);
            await connections.StartAsync(CancellationToken.None);
            rabbitFactory.LastConnection!.DefaultPublishConfirmBehavior = FakePublishConfirmBehavior.Confirm;
            var publisher = new ArticleWorkResponsePublisher(
                connections,
                RabbitMqServiceTests.CreateFastRuntime(),
                NullLogger<ArticleWorkResponsePublisher>.Instance);
            await publisher.StartAsync(CancellationToken.None);
            var (handler, _, _, retention) = CreatePipeline(factory);
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Equal("backfiller.test", handler.LastFqdn);
            Assert.Equal(1190, handler.LastListenerPort);
            var publication = Assert.Single(
                Assert.IsType<FakeBackFillerRabbitMqPublishChannel>(publisher.Channel).Publications);
            using var document = JsonDocument.Parse(publication.Body);
            Assert.Equal("Success", document.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(handler.LastFqdn, document.RootElement.GetProperty("fqdn").GetString());
            Assert.Equal(handler.LastListenerPort, document.RootElement.GetProperty("listenerPort").GetInt32());
            Assert.False(document.RootElement.TryGetProperty("uri", out _));
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalRequestId, document.RootElement.GetProperty("requestId").GetString());
            Assert.True(Assert.Single(channel.Settlements).Acknowledge);
            Assert.True(retention.RetainedPayloadBytes > 0);
            Assert.Equal(1, retention.RetainedCount);
            await publisher.DisposeAsync();
            await connections.DisposeAsync();
        }

        /// <summary>
        /// 430 from provider maps to ArticleNotFound, publishes terminal outcome, and ACKs without requeue.
        /// </summary>

        [Fact]
        public async Task ArticleNotFound_maps_to_ack_without_requeue()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static _ => "430 No such article\r\n");
            factory.Enqueue(server);
            var (_, publisher, pipeline, _) = CreatePipeline(factory);
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ArticleNotFound, outcome);
            var intent = Assert.Single(publisher.Published);
            Assert.Equal(ArticleWorkOutcome.ArticleNotFound, intent.Outcome);
            var settlement = Assert.Single(channel.Settlements);
            Assert.True(settlement.Acknowledge);
            Assert.False(settlement.Requeue);
        }

        /// <summary>
        /// Connect/auth failures requeue without publishing a terminal Article Work response.
        /// </summary>

        [Fact]
        public async Task Provider_and_auth_failures_requeue_without_terminal_response()
        {
            var down = new ScriptedNntpTransportFactory { ConnectException = new IOException("down") };
            var auth = new ScriptedNntpTransportFactory();
            var authServer = new ScriptedNntpServer();
            authServer.Respond(static _ => "481 rejected\r\n");
            auth.Enqueue(authServer);

            var downPipeline = CreatePipeline(down);
            var authPipeline = CreatePipeline(auth, user: "u", password: "p");
            var downChannel = new FakeBackFillerRabbitMqChannel(1);
            var authChannel = new FakeBackFillerRabbitMqChannel(1);

            var downOutcome = await downPipeline.Pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                downChannel,
                static () => true,
                CancellationToken.None);
            var authOutcome = await authPipeline.Pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                authChannel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ProviderFailure, downOutcome);
            Assert.Equal(ArticleWorkOutcome.ProviderFailure, authOutcome);
            Assert.Empty(downPipeline.Publisher.Published);
            Assert.Empty(authPipeline.Publisher.Published);
            Assert.True(Assert.Single(downChannel.Settlements).Requeue);
            Assert.True(Assert.Single(authChannel.Settlements).Requeue);
        }

        /// <summary>
        /// Canceled processing requeues and does not classify as ArticleNotFound.
        /// </summary>

        [Fact]
        public async Task Cancellation_requeues_and_is_not_not_found()
        {
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(new ScriptedNntpServer());
            var (_, publisher, pipeline, _) = CreatePipeline(factory);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                cts.Token);

            Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
            Assert.Empty(publisher.Published);
            Assert.True(Assert.Single(channel.Settlements).Requeue);
        }

        /// <summary>
        /// When generation is stale after retrieval, outcome is Success but no RabbitMQ settlement is recorded.
        /// </summary>

        [Fact]
        public async Task Stale_generation_skips_settlement_after_retrieval()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(_ => ValidArticleResponse);
            factory.Enqueue(server);
            var (handler, publisher, pipeline, _) = CreatePipeline(factory);
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => false,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, handler.LastKind);
            Assert.Empty(publisher.Published);
            Assert.Empty(channel.Settlements);
        }

        /// <summary>
        /// Retention rejection requeues without ACK and is distinct from ArticleNotFound.
        /// </summary>

        [Fact]
        public async Task Retention_capacity_failure_is_not_article_not_found_and_does_not_ack()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(_ => ValidArticleResponse);
            factory.Enqueue(server);
            var catalog = new StaticBackFillerProviderCatalog(
            [
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
            ]);
            var registry = new NntpProviderRegistry(
                catalog,
                factory,
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                TimeSpan.FromSeconds(2),
                NullLogger<NntpProviderRegistry>.Instance);
            var handler = new ProviderArticleWorkHandler(
                new NntpArticleRetriever(registry),
                CreateRetention(maxBytes: 1));
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.RetentionRejected, outcome);
            Assert.Equal(ArticleRetentionKind.PayloadExceedsCapacity, handler.LastRetentionKind);
            Assert.NotEqual(ArticleWorkOutcome.ArticleNotFound, outcome);
            Assert.Empty(publisher.Published);
            Assert.False(Assert.Single(channel.Settlements).Acknowledge);
            Assert.True(Assert.Single(channel.Settlements).Requeue);
        }

        /// <summary>
        /// Unknown backbone name yields ProviderFailure and requeue.
        /// </summary>

        [Fact]
        public async Task Missing_provider_is_provider_failure()
        {
            var factory = new ScriptedNntpTransportFactory();
            var handler = new ProviderArticleWorkHandler(
                new NntpArticleRetriever(
                    new NntpProviderRegistry(
                        new StaticBackFillerProviderCatalog(),
                        factory,
                        NntpSessionOptions.Default,
                        TimeSpan.FromSeconds(2),
                        NullLogger<NntpProviderRegistry>.Instance)),
                CreateRetention(maxBytes: 1024));
            var publisher = new RecordingArticleWorkResponsePublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);
            var channel = new FakeBackFillerRabbitMqChannel(1);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ProviderFailure, outcome);
            Assert.True(Assert.Single(channel.Settlements).Requeue);
        }

        /// <summary>Wires Giganews on loopback with scripted transport, retention, handler, publisher, and a 1024-byte delivery pipeline.</summary>
        /// <param name="factory">Supplies NNTP sessions and scripted server responses.</param>
        /// <param name="user">Optional AUTHINFO user bound into the provider catalog.</param>
        /// <param name="password">Optional AUTHINFO password bound into the provider catalog.</param>
        /// <returns>Connected components sharing one handler and retention authority instance.</returns>
        private static (ProviderArticleWorkHandler Handler, RecordingArticleWorkResponsePublisher Publisher, ArticleWorkDeliveryPipeline Pipeline, ArticleRetentionAuthority Retention)
            CreatePipeline(ScriptedNntpTransportFactory factory, string? user = null, string? password = null)
        {
            var catalog = new StaticBackFillerProviderCatalog(
            [
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, user, password, 0, 1),
            ]);
            var registry = new NntpProviderRegistry(
                catalog,
                factory,
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                TimeSpan.FromSeconds(2),
                NullLogger<NntpProviderRegistry>.Instance);
            var retention = CreateRetention(maxBytes: 1024 * 1024);
            var handler = new ProviderArticleWorkHandler(
                new NntpArticleRetriever(registry),
                retention);
            var publisher = new RecordingArticleWorkResponsePublisher();
            return (handler, publisher, new ArticleWorkDeliveryPipeline(handler, publisher, 1024), retention);
        }

        /// <summary>
        /// Creates ArticleRetentionAuthority with the requested byte capacity for integration tests.
        /// </summary>

        private static ArticleRetentionAuthority CreateRetention(int maxBytes) =>
            new(
                new VectorNNTPD.BackFillerService.Configuration.BackFillerArticleRetentionRuntimeOptions(
                    maxBytes,
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromSeconds(1),
                    MaxOpenableRequestIdsPerArticle: 16),
                "backfiller.test",
                1190,
                TimeProvider.System,
                NullLogger.Instance);
    }
}
