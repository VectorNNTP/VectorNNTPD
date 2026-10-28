using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Accounts
{
    /// <summary>
    /// Unit coverage for lab destination rewrite: loopback retarget, credential clear, no AUTHINFO.
    /// </summary>
    public sealed class LabUpstreamDestinationRewriterTests
    {
        [Fact]
        public void Disabled_rewrite_returns_same_instance()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create()]);
            var providers = mapped.Providers;
            var result = LabUpstreamDestinationRewriter.Apply(
                providers,
                new LabUpstreamIsolationOptions { Enabled = false, RewritePublishedDestinations = false });
            Assert.Same(providers, result);
            Assert.Equal("news.example.test", Assert.Single(result).Host);
            Assert.Equal(ProviderAccountTestRows.SecretPassword, Assert.Single(result).Password);
        }

        [Fact]
        public void Enabled_rewrite_retargets_cleartext_and_clears_credentials()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create()]);
            var rewritten = LabUpstreamDestinationRewriter.Apply(
                mapped.Providers,
                LabRewrite());
            var provider = Assert.Single(rewritten);
            Assert.Equal("Giganews", provider.Backbone);
            Assert.Equal("127.0.0.1", provider.Host);
            Assert.Equal(11119, provider.Port);
            Assert.False(provider.UseTls);
            Assert.Null(provider.Username);
            Assert.Null(provider.Password);
            Assert.False(provider.RequiresAuthentication);
            Assert.Equal(4, provider.MaxSessions);
        }

        [Fact]
        public void Rewrite_with_enabled_false_throws()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create()]);
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LabUpstreamDestinationRewriter.Apply(
                    mapped.Providers,
                    new LabUpstreamIsolationOptions
                    {
                        Enabled = false,
                        AllowedPort = 11119,
                        RewritePublishedDestinations = true,
                    }));
            Assert.Contains("Enabled=true", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Rewritten_provider_session_sends_no_authinfo()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create()]);
            var provider = Assert.Single(LabUpstreamDestinationRewriter.Apply(mapped.Providers, LabRewrite()));

            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            factory.Enqueue(server);
            await using var session = new NntpProviderSession(
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
            Assert.DoesNotContain(
                "nntp-user",
                string.Join('\n', server.Commands),
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task Production_credentials_still_send_authinfo_when_rewrite_disabled()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create()]);
            var provider = Assert.Single(
                LabUpstreamDestinationRewriter.Apply(
                    mapped.Providers,
                    new LabUpstreamIsolationOptions { Enabled = false, RewritePublishedDestinations = false }));
            Assert.True(provider.RequiresAuthentication);

            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static command => command.StartsWith("AUTHINFO USER", StringComparison.Ordinal)
                ? "381 password required\r\n"
                : "281 ok\r\n");
            factory.Enqueue(server);
            await using var session = new NntpProviderSession(
                provider,
                NntpSessionOptions.Default with
                {
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                },
                NullLogger.Instance);

            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            Assert.Contains(server.Commands, static c => c.StartsWith("AUTHINFO USER", StringComparison.Ordinal));
            Assert.Contains(server.Commands, static c => c.StartsWith("AUTHINFO PASS", StringComparison.Ordinal));
        }

        private static LabUpstreamIsolationOptions LabRewrite() =>
            new()
            {
                Enabled = true,
                AllowedHost = "127.0.0.1",
                AllowedPort = 11119,
                RejectTls = true,
                RewritePublishedDestinations = true,
            };
    }
}
