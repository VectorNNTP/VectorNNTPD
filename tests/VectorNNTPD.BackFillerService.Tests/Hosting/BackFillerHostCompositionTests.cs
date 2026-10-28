using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Diagnostics;
using VectorNNTPD.Common.Core;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Hosting.Systemd;
using VectorNNTPD.BackFillerService.Listener;
using VectorNNTPD.BackFillerService.Logging;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.NntpDb;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Configuration;
using RabbitMQ.Client;
using System.Net.Sockets;
using System.Net;

namespace VectorNNTPD.BackFillerService.Tests.Hosting
{
    /// <summary>
    /// Regresses DI registration order, singleton runtime options, RabbitMQ lazy connect, and absence of legacy NNTPD or placeholder hosted services.
    /// </summary>
    public sealed class BackFillerHostCompositionTests
    {
        /// <summary>
        /// Hosted service order (thirteen BackFiller-hosted services ending with
        /// <see cref="SilentConsumerMonitorHostedService"/> after <see cref="ArticleWorkConsumerService"/>),
        /// application-service triple (DNS, ACME, listener), and key singleton aliases match the current composition contract.
        /// </summary>
        [Fact]
        public void AddBackFillerHosting_registers_rabbitmq_then_article_work_consumers()
        {
            using var host = CreateHost(replaceAcme: false);

            Assert.Same(TimeProvider.System, host.Services.GetRequiredService<TimeProvider>());
            var hosted = host.Services.GetServices<IHostedService>()
                .Where(static service => service.GetType().Assembly == typeof(BackFillerServiceCollectionExtensions).Assembly)
                .ToArray();
            Assert.Equal(13, hosted.Length);
            Assert.IsType<SystemdLifecycleNotifier>(hosted[0]);
            Assert.IsType<SystemdWatchdogService>(hosted[1]);
            Assert.IsType<NntpDbServiceHostedAdapter>(hosted[2]);
            Assert.Same(host.Services.GetRequiredService<ProviderAccountConfigurationService>(), hosted[3]);
            Assert.IsType<RabbitMqServiceHostedAdapter>(hosted[4]);
            Assert.Same(host.Services.GetRequiredService<NntpProviderRegistry>(), hosted[5]);
            Assert.IsType<BackFillerApplicationHostedService>(hosted[6]);
            Assert.False(typeof(BackFillerApplicationHostedService).IsAssignableTo(typeof(IDisposable)));
            Assert.IsType<VectorNNTPD.BackFillerService.Core.ApplicationServiceManager>(
                host.Services.GetRequiredService<VectorNNTPD.BackFillerService.Core.ApplicationServiceManager>());
            Assert.Same(host.Services.GetRequiredService<ArticleRetentionSweepService>(), hosted[7]);
            Assert.Same(host.Services.GetRequiredService<ArticleRetentionMetrics>(), hosted[8]);
            Assert.Same(host.Services.GetRequiredService<ArticleWorkStageProfilingHostedService>(), hosted[9]);
            Assert.Same(host.Services.GetRequiredService<ArticleWorkResponsePublisher>(), hosted[10]);
            Assert.Same(host.Services.GetRequiredService<ArticleWorkConsumerService>(), hosted[11]);
            Assert.IsType<SilentConsumerMonitorHostedService>(hosted[12]);
            Assert.Same(
                host.Services.GetRequiredService<BackFillerApplicationHealth>(),
                host.Services.GetRequiredService<IApplicationHealth>());
            Assert.IsType<AcmeCertificateWatchdog>(
                host.Services.GetRequiredService<IAcmeCertificateObservation>());
            Assert.NotNull(host.Services.GetRequiredService<ISystemdNotifyBridge>());
            Assert.NotNull(host.Services.GetRequiredService<ISystemdRuntime>());
            Assert.DoesNotContain(
                hosted,
                static service => service.GetType().Name == "AcmeCertificateHostedService");
            Assert.DoesNotContain(
                hosted,
                static service => service.GetType().Name == "CloudflareDnsReconciliationHostedService");
            var application = host.Services.GetServices<IApplicationService>().ToArray();
            Assert.Equal(3, application.Length);
            Assert.IsType<CloudflareDnsReconciliationApplicationService>(application[0]);
            Assert.Equal(
                typeof(CloudflareDnsReconciliationService).Assembly,
                application[0].GetType().Assembly);
            Assert.IsType<AcmeCertificateApplicationService>(application[1]);
            Assert.Same(host.Services.GetRequiredService<CacheListenerService>(), application[2]);
            Assert.All(
                application,
                static service => Assert.IsAssignableFrom<IApplicationService>(service));
            Assert.DoesNotContain(application, static service => service is NntpDbService);
            Assert.DoesNotContain(application, static service => service is RabbitMqService);
            var database = host.Services.GetRequiredService<NntpDbService>();
            Assert.Equal("VectorNNTPD.Common", database.GetType().Assembly.GetName().Name);
            Assert.Same(database, Assert.Single(host.Services.GetServices<NntpDbService>()));
            Assert.IsType<NntpDbSharedConfigurationSource>(
                host.Services.GetRequiredService<IBackFillerSharedConfigurationSource>());
            Assert.Same(
                host.Services.GetRequiredService<ProviderAccountConfigurationService>(),
                host.Services.GetRequiredService<INntpSharedConfigurationCatalogue>());
            Assert.DoesNotContain(hosted, static service => service is CacheListenerService);
            Assert.Same(
                host.Services.GetRequiredService<ArticleRetentionAuthority>(),
                host.Services.GetRequiredService<IArticleRetentionAuthority>());
            Assert.Same(
                host.Services.GetRequiredService<ProviderConfigurationCatalog>(),
                host.Services.GetRequiredService<IBackFillerProviderCatalog>());
            Assert.IsType<FakeProviderAccountSource>(host.Services.GetRequiredService<IProviderAccountSource>());
            Assert.IsType<ProviderArticleWorkHandler>(host.Services.GetRequiredService<IArticleWorkHandler>());
            Assert.Same(
                host.Services.GetRequiredService<ArticleWorkResponsePublisher>(),
                host.Services.GetRequiredService<IArticleWorkResponsePublisher>());
            var publisher = host.Services.GetRequiredService<IAcmeCertificatePublisher>();
            Assert.Same(host.Services.GetRequiredService<TlsCertificateContextProvider>(), publisher);
            Assert.Same(host.Services.GetRequiredService<ITlsCertificateContextProvider>(), publisher);
            Assert.NotNull(host.Services.GetRequiredService<CloudflareDnsReconciliationService>());
            var parser = host.Services.GetRequiredService<NntpArticleParser>();
            Assert.Equal(typeof(NntpArticleParser), parser.GetType());
            Assert.Equal("VectorNNTPD.Common", parser.GetType().Assembly.GetName().Name);
            Assert.DoesNotContain(
                typeof(BackFillerServiceCollectionExtensions).Assembly.GetTypes(),
                static type => type.Name is "NntpArticleParser"
                    or "YEncArticleValidator"
                    or "NntpMessageIdValidation"
                    or "ArticleTypeClassifier"
                    or "ArticlePathCanonicalizer"
                    or "NewsDateParser");
        }

        /// <summary>
        /// Production DI defaults leave lab isolation and stage profiling off, keep the plain TCP transport,
        /// and wire the pipelined response publisher with a confirm bound matching RabbitMQ.Client 7.2.2's
        /// default outstanding-confirm rate limiter (128).
        /// </summary>
        [Fact]
        public void AddBackFillerHosting_production_defaults_isolate_lab_features_and_pipeline_publisher()
        {
            using var host = CreateHost();

            var lab = host.Services.GetRequiredService<IOptions<LabUpstreamIsolationOptions>>().Value;
            Assert.False(lab.Enabled);
            Assert.False(lab.RewritePublishedDestinations);

            var profiling = host.Services.GetRequiredService<IOptions<LabStageProfilingOptions>>().Value;
            Assert.False(profiling.Enabled);

            Assert.IsType<TcpNntpTransportFactory>(host.Services.GetRequiredService<INntpTransportFactory>());
            Assert.Same(
                host.Services.GetRequiredService<ArticleWorkResponsePublisher>(),
                host.Services.GetRequiredService<IArticleWorkResponsePublisher>());
            Assert.Equal(
                RabbitMqPublisherConfirmLimits.MaxOutstandingConfirmations,
                ArticleWorkResponsePublisher.MaxConcurrentConfirmedPublications);
            Assert.Equal(128, ArticleWorkResponsePublisher.MaxConcurrentConfirmedPublications);

            // Production CreatePublishChannelAsync must not use the two-arg CreateChannelOptions overload:
            // that leaves OutstandingPublisherConfirmationsRateLimiter null (unlimited). Explicit limiter required.
            var unboundedDefaults = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true);
            Assert.Null(unboundedDefaults.OutstandingPublisherConfirmationsRateLimiter);

            using var limiter = new ThrottlingRateLimiter(
                RabbitMqPublisherConfirmLimits.MaxOutstandingConfirmations);
            var productionOptions = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true,
                outstandingPublisherConfirmationsRateLimiter: limiter);
            Assert.True(productionOptions.PublisherConfirmationsEnabled);
            Assert.True(productionOptions.PublisherConfirmationTrackingEnabled);
            Assert.NotNull(productionOptions.OutstandingPublisherConfirmationsRateLimiter);
            Assert.Same(limiter, productionOptions.OutstandingPublisherConfirmationsRateLimiter);
        }

        /// <summary>
        /// The Cloudflare API client and ACME component factory resolve from Common.
        /// Listener A/AAAA publication stays withheld until bind, and BackFiller defines no DNS query client.
        /// </summary>
        [Fact]
        public void AddBackFillerHosting_resolves_common_dns_and_withholds_publication_until_bind()
        {
            using var host = CreateHost(replaceAcme: false);

            var apiClient = host.Services.GetRequiredService<ICloudflareDnsClient>();
            Assert.IsType<CloudflareDnsClient>(apiClient);
            Assert.Equal("VectorNNTPD.Common", apiClient.GetType().Assembly.GetName().Name);
            Assert.Same(apiClient, host.Services.GetRequiredService<ICloudflareDnsClient>());

            var factory = host.Services.GetRequiredService<AcmeComponentFactory>();
            Assert.Equal("VectorNNTPD.Common", factory.GetType().Assembly.GetName().Name);
            Assert.Same(factory, host.Services.GetRequiredService<AcmeComponentFactory>());

            var lease = host.Services.GetRequiredService<IDnsPublicationLease>();
            Assert.IsType<ListenerBoundDnsPublicationLease>(lease);
            Assert.False(lease.PublishDuringStart);
            var publication = host.Services.GetRequiredService<IListenerDnsPublication>();
            Assert.IsType<CloudflareDnsReconciliationService>(publication);
            Assert.NotSame(apiClient, publication);

            Assert.DoesNotContain(
                typeof(BackFillerServiceCollectionExtensions).Assembly.GetTypes(),
                static type => type.Namespace == "VectorNNTPD.BackFillerService.Dns"
                    || type.Name is "AuthoritativeTxtClient"
                        or "DnsQueryBuilder"
                        or "ZoneApexNameserverDiscovery"
                        or "DnsUdpExchange");
        }

        /// <summary><see cref="BackFillerRuntimeOptions"/> is a single snapshot shared by resolves; shutdown grace matches <see cref="HostOptions.ShutdownTimeout"/>.</summary>
        [Fact]
        public void AddBackFillerHosting_registers_a_single_runtime_options_snapshot()
        {
            using var host = CreateHost();

            var first = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            var second = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            Assert.Same(first, second);
            Assert.Equal("backfiller01.usenet.ninja", first.Fqdn);
            Assert.Equal("127.0.0.1", first.NntpDb.Server);
            Assert.Equal("nntp", first.NntpDb.Database);
            var nntpDb = host.Services.GetRequiredService<IOptions<NntpDbOptions>>().Value;
            Assert.Equal(first.NntpDb.ConnectionString, nntpDb.ConnectionString);
            Assert.Contains("Database=nntp", nntpDb.ConnectionString, StringComparison.Ordinal);
            Assert.Equal(TimeSpan.FromSeconds(60), first.AccountRefreshInterval);
            Assert.Equal(TimeSpan.FromSeconds(45), first.Shutdown.GracePeriod);
            Assert.Equal(
                first.Shutdown.GracePeriod,
                host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
        }

        /// <summary>After host start, RabbitMQ is ready with one connection generation; stop disposes consumer and publish channels exactly once.</summary>
        [Fact]
        public async Task Host_starts_rabbitmq_and_article_work_consumers_without_a_placeholder_background_service()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            using var host = CreateHost(factory);

            await host.StartAsync();
            try
            {
                Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
                var rabbit = host.Services.GetRequiredService<IRabbitMqService>();
                Assert.True(rabbit.IsReady);
                Assert.Equal(1, rabbit.ConnectionGeneration);
                var consumer = host.Services.GetRequiredService<ArticleWorkConsumerService>();
                Assert.Empty(consumer.Sessions);
                Assert.Empty(factory.LastConnection!.Channels);
                Assert.Single(factory.LastConnection.PublishChannels);
                Assert.Equal(1, factory.ConnectCount);
                Assert.Contains(
                    host.Services.GetServices<IHostedService>(),
                    static service => service is ArticleRetentionSweepService);
                Assert.DoesNotContain(
                    host.Services.GetServices<IHostedService>(),
                    static service => service.GetType().Name == "PlaceholderBackgroundService");
            }
            finally
            {
                await host.StopAsync();
            }

            Assert.False(host.Services.GetRequiredService<IRabbitMqService>().IsReady);
            Assert.All(factory.LastConnection!.Channels, static channel => Assert.Equal(1, channel.DisposeCount));
            Assert.All(factory.LastConnection.PublishChannels, static channel => Assert.Equal(1, channel.DisposeCount));
        }

        /// <summary>Building the host must not connect to RabbitMQ until hosted services start.</summary>
        [Fact]
        public void AddBackFillerHosting_registers_rabbitmq_without_connecting()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            using var host = CreateHost(factory);

            Assert.Equal(0, factory.ConnectCount);
            Assert.NotNull(host.Services.GetRequiredService<IRabbitMqService>());
            Assert.Same(
                host.Services.GetRequiredService<RabbitMqService>(),
                host.Services.GetRequiredService<IRabbitMqService>());
            Assert.Equal(0, factory.ConnectCount);
        }

        /// <summary>Physical memory comes from <see cref="OsPhysicalMemoryProvider"/> as a singleton.</summary>
        [Fact]
        public void AddBackFillerHosting_registers_os_physical_memory_provider()
        {
            var builder = Host.CreateApplicationBuilder([]);
            builder.AddBackFillerHosting();

            var descriptor = Assert.Single(
                builder.Services,
                static service => service.ServiceType == typeof(IPhysicalMemoryProvider));
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.Equal(typeof(OsPhysicalMemoryProvider), descriptor.ImplementationType);
        }

        /// <summary>Without test overrides, provider accounts load from MySQL via <see cref="MySqlProviderAccountSource"/>.</summary>
        [Fact]
        public void AddBackFillerHosting_defaults_to_the_mysql_account_source()
        {
            using var host = CreateHost(injectAccountSource: false);

            Assert.IsType<MySqlProviderAccountSource>(host.Services.GetRequiredService<IProviderAccountSource>());
            Assert.IsType<ProviderConfigurationCatalog>(host.Services.GetRequiredService<IBackFillerProviderCatalog>());
            Assert.IsType<NntpDbSharedConfigurationSource>(
                host.Services.GetRequiredService<IBackFillerSharedConfigurationSource>());
        }

        /// <summary>
        /// Common <see cref="NntpDbService"/> and <see cref="MySqlNntpDbConnectionFactory"/> are each registered once.
        /// Account queries and <c>nntpsharedconfig</c> reads are not hosted services of their own.
        /// </summary>
        [Fact]
        public void AddBackFillerHosting_registers_common_nntpdb_once()
        {
            var builder = Host.CreateApplicationBuilder([]);
            builder.AddBackFillerHosting();

            var database = Assert.Single(builder.Services, static descriptor => descriptor.ServiceType == typeof(NntpDbService));
            Assert.Equal(ServiceLifetime.Singleton, database.Lifetime);
            Assert.Equal(typeof(NntpDbService), database.ImplementationType);
            Assert.Equal("VectorNNTPD.Common", typeof(NntpDbService).Assembly.GetName().Name);
            var factory = Assert.Single(
                builder.Services,
                static descriptor => descriptor.ServiceType == typeof(INntpDbConnectionFactory));
            Assert.Equal(typeof(MySqlNntpDbConnectionFactory), factory.ImplementationType);
            Assert.Equal(ServiceLifetime.Singleton, factory.Lifetime);
            var shared = Assert.Single(
                builder.Services,
                static descriptor => descriptor.ServiceType == typeof(IBackFillerSharedConfigurationSource));
            Assert.Equal(typeof(NntpDbSharedConfigurationSource), shared.ImplementationType);
            Assert.Single(builder.Services, static descriptor => descriptor.ServiceType == typeof(IProviderAccountSource));
            Assert.Single(
                builder.Services,
                static descriptor => descriptor.ServiceType == typeof(INntpSharedConfigurationCatalogue));
            Assert.DoesNotContain(
                builder.Services,
                static descriptor => descriptor.ImplementationType?.Namespace == "VectorNNTPD.BackFillerService.NntpDb"
                    && descriptor.ImplementationType.Name != nameof(NntpDbServiceHostedAdapter));
        }

        /// <summary>
        /// The hosted adapter runs Common's startup check once. A second start does not open another connection.
        /// Stop makes further sessions fail, and a second stop does not throw.
        /// </summary>
        [Fact]
        public async Task Common_nntpdb_adapter_starts_and_stops_the_service_once()
        {
            using var host = CreateHost();
            var database = host.Services.GetRequiredService<NntpDbService>();
            var adapter = Assert.Single(
                host.Services.GetServices<IHostedService>().OfType<NntpDbServiceHostedAdapter>());

            await adapter.StartAsync(CancellationToken.None);
            Assert.Equal(1, database.StartupConnectAttempts);
            Assert.True(database.IsAccepting);

            await adapter.StartAsync(CancellationToken.None);
            Assert.Equal(1, database.StartupConnectAttempts);

            await adapter.StopAsync(CancellationToken.None);
            Assert.False(database.IsAccepting);
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await database.OpenAsync(CancellationToken.None));

            await adapter.StopAsync(CancellationToken.None);
            Assert.False(database.IsAccepting);
        }

        /// <summary>
        /// A rejected connection string fails the adapter immediately. Correcting the options lets the next start succeed.
        /// </summary>
        [Fact]
        public async Task Common_nntpdb_adapter_propagates_configuration_failure_then_starts()
        {
            var options = new NntpDbOptions { ConnectionString = " " };
            var database = new NntpDbService(
                new SelectOneConnectionFactory(),
                Options.Create(options),
                NullLogger<NntpDbService>.Instance);
            var adapter = new NntpDbServiceHostedAdapter(database);

            var failure = await Assert.ThrowsAsync<NntpDbConfigurationException>(
                () => adapter.StartAsync(CancellationToken.None));
            Assert.Contains("ConnectionStrings:NntpDB", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Password", failure.Message, StringComparison.Ordinal);
            Assert.False(database.HasStarted);
            Assert.False(database.IsAccepting);
            Assert.Equal(0, database.StartupConnectAttempts);

            options.ConnectionString = "Server=127.0.0.1;Database=nntp;User ID=n;Password=secret;";
            await adapter.StartAsync(CancellationToken.None);
            Assert.True(database.IsAccepting);
            Assert.Equal(1, database.StartupConnectAttempts);

            await adapter.StopAsync(CancellationToken.None);
            Assert.False(database.IsAccepting);
        }

        /// <summary>BackFiller assembly must not take a compile-time dependency on VectorNNTP.NNTPD.</summary>
        [Fact]
        public void BackFiller_assembly_does_not_reference_NNTPD()
        {
            var referenced = typeof(BackFillerServiceCollectionExtensions).Assembly
                .GetReferencedAssemblies()
                .Select(static name => name.Name);

            Assert.DoesNotContain("VectorNNTP.NNTPD", referenced);
        }

        /// <summary>Test host with valid in-memory configuration, platform hosting, logging, and optional fakes.</summary>
        /// <param name="factory">RabbitMQ factory spy; default creates a new fake.</param>
        /// <param name="injectAccountSource">When true, registers <see cref="FakeProviderAccountSource"/>.</param>
        /// <param name="replaceAcme">When true, swaps ACME for immediate-ready test service.</param>
        /// <returns>Built host; caller must dispose.</returns>
        private static IHost CreateHost(
            FakeBackFillerRabbitMqConnectionFactory? factory = null,
            bool injectAccountSource = true,
            bool replaceAcme = true)
        {
            var builder = Host.CreateApplicationBuilder([]);
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            var tlsPort = GetFreePort();
            pairs["BackFiller:BindPortTls"] = tlsPort.ToString();
            pairs["BackFiller:BindAddress:0"] = "*";
            pairs["BackFiller:Logging:File:LogDir"] = Directory.CreateTempSubdirectory("bf-host-logs-").FullName;
            builder.Configuration.AddInMemoryCollection(pairs);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<VectorNNTPD.Common.Cloudflare.ICloudflareDnsReconciler>(new NoOpCloudflareDnsReconciler());
            builder.Services.AddSingleton<IPhysicalMemoryProvider>(new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
            builder.Services.AddSingleton<IRabbitMqConnectionFactory>(
                factory ?? new FakeBackFillerRabbitMqConnectionFactory());
            if (injectAccountSource)
            {
                builder.Services.AddSingleton<IProviderAccountSource>(new FakeProviderAccountSource());
            }

            builder.ConfigureBackFillerLogging();
            builder.ConfigureBackFillerPlatformHosting();
            builder.AddBackFillerHosting();
            SuccessfulNntpDbConnectionFactory.Replace(builder.Services);
            if (replaceAcme)
            {
                ReplaceAcmeApplicationServiceWithImmediateReady(builder.Services);
            }

            return builder.Build();
        }

        /// <summary>Replaces <see cref="AcmeCertificateApplicationService"/> registration with <see cref="ImmediateAcmeReadyApplicationService"/>.</summary>
        /// <param name="services">Service collection after hosting registration.</param>
        private static void ReplaceAcmeApplicationServiceWithImmediateReady(IServiceCollection services)
        {
            for (var i = 0; i < services.Count; i++)
            {
                if (services[i].ServiceType == typeof(IApplicationService)
                    && services[i].ImplementationType == typeof(AcmeCertificateApplicationService))
                {
                    services[i] = ServiceDescriptor.Singleton<IApplicationService, ImmediateAcmeReadyApplicationService>();
                }
            }
        }

        /// <summary>Allocates an ephemeral loopback TCP port for TLS bind configuration.</summary>
        /// <returns>Port number that was bound transiently.</returns>
        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>Opens a session whose <c>SELECT 1</c> returns 1. SQL commands are not used.</summary>
        private sealed class SelectOneConnectionFactory : INntpDbConnectionFactory
        {
            /// <inheritdoc />
            public Task<INntpDbSession> OpenAsync(string connectionString, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<INntpDbSession>(new SelectOneSession());
            }

            /// <summary>Health-check session for the startup probe.</summary>
            private sealed class SelectOneSession : INntpDbSession
            {
                /// <inheritdoc />
                public ValueTask<int> SelectOneAsync(CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(1);
                }

                /// <summary>Not used by the startup probe.</summary>
                /// <returns>Never returns.</returns>
                public MySqlCommand CreateCommand() =>
                    throw new NotSupportedException("The startup probe does not create commands.");

                /// <inheritdoc />
                public ValueTask<MySqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new NotSupportedException("The startup probe does not begin a transaction.");
                }

                /// <inheritdoc />
                public ValueTask<MySqlTransaction> BeginTransactionAsync(
                    System.Data.IsolationLevel isolationLevel,
                    CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new NotSupportedException("The startup probe does not begin a transaction.");
                }

                /// <inheritdoc />
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
        }
    }
}
