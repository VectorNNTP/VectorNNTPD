using System.Net;
using System.Net.Sockets;
using System.Reflection;
using VectorNNTPD.BackFillerService.Networking;
using VectorNNTPD.BackFillerService.Nntp;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>Upstream <see cref="TcpNntpTransportFactory"/> socket options on a real loopback connect.</summary>
    public sealed class TcpNntpTransportFactoryTests
    {
        /// <summary>
        /// The connected socket has independent buffers, Nagle off, and TCP keepalive before the first write.
        /// </summary>
        [Fact]
        public async Task Connect_applies_independent_buffers_and_no_delay_before_the_first_write()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync();
            var factory = new TcpNntpTransportFactory();
            var options = NntpSessionOptions.Default with
            {
                TcpSendBufferBytes = 256 * 1024,
                TcpReceiveBufferBytes = 128 * 1024,
            };
            await using var stream = await factory.ConnectAsync(Provider(port), options, CancellationToken.None);
            using var server = await accepted;
            var client = ClientOf(stream);

            Assert.True(client.Client.NoDelay);
            Assert.True(client.Client.SendBufferSize >= 256 * 1024);
            Assert.True(client.Client.ReceiveBufferSize >= 128 * 1024);
            Assert.NotEqual(client.Client.SendBufferSize, client.Client.ReceiveBufferSize);
            Assert.Equal(64 * 1024, options.ReceiveBufferBytes);
            Assert.True(TcpSocketOptions.IsKeepAliveEnabled(client.Client));

            await stream.WriteAsync(new byte[] { 7 });
            var received = new byte[1];
            Assert.Equal(1, await server.GetStream().ReadAsync(received));
            Assert.Equal(7, received[0]);
            Assert.True(client.Client.NoDelay);
        }

        /// <summary>
        /// Issue #191: TCP keepalive is enabled even when provider DATE keepalive is disabled.
        /// </summary>
        [Fact]
        public async Task Connect_enables_tcp_keepalive_when_provider_date_keepalive_is_disabled()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync();
            var factory = new TcpNntpTransportFactory();
            var provider = new BackFillerProviderDefinition(
                "loopback",
                "127.0.0.1",
                port,
                false,
                null,
                null,
                0,
                1,
                KeepAliveSeconds: 0);
            Assert.False(provider.DateKeepAliveEnabled);

            await using var stream = await factory.ConnectAsync(
                provider,
                NntpSessionOptions.Default,
                CancellationToken.None);
            using var server = await accepted;
            var client = ClientOf(stream);

            Assert.True(TcpSocketOptions.IsKeepAliveEnabled(client.Client));
            if (TcpSocketOptions.TryGetTcpKeepAliveProbeOption(
                    client.Client,
                    SocketOptionName.TcpKeepAliveTime,
                    out var time))
            {
                Assert.Equal(TcpKeepAliveSettings.DefaultTimeSeconds, time);
            }
        }

        /// <summary>A second connect gets its own configured socket after the first stream is disposed.</summary>
        [Fact]
        public async Task Repeated_connect_configures_each_socket()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var factory = new TcpNntpTransportFactory();
            var options = NntpSessionOptions.Default with
            {
                TcpSendBufferBytes = 64 * 1024,
                TcpReceiveBufferBytes = 256 * 1024,
            };

            for (var i = 0; i < 2; i++)
            {
                var accepted = listener.AcceptTcpClientAsync();
                await using var stream = await factory.ConnectAsync(Provider(port), options, CancellationToken.None);
                using var server = await accepted;
                var client = ClientOf(stream);
                Assert.True(client.Client.NoDelay);
                Assert.True(client.Client.SendBufferSize >= 64 * 1024);
                Assert.True(client.Client.ReceiveBufferSize >= 256 * 1024);
                Assert.NotEqual(client.Client.SendBufferSize, client.Client.ReceiveBufferSize);
                Assert.True(TcpSocketOptions.IsKeepAliveEnabled(client.Client));
            }
        }

        /// <summary>A refused connect disposes the client and surfaces the socket failure.</summary>
        [Fact]
        public async Task Refused_connect_fails_and_does_not_return_a_stream()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var factory = new TcpNntpTransportFactory();

            await Assert.ThrowsAnyAsync<SocketException>(() =>
                factory.ConnectAsync(Provider(port), NntpSessionOptions.Default, CancellationToken.None));
        }

        /// <summary>An already canceled token fails the connect before a stream is returned.</summary>
        [Fact]
        public async Task Canceled_connect_does_not_return_a_stream()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var canceled = new CancellationTokenSource();
            await canceled.CancelAsync();
            var factory = new TcpNntpTransportFactory();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                factory.ConnectAsync(Provider(port), NntpSessionOptions.Default, canceled.Token));
        }

        /// <summary>Builds a cleartext loopback provider. Credentials are unused because the factory does not authenticate.</summary>
        /// <param name="port">Listening port.</param>
        private static BackFillerProviderDefinition Provider(int port) =>
            new("loopback", "127.0.0.1", port, false, null, null, 0, 1);

        /// <summary>Reads the <see cref="TcpClient"/> owned by the factory stream.</summary>
        /// <param name="stream">Stream returned by <see cref="TcpNntpTransportFactory.ConnectAsync"/>.</param>
        private static TcpClient ClientOf(Stream stream)
        {
            var field = stream.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.FieldType == typeof(TcpClient));
            return Assert.IsType<TcpClient>(field.GetValue(stream));
        }
    }
}
