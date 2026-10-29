using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.SessionState;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.TransitService.Acme;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.Hosting;
using VectorNNTPD.TransitService.Logging;
using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.TransitService.Networking.Listeners;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.TransitService.Tests.Cloudflare.Lifecycle;

[Collection(SerilogCollection.Name)]
public sealed class CloudflareDnsReconciliationServiceTests
{
    [Fact]
    public async Task StartAsync_WithNoEligibleAddresses_Fails()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["127.0.0.1"];

        var service = CreateService(
            options,
            new FakeLocalIpAddressAssignee(IPAddress.Loopback),
            new FakeCloudflareDnsClient());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(CancellationToken.None));

        Assert.Contains("No eligible IP addresses", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WhenReconcileFails_PropagatesFailure()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];

        var client = new FakeCloudflareDnsClient
        {
            CreateException = new CloudflareDnsException("boom"),
        };

        var service = CreateService(
            options,
            new FakeLocalIpAddressAssignee(TestHostFactory.TestIpv4),
            client);

        await Assert.ThrowsAsync<CloudflareDnsException>(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Lifecycle_DnsReconciliationCompletesBeforeRunning()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];

        var client = new FakeCloudflareDnsClient();
        var dnsService = CreateService(
            options,
            new FakeLocalIpAddressAssignee(TestHostFactory.TestIpv4, TestHostFactory.TestIpv6),
            client);

        var order = new List<string>();
        var other = new FakeApplicationService("other")
        {
            SharedStartOrder = order,
        };

        var trackingDns = new TrackingApplicationService(TestHostFactory.WrapDns(dnsService), order, "dns");

        await using var lifecycle = TestHostFactory.CreateLifecycle([trackingDns, other], options);
        await lifecycle.StartAsync(CancellationToken.None);

        Assert.Equal(ApplicationState.Running, lifecycle.State);
        Assert.Equal(["dns", "other"], order);
        Assert.True(client.CreateCallCount >= 2);
        await lifecycle.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Lifecycle_DnsFailure_PreventsRunning()
    {
        var options = TestHostFactory.CreateValidOptions();
        var client = new FakeCloudflareDnsClient
        {
            ListException = new CloudflareDnsException("list failed"),
        };

        var dnsService = CreateService(
            options,
            new FakeLocalIpAddressAssignee(TestHostFactory.TestIpv4),
            client);

        var other = new FakeApplicationService("other");
        await using var lifecycle = TestHostFactory.CreateLifecycle([TestHostFactory.WrapDns(dnsService), other], options);

        await Assert.ThrowsAsync<CloudflareDnsException>(() => lifecycle.StartAsync(CancellationToken.None));
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(0, other.StartCount);
    }

    [Fact]
    public async Task Host_RegistersDnsReconciliation_BeforeOtherServices()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpTransitHosting(includePlaceholderService: false);

        var started = new List<string>();
        builder.Services.AddSingleton<IApplicationService>(
            new FakeApplicationService(
                "app",
                onStart: _ =>
                {
                    started.Add("app");
                    return Task.CompletedTask;
                }));

        using var host = builder.Build();
        var services = host.Services.GetServices<IApplicationService>().ToArray();
        // Shared configuration is published before Cloudflare so the zone id and FQDN exist.
        Assert.Equal(typeof(NntpDbService), services[0].GetType());
        Assert.Equal(typeof(VectorNNTPD.TransitService.NntpDb.NntpSharedConfigurationService), services[1].GetType());
        AssertRegisteredBefore(services, typeof(NntpDbService), typeof(VectorNNTPD.TransitService.NntpDb.NntpSharedConfigurationService));
        AssertRegisteredBefore(
            services,
            typeof(VectorNNTPD.TransitService.NntpDb.NntpSharedConfigurationService),
            typeof(CloudflareDnsReconciliationApplicationService));
        Assert.Equal(
            typeof(CloudflareDnsReconciliationService).Assembly,
            services.Single(static service => service.GetType() == typeof(CloudflareDnsReconciliationApplicationService)).GetType().Assembly);

        AssertRegisteredBefore(services, typeof(CloudflareDnsReconciliationApplicationService), typeof(VectorNNTPD.TransitService.Redis.RedisService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.TransitService.Redis.RedisService), typeof(VectorNNTPD.Common.Messaging.RabbitMq.RabbitMqService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.Common.Messaging.RabbitMq.RabbitMqService), typeof(VectorNNTPD.TransitService.RabbitMq.RabbitMqTopologyService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.TransitService.RabbitMq.RabbitMqTopologyService), typeof(VectorNNTPD.TransitService.RabbitMq.ArticleWork.ArticleWorkRpcService));
        AssertRegisteredBefore(services, typeof(NntpDbService), typeof(VectorNNTPD.TransitService.Newsgroups.NewsgroupCatalogueService));
        // Transit role: no Reader-only application services (moderator, post filter, email, session state).
        AssertRegisteredBefore(services, typeof(NntpDbService), typeof(VectorNNTPD.TransitService.Newsgroups.NewsgroupCatalogueService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.TransitService.Newsgroups.NewsgroupCatalogueService), typeof(VectorNNTPD.TransitService.History.HistoryWriteService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.TransitService.History.HistoryWriteService), typeof(VectorNNTPD.TransitService.History.HistoryMaintenanceService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.TransitService.History.HistoryMaintenanceService), typeof(IncomingSpoolWriterService));
        AssertRegisteredBefore(services, typeof(IncomingSpoolWriterService), typeof(VectorNNTPD.TransitService.Ninpaths.NinpathsProcessingService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.TransitService.Ninpaths.NinpathsProcessingService), typeof(VectorNNTPD.TransitService.Transit.TransitDnsRefreshService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.TransitService.Transit.TransitDnsRefreshService), typeof(VectorNNTPD.TransitService.Transit.TransitPeerStateService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.TransitService.Transit.TransitPeerStateService), typeof(NntpPlainListenerService));
        AssertRegisteredBefore(services, typeof(NntpPlainListenerService), typeof(AcmeCertificateApplicationService));
        AssertRegisteredBefore(services, typeof(AcmeCertificateApplicationService), typeof(NntpTlsListenerService));
        Assert.DoesNotContain(services, static s => s.GetType().Name == "AccountByteService");
        Assert.DoesNotContain(services, static s => s is VectorNNTPD.TransitService.Moderation.ModeratorCatalogueService);
        Assert.DoesNotContain(services, static s => s is VectorNNTPD.TransitService.PostFilter.PostFilterPolicyService);
        Assert.DoesNotContain(services, static s => s is VectorNNTPD.TransitService.PostFilter.PostFilterRejectionEvidenceService);
        Assert.DoesNotContain(services, static s => s is VectorNNTPD.TransitService.Email.EmailDeliveryService);
        Assert.DoesNotContain(services, static s => s is SessionStateService);

        await host.StartAsync();
        Assert.Equal(ApplicationState.Running, host.Services.GetRequiredService<ApplicationLifecycle>().State);
        Assert.Equal(["app"], started);
        await host.StopAsync();
    }

    [Fact]
    public async Task StartAsync_Cancellation_Propagates()
    {
        var options = TestHostFactory.CreateValidOptions();
        var client = new FakeCloudflareDnsClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = CreateService(
            options,
            new FakeLocalIpAddressAssignee(TestHostFactory.TestIpv4),
            client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(cts.Token));
    }

    private static void AssertRegisteredBefore(IApplicationService[] services, Type earlier, Type later)
    {
        var earlierIndex = -1;
        var laterIndex = -1;
        for (var i = 0; i < services.Length; i++)
        {
            var type = services[i].GetType();
            if (type == earlier && earlierIndex < 0)
            {
                earlierIndex = i;
            }
            else if (type == later && laterIndex < 0)
            {
                laterIndex = i;
            }
        }

        Assert.True(earlierIndex >= 0, $"{earlier.Name} must be registered.");
        Assert.True(laterIndex >= 0, $"{later.Name} must be registered.");
        Assert.True(
            earlierIndex < laterIndex,
            $"{earlier.Name} (index {earlierIndex}) must precede {later.Name} (index {laterIndex}).");
    }

    private static CloudflareDnsReconciliationService CreateService(
        NntpdOptions options,
        ILocalIpAddressAssignee assignee,
        ICloudflareDnsClient client)
    {
        var resolver = new BindAddressResolver(assignee, NullLogger<BindAddressResolver>.Instance);
        var reconciler = new CloudflareDnsReconciler(client, Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()), NullLogger<CloudflareDnsReconciler>.Instance);
        return new CloudflareDnsReconciliationService(
            Options.Create<AcmeCloudflareOptions>(options),
            resolver,
            reconciler,
            NullLogger<CloudflareDnsReconciliationService>.Instance);
    }

    private sealed class TrackingApplicationService : IApplicationService
    {
        private readonly IApplicationService _inner;
        private readonly List<string> _order;
        private readonly string _label;

        public TrackingApplicationService(IApplicationService inner, List<string> order, string label)
        {
            _inner = inner;
            _order = order;
            _label = label;
        }

        public string Name => _inner.Name;

        public Task? Execution => _inner.Execution;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
            _order.Add(_label);
        }

        public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);
    }
}
