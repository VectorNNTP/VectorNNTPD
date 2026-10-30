using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.TransitService.Tests.Networking.Transport;

public sealed class TlsUpgradeTransportTests
{
    [Fact]
    public async Task UpgradeToTls_SameSocket_PlainThenEncryptedTraffic()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);

        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        Assert.False(server.IsTls);

        var localBefore = server.LocalEndPoint;
        var remoteBefore = server.RemoteEndPoint;
        var identityBefore = server.ClientIdentity;

        var plain = "PLAIN-BEFORE"u8.ToArray();
        await clientSocket.SendAsync(plain);
        Assert.Equal(plain, await TransportTestShared.ReadExactAsync(server.Input, plain.Length));

        var upgradeTask = server.UpgradeToTlsAsync(host.CertificateProvider!);
        await using var network = new NetworkStream(clientSocket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(CreateClientSslOptions(clientCertificate: null));
        await upgradeTask;

        Assert.True(server.IsTls);
        Assert.Equal(localBefore, server.LocalEndPoint);
        Assert.Equal(remoteBefore, server.RemoteEndPoint);
        Assert.Same(identityBefore, server.ClientIdentity);
        Assert.False(server.ClientIdentity.UsedProxyHeader);

        var encrypted = new byte[] { 0x10, 0x20, 0x30, 0x0D, 0x0A };
        await ssl.WriteAsync(encrypted);
        await ssl.FlushAsync();
        Assert.Equal(encrypted, await TransportTestShared.ReadExactAsync(server.Input, encrypted.Length));

        var outbound = new byte[] { 0xAA, 0xBB, 0xCC };
        await server.Output.WriteAsync(outbound);
        await server.Output.FlushAsync();
        var received = new byte[outbound.Length];
        var total = 0;
        while (total < received.Length)
        {
            var n = await ssl.ReadAsync(received.AsMemory(total));
            Assert.True(n > 0);
            total += n;
        }

        Assert.Equal(outbound, received);
    }

    [Fact]
    public async Task ImplicitTls_ClientWithoutCertificate_HandshakeSucceeds()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartTlsAsync(pfx);
        await using var client = await host.ConnectTlsClientAsync();
        await using var server = await host.AcceptAsync();

        Assert.True(server.IsTls);
        Assert.Null(client.Stream.LocalCertificate);
        Assert.NotNull(client.RemoteCertificate);

        var payload = new byte[] { 0x01, 0x02, 0x03 };
        await client.Stream.WriteAsync(payload);
        Assert.Equal(payload, await TransportTestShared.ReadExactAsync(server.Input, payload.Length));
    }

    [Fact]
    public async Task UpgradeToTls_ClientWithoutCertificate_HandshakeSucceeds()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var upgradeTask = server.UpgradeToTlsAsync(host.CertificateProvider!);
        await using var network = new NetworkStream(clientSocket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        // Explicitly no client certificate.
        await ssl.AuthenticateAsClientAsync(CreateClientSslOptions(clientCertificate: null));
        await upgradeTask;

        Assert.True(server.IsTls);
        Assert.Null(ssl.LocalCertificate);
    }

    [Fact]
    public async Task UpgradeToTls_PreservesTrustedProxyClientIdentity()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        var trusted = new TrustedProxyHosts([IPAddress.Loopback]);
        await using var host = await TransportTestHost.StartPlainWithProxyAsync(trusted, pfx);

        using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(host.EndPoint);
        await clientSocket.SendAsync(Encoding.ASCII.GetBytes(
            "PROXY TCP4 203.0.113.77 127.0.0.1 51515 119\r\n"));

        await using var server = await host.AcceptAsync();
        Assert.False(server.IsTls);
        Assert.True(server.ClientIdentity.IsTrustedProxy);
        Assert.Equal(IPAddress.Parse("203.0.113.77"), server.ClientIdentity.ClientAddress);
        Assert.Equal(51515, server.ClientIdentity.ClientPort);
        var identity = server.ClientIdentity;

        var upgradeTask = server.UpgradeToTlsAsync(host.CertificateProvider!);
        await using var network = new NetworkStream(clientSocket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(CreateClientSslOptions(clientCertificate: null));
        await upgradeTask;

        Assert.True(server.IsTls);
        Assert.Same(identity, server.ClientIdentity);
        Assert.Equal(IPAddress.Parse("203.0.113.77"), server.ClientIdentity.ClientAddress);
        Assert.Equal(51515, server.ClientIdentity.ClientPort);
        Assert.Equal(IPAddress.Loopback, server.ClientIdentity.TcpPeer.Address);

        var payload = new byte[] { 0x01, 0x02 };
        await ssl.WriteAsync(payload);
        Assert.Equal(payload, await TransportTestShared.ReadExactAsync(server.Input, payload.Length));
    }

    [Fact]
    public async Task After382_WithReadsStillActive_ClientHelloEntersApplicationInput()
    {
        // Documents the historical failure mode: writing 382 while reads remain Active lets
        // ClientHello commit into application Input and fail EnsureApplicationInputDrainedForUpgrade.
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var notice = "382 Continue with TLS negotiation\r\n"u8.ToArray();
        await server.Output.WriteAsync(notice);
        await server.Output.FlushAsync();

        var received = new byte[notice.Length];
        var total = 0;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (total < received.Length)
            {
                var n = await clientSocket.ReceiveAsync(received.AsMemory(total), cts.Token);
                Assert.True(n > 0);
                total += n;
            }
        }

        // Minimal TLS record header (Handshake / TLS 1.2) — enough to prove content-type 0x16.
        var clientHelloPrefix = new byte[] { 0x16, 0x03, 0x03, 0x00, 0x04, 0x01, 0x00, 0x00, 0x00 };
        await clientSocket.SendAsync(clientHelloPrefix);

        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte first = 0;
        while (true)
        {
            waitCts.Token.ThrowIfCancellationRequested();
            if (server.Input.TryRead(out var peek) && !peek.Buffer.IsEmpty)
            {
                first = peek.Buffer.FirstSpan[0];
                server.Input.AdvanceTo(peek.Buffer.Start, peek.Buffer.Start);
                break;
            }

            await Task.Yield();
        }

        Assert.Equal(0x16, first);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.UpgradeToTlsAsync(host.CertificateProvider!));
        Assert.Contains("unconsumed plaintext", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(server.IsTls);
        Assert.False(server.IsCompleted);
    }

    [Fact]
    public async Task PauseReads_Before382_ClientHelloDoesNotEnterApplicationInput()
    {
        // Required STARTTLS order: PauseReads → write 382 → ClientHello must not hit Input.
        // With reads paused, the receive pump does not commit socket octets to Input; SslStream
        // reads ClientHello from the network stream (or PrefixedStream if an in-flight read raced).
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        await server.PauseReadsAsync();

        var idleBefore = server.OutboundIdleVersion;
        var notice = "382 Continue with TLS negotiation\r\n"u8.ToArray();
        await server.Output.WriteAsync(notice);
        await server.Output.FlushAsync();
        await server.WaitForOutboundDeliveryAsync(idleBefore);

        var received = new byte[notice.Length];
        var total = 0;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (total < received.Length)
            {
                var n = await clientSocket.ReceiveAsync(received.AsMemory(total), cts.Token);
                Assert.True(n > 0);
                total += n;
            }
        }

        // Start the client handshake immediately after 382 — the historical race window.
        var upgradeTask = server.UpgradeToTlsAsync(host.CertificateProvider!);
        await using var network = new NetworkStream(clientSocket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        var clientAuth = ssl.AuthenticateAsClientAsync(CreateClientSslOptions(clientCertificate: null));

        // While handshake is in flight, application Input must not observe ClientHello.
        for (var i = 0; i < 20; i++)
        {
            if (server.Input.TryRead(out var peek))
            {
                Assert.True(
                    peek.Buffer.IsEmpty,
                    "ClientHello must not enter application Input after PauseReads.");
                server.Input.AdvanceTo(peek.Buffer.Start, peek.Buffer.Start);
            }

            if (upgradeTask.IsCompleted && clientAuth.IsCompleted)
            {
                break;
            }

            await Task.Yield();
        }

        await clientAuth;
        await upgradeTask;

        Assert.True(server.IsTls);
        Assert.False(server.Input.TryRead(out var leftover) && !leftover.Buffer.IsEmpty);
    }

    [Fact]
    public async Task PauseReadsBefore382_ThenClientHello_UpgradeSucceeds()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        // Correct STARTTLS handoff: pause reads BEFORE 382 can reach the peer.
        await server.PauseReadsAsync();
        var idleBefore = server.OutboundIdleVersion;
        var notice = "382 Continue with TLS negotiation\r\n"u8.ToArray();
        await server.Output.WriteAsync(notice);
        await server.Output.FlushAsync();
        await server.WaitForOutboundDeliveryAsync(idleBefore);

        var received = new byte[notice.Length];
        var total = 0;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (total < received.Length)
            {
                var n = await clientSocket.ReceiveAsync(received.AsMemory(total), cts.Token);
                Assert.True(n > 0);
                total += n;
            }
        }

        var upgradeTask = server.UpgradeToTlsAsync(host.CertificateProvider!);
        await using var network = new NetworkStream(clientSocket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(CreateClientSslOptions(clientCertificate: null));
        await upgradeTask;

        Assert.True(server.IsTls);
        Assert.False(server.Input.TryRead(out var leftover) && !leftover.Buffer.IsEmpty);

        var payload = new byte[] { 0x0A, 0x0B };
        await ssl.WriteAsync(payload);
        Assert.Equal(payload, await TransportTestShared.ReadExactAsync(server.Input, payload.Length));
    }

    [Fact]
    public async Task UpgradeToTls_UnconsumedInput_RefusesUpgradeAndKeepsPlaintext()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
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
                // Leave bytes unconsumed and unexamined so UpgradeToTlsAsync can still observe them.
                server.Input.AdvanceTo(peek.Buffer.Start, peek.Buffer.Start);
                if (hasData)
                {
                    break;
                }
            }

            await Task.Yield();
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.UpgradeToTlsAsync(host.CertificateProvider!));
        Assert.Contains("unconsumed plaintext", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(server.IsTls);
        // Precondition failure refuses the upgrade without tearing down plaintext transport.
        Assert.False(server.ConnectionClosed.IsCancellationRequested);

        // Session can still consume the buffered plaintext.
        Assert.Equal("UNCONSUMED"u8.ToArray(), await TransportTestShared.ReadExactAsync(server.Input, 10));
    }

    [Fact]
    public async Task UpgradeToTls_InvalidClientHello_FailsWithoutPlaintextFallback()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var upgradeTask = server.UpgradeToTlsAsync(host.CertificateProvider!, cts.Token);
        await clientSocket.SendAsync(new byte[] { 0x15, 0x03, 0x03, 0x00, 0x02, 0x02, 0x28 });
        await Assert.ThrowsAnyAsync<Exception>(() => upgradeTask);
        Assert.False(server.IsTls);
        Assert.True(server.ConnectionClosed.IsCancellationRequested);
    }

    [Fact]
    public async Task UpgradeToTls_ClientClosesDuringHandshake_FailsTerminally()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var upgradeTask = server.UpgradeToTlsAsync(host.CertificateProvider!, cts.Token);
        await Task.Yield();
        clientSocket.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => upgradeTask);
        Assert.False(server.IsTls);
        Assert.True(server.ConnectionClosed.IsCancellationRequested);
    }

    [Fact]
    public async Task UpgradeToTls_Cancelled_FailsTerminally()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        using var cts = new CancellationTokenSource();
        var upgradeTask = server.UpgradeToTlsAsync(host.CertificateProvider!, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upgradeTask);
        Assert.False(server.IsTls);
        Assert.True(server.ConnectionClosed.IsCancellationRequested);
    }

    [Fact]
    public async Task UpgradeToTls_AlreadyTls_Throws()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartTlsAsync(pfx);
        await using var client = await host.ConnectTlsClientAsync();
        await using var server = await host.AcceptAsync();
        Assert.True(server.IsTls);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.UpgradeToTlsAsync(host.CertificateProvider!));
    }

    [Fact]
    public async Task UpgradeToTls_AfterComplete_Throws()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        await server.CompleteAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            server.UpgradeToTlsAsync(host.CertificateProvider!));
    }

    [Fact]
    public async Task UpgradeToTls_ConcurrentCalls_SecondRejected()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var first = server.UpgradeToTlsAsync(host.CertificateProvider!);
        var second = server.UpgradeToTlsAsync(host.CertificateProvider!);

        var secondEx = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Contains("already in progress", secondEx.Message, StringComparison.OrdinalIgnoreCase);

        await using var network = new NetworkStream(clientSocket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(CreateClientSslOptions(clientCertificate: null));
        await first;
        Assert.True(server.IsTls);
    }

    [Fact]
    public async Task UpgradeToTls_FlushedPlaintextOutbound_IsDeliveredBeforeHandshake()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var notice = "382 Go ahead\r\n"u8.ToArray();
        await server.Output.WriteAsync(notice);
        await server.Output.FlushAsync();

        var receivedNotice = new byte[notice.Length];
        var noticeTotal = 0;
        using (var noticeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (noticeTotal < notice.Length)
            {
                var n = await clientSocket.ReceiveAsync(
                    receivedNotice.AsMemory(noticeTotal),
                    noticeCts.Token);
                Assert.True(n > 0);
                noticeTotal += n;
            }
        }

        Assert.Equal(notice, receivedNotice);

        var upgradeTask = server.UpgradeToTlsAsync(host.CertificateProvider!);
        await using var network = new NetworkStream(clientSocket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(CreateClientSslOptions(clientCertificate: null));
        await upgradeTask;
        Assert.True(server.IsTls);
    }

    private static SslClientAuthenticationOptions CreateClientSslOptions(
        System.Security.Cryptography.X509Certificates.X509Certificate2? clientCertificate) =>
        new()
        {
            TargetHost = "nntpd01.usenet.ninja",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificates = clientCertificate is null
                ? []
                : [clientCertificate],
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        };
}
