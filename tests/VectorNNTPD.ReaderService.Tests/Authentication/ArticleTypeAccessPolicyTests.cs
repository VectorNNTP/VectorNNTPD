using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.Authentication;

namespace VectorNNTPD.ReaderService.Tests.Authentication;

public sealed class ArticleTypeAccessPolicyTests
{
    [Fact]
    public void NullPolicy_IsUnrestricted()
    {
        Assert.True(ArticleTypeAccessPolicy.Allows(null, ArticleType.Binary | ArticleType.YEncoded));
    }

    [Fact]
    public void AllFlags_AreUnrestricted()
    {
        var policy = new NntpAccountPolicy("alice", 0, 0, 0, 0, "c", ArticleTypeCapabilities.All);
        Assert.True(ArticleTypeAccessPolicy.Allows(policy, ArticleType.Binary));
        Assert.True(ArticleTypeAccessPolicy.Allows(policy, ArticleType.None));
        Assert.Equal(65535u, ArticleTypeCapabilities.AllValue);
        Assert.Equal(ArticleTypeCapabilities.All, (ArticleType)ArticleTypeCapabilities.AllValue);
    }

    [Fact]
    public void TextOnly_RejectsBinary()
    {
        var policy = new NntpAccountPolicy(
            "alice",
            0,
            0,
            0,
            0,
            "c",
            ArticleTypeCapabilities.TextOnly);
        Assert.True(ArticleTypeAccessPolicy.Allows(policy, ArticleType.Default));
        Assert.False(ArticleTypeAccessPolicy.Allows(policy, ArticleType.Binary));
        Assert.False(ArticleTypeAccessPolicy.Allows(policy, ArticleType.Default | ArticleType.Html));
    }

    [Fact]
    public void FromRecord_CopiesCapability()
    {
        var record = new NntpUserRecord(
            "alice",
            "secret",
            allowAuthPlain: true,
            allowAuthScram256: false,
            ReadOnlyMemory<byte>.Empty,
            0,
            ReadOnlyMemory<byte>.Empty,
            ReadOnlyMemory<byte>.Empty,
            0,
            0,
            0,
            0,
            isEnabled: true,
            "cust",
            ArticleTypeCapabilities.TextOnly | ArticleType.Html);
        var policy = NntpAccountPolicy.FromRecord(record);
        Assert.Equal(record.AllowedArtTypes, policy.AllowedArtTypes);
        Assert.True(ArticleTypeAccessPolicy.Allows(policy, ArticleType.Default | ArticleType.Html));
        Assert.False(ArticleTypeAccessPolicy.Allows(policy, ArticleType.Binary));
    }
}
