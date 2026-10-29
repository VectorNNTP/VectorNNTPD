using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;

namespace VectorNNTPD.ReaderService.Tests.Networking.Transport;

[Collection(nameof(TransportTestHostCollection))]
public sealed class DeflateUpgradeTransportTests
{
    [Fact]
    public async Task UpgradeToDeflate_Plain_RoundTripsApplicationBytes()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        Assert.False(server.IsCompressed);

        var before = "PLAIN-BEFORE"u8.ToArray();
        await clientSocket.SendAsync(before);
        Assert.Equal(before, await TransportTestShared.ReadExactAsync(server.Input, before.Length));

        var identity = server.ClientIdentity;
        var local = server.LocalEndPoint;
        var remote = server.RemoteEndPoint;

        await server.UpgradeToDeflateAsync();
        Assert.True(server.IsCompressed);
        Assert.False(server.IsTls);
        Assert.Same(identity, server.ClientIdentity);
        Assert.Equal(local, server.LocalEndPoint);
        Assert.Equal(remote, server.RemoteEndPoint);

        await using var clientStream = new NetworkStream(clientSocket, ownsSocket: true);
        await using var clientDeflate = new NntpDeflateStream(clientStream);

        var payload = "COMPRESSED-PAYLOAD-12345"u8.ToArray();
        await clientDeflate.WriteAsync(payload);
        await clientDeflate.FlushAsync();
        Assert.Equal(payload, await TransportTestShared.ReadExactAsync(server.Input, payload.Length));

        var outbound = "SERVER-TO-CLIENT"u8.ToArray();
        await server.Output.WriteAsync(outbound);
        await server.Output.FlushAsync();
        var received = new byte[outbound.Length];
        Assert.Equal(outbound.Length, await ReadExactAsync(clientDeflate, received));
        Assert.Equal(outbound, received);
    }

    [Fact]
    public async Task UpgradeToDeflate_AfterTls_LayersDeflateAboveTls()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var tlsUpgrade = server.UpgradeToTlsAsync(host.CertificateProvider!);
        await using var network = new NetworkStream(clientSocket, ownsSocket: true);
        var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(CreateClientSslOptions());
        await tlsUpgrade;
        Assert.True(server.IsTls);
        Assert.False(server.IsCompressed);

        await server.UpgradeToDeflateAsync();
        Assert.True(server.IsTls);
        Assert.True(server.IsCompressed);

        // NntpDeflateStream takes ownership of the SslStream.
        await using var clientDeflate = new NntpDeflateStream(ssl);
        var payload = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        await clientDeflate.WriteAsync(payload);
        await clientDeflate.FlushAsync();
        Assert.Equal(payload, await TransportTestShared.ReadExactAsync(server.Input, payload.Length));
    }

    [Fact]
    public async Task UpgradeToTls_AfterDeflate_Throws()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        await server.UpgradeToDeflateAsync();
        Assert.True(server.IsCompressed);

        await using var clientStream = new NetworkStream(clientSocket, ownsSocket: true);
        await using var clientDeflate = new NntpDeflateStream(clientStream);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.UpgradeToTlsAsync(host.CertificateProvider!));
        Assert.Contains("DEFLATE", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(server.IsCompressed);
    }

    [Fact]
    public async Task UpgradeToDeflate_AlreadyCompressed_Throws()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        await server.UpgradeToDeflateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.UpgradeToDeflateAsync());
    }

    [Fact]
    public async Task UpgradeToDeflate_UnconsumedInput_RefusesAndKeepsUncompressed()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        await clientSocket.SendAsync("UNCONSUMED"u8.ToArray());
        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            waitCts.Token.ThrowIfCancellationRequested();
            if (server.Input.TryRead(out var peek))
            {
                var hasData = !peek.Buffer.IsEmpty;
                server.Input.AdvanceTo(peek.Buffer.Start, peek.Buffer.Start);
                if (hasData)
                {
                    break;
                }
            }

            await Task.Yield();
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => server.UpgradeToDeflateAsync());
        Assert.Contains("unconsumed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(server.IsCompressed);
        Assert.False(server.ConnectionClosed.IsCancellationRequested);
        Assert.Equal("UNCONSUMED"u8.ToArray(), await TransportTestShared.ReadExactAsync(server.Input, 10));
    }

    [Fact]
    public async Task UpgradeToDeflate_PreservesTrustedProxyClientIdentity()
    {
        var trusted = new TrustedProxyHosts([IPAddress.Loopback]);
        await using var host = await TransportTestHost.StartPlainWithProxyAsync(trusted);

        using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(host.EndPoint);
        await clientSocket.SendAsync(Encoding.ASCII.GetBytes(
            "PROXY TCP4 203.0.113.88 127.0.0.1 4242 119\r\n"));

        await using var server = await host.AcceptAsync();
        Assert.True(server.ClientIdentity.IsTrustedProxy);
        Assert.Equal(IPAddress.Parse("203.0.113.88"), server.ClientIdentity.ClientAddress);
        Assert.Equal(4242, server.ClientIdentity.ClientPort);
        var identity = server.ClientIdentity;

        await server.UpgradeToDeflateAsync();
        Assert.True(server.IsCompressed);
        Assert.Same(identity, server.ClientIdentity);
        Assert.Equal(IPAddress.Parse("203.0.113.88"), server.ClientIdentity.ClientAddress);
        Assert.Equal(4242, server.ClientIdentity.ClientPort);
    }

    [Fact]
    public async Task UpgradeToDeflate_MalformedPeerData_FailsTerminally()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        await server.UpgradeToDeflateAsync();
        await clientSocket.SendAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x01 });
        clientSocket.Shutdown(SocketShutdown.Send);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var result = await server.Input.ReadAsync(cts.Token);
                if (result.IsCompleted)
                {
                    throw new InvalidDataException("Input completed after malformed DEFLATE.");
                }

                server.Input.AdvanceTo(result.Buffer.End);
            }
        });

        Assert.True(server.ConnectionClosed.IsCancellationRequested);
        Assert.True(server.IsCompressed);
    }

    [Fact]
    public async Task UpgradeToDeflate_CancelledDuringQuiesce_FailsTerminally()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            server.UpgradeToDeflateAsync(cts.Token));
        Assert.True(server.ConnectionClosed.IsCancellationRequested);
    }

    [Fact]
    public async Task UpgradeToDeflate_ConcurrentWithTlsUpgrade_SecondRejected()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var tls = server.UpgradeToTlsAsync(host.CertificateProvider!);
        var deflate = server.UpgradeToDeflateAsync();

        var secondEx = await Assert.ThrowsAsync<InvalidOperationException>(() => deflate);
        Assert.Contains("already in progress", secondEx.Message, StringComparison.OrdinalIgnoreCase);

        await using var network = new NetworkStream(clientSocket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(CreateClientSslOptions());
        await tls;
        Assert.True(server.IsTls);
        Assert.False(server.IsCompressed);
    }

    [Fact]
    public async Task UpgradeToDeflate_FlushedUncompressedOutbound_IsDeliveredBeforeCompression()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var notice = "206 Compression active\r\n"u8.ToArray();
        await server.Output.WriteAsync(notice);
        await server.Output.FlushAsync();

        var receivedNotice = new byte[notice.Length];
        var noticeTotal = 0;
        using (var noticeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (noticeTotal < notice.Length)
            {
                var n = await clientSocket.ReceiveAsync(receivedNotice.AsMemory(noticeTotal), noticeCts.Token);
                Assert.True(n > 0);
                noticeTotal += n;
            }
        }

        Assert.Equal(notice, receivedNotice);

        await server.UpgradeToDeflateAsync();
        Assert.True(server.IsCompressed);

        await using var clientStream = new NetworkStream(clientSocket, ownsSocket: true);
        await using var clientDeflate = new NntpDeflateStream(clientStream);
        var payload = "after"u8.ToArray();
        await clientDeflate.WriteAsync(payload);
        await clientDeflate.FlushAsync();
        Assert.Equal(payload, await TransportTestShared.ReadExactAsync(server.Input, payload.Length));
    }

    private static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer)
    {
        var total = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], cts.Token);
            Assert.True(n > 0);
            total += n;
        }

        return total;
    }

    private static SslClientAuthenticationOptions CreateClientSslOptions() =>
        new()
        {
            TargetHost = "nntpd01.usenet.ninja",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        };
}
