using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Configuration
{
    /// <summary>
    /// Canonical BackFiller FQDN is <c>backfiller{ServerId:00}.{DnsSuffix}</c>.
    /// </summary>
    public sealed class BackFillerCanonicalIdentityTests
    {
        /// <summary>
        /// Contract: Application prefix is fixed and name is not configuration.
        /// </summary>

        [Fact]
        public void Application_prefix_is_fixed_and_name_is_not_configuration()
        {
            Assert.Equal("backfiller", BackFillerOptions.ApplicationPrefix);
            Assert.Null(typeof(BackFillerOptions).GetProperty("Name"));
            Assert.Null(typeof(BackFillerOptions).GetField(
                "NameEnvironmentVariable",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
            Assert.Null(typeof(BackFillerRuntimeOptions).GetProperty("Name"));
        }

        /// <summary>
        /// Contract: TransitServer is not part of BackFiller configuration.
        /// </summary>

        [Fact]
        public void TransitServer_is_not_part_of_BackFiller_configuration()
        {
            Assert.Null(typeof(BackFillerOptions).GetProperty("TransitServer"));
            Assert.Null(typeof(BackFillerRuntimeOptions).GetProperty("TransitServer"));
            Assert.Null(typeof(BackFillerOptions).Assembly.GetType(
                "VectorNNTPD.BackFillerService.Configuration.BackFillerTransitServerOptions"));
            Assert.Null(typeof(BackFillerRuntimeOptions).Assembly.GetType(
                "VectorNNTPD.BackFillerService.Configuration.BackFillerTransitServerRuntimeOptions"));
        }

        /// <summary>
        /// Contract: Canonical fqdn comes from shared builder.
        /// </summary>

        [Theory]
        [InlineData(1, "backfiller01.usenet.ninja")]
        [InlineData(8, "backfiller08.usenet.ninja")]
        [InlineData(99, "backfiller99.usenet.ninja")]
        public void Canonical_fqdn_comes_from_shared_builder(int serverId, string expected)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.ServerId = serverId;
            options.DnsSuffix = "usenet.ninja";

            Assert.Equal(expected, options.Fqdn);
            Assert.Equal(
                ApplicationFqdn.Build(BackFillerOptions.ApplicationPrefix, serverId, "usenet.ninja"),
                options.Fqdn);
            Assert.NotEqual(
                ApplicationFqdn.Build("nntpd", serverId, "usenet.ninja"),
                options.Fqdn);
        }

        /// <summary>
        /// Contract: Leftover name configuration does not change fqdn.
        /// </summary>

        [Fact]
        public void Leftover_name_configuration_does_not_change_fqdn()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:Name"] = "cache";
            pairs["BackFiller:ServerId"] = "1";
            pairs["BackFiller:DnsSuffix"] = "usenet.ninja";

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(pairs)
                .Build();
            var options = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(options);

            Assert.Equal("backfiller01.usenet.ninja", options.Fqdn);
            Assert.NotEqual("cache01.usenet.ninja", options.Fqdn);
        }

        /// <summary>
        /// Contract: Host acme cloudflare and storage use the canonical fqdn.
        /// </summary>

        [Fact]
        public void Host_acme_cloudflare_and_storage_use_the_canonical_fqdn()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:ServerId"] = "8";

            using var host = CreateHost(pairs);
            var identity = host.Services.GetRequiredService<IOptions<BackFillerOptions>>().Value;
            var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
            var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            const string expected = "backfiller08.usenet.ninja";

            Assert.Equal(expected, identity.Fqdn);
            Assert.Equal(expected, acme.Fqdn);
            Assert.Equal(expected, runtime.Fqdn);
            Assert.Equal(identity.Fqdn, acme.Fqdn);
            Assert.Equal(identity.Fqdn, runtime.Fqdn);
            Assert.False(acme.IncludeNewsHostnameInCertificate);
            Assert.Equal([expected], runtime.CertificateDomainNames);
            Assert.DoesNotContain(CertificateIdentities.NewsHostname, runtime.CertificateDomainNames);
            Assert.Equal(
                Path.Join("certs", "journal", expected + ".json"),
                AcmePaths.TransactionJournalPath("certs", runtime.Fqdn));
            Assert.Equal(
                Path.Join("certs", "live", expected),
                AcmePaths.LiveDir("certs", runtime.Fqdn));
        }

        /// <summary>
        /// Builds a host from <paramref name="pairs"/> with assign-all addresses, a no-op DNS reconciler, 64 GiB of memory, and fake RabbitMQ and provider accounts.
        /// </summary>
        /// <param name="pairs">In-memory configuration. This host does not read the production JSON file.</param>
        private static IHost CreateHost(Dictionary<string, string?> pairs)
        {
            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
            {
                ApplicationName = "VectorNNTPD.BackFillerService.Tests",
                EnvironmentName = Environments.Development,
            });
            builder.Configuration.AddInMemoryCollection(pairs);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<VectorNNTPD.Common.Cloudflare.ICloudflareDnsReconciler>(
                new NoOpCloudflareDnsReconciler());
            builder.Services.AddSingleton<IPhysicalMemoryProvider>(
                new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
            builder.Services.AddSingleton<VectorNNTPD.Common.Messaging.RabbitMq.IRabbitMqConnectionFactory>(
                new FakeBackFillerRabbitMqConnectionFactory());
            builder.Services.AddSingleton<VectorNNTPD.BackFillerService.Accounts.IProviderAccountSource>(
                new FakeProviderAccountSource());
            builder.AddBackFillerHosting();
            return builder.Build();
        }
    }
}
