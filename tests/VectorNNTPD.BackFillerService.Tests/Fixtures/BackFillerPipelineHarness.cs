using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.Retention;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Articles.Parsing;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Fixtures
{
    /// <summary>
    /// Composes the real production Article Work path with fakes only at external boundaries.
    /// </summary>
    internal sealed class BackFillerPipelineHarness : IAsyncDisposable
    {
        /// <summary>True after account polling has been started for this harness instance.</summary>
        private bool _accountsStarted;

        /// <summary>Captures composed pipeline dependencies for Article Work integration tests.</summary>
        /// <param name="accounts">Fake account source feeding refresh.</param>
        /// <param name="catalog">Mutable provider catalog.</param>
        /// <param name="accountService">Production account refresh service.</param>
        /// <param name="registry">NNTP provider registry.</param>
        /// <param name="nntp">Scripted transport factory for provider connects.</param>
        /// <param name="retention">Retention authority under test.</param>
        /// <param name="time">Manual clock driving retention TTL.</param>
        /// <param name="handler">Article work handler on the real pipeline path.</param>
        /// <param name="rabbitFactory">Fake RabbitMQ factory for connections.</param>
        /// <param name="connections">Live RabbitMQ service over fakes.</param>
        /// <param name="publisher">Response publisher on the pipeline path.</param>
        /// <param name="pipeline">Delivery pipeline coordinating handler and settlement.</param>
        private BackFillerPipelineHarness(
            FakeProviderAccountSource accounts,
            ProviderConfigurationCatalog catalog,
            ProviderAccountConfigurationService accountService,
            NntpProviderRegistry registry,
            ScriptedNntpTransportFactory nntp,
            ArticleRetentionAuthority retention,
            ManualTimeProvider time,
            ProviderArticleWorkHandler handler,
            FakeBackFillerRabbitMqConnectionFactory rabbitFactory,
            RabbitMqService connections,
            ArticleWorkResponsePublisher publisher,
            ArticleWorkDeliveryPipeline pipeline)
        {
            Accounts = accounts;
            Catalog = catalog;
            AccountService = accountService;
            Registry = registry;
            Nntp = nntp;
            Retention = retention;
            Time = time;
            Handler = handler;
            RabbitFactory = rabbitFactory;
            Connections = connections;
            Publisher = publisher;
            Pipeline = pipeline;
        }

        /// <summary>Fake MySQL account rows consumed by <see cref="AccountService"/> refresh.</summary>

        public FakeProviderAccountSource Accounts { get; }

        /// <summary>Mutable provider catalog updated when account refresh succeeds.</summary>

        public ProviderConfigurationCatalog Catalog { get; }

        /// <summary>Production account refresh service; started once by <see cref="LoadProviderAsync"/>.</summary>

        public ProviderAccountConfigurationService AccountService { get; }

        /// <summary>NNTP provider registry receiving definitions from account refresh.</summary>

        public NntpProviderRegistry Registry { get; }

        /// <summary>Scripted transport factory; enqueue servers before processing work.</summary>

        public ScriptedNntpTransportFactory Nntp { get; }

        /// <summary>Retention authority on the real pipeline path; TTL driven by <see cref="Time"/>.</summary>

        public ArticleRetentionAuthority Retention { get; }

        /// <summary>Manual clock advancing retention sweeps and timeouts in tests.</summary>

        public ManualTimeProvider Time { get; }

        /// <summary>Production article-work handler wired to registry and retention.</summary>

        public ProviderArticleWorkHandler Handler { get; }

        /// <summary>Fake RabbitMQ connection factory backing <see cref="Connections"/>.</summary>

        public FakeBackFillerRabbitMqConnectionFactory RabbitFactory { get; }

        /// <summary>Started RabbitMQ service over the fake factory; disposed with the harness.</summary>

        public RabbitMqService Connections { get; }

        /// <summary>Production response publisher; exposes fake publish channel for assertions.</summary>

        public ArticleWorkResponsePublisher Publisher { get; }

        /// <summary>Delivery pipeline coordinating handler execution and response settlement.</summary>

        public ArticleWorkDeliveryPipeline Pipeline { get; }

        /// <summary>Fake publish channel behind the publisher; fails the test if a real channel is installed.</summary>

        internal FakeBackFillerRabbitMqPublishChannel PublishChannel =>
            Assert.IsType<FakeBackFillerRabbitMqPublishChannel>(Publisher.Channel);

        /// <summary>
        /// Builds a started harness with production pipeline types and boundary fakes only.
        /// Does not enqueue NNTP servers or RabbitMQ deliveries.
        /// </summary>
        /// <param name="confirm">Fake publish confirm behavior for response publication.</param>
        /// <param name="retentionTtl">Retention TTL; defaults to one minute.</param>
        /// <param name="maxArticleBytes">Optional NNTP max article size override.</param>
        /// <param name="maxRetainedPayloadBytes">Optional retention byte cap override.</param>
        /// <returns>Harness with RabbitMQ started and a default Giganews provider loaded.</returns>

        internal static async Task<BackFillerPipelineHarness> StartAsync(
            FakePublishConfirmBehavior confirm = FakePublishConfirmBehavior.Confirm,
            TimeSpan? retentionTtl = null,
            int? maxArticleBytes = null,
            int? maxRetainedPayloadBytes = null)
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var retention = ArticleRetentionAuthorityTests.Create(
                time,
                maxBytes: maxRetainedPayloadBytes ?? (1024 * 1024),
                ttl: retentionTtl ?? TimeSpan.FromSeconds(60));
            var accounts = new FakeProviderAccountSource();
            var catalog = new ProviderConfigurationCatalog();
            var nntp = new ScriptedNntpTransportFactory();
            var options = BackFillerTestOptions.CreateValid();
            options.BackFillerAccountRefreshIntervalSeconds = 3600;
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb()) with
            {
                RabbitMq = RabbitMqServiceTests.CreateFastRuntime().RabbitMq,
            };

            var sessionOptions = NntpSessionOptions.Default with
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                CommandTimeout = TimeSpan.FromSeconds(2),
                ReceiveTimeout = TimeSpan.FromSeconds(2),
            };
            if (maxArticleBytes is { } configuredMax)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(configuredMax, 1);
                sessionOptions = sessionOptions with { MaxArticleBytes = configuredMax };
            }

            var registry = new NntpProviderRegistry(
                catalog,
                nntp,
                sessionOptions,
                TimeSpan.FromSeconds(2),
                NullLogger<NntpProviderRegistry>.Instance);
            var accountService = new ProviderAccountConfigurationService(
                accounts,
                new FixedSharedConfigurationSource(),
                registry,
                runtime,
                Options.Create(new BackFillerOptions()),
                Options.Create(new AcmeCloudflareOptions()),
                NullLogger<ProviderAccountConfigurationService>.Instance);
            var handler = new ProviderArticleWorkHandler(
                new NntpArticleRetriever(registry),
                retention,
                new NntpArticleParser(runtime.Fqdn));

            var rabbitFactory = new FakeBackFillerRabbitMqConnectionFactory
            {
                DefaultPublishConfirmBehavior = confirm,
            };
            var connections = RabbitMqServiceTests.CreateService(rabbitFactory);
            await connections.StartAsync(CancellationToken.None).ConfigureAwait(false);
            var publisher = new ArticleWorkResponsePublisher(
                connections,
                runtime,
                NullLogger<ArticleWorkResponsePublisher>.Instance);
            await publisher.StartAsync(CancellationToken.None).ConfigureAwait(false);
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);

            var harness = new BackFillerPipelineHarness(
                accounts,
                catalog,
                accountService,
                registry,
                nntp,
                retention,
                time,
                handler,
                rabbitFactory,
                connections,
                publisher,
                pipeline);
            await harness.LoadProviderAsync().ConfigureAwait(false);
            return harness;
        }

        /// <summary>
        /// Seeds account rows and runs the first refresh or a subsequent explicit refresh.
        /// Does not start RabbitMQ or mutate scripted NNTP servers.
        /// </summary>
        /// <param name="backbone">Provider backbone name on the fake account row.</param>
        /// <param name="hostname">NNTP hostname on the fake account row.</param>
        /// <param name="port">NNTP port on the fake account row.</param>
        /// <param name="useSsl">SSL flag on the fake account row.</param>
        /// <param name="username">AUTHINFO username on the fake account row.</param>
        /// <param name="password">AUTHINFO password on the fake account row.</param>
        /// <param name="maxConnections">Session pool size on the fake account row.</param>

        internal async Task LoadProviderAsync(
            string backbone = "Giganews",
            string hostname = "news.example.test",
            int port = 563,
            string useSsl = "y",
            string username = "nntp-user",
            string password = ProviderAccountTestRows.SecretPassword,
            int maxConnections = 4)
        {
            Accounts.Rows =
            [
                ProviderAccountTestRows.Create(
                    backbone: backbone,
                    hostname: hostname,
                    port: port,
                    useSsl: useSsl,
                    username: username,
                    password: password,
                    maxConnections: maxConnections),
            ];
            if (!_accountsStarted)
            {
                await AccountService.StartAsync(CancellationToken.None).ConfigureAwait(false);
                _accountsStarted = true;
                return;
            }

            Assert.True(await AccountService.RefreshOnceAsync(CancellationToken.None).ConfigureAwait(false));
        }

        /// <summary>
        /// Creates a scripted article server, enqueues it on the transport factory, and returns it for assertions.
        /// Does not run the delivery pipeline.
        /// </summary>
        /// <param name="payload">Raw article bytes returned after AUTHINFO succeeds.</param>
        /// <param name="blockArticle">Optional gate delaying article body delivery on the server.</param>
        /// <returns>The enqueued scripted server instance.</returns>

        internal ScriptedNntpServer EnqueueArticle(byte[] payload, TaskCompletionSource? blockArticle = null)
        {
            var server = CreateArticleServer(payload, blockArticle);
            Nntp.Enqueue(server);
            return server;
        }

        /// <summary>
        /// Builds a scripted server that accepts AUTHINFO and streams one multiline article body.
        /// Does not register the server with any transport factory.
        /// </summary>
        /// <param name="payload">Article body bytes placed between 220 follows and the terminating dot.</param>
        /// <param name="blockArticle">Optional gate delaying article body delivery on the server.</param>

        private static ScriptedNntpServer CreateArticleServer(byte[] payload, TaskCompletionSource? blockArticle = null)
        {
            var server = new ScriptedNntpServer
            {
                BlockArticle = blockArticle,
                ArticleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var wire = "220 follows\r\n" + Encoding.ASCII.GetString(payload) + "\r\n.\r\n";
            server.Respond(command =>
                command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase)
                    ? "281 authentication accepted\r\n"
                    : wire);
            return server;
        }

        /// <summary>
        /// Parses the delivery and runs the production pipeline for backbone Giganews.
        /// Does not acknowledge or nack the RabbitMQ delivery itself.
        /// </summary>
        /// <param name="delivery">Manual-ack delivery payload and headers.</param>
        /// <param name="channel">Fake channel passed through for settlement.</param>
        /// <param name="channelStillCurrent">Optional gate; defaults to always true.</param>
        /// <param name="cancellationToken">Cancellation forwarded to the pipeline.</param>

        private Task<ArticleWorkOutcome> ProcessAsync(
            RabbitMqManualAckDelivery delivery,
            FakeBackFillerRabbitMqChannel channel,
            Func<bool>? channelStillCurrent = null,
            CancellationToken cancellationToken = default)
        {
            var parsed = ArticleWorkRequestParser.Parse(delivery, "Giganews", Pipeline.MaxPayloadBytes);
            return Pipeline.ProcessAsync(
                delivery,
                "Giganews",
                channel,
                channelStillCurrent ?? (static () => true),
                cancellationToken,
                parsed);
        }

        /// <summary>
        /// Runs <see cref="ProcessAsync"/> with the shared canonical Article Work delivery fixture.
        /// </summary>
        /// <param name="channel">Fake channel passed through for settlement.</param>
        /// <param name="deliveryTag">AMQP delivery tag on the synthetic delivery.</param>
        /// <param name="generation">Channel generation on the synthetic delivery.</param>
        /// <param name="channelStillCurrent">Optional gate; defaults to always true.</param>
        /// <param name="cancellationToken">Cancellation forwarded to the pipeline.</param>

        internal Task<ArticleWorkOutcome> ProcessCanonicalAsync(
            FakeBackFillerRabbitMqChannel channel,
            ulong deliveryTag = 7,
            long generation = 1,
            Func<bool>? channelStillCurrent = null,
            CancellationToken cancellationToken = default) =>
            ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(deliveryTag, generation),
                channel,
                channelStillCurrent,
                cancellationToken);

        /// <summary>Disposes publisher, connections, account service, registry, and retention in order.</summary>

        public async ValueTask DisposeAsync()
        {
            await Publisher.DisposeAsync().ConfigureAwait(false);
            await Connections.DisposeAsync().ConfigureAwait(false);
            await AccountService.DisposeAsync().ConfigureAwait(false);
            await Registry.DisposeAsync().ConfigureAwait(false);
            await Retention.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// IBackFillerSharedConfigurationSource returning a fixed configuration root for harness tests.
    /// </summary>

    internal sealed class FixedSharedConfigurationSource : IBackFillerSharedConfigurationSource
    {
        /// <summary>Returns a fixed shared-configuration snapshot; ignores configuration files and env.</summary>
        /// <param name="cancellationToken">Honored via <see cref="CancellationToken.ThrowIfCancellationRequested"/>.</param>

        public ValueTask<NntpSharedConfiguration> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new NntpSharedConfiguration(
                1024,
                "news.usenet.ninja",
                null,
                "https://acme-v02.api.letsencrypt.org/directory",
                14,
                "0123456789abcdef0123456789abcdef",
                "usenet.ninja"));
        }
    }
}
