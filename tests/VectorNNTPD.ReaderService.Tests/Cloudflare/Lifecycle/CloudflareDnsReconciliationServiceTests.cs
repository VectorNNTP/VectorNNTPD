using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.ReaderService.Acme;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.ReaderService.Hosting;
using VectorNNTPD.ReaderService.Logging;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.ReaderService.Networking.Listeners;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Cloudflare.Lifecycle;

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
        builder.Services.AddNntpReaderHosting(includePlaceholderService: false);

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
        Assert.Equal(typeof(VectorNNTPD.ReaderService.NntpDb.NntpSharedConfigurationService), services[1].GetType());
        AssertRegisteredBefore(services, typeof(NntpDbService), typeof(VectorNNTPD.ReaderService.NntpDb.NntpSharedConfigurationService));
        AssertRegisteredBefore(
            services,
            typeof(VectorNNTPD.ReaderService.NntpDb.NntpSharedConfigurationService),
            typeof(CloudflareDnsReconciliationApplicationService));
        Assert.Equal(
            typeof(CloudflareDnsReconciliationService).Assembly,
            services.Single(static service => service.GetType() == typeof(CloudflareDnsReconciliationApplicationService)).GetType().Assembly);

        AssertRegisteredBefore(services, typeof(CloudflareDnsReconciliationApplicationService), typeof(VectorNNTPD.ReaderService.Redis.RedisService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.ReaderService.Redis.RedisService), typeof(VectorNNTPD.Common.Messaging.RabbitMq.RabbitMqService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.Common.Messaging.RabbitMq.RabbitMqService), typeof(VectorNNTPD.ReaderService.RabbitMq.RabbitMqTopologyService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.ReaderService.RabbitMq.RabbitMqTopologyService), typeof(VectorNNTPD.ReaderService.RabbitMq.ArticleWork.ArticleWorkRpcService));
        AssertRegisteredBefore(services, typeof(NntpDbService), typeof(VectorNNTPD.ReaderService.Newsgroups.NewsgroupCatalogueService));
        // Reader role: no Transit peer/DNS services; SessionState lease renewal follows ninpaths.
        AssertRegisteredBefore(services, typeof(VectorNNTPD.ReaderService.Newsgroups.NewsgroupCatalogueService), typeof(VectorNNTPD.ReaderService.Moderation.ModeratorCatalogueService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.ReaderService.Moderation.ModeratorCatalogueService), typeof(VectorNNTPD.ReaderService.PostFilter.PostFilterPolicyService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.ReaderService.PostFilter.PostFilterPolicyService), typeof(VectorNNTPD.ReaderService.PostFilter.PostFilterRejectionEvidenceService));
        AssertRegisteredBefore(services, typeof(NntpDbService), typeof(VectorNNTPD.ReaderService.PostFilter.PostFilterRejectionEvidenceService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.ReaderService.PostFilter.PostFilterRejectionEvidenceService), typeof(VectorNNTPD.ReaderService.History.HistoryWriteService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.ReaderService.History.HistoryWriteService), typeof(VectorNNTPD.ReaderService.History.HistoryMaintenanceService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.ReaderService.History.HistoryMaintenanceService), typeof(IncomingSpoolWriterService));
        AssertRegisteredBefore(services, typeof(IncomingSpoolWriterService), typeof(VectorNNTPD.ReaderService.Email.EmailDeliveryService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.ReaderService.Email.EmailDeliveryService), typeof(VectorNNTPD.ReaderService.Ninpaths.NinpathsProcessingService));
        AssertRegisteredBefore(services, typeof(VectorNNTPD.ReaderService.Ninpaths.NinpathsProcessingService), typeof(SessionStateService));
        AssertRegisteredBefore(services, typeof(SessionStateService), typeof(NntpPlainListenerService));
        AssertRegisteredBefore(services, typeof(NntpPlainListenerService), typeof(AcmeCertificateApplicationService));
        AssertRegisteredBefore(services, typeof(AcmeCertificateApplicationService), typeof(NntpTlsListenerService));
        Assert.DoesNotContain(services, static s => s.GetType().Name == "AccountByteService");
        Assert.DoesNotContain(services, static s => s is VectorNNTPD.ReaderService.Transit.TransitDnsRefreshService);
        Assert.DoesNotContain(services, static s => s is VectorNNTPD.ReaderService.Transit.TransitPeerStateService);
        Assert.Equal(1, services.Count(static s => s is SessionStateService));

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
