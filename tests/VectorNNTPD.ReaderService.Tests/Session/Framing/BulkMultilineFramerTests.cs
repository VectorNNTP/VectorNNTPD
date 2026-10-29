using System.Text;
using VectorNNTPD.ReaderService.Session.Framing;
using Xunit;

namespace VectorNNTPD.ReaderService.Tests.Session.Framing;

/// <summary>
/// Correctness tests for production <see cref="NntpMultilineDataReader"/> destuff/terminator rules.
/// </summary>
public sealed class BulkMultilineFramerTests
{
    [Fact]
    public async Task EmptyArticle()
    {
        var (status, payload) = await ReadRecording(FramingWireFactory.EmptyArticle, segmentSize: 1);
        Assert.Equal(NntpMultilineReadStatus.Completed, status);
        Assert.Equal(0, payload.Length);
    }

    [Fact]
    public async Task FiveByteDelimiterIsDetected()
    {
        var (status, payload) = await ReadRecording(FramingWireFactory.DataThenDelimiter(), segmentSize: 64);
        Assert.Equal(NntpMultilineReadStatus.Completed, status);
        Assert.Equal("data\r\n", Encoding.ASCII.GetString(payload.Span));
    }

    [Fact]
    public async Task HelloDotIsNotTerminator()
    {
        var wire = FramingWireFactory.WithTerminator("Hello.\r\nSomething else.\r\n");
        var (status, payload) = await ReadRecording(wire, segmentSize: 8);
        Assert.Equal(NntpMultilineReadStatus.Completed, status);
        Assert.Equal("Hello.\r\nSomething else.\r\n", Encoding.ASCII.GetString(payload.Span));
    }

    [Fact]
    public async Task DotStuffedLineIsNotTerminator()
    {
        var wire = FramingWireFactory.WithTerminator("..foo\r\nbar\r\n");
        var (status, payload) = await ReadRecording(wire, segmentSize: 3);
        Assert.Equal(NntpMultilineReadStatus.Completed, status);
        Assert.Equal(".foo\r\nbar\r\n", Encoding.ASCII.GetString(payload.Span));
    }

    [Fact]
    public async Task TerminatorAtEndOfSegment()
    {
        var wire = FramingWireFactory.WithTerminator("abc\r\n");
        var result = await FramingPipe.ReadSegmentedAsync(wire, segmentSize: 5);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal("abc\r\n", Encoding.ASCII.GetString(result.Payload.Span));
    }

    [Fact]
    public async Task TerminatorSplitAcrossSegments()
    {
        var wire = FramingWireFactory.WithTerminator("split-term\r\n");
        var result = await FramingPipe.ReadSplitTerminatorAsync(wire, holdBackBytes: 2);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal("split-term\r\n", Encoding.ASCII.GetString(result.Payload.Span));
    }

    [Fact]
    public async Task TerminatorSplitAcrossReads()
    {
        var wire = FramingWireFactory.WithTerminator("across-reads\r\n");
        var result = await FramingPipe.ReadSplitTerminatorAsync(wire);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal(FramingWireFactory.ExpectedDestuffedLength(wire), result.Payload.Length);
    }

    [Fact]
    public async Task MultiSegmentArticle()
    {
        var wire = FramingWireFactory.SmallArticle();
        var expected = FramingWireFactory.ExpectedDestuffed(wire);
        var (status, payload) = await ReadRecording(wire, segmentSize: 17);
        Assert.Equal(NntpMultilineReadStatus.Completed, status);
        Assert.True(expected.AsSpan().SequenceEqual(payload.Span));
    }

    [Fact]
    public async Task PipelinedSecondArticleRemainsUnread()
    {
        var wire = FramingWireFactory.PipelinedTwoArticles();
        var pipe = new System.IO.Pipelines.Pipe();
        await pipe.Writer.WriteAsync(wire);
        await pipe.Writer.CompleteAsync();
        var first = await NntpMultilineDataReader.ReadArticleAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        var second = await NntpMultilineDataReader.ReadArticleAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Completed, first.Status);
        Assert.Equal(NntpMultilineReadStatus.Completed, second.Status);
        Assert.Equal("article-one\r\n", Encoding.ASCII.GetString(first.Payload.Span));
        Assert.Equal("article-two\r\n", Encoding.ASCII.GetString(second.Payload.Span));
    }

    [Fact]
    public async Task LargeArticle()
    {
        var wire = FramingWireFactory.Large768KiBArticle();
        var expectedLen = FramingWireFactory.ExpectedDestuffedLength(wire);
        var result = await FramingPipe.ReadSegmentedAsync(wire, segmentSize: 64);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal(expectedLen, result.Payload.Length);
    }

    [Fact]
    public async Task MultipleCRLFs()
    {
        var wire = FramingWireFactory.ManyCrlfArticle();
        var expected = FramingWireFactory.ExpectedDestuffed(wire);
        var (status, payload) = await ReadRecording(wire, segmentSize: 11);
        Assert.Equal(NntpMultilineReadStatus.Completed, status);
        Assert.True(expected.AsSpan().SequenceEqual(payload.Span));
    }

    [Fact]
    public async Task MultipleDotStuffedLines()
    {
        var wire = FramingWireFactory.ManyDotStuffedLinesArticle();
        var expected = FramingWireFactory.ExpectedDestuffed(wire);
        var (status, payload) = await ReadRecording(wire, segmentSize: 9);
        Assert.Equal(NntpMultilineReadStatus.Completed, status);
        Assert.Contains(".stuffed-line-0\r\n"u8, payload.Span);
        Assert.True(expected.AsSpan().SequenceEqual(payload.Span));
    }

    [Fact]
    public async Task TrailingPeriodsDoNotTerminate()
    {
        var wire = FramingWireFactory.TrailingPeriodLinesArticle();
        var expected = FramingWireFactory.ExpectedDestuffed(wire);
        var (status, payload) = await ReadRecording(wire, segmentSize: 13);
        Assert.Equal(NntpMultilineReadStatus.Completed, status);
        Assert.True(expected.AsSpan().SequenceEqual(payload.Span));
        Assert.EndsWith("period.\r\n", Encoding.ASCII.GetString(payload.Span));
    }

    [Fact]
    public async Task TerminatorNotDeliveredToSink()
    {
        var wire = FramingWireFactory.WithTerminator("keep-me\r\n");
        var (status, payload) = await ReadRecording(wire, segmentSize: 2);
        Assert.Equal(NntpMultilineReadStatus.Completed, status);
        Assert.False(payload.Span.EndsWith(".\r\n"u8));
        Assert.Equal("keep-me\r\n", Encoding.ASCII.GetString(payload.Span));
    }

    [Fact]
    public async Task PipeReader_ChunkedFeed_MatchesExpected()
    {
        var wire = FramingWireFactory.MediumArticle();
        var expected = FramingWireFactory.ExpectedDestuffedLength(wire);
        var result = await FramingPipe.ReadSegmentedAsync(wire, segmentSize: 32);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal(expected, result.Payload.Length);
    }

    private static async Task<(NntpMultilineReadStatus Status, ReadOnlyMemory<byte> Payload)> ReadRecording(
        byte[] wire,
        int segmentSize)
    {
        var result = await FramingPipe.ReadSegmentedAsync(wire, segmentSize);
        return (result.Status, result.Payload);
    }
}
