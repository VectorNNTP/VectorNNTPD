using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Hosting;
using VectorNNTPD.StorageService.Logging;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Networking.Listeners;

namespace VectorNNTPD.StorageService.Tests.Configuration
{

    /// <summary>
    /// Regression coverage for explicit BindAddress propagation (listener + Cloudflare),
    /// including the Development.json array-overlay failure mode that replaced index 0 with <c>*</c>.
    /// </summary>
    public sealed class StorageServerBindAddressPropagationTests
    {
        private const string ExplicitIpv4 = "198.18.0.66";
        private const string ExplicitIpv6 = "2c0f:f030:1442:501:198:18:0:66";

        [Fact]
        public void Production_appsettings_declares_explicit_bind_addresses()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FindAppsettings("VectorNNTPD.StorageService.json")));
            var bind = doc.RootElement.GetProperty("StorageServer").GetProperty("BindAddress");
            Assert.Equal(2, bind.GetArrayLength());
            Assert.Equal(ExplicitIpv4, bind[0].GetString());
            Assert.Equal(ExplicitIpv6, bind[1].GetString());
            Assert.Equal(0, doc.RootElement.GetProperty("StorageServer").GetProperty("BindPort").GetInt32());
            Assert.Equal(1191, doc.RootElement.GetProperty("StorageServer").GetProperty("BindPortTls").GetInt32());
        }

        [Fact]
        public void Development_appsettings_does_not_override_BindAddress()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FindAppsettings("VectorNNTPD.StorageService.Development.json")));
            Assert.False(
                doc.RootElement.TryGetProperty("StorageServer", out var storage)
                && storage.TryGetProperty("BindAddress", out _),
                "VectorNNTPD.StorageService.Development.json must not set StorageServer:BindAddress; JSON config merges arrays by index and a Development '*' at [0] leaves production [1] in place, producing wildcard listener/Cloudflare expansion.");
        }

        [Fact]
        public void Layered_appsettings_preserve_explicit_BindAddress_under_Development()
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(FindAppsettings("VectorNNTPD.StorageService.json"), optional: false, reloadOnChange: false)
                .AddJsonFile(FindAppsettings("VectorNNTPD.StorageService.Development.json"), optional: false, reloadOnChange: false)
                .Build();

            var bound = new StorageServerOptions();
            configuration.GetSection(StorageServerOptions.SectionName).Bind(bound);

            Assert.NotNull(bound.BindAddress);
            Assert.Equal([ExplicitIpv4, ExplicitIpv6], bound.BindAddress);
            Assert.Equal(0, bound.BindPort);
            Assert.Equal(1191, bound.BindPortTls);
            Assert.DoesNotContain(bound.BindAddress, StorageServerOptions.IsBindAddressWildcard);
        }

        [Fact]
        public void Development_index0_wildcard_overlay_would_corrupt_BindAddress_array()
        {
            // Documents the historical failure mode: Development BindAddress:["*"] only replaces index 0.
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(FindAppsettings("VectorNNTPD.StorageService.json"), optional: false, reloadOnChange: false)
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["StorageServer:BindAddress:0"] = "*",
                    })
                .Build();

            var bound = new StorageServerOptions();
            configuration.GetSection(StorageServerOptions.SectionName).Bind(bound);

            Assert.NotNull(bound.BindAddress);
            Assert.Equal(["*", ExplicitIpv6], bound.BindAddress);
        }

        [Fact]
        public void Runtime_options_preserve_explicit_addresses_with_BindPort_zero()
        {
            var options = StorageServerTestOptions.CreateValid();
            options.BindAddress = [ExplicitIpv4, ExplicitIpv6];
            options.BindPort = 0;
            options.BindPortTls = 1191;

            var runtime = StorageServerRuntimeOptionsFactory.Create(options);

            Assert.Equal([ExplicitIpv4, ExplicitIpv6], runtime.BindAddressTokens);
            Assert.Equal(0, runtime.BindPort);
            Assert.Equal(1191, runtime.BindPortTls);
            Assert.Equal(
                [IPAddress.Parse(ExplicitIpv4), IPAddress.Parse(ExplicitIpv6)],
                runtime.CanonicalBindAddresses);
            Assert.DoesNotContain(runtime.BindAddressTokens, StorageServerOptions.IsBindAddressWildcard);
        }

        [Fact]
        public void Adapter_copies_explicit_BindAddress_and_does_not_force_wildcard_when_BindPort_is_zero()
        {
            var source = StorageServerTestOptions.CreateValid();
            source.BindAddress = [ExplicitIpv4, ExplicitIpv6];
            source.BindPort = 0;
            source.BindPortTls = 1191;

            var destination = new AcmeCloudflareOptions
            {
                DnsSuffix = "keep.example",
                Fqdn = "cache01.keep.example",
                CloudFlareZoneId = "kept-zone",
                AcmeDirectoryUrl = "https://keep.example/directory",
                AcmeRenewalThresholdDays = 3,
            };
            StorageServerAcmeCloudflareOptionsAdapter.Apply(
                destination,
                source,
                StorageServerTestOptions.CreateValidConfiguration());

            Assert.Equal([ExplicitIpv4, ExplicitIpv6], destination.BindAddress);
            Assert.Equal(0, destination.BindPort);
            Assert.Equal(1191, destination.BindPortTls);
            Assert.Equal("cache01.keep.example", destination.Fqdn);
            Assert.Equal("keep.example", destination.DnsSuffix);
            Assert.Equal("kept-zone", destination.CloudFlareZoneId);
            Assert.Equal("https://keep.example/directory", destination.AcmeDirectoryUrl);
            Assert.Equal(3, destination.AcmeRenewalThresholdDays);
            Assert.False(destination.IncludeNewsHostnameInCertificate);
        }

        [Fact]
        public void ListenEndpointPlanner_plans_exactly_two_explicit_tls_endpoints()
        {
            var bindings = ListenEndpointPlanner.Plan([ExplicitIpv4, ExplicitIpv6], port: 1191);

            Assert.Equal(2, bindings.Count);
            Assert.Contains(
                bindings,
                static b => b.Address.Equals(IPAddress.Parse(ExplicitIpv4)) && b.Port == 1191 && !b.DualMode);
            Assert.Contains(
                bindings,
                static b => b.Address.Equals(IPAddress.Parse(ExplicitIpv6)) && b.Port == 1191 && !b.DualMode);
            Assert.DoesNotContain(bindings, static b => b.Address.Equals(IPAddress.IPv6Any));
            Assert.DoesNotContain(bindings, static b => b.Address.Equals(IPAddress.Any));
        }

        [Fact]
        public void BindAddressResolver_publishes_only_explicit_addresses_not_all_local_nics()
        {
            var localOnlyExtra = IPAddress.Parse("2c0f:f030:1442:501:dead:beef:0:1");
            var assignee = new AssignedLocalIpAddressAssignee(
                IPAddress.Parse(ExplicitIpv4),
                IPAddress.Parse(ExplicitIpv6),
                localOnlyExtra,
                IPAddress.Parse("198.18.0.99"));

            var options = new AcmeCloudflareOptions
            {
                BindAddress = [ExplicitIpv4, ExplicitIpv6],
                BindPort = 0,
                BindPortTls = 1191,
                Fqdn = "cache01.usenet.ninja",
            };

            var resolved = new BindAddressResolver(assignee, NullLogger<BindAddressResolver>.Instance)
                .Resolve(options);

            Assert.Equal(
                [IPAddress.Parse(ExplicitIpv4)],
                resolved.IPv4);
            Assert.Equal(
                [IPAddress.Parse(ExplicitIpv6)],
                resolved.IPv6);
            Assert.DoesNotContain(resolved.All, a => a.Equals(localOnlyExtra));
            Assert.DoesNotContain(resolved.All, static a => a.Equals(IPAddress.Parse("198.18.0.99")));
        }

        [Fact]
        public async Task Cloudflare_reconcile_creates_exactly_one_A_and_one_AAAA_for_explicit_addresses()
        {
            var client = new FakeCloudflareDnsClient();
            var options = StorageServerTestOptions.CreateValidAcme();
            options.Fqdn = "cache01.usenet.ninja";
            options.BindAddress = [ExplicitIpv4, ExplicitIpv6];
            options.BindPort = 0;
            options.BindPortTls = 1191;

            var assignee = new AssignedLocalIpAddressAssignee(
                IPAddress.Parse(ExplicitIpv4),
                IPAddress.Parse(ExplicitIpv6),
                IPAddress.Parse("2c0f:f030:1442:501:cafe:0:0:1"),
                IPAddress.Parse("198.18.0.77"));
            var desired = new BindAddressResolver(assignee, NullLogger<BindAddressResolver>.Instance)
                .Resolve(options);

            var reconciler = new CloudflareDnsReconciler(
                client,
                Options.Create(options),
                NullLogger<CloudflareDnsReconciler>.Instance);

            await reconciler.ReconcileAsync(
                "zone",
                "cache01.usenet.ninja",
                desired,
                CancellationToken.None);

            var snapshot = client.Snapshot();
            Assert.Equal(1, snapshot.Count(static r => r.Type == "A"));
            Assert.Equal(1, snapshot.Count(static r => r.Type == "AAAA"));
            Assert.Contains(
                snapshot,
                static r => r.Type == "A" && r.Content == ExplicitIpv4 && r.Name == "cache01.usenet.ninja");
            Assert.Contains(
                snapshot,
                static r => r.Type == "AAAA" && r.Content == ExplicitIpv6 && r.Name == "cache01.usenet.ninja");
        }

        [Fact]
        public void Wildcard_BindAddress_still_expands_local_addresses_for_Cloudflare()
        {
            var assignee = new AssignedLocalIpAddressAssignee(
                IPAddress.Parse(ExplicitIpv4),
                IPAddress.Parse(ExplicitIpv6),
                IPAddress.Parse("198.18.0.77"));

            var options = new AcmeCloudflareOptions
            {
                BindAddress = ["*"],
                BindPort = 0,
                BindPortTls = 1191,
                Fqdn = "cache01.usenet.ninja",
            };

            var resolved = new BindAddressResolver(assignee, NullLogger<BindAddressResolver>.Instance)
                .Resolve(options);

            Assert.Equal(2, resolved.IPv4.Count);
            Assert.Single(resolved.IPv6);

            var bindings = ListenEndpointPlanner.Plan(["*"], port: 1191);
            Assert.Single(bindings);
            Assert.Equal(IPAddress.IPv6Any, bindings[0].Address);
            Assert.True(bindings[0].DualMode);
        }

        [Fact]
        public void Host_composition_binds_explicit_addresses_into_runtime_and_acme_options()
        {
            var builder = Host.CreateApplicationBuilder([]);
            var pairs = StorageServerTestOptions.CreateValidConfigurationPairs();
            pairs["StorageServer:BindAddress:0"] = ExplicitIpv4;
            pairs["StorageServer:BindAddress:1"] = ExplicitIpv6;
            pairs["StorageServer:BindPort"] = "0";
            pairs["StorageServer:BindPortTls"] = "1191";
            builder.Configuration.AddInMemoryCollection(pairs);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(
                new AssignedLocalIpAddressAssignee(
                    IPAddress.Parse(ExplicitIpv4),
                    IPAddress.Parse(ExplicitIpv6)));
            builder.Services.AddSingleton<ICloudflareDnsReconciler>(new NoOpCloudflareDnsReconciler());
            builder.ConfigureStorageServerLogging();
            builder.ConfigureStorageServerPlatformHosting();
            builder.AddStorageServerHosting();
            builder.Services.AddSingleton<IRabbitMqConnectionFactory, FakeStorageServerRabbitMqConnectionFactory>();

            using var host = builder.Build();

            var storage = host.Services.GetRequiredService<IOptions<StorageServerOptions>>().Value;
            Assert.NotNull(storage.BindAddress);
            Assert.Equal([ExplicitIpv4, ExplicitIpv6], storage.BindAddress);
            Assert.Equal(0, storage.BindPort);

            var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
            Assert.Equal([ExplicitIpv4, ExplicitIpv6], acme.BindAddress);
            Assert.Equal(1191, acme.BindPortTls);
            Assert.Equal(string.Empty, acme.DnsSuffix);
            Assert.Equal(string.Empty, acme.CloudFlareZoneId);
            Assert.Equal(string.Empty, acme.AcmeEmail);
            Assert.Equal(string.Empty, acme.CloudFlareApiKey);

            var planned = ListenEndpointPlanner.Plan(acme.BindAddress, acme.BindPortTls);
            Assert.Equal(2, planned.Count);
            Assert.DoesNotContain(planned, static b => b.Address.Equals(IPAddress.IPv6Any));

            var resolved = host.Services.GetRequiredService<IBindAddressResolver>().Resolve(acme);
            Assert.Single(resolved.IPv4);
            Assert.Single(resolved.IPv6);
            Assert.Equal(ExplicitIpv4, IpAddressEligibility.ToDnsContent(resolved.IPv4[0]));
            Assert.Equal(ExplicitIpv6, IpAddressEligibility.ToDnsContent(resolved.IPv6[0]));
        }

        private static string FindAppsettings(string fileName)
        {
            var start = new DirectoryInfo(AppContext.BaseDirectory);
            for (var dir = start; dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", "VectorNNTPD.StorageService", fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException($"Could not locate {fileName}.");
        }
    }
}
