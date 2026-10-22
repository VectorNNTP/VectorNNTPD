using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.Common.Transport.ArticleTransfer;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Listener;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.TestDoubles;

namespace VectorNNTPD.StorageService.Tests.Listener
{

    public sealed class StorageVatpListenerServiceTests
    {
        private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task Listener_refuses_start_without_acme_readiness()
        {
            var runtime = CreateRuntime(StorageServerTestOptions.GetFreeTcpPort());
            var readiness = new AcmeCertificateReadiness();
            await using var service = new StorageVatpListenerService(
                runtime,
                TestListenerCertificates.CreatePublishedProvider(),
                NullStorageArticleOpenBoundary.Instance,
                readiness,
                NullLogger<StorageVatpListenerService>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Contains("ACME certificate is not ready", ex.Message, StringComparison.Ordinal);
            Assert.Equal(StorageVatpListenerState.Stopped, service.State);
            Assert.Empty(service.LocalEndPoints);
        }

        [Fact]
        public async Task Listener_refuses_start_without_certificate()
        {
            var runtime = CreateRuntime(StorageServerTestOptions.GetFreeTcpPort());
            var readiness = new AcmeCertificateReadiness();
            readiness.MarkReady();
            await using var service = new StorageVatpListenerService(
                runtime,
                TestListenerCertificates.CreateUnavailableProvider(),
                NullStorageArticleOpenBoundary.Instance,
                readiness,
                NullLogger<StorageVatpListenerService>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Contains("no TLS certificate", ex.Message, StringComparison.Ordinal);
            Assert.Equal(StorageVatpListenerState.Stopped, service.State);
        }

        [Fact]
        public async Task Listener_starts_when_ready_and_shuts_down_cleanly()
        {
            var port = StorageServerTestOptions.GetFreeTcpPort();
            var runtime = CreateRuntime(port);
            var readiness = new AcmeCertificateReadiness();
            readiness.MarkReady();
            await using var service = new StorageVatpListenerService(
                runtime,
                TestListenerCertificates.CreatePublishedProvider(),
                NullStorageArticleOpenBoundary.Instance,
                readiness,
                NullLogger<StorageVatpListenerService>.Instance);

            await service.StartAsync(CancellationToken.None);
            Assert.Equal(StorageVatpListenerState.Running, service.State);
            Assert.All(service.LocalEndPoints, endpoint =>
            {
                var ip = Assert.IsType<IPEndPoint>(endpoint);
                Assert.Equal(port, ip.Port);
            });

            await service.StopAsync(CancellationToken.None);
            Assert.Equal(StorageVatpListenerState.Stopped, service.State);
            await service.DisposeAsync();
            Assert.Equal(StorageVatpListenerState.Stopped, service.State);
        }

        [Fact]
        public async Task Existing_connection_keeps_certificate_lease_after_rotation()
        {
            var port = StorageServerTestOptions.GetFreeTcpPort();
            var runtime = CreateRuntime(port);
            var certificates = TestListenerCertificates.CreatePublishedProvider("cache-original.test");
            var readiness = new AcmeCertificateReadiness();
            readiness.MarkReady();
            await using var service = new StorageVatpListenerService(
                runtime,
                certificates,
                NullStorageArticleOpenBoundary.Instance,
                readiness,
                NullLogger<StorageVatpListenerService>.Instance);

            await service.StartAsync(CancellationToken.None);

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
                CancellationToken.None);

            Assert.Contains("cache-original.test", ssl.RemoteCertificate!.Subject, StringComparison.OrdinalIgnoreCase);

            using var renewed = TestListenerCertificates.CreateSelfSigned("cache-renewed.test");
            certificates.PublishFromPfx(renewed.Export(X509ContentType.Pfx, "test"), "test");

            Assert.Contains("cache-original.test", ssl.RemoteCertificate.Subject, StringComparison.OrdinalIgnoreCase);

            using var second = new TcpClient();
            await second.ConnectAsync(IPAddress.Loopback, port);
            await using var ssl2 = new SslStream(second.GetStream(), leaveInnerStreamOpen: false);
            await ssl2.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
                CancellationToken.None);
            Assert.Contains("cache-renewed.test", ssl2.RemoteCertificate!.Subject, StringComparison.OrdinalIgnoreCase);

            await service.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task Listener_uses_the_production_initial_window_when_no_limits_are_supplied()
        {
            await using var service = new StorageVatpListenerService(
                CreateRuntime(StorageServerTestOptions.GetFreeTcpPort()),
                TestListenerCertificates.CreatePublishedProvider(),
                NullStorageArticleOpenBoundary.Instance,
                NullLogger<StorageVatpListenerService>.Instance);
            Assert.Equal(1024 * 1024, service.TransferLimits.InitialStreamWindowBytes);
            Assert.Equal(64L * 1024 * 1024, service.TransferLimits.MaxStreamCreditBytes);
        }

        [Fact]
        public async Task Listener_keeps_an_explicit_initial_window_for_its_sessions()
        {
            var limits = new ArticleTransferLimits { InitialStreamWindowBytes = 2 * 1024 * 1024 };
            await using var service = new StorageVatpListenerService(
                CreateRuntime(StorageServerTestOptions.GetFreeTcpPort()),
                TestListenerCertificates.CreatePublishedProvider(),
                NullStorageArticleOpenBoundary.Instance,
                NullLogger<StorageVatpListenerService>.Instance,
                transferLimits: limits);
            Assert.Same(limits, service.TransferLimits);
            Assert.Equal(2 * 1024 * 1024, service.TransferLimits.InitialStreamWindowBytes);
            Assert.Equal(ArticleTransferLimits.Default.MaxStreamCreditBytes, service.TransferLimits.MaxStreamCreditBytes);
        }

        [Fact]
        public void Bench_window_accepts_only_the_five_sizes()
        {
            Assert.Equal(1024 * 1024, StorageVatpThroughputBench.ParseBenchWindow(null));
            Assert.Equal(256 * 1024, StorageVatpThroughputBench.ParseBenchWindow("262144"));
            Assert.Equal(512 * 1024, StorageVatpThroughputBench.ParseBenchWindow("524288"));
            Assert.Equal(1024 * 1024, StorageVatpThroughputBench.ParseBenchWindow("1048576"));
            Assert.Equal(2 * 1024 * 1024, StorageVatpThroughputBench.ParseBenchWindow("2097152"));
            Assert.Equal(4 * 1024 * 1024, StorageVatpThroughputBench.ParseBenchWindow("4194304"));
            var rejected = Assert.Throws<InvalidOperationException>(() => StorageVatpThroughputBench.ParseBenchWindow("1048575"));
            Assert.Contains("262144", rejected.Message, StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => StorageVatpThroughputBench.ParseBenchWindow("67108864"));
        }

        [Fact]
        public async Task Peer_disconnect_releases_admission_and_keeps_the_listener_accepting()
        {
            var port = StorageServerTestOptions.GetFreeTcpPort();
            var runtime = CreateRuntime(port);
            var readiness = new AcmeCertificateReadiness();
            readiness.MarkReady();
            var logger = new CapturingListenerLogger();
            await using var service = new StorageVatpListenerService(
                runtime,
                TestListenerCertificates.CreatePublishedProvider(),
                NullStorageArticleOpenBoundary.Instance,
                readiness,
                logger);

            await service.StartAsync(CancellationToken.None);

            using (var tcp = new TcpClient())
            {
                await tcp.ConnectAsync(IPAddress.Loopback, port);
                await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                    },
                    CancellationToken.None);

                await WaitUntilAsync(() => service.ActiveConnections == 1);
                tcp.Client.LingerState = new LingerOption(true, 0);
                tcp.Client.Close();
            }

            await WaitUntilAsync(() => service.ActiveConnections == 0);
            Assert.Contains("connection peer disconnect", logger.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("connection failed", logger.Text, StringComparison.Ordinal);

            using var second = new TcpClient();
            await second.ConnectAsync(IPAddress.Loopback, port);
            await using var ssl2 = new SslStream(second.GetStream(), leaveInnerStreamOpen: false);
            await ssl2.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => service.ActiveConnections == 1);

            await service.StopAsync(CancellationToken.None);
            Assert.Equal(StorageVatpListenerState.Stopped, service.State);
            Assert.Equal(0, service.ActiveConnections);
        }

        [Fact]
        public async Task Drain_logs_unexpected_faulted_connection_tasks_and_still_completes()
        {
            var port = StorageServerTestOptions.GetFreeTcpPort();
            var runtime = CreateRuntime(port);
            var readiness = new AcmeCertificateReadiness();
            readiness.MarkReady();
            var logger = new CapturingListenerLogger();
            await using var service = new StorageVatpListenerService(
                runtime,
                TestListenerCertificates.CreatePublishedProvider(),
                NullStorageArticleOpenBoundary.Instance,
                readiness,
                logger);

            await service.StartAsync(CancellationToken.None);

            var connections = GetConnectionMap(service);
            var completed = Task.CompletedTask;
            var faulted = Task.FromException(new InvalidOperationException("injected-drain-fault"));
            Assert.True(connections.TryAdd(completed, 0));
            Assert.True(connections.TryAdd(faulted, 0));

            await service.StopAsync(CancellationToken.None);

            Assert.Equal(StorageVatpListenerState.Stopped, service.State);
            Assert.Contains("unexpected task failure", logger.Text, StringComparison.Ordinal);
            Assert.Contains("injected-drain-fault", logger.Text, StringComparison.Ordinal);
        }

        private static ConcurrentDictionary<Task, byte> GetConnectionMap(StorageVatpListenerService service)
        {
            var field = typeof(StorageVatpListenerService).GetField(
                "_connections",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var map = field.GetValue(service) as ConcurrentDictionary<Task, byte>;
            Assert.NotNull(map);
            return map;
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Safety;
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("condition was not met within the safety bound");
                }

                await Task.Delay(10);
            }
        }

        private static StorageServerRuntimeOptions CreateRuntime(int tlsPort)
        {
            var identity = StorageServerTestOptions.CreateValid();
            identity.BindAddress = ["127.0.0.1"];
            identity.BindPortTls = tlsPort;
            var acme = StorageServerTestOptions.CreateValidAcme(identity);
            acme.BindAddress = ["127.0.0.1"];
            acme.BindPortTls = tlsPort;
            return StorageServerRuntimeOptionsFactory.Create(identity, acme);
        }

        private sealed class CapturingListenerLogger : ILogger<StorageVatpListenerService>
        {
            private readonly List<string> _messages = [];

            public string Text
            {
                get
                {
                    lock (_messages)
                    {
                        return string.Join('\n', _messages);
                    }
                }
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_messages)
                {
                    var message = formatter(state, exception);
                    if (exception is not null)
                    {
                        message = $"{message} {exception}";
                    }

                    _messages.Add(message);
                }
            }
        }
    }
}
