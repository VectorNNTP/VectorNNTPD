using System.Net;
using System.Net.Sockets;
using VectorNNTPD.BackFillerService.Networking;

namespace VectorNNTPD.BackFillerService.Tests.Networking
{
    /// <summary>Applies TCP options on real accepted sockets before any application write.</summary>
    public sealed class TcpSocketOptionsTests
    {
        /// <summary>Issue #191: keepalive and probe timings are set on an accepted socket.</summary>
        [Fact]
        public async Task Apply_enables_keepalive_and_probe_timings_when_supported()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var accepted = await listener.AcceptSocketAsync();
            var settings = new TcpKeepAliveSettings(12, 4, 6);

            TcpSocketOptions.Apply(accepted, 128 * 1024, 256 * 1024, settings);

            Assert.True(TcpSocketOptions.IsKeepAliveEnabled(accepted));
            AssertProbeOption(accepted, SocketOptionName.TcpKeepAliveTime, settings.TimeSeconds);
            AssertProbeOption(accepted, SocketOptionName.TcpKeepAliveInterval, settings.IntervalSeconds);
            AssertProbeOption(accepted, SocketOptionName.TcpKeepAliveRetryCount, settings.RetryCount);
        }

        /// <summary>Out-of-range keepalive settings fail before the socket is mutated.</summary>
        [Fact]
        public async Task Apply_rejects_invalid_keepalive_settings_before_mutating_the_socket()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var accepted = await listener.AcceptSocketAsync();
            Assert.False(accepted.NoDelay);
            Assert.False(TcpSocketOptions.IsKeepAliveEnabled(accepted));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => TcpSocketOptions.Apply(
                    accepted,
                    128 * 1024,
                    256 * 1024,
                    new TcpKeepAliveSettings(0, 5, 3)));
            Assert.False(accepted.NoDelay);
            Assert.False(TcpSocketOptions.IsKeepAliveEnabled(accepted));
        }

        /// <summary>An IPv4 accepted socket gets independent buffers and Nagle off before the first byte.</summary>
        [Fact]
        public async Task Apply_configures_an_ipv4_accepted_socket_before_any_write()
        {
            await AssertAcceptedSocketAsync(AddressFamily.InterNetwork, IPAddress.Loopback);
        }

        /// <summary>An IPv6 accepted socket gets the same options when the platform can bind ::1.</summary>
        [Fact]
        public async Task Apply_configures_an_ipv6_accepted_socket_before_any_write()
        {
            if (!Socket.OSSupportsIPv6)
            {
                return;
            }

            try
            {
                await AssertAcceptedSocketAsync(AddressFamily.InterNetworkV6, IPAddress.IPv6Loopback);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable)
            {
                // This host advertised IPv6 but cannot bind the loopback.
            }
        }

        /// <summary>Two accepted sockets can be configured together without sharing one socket's options.</summary>
        [Fact]
        public async Task Apply_configures_concurrent_accepted_sockets()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var firstClient = new TcpClient();
            using var secondClient = new TcpClient();
            await Task.WhenAll(
                firstClient.ConnectAsync(IPAddress.Loopback, port),
                secondClient.ConnectAsync(IPAddress.Loopback, port));
            using var first = await listener.AcceptSocketAsync();
            using var second = await listener.AcceptSocketAsync();

            TcpSocketOptions.Apply(first, 128 * 1024, 256 * 1024, TcpKeepAliveSettings.Default);
            TcpSocketOptions.Apply(second, 64 * 1024, 512 * 1024, TcpKeepAliveSettings.Default);

            AssertRequested(first, 128 * 1024, 256 * 1024);
            AssertRequested(second, 64 * 1024, 512 * 1024);
            await first.SendAsync(new byte[] { 1 });
            await second.SendAsync(new byte[] { 2 });
        }

        /// <summary>An out-of-range size fails before Nagle is changed, and a disposed socket fails on apply.</summary>
        [Fact]
        public async Task Apply_rejects_an_invalid_size_and_a_disposed_socket()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var accepted = await listener.AcceptSocketAsync();
            Assert.False(accepted.NoDelay);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => TcpSocketOptions.Apply(accepted, 1024, 128 * 1024, TcpKeepAliveSettings.Default));
            Assert.False(accepted.NoDelay);

            accepted.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => TcpSocketOptions.Apply(accepted, 128 * 1024, 256 * 1024, TcpKeepAliveSettings.Default));
        }

        /// <summary>Connects one client, applies distinct buffers, checks them, then writes one byte.</summary>
        /// <param name="family">Address family of the listener and client.</param>
        /// <param name="address">Loopback address for that family.</param>
        private static async Task AssertAcceptedSocketAsync(AddressFamily family, IPAddress address)
        {
            using var listen = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            listen.Bind(new IPEndPoint(address, 0));
            listen.Listen(1);
            using var client = new TcpClient(family);
            await client.ConnectAsync((IPEndPoint)listen.LocalEndPoint!);
            using var accepted = await listen.AcceptAsync();
            Assert.False(accepted.NoDelay);

            TcpSocketOptions.Apply(accepted, 128 * 1024, 256 * 1024, TcpKeepAliveSettings.Default);
            AssertRequested(accepted, 128 * 1024, 256 * 1024);
            Assert.True(TcpSocketOptions.IsKeepAliveEnabled(accepted));

            await accepted.SendAsync(new byte[] { 9 });
            var received = new byte[1];
            Assert.Equal(1, await client.Client.ReceiveAsync(received));
            Assert.Equal(9, received[0]);
            AssertRequested(accepted, 128 * 1024, 256 * 1024);
        }

        /// <summary>Checks Nagle and that the reported buffers stayed independent.</summary>
        /// <param name="socket">Socket that <see cref="TcpSocketOptions.Apply"/> already configured.</param>
        /// <param name="send">Requested send limit.</param>
        /// <param name="receive">Requested receive limit.</param>
        private static void AssertRequested(Socket socket, int send, int receive)
        {
            Assert.True(socket.NoDelay);
            Assert.True(socket.SendBufferSize >= send, $"send buffer {socket.SendBufferSize} is below {send}");
            Assert.True(socket.ReceiveBufferSize >= receive, $"receive buffer {socket.ReceiveBufferSize} is below {receive}");
            Assert.NotEqual(socket.SendBufferSize, socket.ReceiveBufferSize);
        }

        /// <summary>
        /// Asserts a probe option when the platform can read it; otherwise requires only that the read was classified unsupported.
        /// </summary>
        private static void AssertProbeOption(Socket socket, SocketOptionName name, int expected)
        {
            if (TcpSocketOptions.TryGetTcpKeepAliveProbeOption(socket, name, out var actual))
            {
                Assert.Equal(expected, actual);
            }
        }
    }
}
