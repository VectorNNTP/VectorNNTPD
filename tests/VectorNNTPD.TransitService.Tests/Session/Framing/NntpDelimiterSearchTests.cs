using System.Buffers;
using VectorNNTPD.TransitService.Session.Framing;

namespace VectorNNTPD.TransitService.Tests.Session.Framing;

/// <summary>
/// STREAM article terminator location: one <c>\r\n.\r\n</c> search, not per-CRLF line walks.
/// </summary>
public sealed class NntpDelimiterSearchTests
{
    [Fact]
    public void EmptyArticle_AtStart_IsThreeByteTerminator()
    {
        var buffer = new ReadOnlySequence<byte>(".\r\nQUIT\r\n"u8.ToArray());
        Assert.True(NntpDelimiterSearch.TryFindArticleTerminator(buffer, atArticleStart: true, out var payload, out var consumed));
        Assert.Equal(0, payload);
        Assert.Equal(3, consumed);
    }

    [Fact]
    public void EmptyArticle_NotAtStart_IsNotThreeByteTerminator()
    {
        var buffer = new ReadOnlySequence<byte>(".\r\n"u8.ToArray());
        Assert.False(NntpDelimiterSearch.TryFindArticleTerminator(buffer, atArticleStart: false, out _, out _));
    }

    [Fact]
    public void FiveByteTerminator_PayloadIncludesLeadingCrlf()
    {
        var buffer = new ReadOnlySequence<byte>("data\r\n.\r\nNEXT\r\n"u8.ToArray());
        Assert.True(NntpDelimiterSearch.TryFindArticleTerminator(buffer, atArticleStart: false, out var payload, out var consumed));
        Assert.Equal("data\r\n"u8.Length, payload);
        Assert.Equal("data\r\n.\r\n"u8.Length, consumed);
    }

    [Fact]
    public void HelloDot_IsNotTerminator()
    {
        var buffer = new ReadOnlySequence<byte>("Hello.\r\nSomething else.\r\n.\r\n"u8.ToArray());
        Assert.True(NntpDelimiterSearch.TryFindArticleTerminator(buffer, atArticleStart: true, out var payload, out _));
        Assert.Equal("Hello.\r\nSomething else.\r\n"u8.Length, payload);
    }

    [Fact]
    public void StuffedDotLine_IsNotTerminator()
    {
        var buffer = new ReadOnlySequence<byte>("..foo\r\nbar\r\n.\r\n"u8.ToArray());
        Assert.True(NntpDelimiterSearch.TryFindArticleTerminator(buffer, atArticleStart: true, out var payload, out _));
        Assert.Equal("..foo\r\nbar\r\n"u8.Length, payload);
    }

    [Fact]
    public void IncompleteFiveByte_ReturnsFalse()
    {
        var buffer = new ReadOnlySequence<byte>("keep\r\n.\r"u8.ToArray());
        Assert.False(NntpDelimiterSearch.TryFindArticleTerminator(buffer, atArticleStart: true, out _, out _));
    }

    [Fact]
    public void MultiSegment_FindsTerminatorSpanningSegments()
    {
        var first = "keep\r\n."u8.ToArray();
        var second = "\r\nNEXT"u8.ToArray();
        var segment1 = new SequenceSegment(first);
        var segment2 = new SequenceSegment(second);
        segment1.SetNext(segment2);
        var buffer = new ReadOnlySequence<byte>(segment1, 0, segment2, second.Length);
        Assert.True(NntpDelimiterSearch.TryFindArticleTerminator(buffer, atArticleStart: true, out var payload, out var consumed));
        Assert.Equal("keep\r\n"u8.Length, payload);
        Assert.Equal("keep\r\n.\r\n"u8.Length, consumed);
    }

    [Fact]
    public void CrlfSplitAcrossSegments_StillFindsLine()
    {
        var first = "DATE\r"u8.ToArray();
        var second = "\n"u8.ToArray();
        var segment1 = new SequenceSegment(first);
        var segment2 = new SequenceSegment(second);
        segment1.SetNext(segment2);
        var buffer = new ReadOnlySequence<byte>(segment1, 0, segment2, second.Length);
        Assert.True(NntpDelimiterSearch.TryReadLine(ref buffer, out var line));
        Assert.Equal("DATE"u8.ToArray(), line.ToArray());
        Assert.True(buffer.IsEmpty);
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(byte[] memory)
        {
            Memory = memory;
        }

        public void SetNext(SequenceSegment next)
        {
            Next = next;
            next.RunningIndex = RunningIndex + Memory.Length;
        }
    }
}
