using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VectorNNTPD.ReaderService.Session.Framing;

namespace VectorNNTPD.ReaderService.Tests.Session.Framing;

public sealed class IHaveArticleReaderTests
{
    private static Pipe CreatePipe() =>
        new(new PipeOptions(pauseWriterThreshold: 4 * 1024 * 1024, resumeWriterThreshold: 1024 * 1024, useSynchronizationContext: false));

    [Fact]
    public async Task OrdinaryLines_SurviveByteForByte_AndLeaveNextCommand()
    {
        var pipe = CreatePipe();
        await pipe.Writer.WriteAsync("Subject: hi\r\n\r\nhello\r\n.\r\nDATE\r\n"u8.ToArray());
        await pipe.Writer.FlushAsync();

        var result = await IHaveArticleReader.ReadAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal("Subject: hi\r\n\r\nhello\r\n"u8.ToArray(), result.Payload.ToArray());
        Assert.Equal(result.Payload.Length, result.Metrics.ArticleSize);
        Assert.Equal("DATE\r\n", await ReadLeftoverAsync(pipe.Reader));
    }

    [Fact]
    public async Task DotStuffedLines_RemainStuffed_InOwnedPayload()
    {
        var pipe = CreatePipe();
        await pipe.Writer.WriteAsync("Subject: d\r\n\r\n..foo\r\n.\r\n"u8.ToArray());
        await pipe.Writer.FlushAsync();

        var result = await IHaveArticleReader.ReadAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal("Subject: d\r\n\r\n..foo\r\n"u8.ToArray(), result.Payload.ToArray());
        Assert.DoesNotContain("\r\n.foo\r\n"u8.ToArray(), result.Payload.ToArray());
    }

    [Fact]
    public async Task DotStuffing_WithinDestuffedLimit_IsRetainedWhenStuffedWireExceedsIt()
    {
        var pipe = CreatePipe();
        var stuffed = string.Concat(Enumerable.Repeat("..\r\n", 10));
        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(stuffed + ".\r\nDATE\r\n"));
        await pipe.Writer.FlushAsync();

        var result = await IHaveArticleReader.ReadAsync(pipe.Reader, maxArticleBytes: 35, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal(40, result.Payload.Length);
        Assert.Equal(stuffed, Encoding.ASCII.GetString(result.Payload.Span));
        Assert.Equal("DATE\r\n", await ReadLeftoverAsync(pipe.Reader));
    }

    [Fact]
    public async Task DotStuffing_OverDestuffedLimit_ReturnsTooLarge_AndLeavesNextCommand()
    {
        var pipe = CreatePipe();
        var stuffed = string.Concat(Enumerable.Repeat("..\r\n", 10));
        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(stuffed + ".\r\nDATE\r\n"));
        await pipe.Writer.FlushAsync();

        var result = await IHaveArticleReader.ReadAsync(pipe.Reader, maxArticleBytes: 29, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.TooLarge, result.Status);
        Assert.Equal(0, result.Payload.Length);
        Assert.Equal("DATE\r\n", await ReadLeftoverAsync(pipe.Reader));
    }

    [Fact]
    public async Task Terminator_IsNotStored()
    {
        var pipe = CreatePipe();
        await pipe.Writer.WriteAsync("From: a\r\n\r\nbody\r\n.\r\n"u8.ToArray());
        await pipe.Writer.FlushAsync();

        var result = await IHaveArticleReader.ReadAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        Assert.Equal("From: a\r\n\r\nbody\r\n"u8.ToArray(), result.Payload.ToArray());
        Assert.False(result.Payload.Span.EndsWith(".\r\n"u8));
    }

    [Fact]
    public async Task TerminatorAndNextCommand_SameBuffer_LeavesCommand()
    {
        var pipe = CreatePipe();
        await pipe.Writer.WriteAsync("Subject: n\r\n\r\nx\r\n.\r\nQUIT\r\n"u8.ToArray());
        await pipe.Writer.FlushAsync();

        var result = await IHaveArticleReader.ReadAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal("QUIT\r\n", await ReadLeftoverAsync(pipe.Reader));
        Assert.DoesNotContain("QUIT"u8.ToArray(), result.Payload.ToArray());
    }

    [Fact]
    public async Task TerminatorSplitAcrossReads_Completes()
    {
        var pipe = CreatePipe();
        var read = IHaveArticleReader.ReadAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        await pipe.Writer.WriteAsync("Subject: s\r\n\r\nbody\r\n.\r"u8.ToArray());
        await pipe.Writer.FlushAsync();
        await Task.Yield();
        await pipe.Writer.WriteAsync("\nDATE\r\n"u8.ToArray());
        await pipe.Writer.FlushAsync();

        var result = await read;
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal("Subject: s\r\n\r\nbody\r\n"u8.ToArray(), result.Payload.ToArray());
        Assert.Equal("DATE\r\n", await ReadLeftoverAsync(pipe.Reader));
    }

    [Fact]
    public async Task PartialPipeSegments_ReassembleWire()
    {
        var pipe = CreatePipe();
        var read = IHaveArticleReader.ReadAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        await pipe.Writer.WriteAsync("Sub"u8.ToArray());
        await pipe.Writer.FlushAsync();
        await Task.Yield();
        await pipe.Writer.WriteAsync("ject: split\r\n\r\nbo"u8.ToArray());
        await pipe.Writer.FlushAsync();
        await Task.Yield();
        await pipe.Writer.WriteAsync("dy\r\n.\r\n"u8.ToArray());
        await pipe.Writer.FlushAsync();

        var result = await read;
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal("Subject: split\r\n\r\nbody\r\n"u8.ToArray(), result.Payload.ToArray());
        Assert.True(result.Metrics.PipeReads >= 2);
    }

    [Fact]
    public async Task PrematureEof_IsIncomplete()
    {
        var pipe = CreatePipe();
        await pipe.Writer.WriteAsync("Subject: x\r\n\r\nno-terminator"u8.ToArray());
        await pipe.Writer.CompleteAsync();

        var result = await IHaveArticleReader.ReadAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Incomplete, result.Status);
    }

    [Fact]
    public async Task TooLarge_ThenTerminator_ReturnsTooLarge_AndLeavesCommand()
    {
        var pipe = CreatePipe();
        var body = new string('A', 64);
        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes($"Subject: big\r\n\r\n{body}\r\n.\r\nDATE\r\n"));
        await pipe.Writer.FlushAsync();

        var result = await IHaveArticleReader.ReadAsync(pipe.Reader, maxArticleBytes: 16, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.TooLarge, result.Status);
        Assert.Equal("DATE\r\n", await ReadLeftoverAsync(pipe.Reader));
    }

    [Fact]
    public async Task LargeArticle_IsOwnedAfterAdvance()
    {
        var body = new string('B', 120 * 1024);
        var pipe = CreatePipe();
        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes("Subject: big\r\n\r\n" + body + "\r\n.\r\nDATE\r\n"));
        await pipe.Writer.FlushAsync();

        var result = await IHaveArticleReader.ReadAsync(pipe.Reader, 2 * 1024 * 1024, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        var leftover = await ReadLeftoverAsync(pipe.Reader);
        Assert.Equal("DATE\r\n", leftover);
        Assert.StartsWith("Subject: big\r\n\r\n", Encoding.ASCII.GetString(result.Payload.Span), StringComparison.Ordinal);
        Assert.True(result.Payload.Length >= 120 * 1024);
        _ = result.Payload.Span[0];
        _ = result.Payload.Span[^1];
    }

    [Fact]
    public async Task StreamingFourKiBChunks_LeaveNextCommandIntact()
    {
        var body = new string('S', 12 * 1024);
        var framed = Encoding.ASCII.GetBytes($"Subject: stream\r\n\r\n{body}\r\n.\r\nDATE\r\n");
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 8 * 1024,
            resumeWriterThreshold: 4 * 1024,
            minimumSegmentSize: 4 * 1024,
            useSynchronizationContext: false));
        var read = IHaveArticleReader.ReadAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        await Task.Yield();
        for (var offset = 0; offset < framed.Length; offset += 4096)
        {
            var n = Math.Min(4096, framed.Length - offset);
            await pipe.Writer.WriteAsync(framed.AsMemory(offset, n));
            await pipe.Writer.FlushAsync();
        }

        var result = await read;
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal("DATE\r\n", await ReadLeftoverAsync(pipe.Reader));
        Assert.True(result.Metrics.PipeReads >= 2);
    }

    [Fact]
    public async Task DeclaredSizes_DoNotConsumePastTerminator()
    {
        var pipe = CreatePipe();
        await pipe.Writer.WriteAsync("Content-Length: 999999\r\n\r\nshort\r\n.\r\nDATE\r\n"u8.ToArray());
        await pipe.Writer.FlushAsync();

        var result = await IHaveArticleReader.ReadAsync(pipe.Reader, 64 * 1024, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal("Content-Length: 999999\r\n\r\nshort\r\n"u8.ToArray(), result.Payload.ToArray());
        Assert.Equal("DATE\r\n", await ReadLeftoverAsync(pipe.Reader));
    }

    private static async Task<string> ReadLeftoverAsync(PipeReader reader)
    {
        var result = await reader.ReadAsync();
        var text = Encoding.ASCII.GetString(result.Buffer.ToArray());
        reader.AdvanceTo(result.Buffer.End);
        return text;
    }
}
