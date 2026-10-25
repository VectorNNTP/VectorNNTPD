using System.Buffers;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using System.Text;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// Single NNTP provider session: connect, AUTHINFO, ARTICLE retrieval, framing, timeouts, TLS, and disposal.
    /// </summary>

    public sealed class NntpProviderSessionTests
    {
        /// <summary>
        /// 200 greeting without credentials reaches Ready without TLS on plain port 119.
        /// </summary>

        [Fact]
        public async Task Connect_accepts_200_greeting_without_authentication()
        {
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(new ScriptedNntpServer("200 ready"));
            var session = CreateSession();

            var failure = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Null(failure);
            Assert.Equal(NntpSessionState.Ready, session.State);
            Assert.False(factory.ConnectAttempts[0].UseTls);
            await session.DisposeAsync();
        }

        /// <summary>
        /// 201 posting-prohibited greeting is accepted as a ready session.
        /// </summary>

        [Fact]
        public async Task Connect_accepts_201_greeting()
        {
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(new ScriptedNntpServer("201 posting prohibited"));
            var session = CreateSession();

            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            await session.DisposeAsync();
        }

        /// <summary>
        /// Non-numeric or 4xx greetings yield ProviderFailure and retire the session.
        /// </summary>

        [Fact]
        public async Task Connect_rejects_malformed_and_non_ready_greetings()
        {
            var malformed = new ScriptedNntpTransportFactory();
            malformed.Enqueue(new ScriptedNntpServer("hello"));
            var bad = new ScriptedNntpTransportFactory();
            bad.Enqueue(new ScriptedNntpServer("400 service unavailable"));

            var first = CreateSession();
            var second = CreateSession();
            var malformedResult = await first.ConnectAsync(malformed, CancellationToken.None);
            var badResult = await second.ConnectAsync(bad, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, malformedResult!.Kind);
            Assert.False(malformedResult.SessionReusable);
            Assert.Equal(ArticleRetrievalKind.ProviderFailure, badResult!.Kind);
            Assert.Equal(400, badResult.StatusCode);
            await first.DisposeAsync();
            await second.DisposeAsync();
        }

        /// <summary>
        /// Transport connect failures map to ProviderFailure rather than article outcomes.
        /// </summary>

        [Fact]
        public async Task Connect_failure_and_timeout_are_provider_failures()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new IOException("refused"),
            };
            var session = CreateSession();

            var result = await session.ConnectAsync(factory, CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.Equal("IOException: refused", result.Reason);
            await session.DisposeAsync();
        }

        /// <summary>
        /// A real <see cref="SocketException"/> from connect is a non-reusable provider failure whose reason includes type and message.
        /// </summary>
        [Fact]
        public async Task Connect_socket_exception_is_a_non_reusable_provider_failure()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new SocketException((int)SocketError.ConnectionRefused),
            };
            var session = CreateSession();

            var result = await session.ConnectAsync(factory, CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
            Assert.False(result.SessionReusable);
            Assert.StartsWith(nameof(SocketException) + ":", result.Reason, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason[(nameof(SocketException) + ": ").Length..]));
            Assert.Equal(NntpSessionState.Retiring, session.State);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Full AUTHINFO USER/PASS succeeds; 481 maps to AuthenticationFailure without echoing the password in Reason.
        /// </summary>

        [Fact]
        public async Task Authentication_success_and_failure()
        {
            var okFactory = new ScriptedNntpTransportFactory();
            var okServer = new ScriptedNntpServer();
            okServer.Respond(static command => command.StartsWith("AUTHINFO USER", StringComparison.Ordinal)
                ? "381 password required\r\n"
                : "281 ok\r\n");
            okFactory.Enqueue(okServer);

            var failFactory = new ScriptedNntpTransportFactory();
            var failServer = new ScriptedNntpServer();
            failServer.Respond(static _ => "481 rejected\r\n");
            failFactory.Enqueue(failServer);

            var ok = CreateSession(user: "user", password: "secret");
            var fail = CreateSession(user: "user", password: "secret");
            Assert.Null(await ok.ConnectAsync(okFactory, CancellationToken.None));
            Assert.Equal("CAPABILITIES", okServer.Commands[0]);
            Assert.Equal("AUTHINFO USER user", okServer.Commands[1]);
            Assert.Equal("AUTHINFO PASS secret", okServer.Commands[2]);
            var failed = await fail.ConnectAsync(failFactory, CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.AuthenticationFailure, failed!.Kind);
            Assert.DoesNotContain("secret", failed.Reason, StringComparison.Ordinal);
            await ok.DisposeAsync();
            await fail.DisposeAsync();
        }

        /// <summary>
        /// AUTHINFO PASS is the expected CRLF command on the wire, and the rented buffer is clear when it is returned.
        /// Debug logs keep the redacted command.
        /// </summary>
        [Fact]
        public async Task Authinfo_pass_buffer_is_clear_when_returned()
        {
            const string Password = "pool-clear-secret";
            var pool = new CredentialCommandPool();
            var logger = new CollectingLogger<NntpProviderSession>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static command => command.StartsWith("AUTHINFO USER", StringComparison.Ordinal)
                ? "381 password required\r\n"
                : "281 ok\r\n");
            factory.Enqueue(server);
            var session = CreateSession(user: "acct", password: Password, logger: logger);
            session.PrefixedCommandPool = pool;
            try
            {
                Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
                Assert.Equal(
                    ["CAPABILITIES", "AUTHINFO USER acct", "AUTHINFO PASS " + Password],
                    server.Commands);
                Assert.True(server.LastCommandUsedCrlf);
                Assert.Equal(2, pool.RentCount);
                Assert.Equal(2, pool.ReturnCount);
                AssertClearedCredential(pool, 0, Password);
                AssertClearedCredential(pool, 1, Password);
                Assert.Equal(
                    NntpProtocolIo.AuthInfoPassPrefix.Length + Password.Length + NntpProtocolIo.Crlf.Length,
                    pool.RequestedLength(1));
                Assert.Contains(logger.Messages, static message => message.Contains("TX: AUTHINFO PASS ***", StringComparison.Ordinal));
                Assert.DoesNotContain(logger.Messages, message => message.Contains(Password, StringComparison.Ordinal));
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        /// <summary>A failed AUTHINFO PASS write still clears and returns the rented buffer once.</summary>
        [Fact]
        public async Task Authinfo_pass_write_fault_still_clears_the_buffer()
        {
            const string Password = "pool-clear-secret";
            var pool = new CredentialCommandPool();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                FaultClientAuthInfoPassWrite = true,
            };
            server.Respond(static command => command.StartsWith("AUTHINFO USER", StringComparison.Ordinal)
                ? "381 password required\r\n"
                : "281 ok\r\n");
            factory.Enqueue(server);
            var session = CreateSession(user: "acct", password: Password);
            session.PrefixedCommandPool = pool;
            try
            {
                var result = await session.ConnectAsync(factory, CancellationToken.None);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
                Assert.Equal("IOException: AUTHINFO PASS write fault", result.Reason);
                Assert.DoesNotContain(Password, result.Reason, StringComparison.Ordinal);
                Assert.Equal("AUTHINFO USER acct", server.Commands[1]);
                Assert.DoesNotContain(
                    server.Commands,
                    static command => command.StartsWith("AUTHINFO PASS", StringComparison.Ordinal));
                Assert.Equal(2, pool.RentCount);
                Assert.Equal(2, pool.ReturnCount);
                AssertClearedCredential(pool, 0, Password);
                AssertClearedCredential(pool, 1, Password);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        /// <summary>
        /// Cancellation during AUTHINFO PASS returns the buffer only after the write has left it, and the bytes are then clear.
        /// </summary>
        [Fact]
        public async Task Authinfo_pass_cancellation_clears_the_buffer_after_the_write()
        {
            const string Password = "pool-clear-secret";
            var pool = new CredentialCommandPool();
            var factory = new ScriptedNntpTransportFactory();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new ScriptedNntpServer
            {
                HoldAuthInfoPassWrite = release,
                AuthInfoPassWriteEntered = entered,
            };
            server.Respond(static command => command.StartsWith("AUTHINFO USER", StringComparison.Ordinal)
                ? "381 password required\r\n"
                : "281 ok\r\n");
            factory.Enqueue(server);
            var session = CreateSession(user: "acct", password: Password);
            session.PrefixedCommandPool = pool;
            using var cancel = new CancellationTokenSource();
            try
            {
                var connect = session.ConnectAsync(factory, cancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(2, pool.RentCount);
                Assert.Equal(1, pool.ReturnCount);
                AssertClearedCredential(pool, 0, Password);
                Assert.True(pool.Rented(1).AsSpan().IndexOf(Encoding.ASCII.GetBytes(Password)) >= 0);
                cancel.Cancel();
                var result = await connect.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(ArticleRetrievalKind.Cancelled, result!.Kind);
                Assert.Equal(2, pool.ReturnCount);
                AssertClearedCredential(pool, 1, Password);
                Assert.DoesNotContain(
                    server.Commands,
                    static command => command.StartsWith("AUTHINFO PASS", StringComparison.Ordinal));
            }
            finally
            {
                release.TrySetResult();
                await session.DisposeAsync();
            }
        }

        /// <summary>
        /// <see cref="ArrayPool{T}.Return(T[], bool)"/> with clearing zeroes the whole rented array, including slack past the request.
        /// </summary>
        [Fact]
        public void ArrayPool_return_with_clearArray_zeroes_the_entire_rental()
        {
            var pool = ArrayPool<byte>.Create();
            var rented = pool.Rent(8);
            rented.AsSpan().Fill(0x5A);
            Assert.True(rented.Length >= 8);
            pool.Return(rented, clearArray: true);
            Assert.All(rented, static value => Assert.Equal(0, value));
        }

        /// <summary>
        /// User without password fails AuthenticationFailure without AUTHINFO on the wire.
        /// </summary>

        [Fact]
        public async Task Half_configured_credentials_fail_without_sending_authinfo()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            factory.Enqueue(server);
            var session = CreateSession(user: "only-user", password: null);

            var result = await session.ConnectAsync(factory, CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.AuthenticationFailure, result!.Kind);
            Assert.DoesNotContain(server.Commands, static command => command.StartsWith("AUTHINFO", StringComparison.Ordinal));
            await session.DisposeAsync();
        }

        /// <summary>
        /// Mapper auth-disable (SQL NULL username or password) connects without AUTHINFO and remains usable.
        /// </summary>
        [Fact]
        public async Task Mapper_auth_disabled_provider_connects_without_sending_authinfo()
        {
            var mapped = ProviderAccountMapper.Map(
            [
                ProviderAccountTestRows.Create(
                    username: null,
                    password: ProviderAccountTestRows.SecretPassword,
                    port: 119,
                    useSsl: "n"),
            ]);
            var provider = Assert.Single(mapped.Providers);
            Assert.False(provider.RequiresAuthentication);
            Assert.Null(provider.Password);

            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            factory.Enqueue(server);
            var session = new NntpProviderSession(
                provider,
                NntpSessionOptions.Default with
                {
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                },
                NullLogger.Instance);

            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            Assert.Equal("CAPABILITIES", Assert.Single(server.Commands));
            Assert.DoesNotContain(
                server.Commands,
                static command => command.StartsWith("AUTHINFO", StringComparison.Ordinal));
            Assert.DoesNotContain(
                ProviderAccountTestRows.SecretPassword,
                string.Join('\n', server.Commands),
                StringComparison.Ordinal);
            Assert.True(session.IsReusable);
            await session.DisposeAsync();
        }

        /// <summary>
        /// ARTICLE sends exact Message-ID, CRLF-framed command, destuffs body dots, and leaves session reusable.
        /// </summary>

        [Fact]
        public async Task Article_preserves_exact_message_id_and_destuffs_payload()
        {
            const string messageId = "<AbC@Example.INVALID>";
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.RespondBytes(static command =>
            {
                if (!command.StartsWith("ARTICLE ", StringComparison.Ordinal))
                {
                    return "500 unknown\r\n"u8.ToArray();
                }

                return "220 0 <AbC@Example.INVALID> article follows\r\nFrom: a@b\r\n\r\n..hidden\r\nbody\r\n.\r\n"u8.ToArray();
            });
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            using var result = await session.DownloadArticleAsync(messageId, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
            Assert.Equal("CAPABILITIES", server.Commands[0]);
            Assert.Equal($"ARTICLE {messageId}", server.Commands[1]);
            Assert.Equal(2, server.Commands.Count);
            Assert.True(server.LastCommandUsedCrlf);
            var text = Encoding.ASCII.GetString(result.Article!.Memory.Span);
            Assert.Contains(".hidden", text, StringComparison.Ordinal);
            Assert.DoesNotContain("..hidden", text, StringComparison.Ordinal);
            Assert.False(text.EndsWith(".\r\n", StringComparison.Ordinal));
            Assert.True(session.IsReusable);
            Assert.NotEqual(0, result.CommandStartedTimestamp);
            Assert.Equal(result.CommandStartedTimestamp, session.ArticleCommandStartedTimestamp);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Disposal drops the session's stream and reader after QUIT, and does not change article bytes already returned.
        /// A second disposal stays on the idempotent path.
        /// </summary>
        [Fact]
        public async Task Dispose_releases_stream_and_reader_without_invalidating_returned_article_bytes()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.RespondBytes(static command =>
            {
                if (!command.StartsWith("ARTICLE ", StringComparison.Ordinal))
                {
                    return "500 unknown\r\n"u8.ToArray();
                }

                return "220 0 <a@b> article follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n"u8.ToArray();
            });
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            Assert.NotNull(SessionField(session, "_stream"));
            Assert.NotNull(SessionField(session, "_reader"));

            using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            var payload = result.Article!.Memory.ToArray();

            await session.DisposeAsync();

            Assert.Equal(NntpSessionState.Closed, session.State);
            Assert.Null(SessionField(session, "_stream"));
            Assert.Null(SessionField(session, "_reader"));
            Assert.Equal(payload, result.Article.Memory.ToArray());
            Assert.Equal(1, server.ClientStreamDisposeCount);

            await session.DisposeAsync();

            Assert.Equal(NntpSessionState.Closed, session.State);
            Assert.Equal(1, server.ClientStreamDisposeCount);
            Assert.Equal(payload, result.Article.Memory.ToArray());
        }

        /// <summary>
        /// Disposal after the 220 status is copied, and before payload reading, still classifies the owned ARTICLE result.
        /// The payload call uses the reader captured for the command, so clearing <c>_reader</c> does not throw <see cref="NullReferenceException"/>.
        /// </summary>
        [Fact]
        public async Task Dispose_during_owned_article_payload_read_classifies_provider_failure()
        {
            await AssertDisposeDuringArticlePayloadReadAsync(useCallback: false);
        }

        /// <summary>
        /// Disposal after the 220 status is copied, and before payload reading, still classifies the callback ARTICLE result.
        /// The callback is not invoked, and clearing <c>_reader</c> does not throw <see cref="NullReferenceException"/>.
        /// </summary>
        [Fact]
        public async Task Dispose_during_callback_article_payload_read_classifies_provider_failure()
        {
            await AssertDisposeDuringArticlePayloadReadAsync(useCallback: true);
        }

        /// <summary>
        /// Disposal after ARTICLE bytes are written, and before flush, still classifies the owned ARTICLE result.
        /// Flush uses the stream captured for the command, so clearing <c>_stream</c> does not throw <see cref="NullReferenceException"/>.
        /// </summary>
        [Fact]
        public async Task Dispose_between_owned_article_write_and_flush_classifies_provider_failure()
        {
            await AssertDisposeBetweenArticleWriteAndFlushAsync(useCallback: false);
        }

        /// <summary>
        /// Disposal after ARTICLE bytes are written, and before flush, still classifies the callback ARTICLE result.
        /// The callback is not invoked, and clearing <c>_stream</c> does not throw <see cref="NullReferenceException"/>.
        /// </summary>
        [Fact]
        public async Task Dispose_between_callback_article_write_and_flush_classifies_provider_failure()
        {
            await AssertDisposeBetweenArticleWriteAndFlushAsync(useCallback: true);
        }

        /// <summary>
        /// Disposal after STARTTLS has captured the transport, and before the handshake reads it, is a provider failure.
        /// The handshake keeps that captured stream, so a cleared <c>_stream</c> is not passed as null.
        /// </summary>
        [Fact]
        public async Task Dispose_during_starttls_handshake_classifies_provider_failure()
        {
            var handshakeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHandshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
            };
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var session = CreateSession();
            session.BeforeStartTlsHandshake = () =>
            {
                handshakeEntered.TrySetResult();
                return releaseHandshake.Task;
            };
            var connect = session.ConnectAsync(factory, CancellationToken.None);
            try
            {
                await handshakeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await session.DisposeAsync();
                releaseHandshake.TrySetResult();
                var result = await connect.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(result);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.False(result.SessionReusable);
                Assert.DoesNotContain("ArgumentNull", result.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("NullReference", result.Reason, StringComparison.Ordinal);
                Assert.Null(SessionField(session, "_stream"));
                Assert.Null(SessionField(session, "_reader"));
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.False(session.IsReusable);
                Assert.Equal(NntpSessionState.Closed, session.State);
            }
            finally
            {
                session.BeforeStartTlsHandshake = null;
                releaseHandshake.TrySetResult();
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
        /// Disposal during a stalled STARTTLS handshake cancels that handshake while the caller token stays uncancelled.
        /// The upgrade still closes the captured transport once, and the session ends closed.
        /// </summary>
        [Fact]
        public async Task Dispose_during_stalled_starttls_handshake_cancels_without_caller_cancellation()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handshakeCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CountingDisposeSslStream? tls = null;
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
            };
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var session = CreateSession(connectTimeout: TimeSpan.FromSeconds(30));
            session.TlsStreamFactory = inner =>
                tls = new CountingDisposeSslStream(new StalledPlaintextStream(inner, entered, handshakeCancelled));
            var connect = session.ConnectAsync(factory, CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(handshakeCancelled.Task.IsCompleted);
                await session.DisposeAsync();
                await handshakeCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var result = await connect.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(result);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.Equal("NNTP session was disposed during STARTTLS.", result.Reason);
                Assert.False(result.SessionReusable);
                Assert.Null(SessionField(session, "_stream"));
                Assert.Null(SessionField(session, "_reader"));
                Assert.Equal(NntpSessionState.Closed, session.State);
                Assert.False(session.IsReusable);
                Assert.NotNull(tls);
                Assert.Equal(1, tls.DisposeAsyncCount);
                Assert.Equal(1, server.ClientStreamDisposeCount);
            }
            finally
            {
                session.TlsStreamFactory = null;
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
        /// A throwing handshake-cancellation callback does not skip transport disposal or the closed state.
        /// </summary>
        [Fact]
        public async Task Dispose_continues_when_handshake_cancellation_callback_throws()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer("200 ready");
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            var cancellation = Assert.IsType<CancellationTokenSource>(SessionField(session, "_handshakeCancellation"));
            cancellation.Token.Register(static () => throw new InvalidOperationException("handshake cancel callback failed"));

            await session.DisposeAsync();

            Assert.Equal(NntpSessionState.Closed, session.State);
            Assert.Null(SessionField(session, "_stream"));
            Assert.Null(SessionField(session, "_reader"));
            Assert.Equal(1, server.ClientStreamDisposeCount);
        }

        /// <summary>
        /// Disposal that wins while connect is paused, before the transport is published, stays closed.
        /// The stream created after that disposal is closed once and is not stored on the session.
        /// </summary>
        [Fact]
        public async Task Dispose_during_connect_does_not_publish_the_late_transport()
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new ScriptedNntpServer("200 ready");
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectStarted = started,
                BlockConnect = block,
            };
            factory.Enqueue(server);
            var session = CreateSession();
            var connect = session.ConnectAsync(factory, CancellationToken.None);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await session.DisposeAsync();
                block.TrySetResult();
                using var result = await connect.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(result);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.False(result.SessionReusable);
                Assert.Equal("NNTP session was disposed during connect.", result.Reason);
                Assert.Null(SessionField(session, "_stream"));
                Assert.Null(SessionField(session, "_reader"));
                Assert.Equal(NntpSessionState.Closed, session.State);
                Assert.False(session.IsReusable);
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.Equal(1, server.ClientStreamDisposeAsyncCount);
                await session.DisposeAsync();
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.Equal(1, server.ClientStreamDisposeAsyncCount);
            }
            finally
            {
                factory.BlockConnect = null;
                block.TrySetResult();
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
        /// A logger that throws from the pre-handshake event does not leave the plaintext transport open.
        /// No TLS wrapper exists yet, so the session must close that stream once and a later dispose must not close it again.
        /// </summary>
        [Fact]
        public async Task Starttls_log_failure_before_handshake_disposes_the_plaintext_transport_once()
        {
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
            };
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var session = CreateSession(logger: new ThrowingTlsHandshakeLogger());
            try
            {
                using var result = await session.ConnectAsync(factory, CancellationToken.None);
                Assert.NotNull(result);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.False(result.SessionReusable);
                Assert.Equal("InvalidOperationException: TLS handshake starting", result.Reason);
                Assert.Null(SessionField(session, "_stream"));
                Assert.Null(SessionField(session, "_reader"));
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.Equal(1, server.ClientStreamDisposeAsyncCount);
                await session.DisposeAsync();
                Assert.Null(SessionField(session, "_stream"));
                Assert.Null(SessionField(session, "_reader"));
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.Equal(1, server.ClientStreamDisposeAsyncCount);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        /// <summary>
        /// A dispose failure while abandoning the plaintext transport does not replace the pre-handshake exception.
        /// The session still drops the stream and reader, and a later dispose does not close that transport again.
        /// </summary>
        [Fact]
        public async Task Starttls_cleanup_failure_preserves_the_pre_handshake_exception()
        {
            var logger = new RecordingTlsHandshakeLogger();
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
                FaultClientDispose = true,
            };
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var session = CreateSession(logger: logger);
            try
            {
                using var result = await session.ConnectAsync(factory, CancellationToken.None);
                Assert.NotNull(result);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.False(result.SessionReusable);
                Assert.Equal("InvalidOperationException: TLS handshake starting", result.Reason);
                Assert.Null(SessionField(session, "_stream"));
                Assert.Null(SessionField(session, "_reader"));
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.Equal(1, server.ClientStreamDisposeAsyncCount);
                Assert.Contains(logger.Messages, static message => message.Contains("STARTTLS transport cleanup failed", StringComparison.Ordinal) && message.Contains("IOException", StringComparison.Ordinal));
                await session.DisposeAsync();
                Assert.Null(SessionField(session, "_stream"));
                Assert.Null(SessionField(session, "_reader"));
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.Equal(1, server.ClientStreamDisposeAsyncCount);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        /// <summary>
        /// A greeting read that fails after disposal has closed the session does not move the session back to retiring.
        /// The transport captured at publication is closed once.
        /// </summary>
        [Fact]
        public async Task Greeting_failure_after_dispose_leaves_the_session_closed()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new ScriptedNntpServer
            {
                HoldInitialRead = release,
                InitialReadEntered = entered,
            };
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var session = CreateSession();
            var connect = session.ConnectAsync(factory, CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await session.DisposeAsync();
                Assert.Equal(NntpSessionState.Closed, session.State);
                release.TrySetResult();
                using var result = await connect.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(result);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.False(result.SessionReusable);
                Assert.Equal(NntpSessionState.Closed, session.State);
                Assert.Null(SessionField(session, "_stream"));
                Assert.Null(SessionField(session, "_reader"));
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.False(session.IsReusable);
                Assert.Equal(NntpSessionState.Closed, session.State);
                await session.DisposeAsync();
                Assert.Equal(NntpSessionState.Closed, session.State);
                Assert.Equal(1, server.ClientStreamDisposeCount);
            }
            finally
            {
                server.HoldInitialRead = null;
                release.TrySetResult();
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
        /// A reusable ARTICLE status that is copied before disposal, and parsed after disposal has closed the session, does not assign <see cref="NntpSessionState.Ready"/>.
        /// </summary>
        [Fact]
        public async Task Article_not_found_after_dispose_does_not_leave_ready()
        {
            var statusCopied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new ScriptedNntpServer();
            server.Respond(static command =>
                command.StartsWith("ARTICLE ", StringComparison.Ordinal)
                    ? "430 no such article\r\n"
                    : "500 unknown\r\n");
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            server.HoldStatusLineReturn = releaseStatus;
            server.StatusLineCopied = statusCopied;
            var download = session.DownloadArticleAsync("<missing@example>", CancellationToken.None);
            try
            {
                await statusCopied.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await session.DisposeAsync();
                Assert.Equal(NntpSessionState.Closed, session.State);
                releaseStatus.TrySetResult();
                using var finished = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ArticleNotFound, finished.Kind);
                Assert.True(finished.SessionReusable);
                Assert.Equal(NntpSessionState.Closed, session.State);
                Assert.Null(SessionField(session, "_stream"));
                Assert.Null(SessionField(session, "_reader"));
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.False(session.IsReusable);
            }
            finally
            {
                releaseStatus.TrySetResult();
                try
                {
                    using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                {
                    _ = ex.GetType();
                }

                await session.DisposeAsync();
            }
        }

        /// <summary>
        /// ARTICLE before connect is ProviderFailure and does not start the article command timer.
        /// </summary>

        [Fact]
        public async Task Article_before_connect_does_not_start_the_command_timer()
        {
            var session = CreateSession();

            using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
            Assert.Equal(0, result.CommandStartedTimestamp);
            Assert.Equal(0, session.ArticleCommandStartedTimestamp);
            await session.DisposeAsync();
        }

        /// <summary>
        /// 430 ArticleNotFound keeps the session reusable for later commands.
        /// </summary>

        [Fact]
        public async Task Article_not_found_is_reusable()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static _ => "430 No such article\r\n");
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ArticleNotFound, result.Kind);
            Assert.Equal(430, result.StatusCode);
            Assert.True(result.SessionReusable);
            await session.DisposeAsync();
        }

        /// <summary>
        /// ARTICLE 451 is classified as ArticleNotFound (not ProviderFailure) and keeps the session reusable.
        /// </summary>
        [Fact]
        public async Task Article_451_is_article_not_found_and_reusable()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static _ => "451 article unavailable\r\n");
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ArticleNotFound, result.Kind);
            Assert.Equal(NntpStatusCode.ArticleUnavailable, result.StatusCode);
            Assert.NotEqual(ArticleRetrievalKind.ProviderFailure, result.Kind);
            Assert.True(result.SessionReusable);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Truncated multiline or bad status after 220 retires the session as ProviderFailure.
        /// </summary>

        [Fact]
        public async Task Missing_terminator_and_malformed_status_retire_the_session()
        {
            var truncatedFactory = new ScriptedNntpTransportFactory();
            var truncated = new ScriptedNntpServer { CompleteAfterResponse = true };
            truncated.Respond(static _ => "220 article follows\r\nFrom: a@b\r\n\r\nbody\r\n");
            truncatedFactory.Enqueue(truncated);

            var malformedFactory = new ScriptedNntpTransportFactory();
            var malformed = new ScriptedNntpServer();
            malformed.Respond(static _ => "not-a-status\r\n");
            malformedFactory.Enqueue(malformed);

            var first = CreateSession();
            var second = CreateSession();
            Assert.Null(await first.ConnectAsync(truncatedFactory, CancellationToken.None));
            Assert.Null(await second.ConnectAsync(malformedFactory, CancellationToken.None));

            using var truncatedResult = await first.DownloadArticleAsync("<a@b>", CancellationToken.None);
            using var malformedResult = await second.DownloadArticleAsync("<a@b>", CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ProviderFailure, truncatedResult.Kind);
            Assert.False(truncatedResult.SessionReusable);
            Assert.Equal(ArticleRetrievalKind.ProviderFailure, malformedResult.Kind);
            Assert.False(malformedResult.SessionReusable);
            await first.DisposeAsync();
            await second.DisposeAsync();
        }

        /// <summary>
        /// Body without header/body separator is InvalidArticle but session stays reusable.
        /// </summary>

        [Fact]
        public async Task Empty_article_without_separator_is_invalid_and_reusable()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static _ => "220 follows\r\njust-a-line\r\n.\r\n");
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.InvalidArticle, result.Kind);
            Assert.True(result.SessionReusable);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Payload callback runs while session is Busy; DATE is suppressed; following ARTICLE succeeds.
        /// </summary>

        [Fact]
        public async Task Callback_RunsWhileBusy_AndSessionCanReadTheNextArticle()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.RespondBytes(static command =>
            {
                if (!command.StartsWith("ARTICLE ", StringComparison.Ordinal))
                {
                    return "500 unknown\r\n"u8.ToArray();
                }

                return "220 0 <AbC@Example.INVALID> article follows\r\nFrom: a@b\r\n\r\n..hidden\r\nbody\r\n.\r\n"u8.ToArray();
            });
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            using var result = await session.DownloadArticleAsync(
                "<AbC@Example.INVALID>",
                CancellationToken.None,
                memory =>
                {
                    Assert.Equal(NntpSessionState.Busy, session.State);
                    var keepAlive = session.SendDateKeepAliveAsync(CancellationToken.None);
                    Assert.True(keepAlive.IsCompleted);
                    Assert.True(keepAlive.GetAwaiter().GetResult());
                    var text = Encoding.ASCII.GetString(memory.Span);
                    Assert.Contains(".hidden", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("..hidden", text, StringComparison.Ordinal);
                    return default;
                });

            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
            Assert.Null(result.Article);
            Assert.Equal(NntpSessionState.Ready, session.State);
            Assert.True(session.IsReusable);
            Assert.DoesNotContain(server.Commands, static command => command.StartsWith("DATE", StringComparison.Ordinal));

            using var second = await session.DownloadArticleAsync("<AbC@Example.INVALID>", CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, second.Kind);
            Assert.Contains("body"u8, second.Article!.Memory.Span);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Callback exception propagates yet session returns Ready and accepts another ARTICLE.
        /// </summary>

        [Fact]
        public async Task Callback_Exception_LeavesTheSessionReusable()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.RespondBytes(static command => command.StartsWith("ARTICLE ", StringComparison.Ordinal)
                ? "220 follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n"u8.ToArray()
                : "500 unknown\r\n"u8.ToArray());
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.DownloadArticleAsync(
                    "<a@b>",
                    CancellationToken.None,
                    Fail));

            static ArticleRecordCreateResult Fail(ReadOnlyMemory<byte> memory)
            {
                throw new InvalidOperationException("parse failed");
            }

            Assert.Equal("parse failed", error.Message);
            Assert.Equal(NntpSessionState.Ready, session.State);
            Assert.True(session.IsReusable);

            using var second = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, second.Kind);
            Assert.Contains("body"u8, second.Article!.Memory.Span);
            await session.DisposeAsync();
        }

        /// <summary>
        /// InvalidArticle without separator does not invoke the payload callback.
        /// </summary>

        [Fact]
        public async Task Callback_MissingSeparator_DoesNotInvokeTheConsumer()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static _ => "220 follows\r\njust-a-line\r\n.\r\n");
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            var called = false;

            using var result = await session.DownloadArticleAsync(
                "<a@b>",
                CancellationToken.None,
                _ =>
                {
                    called = true;
                    return default;
                });

            Assert.False(called);
            Assert.Equal(ArticleRetrievalKind.InvalidArticle, result.Kind);
            Assert.True(result.SessionReusable);
            Assert.True(session.IsReusable);
            await session.DisposeAsync();
        }

        /// <summary>
        /// ArticleRecord bytes remain valid after session disposal completes.
        /// </summary>

        [Fact]
        public async Task Payload_remains_readable_after_session_dispose()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n");
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            await session.DisposeAsync();

            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
            Assert.Contains("body"u8, result.Article!.Memory.Span);
        }

        /// <summary>
        /// Canceled ARTICLE download is Cancelled, not ArticleNotFound.
        /// </summary>

        [Fact]
        public async Task Caller_cancellation_is_not_article_not_found()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.RespondBytes(_ =>
            {
                gate.TrySetResult();
                return "220 follows\r\nFrom: a@b\r\n\r\n"u8.ToArray();
            });
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            using var cts = new CancellationTokenSource();
            var download = session.DownloadArticleAsync("<a@b>", cts.Token);
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cts.Cancel();

            using var result = await download;
            Assert.Equal(ArticleRetrievalKind.Cancelled, result.Kind);
            Assert.NotEqual(ArticleRetrievalKind.ArticleNotFound, result.Kind);
            await session.DisposeAsync();
        }

        /// <summary>
        /// EOF before greeting is ProviderFailure with session not reusable.
        /// </summary>

        [Fact]
        public async Task Eof_before_greeting_is_provider_failure()
        {
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(new ScriptedNntpServer { CompleteWithoutGreeting = true });
            var session = CreateSession();

            var result = await session.ConnectAsync(factory, CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.False(result.SessionReusable);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Connect timeout is ProviderFailure, distinct from ArticleNotFound.
        /// </summary>

        [Fact]
        public async Task Connect_timeout_is_provider_failure_not_not_found()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectDelay = TimeSpan.FromSeconds(5),
            };
            var session = new NntpProviderSession(
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromMilliseconds(40),
                    CommandTimeout = TimeSpan.FromMilliseconds(40),
                    ReceiveTimeout = TimeSpan.FromMilliseconds(40),
                },
                NullLogger.Instance);

            var result = await session.ConnectAsync(factory, CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.NotEqual(ArticleRetrievalKind.ArticleNotFound, result.Kind);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Payload exceeding MaxArticleBytes during receive is InvalidArticle and retires the session.
        /// </summary>

        [Fact]
        public async Task Oversized_article_is_invalid_article_and_retires_the_session()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\n" + new string('x', 64) + "\r\n.\r\n");
            factory.Enqueue(server);
            var session = new NntpProviderSession(
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
                NntpSessionOptions.Default with
                {
                    MaxArticleBytes = 16,
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                },
                NullLogger.Instance);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.InvalidArticle, result.Kind);
            Assert.NotEqual(ArticleRetrievalKind.ProviderFailure, result.Kind);
            Assert.False(result.SessionReusable);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Destuffed payload exactly at MaxArticleBytes is accepted with reusable session.
        /// </summary>

        [Fact]
        public async Task Exactly_MaxArticleBytes_destuffed_payload_is_accepted()
        {
            const int maxBytes = 16;
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            // Destuffed "A:b\r\n\r\n1234567\r\n" is exactly 16 bytes (last content CRLF included).
            server.Respond(static _ => "220 follows\r\nA:b\r\n\r\n1234567\r\n.\r\n");
            factory.Enqueue(server);
            var session = new NntpProviderSession(
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
                NntpSessionOptions.Default with
                {
                    MaxArticleBytes = maxBytes,
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                },
                NullLogger.Instance);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
            Assert.True(result.SessionReusable);
            Assert.Equal(maxBytes, result.Article!.Memory.Length);
            await session.DisposeAsync();
        }

        /// <summary>
        /// One byte over MaxArticleBytes during receive is InvalidArticle and retires the session.
        /// </summary>

        [Fact]
        public async Task MaxArticleBytes_plus_one_is_invalid_article_and_retires_the_session()
        {
            const int maxBytes = 16;
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            // Destuffed "A:b\r\n\r\n12345678\r\n" is 17 bytes and exceeds during receive.
            server.Respond(static _ => "220 follows\r\nA:b\r\n\r\n12345678\r\n.\r\n");
            factory.Enqueue(server);
            var session = new NntpProviderSession(
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
                NntpSessionOptions.Default with
                {
                    MaxArticleBytes = maxBytes,
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                },
                NullLogger.Instance);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.InvalidArticle, result.Kind);
            Assert.False(result.SessionReusable);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Connection-terminating or authority ARTICLE statuses are ProviderFailure (not NotFound) and retire the session.
        /// </summary>
        [Theory]
        [InlineData("400 service unavailable")]
        [InlineData("502 not permitted")]
        public async Task Provider_terminating_status_is_not_article_not_found_and_retires(string status)
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(_ => status + "\r\n");
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
            Assert.NotEqual(ArticleRetrievalKind.ArticleNotFound, result.Kind);
            Assert.False(result.SessionReusable);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Single-line ARTICLE 403/500/501/503 are ProviderFailure (not NotFound) and keep the session reusable.
        /// </summary>
        [Theory]
        [InlineData("403 internal fault", NntpStatusCode.InternalFault)]
        [InlineData("500 server error", NntpStatusCode.CommandUnknown)]
        [InlineData("500 unknown command", NntpStatusCode.CommandUnknown)]
        [InlineData("501 syntax error", NntpStatusCode.CommandSyntaxError)]
        [InlineData("503 program fault", NntpStatusCode.ProgramFault)]
        public async Task Article_level_5xx_is_provider_failure_and_reusable(string status, int expectedCode)
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(_ => status + "\r\n");
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
            Assert.NotEqual(ArticleRetrievalKind.ArticleNotFound, result.Kind);
            Assert.Equal(expectedCode, result.StatusCode);
            Assert.True(result.SessionReusable);
            Assert.True(session.IsReusable);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Giganews-style ARTICLE 500 server error leaves the connection synchronized for a later Found ARTICLE.
        /// </summary>
        [Fact]
        public async Task Giganews_500_server_error_allows_subsequent_article_on_same_session()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            var articleOrdinal = 0;
            server.Respond(_ =>
            {
                var n = Interlocked.Increment(ref articleOrdinal);
                return n == 1
                    ? "500 server error\r\n"
                    : "220 0 <ok@b> article follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n";
            });
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            var connectionId = session.ConnectionId;

            using (var failed = await session.DownloadArticleAsync("<miss@b>", CancellationToken.None))
            {
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, failed.Kind);
                Assert.Equal(NntpStatusCode.CommandUnknown, failed.StatusCode);
                Assert.Equal("server error", failed.Reason);
                Assert.True(failed.SessionReusable);
                Assert.Equal(connectionId, failed.ConnectionId);
            }

            using (var found = await session.DownloadArticleAsync("<ok@b>", CancellationToken.None))
            {
                Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, found.Kind);
                Assert.Equal(NntpStatusCode.ArticleFollows, found.StatusCode);
                Assert.True(found.SessionReusable);
                Assert.Equal(connectionId, found.ConnectionId);
                Assert.NotNull(found.Article);
            }

            Assert.True(session.IsReusable);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Single leading dot on body lines is destuffed in returned article bytes.
        /// </summary>

        [Fact]
        public async Task Single_leading_dot_on_a_body_line_is_unstuffed()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\n.hidden\r\n..kept\r\nbody\r\n.\r\n");
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
            var text = Encoding.ASCII.GetString(result.Article!.Memory.Span);
            Assert.Equal("From: a@b\r\n\r\nhidden\r\n.kept\r\nbody\r\n", text);
            await session.DisposeAsync();
        }

        /// <summary>
        /// UseTls on provider definition is forwarded to the transport factory connect attempt.
        /// </summary>

        [Fact]
        public async Task Tls_flag_is_passed_to_the_transport_factory()
        {
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(new ScriptedNntpServer());
            var session = CreateSession(useTls: true);
            _ = await session.ConnectAsync(factory, CancellationToken.None);
            Assert.True(factory.ConnectAttempts[0].UseTls);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Production TcpNntpTransportFactory and NntpTlsClient source omit accept-all certificate callbacks.
        /// </summary>

        [Fact]
        public void Tcp_factory_does_not_install_an_accept_all_certificate_callback()
        {
            var factorySource = File.ReadAllText(FindSource("TcpNntpTransportFactory.cs"));
            var tlsSource = File.ReadAllText(FindSource("NntpTlsClient.cs"));
            Assert.DoesNotContain("return true", tlsSource, StringComparison.Ordinal);
            Assert.Contains("SslProtocols.Tls12", tlsSource, StringComparison.Ordinal);
            Assert.Contains("SslProtocols.Tls13", tlsSource, StringComparison.Ordinal);
            Assert.Contains("if (serverCertificateValidationCallback is not null)", tlsSource, StringComparison.Ordinal);
            Assert.Contains("NntpTlsClient.AuthenticateAsClientAsync", factorySource, StringComparison.Ordinal);
        }

        /// <summary>
        /// Dispose reaches Closed even when QUIT write fails after CompleteAfterResponse.
        /// </summary>

        [Fact]
        public async Task Dispose_still_closes_when_quit_write_fails()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            server.CompleteAfterResponse = true;
            await session.DisposeAsync();

            Assert.Equal(NntpSessionState.Closed, session.State);
        }

        /// <summary>
        /// A connected session swallows <see cref="SocketException"/> from the QUIT write, disposes the stream once, and closes.
        /// A second dispose returns before another stream disposal or another retirement log.
        /// </summary>

        [Fact]
        public async Task Dispose_swallows_socket_exception_from_quit_write_and_second_dispose_is_idempotent()
        {
            var logger = new CollectingLogger<NntpProviderSession>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            factory.Enqueue(server);
            var session = CreateSession(logger: logger);

            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            Assert.Equal(NntpSessionState.Ready, session.State);
            Assert.Contains(server.Commands, static command => command.StartsWith("CAPABILITIES", StringComparison.Ordinal));

            server.FaultClientQuitWrite = true;
            await session.DisposeAsync();

            Assert.Equal(NntpSessionState.Closed, session.State);
            Assert.Equal(1, server.ClientStreamDisposeCount);
            Assert.Equal(1, server.ClientStreamDisposeAsyncCount);
            Assert.DoesNotContain(server.Commands, static command => command.StartsWith("QUIT", StringComparison.Ordinal));
            Assert.Contains(
                logger.Entries,
                static entry => entry.EventId.Id == 5417
                    && entry.Message.Contains("QUIT failed during dispose", StringComparison.Ordinal)
                    && entry.Message.Contains(nameof(SocketException), StringComparison.Ordinal));
            var logCount = logger.Entries.Count;

            await session.DisposeAsync();

            Assert.Equal(NntpSessionState.Closed, session.State);
            Assert.Equal(1, server.ClientStreamDisposeCount);
            Assert.Equal(1, server.ClientStreamDisposeAsyncCount);
            Assert.Equal(logCount, logger.Entries.Count);
        }

        /// <summary>
        /// Production Nntp sources under src/VectorNNTPD.BackFillerService/Nntp contain no blocking sync waits in async paths.
        /// </summary>

        [Fact]
        public void Production_nntp_code_does_not_block_synchronously()
        {
            var directory = FindSourceDirectory();
            foreach (var text in Directory.GetFiles(directory, "*.cs").Select(static file => File.ReadAllText(file)))
            {
                Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
                Assert.DoesNotContain("Password={", text, StringComparison.Ordinal);
                Assert.DoesNotContain("Username={", text, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Pauses after the 220 line is copied and before that read returns, disposes, then lets payload reading observe the closed transport.
        /// The body stays unwritten until cleanup, so a payload read that rereads a cleared <c>_reader</c> cannot succeed from bytes already buffered.
        /// </summary>
        /// <param name="useCallback"><see langword="true"/> exercises the callback payload path. <see langword="false"/> exercises the owned-result path.</param>
        /// <remarks>
        /// The download task is joined again in <see langword="finally"/>. <see cref="NullReferenceException"/> and <see cref="ArgumentNullException"/> propagate from that join.
        /// The classified reason is <see cref="InvalidOperationException"/> from reading the captured reader after the pipe is completed.
        /// A payload call that rereads a cleared <c>_reader</c> throws <see cref="NullReferenceException"/>, which this helper does not classify.
        /// </remarks>
        private static async Task AssertDisposeDuringArticlePayloadReadAsync(bool useCallback)
        {
            var statusCopied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new ScriptedNntpServer
            {
                HoldArticleBody = releaseBody,
            };
            server.Respond(static command =>
                command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase)
                    ? "281 authentication accepted\r\n"
                    : "500 unknown\r\n");
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            server.HoldStatusLineReturn = releaseStatus;
            server.StatusLineCopied = statusCopied;
            var consumed = 0;
            var download = useCallback
                ? session.DownloadArticleAsync(
                    "<a@b>",
                    CancellationToken.None,
                    _ =>
                    {
                        Interlocked.Increment(ref consumed);
                        return default;
                    })
                : session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            try
            {
                await statusCopied.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await session.DisposeAsync();
                releaseStatus.TrySetResult();
                using var finished = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, finished.Kind);
                Assert.False(finished.SessionReusable);
                Assert.Null(finished.Article);
                var reason = finished.Reason ?? string.Empty;
                var prefix = nameof(InvalidOperationException) + ":";
                Assert.StartsWith(prefix, reason, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(reason[prefix.Length..].TrimStart()));
                Assert.DoesNotContain("NullReference", reason, StringComparison.Ordinal);
                Assert.DoesNotContain("ArgumentNull", reason, StringComparison.Ordinal);
                Assert.Equal(0, Volatile.Read(ref consumed));
                Assert.Null(SessionField(session, "_stream"));
                Assert.Null(SessionField(session, "_reader"));
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.False(session.IsReusable);
                Assert.Equal(NntpSessionState.Closed, session.State);
            }
            finally
            {
                releaseStatus.TrySetResult();
                releaseBody.TrySetResult();
                try
                {
                    using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                {
                    _ = ex.GetType();
                }

                await session.DisposeAsync();
            }
        }

        /// <summary>
        /// Pauses after the ARTICLE command is flushed and before the session flush returns. The peer has the command and has not written a response.
        /// Disposal runs in that window, then the command resumes on the stream captured before the write.
        /// </summary>
        /// <param name="useCallback"><see langword="true"/> starts ARTICLE through the callback overload. <see langword="false"/> uses the owned-result overload.</param>
        /// <remarks>
        /// <see cref="ScriptedNntpServer.BlockArticle"/> stays closed until cleanup, so a resumed read cannot observe a response that was written before disposal.
        /// The download task is joined again in <see langword="finally"/>. <see cref="NullReferenceException"/> and <see cref="ArgumentNullException"/> propagate from that join.
        /// The classified reason is <see cref="ObjectDisposedException"/> from flushing the stream captured before disposal.
        /// An empty status, or <see cref="InvalidOperationException"/> from rereading a cleared <c>_stream</c>, fails that assertion.
        /// </remarks>
        private static async Task AssertDisposeBetweenArticleWriteAndFlushAsync(bool useCallback)
        {
            var writeFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var articleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseArticle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new ScriptedNntpServer
            {
                ArticleStarted = articleStarted,
                BlockArticle = releaseArticle,
            };
            server.RespondBytes(static command =>
            {
                if (!command.StartsWith("ARTICLE ", StringComparison.Ordinal))
                {
                    return "500 unknown\r\n"u8.ToArray();
                }

                return "220 0 <a@b> article follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n"u8.ToArray();
            });
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var session = CreateSession();
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            server.HoldAfterCommandWrite = releaseWrite;
            server.CommandWriteFlushed = writeFlushed;
            var consumed = 0;
            var download = useCallback
                ? session.DownloadArticleAsync(
                    "<a@b>",
                    CancellationToken.None,
                    _ =>
                    {
                        Interlocked.Increment(ref consumed);
                        return default;
                    })
                : session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            try
            {
                await writeFlushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await articleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await session.DisposeAsync();
                releaseWrite.TrySetResult();
                using var finished = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, finished.Kind);
                Assert.False(finished.SessionReusable);
                Assert.Null(finished.Article);
                Assert.StartsWith(nameof(ObjectDisposedException) + ":", finished.Reason, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(finished.Reason[(nameof(ObjectDisposedException) + ": ").Length..]));
                Assert.Equal(0, Volatile.Read(ref consumed));
                Assert.Null(SessionField(session, "_stream"));
                Assert.Null(SessionField(session, "_reader"));
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.False(session.IsReusable);
                Assert.Equal(NntpSessionState.Closed, session.State);
            }
            finally
            {
                releaseWrite.TrySetResult();
                releaseArticle.TrySetResult();
                try
                {
                    using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                {
                    _ = ex.GetType();
                }

                await session.DisposeAsync();
            }
        }

        /// <summary>
        /// An idle ready session writes one QUIT, closes the transport once, and ends Closed.
        /// </summary>
        [Fact]
        public async Task Idle_dispose_writes_quit_and_closes_once()
        {
            var logger = new CollectingLogger<NntpProviderSession>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            factory.Enqueue(server);
            var session = CreateSession(logger: logger);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            await session.DisposeAsync();
            await session.DisposeAsync();

            Assert.Equal(NntpSessionState.Closed, session.State);
            Assert.Equal(1, server.ClientStreamDisposeCount);
            Assert.Contains(logger.Messages, static message => message.Contains("TX: QUIT", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, static message => message.Contains("QUIT failed during dispose", StringComparison.Ordinal));
            Assert.False(session.IsReusable);
        }

        /// <summary>
        /// An already expired shutdown deadline closes the transport and does not start QUIT.
        /// </summary>
        [Fact]
        public async Task Expired_deadline_skips_quit_and_still_disposes_the_transport()
        {
            var logger = new CollectingLogger<NntpProviderSession>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                HoldQuitWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            factory.Enqueue(server);
            var session = CreateSession(logger: logger);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            var started = Stopwatch.GetTimestamp();
            await session.DisposeForShutdownAsync(NntpShutdownDeadline.FromBudget(TimeSpan.Zero), CancellationToken.None);
            var elapsed = Stopwatch.GetElapsedTime(started);

            Assert.Equal(NntpSessionState.Closed, session.State);
            Assert.Equal(1, server.ClientStreamDisposeCount);
            Assert.Equal(0, server.QuitWriteStarted);
            Assert.DoesNotContain(logger.Messages, static message => message.Contains("TX: QUIT", StringComparison.Ordinal));
            Assert.True(elapsed < TimeSpan.FromSeconds(1), $"elapsed {elapsed.TotalMilliseconds:0} ms");
        }

        /// <summary>
        /// A stalled QUIT is cancelled by the remaining shutdown budget rather than the standalone two-second allowance.
        /// </summary>
        /// <remarks>
        /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> cancels the write token.
        /// The stalled write then throws <see cref="TaskCanceledException"/>, which is an <see cref="OperationCanceledException"/>.
        /// The dispose log records <c>GetType().Name</c>, so the observed text is <c>TaskCanceledException</c> rather than the base type name.
        /// </remarks>
        [Fact]
        public async Task Short_deadline_cancels_a_stalled_quit_before_the_standalone_allowance()
        {
            var logger = new CollectingLogger<NntpProviderSession>();
            var factory = new ScriptedNntpTransportFactory();
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new ScriptedNntpServer { HoldQuitWrite = hold };
            factory.Enqueue(server);
            var session = CreateSession(logger: logger);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            var budget = TimeSpan.FromMilliseconds(300);

            var started = Stopwatch.GetTimestamp();
            try
            {
                await session.DisposeForShutdownAsync(NntpShutdownDeadline.FromBudget(budget), CancellationToken.None)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                hold.TrySetResult();
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            Assert.Equal(NntpSessionState.Closed, session.State);
            Assert.Equal(1, server.ClientStreamDisposeCount);
            Assert.Equal(1, server.QuitWriteStarted);
            Assert.Contains(logger.Messages, static message => message.Contains("TX: QUIT", StringComparison.Ordinal));
            Assert.Contains(
                logger.Messages,
                static message => message.Contains(
                    "QUIT failed during dispose error=TaskCanceledException",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                logger.Messages,
                static message => message.Contains("QUIT failed during dispose error=SocketException", StringComparison.Ordinal)
                    || message.Contains("QUIT failed during dispose error=IOException", StringComparison.Ordinal));
            Assert.True(
                elapsed < TimeSpan.FromMilliseconds(1200),
                $"budget {budget.TotalMilliseconds:0} ms elapsed {elapsed.TotalMilliseconds:0} ms");
        }

        /// <summary>
        /// A caller token that is already cancelled skips QUIT and still closes the transport.
        /// </summary>
        [Fact]
        public async Task Cancelled_caller_skips_quit_and_still_disposes_the_transport()
        {
            var logger = new CollectingLogger<NntpProviderSession>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                HoldQuitWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            factory.Enqueue(server);
            var session = CreateSession(logger: logger);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await session.DisposeForShutdownAsync(NntpShutdownDeadline.FromBudget(TimeSpan.FromSeconds(10)), cancelled.Token);

            Assert.Equal(NntpSessionState.Closed, session.State);
            Assert.Equal(1, server.ClientStreamDisposeCount);
            Assert.Equal(0, server.QuitWriteStarted);
            Assert.DoesNotContain(logger.Messages, static message => message.Contains("TX: QUIT", StringComparison.Ordinal));
        }

        /// <summary>
        /// QUIT and transport-dispose failures are logged, and the session still closes without a second transport dispose.
        /// </summary>
        [Fact]
        public async Task Quit_and_transport_dispose_failures_still_close_the_session()
        {
            var logger = new CollectingLogger<NntpProviderSession>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                FaultClientQuitWrite = true,
                FaultClientDispose = true,
            };
            factory.Enqueue(server);
            var session = CreateSession(logger: logger);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            await session.DisposeAsync();
            await session.DisposeAsync();

            Assert.Equal(NntpSessionState.Closed, session.State);
            Assert.Equal(1, server.ClientStreamDisposeCount);
            Assert.Contains(
                logger.Entries,
                static entry => entry.EventId.Id == 5417 && entry.Message.Contains(nameof(SocketException), StringComparison.Ordinal));
            Assert.Contains(
                logger.Entries,
                static entry => entry.EventId.Id == 5420 && entry.Message.Contains(nameof(IOException), StringComparison.Ordinal));
            Assert.False(session.IsReusable);
        }

        /// <summary>
        /// Two overlapping dispose calls send QUIT once and close the transport once.
        /// </summary>
        [Fact]
        public async Task Concurrent_dispose_of_an_idle_session_quits_once()
        {
            var logger = new CollectingLogger<NntpProviderSession>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            factory.Enqueue(server);
            var session = CreateSession(logger: logger);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask());

            Assert.Equal(NntpSessionState.Closed, session.State);
            Assert.Equal(1, server.ClientStreamDisposeCount);
            Assert.Equal(1, logger.Messages.Count(static message => message.Contains("TX: QUIT", StringComparison.Ordinal)));
        }

        /// <summary>
        /// A command waiting to enter the gate does not write after disposal has already closed the idle session.
        /// </summary>
        [Fact]
        public async Task Article_waiting_to_enter_does_not_write_after_dispose()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var logger = new CollectingLogger<NntpProviderSession>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            factory.Enqueue(server);
            var session = CreateSession(logger: logger);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            session.BeforeArticleBusyWait = () =>
            {
                entered.TrySetResult();
                return release.Task;
            };
            var download = session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await session.DisposeAsync();
                Assert.Equal(NntpSessionState.Closed, session.State);
                Assert.Contains(logger.Messages, static message => message.Contains("TX: QUIT", StringComparison.Ordinal));

                release.TrySetResult();
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.False(result.SessionReusable);
                Assert.DoesNotContain(server.Commands, static command => command.StartsWith("ARTICLE", StringComparison.Ordinal));
                Assert.DoesNotContain(logger.Messages, static message => message.Contains("TX: ARTICLE", StringComparison.Ordinal));
            }
            finally
            {
                release.TrySetResult();
                try
                {
                    using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                {
                    _ = ex.GetType();
                }
            }
        }

        /// <summary>
        /// Disposal while ARTICLE is blocked in its write aborts that write and does not send QUIT.
        /// The command releases the gate after the abort, so unwind does not depend on a disposed semaphore.
        /// </summary>
        [Fact]
        public async Task Dispose_during_blocked_article_write_skips_quit_and_unwinds()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            AbortingWriteStream? gate = null;
            var server = new ScriptedNntpServer
            {
                ClientStreamDecorator = inner =>
                {
                    gate = new AbortingWriteStream(inner, entered);
                    return gate;
                },
            };
            var logger = new CollectingLogger<NntpProviderSession>();
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var session = CreateSession(logger: logger);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            Assert.NotNull(gate);
            gate.Arm();
            var download = session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await session.DisposeAsync();
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.False(result.SessionReusable);
                Assert.Equal(NntpSessionState.Closed, session.State);
                Assert.Equal(1, gate.MaxWriteDepth);
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.DoesNotContain(logger.Messages, static message => message.Contains("TX: QUIT", StringComparison.Ordinal));
                Assert.Null(SessionField(session, "_stream"));
            }
            finally
            {
                try
                {
                    using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                {
                    _ = ex.GetType();
                }
            }
        }

        /// <summary>
        /// Disposal while ARTICLE is waiting for a response closes the transport and does not write QUIT.
        /// The pending read ends, and the command still releases its gate.
        /// </summary>
        [Fact]
        public async Task Dispose_during_blocked_article_read_skips_quit_and_unwinds()
        {
            var articleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockArticle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new ScriptedNntpServer
            {
                ArticleStarted = articleStarted,
                BlockArticle = blockArticle,
            };
            var logger = new CollectingLogger<NntpProviderSession>();
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var session = CreateSession(logger: logger);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            var download = session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            try
            {
                await articleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await session.DisposeAsync();
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.False(result.SessionReusable);
                Assert.Equal(NntpSessionState.Closed, session.State);
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.Contains(server.Commands, static command => command.StartsWith("ARTICLE", StringComparison.Ordinal));
                Assert.DoesNotContain(logger.Messages, static message => message.Contains("TX: QUIT", StringComparison.Ordinal));
                Assert.DoesNotContain(server.Commands, static command => command.StartsWith("QUIT", StringComparison.Ordinal));
            }
            finally
            {
                blockArticle.TrySetResult();
                try
                {
                    using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                {
                    _ = ex.GetType();
                }
            }
        }

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
        /// Builds a Giganews session against 127.0.0.1:119 with two-second command, receive, and connect timeouts.
        /// </summary>
        /// <param name="useTls">Forwarded to the provider definition and observed on the transport connect attempt.</param>
        /// <param name="user">AUTHINFO user. Null leaves the session anonymous.</param>
        /// <param name="password">AUTHINFO password paired with <paramref name="user"/>.</param>
        /// <param name="logger">Logger for the session. Null uses <see cref="NullLogger"/>.</param>
        /// <param name="connectTimeout">Connect and STARTTLS budget. Two seconds when null.</param>
        private static NntpProviderSession CreateSession(
            bool useTls = false,
            string? user = null,
            string? password = null,
            ILogger? logger = null,
            TimeSpan? connectTimeout = null) =>
            new(
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, useTls, user, password, 0, 1),
                NntpSessionOptions.Default with
                {
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                    ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2),
                },
                logger ?? NullLogger.Instance);

        /// <summary>Locates <paramref name="fileName"/> under <see cref="FindSourceDirectory"/>.</summary>
        /// <param name="fileName">Production NNTP source file name.</param>
        private static string FindSource(string fileName) => RelativePath.Under(FindSourceDirectory(), fileName);

        /// <summary>Walks parents of the test output directory until <c>src/VectorNNTPD.BackFillerService/Nntp</c> exists.</summary>
        /// <returns>Absolute path of the production NNTP source directory.</returns>
        /// <exception cref="DirectoryNotFoundException">No parent contains that directory.</exception>
        internal static string FindSourceDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = RelativePath.Under(directory.FullName, "src", "VectorNNTPD.BackFillerService", "Nntp");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate src/VectorNNTPD.BackFillerService/Nntp.");
        }

        /// <summary>
        /// Logger that throws <see cref="InvalidOperationException"/> from the STARTTLS handshake-starting event.
        /// Other events are ignored. The throw happens before a TLS wrapper exists.
        /// </summary>
        private sealed class ThrowingTlsHandshakeLogger : ILogger
        {
            /// <inheritdoc />
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <inheritdoc />
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == 5410)
                {
                    throw new InvalidOperationException("TLS handshake starting");
                }
            }

            /// <summary>No-op scope returned from <see cref="BeginScope{TState}"/>.</summary>
            private sealed class NullScope : IDisposable
            {
                /// <summary>Shared no-op scope instance.</summary>
                public static NullScope Instance { get; } = new();

                /// <inheritdoc />
                public void Dispose()
                {
                }
            }
        }

        /// <summary>
        /// Logger that throws <see cref="InvalidOperationException"/> from the STARTTLS handshake-starting event and records every other message.
        /// The throw happens before a TLS wrapper exists.
        /// </summary>
        private sealed class RecordingTlsHandshakeLogger : ILogger
        {
            /// <summary>Formatted messages other than the throwing handshake-start event.</summary>
            private readonly List<string> _messages = [];

            /// <summary>Messages recorded before and after the throwing handshake-start event.</summary>
            internal IReadOnlyList<string> Messages
            {
                get
                {
                    lock (_messages)
                    {
                        return [.. _messages];
                    }
                }
            }

            /// <inheritdoc />
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <inheritdoc />
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == 5410)
                {
                    throw new InvalidOperationException("TLS handshake starting");
                }

                ArgumentNullException.ThrowIfNull(formatter);
                var message = formatter(state, exception);
                lock (_messages)
                {
                    _messages.Add(message);
                }
            }

            /// <summary>No-op scope returned from <see cref="BeginScope{TState}"/>.</summary>
            private sealed class NullScope : IDisposable
            {
                /// <summary>Shared no-op scope instance.</summary>
                public static NullScope Instance { get; } = new();

                /// <inheritdoc />
                public void Dispose()
                {
                }
            }
        }

        /// <summary>
        /// Counts asynchronous disposal of the TLS wrapper. The handshake itself is the base implementation.
        /// </summary>
        private sealed class CountingDisposeSslStream : SslStream
        {
            /// <summary>Entries to <see cref="DisposeAsync"/>.</summary>
            private int _disposeAsyncCount;

            /// <summary>Wraps <paramref name="inner"/> and closes it on disposal.</summary>
            /// <param name="inner">Plaintext transport owned by this wrapper.</param>
            public CountingDisposeSslStream(Stream inner)
                : base(inner, leaveInnerStreamOpen: false)
            {
            }

            /// <summary>Gets how many times <see cref="DisposeAsync"/> was entered.</summary>
            public int DisposeAsyncCount => Volatile.Read(ref _disposeAsyncCount);

            /// <inheritdoc />
            public override ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _disposeAsyncCount);
                return base.DisposeAsync();
            }
        }

        /// <summary>
        /// Plaintext transport whose first handshake read waits until that read is cancelled.
        /// Later writes pass through so the client hello can leave the socket.
        /// </summary>
        /// <summary>Asserts one returned command buffer no longer contains <paramref name="password"/>.</summary>
        /// <param name="pool">Pool that observed the return.</param>
        /// <param name="index">Return order. Zero is AUTHINFO USER.</param>
        /// <param name="password">Credential that must be absent. Not written to the assertion message.</param>
        private static void AssertClearedCredential(CredentialCommandPool pool, int index, string password)
        {
            Assert.True(pool.ClearArray(index));
            Assert.All(pool.PrefixAtReturn(index), static value => Assert.Equal(0, value));
            var secret = Encoding.ASCII.GetBytes(password);
            Assert.True(pool.Rented(index).AsSpan().IndexOf(secret) < 0);
        }

        /// <summary>
        /// Records the buffers <see cref="NntpProviderSession.WritePrefixedAsync"/> rents and the bytes present when they are returned.
        /// Does not consult <see cref="ArrayPool{T}.Shared"/>.
        /// </summary>
        private sealed class CredentialCommandPool : ArrayPool<byte>
        {
            /// <summary>Guards the rental lists.</summary>
            private readonly Lock _gate = new();

            /// <summary>Arrays rented, in rent order.</summary>
            private readonly List<byte[]> _rented = [];

            /// <summary>Requested lengths, in rent order.</summary>
            private readonly List<int> _requested = [];

            /// <summary>Arrays still rented.</summary>
            private readonly HashSet<byte[]> _outstanding = new(ReferenceEqualityComparer.Instance);

            /// <summary><c>clearArray</c> values in return order.</summary>
            private readonly List<bool> _clearArray = [];

            /// <summary>Copies of each populated prefix at the moment of return, in return order.</summary>
            private readonly List<byte[]> _prefixAtReturn = [];

            /// <summary>Calls to <see cref="Rent"/>.</summary>
            internal int RentCount { get; private set; }

            /// <summary>Calls to <see cref="Return"/>.</summary>
            internal int ReturnCount { get; private set; }

            /// <summary>The array from rent <paramref name="index"/>.</summary>
            /// <param name="index">Zero-based rent order.</param>
            /// <returns>The rented array.</returns>
            internal byte[] Rented(int index)
            {
                lock (_gate)
                {
                    return _rented[index];
                }
            }

            /// <summary>The length requested for rent <paramref name="index"/>.</summary>
            /// <param name="index">Zero-based rent order.</param>
            /// <returns>The minimum length passed to <see cref="Rent"/>.</returns>
            internal int RequestedLength(int index)
            {
                lock (_gate)
                {
                    return _requested[index];
                }
            }

            /// <summary>Whether return <paramref name="index"/> asked for the array to be cleared.</summary>
            /// <param name="index">Zero-based return order.</param>
            /// <returns><see langword="true"/> when <c>clearArray</c> was set.</returns>
            internal bool ClearArray(int index)
            {
                lock (_gate)
                {
                    return _clearArray[index];
                }
            }

            /// <summary>The populated prefix copied when return <paramref name="index"/> was called.</summary>
            /// <param name="index">Zero-based return order.</param>
            /// <returns>A copy taken before this pool mutates the array.</returns>
            internal byte[] PrefixAtReturn(int index)
            {
                lock (_gate)
                {
                    return _prefixAtReturn[index];
                }
            }

            /// <inheritdoc />
            public override byte[] Rent(int minimumLength)
            {
                var array = new byte[minimumLength + 8];
                Array.Fill(array, (byte)0xA5);
                lock (_gate)
                {
                    RentCount++;
                    _rented.Add(array);
                    _requested.Add(minimumLength);
                    _outstanding.Add(array);
                }

                return array;
            }

            /// <inheritdoc />
            public override void Return(byte[] array, bool clearArray = false)
            {
                ArgumentNullException.ThrowIfNull(array);
                lock (_gate)
                {
                    if (!_outstanding.Remove(array))
                    {
                        throw new InvalidOperationException("Returned a command buffer that was not outstanding.");
                    }

                    var index = _rented.IndexOf(array);
                    ReturnCount++;
                    _clearArray.Add(clearArray);
                    _prefixAtReturn.Add(array.AsSpan(0, _requested[index]).ToArray());
                }
            }
        }

        /// <summary>Wraps a plaintext transport so the first handshake read can be stalled and cancellation observed.</summary>
        private sealed class StalledPlaintextStream : Stream
        {
            /// <summary>Transport wrapped for the scripted server.</summary>
            private readonly Stream _inner;

            /// <summary>Completed when the handshake attempts its first read.</summary>
            private readonly TaskCompletionSource _entered;

            /// <summary>Completed when that read observes a cancelled token.</summary>
            private readonly TaskCompletionSource _cancelled;

            /// <summary>One after this wrapper has closed <see cref="_inner"/>.</summary>
            private int _innerDisposed;

            /// <summary>Wraps <paramref name="inner"/> and signals the handshake read gates.</summary>
            /// <param name="inner">Plaintext transport closed exactly once by this wrapper.</param>
            /// <param name="entered">Completed when the first handshake read starts.</param>
            /// <param name="cancelled">Completed when that read's token is cancelled.</param>
            public StalledPlaintextStream(Stream inner, TaskCompletionSource entered, TaskCompletionSource cancelled)
            {
                _inner = inner;
                _entered = entered;
                _cancelled = cancelled;
            }

            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => true;

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
            public override int Read(byte[] buffer, int offset, int count) =>
                throw new InvalidOperationException("The STARTTLS handshake used a synchronous read.");

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

            /// <inheritdoc />
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                _inner.WriteAsync(buffer, offset, count, cancellationToken);

            /// <inheritdoc />
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
                _inner.WriteAsync(buffer, cancellationToken);

            /// <summary>Signals entry, then waits until <paramref name="cancellationToken"/> is cancelled.</summary>
            /// <param name="buffer">Ignored. The handshake does not receive peer bytes.</param>
            /// <param name="cancellationToken">Token the TLS handshake passed for this read.</param>
            /// <returns>A task that completes only by cancellation.</returns>
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                _ = buffer;
                _entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _cancelled.TrySetResult();
                    throw;
                }

                return 0;
            }

            /// <inheritdoc />
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            /// <inheritdoc />
            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _innerDisposed, 1) == 0)
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
            }

            /// <inheritdoc />
            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _innerDisposed, 1) == 0)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }

    /// <summary>
    /// Forwards a duplex stream and, once <see cref="Arm"/> is called, holds every later write until this stream is disposed.
    /// Disposal completes the hold so the blocked command can unwind.
    /// </summary>
    internal sealed class AbortingWriteStream : Stream
    {
        /// <summary>Inner duplex stream. Owned by this wrapper.</summary>
        private readonly Stream _inner;

        /// <summary>Completed when the first armed write enters.</summary>
        private readonly TaskCompletionSource _entered;

        /// <summary>Completed by <see cref="DisposeAsync"/> so the armed write can throw.</summary>
        private readonly TaskCompletionSource _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>One after <see cref="Arm"/>.</summary>
        private int _armed;

        /// <summary>Armed writes currently inside <see cref="WriteAsync"/>.</summary>
        private int _depth;

        /// <summary>Highest value observed in <see cref="_depth"/>.</summary>
        private int _maxDepth;

        /// <summary>Wraps <paramref name="inner"/> and signals <paramref name="entered"/> from the first armed write.</summary>
        /// <param name="inner">Scripted client stream.</param>
        /// <param name="entered">Completed when an armed write is blocked.</param>
        internal AbortingWriteStream(Stream inner, TaskCompletionSource entered)
        {
            _inner = inner;
            _entered = entered;
        }

        /// <summary>The next writes wait until this stream is disposed.</summary>
        internal void Arm() => Volatile.Write(ref _armed, 1);

        /// <summary>Gets the most armed writes that were inside <see cref="WriteAsync"/> at once.</summary>
        internal int MaxWriteDepth => Volatile.Read(ref _maxDepth);

        /// <inheritdoc />
        public override bool CanRead => _inner.CanRead;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => _inner.CanWrite;

        /// <inheritdoc />
        public override long Length => _inner.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush() => _inner.Flush();

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        /// <summary>Forwards a write, or holds it after <see cref="Arm"/> until disposal.</summary>
        /// <param name="buffer">Command bytes.</param>
        /// <param name="cancellationToken">Cancels the hold and the inner write.</param>
        /// <returns>The inner write, or a fault after the hold is released by disposal.</returns>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 0)
            {
                await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                return;
            }

            var depth = Interlocked.Increment(ref _depth);
            UpdateMax(depth);
            try
            {
                _entered.TrySetResult();
                await _aborted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new IOException("forced abort");
            }
            finally
            {
                Interlocked.Decrement(ref _depth);
            }
        }

        /// <summary>Releases a held write, then disposes the inner stream.</summary>
        /// <returns>The inner dispose task.</returns>
        public override ValueTask DisposeAsync()
        {
            _aborted.TrySetResult();
            return _inner.DisposeAsync();
        }

        /// <summary>Records <paramref name="depth"/> when it exceeds the current maximum.</summary>
        /// <param name="depth">Armed writes inside <see cref="WriteAsync"/>.</param>
        private void UpdateMax(int depth)
        {
            int current;
            do
            {
                current = Volatile.Read(ref _maxDepth);
                if (depth <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _maxDepth, depth, current) != current);
        }
    }
}
