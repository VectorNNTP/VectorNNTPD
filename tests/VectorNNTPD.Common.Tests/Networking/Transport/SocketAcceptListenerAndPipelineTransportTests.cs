using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking.Listeners;
using VectorNNTPD.Common.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;

namespace VectorNNTPD.Common.Tests.Networking.Transport
{
    /// <summary>
    /// Exercises the shared <see cref="SocketAcceptListener"/> + <see cref="NntpConnection"/> pipeline path.
    /// </summary>
    public sealed class SocketAcceptListenerAndPipelineTransportTests
    {
        /// <summary>Accepts one client, reads payload via pipes/stream adapter, and echoes an acknowledgement.</summary>
        [Fact]
        public async Task Plain_accept_delivers_bytes_through_pipeline_and_stream_adapter()
        {
            var payload = Encoding.ASCII.GetBytes("hello-pipeline\r\n");
            var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var listener = new SocketAcceptListener(
                new ListenBinding(IPAddress.Loopback, Port: 0, DualMode: false),
                async (socket, cancellationToken) =>
                {
                    var peer = (IPEndPoint)socket.RemoteEndPoint!;
                    await using var connection = NntpConnection.StartPlain(
                        socket,
                        ConnectionClientIdentity.Direct(peer),
                        NullLogger.Instance);
                    await using var stream = new NntpConnectionStream(connection, ownsConnection: false);
                    var buffer = new byte[64];
                    var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    received.TrySetResult(buffer.AsSpan(0, read).ToArray());

                    var echo = Encoding.ASCII.GetBytes("ack\r\n");
                    await stream.WriteAsync(echo, cancellationToken).ConfigureAwait(false);
                },
                NullLogger.Instance);

            listener.Start();
            var endpoint = listener.LocalEndPoint;

            using var client = new TcpClient();
            await client.ConnectAsync(endpoint, CancellationToken.None);
            await using var clientStream = client.GetStream();
            await clientStream.WriteAsync(payload);

            var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(payload, got);

            var response = new byte[16];
            var responseCount = await clientStream.ReadAsync(response.AsMemory());
            Assert.Equal("ack\r\n", Encoding.ASCII.GetString(response.AsSpan(0, responseCount)));
        }

        /// <summary>Admits several concurrent clients on one listener without serializing accepts.</summary>
        [Fact]
        public async Task Accept_loop_supports_concurrent_clients()
        {
            const int clients = 8;
            var admitted = 0;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var listener = new SocketAcceptListener(
                new ListenBinding(IPAddress.Loopback, Port: 0, DualMode: false),
                async (socket, cancellationToken) =>
                {
                    Interlocked.Increment(ref admitted);
                    var peer = (IPEndPoint)socket.RemoteEndPoint!;
                    await using var connection = NntpConnection.StartPlain(
                        socket,
                        ConnectionClientIdentity.Direct(peer),
                        NullLogger.Instance);
                    await using var stream = new NntpConnectionStream(connection, ownsConnection: false);
                    await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var buffer = new byte[1];
                    _ = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                },
                NullLogger.Instance);

            listener.Start();
            var endpoint = listener.LocalEndPoint;

            // Hold every client open until all connects, admissions, and writes complete.
            using var sockets = new TcpClientGroup();
            for (var i = 0; i < clients; i++)
            {
                var client = new TcpClient();
                await client.ConnectAsync(endpoint, CancellationToken.None);
                sockets.Add(client);
            }

            await WaitUntilAsync(() => Volatile.Read(ref admitted) == clients, TimeSpan.FromSeconds(5));
            gate.TrySetResult();

            foreach (var client in sockets.Items)
            {
                await client.GetStream().WriteAsync(new byte[] { 0x2E });
            }
        }

        /// <summary>Stop completes the accept loop without leaving it running.</summary>
        [Fact]
        public async Task Cancellation_during_accept_stops_loop()
        {
            await using var listener = new SocketAcceptListener(
                new ListenBinding(IPAddress.Loopback, Port: 0, DualMode: false),
                static (_, _) => ValueTask.CompletedTask,
                NullLogger.Instance);

            listener.Start();
            Assert.True(listener.AcceptLoopActive);
            await listener.StopAsync();
            Assert.False(listener.AcceptLoopActive);
            Assert.True(listener.AcceptLoopTask!.IsCompletedSuccessfully);
        }

        /// <summary>Large payload traverses the pipe stream adapter without truncating or hanging.</summary>
        [Fact]
        public async Task Large_payload_roundtrip_through_pipeline_stream()
        {
            var payload = new byte[256 * 1024];
            Random.Shared.NextBytes(payload);
            var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var listener = new SocketAcceptListener(
                new ListenBinding(IPAddress.Loopback, Port: 0, DualMode: false),
                async (socket, cancellationToken) =>
                {
                    var peer = (IPEndPoint)socket.RemoteEndPoint!;
                    await using var connection = NntpConnection.StartPlain(
                        socket,
                        ConnectionClientIdentity.Direct(peer),
                        NullLogger.Instance);
                    await using var stream = new NntpConnectionStream(connection, ownsConnection: false);
                    var buffer = new byte[payload.Length];
                    var offset = 0;
                    while (offset < buffer.Length)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        offset += read;
                    }

                    received.TrySetResult(buffer.AsSpan(0, offset).ToArray());
                    await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                },
                NullLogger.Instance);

            listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(listener.LocalEndPoint, CancellationToken.None);
            await using var clientStream = client.GetStream();
            await clientStream.WriteAsync(payload);

            var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(payload, got);

            var echo = new byte[payload.Length];
            var echoOffset = 0;
            while (echoOffset < echo.Length)
            {
                var read = await clientStream.ReadAsync(echo.AsMemory(echoOffset));
                Assert.True(read > 0);
                echoOffset += read;
            }

            Assert.Equal(payload, echo);
        }

        /// <summary>Exclusive bind rejects a second listener on the same endpoint.</summary>
        [Fact]
        public async Task Exclusive_bind_rejects_second_listener_on_same_port()
        {
            await using var owner = new SocketAcceptListener(
                new ListenBinding(IPAddress.Loopback, Port: 0, DualMode: false),
                static (_, _) => ValueTask.CompletedTask,
                NullLogger.Instance,
                reuseAddress: false);
            owner.Start();
            var port = owner.LocalEndPoint.Port;

            await using var intruder = new SocketAcceptListener(
                new ListenBinding(IPAddress.Loopback, port, DualMode: false),
                static (_, _) => ValueTask.CompletedTask,
                NullLogger.Instance,
                reuseAddress: false);
            Assert.Throws<SocketException>(() => intruder.Start());
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var start = Environment.TickCount64;
            while (!condition())
            {
                if (Environment.TickCount64 - start > timeout.TotalMilliseconds)
                {
                    throw new TimeoutException("Condition was not met in time.");
                }

                await Task.Delay(10);
            }
        }

        /// <summary>Owns connected <see cref="TcpClient"/> instances until the enclosing <c>using</c> ends.</summary>
        private sealed class TcpClientGroup : IDisposable
        {
            private readonly List<TcpClient> _clients = [];

            /// <summary>Clients still owned by this group.</summary>
            public IReadOnlyList<TcpClient> Items => _clients;

            /// <summary>Takes ownership of <paramref name="client"/>.</summary>
            public void Add(TcpClient client) => _clients.Add(client);

            /// <inheritdoc />
            public void Dispose()
            {
                foreach (var client in _clients)
                {
                    client.Dispose();
                }

                _clients.Clear();
            }
        }
    }
}
