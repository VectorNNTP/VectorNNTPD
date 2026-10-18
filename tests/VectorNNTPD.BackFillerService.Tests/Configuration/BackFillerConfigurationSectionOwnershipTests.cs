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
    /// Proves BackFiller-owned bind/ACME/DNS settings bind from the
    /// <c>BackFiller</c> section and that root-level duplicates are ignored.
    /// </summary>
    public sealed class BackFillerConfigurationSectionOwnershipTests
    {
        /// <summary>
        /// Contract: Bind populates acme bind and dns settings from the backfiller section.
        /// </summary>

        [Fact]
        public void Bind_populates_acme_bind_and_dns_settings_from_the_backfiller_section()
        {
            var configuration = BackFillerTestOptions.CreateValidConfiguration();
            var options = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(options);

            Assert.Null(typeof(BackFillerOptions).GetProperty("Name"));
            Assert.Equal(1, options.ServerId);
            Assert.Equal("usenet.ninja", options.DnsSuffix);
            Assert.Equal("0123456789abcdef0123456789abcdef", options.CloudFlareZoneId);
            Assert.Equal(["127.0.0.1"], options.BindAddress ?? []);
            Assert.Null(typeof(BackFillerOptions).GetProperty("BindPort"));
            Assert.Equal(1190, options.BindPortTls);
            Assert.Equal(BackFillerOptions.DefaultAcmeDirectoryUrl, options.AcmeDirectoryUrl);
            Assert.Equal(30, options.AcmeRenewalThresholdDays);
            Assert.Equal("certs/", options.AcmeStateDir);
            Assert.Equal("backfiller01.usenet.ninja", options.Fqdn);
        }

        /// <summary>
        /// Contract: Production configuration declares bind address and tls port without bind port.
        /// </summary>

        [Fact]
        public void Production_configuration_declares_bind_address_and_tls_port_without_bind_port()
        {
            var path = FindProductionConfigurationFile();
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var section = root.GetProperty("BackFiller");

            Assert.False(root.TryGetProperty("BindAddress", out _));
            Assert.False(root.TryGetProperty("BindPort", out _));
            Assert.False(root.TryGetProperty("BindPortTls", out _));
            Assert.False(section.TryGetProperty("BindPort", out _));
            var bindAddress = section.GetProperty("BindAddress");
            Assert.True(bindAddress.GetArrayLength() >= 1, "BackFiller:BindAddress must declare at least one listen address.");
            foreach (var value in bindAddress.EnumerateArray().Select(static entry => entry.GetString()))
            {
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.True(
                    value is "*" or "+" || System.Net.IPAddress.TryParse(value, out _),
                    "BackFiller:BindAddress entries must be wildcards or IP addresses.");
            }

            Assert.Equal(1190, section.GetProperty("BindPortTls").GetInt32());
            Assert.False(root.TryGetProperty("CloudFlareZoneId", out _));
            Assert.False(root.TryGetProperty("DnsSuffix", out _));
            Assert.False(root.TryGetProperty("AcmeDirectoryUrl", out _));
            Assert.False(root.TryGetProperty("AcmeRenewalThresholdDays", out _));
            Assert.False(section.TryGetProperty("CloudFlareZoneId", out _));
            Assert.False(section.TryGetProperty("DnsSuffix", out _));
            Assert.False(section.TryGetProperty("AcmeDirectoryUrl", out _));
            Assert.False(section.TryGetProperty("AcmeRenewalThresholdDays", out _));
            Assert.False(root.TryGetProperty("ServerId", out _));
            Assert.Equal(1, section.GetProperty("ServerId").GetInt32());
            Assert.False(section.TryGetProperty("Name", out _));
        }

        /// <summary>
        /// Contract: Root level cloudflare zone id is not used by backfiller.
        /// </summary>

        [Fact]
        public void Root_level_cloudflare_zone_id_is_not_used_by_backfiller()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["CloudFlareZoneId"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            pairs["BackFiller:CloudFlareZoneId"] = "0123456789abcdef0123456789abcdef";

            var (identity, acme, _) = BindThroughAdapter(pairs);

            Assert.Equal("0123456789abcdef0123456789abcdef", identity.CloudFlareZoneId);
            Assert.Equal("0123456789abcdef0123456789abcdef", acme.CloudFlareZoneId);
            Assert.NotEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", acme.CloudFlareZoneId);
        }

        /// <summary>
        /// Contract: Adapter does not copy or require cleartext bind port.
        /// </summary>

        [Fact]
        public void Adapter_does_not_copy_or_require_cleartext_bind_port()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs.Remove("BackFiller:BindPort");
            pairs["BackFiller:BindPortTls"] = "5630";
            pairs["BindPort"] = "2119";

            var (identity, acme, runtime) = BindThroughAdapter(pairs);

            Assert.Null(typeof(BackFillerOptions).GetProperty("BindPort"));
            Assert.Equal(5630, identity.BindPortTls);
            Assert.Equal(5630, acme.BindPortTls);
            Assert.Equal(5630, runtime.BindPortTls);
            Assert.NotEqual(2119, runtime.BindPortTls);
            Assert.NotEqual(acme.BindPort, runtime.BindPortTls);
        }

        /// <summary>
        /// Contract: Root level acme directory url is not used by backfiller.
        /// </summary>

        [Fact]
        public void Root_level_acme_directory_url_is_not_used_by_backfiller()
        {
            AssertIgnoredRootValue(
                rootKey: "AcmeDirectoryUrl",
                rootValue: "https://root-must-not-bind.example/directory",
                nestedKey: "BackFiller:AcmeDirectoryUrl",
                nestedValue: "https://acme-staging-v02.api.letsencrypt.org/directory",
                actual: static (identity, acme, _) => acme.AcmeDirectoryUrl);
        }

        /// <summary>
        /// Contract: Root level bind address is not used by backfiller.
        /// </summary>

        [Fact]
        public void Root_level_bind_address_is_not_used_by_backfiller()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BindAddress:0"] = "203.0.113.88";
            pairs["BackFiller:BindAddress:0"] = "127.0.0.1";

            var (identity, acme, runtime) = BindThroughAdapter(pairs);

            Assert.Equal(["127.0.0.1"], identity.BindAddress ?? []);
            Assert.Equal(["127.0.0.1"], acme.BindAddress);
            Assert.Equal(["127.0.0.1"], runtime.BindAddressTokens);
            Assert.DoesNotContain("203.0.113.88", acme.BindAddress);
        }

        /// <summary>
        /// Contract: Root level bind port tls is not used by backfiller.
        /// </summary>

        [Fact]
        public void Root_level_bind_port_tls_is_not_used_by_backfiller()
        {
            AssertIgnoredRootValue(
                rootKey: "BindPortTls",
                rootValue: "563",
                nestedKey: "BackFiller:BindPortTls",
                nestedValue: "1190",
                actual: static (_, acme, runtime) => acme.BindPortTls.ToString(System.Globalization.CultureInfo.InvariantCulture),
                runtimeActual: static runtime => runtime.BindPortTls.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Contract: Root level dns suffix is not used by backfiller.
        /// </summary>

        [Fact]
        public void Root_level_dns_suffix_is_not_used_by_backfiller()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["DnsSuffix"] = "root-must-not-bind.example";
            pairs["BackFiller:DnsSuffix"] = "usenet.ninja";

            var (identity, acme, runtime) = BindThroughAdapter(pairs);

            Assert.Equal("usenet.ninja", identity.DnsSuffix);
            Assert.Equal("usenet.ninja", acme.DnsSuffix);
            Assert.Equal("usenet.ninja", runtime.DnsSuffix);
            Assert.Equal("backfiller01.usenet.ninja", identity.Fqdn);
            Assert.Equal("backfiller01.usenet.ninja", acme.Fqdn);
            Assert.Equal("backfiller01.usenet.ninja", runtime.Fqdn);
            Assert.NotEqual("root-must-not-bind.example", acme.DnsSuffix);
        }

        /// <summary>
        /// Contract: Nested backfiller values drive runtime and common options.
        /// </summary>

        [Fact]
        public void Nested_backfiller_values_drive_runtime_and_common_options()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:Name"] = "cache";
            pairs["BackFiller:ServerId"] = "7";
            pairs["BackFiller:DnsSuffix"] = "example.test";
            pairs["BackFiller:BindAddress:0"] = "127.0.0.1";
            pairs["BackFiller:BindPort"] = "1191";
            pairs["BackFiller:BindPortTls"] = "5630";
            pairs["BindPort"] = "2119";
            pairs["BackFiller:AcmeDirectoryUrl"] = "https://acme-staging-v02.api.letsencrypt.org/directory";
            pairs["BackFiller:AcmeEmail"] = "ignored-section@example.test";
            pairs["BackFiller:AcmeRenewalThresholdDays"] = "14";
            pairs["BackFiller:AcmeStateDir"] = "nested-certs/";
            pairs[AcmeCloudflareOptions.AcmeAccountConfigurationKey] = "ops@example.test";
            pairs["BindPortTls"] = "119";
            pairs["DnsSuffix"] = "ignored.example";
            pairs["AcmeDirectoryUrl"] = "https://ignored.example/directory";
            pairs["AcmeEmail"] = "ignored@root.example";

            var (identity, acme, runtime) = BindThroughAdapter(pairs);

            Assert.Null(typeof(BackFillerOptions).GetProperty("Name"));
            Assert.Equal(7, identity.ServerId);
            Assert.Equal("backfiller07.example.test", identity.Fqdn);
            Assert.Equal(5630, acme.BindPortTls);
            Assert.NotEqual(1191, acme.BindPort);
            Assert.NotEqual(2119, acme.BindPortTls);
            Assert.Equal("https://acme-staging-v02.api.letsencrypt.org/directory", acme.AcmeDirectoryUrl);
            Assert.Equal(string.Empty, acme.AcmeEmail);
            Assert.NotEqual("ops@example.test", acme.AcmeEmail);
            Assert.NotEqual("ignored-section@example.test", acme.AcmeEmail);
            Assert.NotEqual("ignored@root.example", acme.AcmeEmail);
            Assert.Equal(14, acme.AcmeRenewalThresholdDays);
            Assert.Equal("nested-certs/", acme.AcmeStateDir);
            Assert.False(acme.IncludeNewsHostnameInCertificate);
            Assert.Equal(["backfiller07.example.test"], CertificateIdentities.ForFqdn(acme.Fqdn, acme.IncludeNewsHostnameInCertificate));
            Assert.Equal(5630, runtime.BindPortTls);
            Assert.Equal("backfiller07.example.test", runtime.Fqdn);
            Assert.Equal(["backfiller07.example.test"], runtime.CertificateDomainNames);
            Assert.DoesNotContain("news.usenet.ninja", runtime.CertificateDomainNames);
        }

        /// <summary>
        /// Contract: Host runtime options use nested backfiller values not root duplicates.
        /// </summary>

        [Fact]
        public void Host_runtime_options_use_nested_backfiller_values_not_root_duplicates()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BindAddress:0"] = "198.51.100.10";
            pairs["BindPortTls"] = "563";
            pairs["DnsSuffix"] = "root-must-not-bind.example";
            pairs["AcmeDirectoryUrl"] = "https://root-must-not-bind.example/directory";
            pairs["BackFiller:BindAddress:0"] = "127.0.0.1";
            pairs["BackFiller:BindPortTls"] = "1190";
            pairs["BackFiller:DnsSuffix"] = "usenet.ninja";

            using var host = CreateHost(pairs);
            var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
            var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();

            Assert.Equal(["127.0.0.1"], acme.BindAddress);
            Assert.Equal(1190, acme.BindPortTls);
            Assert.Equal("usenet.ninja", acme.DnsSuffix);
            Assert.Equal("backfiller01.usenet.ninja", acme.Fqdn);
            Assert.Equal(BackFillerOptions.DefaultAcmeDirectoryUrl, acme.AcmeDirectoryUrl);
            Assert.False(acme.IncludeNewsHostnameInCertificate);
            Assert.Equal(1190, runtime.BindPortTls);
            Assert.Equal("backfiller01.usenet.ninja", runtime.Fqdn);
            Assert.DoesNotContain("198.51.100.10", acme.BindAddress);
        }

        /// <summary>
        /// Host build or options resolution fails validation before the process runs.
        /// </summary>

        [Fact]
        public void Host_validate_on_start_fails_when_nested_bind_port_tls_is_zero()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:BindPortTls"] = "0";
            pairs["BindPortTls"] = "1190";

            var ex = Assert.Throws<OptionsValidationException>(() =>
            {
                using var host = CreateHost(pairs);
            });
            Assert.Contains("BindPortTls", ex.Message, StringComparison.Ordinal);
            Assert.Contains("TLS-only", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: Nested tls only validation fails when bind port tls is zero.
        /// </summary>

        [Fact]
        public void Nested_tls_only_validation_fails_when_bind_port_tls_is_zero()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BindPortTls = 0;
            var backfiller = BackFillerTestOptions.CreateValidator().Validate(null, options);
            Assert.True(backfiller.Failed);
            Assert.Contains(backfiller.Failures!, static f => f.Contains("BindPortTls", StringComparison.Ordinal));

            var acme = BackFillerTestOptions.CreateValidAcme();
            acme.BindPortTls = 0;
            var tlsOnly = new TlsOnlyAcmeCloudflareOptionsValidator().Validate(null, acme);
            Assert.True(tlsOnly.Failed);
            Assert.Contains("TLS-only", tlsOnly.Failures!.Single(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Nested_bind_address_validation_still_rejects_unassigned_addresses()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:BindAddress:0"] = "198.51.100.10";
            pairs["BindAddress:0"] = "*";

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(pairs)
                .Build();
            var identity = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(identity);
            var acme = new AcmeCloudflareOptions();
            BackFillerAcmeCloudflareOptionsAdapter.Apply(acme, identity, configuration);

            var result = new AcmeCloudflareOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: false))
                .Validate(null, acme);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("BindAddress", StringComparison.Ordinal));
            Assert.Contains(result.Failures!, static f => f.Contains("198.51.100.10", StringComparison.Ordinal));
        }

        /// <summary>
        /// Binds both a root key and the nested key, then asserts the nested value is the one that survives.
        /// </summary>
        /// <param name="rootKey">Unprefixed key that must not win.</param>
        /// <param name="rootValue">Value placed on <paramref name="rootKey"/>.</param>
        /// <param name="nestedKey">Section key that must win.</param>
        /// <param name="nestedValue">Expected bound value.</param>
        /// <param name="actual">Reads the bound value from identity, ACME, or runtime options.</param>
        /// <param name="runtimeActual">Optional second read of the runtime snapshot.</param>
        private static void AssertIgnoredRootValue(
            string rootKey,
            string rootValue,
            string nestedKey,
            string nestedValue,
            Func<BackFillerOptions, AcmeCloudflareOptions, BackFillerRuntimeOptions, string> actual,
            Func<BackFillerRuntimeOptions, string>? runtimeActual = null)
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs[rootKey] = rootValue;
            pairs[nestedKey] = nestedValue;

            var (identity, acme, runtime) = BindThroughAdapter(pairs);
            Assert.Equal(nestedValue, actual(identity, acme, runtime));
            if (runtimeActual is not null)
            {
                Assert.Equal(nestedValue, runtimeActual(runtime));
            }
        }

        /// <summary>Binds identity, ACME adapter output, and runtime options from in-memory pairs.</summary>
        /// <param name="pairs">Configuration keys and values under test.</param>
        private static (BackFillerOptions Identity, AcmeCloudflareOptions Acme, BackFillerRuntimeOptions Runtime) BindThroughAdapter(
            Dictionary<string, string?> pairs)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(pairs)
                .Build();
            var identity = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(identity);
            var acme = new AcmeCloudflareOptions();
            BackFillerAcmeCloudflareOptionsAdapter.Apply(acme, identity, configuration);
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                identity,
                BindNntpDb(configuration),
                acme);
            return (identity, acme, runtime);
        }

        /// <summary>Copies <c>ConnectionStrings:NntpDB</c> into options. An absent key becomes an empty string.</summary>
        /// <param name="configuration">Configuration under test.</param>
        private static NntpDbOptions BindNntpDb(IConfiguration configuration)
        {
            var nntpDb = new NntpDbOptions();
            nntpDb.ConnectionString = configuration.GetConnectionString(NntpDbOptions.ConnectionStringName) ?? string.Empty;
            return nntpDb;
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
                ContentRootPath = AppContext.BaseDirectory,
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

        /// <summary>Walks parents of the test output directory until <c>src/VectorNNTPD.BackFillerService/VectorNNTPD.BackFillerService.json</c> exists.</summary>
        /// <exception cref="FileNotFoundException">No parent contains that file.</exception>
        private static string FindProductionConfigurationFile()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = RelativePath.Under(directory.FullName, "src", "VectorNNTPD.BackFillerService", "VectorNNTPD.BackFillerService.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException("Could not locate src/VectorNNTPD.BackFillerService/VectorNNTPD.BackFillerService.json.");
        }
    }
}
