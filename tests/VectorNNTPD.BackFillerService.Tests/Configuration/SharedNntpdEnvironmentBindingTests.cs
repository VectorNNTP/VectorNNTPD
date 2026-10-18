using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Acme;

namespace VectorNNTPD.BackFillerService.Tests.Configuration
{
    /// <summary>
    /// Shared VECTOR environment variable names and BackFiller host binding without root-level bind settings.
    /// </summary>

    public sealed class SharedNntpdEnvironmentBindingTests
    {
        /// <summary>
        /// Contract: BackFiller UsesTheSameVectorEnvironmentVariableNames.
        /// </summary>

        [Fact]
        public void BackFiller_UsesTheSameVectorEnvironmentVariableNames()
        {
            Assert.Equal("VECTOR__CLOUDFLAREAPIKEY", AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable);
            Assert.Equal("VECTOR__ACMECERTIFICATEPASSWORD", AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable);
            Assert.Equal("VECTOR__ACMEACCOUNT", AcmeCloudflareOptions.AcmeAccountEnvironmentVariable);
            Assert.Equal("VECTOR__CLOUDFLAREZONEID", AcmeCloudflareOptions.CloudFlareZoneIdEnvironmentVariable);
            Assert.Null(typeof(BackFillerOptions).GetField(
                "NameEnvironmentVariable",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
            Assert.Null(typeof(BackFillerOptions).GetProperty("Name"));
            Assert.Null(typeof(BackFillerOptions).GetField(
                "ServerIdEnvironmentVariable",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
            Assert.Equal("VECTOR__RABBITMQ__USERNAME", BackFillerOptions.RabbitMqUsernameEnvironmentVariable);
            Assert.Equal("ConnectionStrings__NntpDB", NntpDbOptions.ConnectionStringEnvironmentVariable);
            Assert.Equal("NntpDB", NntpDbOptions.ConnectionStringName);
            Assert.Null(typeof(BackFillerOptions).GetField(
                "GrabberDbEnvironmentVariable",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
            Assert.Equal(VectorEnvironment.Prefix, BackFillerOptions.EnvironmentVariablePrefix);
            Assert.True(VectorEnvironment.IsCanonicalName(AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable));
            Assert.DoesNotContain("nntpd__", AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("backfiller__", AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Contract: BackFiller BindsNestedSection AndRootSecrets WithoutUsingRootBindSettings.
        /// </summary>

        [Fact]
        public void BackFiller_BindsNestedSection_AndRootSecrets_WithoutUsingRootBindSettings()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BindPort"] = "119";
            pairs["BindPortTls"] = "563";
            pairs["BindAddress:0"] = "203.0.113.10";
            pairs["DnsSuffix"] = "root-must-not-bind.example";
            pairs["AcmeDirectoryUrl"] = "https://root-must-not-bind.example/directory";

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(pairs)
                .Build();

            var identity = new BackFillerOptions();
            configuration.GetSection(BackFillerOptions.SectionName).Bind(identity);
            var acme = new AcmeCloudflareOptions();
            BackFillerAcmeCloudflareOptionsAdapter.Apply(acme, identity, configuration);

            Assert.Equal(string.Empty, acme.CloudFlareApiKey);
            Assert.Equal(string.Empty, acme.AcmeCertificatePassword);
            Assert.Equal(string.Empty, acme.AcmeEmail);
            Assert.NotEqual(BackFillerTestOptions.SecretToken, acme.CloudFlareApiKey);
            Assert.Equal("0123456789abcdef0123456789abcdef", acme.CloudFlareZoneId);
            Assert.Equal(1190, acme.BindPortTls);
            Assert.NotEqual(119, acme.BindPortTls);
            Assert.Equal(["127.0.0.1"], acme.BindAddress);
            Assert.Equal("usenet.ninja", acme.DnsSuffix);
            Assert.Equal(BackFillerOptions.DefaultAcmeDirectoryUrl, acme.AcmeDirectoryUrl);
            Assert.DoesNotContain("203.0.113.10", acme.BindAddress);
            Assert.NotEqual("root-must-not-bind.example", acme.DnsSuffix);
        }

        /// <summary>
        /// Contract: BackFillerHost RegistersSharedAcmeCloudflareOptions WithoutNewsSan.
        /// </summary>

        [Fact]
        public void BackFillerHost_RegistersSharedAcmeCloudflareOptions_WithoutNewsSan()
        {
            var builder = Host.CreateApplicationBuilder([]);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<VectorNNTPD.Common.Cloudflare.ICloudflareDnsReconciler>(
                new VectorNNTPD.BackFillerService.Tests.TestDoubles.NoOpCloudflareDnsReconciler());
            builder.ConfigureBackFillerPlatformHosting();
            builder.Configuration.AddInMemoryCollection(BackFillerTestOptions.CreateValidConfigurationPairs());
            builder.AddBackFillerHosting();

            using var host = builder.Build();
            var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
            Assert.False(acme.IncludeNewsHostnameInCertificate);
            Assert.Equal(1190, acme.BindPortTls);
            Assert.Equal(string.Empty, acme.CloudFlareApiKey);
            Assert.Equal(string.Empty, acme.AcmeCertificatePassword);
            Assert.Equal(string.Empty, acme.AcmeEmail);
            var rabbit = host.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
            Assert.Null(rabbit.Username);
            Assert.Null(rabbit.Password);
            Assert.Equal("backfiller01.usenet.ninja", acme.Fqdn);
            Assert.Equal(
                ["backfiller01.usenet.ninja"],
                CertificateIdentities.ForFqdn(acme.Fqdn, acme.IncludeNewsHostnameInCertificate));
        }
    }
}
