using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;
using System.Net;

namespace VectorNNTPD.BackFillerService.Tests.Fixtures
{
    /// <summary>Valid BackFiller, RabbitMQ, and ACME option snapshots for binding and host tests.</summary>
    internal static class BackFillerTestOptions
    {
        /// <summary>Non-production RabbitMQ password used in configuration pairs.</summary>
        internal const string SecretPassword = "super-secret-password-xyz";
        /// <summary>Non-production Cloudflare API token used in configuration pairs.</summary>
        internal const string SecretToken = "super-secret-cloudflare-token-xyz";
        /// <summary>Non-production ACME PFX password used in configuration pairs.</summary>
        internal const string SecretPfx = "super-secret-pfx-password-xyz";

        /// <summary>
        /// Options that pass identity validation: server 1, usenet.ninja, loopback TLS port 1190, and the default ACME directory.
        /// </summary>

        internal static BackFillerOptions CreateValid()
        {
            return new BackFillerOptions
            {
                ServerId = 1,
                DnsSuffix = "usenet.ninja",
                CloudFlareZoneId = "0123456789abcdef0123456789abcdef",
                BindAddress = ["127.0.0.1"],
                BindPortTls = 1190,
                AcmeDirectoryUrl = BackFillerOptions.DefaultAcmeDirectoryUrl,
                AcmeRenewalThresholdDays = BackFillerOptions.DefaultAcmeRenewalThresholdDays,
                AcmeStateDir = "certs/",
                CertificateDirectory = "certs",
            };
        }

        /// <summary>
        /// Local plaintext RabbitMQ options for 127.0.0.1 using <see cref="SecretPassword"/>.
        /// </summary>

        internal static RabbitMqOptions CreateValidRabbitMq() =>
            new()
            {
                Hosts = ["127.0.0.1"],
                Username = "nntparticles",
                Password = SecretPassword,
                EnableSsl = false,
            };

        /// <summary>In-memory configuration key/value pairs that pass production validators.</summary>
        internal static Dictionary<string, string?> CreateValidConfigurationPairs()
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["BackFiller:ServerId"] = "1",
                ["BackFiller:DnsSuffix"] = "usenet.ninja",
                ["BackFiller:CloudFlareZoneId"] = "0123456789abcdef0123456789abcdef",
                ["BackFiller:Logging:File:LogDir"] = "logs",
                ["BackFiller:BindAddress:0"] = "127.0.0.1",
                ["BackFiller:BindPortTls"] = "1190",
                ["BackFiller:AcmeDirectoryUrl"] = BackFillerOptions.DefaultAcmeDirectoryUrl,
                [AcmeCloudflareOptions.AcmeAccountConfigurationKey] = "security@usenet.ninja",
                ["BackFiller:AcmeRenewalThresholdDays"] = "30",
                ["BackFiller:AcmeStateDir"] = "certs/",
                ["RabbitMQ:Hosts:0"] = "127.0.0.1",
                ["RabbitMQ:Username"] = "nntparticles",
                ["RabbitMQ:Password"] = SecretPassword,
                ["RabbitMQ:EnableSsl"] = "false",
                ["AcmeCertificatePassword"] = SecretPfx,
                ["CloudFlareApiKey"] = SecretToken,
                ["BackFiller:Shutdown:GracePeriodSeconds"] = "45",
                ["ConnectionStrings:NntpDB"] = "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz",
            };
        }

        /// <summary>
        /// In-memory configuration from <see cref="CreateValidConfigurationPairs"/>, with <paramref name="overlays"/> replacing matching keys.
        /// </summary>

        internal static IConfiguration CreateValidConfiguration(IReadOnlyDictionary<string, string?>? overlays = null)
        {
            var pairs = CreateValidConfigurationPairs();
            if (overlays is not null)
            {
                foreach (var (key, value) in overlays)
                {
                    pairs[key] = value;
                }
            }

            return new ConfigurationBuilder()
                .AddInMemoryCollection(pairs)
                .Build();
        }

        /// <summary>
        /// NntpDB options pointing at 127.0.0.1 database nntp. The password is a fixture, not a production secret.
        /// </summary>

        internal static NntpDbOptions CreateValidNntpDb()
        {
            return new NntpDbOptions
            {
                ConnectionString = "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz",
            };
        }

        /// <summary>
        /// ACME options copied from <paramref name="options"/>, or from <see cref="CreateValid"/>, plus fixture email, PFX password, and API token.
        /// </summary>

        internal static AcmeCloudflareOptions CreateValidAcme(BackFillerOptions? options = null)
        {
            var identity = options ?? CreateValid();
            return new AcmeCloudflareOptions
            {
                BindAddress = identity.BindAddress is { Length: > 0 } ? identity.BindAddress : ["127.0.0.1"],
                BindPortTls = identity.BindPortTls ?? 1190,
                Fqdn = identity.Fqdn,
                IncludeNewsHostnameInCertificate = false,
                AcmeDirectoryUrl = identity.AcmeDirectoryUrl,
                AcmeEmail = "security@usenet.ninja",
                AcmeRenewalThresholdDays = identity.AcmeRenewalThresholdDays,
                AcmeCertificatePassword = SecretPfx,
                AcmeStateDir = string.IsNullOrWhiteSpace(identity.AcmeStateDir) ? identity.CertificateDirectory : identity.AcmeStateDir,
                CloudFlareApiKey = SecretToken,
                CloudFlareZoneId = identity.CloudFlareZoneId,
                DnsSuffix = identity.DnsSuffix,
            };
        }

        /// <summary>
        /// Validator with 64 GiB of memory unless <paramref name="memory"/> is supplied. <paramref name="assignee"/> is ignored.
        /// </summary>

        internal static BackFillerOptionsValidator CreateValidator(
            ILocalIpAddressAssignee? assignee = null,
            IPhysicalMemoryProvider? memory = null,
            RabbitMqOptions? rabbitMq = null)
        {
            _ = assignee;
            return new BackFillerOptionsValidator(
                memory ?? new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024),
                Options.Create(rabbitMq ?? CreateValidRabbitMq()));
        }
    }

    /// <summary>
    /// ILocalIpAddressAssignee that assigns all or no addresses based on assignAll.
    /// </summary>

    internal sealed class FakeLocalIpAddressAssignee(bool assignAll) : ILocalIpAddressAssignee
    {
        /// <summary>
        /// Returns the constructor flag for every non-null address. Does not inspect the host's interfaces.
        /// </summary>

        public bool IsLocallyAssigned(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            return assignAll;
        }

        /// <summary>Documentation-only addresses 198.18.0.10 and 2001:db8::10 when assigning, otherwise none.</summary>
        public IReadOnlyList<IPAddress> GetAssignedUnicastAddresses() =>
            assignAll ? [IPAddress.Parse("198.18.0.10"), IPAddress.Parse("2001:db8::10")] : [];
    }

    /// <summary>
    /// Returns a fixed total physical memory byte count for validator tests.
    /// </summary>

    internal sealed class FakePhysicalMemoryProvider(long totalBytes) : IPhysicalMemoryProvider
    {
        /// <summary>
        /// Returns the constructor byte count. Does not query the operating system.
        /// </summary>

        public long GetTotalPhysicalMemoryBytes() => totalBytes;
    }

    /// <summary>
    /// GetTotalPhysicalMemoryBytes throws to exercise retention validation when OS memory is unknown.
    /// </summary>

    internal sealed class FailingPhysicalMemoryProvider : IPhysicalMemoryProvider
    {
        /// <summary>
        /// Always throws, so retention validation can observe an unknown memory size.
        /// </summary>

        public long GetTotalPhysicalMemoryBytes() =>
            throw new InvalidOperationException("Total physical memory could not be determined.");
    }
}
