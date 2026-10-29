using System.Text;
using VectorNNTPD.ReaderService.Session.Commands.Posting;

namespace VectorNNTPD.ReaderService.Tests.Session;

public sealed class ApprovedHeaderParserTests
{
    [Theory]
    [InlineData("moderator@example.com", "moderator@example.com")]
    [InlineData(" Moderator@example.com ", "Moderator@example.com")]
    [InlineData("Moderator Name <moderator@example.com>", "moderator@example.com")]
    public void TryParseMailboxList_ExtractsIdentity(string value, string expected)
    {
        Assert.True(ApprovedHeaderParser.TryParseMailboxList(Encoding.ASCII.GetBytes(value), out var identities, out _));
        Assert.Equal(expected, Assert.Single(identities));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("")]
    [InlineData("not-a-mailbox")]
    [InlineData(",moderator@example.com")]
    public void TryParseMailboxList_RejectsMalformed(string value)
    {
        Assert.False(ApprovedHeaderParser.TryParseMailboxList(Encoding.ASCII.GetBytes(value), out _, out var failure));
        Assert.Equal(PostingFailureCategory.InvalidApproved, failure.Category);
    }

    [Fact]
    public void TryRead_NoApproved_ReturnsEmpty()
    {
        var article = Parse(Build());
        Assert.True(ApprovedHeaderParser.TryRead(article, out var identities, out _));
        Assert.Empty(identities);
    }

    [Fact]
    public void TryRead_HeaderNameIsCaseInsensitive()
    {
        var article = Parse(Build("aPpRoVeD: moderator@example.com\r\n"));
        Assert.True(ApprovedHeaderParser.TryRead(article, out var identities, out _));
        Assert.Equal("moderator@example.com", Assert.Single(identities));
    }

    [Fact]
    public void TryRead_IdenticalDuplicates_Collapse()
    {
        var article = Parse(Build("Approved: moderator@example.com\r\nApproved: Moderator@example.com\r\n"));
        Assert.True(ApprovedHeaderParser.TryRead(article, out var identities, out _));
        Assert.Equal("moderator@example.com", Assert.Single(identities));
    }

    [Fact]
    public void TryRead_ConflictingDuplicates_KeepBoth()
    {
        var article = Parse(Build("Approved: a@example.com\r\nApproved: b@example.com\r\n"));
        Assert.True(ApprovedHeaderParser.TryRead(article, out var identities, out _));
        Assert.Equal(["a@example.com", "b@example.com"], identities);
    }

    [Fact]
    public void TryRead_MailboxList_CollectsDistinctIdentities()
    {
        var article = Parse(Build("Approved: a@example.com, b@example.com\r\n"));
        Assert.True(ApprovedHeaderParser.TryRead(article, out var identities, out _));
        Assert.Equal(["a@example.com", "b@example.com"], identities);
    }

    [Fact]
    public void IdentitiesEqual_IsCaseInsensitive()
    {
        Assert.True(ApprovedHeaderParser.IdentitiesEqual("Moderator@Example.COM", "moderator@example.com"));
        Assert.False(ApprovedHeaderParser.IdentitiesEqual("a@example.com", "b@example.com"));
    }

    private static ParsedPostArticle Parse(string article)
    {
        Assert.True(PostHeaderParser.TryParse(Encoding.ASCII.GetBytes(article), out var parsed, out var failure), failure.Detail);
        Assert.NotNull(parsed);
        return parsed!;
    }

    private static string Build(string extraHeaders = "") =>
        "Date: 25 Sep 2026 12:00:00 +0000\r\n" +
        "From: poster@example.com\r\n" +
        "Newsgroups: misc.test\r\n" +
        "Subject: test\r\n" +
        "Message-ID: <ok@example.com>\r\n" +
        extraHeaders +
        "\r\n" +
        "body\r\n";
}
