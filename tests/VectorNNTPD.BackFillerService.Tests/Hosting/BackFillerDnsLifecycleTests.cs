using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Listener;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Networking;
using System.Net.Sockets;
using System.Net;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.Hosting
{
    /// <summary>
    /// Regresses Cloudflare DNS reconciliation ordering in <see cref="VectorNNTPD.BackFillerService.Core.ApplicationServiceManager"/> and full-host startup:
    /// DNS runs before ACME and the cache listener, rollback removes only the BackFiller FQDN, and bind resolution runs once per start attempt.
    /// </summary>
    public sealed class BackFillerDnsLifecycleTests
    {
        /// <summary>FQDN that must never appear in reconcile or remove calls during these tests.</summary>
        private const string UnrelatedFqdn = "nntpd01.usenet.ninja";

        /// <summary>DNS reconciliation is registered as a shared Cloudflare application service, not a BackFiller-only hosted wrapper type.</summary>
        [Fact]
        public void Adapter_is_the_shared_common_type()
        {
            Assert.Equal(
                typeof(CloudflareDnsReconciliationService).Assembly,
                typeof(CloudflareDnsReconciliationApplicationService).Assembly);
            Assert.Equal(
                "VectorNNTPD.Common.Cloudflare",
                typeof(CloudflareDnsReconciliationApplicationService).Namespace);
            Assert.Null(
                typeof(BackFillerServiceCollectionExtensions).Assembly
                    .GetType("VectorNNTPD.BackFillerService.Hosting.CloudflareDnsReconciliationHostedService"));
        }

        /// <summary>
        /// When DNS reconcile throws, later application services never start.
        /// Ownership never becomes active, so the name is not removed; only ids created by the attempt are deleted.
        /// </summary>
        [Fact]
        public async Task Dns_startup_failure_does_not_start_later_services_and_cleans_up()
        {
            var harness = CreateDnsHarness();
            harness.Reconciler.ReconcileException = new CloudflareDnsException("reconcile failed");
            var order = new List<string>();
            var dns = new OrderRecordingService(harness.Adapter, order);
            var later = new OrderRecordingService("AcmeCertificate", order);
            var manager = CreateManager(dns, later);

            var thrown = await Assert.ThrowsAsync<CloudflareDnsException>(() => manager.StartAsync(CancellationToken.None));
            Assert.Equal("reconcile failed", thrown.Message);

            Assert.Equal(["start:CloudflareDnsReconciliation"], order);
            Assert.Equal(0, later.StartCount);
            Assert.Empty(manager.StartedServices);
            Assert.Equal([harness.Options.Fqdn], harness.Reconciler.ReconcileCalls.Select(static call => call.Fqdn));
            Assert.Empty(harness.Reconciler.RemoveCalls);
            Assert.Equal([(harness.Options.CloudFlareZoneId, harness.Options.Fqdn, 0)], harness.Reconciler.DeleteCalls);
            Assert.Equal(1, harness.Resolver.ResolveCount);
        }

        /// <summary>After DNS and ACME start, an ACME failure stops DNS and leaves the listener unstarted; remove targets only the configured BackFiller FQDN.</summary>
        [Fact]
        public async Task Dns_succeeds_then_acme_failure_rolls_back_exact_backfiller_fqdn()
        {
            var harness = CreateDnsHarness();
            var order = new List<string>();
            var dns = new OrderRecordingService(harness.Adapter, order);
            var acme = new OrderRecordingService(
                "AcmeCertificate",
                order,
                startException: new InvalidOperationException("acme failed"));
            var listener = new OrderRecordingService("CacheListener", order);
            var manager = CreateManager(dns, acme, listener);

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));

            Assert.Equal(
                ["start:CloudflareDnsReconciliation", "start:AcmeCertificate", "stop:CloudflareDnsReconciliation"],
                order);
            Assert.Equal(0, listener.StartCount);
            Assert.Equal(1, harness.Resolver.ResolveCount);
            Assert.Equal([harness.Options.Fqdn], harness.Reconciler.ReconcileCalls.Select(static call => call.Fqdn));
            Assert.Equal([harness.Options.Fqdn], harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
            Assert.DoesNotContain(UnrelatedFqdn, harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
            Assert.Empty(manager.StartedServices);
        }

        /// <summary>Listener failure after DNS and ACME succeed rolls back in reverse order (listener, ACME, DNS) without touching unrelated FQDNs.</summary>
        [Fact]
        public async Task Dns_succeeds_then_listener_failure_rolls_back_exact_backfiller_fqdn()
        {
            var harness = CreateDnsHarness();
            var order = new List<string>();
            var dns = new OrderRecordingService(harness.Adapter, order);
            var acme = new OrderRecordingService("AcmeCertificate", order);
            var listener = new OrderRecordingService(
                "CacheListener",
                order,
                startException: new InvalidOperationException("listener failed"));
            var manager = CreateManager(dns, acme, listener);

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));

            Assert.Equal(
                [
                    "start:CloudflareDnsReconciliation",
                    "start:AcmeCertificate",
                    "start:CacheListener",
                    "stop:AcmeCertificate",
                    "stop:CloudflareDnsReconciliation",
                ],
                order);
            Assert.Equal([harness.Options.Fqdn], harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
            Assert.DoesNotContain(UnrelatedFqdn, harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
            Assert.Empty(manager.StartedServices);
        }

        /// <summary>Successful start records DNS→ACME→listener; stop reverses that order and remove runs once for the BackFiller FQDN.</summary>
        [Fact]
        public async Task Normal_shutdown_stops_listener_then_acme_then_dns_and_removes_exact_fqdn()
        {
            var harness = CreateDnsHarness();
            var order = new List<string>();
            var dns = new OrderRecordingService(harness.Adapter, order);
            var acme = new OrderRecordingService("AcmeCertificate", order);
            var listener = new OrderRecordingService("CacheListener", order);
            var manager = CreateManager(dns, acme, listener);

            await manager.StartAsync(CancellationToken.None);
            Assert.Equal([dns, acme, listener], manager.StartedServices.ToArray());
            Assert.Equal(1, harness.Resolver.ResolveCount);
            Assert.Empty(harness.Reconciler.RemoveCalls);

            await manager.StopAsync(CancellationToken.None);

            Assert.Equal(
                [
                    "start:CloudflareDnsReconciliation",
                    "start:AcmeCertificate",
                    "start:CacheListener",
                    "stop:CacheListener",
                    "stop:AcmeCertificate",
                    "stop:CloudflareDnsReconciliation",
                ],
                order);
            Assert.Equal([harness.Options.Fqdn], harness.Reconciler.ReconcileCalls.Select(static call => call.Fqdn));
            Assert.Equal([harness.Options.Fqdn], harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
            Assert.DoesNotContain(UnrelatedFqdn, harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
            Assert.Empty(manager.StartedServices);
        }

        /// <summary>Composed host wires DNS as the first <see cref="IApplicationService"/>; stop removes only the configured FQDN and does not register a legacy DNS hosted service type.</summary>
        [Fact]
        public async Task Host_start_and_stop_uses_manager_dns_and_removes_only_the_backfiller_fqdn()
        {
            var reconciler = new RecordingCloudflareDnsReconciler();
            var builder = Host.CreateApplicationBuilder([]);
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            pairs["BackFiller:BindPortTls"] = port.ToString();
            pairs["BackFiller:BindAddress:0"] = "*";
            builder.Configuration.AddInMemoryCollection(pairs);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<ICloudflareDnsReconciler>(reconciler);
            builder.Services.AddSingleton<IPhysicalMemoryProvider>(
                new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
            builder.Services.AddSingleton<IRabbitMqConnectionFactory>(
                new FakeBackFillerRabbitMqConnectionFactory());
            builder.Services.AddSingleton<IProviderAccountSource>(new FakeProviderAccountSource());
            builder.AddBackFillerHosting();
            SuccessfulNntpDbConnectionFactory.Replace(builder.Services);
            ReplaceAcme(builder.Services);

            using var host = builder.Build();
            var application = host.Services.GetServices<IApplicationService>().ToArray();
            Assert.IsType<CloudflareDnsReconciliationApplicationService>(application[0]);
            Assert.IsType<ImmediateAcmeReadyApplicationService>(application[1]);
            Assert.IsType<CacheListenerService>(application[2]);

            await host.StartAsync();
            try
            {
                Assert.Equal(["backfiller01.usenet.ninja"], reconciler.ReconcileCalls.Select(static call => call.Fqdn));
                Assert.Empty(reconciler.RemoveCalls);
                Assert.Equal(CacheListenerState.Running, host.Services.GetRequiredService<CacheListenerService>().State);
            }
            finally
            {
                await host.StopAsync();
            }

            Assert.Equal(["backfiller01.usenet.ninja"], reconciler.RemoveCalls.Select(static call => call.Fqdn));
            Assert.DoesNotContain(UnrelatedFqdn, reconciler.RemoveCalls.Select(static call => call.Fqdn));
            Assert.DoesNotContain(
                host.Services.GetServices<IHostedService>(),
                static service => service.GetType().Name == "CloudflareDnsReconciliationHostedService");
        }

        /// <summary>Builds a <see cref="VectorNNTPD.BackFillerService.Core.ApplicationServiceManager"/> with valid runtime options and no logging side effects.</summary>
        /// <param name="services">Ordered application services under test.</param>
        private static VectorNNTPD.BackFillerService.Core.ApplicationServiceManager CreateManager(params IApplicationService[] services)
        {
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());
            return new VectorNNTPD.BackFillerService.Core.ApplicationServiceManager(
                services,
                runtime,
                NullLogger<VectorNNTPD.BackFillerService.Core.ApplicationServiceManager>.Instance);
        }

        /// <summary>Real DNS reconciliation adapter with counting bind resolver and recording reconciler for FQDN-scoped assertions.</summary>
        private static DnsHarness CreateDnsHarness()
        {
            var identity = BackFillerTestOptions.CreateValid();
            var options = BackFillerTestOptions.CreateValidAcme(identity);
            options.BindAddress = ["198.18.0.10"];
            var innerResolver = new BindAddressResolver(
                new FakeLocalIpAddressAssignee(assignAll: true),
                NullLogger<BindAddressResolver>.Instance);
            var resolver = new CountingBindAddressResolver(innerResolver);
            var reconciler = new RecordingCloudflareDnsReconciler();
            var inner = new CloudflareDnsReconciliationService(
                Options.Create(options),
                resolver,
                reconciler,
                NullLogger<CloudflareDnsReconciliationService>.Instance);
            return new DnsHarness(
                new CloudflareDnsReconciliationApplicationService(inner),
                reconciler,
                resolver,
                options);
        }

        /// <summary>Swaps real ACME application service registration for immediate-ready test double in the host composition test.</summary>
        /// <param name="services">Host service collection after <c>AddBackFillerHosting</c>.</param>
        private static void ReplaceAcme(IServiceCollection services)
        {
            for (var i = 0; i < services.Count; i++)
            {
                if (services[i].ServiceType == typeof(IApplicationService)
                    && services[i].ImplementationType == typeof(AcmeCertificateApplicationService))
                {
                    services[i] = ServiceDescriptor.Singleton<IApplicationService, ImmediateAcmeReadyApplicationService>();
                }
            }
        }

        /// <summary>DNS reconciliation test fixture: adapter, spy reconciler, resolve counter, and bound options.</summary>
        /// <param name="Adapter">Application-service wrapper around the inner reconciler.</param>
        /// <param name="Reconciler">Records reconcile and remove calls.</param>
        /// <param name="Resolver">Counts bind-address resolution invocations.</param>
        /// <param name="Options">ACME/Cloudflare options including FQDN under test.</param>
        private sealed record DnsHarness(
            CloudflareDnsReconciliationApplicationService Adapter,
            RecordingCloudflareDnsReconciler Reconciler,
            CountingBindAddressResolver Resolver,
            AcmeCloudflareOptions Options);

        /// <summary>Delegates bind resolution and exposes a thread-safe call count for startup-once assertions.</summary>
        private sealed class CountingBindAddressResolver(IBindAddressResolver inner) : IBindAddressResolver
        {
            /// <summary>Observed <see cref="Resolve"/> invocations.</summary>
            private int _resolveCount;

            /// <summary>Number of <see cref="Resolve"/> calls observed.</summary>
            internal int ResolveCount => Volatile.Read(ref _resolveCount);

            /// <inheritdoc />
            public ResolvedBindAddresses Resolve(AcmeCloudflareOptions options)
            {
                Interlocked.Increment(ref _resolveCount);
                return inner.Resolve(options);
            }
        }

        /// <summary>Records start/stop order; optionally wraps a real service or throws on start to simulate downstream failure.</summary>
        private sealed class OrderRecordingService : IApplicationService
        {
            /// <summary>Optional inner service whose lifecycle is forwarded after recording.</summary>
            private readonly IApplicationService? _inner;
            /// <summary>Shared list receiving start/stop order markers.</summary>
            private readonly List<string> _order;
            /// <summary>When set, thrown from <see cref="StartAsync"/> after recording start.</summary>
            private readonly Exception? _startException;

            /// <summary>Wraps an inner service and forwards start/stop after recording order.</summary>
            /// <param name="inner">Service whose <see cref="Name"/> and lifecycle are delegated.</param>
            /// <param name="order">Mutable list receiving <c>start:</c> and <c>stop:</c> entries.</param>
            internal OrderRecordingService(IApplicationService inner, List<string> order)
            {
                ArgumentNullException.ThrowIfNull(inner);
                ArgumentNullException.ThrowIfNull(order);
                _inner = inner;
                _order = order;
                Name = inner.Name;
            }

            /// <summary>Stub service that records order and optionally fails <see cref="StartAsync"/>.</summary>
            /// <param name="name">Recorded service name in order entries.</param>
            /// <param name="order">Mutable list receiving lifecycle order.</param>
            /// <param name="startException">When set, thrown from <see cref="StartAsync"/> after recording start.</param>
            internal OrderRecordingService(string name, List<string> order, Exception? startException = null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                ArgumentNullException.ThrowIfNull(order);
                Name = name;
                _order = order;
                _startException = startException;
            }

            /// <inheritdoc />
            public string Name { get; }

            /// <inheritdoc />
            public Task? Execution => _inner?.Execution;

            /// <summary>Number of successful entries into <see cref="StartAsync"/> before any throw.</summary>
            public int StartCount { get; private set; }

            /// <inheritdoc />
            public async Task StartAsync(CancellationToken cancellationToken)
            {
                StartCount++;
                _order.Add("start:" + Name);
                if (_startException is not null)
                {
                    throw _startException;
                }

                if (_inner is not null)
                {
                    await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            /// <inheritdoc />
            public async Task StopAsync(CancellationToken cancellationToken)
            {
                _order.Add("stop:" + Name);
                if (_inner is not null)
                {
                    await _inner.StopAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
