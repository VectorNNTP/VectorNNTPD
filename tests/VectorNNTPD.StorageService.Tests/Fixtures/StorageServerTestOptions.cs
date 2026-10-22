using System.Net;
using Microsoft.Extensions.Configuration;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.StorageService.Tests.Fixtures
{

    internal static class StorageServerTestOptions
    {
        internal const string SecretToken = "super-secret-cloudflare-token-xyz";
        internal const string SecretPfx = "super-secret-pfx-password-xyz";

        internal static StorageServerOptions CreateValid()
        {
            return new StorageServerOptions
            {
                ServerId = 1,
                DnsSuffix = "usenet.ninja",
                CloudFlareZoneId = "0123456789abcdef0123456789abcdef",
                BindAddress = ["127.0.0.1"],
                BindPort = 0,
                BindPortTls = 1191,
                AcmeDirectoryUrl = StorageServerOptions.DefaultAcmeDirectoryUrl,
                AcmeRenewalThresholdDays = StorageServerOptions.DefaultAcmeRenewalThresholdDays,
                AcmeStateDir = "certs/",
                LogDir = "/logs",
                CertificateDirectory = "certs",
                ApplicationName = "VectorNNTPD.StorageService",
                GracefulShutdownTimeout = TimeSpan.FromSeconds(30),
            };
        }

        internal static Dictionary<string, string?> CreateValidConfigurationPairs()
        {
            var logDir = Path.Combine(Path.GetTempPath(), "vectornntp-storageserver-testhost-logs", Guid.NewGuid().ToString("N"));
            var acmeDir = Path.Combine(Path.GetTempPath(), "vectornntp-storageserver-testhost-acme", Guid.NewGuid().ToString("N"));
            var spoolDir = Path.Combine(Path.GetTempPath(), "vectornntp-storageserver-testhost-spool", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(logDir);
            Directory.CreateDirectory(acmeDir);
            Directory.CreateDirectory(spoolDir);

            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["StorageServer:ServerId"] = "1",
                ["StorageServer:LogDir"] = logDir,
                ["StorageServer:BindAddress:0"] = "*",
                ["StorageServer:BindPort"] = "0",
                ["StorageServer:BindPortTls"] = "1191",
                ["StorageServer:AcmeStateDir"] = acmeDir,
                ["StorageServer:CertificateDirectory"] = acmeDir,
                ["StorageServer:DbDir"] = Path.Combine(Path.GetTempPath(), "vectornntp-storageserver-db", Guid.NewGuid().ToString("N")),
                ["StorageServer:SegmentPools:Directories:0"] = spoolDir,
                ["StorageServer:GracefulShutdownTimeout"] = "00:00:30",
                ["ConnectionStrings:NntpDB"] = FakeNntpDbConnectionFactory.ConnectionString,
                ["RabbitMQ:Hosts:0"] = "127.0.0.1",
                ["RabbitMQ:Port"] = "5672",
                ["RabbitMQ:VirtualHost"] = "/",
                ["RabbitMQ:EnableSsl"] = "false",
            };
        }

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

        internal static AcmeCloudflareOptions CreateValidAcme(StorageServerOptions? options = null)
        {
            var identity = options ?? CreateValid();
            return new AcmeCloudflareOptions
            {
                BindAddress = identity.BindAddress is { Length: > 0 } ? identity.BindAddress : ["127.0.0.1"],
                BindPort = identity.BindPort,
                BindPortTls = identity.BindPortTls ?? 1191,
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

        internal static int GetFreeTcpPort()
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
    }

    internal sealed class FakeLocalIpAddressAssignee(bool assignAll) : ILocalIpAddressAssignee
    {
        public bool IsLocallyAssigned(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            return assignAll;
        }

        public IReadOnlyList<IPAddress> GetAssignedUnicastAddresses() =>
            assignAll ? [IPAddress.Parse("198.18.0.10"), IPAddress.Parse("2001:db8::10")] : [];
    }

    internal sealed class AssignedLocalIpAddressAssignee(params IPAddress[] assigned) : ILocalIpAddressAssignee
    {
        private readonly HashSet<IPAddress> _assigned = [.. assigned];

        public bool IsLocallyAssigned(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            return _assigned.Contains(address);
        }

        public IReadOnlyList<IPAddress> GetAssignedUnicastAddresses() => _assigned.ToArray();
    }
}
