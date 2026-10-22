using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.StorageService.Tests.Cloudflare
{

    public sealed class StorageServerCloudflareHostnameTests
    {
        [Theory]
        [InlineData(1, "cache01.usenet.ninja")]
        [InlineData(9, "cache09.usenet.ninja")]
        [InlineData(255, "cache255.usenet.ninja")]
        public void Options_fqdn_is_expected_cache_hostname(int serverId, string expected)
        {
            var source = StorageServerTestOptions.CreateValid();
            source.ServerId = serverId;
            Assert.Equal(expected, source.Fqdn);
        }

        [Fact]
        public async Task Reconciler_creates_a_aaaa_dns_only_for_cache_fqdn()
        {
            var client = new FakeCloudflareDnsClient();
            var options = StorageServerTestOptions.CreateValidAcme();
            options.Fqdn = "cache01.usenet.ninja";
            var reconciler = new CloudflareDnsReconciler(
                client,
                Options.Create(options),
                NullLogger<CloudflareDnsReconciler>.Instance);
            var desired = new ResolvedBindAddresses(
            [
                IPAddress.Parse("198.18.0.10"),
                IPAddress.Parse("2001:db8::10"),
            ]);

            await reconciler.ReconcileAsync(
                "zone",
                "cache01.usenet.ninja",
                desired,
                CancellationToken.None);

            var snapshot = client.Snapshot();
            Assert.Contains(
                snapshot,
                r => r.Type == "A"
                     && string.Equals(r.Name, "cache01.usenet.ninja", StringComparison.OrdinalIgnoreCase)
                     && r.Content == "198.18.0.10"
                     && r.Proxied == false);
            Assert.Contains(
                snapshot,
                r => r.Type == "AAAA"
                     && string.Equals(r.Name, "cache01.usenet.ninja", StringComparison.OrdinalIgnoreCase)
                     && r.Content == "2001:db8::10"
                     && r.Proxied == false);
            Assert.All(snapshot, r => Assert.False(r.Proxied));
        }

        [Fact]
        public async Task Reconciler_normalizes_duplicate_desired_addresses()
        {
            var client = new FakeCloudflareDnsClient();
            var options = StorageServerTestOptions.CreateValidAcme();
            options.Fqdn = "cache12.usenet.ninja";
            var reconciler = new CloudflareDnsReconciler(
                client,
                Options.Create(options),
                NullLogger<CloudflareDnsReconciler>.Instance);
            var ipv4 = IPAddress.Parse("198.18.0.10");
            var desired = new ResolvedBindAddresses([ipv4, ipv4]);

            await reconciler.ReconcileAsync(
                "zone",
                "cache12.usenet.ninja",
                desired,
                CancellationToken.None);

            Assert.Equal(1, client.Snapshot().Count(r => r.Type == "A"));
        }
    }
}
