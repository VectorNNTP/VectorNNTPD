using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Listener;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using System.Net;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Cloudflare;
using System.Net.Sockets;

namespace VectorNNTPD.BackFillerService.Tests.Hosting
{
    /// <summary>TLS-only bind validation, listener prerequisites (ACME readiness and certificate), and startup journal ordering.</summary>
    public sealed class BackFillerTlsStartupTests
    {
        /// <summary>TlsOnlyAcmeCloudflareOptionsValidator rejects BindPortTls zero with a TLS-only failure message.</summary>
        [Fact]
        public void BindPortTls_zero_fails_tls_only_validation()
        {
            var options = BackFillerTestOptions.CreateValidAcme();
            options.BindPortTls = 0;
            var result = new TlsOnlyAcmeCloudflareOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains("TLS-only", result.Failures!.Single(), StringComparison.Ordinal);
        }
        /// <summary>Out-of-range BindPortTls values fail TLS-only validation before runtime options are built.</summary>

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(65536)]
        public void BindPortTls_out_of_range_fails_tls_only_validation(int port)
        {
            var options = BackFillerTestOptions.CreateValidAcme();
            options.BindPortTls = port;
            var result = new TlsOnlyAcmeCloudflareOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
        }
        /// <summary>Runtime options expose only BindPortTls as the listener port; legacy BindPort is not used for TLS listen.</summary>

        [Fact]
        public void BindPortTls_valid_is_the_only_listener_port()
        {
            var identity = BackFillerTestOptions.CreateValid();
            var acme = BackFillerTestOptions.CreateValidAcme(identity);
            acme.BindPort = 119;
            acme.BindPortTls = 5630;
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                identity,
                BackFillerTestOptions.CreateValidNntpDb(),
                acme);

            Assert.Equal(5630, runtime.BindPortTls);
            Assert.DoesNotContain(119, runtime.BindAddressTokens.Select(_ => runtime.BindPortTls).Distinct().Except([5630]));
        }
        /// <summary>BackFillerRuntimeOptionsFactory throws when BindPortTls is disabled rather than falling back to BindPort.</summary>

        [Fact]
        public void Runtime_factory_does_not_fall_back_to_BindPort_when_tls_is_disabled()
        {
            var identity = BackFillerTestOptions.CreateValid();
            var acme = BackFillerTestOptions.CreateValidAcme(identity);
            acme.BindPort = 1190;
            acme.BindPortTls = 0;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                BackFillerRuntimeOptionsFactory.Create(
                    identity,
                    BackFillerTestOptions.CreateValidNntpDb(),
                    acme));
            Assert.Contains("TLS-only", ex.Message, StringComparison.Ordinal);
            Assert.Contains("BindPortTls", ex.Message, StringComparison.Ordinal);
        }
        /// <summary>CacheListenerService start fails before ListenerStarted when ACME readiness is false.</summary>

        [Fact]
        public async Task Listener_does_not_start_when_acme_is_not_ready()
        {
            var runtime = CreateRuntime(GetFreePort());
            var readiness = new AcmeCertificateReadiness();
            var journal = new BackFillerStartupJournal();
            await using var service = new CacheListenerService(
                runtime,
                TestListenerCertificates.CreatePublishedProvider(),
                new ArticleRetentionAuthority(runtime, TimeProvider.System, NullLogger<ArticleRetentionAuthority>.Instance),
                readiness,
                journal,
                NullLogger<CacheListenerService>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Contains("ACME certificate is not ready", ex.Message, StringComparison.Ordinal);
            Assert.Equal(CacheListenerState.Stopped, service.State);
            Assert.DoesNotContain(BackFillerStartupStages.ListenerStarted, journal.Stages);
            Assert.Empty(service.LocalEndPoints);
        }
        /// <summary>Start fails with no TLS certificate when readiness is true but the provider has no cert.</summary>

        [Fact]
        public async Task Listener_does_not_start_when_certificate_is_missing()
        {
            var runtime = CreateRuntime(GetFreePort());
            var readiness = new AcmeCertificateReadiness();
            readiness.MarkReady();
            var journal = new BackFillerStartupJournal();
            await using var service = new CacheListenerService(
                runtime,
                TestListenerCertificates.CreateUnavailableProvider(),
                new ArticleRetentionAuthority(runtime, TimeProvider.System, NullLogger<ArticleRetentionAuthority>.Instance),
                readiness,
                journal,
                NullLogger<CacheListenerService>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Contains("no TLS certificate", ex.Message, StringComparison.Ordinal);
            Assert.Equal(CacheListenerState.Stopped, service.State);
            Assert.DoesNotContain(BackFillerStartupStages.ListenerStarted, journal.Stages);
        }
        /// <summary>With readiness and a published cert, listener binds only on BindPortTls endpoints.</summary>

        [Fact]
        public async Task Valid_certificate_starts_tls_listener_on_BindPortTls_only()
        {
            var port = GetFreePort();
            var runtime = CreateRuntime(port);
            var readiness = new AcmeCertificateReadiness();
            readiness.MarkReady();
            var journal = new BackFillerStartupJournal();
            await using var service = new CacheListenerService(
                runtime,
                TestListenerCertificates.CreatePublishedProvider(),
                new ArticleRetentionAuthority(runtime, TimeProvider.System, NullLogger<ArticleRetentionAuthority>.Instance),
                readiness,
                journal,
                NullLogger<CacheListenerService>.Instance);

            await service.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal(CacheListenerState.Running, service.State);
                Assert.Contains(BackFillerStartupStages.ListenerStarted, journal.Stages);
                Assert.All(service.LocalEndPoints, endpoint =>
                {
                    var ip = Assert.IsType<IPEndPoint>(endpoint);
                    Assert.Equal(port, ip.Port);
                });
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        /// <summary>Full host startup journal orders AcmeCertificateReady before ListenerStarted.</summary>

        [Fact]
        public async Task Hosted_startup_records_acme_ready_before_listener()
        {
            var builder = Host.CreateApplicationBuilder([]);
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            var port = GetFreePort();
            pairs["BackFiller:BindPortTls"] = port.ToString();
            pairs["BindPort"] = "119";
            pairs["BackFiller:BindAddress:0"] = "*";
            builder.Configuration.AddInMemoryCollection(pairs);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<VectorNNTPD.Common.Cloudflare.ICloudflareDnsReconciler>(
                new NoOpCloudflareDnsReconciler());
            builder.Services.AddSingleton<IPhysicalMemoryProvider>(
                new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
            builder.Services.AddSingleton<IRabbitMqConnectionFactory>(
                new FakeBackFillerRabbitMqConnectionFactory());
            builder.Services.AddSingleton<VectorNNTPD.BackFillerService.Accounts.IProviderAccountSource>(
                new FakeProviderAccountSource());
            builder.AddBackFillerHosting();
            SuccessfulNntpDbConnectionFactory.Replace(builder.Services);
            ReplaceAcme(builder.Services);

            using var host = builder.Build();
            await host.StartAsync();
            try
            {
                var journal = host.Services.GetRequiredService<IBackFillerStartupJournal>();
                var stages = journal.Stages.ToList();
                Assert.Contains(BackFillerStartupStages.Configuration, stages);
                Assert.DoesNotContain("bind-resolution", stages);
                Assert.DoesNotContain("cloudflare-reconciled", stages);
                Assert.Contains(BackFillerStartupStages.AcmeCertificateReady, stages);
                Assert.Contains(BackFillerStartupStages.ListenerStarted, stages);

                var acmeIndex = stages.IndexOf(BackFillerStartupStages.AcmeCertificateReady);
                var listenerIndex = stages.IndexOf(BackFillerStartupStages.ListenerStarted);
                Assert.InRange(acmeIndex, 0, listenerIndex - 1);

                var application = host.Services.GetServices<IApplicationService>().ToArray();
                Assert.IsType<CloudflareDnsReconciliationApplicationService>(application[0]);
                Assert.IsType<ImmediateAcmeReadyApplicationService>(application[1]);
                var listener = host.Services.GetRequiredService<CacheListenerService>();
                Assert.Same(listener, application[2]);
                Assert.Equal(CacheListenerState.Running, listener.State);
                Assert.All(listener.LocalEndPoints, endpoint =>
                {
                    var ip = Assert.IsType<IPEndPoint>(endpoint);
                    Assert.Equal(port, ip.Port);
                });
            }
            finally
            {
                await host.StopAsync();
            }
        }
        /// <summary>CertificateIdentities omits news hostname when IncludeNewsHostnameInCertificate is false.</summary>

        [Fact]
        public void Certificate_identities_are_only_the_backfiller_fqdn()
        {
            var acme = BackFillerTestOptions.CreateValidAcme();
            acme.IncludeNewsHostnameInCertificate = false;
            var names = CertificateIdentities.ForFqdn(acme.Fqdn, acme.IncludeNewsHostnameInCertificate);
            Assert.Equal(["backfiller01.usenet.ninja"], names);
            Assert.DoesNotContain("news.usenet.ninja", names);
            Assert.DoesNotContain("*.usenet.ninja", names);
        }

        /// <summary>Runtime options with loopback bind and the given TLS port.</summary>
        private static BackFillerRuntimeOptions CreateRuntime(int tlsPort)
        {
            var identity = BackFillerTestOptions.CreateValid();
            identity.BindAddress = ["127.0.0.1"];
            var acme = BackFillerTestOptions.CreateValidAcme(identity);
            acme.BindAddress = ["127.0.0.1"];
            acme.BindPortTls = tlsPort;
            return BackFillerRuntimeOptionsFactory.Create(
                identity,
                BackFillerTestOptions.CreateValidNntpDb(),
                acme);
        }

        /// <summary>Substitutes immediate-ready ACME application service in the service collection.</summary>
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

        /// <summary>Ephemeral loopback port for listener bind tests.</summary>
        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

    }
}
