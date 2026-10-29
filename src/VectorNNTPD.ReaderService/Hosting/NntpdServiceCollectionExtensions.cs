using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Hosting;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.ReaderService.Acme;
using VectorNNTPD.ReaderService.History;
using VectorNNTPD.ReaderService.Redis;
using VectorNNTPD.ReaderService.RabbitMq;
using VectorNNTPD.ReaderService.RabbitMq.ArticleWork;
using VectorNNTPD.ReaderService.RabbitMq.Management;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.ArticleIngestion.OverviewDb;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.ReaderService.Hosting.Systemd;
using VectorNNTPD.ReaderService.Networking.Listeners;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.Common.Networking.RateLimiting;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Storage;
using VectorNNTPD.ReaderService.Session.Commands.Posting;
using VectorNNTPD.ReaderService.Session.SpeedTest;
using VectorNNTPD.ReaderService.Diagnostics;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.PostFilter.Quota;
using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.Moderation;
using VectorNNTPD.ReaderService.Email;
using VectorNNTPD.ReaderService.Email.Smtp;
using VectorNNTPD.ReaderService.Logging;
using VectorNNTPD.ReaderService.Ninpaths;
using VectorNNTPD.ReaderService.Telemetry;
using VectorNNTPD.ReaderService.Transit;
using VectorNNTPD.ReaderService.Transport.Vatp;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.ReaderService.SessionState.RateLimiting;
using VectorNNTPD.Common.Networking.Listeners;
namespace VectorNNTPD.ReaderService.Hosting
{
    /// <summary>
    /// Extension methods for registering VectorNNTPD.ReaderService hosting and lifecycle services.
    /// </summary>
    public static class NntpdServiceCollectionExtensions
    {

        /// <summary>
        /// Registers the Reader-role NNTP server host (Core + reader authentication/POST stack).
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional additional options configuration.</param>
        /// <param name="includePlaceholderService">When true, registers a no-op placeholder application service.</param>
        /// <returns>The same <paramref name="services"/> instance.</returns>
        public static IServiceCollection AddNntpReaderHosting(
            this IServiceCollection services,
            Action<NntpdOptions>? configure = null,
            bool includePlaceholderService = false) =>
            AddNntpdHosting(services, configure, includePlaceholderService);

        /// <summary>
        /// Registers the Transit-role NNTP server host (Core + transit peer/streaming stack).
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional additional options configuration.</param>
        /// <param name="includePlaceholderService">When true, registers a no-op placeholder application service.</param>
        /// <returns>The same <paramref name="services"/> instance.</returns>
        public static IServiceCollection AddNntpTransitHosting(
            this IServiceCollection services,
            Action<NntpdOptions>? configure = null,
            bool includePlaceholderService = false) => throw new NotSupportedException("Transit hosting is not registered in ReaderService.");

        /// <summary>
        /// Adds NNTP server lifecycle, options, and hosted service integration for this host role.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional additional options configuration.</param>
        /// <param name="includePlaceholderService">
        /// When <see langword="true"/>, registers a no-op placeholder <see cref="IApplicationService"/>.
        /// Defaults to <see langword="false"/> now that NNTP listeners are registered.
        /// </param>
        /// <returns>The same <paramref name="services"/> instance.</returns>
        public static IServiceCollection AddNntpdHosting(this IServiceCollection services, Action<NntpdOptions>? configure = null, bool includePlaceholderService = false)
        {
            ArgumentNullException.ThrowIfNull(services);
            services.AddSingleton(typeof(NntpServerRole), NntpServerRole.Reader);
            var applicationPrefix = NntpdOptions.ReaderApplicationPrefix;
            bool includeReader = true;
            bool includeTransit = false;

            services.TryAddSingleton<ITrustedProxyHosts, TrustedProxyHosts>();
            services.TryAddSingleton<IListenSocketBinder>(static _ => SocketListenBinder.Instance);

            if (includeReader)
            {
                // Newsmaster (when configured) then MySQL nntpusers. Reader-authority only.
                // Transit AUTHINFO is MODE STREAM / Transit authority and never enters this provider.
                services.TryAddSingleton<MySqlUserRecordStore>();
                services.TryAddSingleton<INntpUserRecordStore>(static sp =>
                    new CachedNntpUserRecordStore(
                        sp.GetRequiredService<MySqlUserRecordStore>(),
                        sp.GetRequiredService<IRedisService>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CachedNntpUserRecordStore>>()));
                services.TryAddSingleton<MySqlNntpCredentialValidator>();
                services.TryAddSingleton<ISessionStateStore, RedisSessionStateStore>();
                services.TryAddSingleton<DistributedSessionStateTracker>(static sp =>
                {
                    var options = sp.GetRequiredService<IOptions<NntpdOptions>>().Value;
                    var prefix = options.HostApplicationPrefix;
                    var nodeId = options.ServerId is { } id
                        ? ApplicationFqdn.FormatHostLabel(prefix, id)
                        : prefix;
                    return new DistributedSessionStateTracker(
                        sp.GetRequiredService<ISessionStateStore>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DistributedSessionStateTracker>>(),
                        nodeId,
                        TimeProvider.System,
                        bytes: sp.GetRequiredService<IAccountByteAccountant>(),
                        rates: sp.GetRequiredService<IAccountRateAllocator>());
                });
                services.TryAddSingleton<ISessionStateTracker>(static sp =>
                    sp.GetRequiredService<DistributedSessionStateTracker>());
                services.TryAddSingleton<ISessionStateLeaseManager>(static sp =>
                    sp.GetRequiredService<DistributedSessionStateTracker>());
                services.TryAddSingleton<SessionStateService>();
                services.TryAddSingleton<NntpSaslService>();
                services.TryAddSingleton<INntpAuthenticationProvider>(static sp =>
                {
                    var options = sp.GetRequiredService<IOptions<NntpdOptions>>().Value;
                    return new CompositeNntpAuthenticationProvider(
                        NewsmasterNntpAuthenticationProvider.Create(options),
                        options.NewsmasterUser,
                        sp.GetRequiredService<MySqlNntpCredentialValidator>());
                });
                services.TryAddSingleton<IAccountByteStore, RedisAccountByteStore>();
                services.TryAddSingleton<IAccountByteDurableStore, MysqlAccountByteDurableStore>();
                services.TryAddSingleton<AccountByteTracker>();
                services.TryAddSingleton<IAccountByteAccountant>(static sp =>
                    sp.GetRequiredService<AccountByteTracker>());
                services.TryAddSingleton<AccountRateAllocator>();
                services.TryAddSingleton<IAccountRateAllocator>(static sp =>
                    sp.GetRequiredService<AccountRateAllocator>());
            }
            else
            {
                // Transit-only host: deny reader AUTHINFO; peer identity comes from AllowFrom + Transit AUTHINFO.
                services.TryAddSingleton<INntpAuthenticationProvider>(_ =>
                    DenyAllNntpAuthenticationProvider.Instance);
            }

            if (includeTransit)
            {
                services.TryAddSingleton<TransitConfigurationStore>();
                services.TryAddSingleton<ITransitDnsResolver, TransitDnsClientResolver>();
                services.TryAddSingleton<ITransitDnsAddressCache, TransitDnsAddressCache>();
                services.TryAddSingleton<ITransitPeerStateStore, RedisTransitPeerStateStore>();
                services.TryAddSingleton<DistributedTransitPeerStateTracker>(static sp =>
                {
                    var options = sp.GetRequiredService<IOptions<NntpdOptions>>().Value;
                    var prefix = options.HostApplicationPrefix;
                    var nodeId = options.ServerId is { } id
                        ? ApplicationFqdn.FormatHostLabel(prefix, id)
                        : prefix;
                    return new DistributedTransitPeerStateTracker(
                        sp.GetRequiredService<ITransitPeerStateStore>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DistributedTransitPeerStateTracker>>(),
                        nodeId);
                });
                services.TryAddSingleton<ITransitPeerStateTracker>(static sp =>
                    sp.GetRequiredService<DistributedTransitPeerStateTracker>());
                services.TryAddSingleton<ITransitPeerStateLeaseManager>(static sp =>
                    sp.GetRequiredService<DistributedTransitPeerStateTracker>());
                services.TryAddSingleton<TransitPeerStateService>();
                services.TryAddSingleton<ITransitInboundConnectionLimiter, TransitInboundConnectionLimiter>();
                services.TryAddSingleton<IFeedDiagnostics>(static sp =>
                {
                    var options = sp.GetRequiredService<IOptions<NntpdOptions>>().Value;
                    return FeedDiagnosticsOptions.ResolveEnabled(options)
                        ? ActivatorUtilities.CreateInstance<FeedDiagnosticsHub>(sp)
                        : NullFeedDiagnostics.Instance;
                });
                services.TryAddSingleton<ITransitPeerAuthorization, TransitPeerAuthorization>();
                services.TryAddSingleton<TransitConfigurationHotReload>();
                services.TryAddSingleton<ISpeedTestCoordinator, SpeedTestCoordinator>();
            }
            else
            {
                // Reader-only hosts never grant transit/streaming peer privileges on connect.
                services.TryAddSingleton<ITransitPeerAuthorization>(_ => TransitPeerAuthorization.Disabled);
                services.TryAddSingleton<ITransitInboundConnectionLimiter>(_ => TransitInboundConnectionLimiter.Disabled);
                services.TryAddSingleton<IFeedDiagnostics>(_ => NullFeedDiagnostics.Instance);
            }

            var optionsBuilder = services
                .AddOptions<NntpdOptions>()
                .BindConfiguration(NntpdOptions.SectionName)
                .Configure<IConfiguration>(static (options, configuration) =>
                {
                    NntpdAcmeCloudflareOptionsOverlay.OverlaySharedFromRootPreservingApplicationAcme(
                        options,
                        configuration);
                })
                .ValidateDataAnnotations()
                .ValidateOnStart()
                .PostConfigure(options =>
                {
                    options.HostApplicationPrefix = applicationPrefix;
                    options.Systemd ??= new SystemdOptions();
                    options.ArticleIngestion ??= new ArticleIngestionOptions();
                    options.Transit ??= new TransitOptions();
                    options.SpeedTest ??= new SpeedTestOptions();
                    options.FeedDiagnostics ??= new FeedDiagnosticsOptions();
                    NormalizeBindAddresses(options);
                    NormalizeProxyHosts(options);
                    options.Transit ??= new TransitOptions();
                    options.AcmeStateDir = ApplicationLocalPath.ResolveApplicationLocalPath(
                        options.AcmeStateDir,
                        AppContext.BaseDirectory);
                    options.AcmeEmail = string.Empty;
                    options.AcmeCertificatePassword = string.Empty;
                    options.CloudFlareApiKey = string.Empty;
                });

            services.AddSingleton<IValidateOptions<NntpdOptions>, NntpdOptionsValidator>();
            services.AddSingleton<IValidateOptions<NntpdOptions>, PostFilterLeftoverConfigurationValidator>();
            services.AddSingleton<IValidateOptions<NntpdOptions>, TransitLeftoverConfigurationValidator>();
            services.AddSingleton<IOptions<AcmeCloudflareOptions>>(static sp =>
                Options.Create<AcmeCloudflareOptions>(sp.GetRequiredService<IOptions<NntpdOptions>>().Value));
            services.AddSingleton<IDnsPublicationLease, ListenerBoundDnsPublicationLease>();
            services.AddAcmeCloudflareInfrastructure();

            services
                .AddOptions<RedisOptions>()
                .BindConfiguration(RedisOptions.SectionName)
                .ValidateOnStart();
            services.AddSingleton<IValidateOptions<RedisOptions>, RedisOptionsValidator>();

            services
                .AddOptions<RabbitMqOptions>()
                .BindConfiguration(RabbitMqOptions.SectionName)
                .PostConfigure(static options =>
                {
                    options.Management ??= new RabbitMqManagementOptions();
                    options.Username = null;
                    options.Password = null;
                })
                .ValidateOnStart();
            services.AddSingleton<IValidateOptions<RabbitMqOptions>, NntpdRabbitMqOptionsValidator>();

            services.AddHttpClient(RabbitMqManagementHttpClient.HttpClientName, static (sp, client) =>
                {
                    var options = sp.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
                    var baseUrl = options.Management?.BaseUrl?.Trim().TrimEnd('/');
                    if (string.IsNullOrWhiteSpace(baseUrl))
                    {
                        throw new InvalidOperationException("RabbitMQ:Management:BaseUrl is required.");
                    }

                    client.BaseAddress = new Uri(baseUrl + "/", UriKind.Absolute);
                    // Stall protection comes from per-request CancelAfter on the Management client.
                    client.Timeout = Timeout.InfiniteTimeSpan;
                });
            services.TryAddSingleton<IRabbitMqManagementQueueInventory>(static sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>()
                    .CreateClient(RabbitMqManagementHttpClient.HttpClientName);
                return new RabbitMqManagementHttpClient(
                    http,
                    sp.GetRequiredService<IOptions<RabbitMqOptions>>());
            });

            services
                .AddOptions<TransitPeersOptions>()
                .BindConfiguration(TransitPeersOptions.SectionName);
            // Validation is applied when building the immutable snapshot (startup throw /
            // reload ignore). IValidateOptions is not registered so an invalid reload
            // cannot throw from IOptionsMonitor before the last valid snapshot is kept.

            services
                .AddOptions<ControlOptions>()
                .BindConfiguration(ControlOptions.SectionName)
                .PostConfigure(static options =>
                {
                    options.PgpAuthorities ??= new PgpAuthoritiesOptions();
                    options.PgpAuthorities.Source ??= new PgpAuthoritySourceOptions();
                    options.PgpAuthorities.Authorities ??= [];
                    foreach (var authority in options.PgpAuthorities.Authorities)
                    {
                        if (authority is null)
                        {
                            continue;
                        }

                        authority.Authorizations ??= [];
                        if (PgpAuthorityFingerprint.TryNormalize(authority.KeyFingerprint, out var normalized))
                        {
                            authority.KeyFingerprint = normalized;
                        }
                    }
                });
            services.AddSingleton<IValidateOptions<ControlOptions>, ControlOptionsValidator>();
            // Catalogue validation is available for tests and explicit IOptions resolution.
            // ValidateOnStart is not used: an empty or omitted Control section must not
            // prevent NNTPD startup, and this catalogue is not a runtime dependency.

            if (includeReader)
            {
                services
                    .AddOptions<ModerationOptions>()
                    .BindConfiguration(ModerationOptions.SectionName)
                    .PostConfigure(static options =>
                    {
                        options.Source ??= new ModerationSourceOptions();
                        options.Moderators ??= [];
                    })
                    .ValidateOnStart();
                services.AddSingleton<IValidateOptions<ModerationOptions>, ModerationOptionsValidator>();
                services.TryAddSingleton<INntpModeratorRepository, MySqlNntpModeratorRepository>();
                services.TryAddSingleton<ModeratorCatalogueService>();
                services.TryAddSingleton<IModeratorCatalogue>(static sp =>
                    sp.GetRequiredService<ModeratorCatalogueService>());
                services.TryAddSingleton<IModeratorAuthorization>(static sp =>
                    sp.GetRequiredService<ModeratorCatalogueService>());
                services
                    .AddOptions<EmailOptions>()
                    .BindConfiguration(EmailOptions.SectionName)
                    .PostConfigure(static options =>
                    {
                        options.Smtp ??= new SmtpOptions();
                        options.Spool ??= new EmailSpoolOptions();
                    })
                    .ValidateOnStart();
                services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();
                services.TryAddSingleton<IEmailSpool, FilesystemEmailSpool>();
                services.TryAddSingleton<IEmailMessageEncoder, Rfc5322MessageEncoder>();
                services.TryAddSingleton<ISmtpTransport, SmtpTransport>();
                services.TryAddSingleton<IEmailService, EmailService>();
                services.TryAddSingleton<ModerationEmailComposer>();
                services.TryAddSingleton<IModerationSubmissionService, EmailModerationSubmissionService>();
            }

            services.AddNntpDbOptions();

            if (configure is not null)
            {
                optionsBuilder.Configure(configure);
            }

            // Startup order (sequential ApplicationServiceManager):
            // NntpDB (hard dep; MySqlConnector pool) →
            // nntpsharedconfig (initial snapshot, including RabbitMQ, ACME, and Cloudflare credentials, before those consumers) →
            // Cloudflare DNS → Redis → RabbitMQ (hard dep; connection lifecycle only) →
            // RabbitMQ topology (hard dep; cache.requests fanout + cache.broadcast + overviewdb.queue) →
            // StorageServer fleet consumer (hard dep; cache.<fqdn> + in-memory registry) →
            // StorageServer article lookup (hard dep; cache.requests publish + reply consumer) →
            // RabbitMQ article-work RPC (hard dep; reply consumer + request orchestration) →
            // newsgroup catalogue (initial snapshot before RUNNING) →
            // moderator catalogue (nntpmoderators snapshot before RUNNING) →
            // PostFilter policy → PostFilter rejection evidence →
            // HistoryDB writer → HistoryDB maintenance → incoming spool writer →
            // Email delivery (lazy SMTP) → ninpaths (completed inpaths; Top1000) →
            // Transit AllowFrom DNS refresh → SessionState lease renewal →
            // Transit inbound-ownership renewal →
            // plain NNTP listener → ACME → TLS NNTP listener →
            // optional feed-diagnostics reporter → always-on application telemetry.
            services.TryAddSingleton<INntpDbConnectionFactory, MySqlNntpDbConnectionFactory>();
            services.TryAddSingleton<NntpDbService>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, NntpDbService>(static sp =>
                    sp.GetRequiredService<NntpDbService>()));

            services.TryAddSingleton<NntpSharedConfigurationService>();
            services.TryAddSingleton<INntpArticlePolicySource>(static sp =>
                sp.GetRequiredService<NntpSharedConfigurationService>());
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, NntpSharedConfigurationService>(static sp =>
                    sp.GetRequiredService<NntpSharedConfigurationService>()));

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, CloudflareDnsReconciliationApplicationService>());

            services.TryAddSingleton<IRedisConnectionFactory, StackExchangeRedisConnectionFactory>();
            services.TryAddSingleton<RedisService>();
            services.TryAddSingleton<IRedisService>(static sp => sp.GetRequiredService<RedisService>());
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, RedisService>(static sp =>
                    sp.GetRequiredService<RedisService>()));

            services.TryAddSingleton<IRabbitMqConnectionNameProvider>(static sp =>
                new DelegateRabbitMqConnectionNameProvider(() =>
                    RabbitMqRuntimeOptions.GetDefaultConnectionName(
                        ApplicationJsonConfiguration.EntryAssemblyName,
                        sp.GetRequiredService<IOptions<NntpdOptions>>().Value.Fqdn)));
            services.AddRabbitMqInfrastructure();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, RabbitMqService>(static sp =>
                    sp.GetRequiredService<RabbitMqService>()));

            services.TryAddSingleton<RabbitMqTopologyService>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, RabbitMqTopologyService>(static sp =>
                    sp.GetRequiredService<RabbitMqTopologyService>()));

            services.TryAddSingleton<IStorageServerRegistry, StorageServerRegistry>();
            services.TryAddSingleton<StorageServerFleetConsumerService>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, StorageServerFleetConsumerService>(static sp =>
                    sp.GetRequiredService<StorageServerFleetConsumerService>()));

            services.TryAddSingleton<StorageArticleLookupService>();
            services.TryAddSingleton<IStorageArticleLookupClient>(
                static sp => sp.GetRequiredService<StorageArticleLookupService>());
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, StorageArticleLookupService>(static sp =>
                    sp.GetRequiredService<StorageArticleLookupService>()));

            services.TryAddSingleton<VatpConnectionPool>();
            services.TryAddSingleton<IVatpArticleClient, VatpArticleClient>();
            services.TryAddSingleton<IArticlePlacementClient>(static provider =>
            {
                var ingestion = provider.GetRequiredService<IOptions<NntpdOptions>>().Value.ArticleIngestion
                    ?? new ArticleIngestionOptions();
                return new ArticlePlacementClient(
                    provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ArticlePlacementClient>>(),
                    ingestion.MaxOutstandingStoresPerConnection);
            });

            services.TryAddSingleton<ArticleWorkRpcService>();
            services.TryAddSingleton<IArticleWorkRpcClient>(
                static sp => sp.GetRequiredService<ArticleWorkRpcService>());
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, ArticleWorkRpcService>(
                    static sp => sp.GetRequiredService<ArticleWorkRpcService>()));

            services.TryAddSingleton<NewsgroupCatalogueService>();
            services.TryAddSingleton<INewsgroupCatalogue>(static sp =>
                sp.GetRequiredService<NewsgroupCatalogueService>());
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, NewsgroupCatalogueService>(static sp =>
                    sp.GetRequiredService<NewsgroupCatalogueService>()));
            if (includeReader)
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IApplicationService, ModeratorCatalogueService>(static sp =>
                        sp.GetRequiredService<ModeratorCatalogueService>()));

                services.TryAddSingleton<PostFilterMetrics>();
                services.TryAddSingleton<PostFilterReservationIdentity>(static sp =>
                {
                    var options = sp.GetRequiredService<IOptions<NntpdOptions>>().Value;
                    var prefix = options.HostApplicationPrefix;
                    var nodeId = options.ServerId is { } id
                        ? ApplicationFqdn.FormatHostLabel(prefix, id)
                        : prefix;
                    return new PostFilterReservationIdentity(nodeId);
                });
                services.TryAddSingleton<IPostFilterQuotaStore, RedisPostFilterQuotaStore>();
                services.TryAddSingleton<SpamdTransportMetrics>();
                services.TryAddSingleton<IPostFilterSpamAssassin>(static sp =>
                    new SpamdCheckClient(
                        sp.GetRequiredService<SpamdTransportMetrics>(),
                        sp.GetService<ILogger<SpamdCheckClient>>()));
                services.TryAddSingleton<IPostFilterPolicyRepository, MySqlPostFilterPolicyRepository>();
                services.TryAddSingleton<PostFilterPolicyService>();
                services.TryAddSingleton<IPostFilterPolicySource>(static sp =>
                    sp.GetRequiredService<PostFilterPolicyService>());
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IApplicationService, PostFilterPolicyService>(static sp =>
                        sp.GetRequiredService<PostFilterPolicyService>()));
                services.TryAddSingleton<IPostFilter, PostFilterEvaluator>();
                services.TryAddSingleton<PostFilterRejectionEvidenceQueue>();
                services.TryAddSingleton<IPostFilterRejectionEvidenceQueue>(static sp =>
                    sp.GetRequiredService<PostFilterRejectionEvidenceQueue>());
                services.TryAddSingleton<PostFilterRejectionEvidenceService>();
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IApplicationService, PostFilterRejectionEvidenceService>(static sp =>
                        sp.GetRequiredService<PostFilterRejectionEvidenceService>()));
                services.TryAddSingleton<IPostingTraceProtector>(static sp =>
                    AesGcmPostingTraceProtector.Create(sp.GetRequiredService<IOptions<NntpdOptions>>()));
            }

            services.TryAddSingleton<HistoryDb>();
            services.TryAddSingleton<IHistoryDb>(static sp => sp.GetRequiredService<HistoryDb>());
            services.TryAddSingleton<IHistoryLookupMetrics>(static sp => sp.GetRequiredService<HistoryDb>());
            services.TryAddSingleton<ITransitPeerMetrics, TransitPeerMetrics>();
            services.TryAddSingleton<INntpSessionCensus, NntpSessionCensus>();
            services.TryAddSingleton<HistoryWriteService>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, HistoryWriteService>(static sp =>
                    sp.GetRequiredService<HistoryWriteService>()));
            services.TryAddSingleton<HistoryMaintenanceService>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, HistoryMaintenanceService>(static sp =>
                    sp.GetRequiredService<HistoryMaintenanceService>()));

            services.TryAddSingleton<IArticleIngestionQueue, ArticleIngestionQueue>();
            services.TryAddSingleton(IngestionPipelineMetrics.Shared);
            services.TryAddSingleton<INewsLogWriter, SerilogNewsLogWriter>();
            services.TryAddSingleton<NinpathsProcessingService>();
            services.TryAddSingleton<ICompletedPathSurveyFileHandler, NinpathsCompletedFileHandler>();
            services.TryAddSingleton<IPathSurveyWriter, SerilogPathSurveyWriter>();
            services.TryAddSingleton<OverviewDbHandoffPublisher>();
            services.TryAddSingleton<IOverviewDbHandoffPublisher>(static sp =>
                sp.GetRequiredService<OverviewDbHandoffPublisher>());
            services.TryAddSingleton<IncomingSpoolWriterService>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, IncomingSpoolWriterService>(static sp =>
                    sp.GetRequiredService<IncomingSpoolWriterService>()));

            if (includeReader)
            {
                services.TryAddSingleton<EmailDeliveryService>();
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IApplicationService, EmailDeliveryService>(static sp =>
                        sp.GetRequiredService<EmailDeliveryService>()));
            }

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, NinpathsProcessingService>(static sp =>
                    sp.GetRequiredService<NinpathsProcessingService>()));

            if (includeTransit)
            {
                services.TryAddSingleton<TransitCatalogueService>();
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IApplicationService, TransitCatalogueService>(static sp =>
                        sp.GetRequiredService<TransitCatalogueService>()));

                services.TryAddSingleton<TransitDnsRefreshService>();
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IApplicationService, TransitDnsRefreshService>(static sp =>
                    {
                        _ = sp.GetRequiredService<TransitConfigurationHotReload>();
                        return sp.GetRequiredService<TransitDnsRefreshService>();
                    }));
            }

            // SessionState lease renewal follows Transit DNS refresh when both roles are hosted
            // (Combined tests). Reader-only registers it after ninpaths; Transit-only omits it.
            if (includeReader)
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IApplicationService, SessionStateService>(static sp =>
                        sp.GetRequiredService<SessionStateService>()));
            }

            if (includeTransit)
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IApplicationService, TransitPeerStateService>(static sp =>
                        sp.GetRequiredService<TransitPeerStateService>()));
            }

            services.TryAddSingleton<NntpPlainListenerService>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, NntpPlainListenerService>(static sp =>
                    sp.GetRequiredService<NntpPlainListenerService>()));

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, AcmeCertificateApplicationService>());

            services.TryAddSingleton<NntpTlsListenerService>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, NntpTlsListenerService>(static sp =>
                    sp.GetRequiredService<NntpTlsListenerService>()));
            if (includeTransit)
            {
                services.TryAddSingleton<FeedDiagnosticsService>();
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IApplicationService, FeedDiagnosticsService>(static sp =>
                        sp.GetRequiredService<FeedDiagnosticsService>()));
            }

            services.TryAddSingleton<ApplicationTelemetryService>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, ApplicationTelemetryService>(static sp =>
                    sp.GetRequiredService<ApplicationTelemetryService>()));

            if (includePlaceholderService)
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IApplicationService, PlaceholderApplicationService>());
            }

            services.AddSingleton<IApplicationLifecycleOptions>(static sp =>
                sp.GetRequiredService<IOptions<NntpdOptions>>().Value);
            services.AddSingleton<ApplicationServiceManager>();
            services.AddSingleton<ApplicationLifecycle>(static sp => new ApplicationLifecycle(
                sp.GetRequiredService<ApplicationServiceManager>(),
                sp.GetRequiredService<IApplicationLifecycleOptions>(),
                sp.GetRequiredService<ILogger<ApplicationLifecycle>>()));
            services.AddSingleton<NntpdHostLifetime>();
            services.AddSingleton<IApplicationHealth, ApplicationHealth>();

            services.TryAddSingleton<ISystemdNotifyBridge, SystemdNotifyBridge>();
            services.TryAddSingleton<ISystemdRuntime, SystemdRuntime>();

            // Register systemd lifecycle notifier before the main hosted service so it is
            // constructed and subscribed before application startup transitions occur.
            services.AddSingleton<SystemdLifecycleNotifier>();
            services.AddHostedService(sp => sp.GetRequiredService<SystemdLifecycleNotifier>());
            services.AddHostedService<SystemdWatchdogService>();
            services.AddHostedService<NntpdHostedService>();

            return services;
        }

        /// <summary>
        /// Configures platform hosting integrations for Windows Service and Linux systemd without
        /// preventing interactive console execution on other platforms.
        /// </summary>
        /// <param name="builder">The host application builder.</param>
        /// <returns>The same <paramref name="builder"/> instance.</returns>
        /// <remarks>
        /// <para>
        /// <c>AddSystemd()</c> is context-aware: it activates systemd lifetime and notify support only
        /// when the process is detected as a systemd service or <c>NOTIFY_SOCKET</c> is set.
        /// Interactive console and Windows Service execution remain supported.
        /// </para>
        /// <para>
        /// Console log formatting is owned exclusively by Serilog. Any Microsoft console formatter
        /// options registered by <c>AddSystemd()</c> are removed immediately afterward because they
        /// are unused under the Serilog-only logging pipeline.
        /// </para>
        /// <para>
        /// Signal handling (SIGTERM/SIGINT) is provided by the Generic Host /
        /// <c>SystemdLifetime</c>. This application does not register competing signal handlers.
        /// </para>
        /// </remarks>
        public static HostApplicationBuilder ConfigureNntpdPlatformHosting(this HostApplicationBuilder builder)
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

                var shutdown = builder.Configuration.GetSection(NntpdOptions.SectionName)
                    .GetValue<TimeSpan?>("GracefulShutdownTimeout");
                if (shutdown is { } timeout)
                {
                    options.ShutdownTimeout = timeout;
                }
            });

            return builder;
        }

        /// <summary>
        /// Adds the canonical <c>VECTOR__</c> environment-variable source.
        /// </summary>
        /// <param name="builder">The configuration builder.</param>
        /// <returns>The same <paramref name="builder"/> instance.</returns>
        public static IConfigurationBuilder AddNntpdPrefixedEnvironmentVariables(this IConfigurationBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.AddVectorEnvironmentVariables();
        }

        /// <summary>
        /// Removes Microsoft console formatter options that <c>AddSystemd()</c> may register.
        /// </summary>
        /// <remarks>
        /// <c>Microsoft.Extensions.Hosting.Systemd</c> configures
        /// <see cref="ConsoleLoggerOptions.FormatterName"/> to the systemd MEL formatter when it
        /// detects a systemd service. That configuration is obsolete after Phase 0.2 because Serilog
        /// owns console output. Stripping these registrations prevents a misleading unused MEL
        /// formatter from remaining in DI while preserving systemd lifetime and notify registration.
        /// </remarks>
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

        private static void NormalizeBindAddresses(NntpdOptions options)
        {
            if (options.BindAddress is null || options.BindAddress.Length == 0)
            {
                // Code default when configuration omits BindAddress: listen on all interfaces.
                options.BindAddress = ["*"];
                return;
            }

            for (var i = 0; i < options.BindAddress.Length; i++)
            {
                options.BindAddress[i] = options.BindAddress[i]?.Trim() ?? string.Empty;
            }
        }

        private static void NormalizeProxyHosts(NntpdOptions options)
        {
            if (options.ProxyHosts is null)
            {
                options.ProxyHosts = [];
                return;
            }

            for (var i = 0; i < options.ProxyHosts.Length; i++)
            {
                options.ProxyHosts[i] = options.ProxyHosts[i]?.Trim() ?? string.Empty;
            }
        }

    }

    /// <summary>
    /// Phase-0 placeholder application service so the host has a deterministic no-op service to manage.
    /// Replaced by real NNTP services in later phases.
    /// </summary>
    internal sealed class PlaceholderApplicationService : IApplicationService
    {
        public string Name => "Placeholder";

        public Task? Execution => null;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
