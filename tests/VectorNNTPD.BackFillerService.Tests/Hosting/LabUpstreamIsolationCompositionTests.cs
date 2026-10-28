using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Logging;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Messaging.RabbitMq;
using System.Net;
using System.Net.Sockets;

namespace VectorNNTPD.BackFillerService.Tests.Hosting
{
    /// <summary>
    /// Verifies DI wires the lab upstream allowlist only when explicitly enabled.
    /// </summary>
    public sealed class LabUpstreamIsolationCompositionTests
    {
        [Fact]
        public void Disabled_by_default_resolves_TcpNntpTransportFactory()
        {
            using var host = CreateHost([]);
            var transport = host.Services.GetRequiredService<INntpTransportFactory>();
            Assert.IsType<TcpNntpTransportFactory>(transport);
            Assert.Same(transport, host.Services.GetRequiredService<TcpNntpTransportFactory>());
        }

        [Fact]
        public void Enabled_with_valid_config_resolves_allowlist_decorator()
        {
            using var host = CreateHost(
            [
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:Enabled", "true"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:AllowedHost", "127.0.0.1"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:AllowedPort", "11119"),
            ]);
            var transport = host.Services.GetRequiredService<INntpTransportFactory>();
            Assert.IsType<LabUpstreamAllowlistNntpTransportFactory>(transport);
            Assert.IsType<TcpNntpTransportFactory>(host.Services.GetRequiredService<TcpNntpTransportFactory>());
            Assert.NotSame(transport, host.Services.GetRequiredService<TcpNntpTransportFactory>());
        }

        [Fact]
        public void Enabled_with_invalid_config_fails_ValidateOnStart()
        {
            var builder = CreateBuilder(
            [
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:Enabled", "true"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:AllowedHost", "localhost"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:AllowedPort", "11119"),
            ]);
            using var host = builder.Build();
            var ex = Assert.Throws<OptionsValidationException>(() =>
                _ = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LabUpstreamIsolationOptions>>().Value);
            Assert.Contains("AllowedHost", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Proves the DI-selected allowlist activates on <see cref="INntpTransportFactory.ConnectAsync"/>
        /// (not merely that the type is registered). A WAN destination must fail closed before TCP.
        /// </summary>
        [Fact]
        public async Task Enabled_DI_factory_ConnectAsync_rejects_wan_before_socket()
        {
            using var host = CreateHost(
            [
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:Enabled", "true"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:AllowedHost", "127.0.0.1"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:AllowedPort", "11119"),
            ]);
            var transport = host.Services.GetRequiredService<INntpTransportFactory>();
            Assert.IsType<LabUpstreamAllowlistNntpTransportFactory>(transport);

            var wan = new BackFillerProviderDefinition(
                "Giganews",
                "news.example.com",
                563,
                UseTls: true,
                Username: null,
                Password: null,
                MinSessions: 0,
                MaxSessions: 1);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                transport.ConnectAsync(wan, NntpSessionOptions.Default, CancellationToken.None));
            Assert.Contains("Lab upstream isolation refused", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SocketException", ex.ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Proves RejectTls is enforced through the composed factory for an otherwise allowlisted host/port.
        /// </summary>
        [Fact]
        public async Task Enabled_DI_factory_ConnectAsync_rejects_tls_without_rewriting()
        {
            using var host = CreateHost(
            [
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:Enabled", "true"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:AllowedHost", "127.0.0.1"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:AllowedPort", "11119"),
            ]);
            var transport = host.Services.GetRequiredService<INntpTransportFactory>();
            var tlsPeer = new BackFillerProviderDefinition(
                "Giganews",
                "127.0.0.1",
                11119,
                UseTls: true,
                Username: null,
                Password: null,
                MinSessions: 0,
                MaxSessions: 1);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                transport.ConnectAsync(tlsPeer, NntpSessionOptions.Default, CancellationToken.None));
            Assert.Contains("UseTls=true", ex.Message, StringComparison.Ordinal);
            Assert.Contains("not rewritten", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Rewrite_without_Enabled_fails_ValidateOnStart()
        {
            var builder = CreateBuilder(
            [
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:Enabled", "false"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:RewritePublishedDestinations", "true"),
            ]);
            using var host = builder.Build();
            var ex = Assert.Throws<OptionsValidationException>(() =>
                _ = host.Services.GetRequiredService<IOptions<LabUpstreamIsolationOptions>>().Value);
            Assert.Contains("RewritePublishedDestinations", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Enabled=true", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rewrite_with_RejectTls_false_fails_ValidateOnStart()
        {
            var builder = CreateBuilder(
            [
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:Enabled", "true"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:AllowedHost", "127.0.0.1"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:AllowedPort", "11119"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:RejectTls", "false"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:RewritePublishedDestinations", "true"),
            ]);
            using var host = builder.Build();
            var ex = Assert.Throws<OptionsValidationException>(() =>
                _ = host.Services.GetRequiredService<IOptions<LabUpstreamIsolationOptions>>().Value);
            Assert.Contains("RejectTls", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Real DI composition: rewrite + allowlist retarget published providers to 127.0.0.1:11119
        /// without mutating FakeProviderAccountSource rows (stand-in for MySQL).
        /// </summary>
        [Fact]
        public async Task DI_rewrite_publishes_loopback_and_allowlist_rejects_wan()
        {
            using var host = CreateHost(
            [
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:Enabled", "true"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:AllowedHost", "127.0.0.1"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:AllowedPort", "11119"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:RejectTls", "true"),
                new KeyValuePair<string, string?>("BackFiller:LabUpstreamIsolation:RewritePublishedDestinations", "true"),
            ]);

            var source = (FakeProviderAccountSource)host.Services.GetRequiredService<IProviderAccountSource>();
            source.Rows = [ProviderAccountTestRows.Create()];
            var accounts = host.Services.GetRequiredService<ProviderAccountConfigurationService>();
            await accounts.StartAsync(CancellationToken.None);

            var published = Assert.Single(accounts.PublishedProviders);
            Assert.Equal("127.0.0.1", published.Host);
            Assert.Equal(11119, published.Port);
            Assert.False(published.UseTls);
            Assert.Null(published.Username);
            Assert.Null(published.Password);
            Assert.Equal("news.example.test", source.Rows[0].Hostname);
            Assert.Equal(ProviderAccountTestRows.SecretPassword, source.Rows[0].Password);

            var transport = host.Services.GetRequiredService<INntpTransportFactory>();
            Assert.IsType<LabUpstreamAllowlistNntpTransportFactory>(transport);
            var wan = new BackFillerProviderDefinition(
                "Giganews",
                "news.example.com",
                563,
                UseTls: true,
                Username: "nntp-user",
                Password: ProviderAccountTestRows.SecretPassword,
                MinSessions: 0,
                MaxSessions: 1);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                transport.ConnectAsync(wan, NntpSessionOptions.Default, CancellationToken.None));
            Assert.Contains("Lab upstream isolation refused", refused.Message, StringComparison.Ordinal);

            await accounts.DisposeAsync();
        }

        private static IHost CreateHost(IEnumerable<KeyValuePair<string, string?>> extra) =>
            CreateBuilder(extra).Build();

        private static HostApplicationBuilder CreateBuilder(IEnumerable<KeyValuePair<string, string?>> extra)
        {
            var builder = Host.CreateApplicationBuilder([]);
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            pairs["BackFiller:BindPortTls"] = ((IPEndPoint)listener.LocalEndpoint).Port.ToString();
            listener.Stop();
            pairs["BackFiller:BindAddress:0"] = "*";
            pairs["BackFiller:Logging:File:LogDir"] = Directory.CreateTempSubdirectory("bf-lab-iso-").FullName;
            foreach (var pair in extra)
            {
                pairs[pair.Key] = pair.Value;
            }

            builder.Configuration.AddInMemoryCollection(pairs);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<ICloudflareDnsReconciler>(new NoOpCloudflareDnsReconciler());
            builder.Services.AddSingleton<IPhysicalMemoryProvider>(
                new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
            builder.Services.AddSingleton<IRabbitMqConnectionFactory>(new FakeBackFillerRabbitMqConnectionFactory());
            builder.Services.AddSingleton<IProviderAccountSource>(new FakeProviderAccountSource());
            builder.ConfigureBackFillerLogging();
            builder.ConfigureBackFillerPlatformHosting();
            builder.AddBackFillerHosting();
            SuccessfulNntpDbConnectionFactory.Replace(builder.Services);
            // Avoid live MySQL for shared-config reads when account StartAsync is exercised.
            builder.Services.RemoveAll<IBackFillerSharedConfigurationSource>();
            builder.Services.AddSingleton<IBackFillerSharedConfigurationSource, FixedSharedConfigurationSource>();
            return builder;
        }
    }
}
