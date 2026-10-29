using System.IO.Compression;
using System.Text;

namespace VectorNNTPD.TransitService.Tests.Networking.Transport;

/// <summary>
/// Codec-level tests for raw DEFLATE used by NNTP COMPRESS (RFC 8054 §4 / RFC 1951).
/// </summary>
public sealed class NntpDeflateStreamTests
{
    [Fact]
    public void DeflateStream_ProducesRawDeflate_NotZlibOrGzip()
    {
        var plain = "hello hello hello hello NNTP raw deflate"u8.ToArray();
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            ds.Write(plain);
        }

        var compressed = ms.ToArray();
        Assert.NotEmpty(compressed);
        // zlib wrapper starts with CMF/FLG typically 0x78; gzip with 0x1F 0x8B.
        Assert.NotEqual(0x78, compressed[0]);
        Assert.False(compressed.Length >= 2 && compressed[0] == 0x1F && compressed[1] == 0x8B);

        using var zlibMs = new MemoryStream();
        using (var zs = new ZLibStream(zlibMs, CompressionLevel.Optimal, leaveOpen: true))
        {
            zs.Write(plain);
        }

        Assert.Equal(0x78, zlibMs.ToArray()[0]);
        Assert.ThrowsAny<InvalidDataException>(() =>
        {
            using var reader = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            reader.CopyTo(Stream.Null);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("hello world")]
    [InlineData("AAAAAAABBBBBBCCCCCC")]
    public async Task RoundTrip_VariousPayloads(string text)
    {
        var plain = Encoding.UTF8.GetBytes(text);
        await AssertRoundTripAsync(plain);
    }

    [Fact]
    public async Task RoundTrip_BinaryAndHighEntropy()
    {
        var plain = new byte[4096];
        Random.Shared.NextBytes(plain);
        await AssertRoundTripAsync(plain);
    }

    [Fact]
    public async Task RoundTrip_LargeStreaming()
    {
        var plain = new byte[256 * 1024];
        for (var i = 0; i < plain.Length; i++)
        {
            plain[i] = (byte)('A' + (i % 26));
        }

        await AssertRoundTripAsync(plain, writeChunk: 17, readChunk: 31);
    }

    [Fact]
    public async Task Flush_MakesCompressedBytesVisibleWithoutDispose()
    {
        await using var peer = await ConnectedStreamPair.CreateAsync();
        await using var server = new VectorNNTPD.Common.Networking.Transport.NntpDeflateStream(peer.Server);
        await using var client = new VectorNNTPD.Common.Networking.Transport.NntpDeflateStream(peer.Client);

        var payload = "flush-boundary-test-payload"u8.ToArray();
        await server.WriteAsync(payload);
        await server.FlushAsync();

        var buffer = new byte[payload.Length];
        var total = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (total < buffer.Length)
        {
            var n = await client.ReadAsync(buffer.AsMemory(total), cts.Token);
            Assert.True(n > 0);
            total += n;
        }

        Assert.Equal(payload, buffer);
    }

    [Fact]
    public async Task MalformedCompressedInput_ThrowsWithoutSilentCorruption()
    {
        await using var peer = await ConnectedStreamPair.CreateAsync();
        await using var server = new VectorNNTPD.Common.Networking.Transport.NntpDeflateStream(peer.Server);
        // Write non-DEFLATE junk on the wire (peer.Client is the raw NetworkStream).
        await peer.Client.WriteAsync(new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE });
        await peer.Client.FlushAsync();

        var buffer = new byte[64];
        await Assert.ThrowsAnyAsync<InvalidDataException>(async () =>
        {
            _ = await server.ReadAsync(buffer);
        });
    }

    [Fact]
    public async Task IndependentStreams_DoNotShareDictionaryState()
    {
        await using var a = await ConnectedStreamPair.CreateAsync();
        await using var b = await ConnectedStreamPair.CreateAsync();
        await using var aServer = new VectorNNTPD.Common.Networking.Transport.NntpDeflateStream(a.Server);
        await using var aClient = new VectorNNTPD.Common.Networking.Transport.NntpDeflateStream(a.Client);
        await using var bServer = new VectorNNTPD.Common.Networking.Transport.NntpDeflateStream(b.Server);
        await using var bClient = new VectorNNTPD.Common.Networking.Transport.NntpDeflateStream(b.Client);

        await aServer.WriteAsync("connection-A-payload"u8.ToArray());
        await aServer.FlushAsync();
        await bServer.WriteAsync("connection-B-different"u8.ToArray());
        await bServer.FlushAsync();

        var bufA = new byte[20];
        var bufB = new byte[22];
        Assert.Equal(20, await ReadExactAsync(aClient, bufA));
        Assert.Equal(22, await ReadExactAsync(bClient, bufB));
        Assert.Equal("connection-A-payload"u8.ToArray(), bufA);
        Assert.Equal("connection-B-different"u8.ToArray(), bufB);
    }

    private static async Task AssertRoundTripAsync(byte[] plain, int writeChunk = 0, int readChunk = 0)
    {
        await using var peer = await ConnectedStreamPair.CreateAsync();
        await using var server = new VectorNNTPD.Common.Networking.Transport.NntpDeflateStream(peer.Server);
        await using var client = new VectorNNTPD.Common.Networking.Transport.NntpDeflateStream(peer.Client);

        if (writeChunk <= 0)
        {
            await server.WriteAsync(plain);
        }
        else
        {
            for (var i = 0; i < plain.Length; i += writeChunk)
            {
                var len = Math.Min(writeChunk, plain.Length - i);
                await server.WriteAsync(plain.AsMemory(i, len));
            }
        }

        await server.FlushAsync();

        var received = new byte[plain.Length];
        if (readChunk <= 0)
        {
            Assert.Equal(plain.Length, await ReadExactAsync(client, received));
        }
        else
        {
            var total = 0;
            while (total < received.Length)
            {
                var n = await client.ReadAsync(received.AsMemory(total, Math.Min(readChunk, received.Length - total)));
                Assert.True(n > 0);
                total += n;
            }
        }

        Assert.Equal(plain, received);
    }

    private static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer)
    {
        var total = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], cts.Token);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private sealed class ConnectedStreamPair : IAsyncDisposable
    {
        private readonly System.Net.Sockets.Socket _listener;
        private readonly System.Net.Sockets.Socket _serverSocket;
        private readonly System.Net.Sockets.Socket _clientSocket;

        private ConnectedStreamPair(
            System.Net.Sockets.Socket listener,
            System.Net.Sockets.Socket serverSocket,
            System.Net.Sockets.Socket clientSocket,
            System.Net.Sockets.NetworkStream server,
            System.Net.Sockets.NetworkStream client)
        {
            _listener = listener;
            _serverSocket = serverSocket;
            _clientSocket = clientSocket;
            Server = server;
            Client = client;
        }

        public System.Net.Sockets.NetworkStream Server { get; }
        public System.Net.Sockets.NetworkStream Client { get; }

        public static async Task<ConnectedStreamPair> CreateAsync()
        {
            var listener = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            listener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            listener.Listen(1);

            var clientSocket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            var connect = clientSocket.ConnectAsync(listener.LocalEndPoint!);
            var serverSocket = await listener.AcceptAsync();
            await connect;

            return new ConnectedStreamPair(
                listener,
                serverSocket,
                clientSocket,
                new System.Net.Sockets.NetworkStream(serverSocket, ownsSocket: true),
                new System.Net.Sockets.NetworkStream(clientSocket, ownsSocket: true));
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Client.DisposeAsync();
            _listener.Dispose();
            _serverSocket.Dispose();
            _clientSocket.Dispose();
        }
    }
}
