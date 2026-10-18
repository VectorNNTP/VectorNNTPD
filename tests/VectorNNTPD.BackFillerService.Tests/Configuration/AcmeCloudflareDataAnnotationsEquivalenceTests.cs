using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Configuration
{
    /// <summary>
    /// Proves the former <c>ValidateDataAnnotations()</c> rules on
    /// <see cref="AcmeCloudflareOptions"/> still fail startup via the registered
    /// <see cref="IValidateOptions{TOptions}"/> validators (AOT-safe path).
    /// </summary>
    public sealed class AcmeCloudflareDataAnnotationsEquivalenceTests
    {
        /// <summary>
        /// Contract: Host validate on start succeeds for valid configuration.
        /// </summary>

        [Fact]
        public void Host_validate_on_start_succeeds_for_valid_configuration()
        {
            using var host = CreateHost(BackFillerTestOptions.CreateValidConfigurationPairs());
            var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
            Assert.Equal(string.Empty, acme.CloudFlareApiKey);
            Assert.Equal(string.Empty, acme.AcmeCertificatePassword);
            Assert.Equal(string.Empty, acme.AcmeEmail);
            Assert.False(string.IsNullOrWhiteSpace(acme.CloudFlareZoneId));
            Assert.InRange(acme.AcmeRenewalThresholdDays, 1, 90);
            Assert.InRange(acme.BindPortTls, 1, 65535);
        }

        /// <summary>
        /// Contract: Host validate on start does not require the shared api key.
        /// </summary>

        [Fact]
        public void Host_validate_on_start_does_not_require_the_shared_api_key()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["CloudFlareApiKey"] = "";
            pairs["AcmeCertificatePassword"] = "";
            pairs[AcmeCloudflareOptions.AcmeAccountConfigurationKey] = "";

            using var host = CreateHost(pairs);
            var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
            var rabbit = host.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
            Assert.Equal(string.Empty, acme.CloudFlareApiKey);
            Assert.Equal(string.Empty, acme.AcmeCertificatePassword);
            Assert.Equal(string.Empty, acme.AcmeEmail);
            Assert.Null(rabbit.Username);
            Assert.Null(rabbit.Password);
        }

        /// <summary>
        /// Contract: Host validate on start does not require the shared zone id.
        /// </summary>

        [Fact]
        public void Host_validate_on_start_does_not_require_the_shared_zone_id()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:CloudFlareZoneId"] = " ";

            using var host = CreateHost(pairs);
            var options = host.Services.GetRequiredService<IOptions<BackFillerOptions>>().Value;
            Assert.Equal(" ", options.CloudFlareZoneId);
        }

        /// <summary>
        /// Contract: Host validate on start does not reject shared renewal days.
        /// </summary>

        [Theory]
        [InlineData(0)]
        [InlineData(91)]
        public void Host_validate_on_start_does_not_reject_shared_renewal_days(int days)
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:AcmeRenewalThresholdDays"] = days.ToString();

            using var host = CreateHost(pairs);
            var options = host.Services.GetRequiredService<IOptions<BackFillerOptions>>().Value;
            Assert.Equal(days, options.AcmeRenewalThresholdDays);
        }

        /// <summary>
        /// Host build or options resolution fails validation before the process runs.
        /// </summary>

        [Theory]
        [InlineData(-1)]
        [InlineData(65536)]
        public void Host_validate_on_start_fails_when_bind_port_tls_outside_data_annotation_range(int port)
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:BindPortTls"] = port.ToString();

            var ex = Assert.Throws<OptionsValidationException>(() =>
            {
                using var host = CreateHost(pairs);
            });
            Assert.Contains(nameof(AcmeCloudflareOptions.BindPortTls), ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: Explicit validator rejects each former data annotation failure.
        /// </summary>

        [Fact]
        public void Explicit_validator_rejects_each_former_data_annotation_failure()
        {
            var assignee = new FakeLocalIpAddressAssignee(assignAll: true);
            var validator = new AcmeCloudflareOptionsValidator(assignee);

            var missingKey = BackFillerTestOptions.CreateValidAcme();
            missingKey.CloudFlareApiKey = string.Empty;
            Assert.True(validator.Validate(null, missingKey).Failed);

            var missingZone = BackFillerTestOptions.CreateValidAcme();
            missingZone.CloudFlareZoneId = string.Empty;
            Assert.True(validator.Validate(null, missingZone).Failed);

            var badRenewal = BackFillerTestOptions.CreateValidAcme();
            badRenewal.AcmeRenewalThresholdDays = 0;
            Assert.True(validator.Validate(null, badRenewal).Failed);

            var badPort = BackFillerTestOptions.CreateValidAcme();
            badPort.BindPortTls = 65536;
            Assert.True(validator.Validate(null, badPort).Failed);

            var valid = BackFillerTestOptions.CreateValidAcme();
            Assert.True(validator.Validate(null, valid).Succeeded);
            Assert.True(new TlsOnlyAcmeCloudflareOptionsValidator().Validate(null, valid).Succeeded);
        }

        /// <summary>
        /// Builds a BackFiller host with in-memory configuration and infrastructure fakes for validate-on-start assertions.
        /// </summary>
        /// <param name="pairs">Configuration key/value pairs merged before hosting is added.</param>
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
    }
}
