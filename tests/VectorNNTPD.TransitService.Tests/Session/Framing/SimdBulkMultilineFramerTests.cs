using VectorNNTPD.TransitService.Session.Framing;
using Xunit;

namespace VectorNNTPD.TransitService.Tests.Session.Framing;

/// <summary>
/// Segment-boundary destuff coverage for production <see cref="NntpMultilineDataReader"/>.
/// </summary>
public sealed class SimdBulkMultilineFramerTests
{
    public static IEnumerable<object[]> AllFixtureNames()
    {
        foreach (var entry in InnArticleCorpus.LoadManifest().Files)
        {
            yield return [entry.RelativePath];
        }
    }

    public static IEnumerable<object[]> SegmentSizes()
    {
        foreach (var size in new[] { 1, 2, 4, 16, 4096 })
        {
            yield return [size];
        }
    }

    [Fact]
    public async Task EmptyArticle()
    {
        var result = await FramingPipe.ReadSegmentedAsync(FramingWireFactory.EmptyArticle, 1);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal(0, result.Payload.Length);
    }

    [Fact]
    public async Task FiveByteDelimiterIsDetected()
    {
        var result = await FramingPipe.ReadSegmentedAsync(FramingWireFactory.DataThenDelimiter(), 64);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal("data\r\n"u8.ToArray(), result.Payload.ToArray());
    }

    [Fact]
    public async Task HelloDotIsNotTerminator()
    {
        var wire = FramingWireFactory.WithTerminator("Hello.\r\nSomething else.\r\n");
        var result = await FramingPipe.ReadSegmentedAsync(wire, 8);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal("Hello.\r\nSomething else.\r\n"u8.ToArray(), result.Payload.ToArray());
    }

    [Fact]
    public async Task DotStuffedLineIsNotTerminator()
    {
        var wire = FramingWireFactory.WithTerminator("..foo\r\nbar\r\n");
        var result = await FramingPipe.ReadSegmentedAsync(wire, 3);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal(".foo\r\nbar\r\n"u8.ToArray(), result.Payload.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task DelimiterSplitAtEveryBoundary(int split)
    {
        var wire = FramingWireFactory.WithTerminator("split-term\r\n");
        var expected = FramingWireFactory.ExpectedDestuffed(wire);
        var holdBack = split == 0 ? 5 : Math.Max(1, 5 - split);
        var result = await FramingPipe.ReadSplitTerminatorAsync(wire, holdBack);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.AsSpan().SequenceEqual(result.Payload.Span));
    }

    [Theory]
    [MemberData(nameof(SegmentSizes))]
    public async Task Wire7_IdenticalAcrossSegmentSizes(int segmentSize)
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-7");
        var expected = FramingWireFactory.DestuffCompleteWire(wire);
        var result = await FramingPipe.ReadSegmentedAsync(wire, segmentSize);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.AsSpan().SequenceEqual(result.Payload.Span));
    }

    [Theory]
    [MemberData(nameof(AllFixtureNames))]
    public async Task EveryInnFixture_MatchesBulkSemantics(string relativePath)
    {
        var entry = InnArticleCorpus.LoadManifest().Files.Single(f => f.RelativePath == relativePath);
        var wire = InnArticleCorpus.ReadAllBytes(relativePath);
        var result = await FramingPipe.ReadOneAsync(wire);

        if (entry.FramingClassification == "complete_multiline_wire")
        {
            Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
            var expected = FramingWireFactory.DestuffCompleteWire(wire);
            Assert.True(expected.AsSpan().SequenceEqual(result.Payload.Span));
        }
        else
        {
            Assert.Equal(NntpMultilineReadStatus.Incomplete, result.Status);
            Assert.True(result.Payload.IsEmpty);
        }
    }

    [Fact]
    public async Task ManyDotsHostile_DoesNotFalseTerminate()
    {
        var wire = FramingWireFactory.ManyDotsHostileArticle();
        var expected = FramingWireFactory.ExpectedDestuffed(wire);
        Assert.True(expected.AsSpan().IndexOf("Hello.\r\n"u8) >= 0);

        var result = await FramingPipe.ReadSegmentedAsync(wire, 17);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.AsSpan().SequenceEqual(result.Payload.Span));
    }

    [Fact]
    public async Task IndexOfFiveByteDelimiter_RejectsTrailingPeriodLines()
    {
        var content = "Hello.\r\nSomething else.\r\n";
        var withoutTerm = await FramingPipe.ReadOneAsync(System.Text.Encoding.ASCII.GetBytes(content));
        Assert.Equal(NntpMultilineReadStatus.Incomplete, withoutTerm.Status);

        var withTerm = FramingWireFactory.WithTerminator(content);
        var complete = await FramingPipe.ReadOneAsync(withTerm);
        Assert.Equal(NntpMultilineReadStatus.Completed, complete.Status);
        Assert.Equal(content, System.Text.Encoding.ASCII.GetString(complete.Payload.Span));
    }

    [Fact]
    public async Task DelimiterSplitAcrossReads()
    {
        var wire = FramingWireFactory.WithTerminator("across-reads\r\n");
        var expected = FramingWireFactory.ExpectedDestuffed(wire);
        var result = await FramingPipe.ReadSplitTerminatorAsync(wire);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.AsSpan().SequenceEqual(result.Payload.Span));
    }

    [Fact]
    public async Task PipelinedSecondArticleRemains()
    {
        var wire = FramingWireFactory.PipelinedTwoArticles();
        var pipe = new System.IO.Pipelines.Pipe();
        await pipe.Writer.WriteAsync(wire);
        await pipe.Writer.CompleteAsync();
        var first = await NntpMultilineDataReader.ReadArticleAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        var second = await NntpMultilineDataReader.ReadArticleAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Completed, first.Status);
        Assert.Equal(NntpMultilineReadStatus.Completed, second.Status);
        Assert.Equal("article-one\r\n"u8.ToArray(), first.Payload.ToArray());
        Assert.Equal("article-two\r\n"u8.ToArray(), second.Payload.ToArray());
    }
}
