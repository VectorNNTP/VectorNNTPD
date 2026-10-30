using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Listeners;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Tests.Acme;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.Common.Networking.Listeners;

namespace VectorNNTPD.ReaderService.Tests.Networking.Transport;

public sealed class ListenEndpointPlannerTests
{
    [Fact]
    public void Star_ProducesSingleDualStackIpv6Any()
    {
        var bindings = ListenEndpointPlanner.Plan(["*"], 1199);
        Assert.Single(bindings);
        Assert.Equal(IPAddress.IPv6Any, bindings[0].Address);
        Assert.True(bindings[0].DualMode);
    }

    [Fact]
    public void Star_IgnoresAdditionalExplicits()
    {
        var bindings = ListenEndpointPlanner.Plan(["*", "127.0.0.1", "::1"], 1199);
        Assert.Single(bindings);
        Assert.True(bindings[0].DualMode);
    }

    [Fact]
    public void Ipv4Any_Alone_ProducesIpv4Wildcard()
    {
        var bindings = ListenEndpointPlanner.Plan(["0.0.0.0"], 1200);
        Assert.Single(bindings);
        Assert.Equal(IPAddress.Any, bindings[0].Address);
        Assert.False(bindings[0].DualMode);
    }

    [Fact]
    public void Ipv6Any_Alone_ProducesDualStack()
    {
        var bindings = ListenEndpointPlanner.Plan(["::"], 1200);
        Assert.Single(bindings);
        Assert.Equal(IPAddress.IPv6Any, bindings[0].Address);
        Assert.True(bindings[0].DualMode);
    }

    [Fact]
    public void ExplicitIpv4_Alone()
    {
        var address = IPAddress.Parse("192.0.2.10");
        var bindings = ListenEndpointPlanner.Plan(["192.0.2.10"], 1200);
        Assert.Single(bindings);
        Assert.Equal(address, bindings[0].Address);
        Assert.False(bindings[0].DualMode);
    }

    [Fact]
    public void ExplicitIpv6_Alone()
    {
        var address = IPAddress.Parse("2001:db8::10");
        var bindings = ListenEndpointPlanner.Plan(["2001:db8::10"], 1200);
        Assert.Single(bindings);
        Assert.Equal(address, bindings[0].Address);
        Assert.False(bindings[0].DualMode);
    }

    [Fact]
    public void ExplicitAddresses_Deduplicate()
    {
        var bindings = ListenEndpointPlanner.Plan(["127.0.0.1", "127.0.0.1", "::1"], 1200);
        Assert.Equal(2, bindings.Count);
    }

    [Fact]
    public void MultipleIndependentExplicits_AreRetained()
    {
        var bindings = ListenEndpointPlanner.Plan(["192.0.2.10", "2001:db8::10"], 1200);
        Assert.Equal(2, bindings.Count);
        Assert.Contains(bindings, static b => b.Address.Equals(IPAddress.Parse("192.0.2.10")));
        Assert.Contains(bindings, static b => b.Address.Equals(IPAddress.Parse("2001:db8::10")));
    }

    [Fact]
    public void Ipv4AnyAndIpv6Any_AreSeparateSingleFamilySockets()
    {
        var bindings = ListenEndpointPlanner.Plan(["0.0.0.0", "::"], 1201);
        Assert.Equal(2, bindings.Count);
        Assert.Contains(bindings, static b => b.Address.Equals(IPAddress.Any) && !b.DualMode);
        Assert.Contains(bindings, static b => b.Address.Equals(IPAddress.IPv6Any) && !b.DualMode);
    }

    [Fact]
    public void Ipv6AnyDualMode_PlusExplicitIpv4_DoesNotOverlap()
    {
        var bindings = ListenEndpointPlanner.Plan(["::", "192.0.2.10"], 1202);
        Assert.Single(bindings);
        Assert.Equal(IPAddress.IPv6Any, bindings[0].Address);
        Assert.True(bindings[0].DualMode);
    }

    [Fact]
    public void Ipv6AnyDualMode_PlusIpv4MappedExplicit_DoesNotOverlap()
    {
        var bindings = ListenEndpointPlanner.Plan(["::", "::ffff:192.0.2.10"], 1203);
        Assert.Single(bindings);
        Assert.Equal(IPAddress.IPv6Any, bindings[0].Address);
        Assert.True(bindings[0].DualMode);
    }

    [Fact]
    public void Ipv4Any_PlusExplicitIpv4_SkipsExplicit()
    {
        var bindings = ListenEndpointPlanner.Plan(["0.0.0.0", "192.0.2.10"], 1204);
        Assert.Single(bindings);
        Assert.Equal(IPAddress.Any, bindings[0].Address);
    }

    [Fact]
    public void Ipv6Any_PlusExplicitIpv6_SkipsExplicit()
    {
        var bindings = ListenEndpointPlanner.Plan(["::", "2001:db8::10"], 1205);
        Assert.Single(bindings);
        Assert.Equal(IPAddress.IPv6Any, bindings[0].Address);
        Assert.True(bindings[0].DualMode);
    }
}

/// <summary>
/// Binds planned dual-stack endpoints on loopback/ephemeral ports (Windows dual-stack semantics).
/// </summary>
public sealed class ListenEndpointBindTests
{
    [Fact]
    public async Task DualStackIpv6Any_AcceptsIpv4AndIpv6Loopback()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        var plan = ListenEndpointPlanner.Plan(["::", "127.0.0.1"], port: 1199);
        Assert.Single(plan);
        Assert.True(plan[0].DualMode);

        // Ephemeral port: one DualMode IPv6Any listener (no separate IPv4 socket from the planner).
        var binding = new ListenBinding(IPAddress.IPv6Any, 0, DualMode: true);
        await using var listener = new SocketAcceptListener(
            binding,
            static (_, _) => ValueTask.CompletedTask,
            NullLogger<SocketAcceptListener>.Instance);
        listener.Start();
        var port = listener.LocalEndPoint.Port;

        using (var v4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await v4.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        }

        using (var v6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp))
        {
            await v6.ConnectAsync(new IPEndPoint(IPAddress.IPv6Loopback, port));
        }
    }
}

public sealed class NntpPlainTransportTests
{
    [Fact]
    public async Task Accept_ExchangeArbitraryBytes_Bidirectional()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        var payload = new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF, 0x0D, 0x0A, 0x2E, 0x2E };
        await client.SendAsync(payload);
        Assert.Equal(payload, await TransportTestShared.ReadExactAsync(server.Input, payload.Length));

        var reply = new byte[] { 0xAA, 0x55, 0x00, 0xFF };
        await server.Output.WriteAsync(reply);
        await server.Output.FlushAsync();
        var buffer = new byte[reply.Length];
        Assert.Equal(reply.Length, await client.ReceiveAsync(buffer));
        Assert.Equal(reply, buffer);
    }

    [Fact]
    public async Task FragmentedReceives_PreserveOrder()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        await client.SendAsync(new byte[] { 0x01, 0x02 });
        await client.SendAsync(new byte[] { 0x03, 0x04, 0x05 });
        Assert.Equal(
            new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 },
            await TransportTestShared.ReadExactAsync(server.Input, 5));
    }

    [Fact]
    public async Task MultipleConnections_AreIndependent()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var c1 = await host.ConnectPlainClientAsync();
        using var c2 = await host.ConnectPlainClientAsync();
        await using var s1 = await host.AcceptAsync();
        await using var s2 = await host.AcceptAsync();

        await c1.SendAsync(new byte[] { 0x11 });
        await c2.SendAsync(new byte[] { 0x22 });
        Assert.Equal(new byte[] { 0x11 }, await TransportTestShared.ReadExactAsync(s1.Input, 1));
        Assert.Equal(new byte[] { 0x22 }, await TransportTestShared.ReadExactAsync(s2.Input, 1));
    }

    [Fact]
    public async Task RemoteGracefulClose_CompletesInput()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        var client = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();

        // Arm inbound read before FIN so Ordering A/B are both covered without sleeps.
        var inboundRead = server.Input.ReadAsync().AsTask();

        client.Shutdown(SocketShutdown.Send);
        client.Dispose();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Writer completion (receive EOF) must unblock ReadAsync as IsCompleted.
        // CompleteAsync must not complete Input.Reader while this consumer is still reading.
        ReadResult result;
        try
        {
            result = await inboundRead.WaitAsync(timeout.Token);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Fail(
                "Input.Reader was completed while ReadAsync was outstanding: " + ex.Message);
            return;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Assert.Fail("Timed out waiting for inbound completion after remote FIN.");
            return;
        }

        Assert.True(result.IsCompleted);
        server.Input.AdvanceTo(result.Buffer.End);

        // 2) Connection lifecycle must reach closed (may already be cancelled).
        if (!server.ConnectionClosed.IsCancellationRequested)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                server.ConnectionClosed,
                timeout.Token);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                Assert.Fail("Expected ConnectionClosed after remote FIN.");
            }
            catch (OperationCanceledException ex) when (server.ConnectionClosed.IsCancellationRequested)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }
        }

        Assert.True(server.ConnectionClosed.IsCancellationRequested);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task ListenerContinues_AfterPriorConnectionCloses()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var c1 = await host.ConnectPlainClientAsync();
        await using var s1 = await host.AcceptAsync();
        await s1.CompleteAsync();
        await s1.DisposeAsync();
        c1.Dispose();

        using var c2 = await host.ConnectPlainClientAsync();
        await using var s2 = await host.AcceptAsync();
        await c2.SendAsync(new byte[] { 0x42 });
        Assert.Equal(new byte[] { 0x42 }, await TransportTestShared.ReadExactAsync(s2.Input, 1));
    }

    [Fact]
    public async Task PlainListenerService_BindsAndAccepts()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["127.0.0.1"];
        options.BindPort = TestHostFactory.GetFreeTcpPort();
        await using var service = new NntpPlainListenerService(
            Options.Create(options),
            new TrustedProxyHosts(Options.Create(options)),
            new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance),
            DenyAllNntpAuthenticationProvider.Instance,
            DisabledArticleIngestionQueue.Instance,
            TransitPeerAuthorization.Disabled,
            NullLoggerFactory.Instance,
            NullLogger<NntpPlainListenerService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.NotEmpty(service.LocalEndPoints);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(service.LocalEndPoints[0]);
        await client.SendAsync(new byte[] { 0x01 });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (service.ActiveConnectionCount < 1 && !cts.IsCancellationRequested)
        {
            await Task.Delay(10, cts.Token);
        }

        Assert.True(service.ActiveConnectionCount >= 1);
        await service.StopAsync(CancellationToken.None);
    }
}

public sealed class NntpTlsTransportTests
{
    [Fact]
    public async Task TlsDisabled_DoesNotBind()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindPortTls = 0;
        await using var service = new NntpTlsListenerService(
            Options.Create(options),
            new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance),
            new TrustedProxyHosts(Options.Create(options)),
            DenyAllNntpAuthenticationProvider.Instance,
            DisabledArticleIngestionQueue.Instance,
            TransitPeerAuthorization.Disabled,
            NullLoggerFactory.Instance,
            NullLogger<NntpTlsListenerService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.False(service.ListenersBound);
    }

    [Fact]
    public async Task TlsEnabledWithoutCertificate_FailsStart()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["127.0.0.1"];
        options.BindPortTls = TestHostFactory.GetFreeTcpPort();
        await using var service = new NntpTlsListenerService(
            Options.Create(options),
            new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance),
            new TrustedProxyHosts(Options.Create(options)),
            DenyAllNntpAuthenticationProvider.Instance,
            DisabledArticleIngestionQueue.Instance,
            TransitPeerAuthorization.Disabled,
            NullLoggerFactory.Instance,
            NullLogger<NntpTlsListenerService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SuccessfulHandshake_ExchangesBinary_AndPresentsIdentity()
    {
        await using var host = await TransportTestHost.StartTlsAsync(
            TransportTestShared.CreatePfx("nntpd01.usenet.ninja"));
        await using var client = await host.ConnectTlsClientAsync();
        Assert.NotNull(client.RemoteCertificate);
        Assert.Contains(
            "nntpd01.usenet.ninja",
            client.RemoteCertificate!.Subject,
            StringComparison.OrdinalIgnoreCase);

        await using var server = await host.AcceptAsync();
        Assert.True(server.IsTls);
        var payload = new byte[] { 0x00, 0x80, 0xFF, 0x0D, 0x0A, 0x2E };
        await client.Stream.WriteAsync(payload);
        await client.Stream.FlushAsync();
        Assert.Equal(payload, await TransportTestShared.ReadExactAsync(server.Input, payload.Length));
    }

    [Fact]
    public async Task TlsOutbound_MultiplePipeBatches_PreserveOrderWithoutSslFlush()
    {
        await using var host = await TransportTestHost.StartTlsAsync(
            TransportTestShared.CreatePfx("nntpd01.usenet.ninja"));
        await using var client = await host.ConnectTlsClientAsync();
        await using var server = await host.AcceptAsync();

        // Distinct PipeWriter flushes → distinct send-pump ReadAsync batches.
        // Delivery must not depend on SslStream.FlushAsync (NetworkStream is unbuffered).
        var batch1 = new byte[] { 0x01, 0x02, 0x03 };
        var batch2 = new byte[] { 0xFE, 0xFF, 0x00 };
        var batch3 = new byte[] { 0x0D, 0x0A, 0x2E, 0x2E };

        await server.Output.WriteAsync(batch1);
        await server.Output.FlushAsync();
        await server.Output.WriteAsync(batch2);
        await server.Output.FlushAsync();
        await server.Output.WriteAsync(batch3);
        await server.Output.FlushAsync();

        var expected = batch1.Concat(batch2).Concat(batch3).ToArray();
        var received = new byte[expected.Length];
        var total = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (total < expected.Length)
        {
            var n = await client.Stream.ReadAsync(received.AsMemory(total), cts.Token);
            Assert.True(n > 0, "TLS stream closed before all outbound batches arrived.");
            total += n;
        }

        Assert.Equal(expected, received);
    }

    [Fact]
    public async Task FailedHandshake_DoesNotKillListener()
    {
        await using var host = await TransportTestHost.StartTlsAsync(
            TransportTestShared.CreatePfx("nntpd01.usenet.ninja"));
        using (var raw = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await raw.ConnectAsync(host.EndPoint);
            await raw.SendAsync(new byte[] { 0x15, 0x00, 0x00, 0x00 });
        }

        await using var client = await host.ConnectTlsClientAsync();
        await using var server = await host.AcceptAsync();
        await client.Stream.WriteAsync(new byte[] { 0x01 });
        await client.Stream.FlushAsync();
        Assert.Equal(new byte[] { 0x01 }, await TransportTestShared.ReadExactAsync(server.Input, 1));
    }

    [Fact]
    public async Task CertificateRotation_KeepsOldSession_NewHandshakeUsesNewCert()
    {
        var pfxA = TransportTestShared.CreatePfx("cert-a.usenet.ninja");
        var pfxB = TransportTestShared.CreatePfx("cert-b.usenet.ninja");
        await using var host = await TransportTestHost.StartTlsAsync(pfxA);

        await using var client1 = await host.ConnectTlsClientAsync();
        Assert.Contains(
            "cert-a.usenet.ninja",
            client1.RemoteCertificate!.Subject,
            StringComparison.OrdinalIgnoreCase);
        await using var server1 = await host.AcceptAsync();

        host.PublishCertificate(pfxB);

        await using var client2 = await host.ConnectTlsClientAsync();
        Assert.Contains(
            "cert-b.usenet.ninja",
            client2.RemoteCertificate!.Subject,
            StringComparison.OrdinalIgnoreCase);
        await using var server2 = await host.AcceptAsync();

        await client1.Stream.WriteAsync(new byte[] { 0xA1 });
        await client1.Stream.FlushAsync();
        Assert.Equal(new byte[] { 0xA1 }, await TransportTestShared.ReadExactAsync(server1.Input, 1));

        await client2.Stream.WriteAsync(new byte[] { 0xB2 });
        await client2.Stream.FlushAsync();
        Assert.Equal(new byte[] { 0xB2 }, await TransportTestShared.ReadExactAsync(server2.Input, 1));
    }
}

public sealed class TlsCertificateContextProviderTests
{
    [Fact]
    public void Publish_AtomicSwap_HeldLeaseRemainsUsable()
    {
        var provider = new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance);
        provider.PublishFromPfx(
            TransportTestShared.CreatePfx("a.usenet.ninja"),
            AcmeConfigurationTests.TestPfxPassword);
        using var leaseA = provider.Acquire();
        provider.PublishFromPfx(
            TransportTestShared.CreatePfx("b.usenet.ninja"),
            AcmeConfigurationTests.TestPfxPassword);
        using var leaseB = provider.Acquire();
        Assert.Contains(
            "a.usenet.ninja",
            leaseA.Context.TargetCertificate.Subject,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "b.usenet.ninja",
            leaseB.Context.TargetCertificate.Subject,
            StringComparison.OrdinalIgnoreCase);
    }
}

internal static class TransportTestShared
{
    public static byte[] CreatePfx(string cn) =>
        TestCertificateFactory.CreateMaterial(
                [cn, "news.usenet.ninja"],
                AcmeConfigurationTests.TestPfxPassword,
                DateTimeOffset.UtcNow.AddDays(30))
            .PfxBytes;

    public static async Task<byte[]> ReadExactAsync(PipeReader reader, int count)
    {
        while (true)
        {
            var result = await reader.ReadAsync();
            if (result.Buffer.Length >= count)
            {
                var slice = result.Buffer.Slice(0, count);
                var copy = new byte[count];
                slice.CopyTo(copy.AsSpan());
                reader.AdvanceTo(slice.End);
                return copy;
            }

            if (result.IsCompleted)
            {
                throw new InvalidOperationException($"Completed before {count} bytes arrived.");
            }

            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
    }
}

internal sealed class TransportTestHost : IAsyncDisposable
{
    private readonly SocketAcceptListener _listener;
    private readonly TlsCertificateContextProvider? _certs;
    private readonly ConcurrentQueue<TaskCompletionSource<INntpConnection>> _waiters = new();
    private readonly ConcurrentQueue<INntpConnection> _ready = new();
    private readonly object _gate = new();

    private TransportTestHost(SocketAcceptListener listener, TlsCertificateContextProvider? certs)
    {
        _listener = listener;
        _certs = certs;
    }

    public IPEndPoint EndPoint => _listener.LocalEndPoint;

    public TlsCertificateContextProvider? CertificateProvider => _certs;

    public static Task<TransportTestHost> StartPlainAsync(ILogger<NntpConnection>? connectionLogger = null) =>
        StartAsync(tls: false, pfx: null, connectionLogger: connectionLogger);

    /// <summary>Plain accept path with a certificate provider available for <see cref="INntpConnection.UpgradeToTlsAsync"/>.</summary>
    public static Task<TransportTestHost> StartPlainWithCertificateAsync(byte[] pfx) =>
        StartAsync(tls: false, pfx);

    public static Task<TransportTestHost> StartTlsAsync(byte[] pfx) => StartAsync(tls: true, pfx);

    /// <summary>
    /// Starts a TLS test host that resolves PROXY identity via <paramref name="trustedProxyHosts"/>
    /// before the TLS handshake (production listener ordering).
    /// </summary>
    public static Task<TransportTestHost> StartTlsWithProxyAsync(byte[] pfx, ITrustedProxyHosts trustedProxyHosts) =>
        StartAsync(tls: true, pfx, trustedProxyHosts);

    /// <summary>Plain accept with PROXY trust evaluation and optional upgrade certificate provider.</summary>
    public static Task<TransportTestHost> StartPlainWithProxyAsync(
        ITrustedProxyHosts trustedProxyHosts,
        byte[]? upgradePfx = null) =>
        StartAsync(tls: false, upgradePfx, trustedProxyHosts);

    private static Task<TransportTestHost> StartAsync(
        bool tls,
        byte[]? pfx,
        ITrustedProxyHosts? trustedProxyHosts = null,
        ILogger<NntpConnection>? connectionLogger = null)
    {
        TlsCertificateContextProvider? certs = null;
        if (pfx is not null)
        {
            certs = new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance);
            certs.PublishFromPfx(pfx, AcmeConfigurationTests.TestPfxPassword);
        }

        if (tls)
        {
            ArgumentNullException.ThrowIfNull(certs);
        }

        trustedProxyHosts ??= new TrustedProxyHosts(Options.Create(new NntpdOptions { ProxyHosts = [] }));
        var logger = connectionLogger ?? NullLogger<NntpConnection>.Instance;

        TransportTestHost? host = null;
        var binding = new ListenBinding(IPAddress.Loopback, 0, DualMode: false);
        var listener = new SocketAcceptListener(
            binding,
            async (socket, ct) =>
            {
                if (!ProxyPreambleResolver.TryGetTcpPeer(socket, out var tcpPeer))
                {
                    socket.Dispose();
                    return;
                }

                ProxyPreambleResolution preamble;
                try
                {
                    preamble = await ProxyPreambleResolver
                        .ResolveAsync(socket, tcpPeer, trustedProxyHosts, ct)
                        .ConfigureAwait(false);
                }
                catch (ProxyProtocolException)
                {
                    socket.Dispose();
                    return;
                }

                INntpConnection connection = tls
                    ? await NntpConnection.StartTlsAsync(
                            socket,
                            certs!,
                            preamble.Identity,
                            logger,
                            ct,
                            preamble.Leftover)
                        .ConfigureAwait(false)
                    : NntpConnection.StartPlain(
                        socket,
                        preamble.Identity,
                        logger,
                        preamble.Leftover);

                lock (host!._gate)
                {
                    if (host._waiters.TryDequeue(out var waiter))
                    {
                        waiter.TrySetResult(connection);
                    }
                    else
                    {
                        host._ready.Enqueue(connection);
                    }
                }
            },
            NullLogger<SocketAcceptListener>.Instance);

        host = new TransportTestHost(listener, certs);
        listener.Start();
        return Task.FromResult(host);
    }

    public void PublishCertificate(byte[] pfx)
    {
        ArgumentNullException.ThrowIfNull(_certs);
        _certs.PublishFromPfx(pfx, AcmeConfigurationTests.TestPfxPassword);
    }

    public async Task<Socket> ConnectPlainClientAsync()
    {
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(EndPoint);
        return client;
    }

    public async Task<TlsClient> ConnectTlsClientAsync()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(EndPoint);
        var network = new NetworkStream(socket, ownsSocket: true);
        var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "nntpd01.usenet.ninja",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            });
        var remoteCertificate = ssl.RemoteCertificate is null
            ? null
            : new X509Certificate2(ssl.RemoteCertificate);
        return new TlsClient(ssl, remoteCertificate);
    }

    public async Task<INntpConnection> AcceptAsync()
    {
        TaskCompletionSource<INntpConnection> tcs;
        lock (_gate)
        {
            if (_ready.TryDequeue(out var ready))
            {
                return ready;
            }

            tcs = new TaskCompletionSource<INntpConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(tcs);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var reg = cts.Token.Register(
            static s => ((TaskCompletionSource<INntpConnection>)s!).TrySetCanceled(),
            tcs);
        return await tcs.Task;
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();
        if (_certs is not null)
        {
            await _certs.DisposeAsync();
        }
    }

    internal sealed class TlsClient : IAsyncDisposable
    {
        public TlsClient(SslStream stream, X509Certificate2? remoteCertificate)
        {
            Stream = stream;
            RemoteCertificate = remoteCertificate;
        }

        public SslStream Stream { get; }

        public X509Certificate2? RemoteCertificate { get; }

        public ValueTask DisposeAsync()
        {
            Stream.Dispose();
            RemoteCertificate?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
