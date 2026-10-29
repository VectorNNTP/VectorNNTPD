using System.Security.Cryptography;
using System.Text;
using VectorNNTPD.ReaderService.PostFilter;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

public sealed class PostFilterAccountIdentityTests
{
    [Fact]
    public void FromUsername_IsLowercaseMd5HexOfUtf8Bytes()
    {
        var expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("poster"))).ToLowerInvariant();
        Assert.Equal(32, expected.Length);
        Assert.Equal(expected, PostFilterAccountIdentity.FromUsername("poster"));
        Assert.True(PostFilterAccountIdentity.IsStoredIdentifier(expected));
    }

    [Fact]
    public void FromUsername_DoesNotTrim()
    {
        Assert.NotEqual(
            PostFilterAccountIdentity.FromUsername("poster"),
            PostFilterAccountIdentity.FromUsername(" poster"));
    }

    [Fact]
    public void FromUsername_IsOrdinalCaseSensitive()
    {
        Assert.NotEqual(
            PostFilterAccountIdentity.FromUsername("Poster"),
            PostFilterAccountIdentity.FromUsername("poster"));
    }

    [Fact]
    public void FromPolicyEntry_TrimsThenHashes()
    {
        Assert.Equal(
            PostFilterAccountIdentity.FromUsername("poster"),
            PostFilterAccountIdentity.FromPolicyEntry("  poster  "));
    }

    [Fact]
    public void FromPolicyEntry_KeepsStoredIdentifier()
    {
        var stored = PostFilterAccountIdentity.FromUsername("poster");
        Assert.Equal(stored, PostFilterAccountIdentity.FromPolicyEntry(stored));
    }
}
