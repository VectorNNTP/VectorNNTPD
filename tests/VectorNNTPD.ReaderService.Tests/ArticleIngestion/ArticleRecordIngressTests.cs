using System.Net;
using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.ArticleIngestion;

public sealed class ArticleRecordIngressTests
{
    private static readonly NntpArticleParser Parser = new("nntpd01.usenet.ninja");

    [Fact]
    public void TryCreateFromDestuffed_ValidArticle_ReturnsCanonicalRecord()
    {
        var destuffed = Encoding.ASCII.GetBytes(CanonicalArticleText.Destuffed("<id@example.test>", "one\r\ntwo\r\n"));
        var created = ArticleRecordIngress.TryCreateFromDestuffed(Parser, destuffed);
        Assert.True(created.IsAccepted);
        var record = created.Record;
        Assert.Equal(ArticleParseStatus.CanonicalV1, record.ParseStatus);
        Assert.Equal(record.ArtData.Length, record.ArtSize);
        Assert.Equal(2, record.ArtLines);
        Assert.NotEqual(ArticleType.None, record.ArtType);
        Assert.True(record.MessageId.SequenceEqual("<id@example.test>"u8));
        Assert.True(record.Fields.MessageId.IsPresent);
        Assert.True(record.Fields.Newsgroups.IsPresent);
        Assert.True(record.ArtData.Span.Slice(record.Fields.MessageId.Offset, record.Fields.MessageId.Length)
            .SequenceEqual("<id@example.test>"u8));
    }

    [Fact]
    public void TryCreateFromStuffedWire_DestuffsOnce_ThenFactory()
    {
        var destuffed = CanonicalArticleText.Destuffed("<dot@example.test>", ".hidden\r\n");
        var stuffed = destuffed.Replace(".hidden\r\n", "..hidden\r\n", StringComparison.Ordinal);
        var created = ArticleRecordIngress.TryCreateFromStuffedWire(
            Parser,
            Encoding.ASCII.GetBytes(stuffed),
            64 * 1024);
        Assert.True(created.IsAccepted);
        var text = Encoding.ASCII.GetString(created.Record.ArtData.Span);
        Assert.Contains("\r\n\r\n..hidden\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n\r\n.hidden\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreateFromDestuffed_MissingDate_DoesNotProduceRecord()
    {
        var destuffed = "Message-ID: <bad@example.test>\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\n\r\nbody\r\n"u8.ToArray();
        var created = ArticleRecordIngress.TryCreateFromDestuffed(Parser, destuffed);
        Assert.False(created.IsAccepted);
        Assert.Equal(NntpArticleParseFailureCode.MissingOrInvalidDate, created.ParseFailure);
        Assert.Equal(ArticleParseStatus.None, created.Record.ParseStatus);
        Assert.Equal(0, created.Record.ArtSize);
    }

    [Fact]
    public void CreateQueued_PayloadAliasesArtData()
    {
        var destuffed = Encoding.ASCII.GetBytes(CanonicalArticleText.Destuffed("<alias@example.test>"));
        var created = ArticleRecordIngress.TryCreateFromDestuffed(Parser, destuffed);
        Assert.True(created.IsAccepted);
        var inbound = ArticleRecordIngress.CreateQueued(
            "<alias@example.test>",
            created.Record,
            ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)),
            DateTimeOffset.UtcNow,
            InboundArticleProducer.TakeThis);
        Assert.True(inbound.Payload.Equals(created.Record.ArtData));
        Assert.Equal(ArticleParseStatus.CanonicalV1, inbound.Record.ParseStatus);
        Assert.Equal(InboundArticleProducer.TakeThis, inbound.Producer);
    }

    [Fact]
    public void CreateQueued_IHave_IsCanonicalV1()
    {
        var destuffed = Encoding.ASCII.GetBytes(CanonicalArticleText.Destuffed("<ihave@example.test>"));
        var created = ArticleRecordIngress.TryCreateFromDestuffed(Parser, destuffed);
        Assert.True(created.IsAccepted);
        var inbound = ArticleRecordIngress.CreateQueued(
            "<ihave@example.test>",
            created.Record,
            ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)),
            DateTimeOffset.UtcNow,
            InboundArticleProducer.IHave);
        Assert.Equal(InboundArticleProducer.IHave, inbound.Producer);
        Assert.Equal(ArticleParseStatus.CanonicalV1, inbound.Record.ParseStatus);
        Assert.True(inbound.Payload.Equals(created.Record.ArtData));
    }

    [Fact]
    public void CreateQueued_RejectsNonCanonicalRecord()
    {
        var destuffed = Encoding.ASCII.GetBytes(CanonicalArticleText.Destuffed("<alias@example.test>"));
        var created = ArticleRecordIngress.TryCreateFromDestuffed(Parser, destuffed);
        Assert.True(created.IsAccepted);
        var invalid = new ArticleRecord(
            created.Record.ArtId,
            created.Record.ArtHash,
            created.Record.ArtType,
            created.Record.ArtLines,
            created.Record.CanonicalUtc,
            ArticleParseStatus.None,
            created.Record.ArtData.ToArray(),
            created.Record.Fields);
        Assert.Throws<ArgumentException>(() =>
            ArticleRecordIngress.CreateQueued(
                "<alias@example.test>",
                invalid,
                ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)),
                DateTimeOffset.UtcNow,
                InboundArticleProducer.Post));
    }
}
