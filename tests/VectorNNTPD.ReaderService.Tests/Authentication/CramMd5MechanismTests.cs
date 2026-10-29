using System.Text;
using VectorNNTPD.ReaderService.Authentication.Sasl;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.Authentication;

public sealed class CramMd5MechanismTests
{
    [Fact]
    public void CreateChallenge_IsRfc2195MsgIdWithNntpdFqdn()
    {
        var fqdn = NntpdOptions.FormatFqdn(1, "usenet.ninja");
        var clock = new ControllableTimeProvider();
        clock.Advance(TimeSpan.FromSeconds(1_700_000_000));
        var challenge = CramMd5Mechanism.CreateChallenge(fqdn, clock);

        Assert.True(CramMd5Mechanism.IsRfc2195Challenge(challenge));
        Assert.StartsWith("<", challenge, StringComparison.Ordinal);
        Assert.True(challenge.Contains(".1700000000@", StringComparison.Ordinal));
        Assert.EndsWith("@nntpd01.usenet.ninja>", challenge, StringComparison.Ordinal);
        Assert.Equal(-1, challenge.IndexOf(' '));
    }

    [Fact]
    public void CreateChallenge_IsUniqueBetweenCalls()
    {
        var fqdn = NntpdOptions.FormatFqdn(7, "usenet.ninja");
        var first = CramMd5Mechanism.CreateChallenge(fqdn);
        var second = CramMd5Mechanism.CreateChallenge(fqdn);
        Assert.NotEqual(first, second);
        Assert.EndsWith("@nntpd07.usenet.ninja>", first, StringComparison.Ordinal);
        Assert.EndsWith("@nntpd07.usenet.ninja>", second, StringComparison.Ordinal);
    }

    [Fact]
    public void HmacInput_IsDecodedNativeChallenge_NotBase64WireToken()
    {
        const string password = "tanstaaftanstaaf";
        var native = CramMd5Mechanism.CreateChallenge(NntpdOptions.FormatFqdn(1, "usenet.ninja"));
        var wire = Convert.ToBase64String(Encoding.ASCII.GetBytes(native));
        var decoded = Encoding.ASCII.GetString(Convert.FromBase64String(wire));
        Assert.Equal(native, decoded);

        var secret = CramMd5Mechanism.SecretFromPassword(password);
        var digest = CramMd5Mechanism.ComputeHexDigest(secret, native);
        Assert.Equal(digest, CramMd5Mechanism.ComputeHexDigest(secret, decoded));
        Assert.NotEqual(digest, CramMd5Mechanism.ComputeHexDigest(secret, wire));
        Assert.True(CramMd5Mechanism.Verify("tim", "tim " + digest, native, secret));
        Assert.False(CramMd5Mechanism.Verify("tim", "tim " + digest, wire, secret));
    }

    [Fact]
    public void Rfc2195Example_ProducesPublishedDigest()
    {
        const string native = "<1896.697170952@postoffice.reston.mci.net>";
        const string secret = "tanstaaftanstaaf";
        var digest = CramMd5Mechanism.ComputeHexDigest(Encoding.ASCII.GetBytes(secret), native);
        Assert.Equal("b913a602c7eda7a495b4e6e7334d3890", digest);
        Assert.True(
            CramMd5Mechanism.Verify(
                "tim",
                "tim b913a602c7eda7a495b4e6e7334d3890",
                native,
                Encoding.ASCII.GetBytes(secret)));
    }

    [Fact]
    public void CreateChallenge_RejectsInvalidFqdn()
    {
        Assert.Throws<ArgumentException>(() => CramMd5Mechanism.CreateChallenge("host@evil"));
        Assert.Throws<ArgumentException>(() => CramMd5Mechanism.CreateChallenge(" "));
    }
}
