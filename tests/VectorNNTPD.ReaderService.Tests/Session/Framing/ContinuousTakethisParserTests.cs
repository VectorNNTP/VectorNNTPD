using System.Text;
using VectorNNTPD.ReaderService.Session.Framing;
using Xunit;

namespace VectorNNTPD.ReaderService.Tests.Session.Framing;

public sealed class ContinuousTakethisParserTests
{
    [Fact]
    public async Task ParsesMultipleTakethis_Contiguous()
    {
        var wire = FramingWireFactory.BuildFixedSizeTakethisStream(articleBytes: 128, articleCount: 5);
        var result = await ProductionTakethisStream.ParseAsync(wire);
        Assert.True(result.IsComplete);
        Assert.Equal(5, result.Articles);
        Assert.Equal(5 * 128, result.ArticleBytes);
    }

    [Fact]
    public async Task ParsesAcrossSmallSegments()
    {
        var wire = FramingWireFactory.BuildFixedSizeTakethisStream(256, 3);
        var result = await ProductionTakethisStream.ParseAsync(wire, chunkSize: 3);
        Assert.True(result.IsComplete);
        Assert.Equal(3, result.Articles);
    }

    [Fact]
    public async Task DotStuffedAndTrailingPeriod_NotTerminator()
    {
        var body = Encoding.ASCII.GetBytes("Hello.\r\n..foo\r\n...\r\n");
        using var ms = new MemoryStream();
        ms.Write("TAKETHIS <a@b>\r\n"u8);
        ms.Write(body);
        ms.Write(".\r\n"u8);
        ms.Write("TAKETHIS <c@d>\r\n"u8);
        ms.Write("x\r\n"u8);
        ms.Write(".\r\n"u8);
        var result = await ProductionTakethisStream.ParseAsync(ms.ToArray());
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Articles);
        var destuffedFirst = FramingWireFactory.DestuffPayload(body);
        Assert.Equal(destuffedFirst, result.Payloads[0].ToArray());
        Assert.Equal("x\r\n"u8.ToArray(), result.Payloads[1].ToArray());
        Assert.Equal(destuffedFirst.Length + result.Payloads[1].Length, result.ArticleBytes);
    }

    [Fact]
    public async Task CheckAndQuit_EscapeToControlPlane()
    {
        using var ms = new MemoryStream();
        ms.Write("CHECK <x@y>\r\n"u8);
        ms.Write("TAKETHIS <a@b>\r\n"u8);
        ms.Write("hi\r\n"u8);
        ms.Write(".\r\n"u8);
        ms.Write("QUIT\r\n"u8);
        var result = await ProductionTakethisStream.ParseAsync(ms.ToArray());
        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Articles);
        Assert.Equal(1, result.Checks);
        Assert.Equal(1, result.Quits);
    }

    [Fact]
    public async Task EmptyArticle()
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <e@e>\r\n.\r\n");
        var result = await ProductionTakethisStream.ParseAsync(wire);
        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Articles);
        Assert.Equal(0, result.ArticleBytes);
    }

    [Fact]
    public async Task IncompleteArticle_ReturnsIncomplete()
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <e@e>\r\nno-terminator");
        var result = await ProductionTakethisStream.ParseAsync(wire);
        Assert.False(result.IsComplete);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    public async Task ProductionReader_AgreesOnMultiArticleStream_AcrossChunkSizes(int chunkSize)
    {
        var wire = FramingWireFactory.BuildFixedSizeTakethisStream(512, 8);
        var result = await ProductionTakethisStream.ParseAsync(wire, chunkSize: chunkSize);
        Assert.True(result.IsComplete);
        Assert.Equal(8, result.Articles);
        Assert.Equal(8 * 512, result.ArticleBytes);

        var stuffed = FramingWireFactory.WithTerminator("Hello.\r\n..foo\r\nbar\r\n");
        var segmented = await FramingPipe.ReadSegmentedAsync(stuffed, chunkSize);
        Assert.Equal(NntpMultilineReadStatus.Completed, segmented.Status);
        Assert.Equal(FramingWireFactory.ExpectedDestuffed(stuffed), segmented.Payload.ToArray());
    }

    [Fact]
    public async Task InnWire7_SingleArticleStream()
    {
        var article = InnArticleCorpus.ReadAllBytes("wire-7");
        using var ms = new MemoryStream();
        ms.Write("TAKETHIS <wire-7@inn>\r\n"u8);
        ms.Write(article);
        var result = await ProductionTakethisStream.ParseAsync(ms.ToArray());
        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Articles);
        Assert.Equal(
            FramingWireFactory.DestuffCompleteWire(article).Length,
            result.ArticleBytes);
    }
}
