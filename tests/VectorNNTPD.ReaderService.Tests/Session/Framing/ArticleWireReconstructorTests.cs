using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VectorNNTPD.ReaderService.Session.Framing;
using Xunit;

namespace VectorNNTPD.ReaderService.Tests.Session.Framing;

public sealed class ArticleWireReconstructorTests
{
    [Fact]
    public void Restuff_NormalLine_Unchanged()
    {
        var stored = "hello\r\n"u8.ToArray();
        var wire = ArticleWireReconstructor.RestuffArticle(stored);
        Assert.Equal("hello\r\n.\r\n"u8.ToArray(), wire);
    }

    [Fact]
    public void Restuff_LeadingDot_GetsStuffed()
    {
        var stored = ".example\r\n"u8.ToArray();
        var wire = ArticleWireReconstructor.RestuffArticle(stored);
        Assert.Equal("..example\r\n.\r\n"u8.ToArray(), wire);
    }

    [Fact]
    public void Restuff_MultipleLeadingDots()
    {
        var stored = "..example\r\n"u8.ToArray();
        var wire = ArticleWireReconstructor.RestuffArticle(stored);
        Assert.Equal("...example\r\n.\r\n"u8.ToArray(), wire);
    }

    [Fact]
    public async Task Restuff_LoneDotContentLine_DoesNotTerminateEarly()
    {
        var stored = ".\r\n"u8.ToArray();
        var wire = ArticleWireReconstructor.RestuffArticle(stored);
        Assert.Equal("..\r\n.\r\n"u8.ToArray(), wire);

        var reader = PipeReader.Create(new ReadOnlySequence<byte>(wire));
        var result = await NntpMultilineDataReader
            .ReadArticleAsync(reader, 1024, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal(".\r\n"u8.ToArray(), result.Payload.ToArray());
    }

    [Fact]
    public void Restuff_EmptyArticle()
    {
        var wire = ArticleWireReconstructor.RestuffArticle(ReadOnlySpan<byte>.Empty);
        Assert.Equal(".\r\n"u8.ToArray(), wire);
    }

    [Fact]
    public void Restuff_WithoutTerminator_MatchesIngestQueueContract()
    {
        var stored = "Subject: t\r\n\r\n.hidden\r\n"u8.ToArray();
        var wire = ArticleWireReconstructor.RestuffArticle(stored, includeTerminator: false);
        Assert.Equal("Subject: t\r\n\r\n..hidden\r\n"u8.ToArray(), wire);
        Assert.False(wire.AsSpan().EndsWith("\r\n.\r\n"u8));
    }

    [Fact]
    public async Task RoundTrip_DestuffThenRestuff_MatchesOriginalWire()
    {
        var originalWire = Encoding.ASCII.GetBytes(
            "Subject: t\r\n\r\n..leading\r\n...three\r\nnormal\r\n.\r\n");
        var reader = PipeReader.Create(new ReadOnlySequence<byte>(originalWire));
        var destuffed = await NntpMultilineDataReader
            .ReadArticleAsync(reader, 64 * 1024, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Completed, destuffed.Status);
        var reconstructed = ArticleWireReconstructor.RestuffArticle(destuffed.Payload.Span);
        Assert.Equal(originalWire, reconstructed);
    }

    [Fact]
    public void BuildTakeThisTransaction_PrefixesCommand()
    {
        var stored = "body\r\n"u8.ToArray();
        var txn = FramingWireFactory.BuildTakeThisTransaction("<a@b>", stored);
        Assert.StartsWith("TAKETHIS <a@b>\r\n", Encoding.ASCII.GetString(txn));
        Assert.EndsWith(".\r\n", Encoding.ASCII.GetString(txn));
    }

    [Fact]
    public async Task ContinuousParser_DoesNotTreatCommandsInsideArticleAsControl()
    {
        var stored = Encoding.ASCII.GetBytes("QUIT\r\nCHECK x\r\nTAKETHIS y\r\n");
        var txn = FramingWireFactory.BuildTakeThisTransaction("<in@body>", stored);
        var next = FramingWireFactory.BuildTakeThisTransaction("<next@id>", "x\r\n"u8);
        var stream = txn.Concat(next).ToArray();
        var result = await ProductionTakethisStream.ParseAsync(stream);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Articles);
        Assert.Equal(0, result.Quits);
        Assert.Equal(0, result.Checks);
        Assert.Equal(stored, result.Payloads[0].ToArray());
    }

    [Fact]
    public void Restuff_IsDeterministic_AndMatchesByteEstimate()
    {
        var stored = Encoding.ASCII.GetBytes(".a\r\nbb\r\n..c\r\n");
        var first = ArticleWireReconstructor.RestuffArticle(stored);
        var second = ArticleWireReconstructor.RestuffArticle(stored);
        Assert.Equal(first, second);
        Assert.Equal(first.Length, ArticleWireReconstructor.EstimateRestuffedWireBytes(stored));
    }
}

public sealed class MessageIdDigestTests
{
    [Fact]
    public void Blake3_WithAngleBrackets_MatchesKnownCorpusFilename()
    {
        const string mid = "<3810724$5fcf702$2a675b8@82ecaa5ff0.e67ff>";
        const string expected = "00024a9fbd6824b3529c34dabdb6af28505b5be684936bdca64279279736e7ad";
        Assert.Equal(expected, MessageIdDigest.ComputeHexLower(mid));
        Assert.Equal(
            Path.Join("00", "02", expected),
            MessageIdDigest.BuildRelativePath(expected));
    }

    [Fact]
    public void WithoutAngleBrackets_DoesNotMatch()
    {
        const string mid = "<3810724$5fcf702$2a675b8@82ecaa5ff0.e67ff>";
        const string expected = "00024a9fbd6824b3529c34dabdb6af28505b5be684936bdca64279279736e7ad";
        Assert.NotEqual(expected, MessageIdDigest.ComputeHexLower(mid.Trim('<', '>')));
    }

    [Fact]
    public void ExtractMessageId_AcceptsBareLfAfterPath()
    {
        var stored = Encoding.ASCII.GetBytes(
            "Path: host!not-for-mail\n" +
            "Message-ID: <barelf@test.local>\r\n" +
            "Subject: x\r\n" +
            "\r\n" +
            "body\r\n");
        Assert.True(StoredArticleReader.TryExtractMessageId(stored, out var mid));
        Assert.Equal("<barelf@test.local>", mid);
    }
}
