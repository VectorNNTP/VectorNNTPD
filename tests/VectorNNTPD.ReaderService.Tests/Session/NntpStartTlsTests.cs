using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Tests.Networking.Transport;

namespace VectorNNTPD.ReaderService.Tests.Session;

/// <summary>Session-level STARTTLS lifecycle regressions (plain → 382 → TLS).</summary>
[Collection(nameof(TransportTestHostCollection))]
public sealed class NntpStartTlsTests
{
    [Fact]
    public async Task StartTls_Success_Stress_NoUnconsumedPlaintextFalsePositive()
    {
        // Expose the historical ClientHello→Input race under normal AuthenticateAsClient timing.
        for (var i = 0; i < 30; i++)
        {
            var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
            await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
            using var clientSocket = await host.ConnectPlainClientAsync();
            await using var server = await host.AcceptAsync();

            var sessionTask = new NntpSession(
                    server,
                    NullLogger<NntpSession>.Instance,
                    certificateProvider: host.CertificateProvider)
                .RunAsync();

            _ = await ReadPlainLineAsync(clientSocket);
            await clientSocket.SendAsync("STARTTLS\r\n"u8.ToArray());
            Assert.StartsWith("382 ", await ReadPlainLineAsync(clientSocket), StringComparison.Ordinal);

            await using var network = new NetworkStream(clientSocket, ownsSocket: true);
            await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(CreateClientSslOptions());
            await WaitForServerTlsAsync(server);

            await WriteSslLineAsync(ssl, "DATE");
            Assert.StartsWith("111 ", await ReadSslLineAsync(ssl), StringComparison.Ordinal);

            await server.CompleteAsync();
            await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StartTls_Success_ThenDateOverTls()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var sessionTask = new NntpSession(
                server,
                NullLogger<NntpSession>.Instance,
                certificateProvider: host.CertificateProvider)
            .RunAsync();

        var greeting = await ReadPlainLineAsync(clientSocket);
        Assert.StartsWith("201 ", greeting, StringComparison.Ordinal);

        await clientSocket.SendAsync("STARTTLS\r\n"u8.ToArray());
        Assert.StartsWith("382 ", await ReadPlainLineAsync(clientSocket), StringComparison.Ordinal);

        await using var network = new NetworkStream(clientSocket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(CreateClientSslOptions());
        await WaitForServerTlsAsync(server);

        Assert.False(server.IsCompleted);
        Assert.False(sessionTask.IsCompleted);

        await WriteSslLineAsync(ssl, "DATE");
        Assert.StartsWith("111 ", await ReadSslLineAsync(ssl), StringComparison.Ordinal);

        // Tear down without QUIT to avoid racing CompleteAsync against the last status line.
        await server.CompleteAsync();
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StartTls_CommandFullyConsumed_NoFalsePositiveDrain()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var sessionTask = new NntpSession(
                server,
                NullLogger<NntpSession>.Instance,
                certificateProvider: host.CertificateProvider)
            .RunAsync();

        _ = await ReadPlainLineAsync(clientSocket);
        await clientSocket.SendAsync("STARTTLS\r\n"u8.ToArray());
        Assert.StartsWith("382 ", await ReadPlainLineAsync(clientSocket), StringComparison.Ordinal);

        await using var network = new NetworkStream(clientSocket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(CreateClientSslOptions());
        await WaitForServerTlsAsync(server);

        Assert.False(server.IsCompleted);
        await server.CompleteAsync();
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StartTls_PipelinedCommand_IsDiscarded_ThenTlsSucceeds()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var sessionTask = new NntpSession(
                server,
                NullLogger<NntpSession>.Instance,
                certificateProvider: host.CertificateProvider)
            .RunAsync();

        _ = await ReadPlainLineAsync(clientSocket);
        await clientSocket.SendAsync("STARTTLS\r\nDATE\r\n"u8.ToArray());
        Assert.StartsWith("382 ", await ReadPlainLineAsync(clientSocket), StringComparison.Ordinal);

        await using var network = new NetworkStream(clientSocket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(CreateClientSslOptions());
        await WaitForServerTlsAsync(server);

        await WriteSslLineAsync(ssl, "DATE");
        Assert.StartsWith("111 ", await ReadSslLineAsync(ssl), StringComparison.Ordinal);

        await server.CompleteAsync();
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StartTls_HandshakeFailure_IsTerminal_NoSecondaryNntpResponse()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var sessionTask = new NntpSession(
                server,
                NullLogger<NntpSession>.Instance,
                certificateProvider: host.CertificateProvider)
            .RunAsync();

        _ = await ReadPlainLineAsync(clientSocket);
        await clientSocket.SendAsync("STARTTLS\r\n"u8.ToArray());
        Assert.StartsWith("382 ", await ReadPlainLineAsync(clientSocket), StringComparison.Ordinal);

        await clientSocket.SendAsync(new byte[] { 0x15, 0x03, 0x03, 0x00, 0x02, 0x02, 0x28 });

        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(server.IsCompleted);
        Assert.True(server.ConnectionClosed.IsCancellationRequested);
        Assert.False(server.IsTls);

        using var idleCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var buffer = new byte[64];
        try
        {
            var n = await clientSocket.ReceiveAsync(buffer, idleCts.Token);
            if (n > 0)
            {
                var text = Encoding.ASCII.GetString(buffer, 0, n);
                Assert.DoesNotContain("403 ", text, StringComparison.Ordinal);
                Assert.DoesNotContain("Command failed", text, StringComparison.Ordinal);
            }
        }
        catch (OperationCanceledException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
        catch (SocketException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
    }

    [Fact]
    public async Task StartTls_ClientClosesAfter382_SessionEndsCleanly()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var sessionTask = new NntpSession(
                server,
                NullLogger<NntpSession>.Instance,
                certificateProvider: host.CertificateProvider)
            .RunAsync();

        _ = await ReadPlainLineAsync(clientSocket);
        await clientSocket.SendAsync("STARTTLS\r\n"u8.ToArray());
        Assert.StartsWith("382 ", await ReadPlainLineAsync(clientSocket), StringComparison.Ordinal);

        clientSocket.Dispose();
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(server.ConnectionClosed.IsCancellationRequested);
        Assert.False(server.IsTls);
    }

    private static async Task WaitForServerTlsAsync(INntpConnection server)
    {
        using var tlsWait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.IsTls)
        {
            tlsWait.Token.ThrowIfCancellationRequested();
            if (server.IsCompleted || server.ConnectionClosed.IsCancellationRequested)
            {
                throw new InvalidOperationException("Server closed before TLS mode was published.");
            }

            await Task.Delay(10, tlsWait.Token);
        }
    }

    private static SslClientAuthenticationOptions CreateClientSslOptions() =>
        new()
        {
            TargetHost = "nntpd01.usenet.ninja",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        };

    private static async Task<string> ReadPlainLineAsync(Socket socket)
    {
        var buffer = new byte[512];
        var total = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (total < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(total), cts.Token);
            Assert.True(n > 0);
            total += n;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var idx = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (idx >= 0)
            {
                return text[..idx];
            }
        }

        throw new InvalidOperationException("Line too long.");
    }

    private static async Task WriteSslLineAsync(SslStream ssl, string command)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
        await ssl.WriteAsync(bytes);
        await ssl.FlushAsync();
    }

    private static async Task<string> ReadSslLineAsync(SslStream ssl)
    {
        var buffer = new byte[512];
        var total = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (total < buffer.Length)
        {
            var n = await ssl.ReadAsync(buffer.AsMemory(total), cts.Token);
            Assert.True(n > 0);
            total += n;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var idx = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (idx >= 0)
            {
                return text[..idx];
            }
        }

        throw new InvalidOperationException("Line too long.");
    }
}
