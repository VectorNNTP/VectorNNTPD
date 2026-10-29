using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Tests.ArticleIngestion;

public sealed class ArticleTypeClassifierTests
{
    [Fact]
    public void PlainText_IsDefault()
    {
        var type = ArticleTypeClassifier.Classify("From: a@b\r\nSubject: hi\r\n"u8, "hello\r\n"u8);
        Assert.Equal(ArticleType.Default, type);
    }

    [Fact]
    public void YbeginLine_IsYEncodedAndBinary()
    {
        var type = ArticleTypeClassifier.Classify(
            "Subject: part\r\n"u8,
            "=ybegin line=128 size=12 name=a.bin\r\n"u8);
        Assert.True(type.HasFlag(ArticleType.YEncoded));
        Assert.True(type.HasFlag(ArticleType.Binary));
    }

    [Fact]
    public void YbeginPart_IsYEncodedPartial()
    {
        var type = ArticleTypeClassifier.Classify(
            default,
            "=ybegin part=1 line=128 size=99 name=a.bin\r\n"u8);
        Assert.True(type.HasFlag(ArticleType.YEncoded));
        Assert.True(type.HasFlag(ArticleType.Partial));
    }

    [Fact]
    public void MimeAndBase64Headers_ClassifyWithoutBodyScan()
    {
        var type = ArticleTypeClassifier.Classify(
            "Mime-Version: 1.0\r\nContent-Type: text/plain\r\nContent-Transfer-Encoding: base64\r\n"u8,
            default);
        Assert.True(type.HasFlag(ArticleType.Mime));
        Assert.True(type.HasFlag(ArticleType.Base64));
        Assert.True(type.HasFlag(ArticleType.Binary));
    }

    [Fact]
    public void UuencodeBegin_IsUuEncode()
    {
        var type = ArticleTypeClassifier.Classify(default, "begin 644 file.dat\r\n"u8);
        Assert.True(type.HasFlag(ArticleType.UuEncode));
        Assert.True(type.HasFlag(ArticleType.Binary));
    }

    [Fact]
    public void ContentLength_ParsesDestuffedHint()
    {
        Assert.Equal(12, ArticleTypeClassifier.TryParseContentLength("Content-Length: 12"u8));
        Assert.Equal(-1, ArticleTypeClassifier.TryParseContentLength("Subject: 12"u8));
    }

    [Fact]
    public void YencSize_IsDecodedSizeNotWire()
    {
        Assert.Equal(12345, ArticleTypeClassifier.TryParseYencSize("=ybegin line=128 size=12345 name=a.bin"u8));
        Assert.Equal(-1, ArticleTypeClassifier.TryParseYencSize("=ybegin line=128 name=a.bin"u8));
    }

    [Fact]
    public void YEncoded_MatchesTransitYencBit()
    {
        Assert.Equal((int)VectorNNTPD.ReaderService.Configuration.TransitMessageTypes.Yenc, (int)ArticleType.YEncoded);
    }

    [Fact]
    public void CommonArticleTypeBits_MatchTransitMessageTypes()
    {
        Assert.Equal((int)TransitMessageTypes.None, (int)ArticleType.None);
        Assert.Equal((int)TransitMessageTypes.Default, (int)ArticleType.Default);
        Assert.Equal((int)TransitMessageTypes.Control, (int)ArticleType.Control);
        Assert.Equal((int)TransitMessageTypes.Cancel, (int)ArticleType.Cancel);
        Assert.Equal((int)TransitMessageTypes.Mime, (int)ArticleType.Mime);
        Assert.Equal((int)TransitMessageTypes.Binary, (int)ArticleType.Binary);
        Assert.Equal((int)TransitMessageTypes.UuEncode, (int)ArticleType.UuEncode);
        Assert.Equal((int)TransitMessageTypes.Base64, (int)ArticleType.Base64);
        Assert.Equal((int)TransitMessageTypes.Yenc, (int)ArticleType.YEncoded);
        Assert.Equal((int)TransitMessageTypes.BommaNews, (int)ArticleType.BommaNews);
        Assert.Equal((int)TransitMessageTypes.UniData, (int)ArticleType.UniData);
        Assert.Equal((int)TransitMessageTypes.Multipart, (int)ArticleType.Multipart);
        Assert.Equal((int)TransitMessageTypes.Html, (int)ArticleType.Html);
        Assert.Equal((int)TransitMessageTypes.PostScript, (int)ArticleType.PostScript);
        Assert.Equal((int)TransitMessageTypes.BinHex, (int)ArticleType.BinHex);
        Assert.Equal((int)TransitMessageTypes.Partial, (int)ArticleType.Partial);
        Assert.Equal((int)TransitMessageTypes.PgpMessage, (int)ArticleType.PgpMessage);
    }

    [Fact]
    public void IhaveDestuff_UsesCommonClassifier()
    {
        var article = IhaveArticleInterpreter.DestuffToArticle(
            "Control: cancel <m@example.test>\r\n\r\nbody\r\n"u8,
            64 * 1024);
        Assert.True(article.Type.HasFlag(ArticleType.Control));
        Assert.True(article.Type.HasFlag(ArticleType.Cancel));
    }

    [Fact]
    public void ArticleSize_IsDestuffedCompleteArticle()
    {
        var headers = Encoding.ASCII.GetBytes("Subject: hi\r\n");
        var body = Encoding.ASCII.GetBytes("body\r\n");
        var article = new Article(headers, body, ArticleType.Default);
        Assert.Equal(headers.Length + 2 + body.Length, article.Size);
        Assert.Equal("Subject: hi\r\n\r\nbody\r\n", Encoding.ASCII.GetString(article.Payload.Span));
    }
}
