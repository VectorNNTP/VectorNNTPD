using System.Collections.Concurrent;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Listener;
using VectorNNTPD.BackFillerService.Networking;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.Retention;
using System.Net.Sockets;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Networking.Certificates;
using System.Net;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Listener
{
    /// <summary>
    /// TLS cache listener service: bind, admission, handshake/timeouts, article delivery, and lifecycle.
    /// </summary>

    public sealed class CacheListenerServiceTests(ITestOutputHelper output)
    {
        /// <summary>
        /// Start binds loopback, Running state, and repeated dispose reaches Stopped safely.
        /// </summary>

        [Fact]

        public async Task Starts_binds_ipv4_and_repeated_dispose_is_safe()
        {
            await using var context = await ListenerContext.StartAsync();

            Assert.Equal(CacheListenerState.Running, context.Service.State);
            Assert.Contains(context.Service.LocalEndPoints, static endpoint => endpoint is IPEndPoint ip && ip.Address.Equals(IPAddress.Loopback));

            await context.Service.DisposeAsync();
            Assert.Equal(CacheListenerState.Stopped, context.Service.State);
        }

        /// <summary>
        /// Missing certificate provider fails StartAsync and leaves Stopped.
        /// </summary>

        [Fact]

        public async Task Startup_fails_when_the_certificate_is_missing()
        {
            var runtime = CreateRuntime(GetFreePort());
            var service = new CacheListenerService(
                runtime,
                TestListenerCertificates.CreateUnavailableProvider(),
                ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                NullLogger<CacheListenerService>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Equal(CacheListenerState.Stopped, service.State);
        }

        /// <summary>
        /// Faulted connection task is logged; dispose still stops listener.
        /// </summary>

        [Fact]

        public async Task Unexpected_connection_task_fault_during_dispose_still_stops_the_listener()
        {
            var logger = new CollectingLogger<CacheListenerService>();
            var service = new CacheListenerService(
                CreateRuntime(GetFreePort()),
                TestListenerCertificates.CreateUnavailableProvider(),
                ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                logger);
            var connectionsField = typeof(CacheListenerService).GetField(
                "_connections",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var connections = Assert.IsType<ConcurrentDictionary<Task, byte>>(connectionsField?.GetValue(service));
            connections[Task.FromException(new NotSupportedException("connection boom"))] = 0;

            await service.DisposeAsync();

            Assert.Equal(CacheListenerState.Stopped, service.State);
            Assert.Contains(
                logger.Entries,
                entry => entry.EventId.Id == 5414
                    && entry.Message.Contains(nameof(NotSupportedException), StringComparison.Ordinal));
        }

        /// <summary>
        /// Session exception is logged; service remains Running with zero active connections.
        /// </summary>

        [Fact]

        public async Task Unexpected_connection_failure_is_logged_and_the_listener_stays_up()
        {
            var logger = new CollectingLogger<CacheListenerService>();
            var runtime = CreateRuntime(GetFreePort());
            var certificates = TestListenerCertificates.CreatePublishedProvider();
            var service = new CacheListenerService(runtime, certificates, new ThrowingRetention(), logger);
            await service.StartAsync(CancellationToken.None);
            try
            {
                await using var client = await ConnectAsync(runtime.BindPortTls);
                await ReadGreetingAsync(client.Stream);
                await client.Stream.WriteAsync("ARTICLE <a@b>\r\n"u8.ToArray());
                await ArticleWorkTestDeliveries.WaitUntilAsync(
                    () => service.State == CacheListenerState.Running && service.ActiveConnections == 0,
                    TimeSpan.FromSeconds(2));

                Assert.Equal(CacheListenerState.Running, service.State);
                Assert.Contains(
                    logger.Entries,
                    entry => entry.EventId.Id == 5411
                        && entry.Message.Contains(nameof(NotSupportedException), StringComparison.Ordinal));
            }
            finally
            {
                await service.DisposeAsync();
                await certificates.DisposeAsync();
            }
        }

        /// <summary>
        /// TLS client reads 201 posting-prohibited greeting after handshake.
        /// </summary>

        [Fact]

        public async Task Tls_client_receives_the_nntp_greeting()
        {
            await using var context = await ListenerContext.StartAsync();
            await using var client = await ConnectAsync(context);
            await ReadGreetingAsync(client.Stream);
        }

        /// <summary>
        /// ARTICLE returns stuffed wire bytes then 430 on second request; retention claim consumes entry.
        /// </summary>

        [Fact]

        public async Task Tls_article_writes_exact_bytes_and_the_next_request_is_430()
        {
            await using var context = await ListenerContext.StartAsync();
            var prepared = RetentionTestArticles.RetainPrepared(context.Authority, "<a@b>", ".\r\n");
            await using var client = await ConnectAsync(context);
            await ReadGreetingAsync(client.Stream);
            await client.Stream.WriteAsync("ARTICLE <a@b>\r\nARTICLE <a@b>\r\n"u8.ToArray());

            var status = "220 0 <a@b> article follows\r\n"u8.ToArray();
            var article = new byte[status.Length + prepared.ArtData.Length + ".\r\n"u8.Length];
            await client.Stream.ReadExactlyAsync(article);
            Assert.True(article.AsSpan(0, status.Length).SequenceEqual(status));
            Assert.True(article.AsSpan(status.Length, prepared.ArtData.Length).SequenceEqual(prepared.ArtData));
            Assert.True(article.AsSpan(status.Length + prepared.ArtData.Length).SequenceEqual(".\r\n"u8));
            Assert.Contains("\r\n..\r\n"u8, prepared.ArtData);

            var missing = new byte["430 No Such Article Found\r\n"u8.Length];
            await client.Stream.ReadExactlyAsync(missing);
            Assert.True(missing.AsSpan().SequenceEqual("430 No Such Article Found\r\n"u8));
            Assert.Equal(0, context.Authority.RetainedCount);
        }

        /// <summary>
        /// Certificate renewal updates served cert without stopping listener.
        /// </summary>

        [Fact]

        public async Task Renewal_publishes_to_the_running_listener_without_restart()
        {
            await using var context = await ListenerContext.StartAsync();
            Assert.Equal(CacheListenerState.Running, context.Service.State);

            await using (var initial = await ConnectAsync(context))
            {
                Assert.Contains(
                    "backfiller.test",
                    initial.Stream.RemoteCertificate!.Subject,
                    StringComparison.OrdinalIgnoreCase);
            }

            using var renewed = TestListenerCertificates.CreateSelfSigned("backfiller-renewed.test");
            context.Certificates.PublishFromPfx(renewed.Export(X509ContentType.Pfx, "test"), "test");

            await using var client = await ConnectAsync(context);
            var remote = client.Stream.RemoteCertificate;
            Assert.NotNull(remote);
            Assert.Contains("backfiller-renewed.test", remote.Subject, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CN=backfiller.test", remote.Subject, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(CacheListenerState.Running, context.Service.State);
        }

        /// <summary>
        /// Invalid TLS ClientHello does not stop Running listener.
        /// </summary>

        [Fact]

        public async Task Handshake_failure_does_not_fault_the_listener()
        {
            await using var context = await ListenerContext.StartAsync();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, context.Port);
            tcp.GetStream().WriteByte(0x16);
            tcp.Close();
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Service.State == CacheListenerState.Running && context.Service.ActiveConnections == 0,
                TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// MaxActiveConnections prevents second simultaneous connection.
        /// </summary>

        [Fact]

        public async Task Max_connections_rejects_additional_clients()
        {
            var runtime = CreateRuntime(GetFreePort(), maxConnections: 1);
            await using var context = await ListenerContext.StartAsync(runtime: runtime);
            await using var held = await ConnectAsync(context);
            using var overflow = new TcpClient();
            await overflow.ConnectAsync(IPAddress.Loopback, context.Port);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => !overflow.Connected || overflow.Available >= 0,
                TimeSpan.FromSeconds(2));
            Assert.Equal(1, context.Service.ActiveConnections);
        }

        /// <summary>
        /// Writer timeout/failure during command flood releases admission slot for a new client.
        /// </summary>

        [Fact]

        public async Task Writer_failure_releases_connection_admission()
        {
            var runtime = CreateRuntime(GetFreePort(), maxConnections: 1);
            runtime = runtime with
            {
                Listener = runtime.Listener with { IoProgressTimeout = TimeSpan.FromMilliseconds(200) },
            };
            await using var context = await ListenerContext.StartAsync(runtime: runtime);
            Assert.Equal(0, context.Service.ActiveConnections);

            await using var client = await ConnectAsync(context);
            using var floodCts = new CancellationTokenSource();
            var flood = StartRejectedCommandFlood(client, floodCts);

            try
            {
                await ArticleWorkTestDeliveries.WaitUntilAsync(
                    () => context.Service.ActiveConnections == 0,
                    TimeSpan.FromSeconds(15));
            }
            finally
            {
                await ObserveFloodAsync(floodCts, flood);
            }

            Assert.True(flood.IsCompleted);
            Assert.Equal(0, context.Service.ActiveConnections);
            await using var second = await ConnectAsync(context);
            await ReadGreetingAsync(second.Stream);
            Assert.Equal(1, context.Service.ActiveConnections);
        }

        /// <summary>
        /// Test harness observes flood task completion around synthetic wait timeout.
        /// </summary>

        [Fact]

        public async Task Writer_wait_timeout_observes_the_flood_task_before_disposal()
        {
            await using var context = await ListenerContext.StartAsync();
            await using var client = await ConnectAsync(context);
            using var floodCts = new CancellationTokenSource();
            var flood = StartRejectedCommandFlood(client, floodCts);
            OperationCanceledException? timedOut = null;
            try
            {
                await ArticleWorkTestDeliveries.WaitUntilAsync(static () => false, TimeSpan.FromMilliseconds(40));
            }
            catch (OperationCanceledException ex)
            {
                timedOut = ex;
            }
            finally
            {
                await ObserveFloodAsync(floodCts, flood);
            }

            Assert.NotNull(timedOut);
            Assert.True(flood.IsCompleted);
        }

        /// <summary>
        /// Dispose during TCP connect/handshake leaves Stopped without crashing.
        /// </summary>

        [Fact]

        public async Task Shutdown_during_handshake_is_safe()
        {
            await using var context = await ListenerContext.StartAsync();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, context.Port);
            await context.Service.DisposeAsync();
            Assert.Equal(CacheListenerState.Stopped, context.Service.State);
        }

        /// <summary>
        /// TlsHandshakeTimeout releases admission while listener stays Running.
        /// </summary>

        [Fact]

        public async Task Handshake_timeout_releases_the_connection_slot()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BindPortTls = GetFreePort();
            options.BindAddress = ["127.0.0.1"];
            options.Listener.TlsHandshakeTimeoutSeconds = 1;
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());
            runtime = runtime with
            {
                Listener = runtime.Listener with { TlsHandshakeTimeout = TimeSpan.FromMilliseconds(80) },
            };

            await using var context = await ListenerContext.StartAsync(runtime: runtime);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, context.Port);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Service.ActiveConnections == 0,
                TimeSpan.FromSeconds(2));
            Assert.Equal(CacheListenerState.Running, context.Service.State);
        }

        /// <summary>
        /// IoProgressTimeout closes idle authenticated TLS session.
        /// </summary>

        [Fact]

        public async Task Io_timeout_closes_an_idle_authenticated_connection()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BindPortTls = GetFreePort();
            options.BindAddress = ["127.0.0.1"];
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());
            runtime = runtime with
            {
                Listener = runtime.Listener with { IoProgressTimeout = TimeSpan.FromMilliseconds(80) },
            };

            await using var context = await ListenerContext.StartAsync(runtime: runtime);
            await using var client = await ConnectAsync(context);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Service.ActiveConnections == 0,
                TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// BindAddress * includes IPv6Any endpoint when started.
        /// </summary>

        [Fact]

        public async Task Wildcard_bind_listens_on_ipv4_any()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BindPortTls = GetFreePort();
            options.BindAddress = ["*"];
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());

            await using var service = new CacheListenerService(
                runtime,
                TestListenerCertificates.CreatePublishedProvider(),
                ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                NullLogger<CacheListenerService>.Instance);
            await service.StartAsync(CancellationToken.None);
            Assert.Contains(
                service.LocalEndPoints,
                static endpoint => endpoint is IPEndPoint ip && ip.Address.Equals(IPAddress.IPv6Any));
            await service.DisposeAsync();
            Assert.Equal(CacheListenerState.Stopped, service.State);
        }

        /// <summary>
        /// StreamCacheListenerTransport ReadAsync throws TimeoutException when peer stalls.
        /// </summary>

        [Fact]

        public async Task Stream_transport_enforces_io_progress_timeout()
        {
            await using var hanging = new HangingStream();
            var transport = new StreamCacheListenerTransport(hanging, TimeSpan.FromMilliseconds(40), leaveInnerStreamOpen: true);
            await Assert.ThrowsAsync<TimeoutException>(async () =>
            {
                var buffer = new byte[8];
                _ = await transport.ReadAsync(buffer, CancellationToken.None);
            });
        }

        /// <summary>
        /// TLS 1.2 client completes handshake and reads greeting.
        /// </summary>

        [Fact]

        public async Task Tls12_client_can_complete_handshake()
        {
            await AssertHandshakeProtocolAsync(SslProtocols.Tls12);
        }

        /// <summary>
        /// TLS 1.3 handshake when platform supports SslStream TLS 1.3.
        /// </summary>

        [Fact]

        public async Task Tls13_client_can_complete_handshake_when_the_platform_supports_it()
        {
            try
            {
                await AssertHandshakeProtocolAsync(SslProtocols.Tls13);
            }
            catch (Exception ex) when (ex is AuthenticationException or PlatformNotSupportedException)
            {
                output.WriteLine($"SKIP: TLS 1.3 is not available for SslStream on this platform/runtime ({ex.GetType().Name}).");
                return;
            }
        }

        /// <summary>
        /// Non-NNTP GET receives 500 then QUIT closes with 205.
        /// </summary>

        [Fact]

        public async Task Legacy_get_is_an_unknown_nntp_command()
        {
            await using var context = await ListenerContext.StartAsync();
            await using var client = await ConnectAsync(context);
            await ReadGreetingAsync(client.Stream);
            // A legacy cache request is not an NNTP article command.
            await client.Stream.WriteAsync("GET /legacy\r\n"u8.ToArray());
            var unknown = new byte["500 Unknown command\r\n"u8.Length];
            await client.Stream.ReadExactlyAsync(unknown);
            Assert.True(unknown.AsSpan().SequenceEqual("500 Unknown command\r\n"u8));
            await client.Stream.WriteAsync("QUIT\r\n"u8.ToArray());
            var closing = new byte["205 Connection closing\r\n"u8.Length];
            await client.Stream.ReadExactlyAsync(closing);
            Assert.True(closing.AsSpan().SequenceEqual("205 Connection closing\r\n"u8));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Service.State == CacheListenerState.Running && context.Service.ActiveConnections == 0,
                TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// Bind ::1 when OS supports IPv6 loopback bind.
        /// </summary>

        [Fact]

        public async Task Explicit_ipv6_loopback_binds_when_the_platform_supports_it()
        {
            if (!Socket.OSSupportsIPv6)
            {
                output.WriteLine("SKIP: Socket.OSSupportsIPv6 is false on this platform.");
                return;
            }

            var port = GetFreePort();
            var runtime = CreateRuntime(port) with
            {
                BindAddressTokens = ["::1"],
            };

            CacheListenerService? service = null;
            try
            {
                service = new CacheListenerService(
                    runtime,
                    TestListenerCertificates.CreatePublishedProvider(),
                    ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                    NullLogger<CacheListenerService>.Instance);
                await service.StartAsync(CancellationToken.None);
                Assert.Contains(
                    service.LocalEndPoints,
                    static endpoint => endpoint is IPEndPoint ip && ip.Address.Equals(IPAddress.IPv6Loopback));
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode is SocketError.AddressFamilyNotSupported
                    or SocketError.ProtocolNotSupported
                    or SocketError.AddressNotAvailable)
            {
                output.WriteLine($"SKIP: IPv6 loopback bind is not available ({ex.SocketErrorCode}).");
                return;
            }
            finally
            {
                if (service is not null)
                {
                    await service.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// Production Listener sources avoid blocking sync waits on async paths.
        /// </summary>

        [Fact]

        public void Production_listener_code_does_not_block_synchronously()
        {
            var directory = FindListenerSourceDirectory();
            foreach (var text in Directory.GetFiles(directory, "*.cs").Select(static file => File.ReadAllText(file)))
            {
                Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
            }
        }

        /// <summary>Connects with the given TLS protocol mask, asserts negotiation, and reads the NNTP greeting.</summary>
        /// <param name="protocol">Enabled client SSL protocols for the handshake.</param>
        private static async Task AssertHandshakeProtocolAsync(SslProtocols protocol)
        {
            await using var context = await ListenerContext.StartAsync();
            await using var client = await ConnectAsync(context, protocol);
            Assert.Equal(protocol, client.Stream.SslProtocol);
            await ReadGreetingAsync(client.Stream);
        }

        /// <summary>
        /// Accepted sockets receive independent buffer limits and <see cref="Socket.NoDelay"/> before TLS.
        /// The greeting still arrives, and the options remain after that write.
        /// </summary>
        [Fact]
        public async Task Accepted_socket_is_configured_before_tls_and_remains_configured_after_the_greeting()
        {
            const int send = 256 * 1024;
            const int receive = 128 * 1024;
            var port = GetFreePort();
            var runtime = CreateRuntime(port);
            runtime = runtime with
            {
                Listener = runtime.Listener with
                {
                    SendBufferBytes = send,
                    ReceiveBufferBytes = receive,
                },
            };
            var logger = new CollectingLogger<CacheListenerService>();
            var service = new CacheListenerService(
                runtime,
                TestListenerCertificates.CreatePublishedProvider(),
                ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                logger);
            Socket? accepted = null;
            service.AfterAcceptedSocketConfigured = socket => accepted = socket;
            await service.StartAsync(CancellationToken.None);
            try
            {
                await using var client = await ConnectAsync(port);
                Assert.NotNull(accepted);
                AssertRequestedBuffers(accepted, send, receive);
                await ReadGreetingAsync(client.Stream);
                AssertRequestedBuffers(accepted, send, receive);
                Assert.Contains(
                    logger.Entries,
                    entry => entry.EventId.Id == 5402
                        && entry.Message.Contains(send.ToString(), StringComparison.Ordinal)
                        && entry.Message.Contains(receive.ToString(), StringComparison.Ordinal));
            }
            finally
            {
                await service.DisposeAsync();
            }
        }

        /// <summary>Two overlapping accepts each get the accepted-socket options, and both sessions stay usable.</summary>
        [Fact]
        public async Task Concurrent_accepts_each_configure_their_own_socket()
        {
            const int send = 256 * 1024;
            const int receive = 128 * 1024;
            var port = GetFreePort();
            var runtime = CreateRuntime(port, maxConnections: 8);
            runtime = runtime with
            {
                Listener = runtime.Listener with
                {
                    SendBufferBytes = send,
                    ReceiveBufferBytes = receive,
                },
            };
            var service = new CacheListenerService(
                runtime,
                TestListenerCertificates.CreatePublishedProvider(),
                ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                NullLogger<CacheListenerService>.Instance);
            var seen = new ConcurrentBag<Socket>();
            service.AfterAcceptedSocketConfigured = seen.Add;
            await service.StartAsync(CancellationToken.None);
            try
            {
                var firstConnect = ConnectAsync(port);
                var secondConnect = ConnectAsync(port);
                await using var first = await firstConnect;
                await using var second = await secondConnect;
                Assert.Equal(2, seen.Count);
                foreach (var socket in seen)
                {
                    AssertRequestedBuffers(socket, send, receive);
                }

                await ReadGreetingAsync(first.Stream);
                await ReadGreetingAsync(second.Stream);
            }
            finally
            {
                await service.DisposeAsync();
            }
        }

        /// <summary>
        /// A socket-option failure releases the only admission slot, and the next connection still completes.
        /// </summary>
        [Fact]
        public async Task Accepted_socket_option_failure_releases_admission_and_the_listener_stays_usable()
        {
            var port = GetFreePort();
            var logger = new CollectingLogger<CacheListenerService>();
            var service = new CacheListenerService(
                CreateRuntime(port, maxConnections: 1),
                TestListenerCertificates.CreatePublishedProvider(),
                ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                logger);
            var failed = 0;
            service.BeforeAcceptedSocketOptions = _ =>
            {
                if (Interlocked.Increment(ref failed) == 1)
                {
                    throw new SocketException((int)SocketError.InvalidArgument);
                }
            };
            await service.StartAsync(CancellationToken.None);
            try
            {
                var error = await Record.ExceptionAsync(() => ConnectAsync(port));
                Assert.True(error is IOException or AuthenticationException or SocketException);
                await ArticleWorkTestDeliveries.WaitUntilAsync(
                    () => service.ActiveConnections == 0,
                    TimeSpan.FromSeconds(2));
                Assert.Contains(
                    logger.Entries,
                    entry => entry.EventId.Id == 5415
                        && entry.Message.Contains(nameof(SocketError.InvalidArgument), StringComparison.Ordinal));

                await using var client = await ConnectAsync(port);
                await ReadGreetingAsync(client.Stream);
            }
            finally
            {
                await service.DisposeAsync();
            }
        }

        /// <summary>An IPv6 accepted socket gets the same options before the greeting when the platform can bind ::1.</summary>
        [Fact]
        public async Task Accepted_ipv6_socket_is_configured_before_the_greeting_when_supported()
        {
            if (!Socket.OSSupportsIPv6)
            {
                output.WriteLine("SKIP: Socket.OSSupportsIPv6 is false on this platform.");
                return;
            }

            const int send = 256 * 1024;
            const int receive = 128 * 1024;
            var port = GetFreePort();
            var runtime = CreateRuntime(port) with { BindAddressTokens = ["::1"] };
            runtime = runtime with
            {
                Listener = runtime.Listener with
                {
                    SendBufferBytes = send,
                    ReceiveBufferBytes = receive,
                },
            };
            var service = new CacheListenerService(
                runtime,
                TestListenerCertificates.CreatePublishedProvider(),
                ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                NullLogger<CacheListenerService>.Instance);
            Socket? accepted = null;
            service.AfterAcceptedSocketConfigured = socket => accepted = socket;
            try
            {
                await service.StartAsync(CancellationToken.None);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable)
            {
                output.WriteLine("SKIP: IPv6 loopback bind is not available: {0}", ex.SocketErrorCode);
                await service.DisposeAsync();
                return;
            }

            try
            {
                using var tcp = new TcpClient(AddressFamily.InterNetworkV6);
                await tcp.ConnectAsync(IPAddress.IPv6Loopback, port);
                await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, static (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                });
                Assert.NotNull(accepted);
                Assert.Equal(AddressFamily.InterNetworkV6, accepted.AddressFamily);
                AssertRequestedBuffers(accepted, send, receive);
                await ReadGreetingAsync(ssl);
                AssertRequestedBuffers(accepted, send, receive);
            }
            finally
            {
                await service.DisposeAsync();
            }
        }

        /// <summary>Reads exactly the 201 posting-prohibited greeting from the TLS stream.</summary>
        /// <param name="stream">Authenticated client-side SSL stream.</param>
        private static async Task ReadGreetingAsync(SslStream stream)
        {
            var expected = "201 NNTP Service Ready, posting prohibited\r\n"u8.ToArray();
            var greeting = new byte[expected.Length];
            await stream.ReadExactlyAsync(greeting);
            Assert.Equal(expected, greeting);
        }

        /// <summary>Background loop sending invalid NOPE commands until canceled or the connection fails.</summary>
        /// <param name="client">TLS client whose stream receives the flood.</param>
        /// <param name="floodCts">Cancellation that stops the flood loop.</param>
        private static Task StartRejectedCommandFlood(TlsClient client, CancellationTokenSource floodCts) =>
            Task.Run(async () =>
            {
                var command = "NOPE\r\n"u8.ToArray();
                while (!floodCts.IsCancellationRequested)
                {
                    try
                    {
                        await client.Stream.WriteAsync(command, floodCts.Token);
                    }
                    catch (OperationCanceledException) when (floodCts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (IOException)
                    {
                        return;
                    }
                }
            });

        /// <summary>Cancels the flood and awaits its task, ignoring expected IO failures during teardown.</summary>
        /// <param name="floodCts">Source used to cancel the flood.</param>
        /// <param name="flood">Background flood task from <see cref="StartRejectedCommandFlood"/>.</param>
        private async Task ObserveFloodAsync(CancellationTokenSource floodCts, Task flood)
        {
            try
            {
                await floodCts.CancelAsync();
            }
            finally
            {
                try
                {
                    await flood;
                }
                catch (IOException ex)
                {
                    output.WriteLine(
                        "Ignoring expected IOException while awaiting the flood task during cancellation or shutdown: {0}",
                        ex.Message);
                }
            }
        }

        /// <summary>Opens a TLS client to the listener started in <paramref name="context"/>.</summary>
        /// <param name="context">Running listener exposing the bind port.</param>
        /// <param name="protocols">Enabled client SSL protocols.</param>
        private static Task<TlsClient> ConnectAsync(
            ListenerContext context,
            SslProtocols protocols = SslProtocols.Tls12 | SslProtocols.Tls13) =>
            ConnectAsync(context.Port, protocols);

        /// <summary>Connects to loopback on <paramref name="port"/> and completes TLS with certificate validation disabled.</summary>
        /// <param name="port">Listener TLS port.</param>
        /// <param name="protocols">Enabled client SSL protocols.</param>
        private static async Task<TlsClient> ConnectAsync(
            int port,
            SslProtocols protocols = SslProtocols.Tls12 | SslProtocols.Tls13)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, static (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });
            return new TlsClient(tcp, ssl);
        }

        /// <summary>
        /// Transient accept SocketException is followed by the configured backoff before the next accept attempt.
        /// </summary>
        [Fact]
        public async Task Transient_accept_failure_delays_before_retry_and_resets_after_success()
        {
            var logger = new CollectingLogger<CacheListenerService>();
            var port = GetFreePort();
            await using var certificates = TestListenerCertificates.CreatePublishedProvider();
            var service = CreateListenerService(CreateRuntime(port), certificates, logger);
            var acceptAttempts = 0;
            var delayCalls = new ConcurrentBag<TimeSpan>();
            service.AcceptFailureDelayAsync = (delay, _) =>
            {
                delayCalls.Add(delay);
                return Task.CompletedTask;
            };
            service.AcceptAsyncOverride = (listenSocket, cancellationToken) =>
            {
                var attempt = Interlocked.Increment(ref acceptAttempts);
                if (attempt <= 2)
                {
                    return ValueTask.FromException<Socket>(
                        new SocketException((int)SocketError.ConnectionReset));
                }

                return listenSocket.AcceptAsync(cancellationToken);
            };

            await service.StartAsync(CancellationToken.None);
            try
            {
                await using var client = await ConnectAsync(port);
                await ReadGreetingAsync(client.Stream);
                await ArticleWorkTestDeliveries.WaitUntilAsync(
                    () => delayCalls.Count >= 2 && acceptAttempts >= 3,
                    TimeSpan.FromSeconds(5));

                Assert.Equal(2, delayCalls.Count);
                Assert.All(delayCalls, static delay => Assert.Equal(CacheListenerService.AcceptFailureRetryDelay, delay));
                Assert.Contains(logger.Entries, static entry => entry.EventId.Id == 5416);
                Assert.Contains(logger.Entries, static entry => entry.EventId.Id == 5417);
                Assert.Equal(CacheListenerState.Running, service.State);
                Assert.False(service.Execution!.IsFaulted);
            }
            finally
            {
                await service.DisposeAsync();
            }
        }

        /// <summary>
        /// Consecutive accept failures reach the threshold, fault Execution, and stop with rate-limited logging.
        /// </summary>
        [Fact]
        public async Task Consecutive_accept_failures_reach_threshold_and_fault_execution()
        {
            var logger = new CollectingLogger<CacheListenerService>();
            var port = GetFreePort();
            await using var certificates = TestListenerCertificates.CreatePublishedProvider();
            var service = CreateListenerService(CreateRuntime(port), certificates, logger);
            var acceptAttempts = 0;
            var delayCalls = 0;
            service.AcceptFailureDelayAsync = (_, _) =>
            {
                Interlocked.Increment(ref delayCalls);
                return Task.CompletedTask;
            };
            service.AcceptAsyncOverride = (_, _) =>
            {
                Interlocked.Increment(ref acceptAttempts);
                return ValueTask.FromException<Socket>(
                    new SocketException((int)SocketError.NetworkDown));
            };

            await service.StartAsync(CancellationToken.None);
            var execution = service.Execution;
            Assert.NotNull(execution);
            var fault = await Assert.ThrowsAnyAsync<Exception>(async () => await execution);
            var invalid = Assert.IsType<InvalidOperationException>(
                fault is AggregateException aggregate
                    ? Assert.Single(aggregate.Flatten().InnerExceptions.OfType<InvalidOperationException>())
                    : fault);
            Assert.Contains("consecutive", invalid.Message, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<SocketException>(invalid.InnerException);
            Assert.Equal(CacheListenerService.MaxConsecutiveAcceptFailures, acceptAttempts);
            Assert.Equal(CacheListenerService.MaxConsecutiveAcceptFailures - 1, delayCalls);
            Assert.Equal(
                2,
                logger.Entries.Count(static entry => entry.EventId.Id == 5416));
            Assert.Contains(logger.Entries, static entry => entry.EventId.Id == 5418);
            Assert.DoesNotContain(logger.Entries, static entry => entry.EventId.Id == 5417);

            await service.DisposeAsync();
            Assert.Equal(CacheListenerState.Stopped, service.State);
            Assert.Contains(logger.Entries, static entry => entry.EventId.Id == 5419);
        }

        /// <summary>
        /// An unexpected accept-task fault is not treated as an expected dispose observation; cleanup still stops,
        /// then dispose rethrows so the programming error remains visible.
        /// </summary>
        [Fact]
        public async Task Unexpected_accept_fault_is_not_swallowed_by_dispose()
        {
            var logger = new CollectingLogger<CacheListenerService>();
            var port = GetFreePort();
            await using var certificates = TestListenerCertificates.CreatePublishedProvider();
            var service = CreateListenerService(CreateRuntime(port), certificates, logger);
            service.AcceptAsyncOverride = (_, _) =>
                ValueTask.FromException<Socket>(new DivideByZeroException("accept boom"));

            await service.StartAsync(CancellationToken.None);
            var execution = service.Execution;
            Assert.NotNull(execution);
            await Assert.ThrowsAnyAsync<Exception>(async () => await execution);
            Assert.True(execution.IsFaulted);

            var disposeFault = await Record.ExceptionAsync(async () => await service.DisposeAsync());
            Assert.NotNull(disposeFault);
            Assert.Contains(
                disposeFault is AggregateException aggregate
                    ? aggregate.Flatten().InnerExceptions
                    : [disposeFault],
                static ex => ex is DivideByZeroException || ex.GetBaseException() is DivideByZeroException);
            Assert.DoesNotContain(logger.Entries, static entry => entry.EventId.Id == 5419);
            Assert.Equal(CacheListenerState.Stopped, service.State);
        }

        /// <summary>
        /// Cancellation during accept backoff exits the loop without treating shutdown as an unexpected failure.
        /// </summary>
        [Fact]
        public async Task Cancellation_during_accept_backoff_exits_without_faulting_execution()
        {
            var logger = new CollectingLogger<CacheListenerService>();
            var port = GetFreePort();
            await using var certificates = TestListenerCertificates.CreatePublishedProvider();
            var service = CreateListenerService(CreateRuntime(port), certificates, logger);
            var enteredDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.AcceptAsyncOverride = (_, _) => ValueTask.FromException<Socket>(
                new SocketException((int)SocketError.Interrupted));
            service.AcceptFailureDelayAsync = async (_, cancellationToken) =>
            {
                enteredDelay.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            };

            await service.StartAsync(CancellationToken.None);
            var execution = service.Execution;
            Assert.NotNull(execution);
            await enteredDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.DisposeAsync();

            Assert.True(execution.IsCompletedSuccessfully);
            Assert.Equal(CacheListenerState.Stopped, service.State);
            Assert.DoesNotContain(logger.Entries, static entry => entry.EventId.Id == 5418);
        }

        /// <summary>
        /// Shutdown cancellation while AcceptAsync is waiting exits cleanly without an unexpected failure log.
        /// </summary>
        [Fact]
        public async Task Shutdown_cancellation_during_accept_exits_cleanly()
        {
            var logger = new CollectingLogger<CacheListenerService>();
            var port = GetFreePort();
            await using var certificates = TestListenerCertificates.CreatePublishedProvider();
            var service = CreateListenerService(CreateRuntime(port), certificates, logger);
            var enteredAccept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.AcceptAsyncOverride = async (_, cancellationToken) =>
            {
                enteredAccept.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Accept override should not resume after cancellation.");
            };

            await service.StartAsync(CancellationToken.None);
            var execution = service.Execution;
            Assert.NotNull(execution);
            await enteredAccept.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.DisposeAsync();

            Assert.True(execution.IsCompletedSuccessfully);
            Assert.Equal(CacheListenerState.Stopped, service.State);
            Assert.DoesNotContain(logger.Entries, static entry => entry.EventId.Id == 5416);
            Assert.DoesNotContain(logger.Entries, static entry => entry.EventId.Id == 5418);
        }

        /// <summary>Builds a production-shaped listener with injectable logger for accept-loop tests.</summary>
        private static CacheListenerService CreateListenerService(
            BackFillerRuntimeOptions runtime,
            ITlsCertificateContextProvider certificates,
            ILogger<CacheListenerService> logger)
        {
            var readiness = new AcmeCertificateReadiness();
            readiness.MarkReady();
            return new CacheListenerService(
                runtime,
                certificates,
                ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                readiness,
                new BackFillerStartupJournal(),
                logger);
        }

        /// <summary>Builds valid runtime options bound to loopback <paramref name="port"/> with a connection cap.</summary>
        /// <param name="port">TLS bind port for the listener under test.</param>
        /// <param name="maxConnections">Maximum concurrent active connections.</param>
        private static BackFillerRuntimeOptions CreateRuntime(int port, int maxConnections = 8)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BindPortTls = port;
            options.BindAddress = ["127.0.0.1"];
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());
            return runtime with
            {
                Listener = runtime.Listener with { MaxActiveConnections = maxConnections },
            };
        }

        /// <summary>
        /// Asserts Nagle is off, TCP keepalive is on, and the OS-reported buffers are at least the requested sizes and are not the same value.
        /// </summary>
        /// <param name="socket">Accepted socket under test.</param>
        /// <param name="send">Requested send-buffer limit.</param>
        /// <param name="receive">Requested receive-buffer limit. Must differ from <paramref name="send"/>.</param>
        private static void AssertRequestedBuffers(Socket socket, int send, int receive)
        {
            Assert.True(socket.NoDelay);
            Assert.True(TcpSocketOptions.IsKeepAliveEnabled(socket));
            Assert.True(socket.SendBufferSize >= send, $"send buffer {socket.SendBufferSize} is below {send}");
            Assert.True(socket.ReceiveBufferSize >= receive, $"receive buffer {socket.ReceiveBufferSize} is below {receive}");
            Assert.NotEqual(socket.SendBufferSize, socket.ReceiveBufferSize);
        }

        /// <summary>Allocates an ephemeral TCP port by binding loopback port zero.</summary>
        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>Locates <c>src/VectorNNTPD.BackFillerService/Listener</c> by walking up from the test output directory.</summary>
        private static string FindListenerSourceDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = RelativePath.Under(directory.FullName, "src", "VectorNNTPD.BackFillerService", "Listener");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate src/VectorNNTPD.BackFillerService/Listener.");
        }

        /// <summary>
        /// Stream whose ReadAsync waits until canceled for IO timeout tests.
        /// </summary>

        private sealed class HangingStream : Stream
        {
            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => false;

            /// <inheritdoc />
            public override long Length => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <summary>Never completes until <paramref name="cancellationToken"/> fires, exercising read timeouts.</summary>
            /// <param name="buffer">Unused; no bytes are ever copied.</param>
            /// <param name="cancellationToken">Cancellation that ends the infinite delay.</param>
            /// <returns>Zero after cancellation; otherwise the delay does not return.</returns>
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
        }

        /// <summary>
        /// Owns TcpClient and SslStream for listener integration tests.
        /// </summary>

        private sealed class TlsClient(TcpClient tcp, SslStream stream) : IAsyncDisposable
        {
            /// <summary>Negotiated SSL stream used to send NNTP commands to the listener.</summary>
            public SslStream Stream { get; } = stream;

            /// <summary>Disposes the SSL stream and underlying TCP client.</summary>
            public async ValueTask DisposeAsync()
            {
                await Stream.DisposeAsync();
                tcp.Dispose();
            }
        }

        /// <summary>
        /// IArticleRetentionAuthority double that throws on all operations for failure logging test.
        /// </summary>

        private sealed class ThrowingRetention : IArticleRetentionAuthority
        {
            /// <summary>Reports zero sweep interval; no retention backing store exists.</summary>
            public TimeSpan SweepInterval => TimeSpan.Zero;

            /// <summary>Always zero retained payload bytes.</summary>
            public long RetainedPayloadBytes => 0;

            /// <summary>Always zero retained articles.</summary>
            public int RetainedCount => 0;

            /// <summary>Always throws to simulate retention failure during listener handling.</summary>
            /// <param name="messageId">Unused; throws before processing.</param>
            /// <param name="requestId">Unused; throws before processing.</param>
            /// <param name="record">Unused; throws before processing.</param>
            /// <param name="selectedDateHeaderName">Unused; throws before processing.</param>
            /// <param name="endpointFqdn">Unused; throws before processing.</param>
            public ArticleRetentionResult RetainCanonical(
                string messageId,
                Guid requestId,
                ArticleRecord record,
                NntpArticleHeaderName selectedDateHeaderName,
                string? endpointFqdn = null) =>
                throw new NotSupportedException("retention boom");

            /// <summary>Always throws to simulate delivery claim failure.</summary>
            /// <param name="messageIdOctets">Unused; throws before processing.</param>
            public ArticleDeliveryClaimResult TryClaimForDelivery(ReadOnlySpan<byte> messageIdOctets) =>
                throw new NotSupportedException("retention boom");

            /// <summary>Always throws to simulate cancel failure.</summary>
            /// <param name="requestId">Unused; throws before processing.</param>
            public bool TryCancelPendingRequest(Guid requestId) =>
                throw new NotSupportedException("retention boom");

            /// <summary>Always throws to simulate sweep failure.</summary>
            public long SweepExpired() => throw new NotSupportedException("retention boom");

            /// <summary>No-op shutdown hook; other members still throw if invoked.</summary>
            public void BeginShutdown()
            {
            }
        }

        /// <summary>
        /// Started CacheListenerService with retention and certificates for integration tests.
        /// </summary>

        private sealed class ListenerContext : IAsyncDisposable
        {
            /// <summary>Captures started service, shared retention, bind port, and certificate provider from <see cref="StartAsync"/>.</summary>
            /// <param name="service">Running listener service under test.</param>
            /// <param name="authority">Retention store used by the listener and disposed with the context.</param>
            /// <param name="port">TLS bind port from runtime options.</param>
            /// <param name="certificates">Published test certificate context disposed on teardown.</param>
            private ListenerContext(
                CacheListenerService service,
                ArticleRetentionAuthority authority,
                int port,
                TlsCertificateContextProvider certificates)
            {
                Service = service;
                Authority = authority;
                Port = port;
                Certificates = certificates;
            }

            /// <summary>Running cache listener service under test.</summary>
            public CacheListenerService Service { get; }

            /// <summary>Shared retention authority wired into the listener.</summary>
            public ArticleRetentionAuthority Authority { get; }

            /// <summary>Published test TLS certificate provider disposed with the context.</summary>
            public TlsCertificateContextProvider Certificates { get; }

            /// <summary>TLS bind port from runtime options.</summary>
            public int Port { get; }

            /// <summary>Starts a listener on a free or supplied runtime with test retention and certificates.</summary>
            /// <param name="runtime">Optional runtime; defaults to loopback on an ephemeral port.</param>
            internal static async Task<ListenerContext> StartAsync(BackFillerRuntimeOptions? runtime = null)
            {
                runtime ??= CreateRuntime(GetFreePort());
                var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024 * 1024);
                var certificates = TestListenerCertificates.CreatePublishedProvider();
                var service = new CacheListenerService(
                    runtime,
                    certificates,
                    authority,
                    NullLogger<CacheListenerService>.Instance);
                await service.StartAsync(CancellationToken.None);
                return new ListenerContext(service, authority, runtime.BindPortTls, certificates);
            }

            /// <summary>Stops the listener and disposes retention and certificate resources.</summary>
            public async ValueTask DisposeAsync()
            {
                await Service.DisposeAsync();
                await Authority.DisposeAsync();
                await Certificates.DisposeAsync();
            }
        }
    }
}
