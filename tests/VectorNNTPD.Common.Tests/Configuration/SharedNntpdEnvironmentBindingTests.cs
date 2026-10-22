using Microsoft.Extensions.Configuration;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.Configuration
{
    /// <summary>
    /// Locks the shared <c>VECTOR__</c> environment names, root overlay behavior, certificate identities, and secret-free validation failures.
    /// </summary>
    public sealed class SharedNntpdEnvironmentBindingTests
    {
        /// <summary>
        /// Asserts the canonical <c>VECTOR__</c> names and root configuration keys for the shared Cloudflare and ACME secrets, and that those environment names do not use an <c>nntpd__</c> or <c>backfiller__</c> prefix.
        /// </summary>
        [Fact]
        public void SharedEnvironmentVariableNames_AreCanonicalUppercaseVectorPrefix()
        {
            Assert.Equal("VECTOR__CLOUDFLAREAPIKEY", AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable);
            Assert.Equal("VECTOR__ACMECERTIFICATEPASSWORD", AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable);
            Assert.Equal("VECTOR__ACMEACCOUNT", AcmeCloudflareOptions.AcmeAccountEnvironmentVariable);
            Assert.Equal("VECTOR__CLOUDFLAREZONEID", AcmeCloudflareOptions.CloudFlareZoneIdEnvironmentVariable);
            Assert.Equal(AcmeCloudflareOptions.SectionName, string.Empty);
            Assert.Equal("CloudFlareApiKey", AcmeCloudflareOptions.CloudFlareApiKeyConfigurationKey);
            Assert.Equal("AcmeCertificatePassword", AcmeCloudflareOptions.AcmeCertificatePasswordConfigurationKey);
            Assert.Equal("ACMEACCOUNT", AcmeCloudflareOptions.AcmeAccountConfigurationKey);
            Assert.Equal("CloudFlareZoneId", AcmeCloudflareOptions.CloudFlareZoneIdConfigurationKey);

            Assert.True(VectorEnvironment.IsCanonicalName(AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable));
            Assert.True(VectorEnvironment.IsCanonicalName(AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable));
            Assert.True(VectorEnvironment.IsCanonicalName(AcmeCloudflareOptions.CloudFlareZoneIdEnvironmentVariable));
            Assert.Equal(
                AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable,
                VectorEnvironment.Variable(AcmeCloudflareOptions.CloudFlareApiKeyConfigurationKey));
            Assert.Equal(
                AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable,
                VectorEnvironment.Variable(AcmeCloudflareOptions.AcmeCertificatePasswordConfigurationKey));
            Assert.Equal(
                AcmeCloudflareOptions.AcmeAccountEnvironmentVariable,
                VectorEnvironment.Variable(AcmeCloudflareOptions.AcmeAccountConfigurationKey));
            Assert.Equal(
                AcmeCloudflareOptions.CloudFlareZoneIdEnvironmentVariable,
                VectorEnvironment.Variable(AcmeCloudflareOptions.CloudFlareZoneIdConfigurationKey));

            Assert.DoesNotContain("nntpd__", AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("backfiller__", AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "nntpd__",
                AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Asserts that root keys bind the mixed-case Cloudflare API key, certificate password, and zone id onto <see cref="AcmeCloudflareOptions"/> without changing case.
        /// </summary>
        [Fact]
        public void SharedRootTree_BindsTheSameOptionNames()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CloudFlareApiKey"] = "AbC123xYz!",
                    ["AcmeCertificatePassword"] = "Pfx-CaseSensitive-Secret!",
                    ["CloudFlareZoneId"] = "5811a29d39a0732afb5f160c9b137c3d",
                    [AcmeCloudflareOptions.AcmeAccountConfigurationKey] = "ops@example.org",
                })
                .Build();

            var options = new AcmeCloudflareOptions();
            configuration.Bind(options);

            Assert.Equal("AbC123xYz!", options.CloudFlareApiKey);
            Assert.Equal("Pfx-CaseSensitive-Secret!", options.AcmeCertificatePassword);
            Assert.Equal("5811a29d39a0732afb5f160c9b137c3d", options.CloudFlareZoneId);
        }

        /// <summary>
        /// Asserts that the root overlay replaces the certificate password and Cloudflare API key exactly, leaves bind addresses, ports, zone id, and DNS suffix on the options instance, and does not rewrite the configuration value.
        /// </summary>
        [Fact]
        public void OverlaySharedFromRoot_PreservesExactSecretValues()
        {
            const string password = "AbC123xYz!";
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AcmeCertificatePassword"] = password,
                    ["CloudFlareApiKey"] = "Cf-Key-MixedCASE-99",
                    ["CloudFlareZoneId"] = "ZoneIdMixedCase",
                })
                .Build();

            var options = new AcmeCloudflareOptions
            {
                BindAddress = ["198.18.0.66"],
                BindPort = 1199,
                BindPortTls = 5633,
                AcmeCertificatePassword = "from-nntpd-section-must-not-win",
                CloudFlareApiKey = "from-nntpd-section-must-not-win",
                CloudFlareZoneId = "from-section-must-win",
                DnsSuffix = "from-section.test",
            };
            AcmeCloudflareOptions.OverlaySharedFromRoot(options, configuration);

            Assert.Equal(["198.18.0.66"], options.BindAddress);
            Assert.Equal(1199, options.BindPort);
            Assert.Equal(5633, options.BindPortTls);
            Assert.Equal(password, options.AcmeCertificatePassword);
            Assert.Equal("Cf-Key-MixedCASE-99", options.CloudFlareApiKey);
            Assert.Equal("from-section-must-win", options.CloudFlareZoneId);
            Assert.Equal("from-section.test", options.DnsSuffix);
            Assert.Equal(password, configuration["AcmeCertificatePassword"]);
        }

        /// <summary>
        /// Asserts that root <c>BindAddress</c>, <c>BindPort</c>, and <c>BindPortTls</c> values do not replace the options already set on the instance.
        /// </summary>
        [Fact]
        public void OverlaySharedFromRoot_DoesNotCopyRootBindSettings()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BindAddress:0"] = "203.0.113.10",
                    ["BindPort"] = "2119",
                    ["BindPortTls"] = "2563",
                })
                .Build();

            var options = new AcmeCloudflareOptions
            {
                BindAddress = ["127.0.0.1"],
                BindPort = 1199,
                BindPortTls = 5633,
            };
            AcmeCloudflareOptions.OverlaySharedFromRoot(options, configuration);

            Assert.Equal(["127.0.0.1"], options.BindAddress);
            Assert.Equal(1199, options.BindPort);
            Assert.Equal(5633, options.BindPortTls);
        }

        /// <summary>
        /// Asserts that the shared ACME account key overwrites <see cref="AcmeCloudflareOptions.AcmeEmail"/> and that a legacy <c>AcmeEmail</c> root value does not, using <c>VECTOR__ACMEACCOUNT</c> with no application prefix.
        /// </summary>
        [Fact]
        public void OverlaySharedFromRoot_UsesSharedAcmeAccountNotApplicationEmail()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AcmeCloudflareOptions.AcmeAccountConfigurationKey] = "shared@usenet.ninja",
                    ["AcmeEmail"] = "ignored-legacy@example.test",
                })
                .Build();

            var options = new AcmeCloudflareOptions { AcmeEmail = "section-must-not-win@example.test" };
            AcmeCloudflareOptions.OverlaySharedFromRoot(options, configuration);

            Assert.Equal("shared@usenet.ninja", options.AcmeEmail);
            Assert.Equal(
                "VECTOR__ACMEACCOUNT",
                AcmeCloudflareOptions.AcmeAccountEnvironmentVariable);
            Assert.DoesNotContain("NNTPD", AcmeCloudflareOptions.AcmeAccountEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BACKFILLER", AcmeCloudflareOptions.AcmeAccountEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Asserts that obsolete <c>nntpd</c>, <c>Nntpd</c>, <c>backfiller</c>, and <c>BackFiller:LetsEncrypt</c> keys do not populate the shared Cloudflare or certificate-password options.
        /// </summary>
        [Fact]
        public void ObsoleteApplicationPrefixes_DoNotBindSharedOptions()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["nntpd:CloudFlareApiKey"] = "obsolete-nntpd-prefix",
                    ["nntpd:AcmeCertificatePassword"] = "obsolete-nntpd-pfx",
                    ["Nntpd:CloudFlareZoneId"] = "obsolete-nntpd-zone",
                    ["backfiller:CloudFlareApiKey"] = "obsolete-backfiller-prefix",
                    ["BackFiller:LetsEncrypt:CloudFlareApiToken"] = "obsolete-letsencrypt",
                })
                .Build();

            var options = new AcmeCloudflareOptions();
            configuration.Bind(options);
            AcmeCloudflareOptions.OverlaySharedFromRoot(options, configuration);

            Assert.True(string.IsNullOrEmpty(options.CloudFlareApiKey));
            Assert.True(string.IsNullOrEmpty(options.AcmeCertificatePassword));
            Assert.True(string.IsNullOrEmpty(options.CloudFlareZoneId));
        }

        /// <summary>
        /// Asserts that stripping <see cref="VectorEnvironment.Prefix"/> leaves the uppercase root keys, and that the RabbitMQ username variable is <c>VECTOR__RABBITMQ__USERNAME</c>.
        /// </summary>
        [Fact]
        public void VectorPrefix_StripsToRootConfigurationKeys()
        {
            Assert.Equal("CLOUDFLAREAPIKEY", StripVectorPrefix("VECTOR__CLOUDFLAREAPIKEY"));
            Assert.Equal("ACMECERTIFICATEPASSWORD", StripVectorPrefix("VECTOR__ACMECERTIFICATEPASSWORD"));
            Assert.Equal("ACMEACCOUNT", StripVectorPrefix("VECTOR__ACMEACCOUNT"));
            Assert.Equal("CLOUDFLAREZONEID", StripVectorPrefix("VECTOR__CLOUDFLAREZONEID"));
            Assert.Equal("RABBITMQ:USERNAME", StripVectorPrefix("VECTOR__RABBITMQ__USERNAME").Replace("__", ":", StringComparison.Ordinal));
            Assert.Equal(
                "VECTOR__RABBITMQ__USERNAME",
                VectorEnvironment.Variable("RabbitMQ", "Username"));
        }

        /// <summary>
        /// Asserts that <c>docs/common/configuration.md</c> names the canonical <c>VECTOR__</c> secret variables and the application configuration keys, and does not prescribe retired environment names for those secrets or for host bind settings.
        /// </summary>
        [Fact]
        public void ConfigurationDocumentation_DoesNotPrescribeObsoletePrefixes()
        {
            var docs = FindRepoFile(Path.Join("docs", "common", "configuration.md"));
            Assert.True(File.Exists(docs), docs);
            var text = File.ReadAllText(docs);
            Assert.Contains("VECTOR__CLOUDFLAREAPIKEY", text, StringComparison.Ordinal);
            Assert.Contains("VECTOR__ACMECERTIFICATEPASSWORD", text, StringComparison.Ordinal);
            Assert.Contains("VECTOR__ACMEACCOUNT", text, StringComparison.Ordinal);
            Assert.Contains("Nntpd:CloudFlareZoneId", text, StringComparison.Ordinal);
            Assert.Contains("BackFiller:CloudFlareZoneId", text, StringComparison.Ordinal);
            Assert.Contains("BackFiller:ServerId", text, StringComparison.Ordinal);
            Assert.DoesNotContain("BACKFILLER__SERVERID", text, StringComparison.Ordinal);
            Assert.Contains("VECTOR__RABBITMQ__USERNAME", text, StringComparison.Ordinal);
            Assert.DoesNotContain("nntpd__cloudflareapikey", text, StringComparison.Ordinal);
            Assert.DoesNotContain("nntpd__AcmeCertificatePassword", text, StringComparison.Ordinal);
            Assert.DoesNotContain("nntpd__RabbitMQ__Username", text, StringComparison.Ordinal);
            Assert.DoesNotContain("backfiller__BackFiller__", text, StringComparison.Ordinal);
            Assert.DoesNotContain("VECTOR__XTRACEKEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("VECTOR__XTRACEPREVIOUSKEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("VECTOR__NEWSMASTERUSER", text, StringComparison.Ordinal);
            Assert.DoesNotContain("VECTOR__NEWSMASTERPASSWORD", text, StringComparison.Ordinal);
            Assert.DoesNotContain("VECTOR__NAME", text, StringComparison.Ordinal);
            Assert.DoesNotContain("VECTOR__SERVERID", text, StringComparison.Ordinal);
            Assert.DoesNotContain("VECTOR__BINDADDRESS", text, StringComparison.Ordinal);
            Assert.DoesNotContain("VECTOR__BINDPORT", text, StringComparison.Ordinal);
            Assert.DoesNotContain("VECTOR__BINDPORTTLS", text, StringComparison.Ordinal);
            Assert.Contains("NNTPD__XTRACEKEY", text, StringComparison.Ordinal);
            Assert.Contains("NNTPD__SERVERID", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// Asserts that <c>NNTPD__</c> and <c>BACKFILLER__</c> application settings are not canonical <see cref="VectorEnvironment"/> names, while <c>CloudFlareApiKey</c> is.
        /// </summary>
        [Fact]
        public void SharedVectorEnvironment_DoesNotTreatApplicationSettingsAsCanonical()
        {
            Assert.False(VectorEnvironment.IsCanonicalName("NNTPD__SERVERID"));
            Assert.False(VectorEnvironment.IsCanonicalName("NNTPD__XTRACEKEY"));
            Assert.False(VectorEnvironment.IsCanonicalName("NNTPD__NEWSMASTERPASSWORD"));
            Assert.False(VectorEnvironment.IsCanonicalName("BACKFILLER__NAME"));
            Assert.False(VectorEnvironment.IsCanonicalName("BACKFILLER__SERVERID"));
            Assert.True(VectorEnvironment.IsCanonicalName(VectorEnvironment.Variable("CloudFlareApiKey")));
        }

        /// <summary>
        /// Asserts that a BackFiller FQDN requested without the news hostname yields only that FQDN.
        /// </summary>
        [Fact]
        public void CertificateIdentities_BackFillerOmitsNewsHostname()
        {
            var names = CertificateIdentities.ForFqdn("backfiller01.usenet.ninja", includeNewsHostname: false);
            Assert.Equal(["backfiller01.usenet.ninja"], names);
            Assert.DoesNotContain(CertificateIdentities.NewsHostname, names);
        }

        /// <summary>
        /// Asserts that an NNTPD FQDN includes <see cref="CertificateIdentities.NewsHostname"/> after the host name.
        /// </summary>
        [Fact]
        public void CertificateIdentities_NntpdIncludesNewsHostname()
        {
            var names = CertificateIdentities.ForFqdn("nntpd01.usenet.ninja");
            Assert.Equal(["nntpd01.usenet.ninja", CertificateIdentities.NewsHostname], names);
        }

        /// <summary>
        /// Asserts that validating a complete BackFiller ACME options instance succeeds and that any failure text does not contain the certificate password or Cloudflare API key.
        /// </summary>
        [Fact]
        public void Validator_DoesNotEchoSecrets()
        {
            var options = new AcmeCloudflareOptions
            {
                BindAddress = ["*"],
                BindPort = 1190,
                BindPortTls = 1190,
                AcmeEmail = "ops@example.org",
                AcmeCertificatePassword = "super-secret-pfx",
                CloudFlareApiKey = "super-secret-cf",
                CloudFlareZoneId = "zone",
                Fqdn = "backfiller01.usenet.ninja",
                IncludeNewsHostnameInCertificate = false,
            };

            var result = new AcmeCloudflareOptionsValidator(new AcceptAllAssignee()).Validate(null, options);
            Assert.True(result.Succeeded);
            if (result.Failures is not null)
            {
                foreach (var failure in result.Failures)
                {
                    Assert.DoesNotContain("super-secret-pfx", failure, StringComparison.Ordinal);
                    Assert.DoesNotContain("super-secret-cf", failure, StringComparison.Ordinal);
                }
            }
        }

        /// <summary>
        /// Walks parent directories from <see cref="AppContext.BaseDirectory"/> until <paramref name="relativePath"/> exists.
        /// </summary>
        /// <param name="relativePath">Repository-relative path to locate.</param>
        /// <returns>The absolute path of the first matching file.</returns>
        /// <exception cref="FileNotFoundException">Thrown when no parent contains <paramref name="relativePath"/>.</exception>
        private static string FindRepoFile(string relativePath)
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Join(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException($"Could not locate {relativePath}.");
        }

        /// <summary>
        /// Asserts that <paramref name="environmentVariable"/> is a canonical <see cref="VectorEnvironment"/> name and returns the text after <see cref="VectorEnvironment.Prefix"/>.
        /// </summary>
        /// <param name="environmentVariable">Canonical <c>VECTOR__</c> variable name.</param>
        /// <returns>The variable name with the canonical prefix removed.</returns>
        private static string StripVectorPrefix(string environmentVariable)
        {
            Assert.True(VectorEnvironment.IsCanonicalName(environmentVariable));
            return environmentVariable[VectorEnvironment.Prefix.Length..];
        }

        /// <summary>
        /// Test assignee that treats every address as locally assigned and reports no unicast addresses.
        /// </summary>
        private sealed class AcceptAllAssignee : ILocalIpAddressAssignee
        {
            /// <summary>
            /// Reports every address as locally assigned.
            /// </summary>
            /// <param name="address">Address being tested. The value is not inspected.</param>
            /// <returns>Always <see langword="true"/>.</returns>
            public bool IsLocallyAssigned(System.Net.IPAddress address) => true;

            /// <summary>
            /// Returns no assigned unicast addresses.
            /// </summary>
            /// <returns>An empty list.</returns>
            public IReadOnlyList<System.Net.IPAddress> GetAssignedUnicastAddresses() => [];
        }
    }
}
