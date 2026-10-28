namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    public sealed class LocalUpstreamIsolationTests
    {
        [Theory]
        [InlineData("127.0.0.1", true)]
        [InlineData("::1", true)]
        [InlineData("localhost", true)]
        [InlineData("news.example.com", false)]
        [InlineData("8.8.8.8", false)]
        public void IsLoopbackHost_classifies_hosts(string host, bool expected) =>
            Assert.Equal(expected, LocalUpstreamIsolation.IsLoopbackHost(host));

        [Fact]
        public void EnsureLocalUpstreamOrThrow_is_noop_when_flag_unset()
        {
            var previous = Environment.GetEnvironmentVariable(
                LocalUpstreamIsolation.RequireLocalUpstreamEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(
                    LocalUpstreamIsolation.RequireLocalUpstreamEnvironmentVariable,
                    null);
                LocalUpstreamIsolation.EnsureLocalUpstreamOrThrow("news.example.com");
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    LocalUpstreamIsolation.RequireLocalUpstreamEnvironmentVariable,
                    previous);
            }
        }

        [Fact]
        public void EnsureLocalUpstreamOrThrow_rejects_non_loopback_when_flag_set()
        {
            var previous = Environment.GetEnvironmentVariable(
                LocalUpstreamIsolation.RequireLocalUpstreamEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(
                    LocalUpstreamIsolation.RequireLocalUpstreamEnvironmentVariable,
                    "1");
                Assert.Throws<InvalidOperationException>(() =>
                    LocalUpstreamIsolation.EnsureLocalUpstreamOrThrow("news.example.com"));
                LocalUpstreamIsolation.EnsureLocalUpstreamOrThrow("127.0.0.1");
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    LocalUpstreamIsolation.RequireLocalUpstreamEnvironmentVariable,
                    previous);
            }
        }

        [Fact]
        public void ValidateBindOrThrow_rejects_non_loopback_without_override()
        {
            var previous = Environment.GetEnvironmentVariable(
                LocalArticleNntpPeerOptions.AllowNonLoopbackEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(
                    LocalArticleNntpPeerOptions.AllowNonLoopbackEnvironmentVariable,
                    null);
                var options = new LocalArticleNntpPeerOptions
                {
                    CorpusRoot = Path.GetTempPath(),
                    BindAddress = System.Net.IPAddress.Parse("8.8.8.8"),
                };
                Assert.Throws<InvalidOperationException>(options.ValidateBindOrThrow);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    LocalArticleNntpPeerOptions.AllowNonLoopbackEnvironmentVariable,
                    previous);
            }
        }

        [Fact]
        public void RequireLocalPeerEndpointOrThrow_requires_flag_host_and_port()
        {
            var prevRequire = Environment.GetEnvironmentVariable(
                LocalUpstreamIsolation.RequireLocalUpstreamEnvironmentVariable);
            var prevHost = Environment.GetEnvironmentVariable(
                LocalUpstreamIsolation.LocalPeerHostEnvironmentVariable);
            var prevPort = Environment.GetEnvironmentVariable(
                LocalUpstreamIsolation.LocalPeerPortEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(
                    LocalUpstreamIsolation.RequireLocalUpstreamEnvironmentVariable,
                    null);
                Assert.Throws<InvalidOperationException>(
                    static () => LocalUpstreamIsolation.RequireLocalPeerEndpointOrThrow());

                Environment.SetEnvironmentVariable(
                    LocalUpstreamIsolation.RequireLocalUpstreamEnvironmentVariable,
                    "1");
                Environment.SetEnvironmentVariable(
                    LocalUpstreamIsolation.LocalPeerHostEnvironmentVariable,
                    "news.example.com");
                Environment.SetEnvironmentVariable(
                    LocalUpstreamIsolation.LocalPeerPortEnvironmentVariable,
                    "11119");
                Assert.Throws<InvalidOperationException>(
                    static () => LocalUpstreamIsolation.RequireLocalPeerEndpointOrThrow());

                Environment.SetEnvironmentVariable(
                    LocalUpstreamIsolation.LocalPeerHostEnvironmentVariable,
                    "127.0.0.1");
                var endpoint = LocalUpstreamIsolation.RequireLocalPeerEndpointOrThrow();
                Assert.Equal("127.0.0.1", endpoint.Host);
                Assert.Equal(11119, endpoint.Port);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    LocalUpstreamIsolation.RequireLocalUpstreamEnvironmentVariable,
                    prevRequire);
                Environment.SetEnvironmentVariable(
                    LocalUpstreamIsolation.LocalPeerHostEnvironmentVariable,
                    prevHost);
                Environment.SetEnvironmentVariable(
                    LocalUpstreamIsolation.LocalPeerPortEnvironmentVariable,
                    prevPort);
            }
        }
    }
}
