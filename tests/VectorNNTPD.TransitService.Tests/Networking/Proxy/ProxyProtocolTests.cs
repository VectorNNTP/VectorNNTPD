using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Tests.Networking.Transport;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;

namespace VectorNNTPD.TransitService.Tests.Networking.Proxy;

public sealed class TrustedProxyHostsTests
{
    [Fact]
    public void EmptyProxyHosts_IsDisabled()
    {
        var hosts = new TrustedProxyHosts(Options.Create(new NntpdOptions { ProxyHosts = [] }));
        Assert.False(hosts.IsEnabled);
        Assert.False(hosts.IsTrusted(IPAddress.Loopback));
    }

    [Fact]
    public void MissingProxyHosts_IsDisabled()
    {
        var hosts = new TrustedProxyHosts(Options.Create(new NntpdOptions()));
        Assert.False(hosts.IsEnabled);
    }

    [Fact]
    public void SingleIpv4_IsTrusted()
    {
        var hosts = Create(["198.51.100.10"]);
        Assert.True(hosts.IsEnabled);
        Assert.True(hosts.IsTrusted(IPAddress.Parse("198.51.100.10")));
        Assert.False(hosts.IsTrusted(IPAddress.Parse("198.51.100.11")));
    }

    [Fact]
    public void MultipleIpv4_MatchExactly()
    {
        var hosts = Create(["198.51.100.10", "198.51.100.11"]);
        Assert.True(hosts.IsTrusted(IPAddress.Parse("198.51.100.10")));
        Assert.True(hosts.IsTrusted(IPAddress.Parse("198.51.100.11")));
        Assert.False(hosts.IsTrusted(IPAddress.Parse("198.51.100.12")));
    }

    [Fact]
    public void Ipv6_IsTrusted()
    {
        var hosts = Create(["2001:db8::10"]);
        Assert.True(hosts.IsTrusted(IPAddress.Parse("2001:db8::10")));
        Assert.True(hosts.IsTrusted(IPAddress.Parse("2001:db8:0:0:0:0:0:10")));
        Assert.False(hosts.IsTrusted(IPAddress.Parse("2001:db8::11")));
    }

    [Fact]
    public void Ipv4MappedPeer_MatchesConfiguredIpv4()
    {
        var hosts = Create(["198.51.100.10"]);
        var mapped = IPAddress.Parse("::ffff:198.51.100.10");
        Assert.True(hosts.IsTrusted(mapped));
    }

    [Fact]
    public void DuplicateAddresses_AreDeduped()
    {
        var hosts = Create(["198.51.100.10", "198.51.100.10", "::ffff:198.51.100.10"]);
        Assert.True(hosts.IsTrusted(IPAddress.Parse("198.51.100.10")));
    }

    private static TrustedProxyHosts Create(string[] entries) =>
        new(Options.Create(new NntpdOptions { ProxyHosts = entries }));
}

public sealed class ProxyHostsConfigurationTests
{
    [Fact]
    public void Validate_RejectsInvalidProxyHost()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ProxyHosts = ["not-an-ip"];
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("ProxyHosts", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsCidr()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ProxyHosts = ["198.51.100.0/24"];
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_RejectsAnyAddressWildcard()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ProxyHosts = ["0.0.0.0"];
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_AcceptsIpv4AndIpv6()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ProxyHosts = ["198.51.100.10", "2001:db8::1"];
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded);
    }
}

public sealed class ProxyProtocolParserTests
{
    private static readonly IPEndPoint TcpPeer = new(IPAddress.Parse("198.51.100.1"), 45000);

    [Fact]
    public void V1_Tcp4_ParsesClientEndpoint()
    {
        var header = Encoding.ASCII.GetBytes("PROXY TCP4 203.0.113.10 198.51.100.20 41234 119\r\n");
        var status = ProxyProtocolParser.TryParse(header, TcpPeer, out var result, out var error);
        Assert.Equal(ProxyParseStatus.Ok, status);
        Assert.Equal(string.Empty, error);
        Assert.Equal(IPAddress.Parse("203.0.113.10"), result.Identity.ClientAddress);
        Assert.Equal(41234, result.Identity.ClientPort);
        Assert.Equal(TcpPeer, result.Identity.TcpPeer);
        Assert.Equal(header.Length, result.HeaderLength);
    }

    [Fact]
    public void V1_Tcp6_ParsesClientEndpoint()
    {
        var header = Encoding.ASCII.GetBytes(
            "PROXY TCP6 2001:db8::2 2001:db8::1 41234 119\r\n");
        var status = ProxyProtocolParser.TryParse(header, TcpPeer, out var result, out _);
        Assert.Equal(ProxyParseStatus.Ok, status);
        Assert.Equal(IPAddress.Parse("2001:db8::2"), result.Identity.ClientAddress);
        Assert.Equal(41234, result.Identity.ClientPort);
    }

    [Fact]
    public void V1_Unknown_UsesTcpPeer()
    {
        var header = Encoding.ASCII.GetBytes("PROXY UNKNOWN\r\n");
        var status = ProxyProtocolParser.TryParse(header, TcpPeer, out var result, out _);
        Assert.Equal(ProxyParseStatus.Ok, status);
        Assert.Equal(TcpPeer, result.Identity.Client);
    }

    [Fact]
    public void V1_PartialDelivery_NeedsMore()
    {
        var header = Encoding.ASCII.GetBytes("PROXY TCP4 203.0.113.10 198.51.100.20 41234 119\r\n");
        for (var i = 1; i < header.Length; i++)
        {
            var status = ProxyProtocolParser.TryParse(header.AsSpan(0, i), TcpPeer, out _, out _);
            Assert.Equal(ProxyParseStatus.NeedMore, status);
        }

        Assert.Equal(
            ProxyParseStatus.Ok,
            ProxyProtocolParser.TryParse(header, TcpPeer, out _, out _));
    }

    [Fact]
    public void V1_Malformed_Fails()
    {
        var header = Encoding.ASCII.GetBytes("PROXY TCP4 not-an-ip 198.51.100.20 41234 119\r\n");
        Assert.Equal(
            ProxyParseStatus.Error,
            ProxyProtocolParser.TryParse(header, TcpPeer, out _, out var error));
        Assert.Contains("invalid address", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V1_ExceedsMaxLine_Fails()
    {
        var line = "PROXY UNKNOWN " + new string('x', 200) + "\r\n";
        Assert.Equal(
            ProxyParseStatus.Error,
            ProxyProtocolParser.TryParse(Encoding.ASCII.GetBytes(line), TcpPeer, out _, out _));
    }

    [Fact]
    public void V1_LeadingZeroPort_Fails()
    {
        var header = Encoding.ASCII.GetBytes("PROXY TCP4 203.0.113.10 198.51.100.20 01234 119\r\n");
        Assert.Equal(
            ProxyParseStatus.Error,
            ProxyProtocolParser.TryParse(header, TcpPeer, out _, out _));
    }

    [Fact]
    public void V2_Tcp4_ParsesClientEndpoint()
    {
        var header = BuildV2Tcp4(
            IPAddress.Parse("203.0.113.50"),
            IPAddress.Parse("198.51.100.20"),
            srcPort: 40000,
            dstPort: 119);
        var status = ProxyProtocolParser.TryParse(header, TcpPeer, out var result, out var error);
        Assert.Equal(ProxyParseStatus.Ok, status);
        Assert.Equal(string.Empty, error);
        Assert.Equal(IPAddress.Parse("203.0.113.50"), result.Identity.ClientAddress);
        Assert.Equal(40000, result.Identity.ClientPort);
        Assert.Equal(2, result.Version);
    }

    [Fact]
    public void V2_Tcp6_ParsesClientEndpoint()
    {
        var header = BuildV2Tcp6(
            IPAddress.Parse("2001:db8::aa"),
            IPAddress.Parse("2001:db8::bb"),
            srcPort: 40001,
            dstPort: 119);
        var status = ProxyProtocolParser.TryParse(header, TcpPeer, out var result, out _);
        Assert.Equal(ProxyParseStatus.Ok, status);
        Assert.Equal(IPAddress.Parse("2001:db8::aa"), result.Identity.ClientAddress);
        Assert.Equal(40001, result.Identity.ClientPort);
    }

    [Fact]
    public void V2_LocalCommand_UsesTcpPeer()
    {
        var header = new byte[16];
        "\r\n\r\n\0\r\nQUIT\n"u8.CopyTo(header);
        header[12] = 0x20; // version 2, LOCAL
        header[13] = 0x00;
        var status = ProxyProtocolParser.TryParse(header, TcpPeer, out var result, out _);
        Assert.Equal(ProxyParseStatus.Ok, status);
        Assert.True(result.LocalCommand);
        Assert.Equal(TcpPeer, result.Identity.Client);
    }

    [Fact]
    public void V2_PartialDelivery_NeedsMore()
    {
        var header = BuildV2Tcp4(
            IPAddress.Parse("203.0.113.50"),
            IPAddress.Parse("198.51.100.20"),
            40000,
            119);
        for (var i = 1; i < header.Length; i++)
        {
            Assert.Equal(
                ProxyParseStatus.NeedMore,
                ProxyProtocolParser.TryParse(header.AsSpan(0, i), TcpPeer, out _, out _));
        }
    }

    [Fact]
    public void V2_InvalidSignature_Fails()
    {
        var header = new byte[16];
        header.AsSpan().Fill(0x41);
        Assert.Equal(
            ProxyParseStatus.Error,
            ProxyProtocolParser.TryParse(header, TcpPeer, out _, out _));
    }

    [Fact]
    public void UnrecognizedFirstByte_Fails()
    {
        Assert.Equal(
            ProxyParseStatus.Error,
            ProxyProtocolParser.TryParse([(byte)'G'], TcpPeer, out _, out var error));
        Assert.Contains("PROXY header required", error, StringComparison.Ordinal);
    }

    [Fact]
    public void V2_UnexpectedCommand_Fails()
    {
        var header = BuildV2Tcp4(
            IPAddress.Parse("203.0.113.50"),
            IPAddress.Parse("198.51.100.20"),
            40000,
            119);
        header[12] = 0x22; // version 2, unsupported command 2
        Assert.Equal(
            ProxyParseStatus.Error,
            ProxyProtocolParser.TryParse(header, TcpPeer, out _, out var error));
        Assert.Contains("unsupported command", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V2_UnlistedFamilyProtocol_FallsBackToTcpPeer()
    {
        // Valid nibbles (family=1, protocol=0) but not a listed combination → UNSPEC fallback.
        var header = new byte[16];
        "\r\n\r\n\0\r\nQUIT\n"u8.CopyTo(header);
        header[12] = 0x21;
        header[13] = 0x10;
        header[14] = 0x00;
        header[15] = 0x00;
        var status = ProxyProtocolParser.TryParse(header, TcpPeer, out var result, out _);
        Assert.Equal(ProxyParseStatus.Ok, status);
        Assert.Equal(TcpPeer.Address, result.Identity.ClientAddress);
        Assert.Equal(TcpPeer.Port, result.Identity.ClientPort);
    }

    [Fact]
    public void V2_InvalidProtocolNibble_Fails()
    {
        var header = new byte[16];
        "\r\n\r\n\0\r\nQUIT\n"u8.CopyTo(header);
        header[12] = 0x21;
        header[13] = 0x13; // AF_INET + invalid protocol 3
        header[14] = 0x00;
        header[15] = 0x00;
        Assert.Equal(
            ProxyParseStatus.Error,
            ProxyProtocolParser.TryParse(header, TcpPeer, out _, out _));
    }

    [Fact]
    public void V2_AbsentCrc_IsAccepted()
    {
        var header = BuildV2Tcp4(
            IPAddress.Parse("203.0.113.50"),
            IPAddress.Parse("198.51.100.20"),
            40000,
            119);
        Assert.Equal(
            ProxyParseStatus.Ok,
            ProxyProtocolParser.TryParse(header, TcpPeer, out var result, out _));
        Assert.Equal(IPAddress.Parse("203.0.113.50"), result.Identity.ClientAddress);
    }

    [Fact]
    public void V2_ValidCrc32C_IsAccepted()
    {
        var header = BuildV2Tcp4WithCrc(
            IPAddress.Parse("203.0.113.50"),
            IPAddress.Parse("198.51.100.20"),
            40000,
            119,
            corruptCrc: false);
        Assert.Equal(
            ProxyParseStatus.Ok,
            ProxyProtocolParser.TryParse(header, TcpPeer, out var result, out _));
        Assert.Equal(IPAddress.Parse("203.0.113.50"), result.Identity.ClientAddress);
        Assert.Equal(40000, result.Identity.ClientPort);
    }

    [Fact]
    public void V2_InvalidCrc32C_IsRejected()
    {
        var header = BuildV2Tcp4WithCrc(
            IPAddress.Parse("203.0.113.50"),
            IPAddress.Parse("198.51.100.20"),
            40000,
            119,
            corruptCrc: true);
        Assert.Equal(
            ProxyParseStatus.Error,
            ProxyProtocolParser.TryParse(header, TcpPeer, out _, out var error));
        Assert.Contains("CRC32C", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V2_UnsupportedFamilyNibble_Fails()
    {
        var header = new byte[16];
        "\r\n\r\n\0\r\nQUIT\n"u8.CopyTo(header);
        header[12] = 0x21;
        header[13] = 0x41; // family 4 (unspecified) + STREAM
        header[14] = 0x00;
        header[15] = 0x00;
        Assert.Equal(
            ProxyParseStatus.Error,
            ProxyProtocolParser.TryParse(header, TcpPeer, out _, out var error));
        Assert.Contains("unsupported address family/protocol", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V2_MaxAddressLength_LocalCommand_Parses()
    {
        // Length field is uint16; MaxV2AddressOctets (65535) is the wire maximum — no larger value exists.
        var header = new byte[ProxyProtocolParser.MaxV2HeaderOctets];
        "\r\n\r\n\0\r\nQUIT\n"u8.CopyTo(header);
        header[12] = 0x20; // LOCAL
        header[13] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14, 2), ProxyProtocolParser.MaxV2AddressOctets);
        Assert.Equal(
            ProxyParseStatus.Ok,
            ProxyProtocolParser.TryParse(header, TcpPeer, out var result, out _));
        Assert.True(result.LocalCommand);
        Assert.Equal(ProxyProtocolParser.MaxV2HeaderOctets, result.HeaderLength);
    }

    [Fact]
    public void V2_OneByteBelowMaxAddressLength_NeedsMore()
    {
        var header = new byte[ProxyProtocolParser.MaxV2HeaderOctets];
        "\r\n\r\n\0\r\nQUIT\n"u8.CopyTo(header);
        header[12] = 0x20;
        header[13] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14, 2), ProxyProtocolParser.MaxV2AddressOctets);
        Assert.Equal(
            ProxyParseStatus.NeedMore,
            ProxyProtocolParser.TryParse(header.AsSpan(0, header.Length - 1), TcpPeer, out _, out _));
        Assert.Equal(
            ProxyParseStatus.Ok,
            ProxyProtocolParser.TryParse(header, TcpPeer, out _, out _));
    }

    [Fact]
    public void V2_OneByteBelowComplete_NeedsMore()
    {
        var header = BuildV2Tcp4(
            IPAddress.Parse("203.0.113.50"),
            IPAddress.Parse("198.51.100.20"),
            40000,
            119);
        Assert.Equal(
            ProxyParseStatus.NeedMore,
            ProxyProtocolParser.TryParse(header.AsSpan(0, header.Length - 1), TcpPeer, out _, out _));
        Assert.Equal(
            ProxyParseStatus.Ok,
            ProxyProtocolParser.TryParse(header, TcpPeer, out _, out _));
    }

    internal static byte[] BuildV2Tcp4(IPAddress src, IPAddress dst, ushort srcPort, ushort dstPort)
    {
        var header = new byte[28];
        "\r\n\r\n\0\r\nQUIT\n"u8.CopyTo(header);
        header[12] = 0x21; // ver 2 PROXY
        header[13] = 0x11; // TCP4
        header[14] = 0x00;
        header[15] = 12;
        src.GetAddressBytes().CopyTo(header, 16);
        dst.GetAddressBytes().CopyTo(header, 20);
        header[24] = (byte)(srcPort >> 8);
        header[25] = (byte)srcPort;
        header[26] = (byte)(dstPort >> 8);
        header[27] = (byte)dstPort;
        return header;
    }

    internal static byte[] BuildV2Tcp6(IPAddress src, IPAddress dst, ushort srcPort, ushort dstPort)
    {
        var header = new byte[52];
        "\r\n\r\n\0\r\nQUIT\n"u8.CopyTo(header);
        header[12] = 0x21;
        header[13] = 0x21; // TCP6
        header[14] = 0x00;
        header[15] = 36;
        src.GetAddressBytes().CopyTo(header, 16);
        dst.GetAddressBytes().CopyTo(header, 32);
        header[48] = (byte)(srcPort >> 8);
        header[49] = (byte)srcPort;
        header[50] = (byte)(dstPort >> 8);
        header[51] = (byte)dstPort;
        return header;
    }

    internal static byte[] BuildV2Tcp4WithCrc(
        IPAddress src,
        IPAddress dst,
        ushort srcPort,
        ushort dstPort,
        bool corruptCrc)
    {
        // Address block (12) + CRC TLV (3 + 4) = 19.
        var header = new byte[16 + 19];
        "\r\n\r\n\0\r\nQUIT\n"u8.CopyTo(header);
        header[12] = 0x21;
        header[13] = 0x11;
        header[14] = 0x00;
        header[15] = 19;
        src.GetAddressBytes().CopyTo(header, 16);
        dst.GetAddressBytes().CopyTo(header, 20);
        header[24] = (byte)(srcPort >> 8);
        header[25] = (byte)srcPort;
        header[26] = (byte)(dstPort >> 8);
        header[27] = (byte)dstPort;
        header[28] = 0x03; // PP2_TYPE_CRC32C
        header[29] = 0x00;
        header[30] = 0x04;
        header[31] = 0x00;
        header[32] = 0x00;
        header[33] = 0x00;
        header[34] = 0x00;
        var crc = Crc32C.Compute(header);
        if (corruptCrc)
        {
            crc ^= 0xFFFFFFFFu;
        }

        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(31, 4), crc);
        return header;
    }
}

public sealed class ConnectionClientIdentityTests
{
    [Fact]
    public void Direct_DoesNotAliasMutableEndpoints()
    {
        var original = new IPEndPoint(IPAddress.Parse("198.51.100.8"), 3333);
        var identity = ConnectionClientIdentity.Direct(original);

        Assert.False(ReferenceEquals(identity.TcpPeer, identity.Client));
        Assert.False(ReferenceEquals(identity.TcpPeer, original));
        Assert.False(ReferenceEquals(identity.Client, original));

        original.Port = 9999;
        Assert.Equal(3333, identity.ClientPort);
        Assert.Equal(3333, identity.TcpPeer.Port);

        identity.Client.Port = 1;
        Assert.Equal(3333, identity.TcpPeer.Port);
        Assert.Equal(IPAddress.Parse("198.51.100.8"), identity.TcpPeer.Address);
    }

    [Fact]
    public void FromTrustedProxy_ClonesBothEndpoints()
    {
        var peer = new IPEndPoint(IPAddress.Loopback, 40000);
        var client = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 51515);
        var identity = ConnectionClientIdentity.FromTrustedProxy(peer, client, proxyProtocolVersion: 1);

        peer.Port = 1;
        client.Port = 2;
        Assert.Equal(40000, identity.TcpPeer.Port);
        Assert.Equal(51515, identity.ClientPort);
        Assert.False(ReferenceEquals(identity.TcpPeer, peer));
        Assert.False(ReferenceEquals(identity.Client, client));
    }
}

public sealed class ProxyPreambleLiveTests
{
    [Fact]
    public async Task TrustedPeer_FragmentedV1Header_EstablishesClientIdentity()
    {
        var proxyIp = IPAddress.Loopback;
        var trusted = new TrustedProxyHosts([proxyIp]);
        await using var listener = await StartAcceptAsync();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint);

        var accepted = await listener.Accepted;
        var tcpPeer = (IPEndPoint)accepted.RemoteEndPoint!;
        Assert.True(trusted.IsTrusted(tcpPeer.Address));

        var header = Encoding.ASCII.GetBytes("PROXY TCP4 203.0.113.77 127.0.0.1 51515 119\r\nHELLO");
        await client.SendAsync(header.AsMemory(0, 10));
        await client.SendAsync(header.AsMemory(10));

        var resolution = await ProxyPreambleResolver.ResolveAsync(
            accepted,
            tcpPeer,
            trusted,
            CancellationToken.None);
        Assert.Equal(IPAddress.Parse("203.0.113.77"), resolution.Identity.ClientAddress);
        Assert.Equal(51515, resolution.Identity.ClientPort);
        Assert.True(resolution.Identity.IsTrustedProxy);
        Assert.Equal("HELLO"u8.ToArray(), resolution.Leftover.ToArray());
    }

    [Fact]
    public async Task TrustedPeer_SendsNothing_TimesOutWithoutFallbackIdentity()
    {
        var trusted = new TrustedProxyHosts([IPAddress.Loopback]);
        await using var listener = await StartAcceptAsync();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint);
        var accepted = await listener.Accepted;
        var tcpPeer = (IPEndPoint)accepted.RemoteEndPoint!;

        var ex = await Assert.ThrowsAsync<ProxyProtocolTimeoutException>(() =>
            ProxyPreambleResolver.ResolveAsync(
                accepted,
                tcpPeer,
                trusted,
                CancellationToken.None,
                preambleTimeout: TimeSpan.FromMilliseconds(200)).AsTask());
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
        accepted.Dispose();
    }

    [Fact]
    public async Task TrustedPeer_IncompleteV1ThenStall_TimesOut()
    {
        var trusted = new TrustedProxyHosts([IPAddress.Loopback]);
        await using var listener = await StartAcceptAsync();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint);
        var accepted = await listener.Accepted;
        var tcpPeer = (IPEndPoint)accepted.RemoteEndPoint!;

        await client.SendAsync("PROXY TCP4 203.0.113.1 "u8.ToArray());

        await Assert.ThrowsAsync<ProxyProtocolTimeoutException>(() =>
            ProxyPreambleResolver.ResolveAsync(
                accepted,
                tcpPeer,
                trusted,
                CancellationToken.None,
                preambleTimeout: TimeSpan.FromMilliseconds(200)).AsTask());
        accepted.Dispose();
    }

    [Fact]
    public async Task TrustedPeer_IncompleteV2ThenStall_TimesOut()
    {
        var trusted = new TrustedProxyHosts([IPAddress.Loopback]);
        await using var listener = await StartAcceptAsync();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint);
        var accepted = await listener.Accepted;
        var tcpPeer = (IPEndPoint)accepted.RemoteEndPoint!;

        await client.SendAsync("\r\n\r\n\0\r\n"u8.ToArray());

        await Assert.ThrowsAsync<ProxyProtocolTimeoutException>(() =>
            ProxyPreambleResolver.ResolveAsync(
                accepted,
                tcpPeer,
                trusted,
                CancellationToken.None,
                preambleTimeout: TimeSpan.FromMilliseconds(200)).AsTask());
        accepted.Dispose();
    }

    [Fact]
    public async Task TrustedPeer_NonProxyBytes_FailsWithoutTcpFallback()
    {
        var trusted = new TrustedProxyHosts([IPAddress.Loopback]);
        await using var listener = await StartAcceptAsync();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint);
        var accepted = await listener.Accepted;
        var tcpPeer = (IPEndPoint)accepted.RemoteEndPoint!;

        await client.SendAsync("CAPABILITIES\r\n"u8.ToArray());

        var ex = await Assert.ThrowsAsync<ProxyProtocolMalformedException>(() =>
            ProxyPreambleResolver.ResolveAsync(
                accepted,
                tcpPeer,
                trusted,
                CancellationToken.None,
                preambleTimeout: TimeSpan.FromSeconds(2)).AsTask());
        Assert.Contains("PROXY header required", ex.Message, StringComparison.Ordinal);
        accepted.Dispose();
    }

    [Fact]
    public async Task TrustedPeer_ClosesWithoutHeader_Fails()
    {
        var trusted = new TrustedProxyHosts([IPAddress.Loopback]);
        await using var listener = await StartAcceptAsync();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint);
        var accepted = await listener.Accepted;
        var tcpPeer = (IPEndPoint)accepted.RemoteEndPoint!;

        client.Dispose();

        await Assert.ThrowsAsync<ProxyProtocolMalformedException>(() =>
            ProxyPreambleResolver.ResolveAsync(
                accepted,
                tcpPeer,
                trusted,
                CancellationToken.None,
                preambleTimeout: TimeSpan.FromSeconds(2)).AsTask());
        accepted.Dispose();
    }

    [Fact]
    public async Task TrustedPeer_MissingProxyOnTlsListener_DoesNotPublishSession()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        var trusted = new TrustedProxyHosts([IPAddress.Loopback]);
        await using var host = await TransportTestHost.StartTlsWithProxyAsync(pfx, trusted);

        // First peer: trusted TCP but ordinary application bytes — must not become a session.
        using (var bad = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await bad.ConnectAsync(host.EndPoint);
            await bad.SendAsync("CAPABILITIES\r\n"u8.ToArray());
            await WaitForRemoteCloseAsync(bad);
        }

        // Second peer: valid PROXY then TLS — must be the only published connection.
        using var good = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await good.ConnectAsync(host.EndPoint);
        await good.SendAsync(Encoding.ASCII.GetBytes(
            "PROXY TCP4 203.0.113.66 127.0.0.1 45000 563\r\n"));

        var acceptTask = host.AcceptAsync();
        await using var network = new NetworkStream(good, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "nntpd01.usenet.ninja",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            });

        await using var server = await acceptTask;
        Assert.Equal(IPAddress.Parse("203.0.113.66"), server.ClientIdentity.ClientAddress);
        Assert.Equal(45000, server.ClientIdentity.ClientPort);
        Assert.True(server.ClientIdentity.IsTrustedProxy);
    }

    [Fact]
    public async Task UntrustedPeer_CannotReplaceIdentityWithProxyHeader()
    {
        var trusted = new TrustedProxyHosts([IPAddress.Parse("198.51.100.9")]);
        await using var listener = await StartAcceptAsync();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint);

        var accepted = await listener.Accepted;
        var tcpPeer = (IPEndPoint)accepted.RemoteEndPoint!;
        Assert.False(trusted.IsTrusted(tcpPeer.Address));

        var forged = Encoding.ASCII.GetBytes("PROXY TCP4 203.0.113.1 127.0.0.1 1 119\r\n");
        await client.SendAsync(forged);

        var resolution = await ProxyPreambleResolver.ResolveAsync(
            accepted,
            tcpPeer,
            trusted,
            CancellationToken.None);
        Assert.False(resolution.Identity.IsTrustedProxy);
        Assert.False(resolution.Identity.UsedProxyHeader);
        Assert.Equal(tcpPeer.Address, resolution.Identity.ClientAddress);
        Assert.Equal(tcpPeer.Port, resolution.Identity.ClientPort);
        Assert.True(resolution.Leftover.IsEmpty);

        var leftover = new byte[forged.Length];
        var read = await accepted.ReceiveAsync(leftover.AsMemory(), SocketFlags.None);
        Assert.Equal(forged.Length, read);
        Assert.Equal(forged, leftover);
    }

    [Fact]
    public async Task UntrustedPeer_V2SignatureBytesRemainOnSocket()
    {
        var trusted = new TrustedProxyHosts([IPAddress.Parse("198.51.100.9")]);
        await using var listener = await StartAcceptAsync();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint);
        var accepted = await listener.Accepted;
        var tcpPeer = (IPEndPoint)accepted.RemoteEndPoint!;

        var forged = ProxyProtocolParserTests.BuildV2Tcp4(
            IPAddress.Parse("203.0.113.9"),
            IPAddress.Loopback,
            1,
            119);
        await client.SendAsync(forged);

        var resolution = await ProxyPreambleResolver.ResolveAsync(
            accepted,
            tcpPeer,
            trusted,
            CancellationToken.None);
        Assert.Equal(tcpPeer.Address, resolution.Identity.ClientAddress);
        Assert.False(resolution.Identity.UsedProxyHeader);

        var leftover = new byte[forged.Length];
        var read = await accepted.ReceiveAsync(leftover.AsMemory(), SocketFlags.None);
        Assert.Equal(forged.Length, read);
        Assert.Equal(forged, leftover);
    }

    [Fact]
    public async Task UntrustedPeer_EstablishesDirectIdentityWithoutWaitingForProxy()
    {
        var trusted = new TrustedProxyHosts([IPAddress.Parse("198.51.100.9")]);
        await using var listener = await StartAcceptAsync();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint);
        var accepted = await listener.Accepted;
        var tcpPeer = (IPEndPoint)accepted.RemoteEndPoint!;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var resolution = await ProxyPreambleResolver.ResolveAsync(
            accepted,
            tcpPeer,
            trusted,
            cts.Token);
        Assert.False(resolution.Identity.UsedProxyHeader);
        Assert.Equal(tcpPeer.Address, resolution.Identity.ClientAddress);
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task EmptyTrustedSet_UsesTcpPeerWithoutReading()
    {
        var trusted = new TrustedProxyHosts(Options.Create(new NntpdOptions { ProxyHosts = [] }));
        await using var listener = await StartAcceptAsync();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint);
        var accepted = await listener.Accepted;
        var tcpPeer = (IPEndPoint)accepted.RemoteEndPoint!;

        var resolution = await ProxyPreambleResolver.ResolveAsync(
            accepted,
            tcpPeer,
            trusted,
            CancellationToken.None);
        Assert.Equal(tcpPeer.Address, resolution.Identity.ClientAddress);
        Assert.Equal(tcpPeer.Port, resolution.Identity.ClientPort);
        Assert.False(resolution.Identity.UsedProxyHeader);
    }

    [Fact]
    public async Task TlsWithTrustedProxy_HeaderThenHandshake_UsesProxyClientIdentity()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        var trusted = new TrustedProxyHosts([IPAddress.Loopback]);
        await using var host = await TransportTestHost.StartTlsWithProxyAsync(pfx, trusted);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(host.EndPoint);

        var header = Encoding.ASCII.GetBytes(
            "PROXY TCP4 203.0.113.44 127.0.0.1 42424 563\r\n");
        await socket.SendAsync(header);

        var acceptTask = host.AcceptAsync();
        await using var network = new NetworkStream(socket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "nntpd01.usenet.ninja",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            });

        await using var server = await acceptTask;
        Assert.True(server.IsTls);
        Assert.True(server.ClientIdentity.IsTrustedProxy);
        Assert.Equal(IPAddress.Parse("203.0.113.44"), server.ClientIdentity.ClientAddress);
        Assert.Equal(42424, server.ClientIdentity.ClientPort);
        Assert.Equal(IPAddress.Loopback, server.ClientIdentity.TcpPeer.Address);

        var payload = new byte[] { 0x10, 0x20, 0x30 };
        await ssl.WriteAsync(payload);
        await ssl.FlushAsync();
        Assert.Equal(payload, await TransportTestShared.ReadExactAsync(server.Input, payload.Length));
    }

    [Fact]
    public async Task TlsWithTrustedProxy_HeaderAndHandshakePipelined_Succeeds()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        var trusted = new TrustedProxyHosts([IPAddress.Loopback]);
        await using var host = await TransportTestHost.StartTlsWithProxyAsync(pfx, trusted);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(host.EndPoint);

        // PROXY header first; TLS ClientHello follows immediately on the same connection.
        var header = ProxyProtocolParserTests.BuildV2Tcp4(
            IPAddress.Parse("203.0.113.55"),
            IPAddress.Loopback,
            43000,
            563);
        await socket.SendAsync(header);

        var acceptTask = host.AcceptAsync();
        await using var network = new NetworkStream(socket, ownsSocket: true);
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "nntpd01.usenet.ninja",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            });

        await using var server = await acceptTask;
        Assert.Equal(IPAddress.Parse("203.0.113.55"), server.ClientIdentity.ClientAddress);
        Assert.Equal(43000, server.ClientIdentity.ClientPort);
        Assert.Equal(2, server.ClientIdentity.ProxyProtocolVersion);

        var payload = new byte[] { 0xAA, 0xBB };
        await ssl.WriteAsync(payload);
        Assert.Equal(payload, await TransportTestShared.ReadExactAsync(server.Input, payload.Length));
    }

    [Fact]
    public void Session_ExposesEffectiveClientWithoutInspectingTransport()
    {
        var identity = ConnectionClientIdentity.FromTrustedProxy(
            new IPEndPoint(IPAddress.Loopback, 40000),
            new IPEndPoint(IPAddress.Parse("203.0.113.9"), 51515),
            proxyProtocolVersion: 1);

        var sessionFromIdentity = new NntpSession(
            new IdentityOnlyConnection(identity),
            NullLogger<NntpSession>.Instance);
        Assert.Equal(IPAddress.Parse("203.0.113.9"), sessionFromIdentity.ClientAddress);
        Assert.Equal(51515, sessionFromIdentity.ClientPort);
        Assert.Equal(IPAddress.Loopback, sessionFromIdentity.TcpPeer.Address);
        Assert.Same(identity, sessionFromIdentity.ClientIdentity);

        var direct = ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Parse("198.51.100.8"), 3333));
        var directSession = new NntpSession(
            new IdentityOnlyConnection(direct),
            NullLogger<NntpSession>.Instance);
        Assert.Equal(IPAddress.Parse("198.51.100.8"), directSession.ClientAddress);
        Assert.Equal(3333, directSession.ClientPort);
        Assert.Equal(direct.TcpPeer.Address, directSession.TcpPeer.Address);
        Assert.Equal(direct.TcpPeer.Port, directSession.TcpPeer.Port);
    }

    private static async Task WaitForRemoteCloseAsync(Socket socket)
    {
        var buffer = new byte[64];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, cts.Token);
            if (read == 0)
            {
                return;
            }
        }
    }

    private static async Task<AcceptFixture> StartAcceptAsync()
    {
        var tcs = new TaskCompletionSource<Socket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var acceptTask = AcceptOneAsync(listener, tcs);
        return new AcceptFixture(listener, tcs.Task, acceptTask);
    }

    private static async Task AcceptOneAsync(Socket listener, TaskCompletionSource<Socket> tcs)
    {
        try
        {
            var accepted = await listener.AcceptAsync().ConfigureAwait(false);
            tcs.TrySetResult(accepted);
        }
        // codeql[cs/catch-of-all-exceptions] Stopping the test listener can fault the accept loop with any transport exception.
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private sealed class AcceptFixture(Socket listener, Task<Socket> accepted, Task acceptLoop) : IAsyncDisposable
    {
        public IPEndPoint EndPoint => (IPEndPoint)listener.LocalEndPoint!;

        public Task<Socket> Accepted { get; } = accepted;

        public async ValueTask DisposeAsync()
        {
            listener.Dispose();
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Best-effort test cleanup must tolerate any filesystem or disposal failure.
            catch (Exception ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
                // Listener disposed.
            }
        }
    }

    private sealed class IdentityOnlyConnection : INntpConnection
    {
        public IdentityOnlyConnection(ConnectionClientIdentity identity)
        {
            ClientIdentity = identity;
        }

        public System.IO.Pipelines.PipeReader Input => throw new NotSupportedException();

        public System.IO.Pipelines.PipeWriter Output => throw new NotSupportedException();

        public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

        public EndPoint? LocalEndPoint => null;

        public ConnectionClientIdentity ClientIdentity { get; }

        public bool IsTls => false;

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public bool IsCompressed => false;

        public CancellationToken ConnectionClosed => CancellationToken.None;
        public bool IsCompleted => false;
        public long OutboundIdleVersion => 0;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CompleteAsync(Exception? exception = null) => Task.CompletedTask;

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
