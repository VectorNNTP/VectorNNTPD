using System.IO.Pipelines;
using VectorNNTPD.ReaderService.Session.Framing;
using Xunit;

namespace VectorNNTPD.ReaderService.Tests.Session.Framing;

/// <summary>
/// Correctness coverage for the INN <c>tests/data/articles</c> corpus against production
/// <see cref="NntpMultilineDataReader"/> destuff/terminator rules.
/// </summary>
public sealed class InnArticleCorpusFramerTests
{
    public static IEnumerable<object[]> AllFixtureNames()
    {
        foreach (var entry in InnArticleCorpus.LoadManifest().Files)
        {
            yield return [entry.RelativePath];
        }
    }

    public static IEnumerable<object[]> CompleteWireFixtureNames()
    {
        foreach (var entry in InnArticleCorpus.CompleteWireArticles())
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
    public void Manifest_MatchesPinnedCommit_AndFixtureCount()
    {
        var manifest = InnArticleCorpus.LoadManifest();
        Assert.Equal(InnArticleCorpus.UpstreamCommit, manifest.UpstreamCommit);
        Assert.Equal("InterNetNews/inn", manifest.Repository);
        Assert.Equal("tests/data/articles", manifest.SourcePath);
        Assert.Equal(25, manifest.FileCount);
        Assert.Equal(25, manifest.Files.Count);
        Assert.Equal(19544, manifest.TotalBytes);
    }

    [Theory]
    [MemberData(nameof(AllFixtureNames))]
    public void EveryFixture_Sha256MatchesManifest(string relativePath)
    {
        var entry = InnArticleCorpus.LoadManifest().Files.Single(f => f.RelativePath == relativePath);
        InnArticleCorpus.VerifySha256(entry);
    }

    [Theory]
    [MemberData(nameof(AllFixtureNames))]
    public async Task EveryFixture_BulkFramer_StatusMatchesClassification(string relativePath)
    {
        var entry = InnArticleCorpus.LoadManifest().Files.Single(f => f.RelativePath == relativePath);
        var wire = InnArticleCorpus.ReadAllBytes(relativePath);
        var result = await FramingPipe.ReadSegmentedAsync(wire, segmentSize: 7);

        if (entry.FramingClassification == "complete_multiline_wire")
        {
            Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
            var expected = FramingWireFactory.DestuffCompleteWire(wire);
            Assert.True(expected.AsSpan().SequenceEqual(result.Payload.Span));
            Assert.Equal(expected.Length, result.Payload.Length);
            Assert.NotEqual(wire.Length, result.Payload.Length);
        }
        else
        {
            Assert.Equal(NntpMultilineReadStatus.Incomplete, result.Status);
            // Production does not emit an incomplete payload (retired bulk prototype forwarded the buffer).
            Assert.True(result.Payload.IsEmpty);
            Assert.Equal(wire, InnArticleCorpus.ReadAllBytes(relativePath));
        }
    }

    [Theory]
    [MemberData(nameof(CompleteWireFixtureNames))]
    public async Task CompleteWire_PreservesDotStuffing_AndExcludesTerminator(string relativePath)
    {
        var wire = InnArticleCorpus.ReadAllBytes(relativePath);
        Assert.True(InnArticleCorpus.ContainsFiveByteDelimiter(wire));
        var stuffedWithoutTerminator = wire.AsSpan()[..^3];
        Assert.Contains("..\r\n"u8, stuffedWithoutTerminator);

        var expected = FramingWireFactory.DestuffCompleteWire(wire);
        Assert.True(expected.AsSpan().StartsWith("."u8));
        Assert.Contains("..\r\n"u8, expected);
        Assert.NotEqual(wire.Length, expected.Length);
        Assert.False(wire.AsSpan()[..^3].SequenceEqual(expected));

        var result = await FramingPipe.ReadSegmentedAsync(wire, 3);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.AsSpan().SequenceEqual(result.Payload.Span));
        Assert.True(result.Payload.Span.StartsWith("."u8));
    }

    [Theory]
    [MemberData(nameof(SegmentSizes))]
    public async Task Wire7_IdenticalPayload_AcrossSegmentSizes(int segmentSize)
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-7");
        var expected = FramingWireFactory.DestuffCompleteWire(wire);
        var result = await FramingPipe.ReadSegmentedAsync(wire, segmentSize);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.AsSpan().SequenceEqual(result.Payload.Span));
    }

    [Fact]
    public async Task Wire7_FiveByteDelimiter_SplitAtEveryBoundary()
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-7");
        var expected = FramingWireFactory.DestuffCompleteWire(wire);
        Assert.True(wire.AsSpan().EndsWith("\r\n.\r\n"u8));

        var whole = await FramingPipe.ReadOneAsync(wire);
        Assert.Equal(NntpMultilineReadStatus.Completed, whole.Status);
        Assert.True(expected.AsSpan().SequenceEqual(whole.Payload.Span));

        for (var holdBack = 1; holdBack <= 5; holdBack++)
        {
            var result = await FramingPipe.ReadSplitTerminatorAsync(wire, holdBack);
            Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
            Assert.True(expected.AsSpan().SequenceEqual(result.Payload.Span));
        }
    }

    [Fact]
    public async Task Wire7_DelimiterSplitAcrossReads()
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-7");
        var expected = FramingWireFactory.DestuffCompleteWire(wire);
        var result = await FramingPipe.ReadSplitTerminatorAsync(wire);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.AsSpan().SequenceEqual(result.Payload.Span));
    }

    [Fact]
    public async Task Wire7_HelloDotStyleTrailingPeriod_IsNotTerminator()
    {
        // Upstream wire-7 body includes lines that end with '.' before CRLF inside content;
        // terminator is only the final .\r\n line. Prove payload retains those periods.
        var wire = InnArticleCorpus.ReadAllBytes("wire-7");
        var expected = FramingWireFactory.DestuffCompleteWire(wire);
        Assert.Contains(".\r\n"u8, expected);

        var result = await FramingPipe.ReadOneAsync(wire);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.AsSpan().SequenceEqual(result.Payload.Span));
        Assert.Contains("parser.\r\n"u8, result.Payload.Span);
    }

    [Fact]
    public async Task Wire7_CurrentUnstuffs_BulkPreservesWire()
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-7");
        var stuffedWithoutTerminator = wire[..^3];
        var destuffed = FramingWireFactory.DestuffCompleteWire(wire);

        var current = await FramingPipe.ReadOneAsync(wire);
        Assert.Equal(NntpMultilineReadStatus.Completed, current.Status);
        Assert.True(destuffed.AsSpan().SequenceEqual(current.Payload.Span));

        // Retired bulk prototype kept stuffed wire; production destuffs.
        Assert.NotEqual(current.Payload.Length, stuffedWithoutTerminator.Length);
        Assert.True(current.Payload.Span.SequenceEqual(FramingWireFactory.DestuffPayload(stuffedWithoutTerminator)));
    }

    [Fact]
    public async Task WireTruncated_IsIncomplete()
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-truncated");
        Assert.False(InnArticleCorpus.ContainsFiveByteDelimiter(wire));
        var result = await FramingPipe.ReadOneAsync(wire);
        Assert.Equal(NntpMultilineReadStatus.Incomplete, result.Status);
        Assert.True(result.Payload.IsEmpty);
        Assert.False(InnArticleCorpus.ContainsFiveByteDelimiter(wire));
    }

    [Fact]
    public async Task WireStrange_IsIncomplete_PreservesExactBytesIncludingNul()
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-strange");
        Assert.Contains((byte)0, wire);
        var result = await FramingPipe.ReadSegmentedAsync(wire, 1);
        Assert.Equal(NntpMultilineReadStatus.Incomplete, result.Status);
        Assert.True(result.Payload.IsEmpty);
        // Fixture integrity: production does not echo incomplete bytes, but the NUL fixture is unchanged.
        Assert.Contains((byte)0, InnArticleCorpus.ReadAllBytes("wire-strange"));
        Assert.Equal(477, wire.Length);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("6")]
    [InlineData("7")]
    public async Task LeadingDotSpoolFixtures_DoNotFalseComplete(string name)
    {
        var entry = InnArticleCorpus.LoadManifest().Files.Single(f => f.RelativePath == name);
        Assert.True(entry.ContainsLeadingDotLines);
        Assert.Equal("spool_or_non_wire_no_nntp_terminator", entry.FramingClassification);
        var wire = InnArticleCorpus.ReadAllBytes(name);
        var result = await FramingPipe.ReadSegmentedAsync(wire, 2);
        Assert.Equal(NntpMultilineReadStatus.Incomplete, result.Status);
        Assert.True(result.Payload.IsEmpty);
        Assert.False(InnArticleCorpus.ContainsFiveByteDelimiter(wire));
    }

    [Fact]
    public async Task Pipelined_TwoWire7_SecondRemains()
    {
        var one = InnArticleCorpus.ReadAllBytes("wire-7");
        var expected = FramingWireFactory.DestuffCompleteWire(one);
        var combined = new byte[one.Length * 2];
        one.CopyTo(combined.AsSpan());
        one.CopyTo(combined.AsSpan(one.Length));

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(combined);
        await pipe.Writer.CompleteAsync();
        var first = await NntpMultilineDataReader.ReadArticleAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        var second = await NntpMultilineDataReader.ReadArticleAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        var leftover = await NntpMultilineDataReader.ReadArticleAsync(pipe.Reader, 64 * 1024, CancellationToken.None);

        Assert.Equal(NntpMultilineReadStatus.Completed, first.Status);
        Assert.Equal(NntpMultilineReadStatus.Completed, second.Status);
        Assert.True(expected.AsSpan().SequenceEqual(first.Payload.Span));
        Assert.True(expected.AsSpan().SequenceEqual(second.Payload.Span));
        Assert.Equal(NntpMultilineReadStatus.Incomplete, leftover.Status);
        Assert.True(leftover.Payload.IsEmpty);
    }
}
