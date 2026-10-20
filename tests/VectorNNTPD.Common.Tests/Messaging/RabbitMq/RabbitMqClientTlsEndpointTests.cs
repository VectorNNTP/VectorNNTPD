using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RabbitMQ.Client;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.Common.Tests.Messaging.RabbitMq
{
    /// <summary>
    /// Locks per-host TLS SNI identity for RabbitMQ multi-host failover without sharing one factory ServerName.
    /// </summary>
    public sealed class RabbitMqClientTlsEndpointTests
    {
        /// <summary>
        /// Each TLS-enabled endpoint uses the dialed hostname as <see cref="SslOption.ServerName"/>, not <c>Hosts[0]</c>.
        /// </summary>
        [Fact]
        public void CreateAmqpTcpEndpoints_assigns_server_name_matching_each_dialed_host()
        {
            var runtime = CreateTlsRuntime(["rabbit-a.example.test", "rabbit-b.example.test", "rabbit-c.example.test"]);
            var endpoints = RabbitMqClientConnectionFactory.CreateAmqpTcpEndpoints(runtime);

            Assert.Equal(3, endpoints.Count);
            Assert.All(endpoints, static ep => Assert.True(ep.Ssl.Enabled));
            Assert.Equal("rabbit-a.example.test", endpoints[0].HostName);
            Assert.Equal("rabbit-a.example.test", endpoints[0].Ssl.ServerName);
            Assert.Equal("rabbit-b.example.test", endpoints[1].HostName);
            Assert.Equal("rabbit-b.example.test", endpoints[1].Ssl.ServerName);
            Assert.Equal("rabbit-c.example.test", endpoints[2].HostName);
            Assert.Equal("rabbit-c.example.test", endpoints[2].Ssl.ServerName);
            Assert.Equal(5671, endpoints[0].Port);
            Assert.All(
                endpoints,
                static ep => Assert.Equal(
                    SslProtocols.Tls12 | SslProtocols.Tls13,
                    ep.Ssl.Version));
        }

        /// <summary>
        /// When the first host is skipped, the second endpoint still carries the second host's TLS identity.
        /// </summary>
        [Fact]
        public void CreateAmqpTcpEndpoints_second_host_keeps_its_own_tls_identity_for_failover()
        {
            var runtime = CreateTlsRuntime(["unreachable-a.example.test", "reachable-b.example.test"]);
            var endpoints = RabbitMqClientConnectionFactory.CreateAmqpTcpEndpoints(runtime);

            // Simulate ordered failover: first endpoint unused, second dialed.
            var dialed = endpoints[1];
            Assert.Equal("reachable-b.example.test", dialed.HostName);
            Assert.Equal("reachable-b.example.test", dialed.Ssl.ServerName);
            Assert.NotEqual(endpoints[0].Ssl.ServerName, dialed.Ssl.ServerName);
        }

        /// <summary>
        /// Concurrent mutation of one endpoint's ServerName does not change another host's SslOption.
        /// </summary>
        [Fact]
        public async Task CreateAmqpTcpEndpoints_ssl_options_are_independent_under_concurrent_mutation()
        {
            var runtime = CreateTlsRuntime(["host-a.example.test", "host-b.example.test", "host-c.example.test"]);
            var endpoints = RabbitMqClientConnectionFactory.CreateAmqpTcpEndpoints(runtime);

            Assert.NotSame(endpoints[0].Ssl, endpoints[1].Ssl);
            Assert.NotSame(endpoints[1].Ssl, endpoints[2].Ssl);

            var observed = new ConcurrentBag<(string Host, string ServerName)>();
            var tasks = new Task[endpoints.Count];
            for (var i = 0; i < endpoints.Count; i++)
            {
                var index = i;
                tasks[index] = Task.Run(() =>
                {
                    endpoints[index].Ssl.ServerName = $"mutated-{index}.example.test";
                    observed.Add((endpoints[index].HostName, endpoints[index].Ssl.ServerName));
                });
            }

            await Task.WhenAll(tasks).ConfigureAwait(true);

            Assert.Equal(3, observed.Count);
            Assert.Contains(("host-a.example.test", "mutated-0.example.test"), observed);
            Assert.Contains(("host-b.example.test", "mutated-1.example.test"), observed);
            Assert.Contains(("host-c.example.test", "mutated-2.example.test"), observed);
            Assert.Equal("mutated-0.example.test", endpoints[0].Ssl.ServerName);
            Assert.Equal("mutated-1.example.test", endpoints[1].Ssl.ServerName);
            Assert.Equal("mutated-2.example.test", endpoints[2].Ssl.ServerName);
        }

        /// <summary>
        /// TLS-disabled endpoints keep SSL disabled and do not invent a ServerName.
        /// </summary>
        [Fact]
        public void CreateAmqpTcpEndpoints_tls_disabled_leaves_ssl_disabled()
        {
            var options = RabbitMqOptionsValidatorTests.CreateValidConnectivityOnly();
            options.Hosts = ["127.0.0.1", "10.0.0.2"];
            options.EnableSsl = false;
            options.Port = 5672;
            var runtime = options.ToRuntimeOptions();

            var endpoints = RabbitMqClientConnectionFactory.CreateAmqpTcpEndpoints(runtime);

            Assert.Equal(2, endpoints.Count);
            Assert.All(endpoints, static ep => Assert.False(ep.Ssl.Enabled));
            Assert.Equal("127.0.0.1", endpoints[0].HostName);
            Assert.Equal("10.0.0.2", endpoints[1].HostName);
            Assert.Equal(5672, endpoints[0].Port);
        }

        /// <summary>
        /// The shared factory no longer pins <see cref="SslOption.ServerName"/> to <c>Hosts[0]</c>.
        /// </summary>
        [Fact]
        public void CreateClientFactory_does_not_pin_tls_server_name_to_first_host()
        {
            var runtime = CreateTlsRuntime(["first.example.test", "second.example.test"]);
            var factory = RabbitMqClientConnectionFactory.CreateClientFactory(runtime, "VectorNNTPD.Common.Tests:tls");

            Assert.True(factory.Ssl.Enabled);
            Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, factory.Ssl.Version);
            Assert.True(string.IsNullOrEmpty(factory.Ssl.ServerName));
        }

        /// <summary>
        /// TLS-disabled factory keeps SSL disabled with protocol defaults unchanged.
        /// </summary>
        [Fact]
        public void CreateClientFactory_tls_disabled_does_not_enable_ssl()
        {
            var options = RabbitMqOptionsValidatorTests.CreateValidConnectivityOnly();
            options.Hosts = ["127.0.0.1"];
            options.EnableSsl = false;
            var factory = RabbitMqClientConnectionFactory.CreateClientFactory(
                options.ToRuntimeOptions(),
                "VectorNNTPD.Common.Tests:plain");

            Assert.False(factory.Ssl.Enabled);
        }

        /// <summary>
        /// The SslOption.ServerName from CreateAmqpTcpEndpoints is the SNI name presented in a real TLS ClientHello.
        /// </summary>
        [Fact]
        public async Task Endpoint_ssl_server_name_is_presented_as_sni_during_tls_handshake()
        {
            var runtime = CreateTlsRuntime(["sni-primary.example.test", "sni-failover.example.test"]);
            var endpoints = RabbitMqClientConnectionFactory.CreateAmqpTcpEndpoints(runtime);
            var dialed = endpoints[1];

            using var cert = CreateSelfSignedServerCertificate("local-tls-fixture.example.test");
            var capturedSni = await CaptureClientSniAsync(
                    dialed.Ssl,
                    cert,
                    CancellationToken.None)
                .ConfigureAwait(true);

            Assert.Equal("sni-failover.example.test", capturedSni);
            Assert.Equal(dialed.Ssl.ServerName, capturedSni);
            Assert.NotEqual(endpoints[0].Ssl.ServerName, capturedSni);
        }

        /// <summary>
        /// Ordered failover that skips an unreachable first host presents the second host's SNI on the successful TLS dial.
        /// </summary>
        [Fact]
        public async Task Failover_from_unreachable_first_host_uses_second_host_sni()
        {
            var runtime = CreateTlsRuntime(["dead.example.test", "live.example.test"]);
            var endpoints = RabbitMqClientConnectionFactory.CreateAmqpTcpEndpoints(runtime);

            using var cert = CreateSelfSignedServerCertificate("local-tls-fixture.example.test");
            string? capturedSni = null;
            Exception? firstFailure = null;

            // Dead first endpoint: connect to a closed local port (TCP failure before TLS).
            try
            {
                using var dead = new TcpClient();
                await dead.ConnectAsync(IPAddress.Loopback, GetUnusedTcpPort()).ConfigureAwait(true);
                Assert.Fail("Expected the first host dial to fail.");
            }
            catch (SocketException ex)
            {
                firstFailure = ex;
            }

            Assert.NotNull(firstFailure);

            capturedSni = await CaptureClientSniAsync(endpoints[1].Ssl, cert, CancellationToken.None)
                .ConfigureAwait(true);

            Assert.Equal("live.example.test", capturedSni);
            Assert.Equal(endpoints[1].Ssl.ServerName, capturedSni);
            Assert.Equal("dead.example.test", endpoints[0].Ssl.ServerName);
        }

        /// <summary>
        /// Builds TLS-enabled runtime options with the supplied host list and AMQPS port.
        /// </summary>
        private static RabbitMqRuntimeOptions CreateTlsRuntime(string[] hosts)
        {
            var options = RabbitMqOptionsValidatorTests.CreateValidConnectivityOnly();
            options.Hosts = hosts;
            options.EnableSsl = true;
            options.Port = 5671;
            return options.ToRuntimeOptions();
        }

        /// <summary>
        /// Creates a disposable self-signed server certificate for local TLS handshake fixtures.
        /// </summary>
        private static X509Certificate2 CreateSelfSignedServerCertificate(string dnsName)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=" + dnsName,
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(dnsName);
            request.CertificateExtensions.Add(san.Build());
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(
                cert.Export(X509ContentType.Pfx),
                password: null,
                X509KeyStorageFlags.Exportable);
        }

        /// <summary>
        /// Accepts one TLS client on loopback and returns the SNI name from the ClientHello.
        /// </summary>
        private static async Task<string?> CaptureClientSniAsync(
            SslOption clientSsl,
            X509Certificate2 serverCertificate,
            CancellationToken cancellationToken)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = AcceptAndCaptureSniAsync(listener, serverCertificate, cancellationToken);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            await using var clientStream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: static (_, _, _, _) => true);
            var clientOptions = new SslClientAuthenticationOptions
            {
                TargetHost = clientSsl.ServerName,
                EnabledSslProtocols = clientSsl.Version,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };
            await clientStream.AuthenticateAsClientAsync(clientOptions, cancellationToken).ConfigureAwait(false);

            return await serverTask.ConfigureAwait(false);
        }

        /// <summary>
        /// Completes once the TLS handshake yields a ServerCertificateSelectionCallback host name.
        /// </summary>
        private static async Task<string?> AcceptAndCaptureSniAsync(
            TcpListener listener,
            X509Certificate2 serverCertificate,
            CancellationToken cancellationToken)
        {
            using var server = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            string? capturedSni = null;
            await using var ssl = new SslStream(server.GetStream(), leaveInnerStreamOpen: false);
            var serverOptions = new SslServerAuthenticationOptions
            {
                ServerCertificateSelectionCallback = (_, hostName) =>
                {
                    capturedSni = hostName;
                    return serverCertificate;
                },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ClientCertificateRequired = false,
            };
            await ssl.AuthenticateAsServerAsync(serverOptions, cancellationToken).ConfigureAwait(false);
            return capturedSni;
        }

        /// <summary>Binds an ephemeral local TCP port and returns its number after closing the socket.</summary>
        private static int GetUnusedTcpPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
