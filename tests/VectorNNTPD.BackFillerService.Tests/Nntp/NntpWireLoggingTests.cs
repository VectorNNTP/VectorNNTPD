using Microsoft.Extensions.Logging;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// Wire log identity and redaction for connect, AUTHINFO, and ARTICLE without logging article bodies.
    /// </summary>

    public sealed class NntpWireLoggingTests
    {
        /// <summary>Provider account name embedded in <see cref="NntpProviderSession.WireLogIdentity"/> expectations.</summary>
        private const string Account = "optgiga01";

        /// <summary>Credential sent on the wire during AUTHINFO; must not appear verbatim in Debug logs.</summary>
        private const string Password = "super-secret-nntp-password";

        /// <summary>Message-ID used for ARTICLE command logging without retaining article body bytes.</summary>
        private const string MessageId = "<wire-log@example.invalid>";

        /// <summary>
        /// WireLogIdentity encodes backbone/account[connection/max] and is stable per session instance.
        /// </summary>

        [Fact]

        public void Wire_identity_is_stable_and_uses_existing_account_and_slot()
        {
            var first = CreateSession(logger: new CollectingLogger<NntpProviderSession>(), connectionNumber: 1, maxSessions: 100);
            var second = CreateSession(logger: new CollectingLogger<NntpProviderSession>(), connectionNumber: 37, maxSessions: 100);

            Assert.Equal("Giganews/optgiga01[001/100]", first.WireLogIdentity);
            Assert.Equal("Giganews/optgiga01[037/100]", second.WireLogIdentity);
            Assert.Equal(first.WireLogIdentity, first.WireLogIdentity);
            Assert.NotEqual(first.WireLogIdentity, second.WireLogIdentity);
            Assert.Equal(1, first.ConnectionNumber);
            Assert.Equal(37, second.ConnectionNumber);
        }

        /// <summary>
        /// Connect and AUTHINFO emit Debug TX/RX with masked credentials and identity prefix.
        /// </summary>

        [Fact]
        public async Task Connect_and_auth_log_tx_rx_at_debug_with_masked_credentials()
        {
            var logger = new CollectingLogger<NntpProviderSession>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer("200 News.GigaNews.Com");
            server.Respond(static command => command.StartsWith("AUTHINFO USER", StringComparison.Ordinal)
                ? "381 more authentication required\r\n"
                : "281 Authentication accepted\r\n");
            factory.Enqueue(server);
            var session = CreateSession(logger);

            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            Assert.Contains(
                logger.Messages,
                static message => message.Contains("Connecting article acquisition session to 127.0.0.1:119 (SSL=false)", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("TX: CAPABILITIES", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("RX: 101 Capability list follows", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("TX: AUTHINFO USER ***", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("TX: AUTHINFO PASS ***", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("RX: 200 News.GigaNews.Com", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("RX: 381 more authentication required", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("RX: 281 Authentication accepted", StringComparison.Ordinal));
            Assert.All(
                logger.Messages,
                static message =>
                {
                    Assert.DoesNotContain(Password, message, StringComparison.Ordinal);
                    Assert.DoesNotContain("AUTHINFO USER optgiga01", message, StringComparison.Ordinal);
                    Assert.DoesNotContain("AUTHINFO PASS ", message.Replace("AUTHINFO PASS ***", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
                });
            Assert.All(
                logger.Entries.Where(static entry => entry.Message.Contains("TX:", StringComparison.Ordinal)
                                                   || entry.Message.Contains("RX:", StringComparison.Ordinal)
                                                   || entry.Message.Contains("Connecting article acquisition session", StringComparison.Ordinal)),
                static entry => Assert.Equal(LogLevel.Debug, entry.Level));
            Assert.All(
                logger.Messages.Where(static message => message.Contains("TX:", StringComparison.Ordinal)
                                                       || message.Contains("RX:", StringComparison.Ordinal)),
                static message => Assert.Contains("Giganews/optgiga01[001/1]:", message, StringComparison.Ordinal));

            await session.DisposeAsync();
        }

        /// <summary>
        /// ARTICLE logs status and byte count only; article body lines never appear in logs.
        /// </summary>

        [Fact]
        public async Task Article_logs_status_and_payload_bytes_without_dumping_the_body()
        {
            var logger = new CollectingLogger<NntpProviderSession>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            const string payloadMarker = "UNIQUE-ARTICLE-BODY-LINE-MUST-NOT-BE-LOGGED";
            server.RespondBytes(static command =>
            {
                if (command.StartsWith("AUTHINFO USER", StringComparison.Ordinal))
                {
                    return "381 more authentication required\r\n"u8.ToArray();
                }

                if (command.StartsWith("AUTHINFO PASS", StringComparison.Ordinal))
                {
                    return "281 Authentication accepted\r\n"u8.ToArray();
                }

                if (!command.StartsWith("ARTICLE ", StringComparison.Ordinal))
                {
                    return "500 unknown\r\n"u8.ToArray();
                }

                return Encoding.ASCII.GetBytes(
                    "220 0 <wire-log@example.invalid> article follows\r\nFrom: a@b\r\n\r\n"
                    + payloadMarker
                    + "\r\nsecond-payload-line\r\n.\r\n");
            });
            factory.Enqueue(server);
            var session = CreateSession(logger);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            using var result = await session.DownloadArticleAsync(MessageId, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
            var payloadBytes = result.Article!.Memory.Length;
            Assert.Contains(logger.Messages, static message => message.Contains("TX: ARTICLE <wire-log@example.invalid>", StringComparison.Ordinal));
            Assert.Contains(
                logger.Messages,
                static message => message.Contains("RX: 220 0 <wire-log@example.invalid> article follows", StringComparison.Ordinal));
            Assert.Contains(
                logger.Messages,
                message => message.Contains($"RX: ARTICLE payload complete bytes={payloadBytes}", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, static message => message.Contains(payloadMarker, StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, static message => message.Contains("second-payload-line", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, static message => message.Contains("From: a@b", StringComparison.Ordinal));
            Assert.Equal(
                1,
                logger.Messages.Count(static message => message.Contains("RX: ARTICLE payload complete", StringComparison.Ordinal)));
            Assert.Contains(
                logger.Entries,
                static entry => entry.Level == LogLevel.Debug
                    && entry.Message.Contains("TX: ARTICLE <wire-log@example.invalid>", StringComparison.Ordinal));
            Assert.DoesNotContain(
                logger.Entries,
                static entry => entry.Level == LogLevel.Information
                    && entry.Message.Contains("Article processed", StringComparison.Ordinal));
            Assert.All(
                logger.Entries.Where(static entry => entry.Message.Contains("ARTICLE", StringComparison.Ordinal)),
                static entry => Assert.Equal(LogLevel.Debug, entry.Level));
            await session.DisposeAsync();
        }

        /// <summary>
        /// Two pool sessions produce distinct WireLogIdentity values in log prefixes.
        /// </summary>

        [Fact]
        public async Task Distinct_sessions_produce_distinct_identities()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 2);
            await using var pool = new NntpSessionPool(
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, Account, Password, 0, 2),
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                factory,
                logger,
                NntpSessionPoolTests.CreateEstablishmentGate(),
                shutdownGrace: TimeSpan.FromSeconds(2));

            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            await using var first = await pool.AcquireAsync(CancellationToken.None);
            await using var second = await pool.AcquireAsync(CancellationToken.None);

            var identities = new HashSet<string>(StringComparer.Ordinal)
            {
                first.Session.WireLogIdentity,
                second.Session.WireLogIdentity,
            };
            Assert.Equal(
                new HashSet<string>(StringComparer.Ordinal) { "Giganews/optgiga01[001/2]", "Giganews/optgiga01[002/2]" },
                identities);
            Assert.Contains(logger.Messages, static message => message.Contains("Giganews/optgiga01[001/2]:", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("Giganews/optgiga01[002/2]:", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, static message => message.Contains(Password, StringComparison.Ordinal));
        }

        /// <summary>Builds a provider session with test credentials and connection numbering for wire-log identity assertions.</summary>
        /// <param name="logger">Logger receiving wire traffic lines.</param>
        /// <param name="connectionNumber">Pool connection index embedded in wire-log identity.</param>
        /// <param name="maxSessions">Maximum sessions on the provider definition (denominator in identity).</param>
        private static NntpProviderSession CreateSession(
            ILogger logger,
            int connectionNumber = 1,
            int maxSessions = 1) =>
            new(
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, Account, Password, 0, maxSessions),
                NntpSessionOptions.Default with
                {
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                },
                logger,
                connectionNumber);
    }
}
