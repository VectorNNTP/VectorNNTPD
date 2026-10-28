using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    public sealed class LabUpstreamAllowlistNntpTransportFactoryTests
    {
        [Fact]
        public async Task Valid_loopback_destination_invokes_inner_after_validation()
        {
            var inner = new RecordingTransportFactory();
            var factory = CreateAllowlist(inner, port: 11119);
            var provider = Provider("Giganews", "127.0.0.1", 11119, useTls: false);
            await using var stream = await factory.ConnectAsync(provider, NntpSessionOptions.Default, CancellationToken.None);
            Assert.Equal(1, inner.ConnectCount);
            Assert.Same(provider, inner.LastProvider);
            Assert.NotNull(stream);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("localhost")]
        [InlineData("news.example.com")]
        [InlineData("8.8.8.8")]
        [InlineData("10.0.0.1")]
        [InlineData("::1")]
        [InlineData("2001:db8::1")]
        public async Task Invalid_hosts_never_invoke_inner(string? host)
        {
            var inner = new RecordingTransportFactory();
            var factory = CreateAllowlist(inner, port: 11119);
            var provider = Provider("Giganews", host ?? "", 11119, useTls: false);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                factory.ConnectAsync(provider, NntpSessionOptions.Default, CancellationToken.None));
            Assert.Contains("Lab upstream isolation refused", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, inner.ConnectCount);
        }

        [Fact]
        public async Task Mismatched_port_never_invokes_inner()
        {
            var inner = new RecordingTransportFactory();
            var factory = CreateAllowlist(inner, port: 11119);
            var provider = Provider("Giganews", "127.0.0.1", 119, useTls: false);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                factory.ConnectAsync(provider, NntpSessionOptions.Default, CancellationToken.None));
            Assert.Equal(0, inner.ConnectCount);
        }

        [Fact]
        public async Task Tls_provider_rejected_when_RejectTls_true()
        {
            var inner = new RecordingTransportFactory();
            var factory = CreateAllowlist(inner, port: 11119, rejectTls: true);
            var provider = Provider("Giganews", "127.0.0.1", 11119, useTls: true);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                factory.ConnectAsync(provider, NntpSessionOptions.Default, CancellationToken.None));
            Assert.Contains("UseTls=true", ex.Message, StringComparison.Ordinal);
            Assert.Contains("not rewritten", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, inner.ConnectCount);
        }

        [Fact]
        public async Task Retry_path_reapplies_validation_each_connect()
        {
            var inner = new RecordingTransportFactory();
            var factory = CreateAllowlist(inner, port: 11119);
            var bad = Provider("Eweka", "news.example.com", 563, useTls: true);
            var good = Provider("Giganews", "127.0.0.1", 11119, useTls: false);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                factory.ConnectAsync(bad, NntpSessionOptions.Default, CancellationToken.None));
            Assert.Equal(0, inner.ConnectCount);

            await using var stream = await factory.ConnectAsync(good, NntpSessionOptions.Default, CancellationToken.None);
            Assert.Equal(1, inner.ConnectCount);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                factory.ConnectAsync(bad, NntpSessionOptions.Default, CancellationToken.None));
            Assert.Equal(1, inner.ConnectCount);
        }

        [Fact]
        public async Task One_local_account_does_not_mask_another_disallowed_destination()
        {
            var inner = new RecordingTransportFactory();
            var factory = CreateAllowlist(inner, port: 11119);
            await using var _ = await factory.ConnectAsync(
                Provider("Giganews", "127.0.0.1", 11119, false),
                NntpSessionOptions.Default,
                CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                factory.ConnectAsync(
                    Provider("Eweka", "198.18.0.1", 563, true),
                    NntpSessionOptions.Default,
                    CancellationToken.None));
            Assert.Equal(1, inner.ConnectCount);
        }

        [Fact]
        public void Constructor_rejects_disabled_or_incomplete_options()
        {
            var inner = new RecordingTransportFactory();
            Assert.Throws<InvalidOperationException>(() =>
                new LabUpstreamAllowlistNntpTransportFactory(
                    inner,
                    new LabUpstreamIsolationOptions { Enabled = false, AllowedPort = 11119 }));
            Assert.Throws<InvalidOperationException>(() =>
                new LabUpstreamAllowlistNntpTransportFactory(
                    inner,
                    new LabUpstreamIsolationOptions
                    {
                        Enabled = true,
                        AllowedHost = "localhost",
                        AllowedPort = 11119,
                    }));
            Assert.Throws<InvalidOperationException>(() =>
                new LabUpstreamAllowlistNntpTransportFactory(
                    inner,
                    new LabUpstreamIsolationOptions { Enabled = true, AllowedPort = null }));
        }

        [Fact]
        public void Validator_succeeds_when_disabled_regardless_of_port()
        {
            var result = new LabUpstreamIsolationOptionsValidator().Validate(
                null,
                new LabUpstreamIsolationOptions { Enabled = false, AllowedPort = null });
            Assert.True(result.Succeeded);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("localhost", 11119)]
        [InlineData("127.0.0.1", null)]
        [InlineData("127.0.0.1", 0)]
        [InlineData("127.0.0.1", 70000)]
        [InlineData("::1", 11119)]
        public void Validator_fails_closed_when_enabled_and_invalid(string? host, int? port)
        {
            var options = new LabUpstreamIsolationOptions
            {
                Enabled = true,
                AllowedHost = host ?? "",
                AllowedPort = port,
            };
            var result = new LabUpstreamIsolationOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
        }

        [Fact]
        public void Validator_accepts_enabled_loopback_port()
        {
            var result = new LabUpstreamIsolationOptionsValidator().Validate(
                null,
                new LabUpstreamIsolationOptions
                {
                    Enabled = true,
                    AllowedHost = "127.0.0.1",
                    AllowedPort = 11119,
                });
            Assert.True(result.Succeeded);
        }

        [Fact]
        public void Validator_accepts_enabled_rewrite_with_reject_tls()
        {
            var result = new LabUpstreamIsolationOptionsValidator().Validate(
                null,
                new LabUpstreamIsolationOptions
                {
                    Enabled = true,
                    AllowedHost = "127.0.0.1",
                    AllowedPort = 11119,
                    RejectTls = true,
                    RewritePublishedDestinations = true,
                });
            Assert.True(result.Succeeded);
        }

        [Fact]
        public void Validator_fails_rewrite_when_disabled()
        {
            var result = new LabUpstreamIsolationOptionsValidator().Validate(
                null,
                new LabUpstreamIsolationOptions
                {
                    Enabled = false,
                    RewritePublishedDestinations = true,
                });
            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                static f => f.Contains("RewritePublishedDestinations", StringComparison.Ordinal));
        }

        [Fact]
        public void Validator_fails_rewrite_when_RejectTls_false()
        {
            var result = new LabUpstreamIsolationOptionsValidator().Validate(
                null,
                new LabUpstreamIsolationOptions
                {
                    Enabled = true,
                    AllowedHost = "127.0.0.1",
                    AllowedPort = 11119,
                    RejectTls = false,
                    RewritePublishedDestinations = true,
                });
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("RejectTls", StringComparison.Ordinal));
        }

        [Fact]
        public void Disabled_path_uses_inner_factory_directly_without_decorator_construction()
        {
            // Composition contract: when Enabled=false, DI returns TcpNntpTransportFactory (or recording inner)
            // without wrapping. Unit-level: constructing the decorator with Enabled=false throws.
            Assert.Throws<InvalidOperationException>(() =>
                new LabUpstreamAllowlistNntpTransportFactory(
                    new RecordingTransportFactory(),
                    new LabUpstreamIsolationOptions { Enabled = false, AllowedPort = 1 }));
        }

        private static LabUpstreamAllowlistNntpTransportFactory CreateAllowlist(
            RecordingTransportFactory inner,
            int port,
            bool rejectTls = true) =>
            new(
                inner,
                new LabUpstreamIsolationOptions
                {
                    Enabled = true,
                    AllowedHost = "127.0.0.1",
                    AllowedPort = port,
                    RejectTls = rejectTls,
                });

        private static BackFillerProviderDefinition Provider(
            string backbone,
            string host,
            int port,
            bool useTls) =>
            new(backbone, host, port, useTls, null, null, 0, 1);

        private sealed class RecordingTransportFactory : INntpTransportFactory
        {
            public int ConnectCount { get; private set; }

            public BackFillerProviderDefinition? LastProvider { get; private set; }

            public Task<Stream> ConnectAsync(
                BackFillerProviderDefinition provider,
                NntpSessionOptions options,
                CancellationToken cancellationToken)
            {
                ConnectCount++;
                LastProvider = provider;
                return Task.FromResult<Stream>(new MemoryStream());
            }
        }
    }
}
