using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// Stable physical <see cref="NntpProviderSession.ConnectionId"/> assignment and retirement correlation.
    /// </summary>
    public sealed class NntpConnectionIdentityCorrelationTests
    {
        /// <summary>A newly constructed session receives a non-empty connection id that remains stable.</summary>
        [Fact]
        public void New_session_receives_stable_connection_id()
        {
            var session = new NntpProviderSession(
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
                NntpSessionOptions.Default,
                NullLogger.Instance,
                connectionNumber: 1);

            Assert.NotEqual(Guid.Empty, session.ConnectionId);
            Assert.Equal(session.ConnectionId, session.ConnectionId);
        }

        /// <summary>Distinct physical session objects receive distinct connection ids even with the same pool slot.</summary>
        [Fact]
        public void Distinct_sessions_receive_distinct_connection_ids()
        {
            var provider = new BackFillerProviderDefinition("Eweka", "127.0.0.1", 119, false, null, null, 0, 2);
            var first = new NntpProviderSession(provider, NntpSessionOptions.Default, NullLogger.Instance, 1);
            var second = new NntpProviderSession(provider, NntpSessionOptions.Default, NullLogger.Instance, 1);

            Assert.NotEqual(first.ConnectionId, second.ConnectionId);
        }

        /// <summary>
        /// ARTICLE 430 keeps the session reusable; lease dispose does not emit lease-requested retirement.
        /// </summary>
        [Fact]
        public async Task Article_430_does_not_retire_session()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 1);
            await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 1, logger: logger);
            await using var lease = await pool.AcquireAsync(CancellationToken.None);
            var connectionId = lease.Session.ConnectionId;
            using var result = await lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ArticleNotFound, result.Kind);
            Assert.True(result.SessionReusable);
            Assert.Equal(connectionId, result.ConnectionId);
            await lease.DisposeAsync();

            Assert.DoesNotContain(
                logger.Messages,
                static m => m.Contains("cause=lease_requested", StringComparison.Ordinal));
            Assert.Equal(1, pool.LiveSessionCount);
        }

        /// <summary>
        /// ARTICLE 451 follows the same reusable NotFound path as 430 on Eweka (same lifecycle rules).
        /// </summary>
        [Fact]
        public async Task Article_451_does_not_retire_session()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static command =>
                command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase)
                    ? "281 authentication accepted\r\n"
                    : "451 article unavailable\r\n");
            factory.Enqueue(server);
            await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 1, backbone: "Eweka");
            await using var lease = await pool.AcquireAsync(CancellationToken.None);
            using var result = await lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ArticleNotFound, result.Kind);
            Assert.Equal(NntpStatusCode.ArticleUnavailable, result.StatusCode);
            Assert.True(result.SessionReusable);
            Assert.Equal(lease.Session.ConnectionId, result.ConnectionId);
            await lease.DisposeAsync();
            Assert.Equal(1, pool.LiveSessionCount);
        }

        /// <summary>
        /// ARTICLE 400 ProviderFailure marks the session non-reusable; lease dispose retires with matching connectionId.
        /// Replacement does not exceed MaxSessions.
        /// </summary>
        [Fact]
        public async Task Provider_failure_400_retires_with_matching_connection_id()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            var first = new ScriptedNntpServer();
            first.Respond(static command =>
                command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase)
                    ? "281 authentication accepted\r\n"
                    : "400 Service unavailable\r\n");
            factory.Enqueue(first);
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 1);

            await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 1, logger: logger);
            Guid connectionId;
            await using (var lease = await pool.AcquireAsync(CancellationToken.None))
            {
                connectionId = lease.Session.ConnectionId;
                using var result = await lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.Equal(NntpStatusCode.ServiceTemporarilyUnavailable, result.StatusCode);
                Assert.False(result.SessionReusable);
                Assert.Equal(connectionId, result.ConnectionId);
                lease.Retire();
            }

            Assert.Contains(
                logger.Messages,
                m => m.Contains("NNTP session retired", StringComparison.Ordinal)
                    && m.Contains($"connectionId={connectionId:D}", StringComparison.Ordinal)
                    && m.Contains("cause=lease_requested", StringComparison.Ordinal)
                    && m.Contains("reason=lease requested retirement", StringComparison.Ordinal));

            var replacement = await pool.AcquireAsync(CancellationToken.None);
            Assert.NotEqual(connectionId, replacement.Session.ConnectionId);
            Assert.True(pool.LiveSessionCount <= 1);
            await replacement.DisposeAsync();
        }

        /// <summary>
        /// Giganews-style ARTICLE 500 server error is ProviderFailure without lease retirement; pool capacity is unchanged.
        /// </summary>
        [Fact]
        public async Task Article_500_server_error_does_not_retire_session()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static command =>
                command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase)
                    ? "281 authentication accepted\r\n"
                    : "500 server error\r\n");
            factory.Enqueue(server);

            await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 1, logger: logger);
            await using var lease = await pool.AcquireAsync(CancellationToken.None);
            var connectionId = lease.Session.ConnectionId;
            using var result = await lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
            Assert.Equal(NntpStatusCode.CommandUnknown, result.StatusCode);
            Assert.Equal("server error", result.Reason);
            Assert.True(result.SessionReusable);
            Assert.Equal(connectionId, result.ConnectionId);
            await lease.DisposeAsync();

            Assert.DoesNotContain(
                logger.Messages,
                static m => m.Contains("cause=lease_requested", StringComparison.Ordinal));
            Assert.Equal(1, pool.LiveSessionCount);
        }

        /// <summary>
        /// Transport IOException during ARTICLE marks the session non-reusable and retires on lease dispose.
        /// </summary>
        [Fact]
        public async Task Transport_failure_retires_session()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer { CompleteAfterResponse = true };
            server.Respond(static command =>
                command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase)
                    ? "281 authentication accepted\r\n"
                    : "220 article follows\r\nFrom: a@b\r\n\r\nbody\r\n");
            factory.Enqueue(server);
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 1);

            await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 1, logger: logger);
            Guid connectionId;
            await using (var lease = await pool.AcquireAsync(CancellationToken.None))
            {
                connectionId = lease.Session.ConnectionId;
                using var result = await lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.False(result.SessionReusable);
                Assert.Equal(connectionId, result.ConnectionId);
                lease.Retire();
            }

            Assert.Contains(
                logger.Messages,
                m => m.Contains("cause=lease_requested", StringComparison.Ordinal)
                    && m.Contains($"connectionId={connectionId:D}", StringComparison.Ordinal));
            Assert.True(pool.LiveSessionCount <= 1);
        }

        /// <summary>Pool dispose retires remaining sessions under pool_shutting_down without exceeding capacity.</summary>
        [Fact]
        public async Task Shutdown_retires_with_pool_shutting_down_cause()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 1);
            var pool = NntpSessionPoolTests.CreatePool(factory, max: 1, backbone: "Eweka", logger: logger);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            Assert.Equal(1, pool.LiveSessionCount);
            await pool.DisposeAsync();

            Assert.Contains(
                logger.Messages,
                static m => m.Contains("cause=pool_shutting_down", StringComparison.Ordinal)
                    && m.Contains("NNTP session retired", StringComparison.Ordinal));
            Assert.Equal(0, pool.LiveSessionCount);
        }
    }
}
