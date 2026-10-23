using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Hosting;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.StorageService.Acme;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Hosting.Systemd;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Listener;
using VectorNNTPD.StorageService.NntpDb;
using VectorNNTPD.StorageService.Storage;
using VectorNNTPD.StorageService.Storage.ArticleIndex;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;
using VectorNNTPD.StorageService.Storage.SpoolShards;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.StorageService.Hosting
{

    /// <summary>
    /// Extension methods for registering VectorNNTPD.StorageService hosting services.
    /// </summary>
    public static class StorageServerServiceCollectionExtensions
    {
        /// <summary>
        /// Adds StorageServer host services: configuration, validation, ACME/Cloudflare, RabbitMQ connectivity, and lifecycle.
        /// </summary>
        /// <remarks>
        /// Application service start order:
        /// NntpDB → nntpsharedconfig → Cloudflare DNS → RabbitMQ (connectivity only) → ACME → segment-pool validation → spool shard initialization → article index → block-device monitor → accepted-article worker → NNTP feed listener → AdvertisementPublisher.
        /// Shutdown is that order reversed, so advertisement stops before the listener, and the listener completes the queue before the worker stops.
        /// Topology, queues, and consumers are not registered here. NntpDB does not migrate schema.
        /// </remarks>
        public static HostApplicationBuilder AddStorageServerHosting(this HostApplicationBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Services.TryAddSingleton<ILocalIpAddressAssignee, NetworkInterfaceLocalIpAddressAssignee>();
            builder.Services.TryAddSingleton(TimeProvider.System);

            builder.Services
                .AddOptions<StorageServerOptions>()
                .BindConfiguration(StorageServerOptions.SectionName)
                .PostConfigure(static options =>
                {
                    if (options.BindAddress is null || options.BindAddress.Length == 0)
                    {
                        options.BindAddress = ["*"];
                    }
                    else
                    {
                        for (var i = 0; i < options.BindAddress.Length; i++)
                        {
                            options.BindAddress[i] = options.BindAddress[i]?.Trim() ?? string.Empty;
                        }
                    }
                })
                .PostConfigure<IConfiguration>(static (options, configuration) =>
                    SegmentPoolsOptions.ReplaceConfiguredDirectories(options, configuration))
                .ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<StorageServerOptions>, StorageServerOptionsValidator>();

            builder.Services
                .AddOptions<RabbitMqOptions>()
                .BindConfiguration(RabbitMqOptions.SectionName)
                .PostConfigure(static options =>
                {
                    options.Management ??= new RabbitMqManagementOptions();
                    options.Username = null;
                    options.Password = null;
                })
                .ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<RabbitMqOptions>, RabbitMqOptionsValidator>();
            builder.Services.AddNntpDbOptions();

            builder.Services
                .AddOptions<AcmeCloudflareOptions>()
                .Configure<IOptions<StorageServerOptions>, IConfiguration>(
                    static (acme, storage, configuration) =>
                    {
                        StorageServerAcmeCloudflareOptionsAdapter.Apply(acme, storage.Value, configuration);
                    })
                .ValidateOnStart()
                .PostConfigure(static acme =>
                {
                    AcmeCloudflareOptionsValidator.NormalizeBindAddresses(acme);
                    acme.AcmeStateDir = ApplicationLocalPath.ResolveApplicationLocalPath(
                        acme.AcmeStateDir,
                        AppContext.BaseDirectory);
                    acme.AcmeDirectoryUrl = string.Empty;
                    acme.AcmeRenewalThresholdDays = 0;
                    acme.CloudFlareZoneId = string.Empty;
                    acme.DnsSuffix = string.Empty;
                    acme.Fqdn = string.Empty;
                    acme.AcmeEmail = string.Empty;
                    acme.AcmeCertificatePassword = string.Empty;
                    acme.CloudFlareApiKey = string.Empty;
                });
            builder.Services.AddSingleton<IValidateOptions<AcmeCloudflareOptions>>(static provider =>
                new AcmeCloudflareOptionsValidator(
                    provider.GetRequiredService<ILocalIpAddressAssignee>(),
                    validateSharedDatabaseFields: false,
                    validateSharedCredentials: false));
            builder.Services.AddSingleton<IValidateOptions<AcmeCloudflareOptions>, TlsOnlyAcmeCloudflareOptionsValidator>();
            builder.Services.AddSingleton<IDnsPublicationLease, ListenerBoundDnsPublicationLease>();
            builder.Services.AddAcmeCloudflareInfrastructure();

            builder.Services.TryAddSingleton<StorageServerRuntimeOptionsSource>();
            builder.Services.AddSingleton(static provider =>
                provider.GetRequiredService<StorageServerRuntimeOptionsSource>().Current);

            builder.Services.AddOptions<HostOptions>()
                .PostConfigure<IOptions<StorageServerOptions>>(static (host, options) =>
                {
                    host.ShutdownTimeout = options.Value.GracefulShutdownTimeout;
                });

            builder.Services.TryAddSingleton(TimeProvider.System);
            builder.Services.TryAddSingleton<IAcceptedArticleQueue>(static provider =>
                new AcceptedArticleQueue(
                    provider.GetRequiredService<IOptions<StorageServerOptions>>().Value.SpoolPersistence.MaxQueuedBytes,
                    provider.GetRequiredService<TimeProvider>()));
            builder.Services.TryAddSingleton<SpoolPressure>();
            builder.Services.TryAddSingleton(static provider => new SpoolHealth(provider.GetRequiredService<TimeProvider>()));
            builder.Services.TryAddSingleton<SpoolWriteGate>();
            builder.Services.TryAddSingleton<SpoolArticleIdentityGate>();
            builder.Services.TryAddSingleton<SpoolPersistenceCounters>();
            builder.Services.TryAddSingleton<ISpoolFreeSpaceProbe, FilesystemSpoolFreeSpaceProbe>();
            builder.Services.TryAddSingleton<ISpoolArticleFiles, FileSpoolArticleFiles>();
            builder.Services.TryAddSingleton<SpoolArticlePublisher>();
            builder.Services.TryAddSingleton<IAcceptedArticleProcessor, SpoolArticlePersistenceProcessor>();
            builder.Services.TryAddSingleton<AcceptedArticleWorker>();
            builder.Services.TryAddSingleton<IRabbitMqConnectionNameProvider>(static sp =>
                new DelegateRabbitMqConnectionNameProvider(() =>
                    RabbitMqRuntimeOptions.GetDefaultConnectionName(
                        ApplicationJsonConfiguration.EntryAssemblyName,
                        sp.GetRequiredService<IOptions<StorageServerOptions>>().Value.Fqdn)));
            builder.Services.AddRabbitMqInfrastructure();

            builder.Services.TryAddSingleton<IStorageCapacityReader, NoSpoolCapacityReader>();
            builder.Services.TryAddSingleton<StorageServerAdvertisementPublisherService>();
            builder.Services.TryAddSingleton<SegmentPoolCatalog>();
            builder.Services.TryAddSingleton<ISegmentPoolWriteProbe, FileSegmentPoolWriteProbe>();
            builder.Services.TryAddSingleton<ILinuxSysfs, LinuxSysfsReader>();
            builder.Services.TryAddSingleton<ISegmentPoolInspector, PlatformSegmentPoolInspector>();
            builder.Services.TryAddSingleton<IBlockDeviceCounterSource, PlatformBlockDeviceCounterSource>();
            builder.Services.TryAddSingleton<ISpoolShardDirectorySystem, FileSpoolShardDirectorySystem>();
            builder.Services.TryAddSingleton<SegmentPoolValidationService>();
            builder.Services.TryAddSingleton<SpoolShardInitializationService>();
            builder.Services.TryAddSingleton<ArticleIndexService>();
            builder.Services.TryAddSingleton<IArticleIndex>(static provider => provider.GetRequiredService<ArticleIndexService>());
            builder.Services.TryAddSingleton<BlockDeviceMonitorService>();

            builder.Services.TryAddSingleton<INntpDbConnectionFactory, MySqlNntpDbConnectionFactory>();
            builder.Services.TryAddSingleton<NntpDbService>();
            builder.Services.TryAddSingleton<StorageServerSharedConfigurationService>();
            builder.Services.TryAddSingleton<INntpSharedConfigurationCatalogue>(static provider =>
                provider.GetRequiredService<StorageServerSharedConfigurationService>());

            // Startup order: NntpDB → nntpsharedconfig → Cloudflare → RabbitMQ → ACME →
            // segment-pool validation → spool shard initialization → article index → block-device monitor →
            // accepted-article worker → Listener → AdvertisementPublisher.
            // Validation and shard initialization throw before the worker, listener, or advertisement publisher start.
            // ApplicationServiceManager stops in reverse, so advertisement stops first and the worker stops after the listener completes the queue.
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, NntpDbService>(static provider =>
                    provider.GetRequiredService<NntpDbService>()));
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, StorageServerSharedConfigurationService>(static provider =>
                    provider.GetRequiredService<StorageServerSharedConfigurationService>()));
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, CloudflareDnsReconciliationApplicationService>());
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, RabbitMqService>(static sp =>
                    sp.GetRequiredService<RabbitMqService>()));
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, AcmeCertificateApplicationService>());
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, SegmentPoolValidationService>(static provider =>
                    provider.GetRequiredService<SegmentPoolValidationService>()));
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, SpoolShardInitializationService>(static provider =>
                    provider.GetRequiredService<SpoolShardInitializationService>()));
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, ArticleIndexService>(static provider =>
                    provider.GetRequiredService<ArticleIndexService>()));
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, BlockDeviceMonitorService>(static provider =>
                    provider.GetRequiredService<BlockDeviceMonitorService>()));
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, AcceptedArticleWorker>(static provider =>
                    provider.GetRequiredService<AcceptedArticleWorker>()));
            builder.Services.AddSingleton(static provider =>
            {
                var listener = provider.GetRequiredService<IOptions<StorageServerOptions>>().Value.Listener;
                var dns = provider.GetRequiredService<CloudflareDnsReconciliationService>();
                var index = provider.GetService<IArticleIndex>();
                Func<Common.Articles.ArticleId, bool>? articlePresent = index is null
                    ? null
                    : id =>
                    {
                        try
                        {
                            return index.IsReady && index.Contains(id);
                        }
                        catch (InvalidOperationException)
                        {
                            return false;
                        }
                    };
                return new StorageNntpListenerService(
                    provider.GetRequiredService<StorageServerRuntimeOptionsSource>(),
                    provider.GetRequiredService<Common.Networking.Certificates.ITlsCertificateContextProvider>(),
                    provider.GetRequiredService<IAcmeCertificateReadiness>(),
                    provider.GetRequiredService<ILogger<StorageNntpListenerService>>(),
                    provider.GetRequiredService<IAcceptedArticleQueue>(),
                    new StoreAssemblyAdmission(listener.MaxActiveConnections),
                    articlePresent: articlePresent,
                    publishAfterBind: dns.PublishAfterListenerBoundAsync);
            });
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, StorageNntpListenerService>(static provider =>
                    provider.GetRequiredService<StorageNntpListenerService>()));
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, StorageServerAdvertisementPublisherService>(static sp =>
                    sp.GetRequiredService<StorageServerAdvertisementPublisherService>()));

            builder.Services.AddSingleton<IApplicationLifecycleOptions>(static sp =>
                sp.GetRequiredService<IOptions<StorageServerOptions>>().Value);
            builder.Services.AddSingleton<ApplicationServiceManager>();
            builder.Services.AddSingleton<ApplicationLifecycle>(static sp => new ApplicationLifecycle(
                sp.GetRequiredService<ApplicationServiceManager>(),
                sp.GetRequiredService<IApplicationLifecycleOptions>(),
                sp.GetRequiredService<ILogger<ApplicationLifecycle>>()));
            builder.Services.AddSingleton<StorageServerHostShutdown>();
            builder.Services.AddSingleton<IApplicationHealth, ApplicationHealth>();

            builder.Services.TryAddSingleton<ISystemdNotifyBridge, SystemdNotifyBridge>();
            builder.Services.TryAddSingleton<ISystemdRuntime, SystemdRuntime>();

            builder.Services.AddSingleton<SystemdLifecycleNotifier>();
            builder.Services.AddHostedService(static provider =>
                provider.GetRequiredService<SystemdLifecycleNotifier>());
            builder.Services.AddHostedService<SystemdWatchdogService>();
            builder.Services.AddHostedService<StorageServerHostedService>();

            return builder;
        }

        /// <summary>
        /// Registers Windows Service and systemd lifetime support and keeps console formatting on Serilog.
        /// </summary>
        public static HostApplicationBuilder ConfigureStorageServerPlatformHosting(this HostApplicationBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Configuration.AddVectorEnvironmentVariables();

            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = ApplicationJsonConfiguration.EntryAssemblyName;
            });

            builder.Services.AddSystemd();
            RemoveObsoleteMicrosoftConsoleFormatterConfiguration(builder.Services);

            builder.Services.Configure<HostOptions>(options =>
            {
                options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;

                var shutdown = builder.Configuration.GetSection(StorageServerOptions.SectionName)
                    .GetValue<TimeSpan?>("GracefulShutdownTimeout");
                if (shutdown is { } timeout)
                {
                    options.ShutdownTimeout = timeout;
                }
            });

            return builder;
        }

        /// <summary>
        /// Removes Microsoft console formatter options that <c>AddSystemd()</c> may register.
        /// </summary>
        public static void RemoveObsoleteMicrosoftConsoleFormatterConfiguration(IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (IsMicrosoftConsoleLoggerOptionsConfiguration(services[i]))
                {
                    services.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Returns whether a service descriptor configures Microsoft console logger options.
        /// </summary>
        public static bool IsMicrosoftConsoleLoggerOptionsConfiguration(ServiceDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            var serviceType = descriptor.ServiceType;
            if (!serviceType.IsGenericType)
            {
                return false;
            }

            var definition = serviceType.GetGenericTypeDefinition();
            if (definition != typeof(IConfigureOptions<>)
                && definition != typeof(IPostConfigureOptions<>)
                && definition != typeof(IValidateOptions<>))
            {
                return false;
            }

            return serviceType.GenericTypeArguments[0] == typeof(ConsoleLoggerOptions);
        }
    }
}
