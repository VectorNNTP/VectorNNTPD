using System.Net;
using Microsoft.Extensions.Options;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.StorageService.Tests.Configuration
{

    public sealed class StorageServerOptionsFqdnTests
    {
        [Fact]
        public void ServerId_is_required()
        {
            var options = StorageServerTestOptions.CreateValid();
            options.ServerId = null;
            var result = new StorageServerOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("ServerId", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(256)]
        public void ServerId_out_of_range_fails(int serverId)
        {
            var options = StorageServerTestOptions.CreateValid();
            options.ServerId = serverId;
            var result = new StorageServerOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
        }

        [Fact]
        public void BindAddress_defaults_to_wildcard_when_omitted()
        {
            var options = StorageServerTestOptions.CreateValid();
            options.BindAddress = null;
            var runtime = StorageServerRuntimeOptionsFactory.Create(options);
            Assert.Equal(["*"], runtime.BindAddressTokens);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(65536)]
        public void BindPort_out_of_range_fails(int port)
        {
            var options = StorageServerTestOptions.CreateValid();
            options.BindPort = port;
            var result = new StorageServerOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("BindPort", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(65536)]
        public void BindPortTls_must_be_1_to_65535(int? port)
        {
            var options = StorageServerTestOptions.CreateValid();
            options.BindPortTls = port;
            var result = new StorageServerOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("BindPortTls", StringComparison.Ordinal));
        }

        [Fact]
        public void DnsSuffix_is_empty_until_nntpsharedconfig_publishes_it()
        {
            var options = new StorageServerOptions { ServerId = 1 };
            Assert.Equal(string.Empty, options.DnsSuffix);
            Assert.Equal(string.Empty, options.Fqdn);
        }

        [Theory]
        [InlineData(1, "cache01.usenet.ninja")]
        [InlineData(9, "cache09.usenet.ninja")]
        [InlineData(12, "cache12.usenet.ninja")]
        [InlineData(255, "cache255.usenet.ninja")]
        public void Fqdn_uses_cache_prefix_and_two_or_more_digit_id(int serverId, string expected)
        {
            var options = StorageServerTestOptions.CreateValid();
            options.ServerId = serverId;
            Assert.Equal(expected, options.Fqdn);
            Assert.Equal(expected, StorageServerRuntimeOptionsFactory.Create(options).Fqdn);
        }

        [Fact]
        public void Empty_DnsSuffix_is_not_validated_from_json()
        {
            var options = StorageServerTestOptions.CreateValid();
            options.DnsSuffix = " ";
            options.CloudFlareZoneId = " ";
            options.AcmeDirectoryUrl = string.Empty;
            options.AcmeRenewalThresholdDays = 0;
            var result = new StorageServerOptionsValidator().Validate(null, options);
            Assert.True(result.Succeeded);
        }
    }

    public sealed class StorageServerBindAddressValidationTests
    {
        [Fact]
        public void Explicit_address_not_on_nic_fails_acme_validator()
        {
            var options = StorageServerTestOptions.CreateValid();
            options.BindAddress = ["198.18.0.99"];
            var acme = StorageServerTestOptions.CreateValidAcme(options);
            acme.BindAddress = ["198.18.0.99"];
            var result = new AcmeCloudflareOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: false))
                .Validate(null, acme);
            Assert.True(result.Failed);
        }

        [Fact]
        public void Wildcard_bind_address_passes_acme_validator()
        {
            var options = StorageServerTestOptions.CreateValid();
            options.BindAddress = ["*"];
            var acme = StorageServerTestOptions.CreateValidAcme(options);
            acme.BindAddress = ["*"];
            var result = new AcmeCloudflareOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: false))
                .Validate(null, acme);
            Assert.False(result.Failed);
        }

        [Fact]
        public void Assigned_explicit_address_passes()
        {
            var ip = IPAddress.Parse("198.18.0.10");
            var options = StorageServerTestOptions.CreateValid();
            options.BindAddress = ["198.18.0.10"];
            var acme = StorageServerTestOptions.CreateValidAcme(options);
            acme.BindAddress = ["198.18.0.10"];
            var result = new AcmeCloudflareOptionsValidator(new AssignedLocalIpAddressAssignee(ip))
                .Validate(null, acme);
            Assert.False(result.Failed);
        }
    }

    public sealed class StorageServerAcmeAdapterTests
    {
        [Fact]
        public void Adapter_sets_fqdn_only_sans_and_bind_port_zero()
        {
            var source = StorageServerTestOptions.CreateValid();
            source.BindPort = 0;
            source.ServerId = 12;
            var destination = new AcmeCloudflareOptions();
            StorageServerAcmeCloudflareOptionsAdapter.Apply(
                destination,
                source,
                StorageServerTestOptions.CreateValidConfiguration());

            Assert.Equal(string.Empty, destination.Fqdn);
            Assert.Equal("cache12.usenet.ninja", source.Fqdn);
            Assert.False(destination.IncludeNewsHostnameInCertificate);
            Assert.Equal(0, destination.BindPort);
            Assert.Equal(source.BindPortTls, destination.BindPortTls);

            var names = VectorNNTPD.Common.Acme.CertificateIdentities.ForFqdn(
                source.Fqdn,
                destination.IncludeNewsHostnameInCertificate);
            Assert.Equal(["cache12.usenet.ninja"], names);
            Assert.DoesNotContain("news.usenet.ninja", names);
        }

        [Fact]
        public void Tls_only_validator_rejects_zero_bind_port_tls()
        {
            var acme = StorageServerTestOptions.CreateValidAcme();
            acme.BindPortTls = 0;
            var result = new TlsOnlyAcmeCloudflareOptionsValidator().Validate(Options.DefaultName, acme);
            Assert.True(result.Failed);
            Assert.Contains("TLS-only", result.Failures!.Single(), StringComparison.Ordinal);
        }
    }
}
