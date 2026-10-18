using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Nntp;
using System.Net.Sockets;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using System.Net;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// STARTTLS during connect: capability parsing, handshake ordering, failures, and credential redaction in logs.
    /// </summary>

    public sealed class NntpStartTlsTests
    {
        /// <summary>Known AUTHINFO password used in scripted servers; must appear redacted in wire logs.</summary>
        private const string Password = "super-secret-nntp-password";

        /// <summary>
        /// A handshake that finishes while disposal owns the session does not publish the TLS wrapper.
        /// The wrapper and the plaintext transport are each disposed once, and the session fields stay clear.
        /// </summary>
        [Fact]
        public async Task Dispose_after_successful_starttls_does_not_publish_the_tls_stream()
        {
            var publishEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var certificate = TestListenerCertificates.CreateSelfSigned("nntp-starttls-dispose.test");
            await using var server = new LoopbackStartTlsNntpServer(certificate);
            var transport = new DisposeCountingTransport();
            CountingSslStream? tls = null;
            var session = CreateSession(host: "127.0.0.1", port: server.Port, acceptAnyCertificate: true);
            session.TlsStreamFactory = inner =>
            {
                tls = new CountingSslStream(inner);
                return tls;
            };
            session.BeforePublishUpgradedTransport = ssl =>
            {
                Assert.NotSame(ssl, SessionField(session, "_stream"));
                publishEntered.TrySetResult();
                return releasePublish.Task;
            };
            var connect = session.ConnectAsync(transport, CancellationToken.None);
            try
            {
                await publishEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await server.HandshakeCompleted.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(server.TlsHandshakeCompleted);
                await session.DisposeAsync();
                releasePublish.TrySetResult();
                using var result = await connect.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(result);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.False(result.SessionReusable);
                Assert.Equal("NNTP session was disposed during STARTTLS.", result.Reason);
                Assert.DoesNotContain("ArgumentNull", result.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("NullReference", result.Reason, StringComparison.Ordinal);
                Assert.Null(SessionField(session, "_stream"));
                Assert.Null(SessionField(session, "_reader"));
                Assert.Equal(NntpSessionState.Closed, session.State);
                Assert.False(session.IsReusable);
                Assert.NotNull(tls);
                Assert.Equal(1, tls.DisposeCalls);
                Assert.Equal(1, transport.DisposeCalls);
            }
            finally
            {
                session.BeforePublishUpgradedTransport = null;
                session.TlsStreamFactory = null;
                releasePublish.TrySetResult();
                try
                {
                    using var leftover = await connect.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                {
                    _ = ex.GetType();
                }

                await session.DisposeAsync();
            }
        }

        /// <summary>
        /// Loopback server: CAPABILITIES, STARTTLS 382, TLS handshake, then AUTHINFO with redacted wire logs.
        /// </summary>

        [Fact]

        public async Task Capabilities_starttls_382_then_tls_then_authentication()
        {
            var logger = new CollectingLogger<NntpProviderSession>();
            using var certificate = TestListenerCertificates.CreateSelfSigned("nntp-starttls.test");
            await using var server = new LoopbackStartTlsNntpServer(certificate);
            var session = CreateSession(
                logger,
                host: "127.0.0.1",
                port: server.Port,
                user: "user",
                password: Password,
                acceptAnyCertificate: true);

            Assert.Null(await session.ConnectAsync(new TcpNntpTransportFactory(), CancellationToken.None));

            Assert.Equal(NntpSessionState.Ready, session.State);
            Assert.True(session.IsReusable);
            Assert.Equal(["CAPABILITIES", "STARTTLS", "AUTHINFO USER user", "AUTHINFO PASS " + Password], server.Commands);
            Assert.True(server.TlsHandshakeCompleted);
            Assert.Contains(logger.Messages, static message => message.Contains("TX: CAPABILITIES", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("RX: 101 Capability list follows", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("RX: STARTTLS", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("TX: STARTTLS", StringComparison.Ordinal));
            Assert.Contains(
                logger.Messages,
                static message => message.Contains("RX: 382 Continue with TLS negotiation", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("TLS handshake starting", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("TLS handshake completed", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("TX: AUTHINFO USER ***", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("TX: AUTHINFO PASS ***", StringComparison.Ordinal));
            Assert.All(
                logger.Messages,
                static message =>
                {
                    Assert.DoesNotContain(Password, message, StringComparison.Ordinal);
                    Assert.DoesNotContain("AUTHINFO USER user", message, StringComparison.Ordinal);
                });
            await session.DisposeAsync();
        }

        /// <summary>
        /// Without STARTTLS capability, AUTHINFO proceeds on plaintext after CAPABILITIES.
        /// </summary>

        [Fact]

        public async Task Capabilities_without_starttls_continues_plaintext_authentication()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static command => command.StartsWith("AUTHINFO USER", StringComparison.Ordinal)
                ? "381 password required\r\n"
                : "281 ok\r\n");
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            Assert.Equal(NntpSessionState.Ready, session.State);
            Assert.Equal("CAPABILITIES", server.Commands[0]);
            Assert.Equal("AUTHINFO USER user", server.Commands[1]);
            Assert.Equal("AUTHINFO PASS secret", server.Commands[2]);
            Assert.DoesNotContain(server.Commands, static command => command.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(server.Commands, static command => command.StartsWith("COMPRESS", StringComparison.OrdinalIgnoreCase));
            await session.DisposeAsync();
        }

        /// <summary>
        /// Non-382 STARTTLS response is ProviderFailure without AUTHINFO.
        /// </summary>

        [Fact]

        public async Task Advertised_starttls_non_382_retires_session_without_authentication()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
                StartTlsResponse = "502 Command unavailable\r\n",
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.Equal(502, result.StatusCode);
            Assert.False(result.SessionReusable);
            Assert.Equal(NntpSessionState.Retiring, session.State);
            Assert.Equal(["CAPABILITIES", "STARTTLS"], server.Commands);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Truncated STARTTLS exchange retires session; AUTHINFO is not attempted.
        /// </summary>

        [Fact]

        public async Task Tls_handshake_failure_retires_session_without_authentication()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
                CompleteAfterStartTls = true,
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.False(result.SessionReusable);
            Assert.Contains("TLS handshake failed", result.Reason, StringComparison.Ordinal);
            Assert.Equal(NntpSessionState.Retiring, session.State);
            Assert.Equal(["CAPABILITIES", "STARTTLS"], server.Commands);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Bad CAPABILITIES status prevents STARTTLS and AUTHINFO.
        /// </summary>

        [Fact]

        public async Task Capabilities_malformed_status_is_a_protocol_failure()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                CapabilitiesResponse = "not-a-status\r\n",
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.False(result.SessionReusable);
            Assert.DoesNotContain(server.Commands, static command => command.StartsWith("AUTHINFO", StringComparison.Ordinal));
            Assert.DoesNotContain(server.Commands, static command => command.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase));
            await session.DisposeAsync();
        }

        /// <summary>
        /// CAPABILITIES multiline without terminator is ProviderFailure.
        /// </summary>

        [Fact]

        public async Task Capabilities_incomplete_list_is_a_protocol_failure()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                CapabilitiesResponse = "101 Capability list follows\r\nVERSION 2\r\nSTARTTLS\r\n",
                CompleteAfterCapabilities = true,
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.False(result.SessionReusable);
            Assert.DoesNotContain(server.Commands, static command => command.StartsWith("AUTHINFO", StringComparison.Ordinal));
            Assert.DoesNotContain(server.Commands, static command => command.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase));
            await session.DisposeAsync();
        }

        /// <summary>
        /// 480 STARTTLS response blocks AUTHINFO on the wire.
        /// </summary>

        [Fact]

        public async Task Authentication_cannot_occur_before_required_starttls()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
                StartTlsResponse = "480 TLS required but unavailable\r\n",
            };
            server.Respond(static _ => "281 should-never-be-used\r\n");
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.Equal(480, result.StatusCode);
            Assert.Equal(["CAPABILITIES", "STARTTLS"], server.Commands);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Case variants of STARTTLS capability still issue STARTTLS before auth.
        /// </summary>

        [Theory]
        [InlineData("starttls")]

        public async Task Starttls_capability_matching_is_case_insensitive(string label)
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
                StartTlsCapabilityLabel = label,
                StartTlsResponse = "502 no\r\n",
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.Equal("STARTTLS", server.Commands[1]);
            Assert.DoesNotContain(server.Commands, static command => command.StartsWith("AUTHINFO", StringComparison.Ordinal));
            await session.DisposeAsync();
        }

        /// <summary>
        /// COMPRESS is not sent even when advertised alongside STARTTLS.
        /// </summary>

        [Fact]

        public async Task Compress_is_never_negotiated_when_advertised()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
                AdvertiseCompress = true,
                StartTlsResponse = "502 no\r\n",
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            _ = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.DoesNotContain(
                server.Commands,
                static command => command.StartsWith("COMPRESS", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("STARTTLS", server.Commands[1]);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Production Nntp sources contain no COMPRESS negotiation strings.
        /// </summary>

        [Fact]

        public void Production_nntp_code_does_not_mention_compress_negotiation()
        {
            var directory = NntpProviderSessionTests.FindSourceDirectory();
            foreach (var text in Directory.GetFiles(directory, "*.cs").Select(static file => File.ReadAllText(file)))
            {
                Assert.DoesNotContain("COMPRESS", text, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// CapabilityLabelEquals matches STARTTLS token boundaries case-insensitively.
        /// </summary>

        [Fact]

        public void Capability_label_equals_is_case_insensitive_and_token_scoped()
        {
            Assert.True(NntpProtocolIo.CapabilityLabelEquals("STARTTLS"u8, NntpProtocolIo.StartTlsCapability));
            Assert.True(NntpProtocolIo.CapabilityLabelEquals(" StartTls extra"u8, NntpProtocolIo.StartTlsCapability));
            Assert.False(NntpProtocolIo.CapabilityLabelEquals("XSTARTTLS"u8, NntpProtocolIo.StartTlsCapability));
            Assert.False(NntpProtocolIo.CapabilityLabelEquals("COMPRESS DEFLATE"u8, NntpProtocolIo.StartTlsCapability));
        }

        /// <summary>
        /// Implicit TLS skips STARTTLS command even when capability is advertised.
        /// </summary>

        [Fact]

        public async Task Implicit_tls_does_not_issue_starttls_when_advertised()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer { AdvertiseStartTls = true };
            server.Respond(static command => command.StartsWith("AUTHINFO USER", StringComparison.Ordinal)
                ? "381 password required\r\n"
                : "281 ok\r\n");
            factory.Enqueue(server);
            var session = CreateSession(useTls: true, user: "user", password: "secret");

            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            Assert.DoesNotContain(server.Commands, static command => command.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("AUTHINFO USER user", server.Commands[1]);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Unread bytes after 382 response fail connect before AUTHINFO.
        /// </summary>

        [Fact]

        public async Task Leftover_bytes_after_382_fail_before_authentication()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
                StartTlsResponse = "382 Continue with TLS negotiation\r\nNOISE",
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.Contains("unread bytes", result.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(server.Commands, static command => command.StartsWith("AUTHINFO", StringComparison.Ordinal));
            await session.DisposeAsync();
        }

        /// <summary>Constructs a single-session provider client with configurable TLS, credentials, and certificate policy.</summary>
        /// <param name="logger">Optional logger; defaults to null logger.</param>
        /// <param name="host">Remote NNTP host.</param>
        /// <param name="port">Remote NNTP port.</param>
        /// <param name="useTls">Whether the provider definition requests implicit TLS.</param>
        /// <param name="user">Optional AUTHINFO user.</param>
        /// <param name="password">Optional AUTHINFO password.</param>
        /// <param name="acceptAnyCertificate">When true, accepts any server certificate during TLS.</param>
        private static NntpProviderSession CreateSession(
            ILogger? logger = null,
            string host = "127.0.0.1",
            int port = 119,
            bool useTls = false,
            string? user = null,
            string? password = null,
            bool acceptAnyCertificate = false) =>
            new(
                new BackFillerProviderDefinition("Giganews", host, port, useTls, user, password, 0, 1),
                NntpSessionOptions.Default with
                {
                    CommandTimeout = TimeSpan.FromSeconds(5),
                    ReceiveTimeout = TimeSpan.FromSeconds(5),
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    ServerCertificateValidationCallback = acceptAnyCertificate
                        ? static (_, _, _, _) => true
                        : null,
                },
                logger ?? NullLogger.Instance);

        /// <summary>Reads a private instance field from <paramref name="session"/> after the operation under test has quiesced.</summary>
        /// <param name="session">Session whose field is observed.</param>
        /// <param name="name">Field name, including its underscore prefix.</param>
        /// <returns>The field value, or null when the session has released it.</returns>
        private static object? SessionField(NntpProviderSession session, string name)
        {
            var field = typeof(NntpProviderSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return field.GetValue(session);
        }

        /// <summary>
        /// TCP transport whose returned stream counts dispose calls. Used to prove the plaintext transport is closed once.
        /// </summary>
        private sealed class DisposeCountingTransport : INntpTransportFactory
        {
            /// <summary>Dispose entries observed on the stream returned from <see cref="ConnectAsync"/>.</summary>
            private int _disposeCalls;

            /// <summary>Gets how many times the connected stream's dispose path ran.</summary>
            public int DisposeCalls => Volatile.Read(ref _disposeCalls);

            /// <summary>Connects to the provider and returns a stream that counts disposal and owns the <see cref="TcpClient"/>.</summary>
            /// <param name="provider">Endpoint. Implicit TLS is not applied; STARTTLS is the session's job.</param>
            /// <param name="options">Connect timeout.</param>
            /// <param name="cancellationToken">Cancels the TCP connect.</param>
            /// <returns>The counting stream.</returns>
            public async Task<Stream> ConnectAsync(
                BackFillerProviderDefinition provider,
                NntpSessionOptions options,
                CancellationToken cancellationToken)
            {
                var client = new TcpClient();
                try
                {
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    connectCts.CancelAfter(options.ConnectTimeout);
                    await client.ConnectAsync(provider.Host, provider.Port, connectCts.Token).ConfigureAwait(false);
                    return new DisposeCountingStream(client, () => Interlocked.Increment(ref _disposeCalls));
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }
        }

        /// <summary>
        /// Network stream that records each dispose entry and closes the socket once.
        /// A second dispose does not close the socket again, but it still increments the counter.
        /// </summary>
        /// <param name="client">Connected client disposed with the first disposing call.</param>
        /// <param name="recordDispose">Increments the factory counter on every dispose entry.</param>
        private sealed class DisposeCountingStream(TcpClient client, Action recordDispose) : Stream
        {
            /// <summary>Inner network stream. Closed on the first disposing call.</summary>
            private readonly Stream _inner = client.GetStream();

            /// <summary>One after the first disposing call closes <see cref="_inner"/> and <paramref name="client"/>.</summary>
            private int _closed;

            /// <inheritdoc />
            public override bool CanRead => _inner.CanRead;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => _inner.CanWrite;

            /// <inheritdoc />
            public override long Length => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void Flush() => _inner.Flush();

            /// <inheritdoc />
            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                _inner.ReadAsync(buffer, cancellationToken);

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                _inner.WriteAsync(buffer, cancellationToken);

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <summary>Records the call, then closes the socket on the first disposing entry.</summary>
            /// <param name="disposing"><see langword="true"/> when called from <see cref="IDisposable.Dispose"/>.</param>
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    recordDispose();
                    if (Interlocked.Exchange(ref _closed, 1) == 0)
                    {
                        _inner.Dispose();
                        client.Dispose();
                    }
                }

                base.Dispose(disposing);
            }

            /// <summary>Records the call, then closes the socket on the first disposing entry.</summary>
            /// <returns>A completed task. The socket close is synchronous.</returns>
            public override ValueTask DisposeAsync()
            {
                recordDispose();
                if (Interlocked.Exchange(ref _closed, 1) == 0)
                {
                    _inner.Dispose();
                    client.Dispose();
                }

                return default;
            }
        }

        /// <summary>
        /// <see cref="SslStream"/> that counts dispose entries. The inner stream stays owned by the wrapper.
        /// </summary>
        private sealed class CountingSslStream : SslStream
        {
            /// <summary>Dispose entries, including a second call.</summary>
            private int _disposeCalls;

            /// <summary>Wraps <paramref name="inner"/> and closes it when this wrapper is disposed.</summary>
            /// <param name="inner">Plaintext transport. Ownership transfers to this wrapper.</param>
            public CountingSslStream(Stream inner)
                : base(inner, leaveInnerStreamOpen: false)
            {
            }

            /// <summary>Gets how many disposing calls this wrapper has observed.</summary>
            public int DisposeCalls => Volatile.Read(ref _disposeCalls);

            /// <summary>Counts one asynchronous dispose, then disposes the base wrapper and its inner stream.</summary>
            /// <returns>The base dispose task.</returns>
            /// <remarks>
            /// <see cref="SslStream.DisposeAsync"/> closes the wrapper without calling <see cref="Dispose(bool)"/> with <see langword="true"/>.
            /// The synchronous override still counts a direct <see cref="IDisposable.Dispose"/>.
            /// </remarks>
            public override ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _disposeCalls);
                return base.DisposeAsync();
            }

            /// <summary>Counts a synchronous disposing call, then disposes the base wrapper and its inner stream.</summary>
            /// <param name="disposing"><see langword="true"/> when called from <see cref="IDisposable.Dispose"/>.</param>
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Interlocked.Increment(ref _disposeCalls);
                }

                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Loopback TCP NNTP server performing STARTTLS upgrade for integration-style connect tests.
        /// </summary>

        private sealed class LoopbackStartTlsNntpServer : IAsyncDisposable
        {
            /// <summary>Loopback listener bound to an ephemeral port for the duration of the instance.</summary>
            private readonly TcpListener _listener;

            /// <summary>Server certificate presented after STARTTLS; owned for the server lifetime.</summary>
            private readonly X509Certificate2 _certificate;
            /// <summary>Cancels the accept loop when the server is disposed.</summary>
            private readonly CancellationTokenSource _cts = new();

            /// <summary>Background accept/handshake loop; awaited during <see cref="DisposeAsync"/> with a short timeout.</summary>
            private readonly Task _accept;

            /// <summary>Completes after <see cref="SslStream.AuthenticateAsServerAsync"/> returns, together with <see cref="TlsHandshakeCompleted"/>.</summary>
            private readonly TaskCompletionSource _handshakeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Command lines received after the TCP (or post-STARTTLS) session is established.</summary>
            private readonly List<string> _commands = [];

            /// <summary>Starts listening on loopback, captures <see cref="Port"/>, and begins <see cref="_accept"/>.</summary>
            /// <param name="certificate">TLS certificate for the post-STARTTLS server side.</param>
            internal LoopbackStartTlsNntpServer(X509Certificate2 certificate)
            {
                _certificate = certificate;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _accept = AcceptAsync(_cts.Token);
            }

            /// <summary>Ephemeral loopback port chosen when listening starts.</summary>
            public int Port { get; }

            /// <summary>Set after a successful STARTTLS server authentication completes.</summary>
            public bool TlsHandshakeCompleted { get; private set; }

            /// <summary>Completes only after the server handshake await returns. Client publication is not this signal.</summary>
            public Task HandshakeCompleted => _handshakeCompleted.Task;

            /// <summary>Client command lines received after TLS, in order; snapshot is thread-safe via lock on the backing list.</summary>
            /// <value>A copy of accumulated command text without trailing line terminators.</value>
            internal IReadOnlyList<string> Commands
            {
                get
                {
                    lock (_commands)
                    {
                        return [.. _commands];
                    }
                }
            }

            /// <summary>Cancels acceptance, stops listening, and waits briefly for the accept task.</summary>
            public async ValueTask DisposeAsync()
            {
                await _cts.CancelAsync().ConfigureAwait(false);
                _listener.Stop();
                try
                {
                    await _accept.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Stopping the listener can fault the accept task with any transport exception.
                catch (Exception)
                {
                    /* Disposal still releases the listener and the cancellation source. */
                }

                _cts.Dispose();
            }

            /// <summary>Accepts one client, sends the initial greeting, and processes commands until disconnect.</summary>
            /// <param name="cancellationToken">Ends acceptance and command handling when canceled.</param>
            private async Task AcceptAsync(CancellationToken cancellationToken)
            {
                using var tcp = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                await WriteAsync(tcp.GetStream(), "200 ready\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await ReadCommandsAsync(tcp.GetStream(), allowStartTls: true, cancellationToken).ConfigureAwait(false);
            }

            /// <summary>NNTP command loop with CAPABILITIES, optional STARTTLS upgrade, AUTHINFO, and QUIT handling.</summary>
            /// <param name="stream">Cleartext or TLS stream carrying client commands.</param>
            /// <param name="allowStartTls">When true, honors STARTTLS and upgrades the connection.</param>
            /// <param name="cancellationToken">Ends the loop when canceled.</param>
            private async Task ReadCommandsAsync(Stream stream, bool allowStartTls, CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var command = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (command is null)
                    {
                        return;
                    }

                    lock (_commands)
                    {
                        _commands.Add(command);
                    }

                    if (command.Equals("CAPABILITIES", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(
                                stream,
                                "101 Capability list follows\r\nVERSION 2\r\nSTARTTLS\r\nREADER\r\n.\r\n"u8.ToArray(),
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (allowStartTls && command.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(
                                stream,
                                "382 Continue with TLS negotiation\r\n"u8.ToArray(),
                                cancellationToken)
                            .ConfigureAwait(false);
                        using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                        await ssl.AuthenticateAsServerAsync(
                                new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = _certificate,
                                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                        TlsHandshakeCompleted = true;
                        _handshakeCompleted.TrySetResult();
                        await ReadCommandsAsync(ssl, allowStartTls: false, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (command.StartsWith("AUTHINFO USER", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, "381 password required\r\n"u8.ToArray(), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (command.StartsWith("AUTHINFO PASS", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, "281 Authentication accepted\r\n"u8.ToArray(), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (command.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    await WriteAsync(stream, "500 unknown\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                }
            }

            /// <summary>Writes protocol bytes and flushes the stream.</summary>
            /// <param name="stream">Destination stream.</param>
            /// <param name="bytes">Wire payload to send.</param>
            /// <param name="cancellationToken">Passed through to write and flush.</param>
            private static async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken cancellationToken)
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            /// <summary>Reads a CRLF-terminated command line; returns null on clean EOF before any bytes.</summary>
            /// <param name="stream">Source stream.</param>
            /// <param name="cancellationToken">Passed through to reads.</param>
            private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
            {
                var builder = new List<byte>(64);
                var one = new byte[1];
                while (true)
                {
                    var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return builder.Count == 0 ? null : Encoding.ASCII.GetString(builder.ToArray());
                    }

                    if (one[0] == (byte)'\n')
                    {
                        if (builder.Count > 0 && builder[^1] == (byte)'\r')
                        {
                            builder.RemoveAt(builder.Count - 1);
                        }

                        return Encoding.ASCII.GetString(builder.ToArray());
                    }

                    builder.Add(one[0]);
                }
            }
        }
    }
}
