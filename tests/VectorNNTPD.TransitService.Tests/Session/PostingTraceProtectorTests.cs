using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Session.Commands.Posting;
using VectorNNTPD.TransitService.Tests.Fixtures;

namespace VectorNNTPD.TransitService.Tests.Session;

public sealed class PostingTraceProtectorTests
{
    private static readonly byte[] CurrentKey = Convert.FromHexString(TestHostFactory.TestXTraceKey);
    private static readonly byte[] PreviousKey = Convert.FromHexString(
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

    [Fact]
    public void Protect_RoundTripsTransportIdentity()
    {
        var protector = new AesGcmPostingTraceProtector(CurrentKey);
        var payload = new PostingTracePayload(
            IPAddress.Parse("198.18.0.123"),
            1199,
            new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        var token = protector.Protect(payload);
        Assert.StartsWith(AesGcmPostingTraceProtector.TokenPrefix, token, StringComparison.Ordinal);
        Assert.DoesNotContain("198.18.0.123", token, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(payload.Address.GetAddressBytes()), token, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.ASCII.GetBytes("198.18.0.123")), token, StringComparison.Ordinal);
        Assert.True(protector.TryUnprotect(token, out var recovered));
        Assert.Equal(payload.Address, recovered.Address);
        Assert.Equal(payload.Port, recovered.Port);
        Assert.Equal(payload.InjectedAtUtc, recovered.InjectedAtUtc);
        Assert.Equal(payload.TraceId, recovered.TraceId);
        Assert.Null(recovered.AuthenticatedUsername);
    }

    [Fact]
    public void Protect_RoundTripsAuthenticatedUsername()
    {
        var protector = new AesGcmPostingTraceProtector(CurrentKey);
        var payload = new PostingTracePayload(
            IPAddress.Parse("198.18.0.123"),
            1199,
            new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            "poster+tag/name");

        var token = protector.Protect(payload);
        Assert.DoesNotContain("poster+tag/name", token, StringComparison.Ordinal);
        Assert.DoesNotContain("198.18.0.123", token, StringComparison.Ordinal);
        Assert.True(protector.TryUnprotect(token, out var recovered));
        Assert.Equal(payload.Address, recovered.Address);
        Assert.Equal(payload.Port, recovered.Port);
        Assert.Equal(payload.InjectedAtUtc, recovered.InjectedAtUtc);
        Assert.Equal(payload.TraceId, recovered.TraceId);
        Assert.Equal("poster+tag/name", recovered.AuthenticatedUsername);
    }

    [Fact]
    public void Protect_EmptyUsername_IsAbsent()
    {
        var protector = new AesGcmPostingTraceProtector(CurrentKey);
        var token = protector.Protect(new PostingTracePayload(
            IPAddress.Loopback,
            119,
            DateTimeOffset.UnixEpoch,
            Guid.Empty,
            "   "));
        Assert.True(protector.TryUnprotect(token, out var recovered));
        Assert.Null(recovered.AuthenticatedUsername);
    }

    [Fact]
    public void LegacyV1Payload_DecryptsWithoutUsername()
    {
        var protector = new AesGcmPostingTraceProtector(CurrentKey);
        var payload = new PostingTracePayload(
            IPAddress.Parse("192.0.2.10"),
            119,
            DateTimeOffset.UnixEpoch,
            Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var token = protector.ProtectLegacyV1(payload);
        Assert.True(protector.TryUnprotect(token, out var recovered));
        Assert.Equal(payload.Address, recovered.Address);
        Assert.Equal(payload.Port, recovered.Port);
        Assert.Equal(payload.TraceId, recovered.TraceId);
        Assert.Null(recovered.AuthenticatedUsername);
    }

    [Fact]
    public void PreviousKey_CanDecryptUsernamePayload()
    {
        var oldProtector = new AesGcmPostingTraceProtector(PreviousKey);
        var token = oldProtector.Protect(new PostingTracePayload(
            IPAddress.Parse("192.0.2.55"),
            563,
            DateTimeOffset.UnixEpoch,
            Guid.NewGuid(),
            "newsmaster"));
        var rotated = new AesGcmPostingTraceProtector(CurrentKey, PreviousKey);
        Assert.True(rotated.TryUnprotect(token, out var recovered));
        Assert.Equal("newsmaster", recovered.AuthenticatedUsername);
        Assert.False(new AesGcmPostingTraceProtector(CurrentKey).TryUnprotect(token, out _));
    }

    [Fact]
    public void InvalidToken_IsRejected()
    {
        var protector = new AesGcmPostingTraceProtector(CurrentKey);
        Assert.False(protector.TryUnprotect("not-a-token", out _));
        Assert.False(protector.TryUnprotect("v1.$$$$", out _));
        Assert.False(protector.TryUnprotect("v2.AAAA", out _));
    }

    [Fact]
    public void MalformedInnerPayloads_DoNotDecrypt()
    {
        var protector = new AesGcmPostingTraceProtector(CurrentKey);
        Assert.False(protector.TryUnprotect(protector.ProtectRawForTests(LegacyWithExtraByte()), out _));
        Assert.False(protector.TryUnprotect(protector.ProtectRawForTests(UnknownVersion()), out _));
        Assert.False(protector.TryUnprotect(protector.ProtectRawForTests(Version2LengthMismatch()), out _));
        Assert.False(protector.TryUnprotect(protector.ProtectRawForTests(Version2InvalidUtf8()), out _));
        Assert.False(protector.TryUnprotect(protector.ProtectRawForTests(Version2EmbeddedNul()), out _));
    }

    private static byte[] FixedIPv4Prefix(byte version)
    {
        var buffer = new byte[32];
        buffer[0] = version;
        buffer[1] = 4;
        buffer[2] = 127;
        buffer[3] = 0;
        buffer[4] = 0;
        buffer[5] = 1;
        buffer[7] = 119;
        return buffer;
    }

    private static byte[] LegacyWithExtraByte()
    {
        var prefix = FixedIPv4Prefix(1);
        var extra = new byte[prefix.Length + 1];
        prefix.CopyTo(extra, 0);
        extra[^1] = 1;
        return extra;
    }

    private static byte[] UnknownVersion() => FixedIPv4Prefix(99);

    private static byte[] Version2LengthMismatch()
    {
        var prefix = FixedIPv4Prefix(2);
        var raw = new byte[prefix.Length + 3];
        prefix.CopyTo(raw, 0);
        raw[32] = 0;
        raw[33] = 5;
        raw[34] = (byte)'x';
        return raw;
    }

    private static byte[] Version2InvalidUtf8()
    {
        var prefix = FixedIPv4Prefix(2);
        var raw = new byte[prefix.Length + 3];
        prefix.CopyTo(raw, 0);
        raw[32] = 0;
        raw[33] = 1;
        raw[34] = 0xFF;
        return raw;
    }

    private static byte[] Version2EmbeddedNul()
    {
        var prefix = FixedIPv4Prefix(2);
        var raw = new byte[prefix.Length + 5];
        prefix.CopyTo(raw, 0);
        raw[32] = 0;
        raw[33] = 3;
        raw[34] = (byte)'a';
        raw[35] = 0;
        raw[36] = (byte)'b';
        return raw;
    }

    [Fact]
    public void Protect_DoesNotReuseToken()
    {
        var protector = new AesGcmPostingTraceProtector(CurrentKey);
        var payload = new PostingTracePayload(IPAddress.Loopback, 119, DateTimeOffset.UnixEpoch, Guid.Empty);
        Assert.NotEqual(protector.Protect(payload), protector.Protect(payload));
    }

    [Fact]
    public void TamperedToken_IsRejected()
    {
        var protector = new AesGcmPostingTraceProtector(CurrentKey);
        var token = protector.Protect(new PostingTracePayload(IPAddress.Loopback, 119, DateTimeOffset.UnixEpoch, Guid.NewGuid()));
        var chars = token.ToCharArray();
        chars[^1] = chars[^1] == 'A' ? 'B' : 'A';
        Assert.False(protector.TryUnprotect(new string(chars), out _));
    }

    [Fact]
    public void PreviousKey_CanDecryptRotatedToken()
    {
        var oldProtector = new AesGcmPostingTraceProtector(PreviousKey);
        var token = oldProtector.Protect(new PostingTracePayload(IPAddress.Parse("192.0.2.55"), 563, DateTimeOffset.UnixEpoch, Guid.NewGuid()));
        var rotated = new AesGcmPostingTraceProtector(CurrentKey, PreviousKey);
        Assert.True(rotated.TryUnprotect(token, out var recovered));
        Assert.Equal(IPAddress.Parse("192.0.2.55"), recovered.Address);
        Assert.False(new AesGcmPostingTraceProtector(CurrentKey).TryUnprotect(token, out _));
    }

    [Fact]
    public void Protector_DoesNotAcceptLogger()
    {
        var ctors = typeof(AesGcmPostingTraceProtector).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.DoesNotContain(ctors, static ctor => ctor.GetParameters().Any(static p =>
            typeof(ILogger).IsAssignableFrom(p.ParameterType)));
    }
}
