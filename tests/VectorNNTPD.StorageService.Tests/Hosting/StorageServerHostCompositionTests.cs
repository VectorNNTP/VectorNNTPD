using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Acme;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Hosting;
using VectorNNTPD.StorageService.Hosting.Systemd;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Listener;
using VectorNNTPD.StorageService.Logging;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.StorageService.NntpDb;
using VectorNNTPD.StorageService.Storage;
using VectorNNTPD.StorageService.Storage.ArticleIndex;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolShards;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.StorageService.Tests.Hosting
{

    public sealed class StorageServerHostCompositionTests
    {
        [Fact]
        public void AddStorageServerHosting_registers_expected_service_graph()
        {
            using var host = CreateHost(replaceAcme: false, replaceRabbitMq: true);

            var hosted = host.Services.GetServices<IHostedService>()
                .Where(static service => service.GetType().Assembly == typeof(StorageServerServiceCollectionExtensions).Assembly)
                .ToArray();
            Assert.Equal(3, hosted.Length);
            Assert.IsType<SystemdLifecycleNotifier>(hosted[0]);
            Assert.IsType<SystemdWatchdogService>(hosted[1]);
            Assert.IsType<StorageServerHostedService>(hosted[2]);

            Assert.NotNull(host.Services.GetRequiredService<IApplicationHealth>());
            Assert.NotNull(host.Services.GetRequiredService<ISystemdNotifyBridge>());
            Assert.NotNull(host.Services.GetRequiredService<ISystemdRuntime>());
            Assert.NotNull(host.Services.GetRequiredService<ApplicationLifecycle>());
            Assert.NotNull(host.Services.GetRequiredService<ApplicationServiceManager>());
            Assert.NotNull(host.Services.GetRequiredService<StorageServerHostShutdown>());
            var runtimeSource = host.Services.GetRequiredService<StorageServerRuntimeOptionsSource>();
            Assert.False(runtimeSource.IsPublished);
            Assert.NotNull(host.Services.GetRequiredService<IRabbitMqService>());
            Assert.Same(
                host.Services.GetRequiredService<RabbitMqService>(),
                host.Services.GetRequiredService<IRabbitMqService>());

            var application = host.Services.GetServices<IApplicationService>().ToArray();
            Assert.Equal(12, application.Length);
            Assert.IsType<NntpDbService>(application[0]);
            Assert.IsType<StorageServerSharedConfigurationService>(application[1]);
            Assert.IsType<CloudflareDnsReconciliationApplicationService>(application[2]);
            Assert.IsType<RabbitMqService>(application[3]);
            Assert.IsType<AcmeCertificateApplicationService>(application[4]);
            Assert.IsType<SegmentPoolValidationService>(application[5]);
            Assert.IsType<SpoolShardInitializationService>(application[6]);
            Assert.IsType<ArticleIndexService>(application[7]);
            Assert.IsType<BlockDeviceMonitorService>(application[8]);
            Assert.Same(host.Services.GetRequiredService<AcceptedArticleWorker>(), application[9]);
            Assert.Same(host.Services.GetRequiredService<StorageNntpListenerService>(), application[10]);
            Assert.IsType<StorageServerAdvertisementPublisherService>(application[11]);
            Assert.Equal(1, application.Count(static service => service is AcceptedArticleWorker));
            Assert.IsType<StorageNntpListenerService>(host.Services.GetRequiredService<StorageNntpListenerService>());
            Assert.IsType<NoSpoolCapacityReader>(host.Services.GetRequiredService<IStorageCapacityReader>());
            Assert.IsType<AcceptedArticleQueue>(host.Services.GetRequiredService<IAcceptedArticleQueue>());
            Assert.NotNull(host.Services.GetRequiredService<StorageServerAdvertisementPublisherService>());
            Assert.DoesNotContain(
                typeof(StorageServerServiceCollectionExtensions).Assembly.GetTypes(),
                static type => type.Name.Contains("Journal", StringComparison.Ordinal)
                    || type.Name.Contains("SegmentStore", StringComparison.Ordinal)
                    || type.Name.Contains("SegmentWriter", StringComparison.Ordinal)
                    || type.Name.Contains("SegmentFile", StringComparison.Ordinal));
        }

        [Fact]
        public void AddRabbitMqInfrastructure_does_not_connect_during_registration()
        {
            var factory = new FakeStorageServerRabbitMqConnectionFactory();
            var builder = Host.CreateApplicationBuilder([]);
            var pairs = StorageServerTestOptions.CreateValidConfigurationPairs();
            pairs["StorageServer:BindPortTls"] = GetFreePort().ToString();
            pairs["StorageServer:BindAddress:0"] = "*";
            builder.Configuration.AddInMemoryCollection(pairs);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<ICloudflareDnsReconciler>(new NoOpCloudflareDnsReconciler());
            builder.ConfigureStorageServerLogging();
            builder.ConfigureStorageServerPlatformHosting();
            builder.AddStorageServerHosting();
            builder.Services.AddSingleton<IRabbitMqConnectionFactory>(factory);

            using var host = builder.Build();

            Assert.NotNull(host.Services.GetRequiredService<IRabbitMqService>());
            Assert.NotNull(host.Services.GetRequiredService<RabbitMqService>());
            Assert.Equal(0, factory.ConnectCount);
        }

        [Fact]
        public async Task Runtime_options_snapshot_is_singleton_with_cache_fqdn()
        {
            using var host = CreateHost();
            await host.StartAsync();
            try
            {
                var first = host.Services.GetRequiredService<StorageServerRuntimeOptions>();
                var second = host.Services.GetRequiredService<StorageServerRuntimeOptions>();
                Assert.Same(first, second);
                Assert.Equal("cache01.usenet.ninja", first.Fqdn);
                var shared = host.Services.GetRequiredService<IOptions<StorageServerOptions>>().Value;
                Assert.Equal("usenet.ninja", shared.DnsSuffix);
                Assert.Equal("https://acme-staging-v02.api.letsencrypt.org/directory", shared.AcmeDirectoryUrl);
                Assert.Equal(21, shared.AcmeRenewalThresholdDays);
                Assert.Equal("0123456789abcdef0123456789abcdef", shared.CloudFlareZoneId);
                Assert.Equal(0, first.BindPort);
                Assert.Equal(
                    first.GracefulShutdownTimeout,
                    host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
            }
            finally
            {
                await host.StopAsync();
            }
        }

        [Fact]
        public async Task Pool_validation_failure_stops_the_host_before_the_listener_and_advertisement()
        {
            using var host = CreateHost(permitPools: false);
            var error = await Record.ExceptionAsync(() => host.StartAsync());
            Assert.NotNull(error);
            Assert.Contains("does not exist", error.ToString(), StringComparison.Ordinal);

            var lifecycle = host.Services.GetRequiredService<ApplicationLifecycle>();
            Assert.NotEqual(ApplicationState.Running, lifecycle.State);
            Assert.Equal(ApplicationState.Stopped, lifecycle.State);

            var listener = host.Services.GetRequiredService<StorageNntpListenerService>();
            Assert.Equal(StorageNntpListenerState.Created, listener.State);
            Assert.Equal(0, host.Services.GetRequiredService<StorageServerAdvertisementPublisherService>().PublishedCount);

            var application = host.Services.GetServices<IApplicationService>().ToArray();
            Assert.Null(application.OfType<AcceptedArticleWorker>().Single().Execution);
            Assert.Null(application.OfType<SpoolShardInitializationService>().Single().Execution);
            Assert.Null(application.OfType<BlockDeviceMonitorService>().Single().Execution);
        }

        [Fact]
        public async Task Shard_initialization_failure_stops_the_host_before_the_listener_and_advertisement()
        {
            using var host = CreateHost(failShards: true);
            var error = await Record.ExceptionAsync(() => host.StartAsync());
            Assert.NotNull(error);
            Assert.Contains("shard create denied", error.ToString(), StringComparison.Ordinal);

            var lifecycle = host.Services.GetRequiredService<ApplicationLifecycle>();
            Assert.Equal(ApplicationState.Stopped, lifecycle.State);
            Assert.Equal(StorageNntpListenerState.Created, host.Services.GetRequiredService<StorageNntpListenerService>().State);
            Assert.Equal(0, host.Services.GetRequiredService<StorageServerAdvertisementPublisherService>().PublishedCount);
            var application = host.Services.GetServices<IApplicationService>().ToArray();
            Assert.Null(application.OfType<AcceptedArticleWorker>().Single().Execution);
            Assert.Null(application.OfType<BlockDeviceMonitorService>().Single().Execution);
        }

        [Fact]
        public async Task Host_starts_application_services_in_order_when_acme_and_rabbitmq_are_stubbed()
        {
            using var host = CreateHost(replaceAcme: true, replaceRabbitMq: true);
            await host.StartAsync();
            try
            {
                Assert.Equal(
                    ApplicationState.Running,
                    host.Services.GetRequiredService<ApplicationLifecycle>().State);
                var application = host.Services.GetServices<IApplicationService>().ToArray();
                Assert.IsType<NntpDbService>(application[0]);
                Assert.IsType<StorageServerSharedConfigurationService>(application[1]);
                Assert.IsType<CloudflareDnsReconciliationApplicationService>(application[2]);
                Assert.False(host.Services.GetRequiredService<IDnsPublicationLease>().PublishDuringStart);
                Assert.IsType<RabbitMqService>(application[3]);
                Assert.IsType<ImmediateAcmeReadyApplicationService>(application[4]);
                Assert.IsType<SegmentPoolValidationService>(application[5]);
                Assert.IsType<SpoolShardInitializationService>(application[6]);
                Assert.IsType<ArticleIndexService>(application[7]);
                Assert.IsType<BlockDeviceMonitorService>(application[8]);
                Assert.IsType<AcceptedArticleWorker>(application[9]);
                var listener = host.Services.GetRequiredService<StorageNntpListenerService>();
                Assert.Same(listener, application[10]);
                Assert.IsType<StorageServerAdvertisementPublisherService>(application[11]);
                Assert.Equal(StorageNntpListenerState.Running, listener.State);
                Assert.True(host.Services.GetRequiredService<IArticleIndex>().IsReady);
                Assert.True(host.Services.GetRequiredService<IRabbitMqService>().IsReady);
                Assert.NotNull(host.Services.GetRequiredService<IAcceptedArticleQueue>());
            }
            finally
            {
                await host.StopAsync();
            }

            Assert.Equal(
                ApplicationState.Stopped,
                host.Services.GetRequiredService<ApplicationLifecycle>().State);
            var queue = host.Services.GetRequiredService<IAcceptedArticleQueue>();
            Assert.False(queue.TryEnqueue(new AcceptedArticle(default, 1, 1, new byte[] { 1 })));
        }

        [Fact]
        public void Common_assembly_contains_shared_cache_fleet_contract_not_nntpd_topology_types()
        {
            var common = typeof(RabbitMqService).Assembly;
            Assert.Null(common.GetType("VectorNNTP.NNTPD.RabbitMq.OverviewDbTopology"));
            Assert.Null(common.GetType("VectorNNTP.NNTPD.RabbitMq.BackfillArticleRetrievalTopology"));
            Assert.Null(common.GetType("VectorNNTPD.Common.Messaging.RabbitMq.OverviewDbTopology"));
            Assert.NotNull(common.GetType("VectorNNTPD.Common.Messaging.Cache.CacheFleetTopology"));
            Assert.DoesNotContain(
                common.GetManifestResourceNames(),
                static name => name.Contains("backfiller.storage", StringComparison.OrdinalIgnoreCase));

            var cacheFleet = common.GetType("VectorNNTPD.Common.Messaging.Cache.CacheFleetTopology");
            Assert.NotNull(cacheFleet);
            Assert.Equal(
                "cache.requests",
                cacheFleet.GetField(
                    "RequestsExchangeName",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    ?.GetRawConstantValue());
            Assert.Equal(
                "cache.broadcast",
                cacheFleet.GetField(
                    "BroadcastExchangeName",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    ?.GetRawConstantValue());

            // Scan remaining const strings for the excluded topology name. Some literal
            // metadata entries throw NotSupportedException from GetRawConstantValue on
            // certain runtimes; skip those rather than assuming every IsLiteral field
            // has a retrievable constant blob.
            var topologyNames = common.GetTypes()
                .Where(static type => type.Namespace?.StartsWith(
                    "Coverlet.Core.Instrumentation",
                    StringComparison.Ordinal) != true)
                .SelectMany(static type => type.GetFields(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Instance))
                .Where(static field => field.FieldType == typeof(string) && field.IsLiteral)
                .Select(static field =>
                {
                    try
                    {
                        return field.GetRawConstantValue() as string;
                    }
                    catch (NotSupportedException)
                    {
                        return null;
                    }
                })
                .Where(static value => !string.IsNullOrEmpty(value))
                .ToArray();
            Assert.DoesNotContain(topologyNames, static value => value == "backfiller.storage");
        }

        [Fact]
        public void StorageServer_assembly_does_not_reference_NNTPD()
        {
            var referenced = typeof(StorageServerServiceCollectionExtensions).Assembly
                .GetReferencedAssemblies()
                .Select(static name => name.Name);

            Assert.DoesNotContain("VectorNNTP.NNTPD", referenced);
        }

        private static IHost CreateHost(bool replaceAcme = true, bool replaceRabbitMq = true, bool permitPools = true, bool failShards = false)
        {
            var builder = Host.CreateApplicationBuilder([]);
            var pairs = StorageServerTestOptions.CreateValidConfigurationPairs();
            var tlsPort = GetFreePort();
            pairs["StorageServer:BindPortTls"] = tlsPort.ToString();
            pairs["StorageServer:BindAddress:0"] = "*";
            builder.Configuration.AddInMemoryCollection(pairs);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<ICloudflareDnsReconciler>(new NoOpCloudflareDnsReconciler());
            builder.ConfigureStorageServerLogging();
            builder.ConfigureStorageServerPlatformHosting();
            builder.AddStorageServerHosting();
            if (failShards)
            {
                SegmentPoolTestSupport.UseFailingShardDirectories(builder.Services);
            }
            else
            {
                SegmentPoolTestSupport.UseMemoryShardDirectories(builder.Services);
            }

            if (permitPools)
            {
                SegmentPoolTestSupport.UsePermissiveInspector(builder.Services);
            }
            else
            {
                SegmentPoolTestSupport.UseFailingInspector(builder.Services);
            }
            builder.Services.AddSingleton<INntpDbConnectionFactory, FakeNntpDbConnectionFactory>();
            if (replaceRabbitMq)
            {
                builder.Services.AddSingleton<IRabbitMqConnectionFactory, FakeStorageServerRabbitMqConnectionFactory>();
            }

            if (replaceAcme)
            {
                ReplaceAcmeApplicationServiceWithImmediateReady(builder.Services);
            }

            return builder.Build();
        }

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

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
