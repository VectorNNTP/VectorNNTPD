using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Console;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.BackFillerService.Hosting.Systemd;
using VectorNNTPD.BackFillerService.Listener;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.NntpDb;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Articles.Parsing;

namespace VectorNNTPD.BackFillerService.Hosting
{
    /// <summary>
    /// Extension methods for registering VectorNNTPD.BackFillerService hosting services.
    /// </summary>
    internal static class BackFillerServiceCollectionExtensions
    {
        /// <summary>
        /// Adds BackFiller host services: configuration, validation, system time, and RabbitMQ connection ownership.
        /// </summary>
        /// <param name="builder">The host application builder.</param>
        /// <returns>The same <paramref name="builder"/> instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>
        /// Registers <see cref="NntpDbServiceHostedAdapter"/> and then
        /// <see cref="ProviderAccountConfigurationService"/> so <c>nntpsharedconfig</c>, including
        /// RabbitMQ, ACME, and Cloudflare credentials, is published before
        /// <see cref="RabbitMqServiceHostedAdapter"/> and before Cloudflare and ACME start.
        /// Startup fails if the initial broker connection cannot be established.
        /// Then <see cref="NntpProviderRegistry"/>,
        /// then <see cref="BackFillerApplicationHostedService"/> (starts
        /// <see cref="CloudflareDnsReconciliationApplicationService"/>, then
        /// <see cref="AcmeCertificateApplicationService"/>, then
        /// <see cref="CacheListenerService"/>), then
        /// <see cref="ArticleRetentionSweepService"/>, then
        /// <see cref="ArticleWorkResponsePublisher"/>,
        /// then <see cref="ArticleWorkConsumerService"/>. Article Work consume
        /// is reconciled from usable NNTP capacity. Before consumers start for a usable
        /// backbone, BackFiller declares that backbone's quorum <c>backfiller.*</c> topology.
        /// RabbitMQ is not registered into BackFiller <see cref="VectorNNTPD.BackFillerService.Core.ApplicationServiceManager"/>.
        /// </para>
        /// <para>
        /// Direct <c>AddSingleton</c>, <c>TryAddSingleton</c>, and <c>AddHostedService</c> registrations in this method are singletons.
        /// <c>TryAddSingleton</c> leaves an existing registration in place.
        /// <see cref="IHostedService"/> instances are registered in this order:
        /// <see cref="SystemdLifecycleNotifier"/>, <see cref="SystemdWatchdogService"/>,
        /// <see cref="NntpDbServiceHostedAdapter"/>, <see cref="ProviderAccountConfigurationService"/>,
        /// <see cref="RabbitMqServiceHostedAdapter"/>, <see cref="NntpProviderRegistry"/>,
        /// <see cref="BackFillerApplicationHostedService"/>,
        /// <see cref="ArticleRetentionSweepService"/>, <see cref="ArticleWorkResponsePublisher"/>,
        /// then <see cref="ArticleWorkConsumerService"/>.
        /// The notifier is first so it is constructed before later hosted services and can observe
        /// <see cref="IHostApplicationLifetime.ApplicationStarted"/> and
        /// <see cref="IHostApplicationLifetime.ApplicationStopping"/>.
        /// <see cref="HostOptions.ShutdownTimeout"/> is set from
        /// <see cref="BackFillerShutdownRuntimeOptions.GracePeriod"/>.
        /// Bound <see cref="RabbitMqOptions"/>, <see cref="BackFillerOptions"/>, and
        /// <see cref="AcmeCloudflareOptions"/> use <c>ValidateOnStart</c>.
        /// </para>
        /// </remarks>
        internal static HostApplicationBuilder AddBackFillerHosting(this HostApplicationBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Services.TryAddSingleton<ILocalIpAddressAssignee, NetworkInterfaceLocalIpAddressAssignee>();
            builder.Services.TryAddSingleton<IPhysicalMemoryProvider, OsPhysicalMemoryProvider>();
            builder.Services.TryAddSingleton(TimeProvider.System);

            builder.Services.TryAddSingleton<IBackFillerStartupJournal, BackFillerStartupJournal>();

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

            builder.Services
                .AddOptions<BackFillerOptions>()
                .BindConfiguration(BackFillerOptions.SectionName)
                .ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<BackFillerOptions>, BackFillerOptionsValidator>();

            builder.Services
                .AddOptions<AcmeCloudflareOptions>()
                .Configure<IOptions<BackFillerOptions>, IConfiguration>(
                    static (acme, backfiller, configuration) =>
                    {
                        BackFillerAcmeCloudflareOptionsAdapter.Apply(acme, backfiller.Value, configuration);
                    })
                // DataAnnotations on AcmeCloudflareOptions are enforced by the registered
                // IValidateOptions implementations (AcmeCloudflareOptionsValidator /
                // TlsOnlyAcmeCloudflareOptionsValidator). Do not call ValidateDataAnnotations():
                // that extension uses reflection and is incompatible with Native AOT.
                .ValidateOnStart()
                .PostConfigure<IBackFillerStartupJournal>(
                    static (acme, journal) =>
                {
                    AcmeCloudflareOptionsValidator.NormalizeBindAddresses(acme);
                    acme.AcmeStateDir = ApplicationLocalPath.ResolveApplicationLocalPath(
                        acme.AcmeStateDir,
                        AppContext.BaseDirectory);
                    acme.AcmeEmail = string.Empty;
                    acme.AcmeCertificatePassword = string.Empty;
                    acme.CloudFlareApiKey = string.Empty;
                    journal.Record(BackFillerStartupStages.Configuration);
                });
            builder.Services.AddSingleton<IValidateOptions<AcmeCloudflareOptions>>(static provider =>
                new AcmeCloudflareOptionsValidator(
                    provider.GetRequiredService<ILocalIpAddressAssignee>(),
                    validateSharedDatabaseFields: false,
                    validateSharedCredentials: false));
            builder.Services.AddSingleton<IValidateOptions<AcmeCloudflareOptions>, TlsOnlyAcmeCloudflareOptionsValidator>();
            builder.Services.AddSingleton<IDnsPublicationLease, ListenerBoundDnsPublicationLease>();
            builder.Services.AddSingleton<VectorNNTPD.Common.Acme.IAcmeCertificateObservation>(static provider =>
                new AcmeCertificateWatchdog(
                    provider.GetRequiredService<BackFillerApplicationHealth>(),
                    provider.GetRequiredService<ILogger<AcmeCertificateWatchdog>>()));
            VectorNNTPD.Common.Hosting.AcmeCloudflareServiceCollectionExtensions.AddAcmeCloudflareInfrastructure(
                builder.Services);
            VectorNNTPD.Common.Hosting.NntpDbServiceCollectionExtensions.AddNntpDbOptions(builder.Services);

            builder.Services.AddSingleton(static provider =>
            {
                var options = provider.GetRequiredService<IOptions<BackFillerOptions>>().Value;
                var nntpDb = provider.GetRequiredService<IOptions<NntpDbOptions>>().Value;
                var acme = provider.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
                var rabbitMq = provider.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
                return BackFillerRuntimeOptionsFactory.Create(
                    options,
                    nntpDb,
                    acme,
                    rabbitMq,
                    AppContext.BaseDirectory);
            });

            builder.Services.AddOptions<HostOptions>()
                .PostConfigure<BackFillerRuntimeOptions>(static (host, runtime) =>
                {
                    host.ShutdownTimeout = runtime.Shutdown.GracePeriod;
                });

            // Register systemd lifecycle notifier before other hosted services so it is
            // constructed early and observes ApplicationStarted/Stopping for READY/STOPPING.
            builder.Services.AddSingleton<BackFillerApplicationHealth>();
            builder.Services.AddSingleton<IApplicationHealth>(static provider =>
                provider.GetRequiredService<BackFillerApplicationHealth>());
            builder.Services.TryAddSingleton<ISystemdNotifyBridge, SystemdNotifyBridge>();
            builder.Services.TryAddSingleton<ISystemdRuntime, SystemdRuntime>();
            builder.Services.AddSingleton<SystemdLifecycleNotifier>();
            builder.Services.AddHostedService(static provider =>
                provider.GetRequiredService<SystemdLifecycleNotifier>());
            builder.Services.AddHostedService<SystemdWatchdogService>();

            builder.Services.TryAddSingleton<IRabbitMqConnectionNameProvider>(static sp =>
                new DelegateRabbitMqConnectionNameProvider(() =>
                    RabbitMqRuntimeOptions.GetDefaultConnectionName(
                        ApplicationJsonConfiguration.EntryAssemblyName,
                        sp.GetRequiredService<IOptions<BackFillerOptions>>().Value.Fqdn)));
            VectorNNTPD.Common.Hosting.RabbitMqServiceCollectionExtensions.AddRabbitMqInfrastructure(builder.Services);

            builder.Services.AddSingleton<INntpDbConnectionFactory, MySqlNntpDbConnectionFactory>();
            builder.Services.AddSingleton<NntpDbService>();
            builder.Services.AddSingleton<IHostedService>(static provider =>
                new NntpDbServiceHostedAdapter(provider.GetRequiredService<NntpDbService>()));
            builder.Services.TryAddSingleton<IProviderAccountSource>(static provider =>
                new MySqlProviderAccountSource(
                    provider.GetRequiredService<NntpDbService>(),
                    provider.GetRequiredService<BackFillerRuntimeOptions>().ServerId));
            builder.Services.TryAddSingleton<IBackFillerSharedConfigurationSource, NntpDbSharedConfigurationSource>();
            builder.Services.AddSingleton<ProviderConfigurationCatalog>();
            builder.Services.TryAddSingleton<IBackFillerProviderCatalog>(static provider =>
                provider.GetRequiredService<ProviderConfigurationCatalog>());
            builder.Services.AddSingleton(static provider => new ProviderAccountConfigurationService(
                provider.GetRequiredService<IProviderAccountSource>(),
                provider.GetRequiredService<IBackFillerSharedConfigurationSource>(),
                provider.GetRequiredService<NntpProviderRegistry>(),
                provider.GetRequiredService<BackFillerRuntimeOptions>(),
                provider.GetRequiredService<IOptions<BackFillerOptions>>(),
                provider.GetRequiredService<IOptions<AcmeCloudflareOptions>>(),
                provider.GetRequiredService<ILogger<ProviderAccountConfigurationService>>(),
                provider.GetRequiredService<IOptions<RabbitMqOptions>>()));
            builder.Services.AddSingleton<INntpSharedConfigurationCatalogue>(static provider =>
                provider.GetRequiredService<ProviderAccountConfigurationService>());
            builder.Services.TryAddSingleton<INntpTransportFactory, TcpNntpTransportFactory>();
            builder.Services.AddSingleton<BackboneUsableCapacityState>();
            builder.Services.AddSingleton<IBackboneUsableCapacityProvider>(static provider =>
                provider.GetRequiredService<BackboneUsableCapacityState>());
            builder.Services.AddSingleton<IBackboneUsableCapacityStateWriter>(static provider =>
                provider.GetRequiredService<BackboneUsableCapacityState>());
            builder.Services.AddSingleton(static provider =>
            {
                var runtime = provider.GetRequiredService<BackFillerRuntimeOptions>();
                return new ProviderSessionEstablishmentGate(runtime.Nntp.MaxConcurrentSessionEstablishments);
            });
            builder.Services.AddSingleton(static provider => new NntpProviderRegistry(
                provider.GetRequiredService<IBackFillerProviderCatalog>(),
                provider.GetRequiredService<INntpTransportFactory>(),
                provider.GetRequiredService<BackFillerRuntimeOptions>(),
                provider.GetRequiredService<ProviderSessionEstablishmentGate>(),
                provider.GetRequiredService<ILogger<NntpProviderRegistry>>(),
                provider.GetRequiredService<BackboneUsableCapacityState>(),
                provider));
            builder.Services.AddSingleton<IHostedService>(static provider =>
                provider.GetRequiredService<ProviderAccountConfigurationService>());
            builder.Services.AddSingleton<IHostedService>(static sp =>
                new RabbitMqServiceHostedAdapter(sp.GetRequiredService<RabbitMqService>()));
            builder.Services.AddSingleton<IHostedService>(static provider =>
                provider.GetRequiredService<NntpProviderRegistry>());
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, CloudflareDnsReconciliationApplicationService>());
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, AcmeCertificateApplicationService>());
            builder.Services.TryAddSingleton<INntpArticleRetriever, NntpArticleRetriever>();
            builder.Services.AddSingleton(static provider => new ArticleRetentionAuthority(
                provider.GetRequiredService<BackFillerRuntimeOptions>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<ArticleRetentionAuthority>>()));
            builder.Services.AddSingleton<IArticleRetentionAuthority>(static provider =>
                provider.GetRequiredService<ArticleRetentionAuthority>());
            builder.Services.AddSingleton(static provider => new ArticleRetentionSweepService(
                provider.GetRequiredService<IArticleRetentionAuthority>(),
                provider.GetRequiredService<ILogger<ArticleRetentionSweepService>>()));
            builder.Services.AddSingleton(static provider => new CacheListenerService(
                provider.GetRequiredService<BackFillerRuntimeOptions>(),
                provider.GetRequiredService<ITlsCertificateContextProvider>(),
                provider.GetRequiredService<IArticleRetentionAuthority>(),
                provider.GetRequiredService<IAcmeCertificateReadiness>(),
                provider.GetRequiredService<IBackFillerStartupJournal>(),
                provider.GetRequiredService<ILogger<CacheListenerService>>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<IListenerDnsPublication>()));
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, CacheListenerService>(static provider =>
                    provider.GetRequiredService<CacheListenerService>()));
            builder.Services.AddSingleton<VectorNNTPD.BackFillerService.Core.ApplicationServiceManager>();
            builder.Services.AddSingleton<IHostedService, BackFillerApplicationHostedService>();
            builder.Services.AddSingleton<IHostedService>(static provider =>
                provider.GetRequiredService<ArticleRetentionSweepService>());
            builder.Services.TryAddSingleton(static provider =>
                new NntpArticleParser(provider.GetRequiredService<BackFillerRuntimeOptions>().Fqdn));
            builder.Services.AddSingleton<IArticleWorkHandler>(static provider => new ProviderArticleWorkHandler(
                provider.GetRequiredService<INntpArticleRetriever>(),
                provider.GetRequiredService<IArticleRetentionAuthority>(),
                provider.GetRequiredService<NntpArticleParser>(),
                provider.GetRequiredService<INntpSharedConfigurationCatalogue>(),
                provider.GetRequiredService<BackFillerRuntimeOptions>().ServerId,
                provider.GetRequiredService<ILogger<ProviderArticleWorkHandler>>()));
            builder.Services.AddSingleton(static provider => new ArticleWorkResponsePublisher(
                provider.GetRequiredService<IRabbitMqService>(),
                provider.GetRequiredService<BackFillerRuntimeOptions>(),
                provider.GetRequiredService<ILogger<ArticleWorkResponsePublisher>>()));
            builder.Services.AddSingleton<IArticleWorkResponsePublisher>(static provider =>
                provider.GetRequiredService<ArticleWorkResponsePublisher>());
            builder.Services.AddSingleton<IHostedService>(static provider =>
                provider.GetRequiredService<ArticleWorkResponsePublisher>());
            builder.Services.AddSingleton(static provider => new ArticleWorkConsumerService(
                provider.GetRequiredService<IRabbitMqService>(),
                provider.GetRequiredService<BackFillerRuntimeOptions>(),
                provider.GetRequiredService<IArticleWorkHandler>(),
                provider.GetRequiredService<IArticleWorkResponsePublisher>(),
                provider.GetRequiredService<ILogger<ArticleWorkConsumerService>>(),
                provider.GetRequiredService<IBackFillerProviderCatalog>(),
                provider.GetRequiredService<IBackboneUsableCapacityProvider>()));
            builder.Services.AddSingleton<IArticleWorkConsumerReconciliation>(static provider =>
                provider.GetRequiredService<ArticleWorkConsumerService>());
            builder.Services.AddSingleton<IHostedService>(static provider =>
                provider.GetRequiredService<ArticleWorkConsumerService>());

            return builder;
        }

        /// <summary>
        /// Registers Windows Service and systemd lifetime support and keeps console formatting on Serilog.
        /// </summary>
        /// <param name="builder">The host application builder.</param>
        /// <returns>The same <paramref name="builder"/> instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// Adds <see cref="VectorEnvironment"/> variables, sets the Windows service name to the entry assembly name,
        /// calls <c>AddSystemd()</c>, and removes Microsoft <see cref="ConsoleLoggerOptions"/> configurators that
        /// <c>AddSystemd()</c> may add. Sets <see cref="HostOptions.BackgroundServiceExceptionBehavior"/> to
        /// <see cref="BackgroundServiceExceptionBehavior.StopHost"/>.
        /// </remarks>
        internal static HostApplicationBuilder ConfigureBackFillerPlatformHosting(this HostApplicationBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            VectorEnvironment.AddVectorEnvironmentVariables(builder.Configuration);

            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = ApplicationJsonConfiguration.EntryAssemblyName;
            });

            builder.Services.AddSystemd();
            RemoveObsoleteMicrosoftConsoleFormatterConfiguration(builder.Services);

            builder.Services.Configure<HostOptions>(options =>
            {
                options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
            });

            return builder;
        }

        /// <summary>
        /// Removes Microsoft console formatter options that <c>AddSystemd()</c> may register.
        /// </summary>
        /// <param name="services">The service collection to inspect.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// Walks the collection from the end and removes descriptors selected by
        /// <see cref="IsMicrosoftConsoleLoggerOptionsConfiguration"/>.
        /// </remarks>
        private static void RemoveObsoleteMicrosoftConsoleFormatterConfiguration(IServiceCollection services)
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
        /// <param name="descriptor">The descriptor to inspect.</param>
        /// <returns>
        /// <see langword="true"/> when <paramref name="descriptor"/> is
        /// <see cref="IConfigureOptions{TOptions}"/>, <see cref="IPostConfigureOptions{TOptions}"/>, or
        /// <see cref="IValidateOptions{TOptions}"/> of <see cref="ConsoleLoggerOptions"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor"/> is <see langword="null"/>.</exception>
        private static bool IsMicrosoftConsoleLoggerOptionsConfiguration(ServiceDescriptor descriptor)
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
