using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.Nntp;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.BackFillerService.Tests.Configuration
{
    /// <summary>
    /// Issue #172: after <c>nntpsharedconfig</c> projection, the runtime FQDN and dependent consumers
    /// must use the database-derived identity rather than the provisional config-file name.
    /// </summary>
    public sealed class BackFillerRuntimeFqdnProjectionTests
    {
        /// <summary>Config-file suffix that deliberately differs from the shared-configuration suffix.</summary>
        private const string ConfigFileSuffix = "config-file.example";

        /// <summary>Authoritative suffix published from the shared-configuration stub.</summary>
        private const string SharedSuffix = "shared-db.example";

        /// <summary>
        /// Before Start, runtime and ACME keep the config-file FQDN; after Start, runtime, ACME, BackFiller options,
        /// the DI parser accessor, retention fallback, and RabbitMQ connection naming agree on the shared FQDN.
        /// </summary>
        [Fact]
        public async Task Start_WhenSharedDnsSuffixDiffers_UpdatesRuntimeAndDependentConsumers()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.DnsSuffix = ConfigFileSuffix;
            options.ServerId = 7;
            var nntpDb = BackFillerTestOptions.CreateValidNntpDb();
            var acme = Options.Create(BackFillerTestOptions.CreateValidAcme(options));
            var runtime = BackFillerRuntimeOptionsFactory.Create(options, nntpDb, acme.Value);
            var provisionalFqdn = ApplicationFqdn.Build(BackFillerOptions.ApplicationPrefix, 7, ConfigFileSuffix);
            var authoritativeFqdn = ApplicationFqdn.Build(BackFillerOptions.ApplicationPrefix, 7, SharedSuffix);

            Assert.Equal(provisionalFqdn, runtime.Fqdn);
            Assert.Equal(provisionalFqdn, options.Fqdn);
            Assert.Equal(provisionalFqdn, acme.Value.Fqdn);

            var retention = new ArticleRetentionAuthority(runtime, TimeProvider.System, NullLogger<ArticleRetentionAuthority>.Instance);
            var runtimeParser = new BackFillerRuntimeArticleParser(runtime);
            var provisionalParser = runtimeParser.Current;

            var rabbitNameBefore = RabbitMqRuntimeOptions.GetDefaultConnectionName("VectorNNTPD.BackFillerService", runtime.Fqdn);
            Assert.Contains(provisionalFqdn, rabbitNameBefore, StringComparison.Ordinal);

            await using var harness = CreateHarness(options, runtime, acme, SharedSuffix);
            await harness.Service.StartAsync(CancellationToken.None);

            Assert.Equal(authoritativeFqdn, runtime.Fqdn);
            Assert.Equal(SharedSuffix, runtime.DnsSuffix);
            Assert.Equal(authoritativeFqdn, options.Fqdn);
            Assert.Equal(SharedSuffix, options.DnsSuffix);
            Assert.Equal(authoritativeFqdn, acme.Value.Fqdn);
            Assert.Equal(SharedSuffix, acme.Value.DnsSuffix);
            Assert.Equal([authoritativeFqdn], runtime.CertificateDomainNames);

            Assert.NotSame(provisionalParser, runtimeParser.Current);

            var rabbitNameAfter = RabbitMqRuntimeOptions.GetDefaultConnectionName("VectorNNTPD.BackFillerService", runtime.Fqdn);
            Assert.Contains(authoritativeFqdn, rabbitNameAfter, StringComparison.Ordinal);
            Assert.DoesNotContain(provisionalFqdn, rabbitNameAfter, StringComparison.Ordinal);

            var created = CreateMinimalArticle(authoritativeFqdn);
            var retained = retention.RetainCanonical(
                Encoding.ASCII.GetString(created.Record.MessageId),
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                created.Record,
                created.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, retained.Kind);
            Assert.Equal(authoritativeFqdn, retained.Fqdn);
        }

        /// <summary>
        /// When an endpoint FQDN is supplied, retention keeps that value and does not rewrite it to the runtime fallback.
        /// </summary>
        [Fact]
        public void RetainCanonical_WhenEndpointFqdnSupplied_UsesEndpointNotRuntimeFallback()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.DnsSuffix = ConfigFileSuffix;
            var runtime = BackFillerRuntimeOptionsFactory.Create(options, BackFillerTestOptions.CreateValidNntpDb());
            runtime.ApplySharedDnsIdentity(
                SharedSuffix,
                ApplicationFqdn.Build(BackFillerOptions.ApplicationPrefix, 1, SharedSuffix),
                includeNewsHostnameInCertificate: false);

            var retention = new ArticleRetentionAuthority(runtime, TimeProvider.System, NullLogger<ArticleRetentionAuthority>.Instance);
            const string endpoint = "endpoint.override.example";
            var created = CreateMinimalArticle(runtime.Fqdn);
            var retained = retention.RetainCanonical(
                Encoding.ASCII.GetString(created.Record.MessageId),
                Guid.Parse("bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee"),
                created.Record,
                created.SelectedDateHeaderName,
                endpointFqdn: endpoint);

            Assert.Equal(ArticleRetentionKind.Retained, retained.Kind);
            Assert.Equal(endpoint, retained.Fqdn);
        }

        /// <summary>
        /// Matching config-file and shared suffixes leave the same FQDN before and after projection.
        /// </summary>
        [Fact]
        public async Task Start_WhenSharedDnsSuffixMatchesConfigFile_KeepsSameFqdn()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.DnsSuffix = SharedSuffix;
            options.ServerId = 3;
            var expected = ApplicationFqdn.Build(BackFillerOptions.ApplicationPrefix, 3, SharedSuffix);
            var acme = Options.Create(BackFillerTestOptions.CreateValidAcme(options));
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb(),
                acme.Value);
            Assert.Equal(expected, runtime.Fqdn);

            await using var harness = CreateHarness(options, runtime, acme, SharedSuffix);
            await harness.Service.StartAsync(CancellationToken.None);

            Assert.Equal(expected, runtime.Fqdn);
            Assert.Equal(expected, options.Fqdn);
            Assert.Equal(expected, acme.Value.Fqdn);
        }

        /// <summary>
        /// A second Start after a successful first Start is a no-op and does not clear the authoritative FQDN.
        /// </summary>
        [Fact]
        public async Task Start_WhenCalledTwice_KeepsAuthoritativeFqdn()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.DnsSuffix = ConfigFileSuffix;
            options.ServerId = 2;
            var acme = Options.Create(BackFillerTestOptions.CreateValidAcme(options));
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb(),
                acme.Value);
            var authoritativeFqdn = ApplicationFqdn.Build(BackFillerOptions.ApplicationPrefix, 2, SharedSuffix);

            await using var harness = CreateHarness(options, runtime, acme, SharedSuffix);
            await harness.Service.StartAsync(CancellationToken.None);
            await harness.Service.StartAsync(CancellationToken.None);

            Assert.Equal(authoritativeFqdn, runtime.Fqdn);
            Assert.Equal(authoritativeFqdn, acme.Value.Fqdn);
        }

        /// <summary>
        /// Builds a harness whose shared stub publishes <paramref name="sharedDnsSuffix"/>.
        /// </summary>
        private static ProjectionHarness CreateHarness(
            BackFillerOptions options,
            BackFillerRuntimeOptions runtime,
            IOptions<AcmeCloudflareOptions> acme,
            string sharedDnsSuffix)
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            var logger = new CollectingLogger<ProviderAccountConfigurationService>();
            var registry = new NntpProviderRegistry(
                catalog,
                transport,
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                TimeSpan.FromSeconds(2),
                NullLogger<NntpProviderRegistry>.Instance);
            var shared = new FixedSharedConfigurationSource
            {
                Value = new NntpSharedConfiguration(
                    1024,
                    "news.usenet.ninja",
                    null,
                    "https://acme-v02.api.letsencrypt.org/directory",
                    14,
                    "0123456789abcdef0123456789abcdef",
                    sharedDnsSuffix),
            };
            var rabbit = Options.Create(new RabbitMqOptions
            {
                Username = "from-env-user",
                Password = "from-env-pass",
                VirtualHost = "/articles",
            });
            var service = new ProviderAccountConfigurationService(
                new FakeProviderAccountSource(),
                shared,
                registry,
                runtime,
                Options.Create(options),
                acme,
                logger,
                rabbit);
            return new ProjectionHarness(service, registry);
        }

        /// <summary>Builds a minimal accepted article for retention admission.</summary>
        private static ArticleRecordCreateResult CreateMinimalArticle(string localFqdn)
        {
            var destuffed =
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <fqdn-projection-" + Guid.NewGuid().ToString("N") + "@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: fqdn-projection\r\n" +
                "\r\n" +
                "body\r\n";
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser(localFqdn),
                Encoding.ASCII.GetBytes(destuffed));
            Assert.True(created.IsAccepted);
            return created;
        }

        /// <summary>Hosts ProviderAccountConfigurationService for FQDN projection assertions.</summary>
        private sealed class ProjectionHarness : IAsyncDisposable
        {
            internal ProjectionHarness(
                ProviderAccountConfigurationService service,
                NntpProviderRegistry registry)
            {
                Service = service;
                Registry = registry;
            }

            internal ProviderAccountConfigurationService Service { get; }

            internal NntpProviderRegistry Registry { get; }

            public async ValueTask DisposeAsync()
            {
                await Service.DisposeAsync().ConfigureAwait(false);
                await Registry.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Shared-configuration stub with a replaceable snapshot.</summary>
        private sealed class FixedSharedConfigurationSource : IBackFillerSharedConfigurationSource
        {
            public required NntpSharedConfiguration Value { get; set; }

            public ValueTask<NntpSharedConfiguration> ReadAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(Value);
            }
        }
    }
}
