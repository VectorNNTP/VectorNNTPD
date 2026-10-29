using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.PostFilter;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

public sealed class SpamdScanArticleBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Fqdn = "nntpd01.usenet.ninja";

    [Fact]
    public void Build_AddsSyntheticHeaders_StripsNntpHeaders_PreservesBodyAndArticle()
    {
        var original = Encoding.ASCII.GetBytes(
            "Path: news.example!not-for-mail\r\n" +
            "From: poster@example.com\r\n" +
            "Newsgroups: misc.test,alt.test\r\n" +
            "Subject: hello\r\n" +
            "Message-ID: <scan-builder@example.com>\r\n" +
            "Date: 1 Jan 2026 00:00:00 +0000\r\n" +
            "Xref: nntpd01.usenet.ninja misc.test:1\r\n" +
            "Injection-Info: nntpd01.usenet.ninja; posting-account=poster\r\n" +
            "X-Trace: secret-trace\r\n" +
            "X-Complaints-To: abuse@example.com\r\n" +
            "NNTP-Posting-Host: 203.0.113.9\r\n" +
            "\r\n" +
            "body-bytes\r\n");
        var copy = original.ToArray();
        var article = new ArticleRecord(
            ArticleId.FromMessageId("<scan-builder@example.com>"u8),
            artHash: 42,
            artType: ArticleType.Default,
            artLines: 1,
            canonicalUtc: Now.UtcDateTime,
            parseStatus: ArticleParseStatus.CanonicalV1,
            artData: original,
            fields: default);
        var context = new SpamdScanContext(IPAddress.Parse("203.0.113.8"), Fqdn, Now);

        var scan = SpamdScanArticleBuilder.Build(article, context);
        var text = Encoding.ASCII.GetString(scan);

        Assert.StartsWith("Received: from [203.0.113.8] by nntpd01.usenet.ninja with NNTP id <scan-builder@example.com>; Thu, 01 Jan 2026 00:00:00 +0000\r\n", text, StringComparison.Ordinal);
        Assert.Contains("To: usenet@nntpd01.usenet.ninja\r\n", text, StringComparison.Ordinal);
        Assert.Contains("X-Usenet-Newsgroups: misc.test,alt.test\r\n", text, StringComparison.Ordinal);
        Assert.Contains("From: poster@example.com\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Newsgroups: misc.test,alt.test\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Subject: hello\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Message-ID: <scan-builder@example.com>\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Date: 1 Jan 2026 00:00:00 +0000\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Path:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Xref:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Injection-Info:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Trace:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Complaints-To:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NNTP-Posting-Host:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Received-By:", text, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\nbody-bytes\r\n", text, StringComparison.Ordinal);
        Assert.True(original.AsSpan().SequenceEqual(article.ArtData.Span));
        Assert.True(copy.AsSpan().SequenceEqual(article.ArtData.Span));
        Assert.Equal(42UL, article.ArtHash);
        Assert.Equal(ArticleType.Default, article.ArtType);
        Assert.Equal(1, article.ArtLines);
        Assert.Equal(copy.Length, article.ArtSize);
        Assert.False(ReferenceEquals(scan, original));
        Assert.True(MemoryMarshal.TryGetArray(article.ArtData, out var segment));
        Assert.Same(original, segment.Array);
    }

    [Fact]
    public void Build_AddsDateWhenOriginalDateAbsent()
    {
        var original = "From: a@b\r\nNewsgroups: misc.test\r\nSubject: t\r\nMessage-ID: <nodate@example.com>\r\n\r\nplain\r\n"u8.ToArray();
        var article = new ArticleRecord(
            ArticleId.FromMessageId("<nodate@example.com>"u8),
            artHash: 2,
            artType: ArticleType.Default,
            artLines: 1,
            canonicalUtc: Now.UtcDateTime,
            parseStatus: ArticleParseStatus.CanonicalV1,
            artData: original,
            fields: default);
        var scan = Encoding.ASCII.GetString(
            SpamdScanArticleBuilder.Build(
                article,
                new SpamdScanContext(IPAddress.Loopback, Fqdn, Now)));
        Assert.Contains("Date: Thu, 01 Jan 2026 00:00:00 +0000\r\n", scan, StringComparison.Ordinal);
        Assert.True(original.AsSpan().SequenceEqual(article.ArtData.Span));
    }

    [Fact]
    public void Build_MessageIdRemainsValid_AndScanIsIndependent()
    {
        var original = "From: a@b\r\nNewsgroups: misc.test\r\nSubject: t\r\nMessage-ID: <keep@example.com>\r\nDate: 1 Jan 2026 00:00:00 +0000\r\n\r\nxyz\r\n"u8.ToArray();
        var article = new ArticleRecord(
            ArticleId.FromMessageId("<keep@example.com>"u8),
            artHash: 3,
            artType: ArticleType.Default,
            artLines: 1,
            canonicalUtc: Now.UtcDateTime,
            parseStatus: ArticleParseStatus.CanonicalV1,
            artData: original,
            fields: default);
        var first = SpamdScanArticleBuilder.Build(article, new SpamdScanContext(IPAddress.Loopback, Fqdn, Now));
        first[0] = (byte)'X';
        var second = SpamdScanArticleBuilder.Build(article, new SpamdScanContext(IPAddress.Loopback, Fqdn, Now));
        Assert.Equal((byte)'R', second[0]);
        Assert.Contains("Message-ID: <keep@example.com>\r\n", Encoding.ASCII.GetString(second), StringComparison.Ordinal);
        Assert.Equal((byte)'F', article.ArtData.Span[0]);
    }
}
