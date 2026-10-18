using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Hosting.Systemd;
using VectorNNTPD.BackFillerService.Logging;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;
using System.Net.Sockets;
using System.Net;

namespace VectorNNTPD.BackFillerService.Tests.Hosting.Systemd
{
    /// <summary>End-to-end host systemd notify ordering: READY only after successful start, STOPPING on shutdown, none when disabled.</summary>
    public sealed class SystemdHostIntegrationTests
    {
        /// <summary>Under fake systemd runtime, successful host start emits READY once and STOPPING once on stop; health tracks running state.</summary>
        [Fact]
        public async Task Host_EmitsReadyAfterSuccessfulStart_AndStoppingOnShutdown()
        {
            var notify = new FakeSystemdNotifyBridge();
            using var host = CreateHost(notify);

            Assert.Equal(0, notify.ReadyCount);
            await host.StartAsync();
            try
            {
                Assert.Equal(1, notify.ReadyCount);
                Assert.True(host.Services.GetRequiredService<BackFillerApplicationHealth>().IsHealthyForWatchdog);
                Assert.Contains(notify.Notifications, n => n.Contains("Running", StringComparison.Ordinal));
            }
            finally
            {
                await host.StopAsync();
            }

            Assert.Equal(1, notify.StoppingCount);
            Assert.False(host.Services.GetRequiredService<BackFillerApplicationHealth>().IsHealthyForWatchdog);
        }
        /// <summary>Application service start failure must not emit READY=1.</summary>

        [Fact]
        public async Task Host_StartupFailure_DoesNotEmitReady()
        {
            var notify = new FakeSystemdNotifyBridge();
            using var host = CreateHost(notify, failApplicationService: true);

            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

            Assert.Equal(0, notify.ReadyCount);
        }
        /// <summary>When notify bridge is disabled, start and stop produce no systemd notifications.</summary>

        [Fact]
        public async Task Host_OutsideSystemd_DoesNotEmitNotifications()
        {
            var notify = new FakeSystemdNotifyBridge { IsEnabled = false };
            using var host = CreateHost(notify);

            await host.StartAsync();
            try
            {
                Assert.Empty(notify.Notifications);
                Assert.Equal(0, notify.ReadyCount);
            }
            finally
            {
                await host.StopAsync();
            }

            Assert.Equal(0, notify.StoppingCount);
        }

        /// <summary>Builds a BackFiller host with fake systemd notify and optional ACME startup failure.</summary>
        private static IHost CreateHost(FakeSystemdNotifyBridge notify, bool failApplicationService = false)
        {
            var builder = Host.CreateApplicationBuilder([]);
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            var tlsPort = GetFreePort();
            pairs["BackFiller:BindPortTls"] = tlsPort.ToString();
            pairs["BackFiller:BindAddress:0"] = "*";
            pairs["BackFiller:Logging:File:LogDir"] = Directory.CreateTempSubdirectory("bf-systemd-logs-").FullName;
            builder.Configuration.AddInMemoryCollection(pairs);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<ICloudflareDnsReconciler>(new NoOpCloudflareDnsReconciler());
            builder.Services.AddSingleton<IPhysicalMemoryProvider>(new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
            builder.Services.AddSingleton<IRabbitMqConnectionFactory>(new FakeBackFillerRabbitMqConnectionFactory());
            builder.Services.AddSingleton<IProviderAccountSource>(new FakeProviderAccountSource());

            builder.ConfigureBackFillerLogging();
            builder.ConfigureBackFillerPlatformHosting();
            builder.AddBackFillerHosting();
            SuccessfulNntpDbConnectionFactory.Replace(builder.Services);

            builder.Services.RemoveAll<ISystemdNotifyBridge>();
            builder.Services.AddSingleton<ISystemdNotifyBridge>(notify);
            builder.Services.RemoveAll<ISystemdRuntime>();
            builder.Services.AddSingleton<ISystemdRuntime>(RecordingSystemdRuntimeFactory.LinuxUnderSystemd());

            ReplaceAcmeApplicationService(builder.Services, failApplicationService);

            return builder.Build();
        }

        /// <summary>Swaps the registered ACME application service for immediate-ready or failing test doubles.</summary>
        private static void ReplaceAcmeApplicationService(IServiceCollection services, bool fail)
        {
            for (var i = 0; i < services.Count; i++)
            {
                if (services[i].ServiceType == typeof(IApplicationService)
                    && services[i].ImplementationType == typeof(AcmeCertificateApplicationService))
                {
                    services[i] = fail
                        ? ServiceDescriptor.Singleton<IApplicationService, FailingAcmeApplicationService>()
                        : ServiceDescriptor.Singleton<IApplicationService, ImmediateAcmeReadyApplicationService>();
                }
            }
        }

        /// <summary>Binds an ephemeral loopback TCP port for TLS bind configuration in host tests.</summary>
        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>IApplicationService test double that always fails start to regress missing READY notify.</summary>
        private sealed class FailingAcmeApplicationService : IApplicationService
        {
            /// <inheritdoc />
            public string Name => "FailingAcme";

            /// <inheritdoc />
            public Task? Execution => null;

            /// <inheritdoc />
            public Task StartAsync(CancellationToken cancellationToken) =>
                throw new InvalidOperationException("ACME startup failed");

            /// <inheritdoc />
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
