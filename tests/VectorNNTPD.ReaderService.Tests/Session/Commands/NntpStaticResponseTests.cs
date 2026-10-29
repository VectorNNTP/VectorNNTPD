using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Tests.Session.Commands;

/// <summary>
/// Phase B: immortal static NNTP replies are encoded once, CRLF-terminated, and written
/// without a per-call string interpolation / ASCII encode.
/// </summary>
public sealed class NntpStaticResponseTests
{
    [Theory]
    [MemberData(nameof(SingleLineResponses))]
    public void StaticResponse_IsAscii_CrlfOnce_AndImmutable(string name, ReadOnlyMemory<byte> wire, string expected)
    {
        _ = name;
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        Assert.True(wire.Span.SequenceEqual(expectedBytes), Encoding.ASCII.GetString(wire.Span));
        Assert.True(wire.Span.EndsWith("\r\n"u8));
        Assert.Equal(1, CountCrlf(wire.Span));

        Assert.True(MemoryMarshal.TryGetArray(wire, out ArraySegment<byte> segment));
        Assert.NotNull(segment.Array);
        var snapshot = wire.ToArray();
        Assert.True(wire.Span.SequenceEqual(snapshot));
        Assert.True(wire.Span.SequenceEqual(expectedBytes));
    }

    [Fact]
    public async Task WriteLine_Static_DeliversExactBytes_Repeatedly()
    {
        await using var harness = await WriterHarness.CreateAsync();
        await harness.Writer.WriteLineAsync(NntpResponses.ConnectionClosing);
        await harness.Writer.WriteLineAsync(NntpResponses.ConnectionClosing);
        var expected = "205 Connection closing\r\n205 Connection closing\r\n";
        Assert.Equal(expected, await harness.ReadExactAsciiAsync(expected.Length));
        Assert.Equal(2, harness.Writer.PipeFlushCount);
        Assert.True(NntpResponses.ConnectionClosing.Span.SequenceEqual("205 Connection closing\r\n"u8));
    }

    [Fact]
    public async Task EnqueueLine_Static_PreservesCoalesceBatchOfEight()
    {
        await using var harness = await WriterHarness.CreateAsync();
        var expected = new StringBuilder();
        for (var i = 0; i < NntpResponseWriter.CoalesceResponseBatchSize; i++)
        {
            expected.Append("205 Connection closing\r\n");
            await harness.Writer.EnqueueLineAsync(NntpResponses.ConnectionClosing);
        }

        var wire = expected.ToString();
        Assert.Equal(wire, await harness.ReadExactAsciiAsync(wire.Length));
        Assert.Equal(1, harness.Writer.PipeFlushCount);
        Assert.False(harness.Writer.HasCoalescedUnflushed);
    }

    [Fact]
    public async Task StaticBarrier_FlushesQueuedEnqueueLinesFirst()
    {
        await using var harness = await WriterHarness.CreateAsync();
        await harness.Writer.EnqueueLineAsync(239, "<pre@ex.com>");
        await harness.Writer.WriteLineAsync(NntpResponses.ContinueWithTls);
        Assert.Equal(
            "239 <pre@ex.com>\r\n382 Continue with TLS negotiation\r\n",
            await harness.ReadExactAsciiAsync(
                "239 <pre@ex.com>\r\n382 Continue with TLS negotiation\r\n".Length));
        Assert.False(harness.Writer.HasCoalescedUnflushed);
    }

    [Fact]
    public async Task DynamicCheck_StillUsesStringPath_AndIncludesMessageId()
    {
        await using var harness = await WriterHarness.CreateAsync();
        await harness.Writer.WriteLineAsync(
            NntpReplyCodes.SendArticleToBeTransferred,
            "<dyn@example.com> send article to be transferred");
        Assert.Equal(
            "238 <dyn@example.com> send article to be transferred\r\n",
            await harness.ReadExactAsciiAsync(
                "238 <dyn@example.com> send article to be transferred\r\n".Length));
    }

    [Fact]
    public async Task DynamicTakethis_EnqueueLine_StillEncodesMessageIdPerCall()
    {
        await using var harness = await WriterHarness.CreateAsync();
        await harness.Writer.EnqueueLineAsync(NntpReplyCodes.ArticleTransferredOk, "<a@ex.com>");
        await harness.Writer.EnqueueLineAsync(NntpReplyCodes.TransferRejected, "<b@ex.com>");
        await harness.Writer.FlushCoalescedAsync();
        Assert.Equal(
            "239 <a@ex.com>\r\n439 <b@ex.com>\r\n",
            await harness.ReadExactAsciiAsync("239 <a@ex.com>\r\n439 <b@ex.com>\r\n".Length));
    }

    [Fact]
    public async Task WriteBytesAndFlush_ReusesCallerMemory_WithoutCopyingIntoNewArray()
    {
        await using var harness = await WriterHarness.CreateAsync();
        var payload = NntpResponses.CompressionActive;
        Assert.True(MemoryMarshal.TryGetArray(payload, out var before));
        await harness.Writer.WriteBytesAndFlushAsync(payload);
        Assert.True(MemoryMarshal.TryGetArray(payload, out var after));
        Assert.Same(before.Array, after.Array);
        Assert.Equal("206 Compression active\r\n", await harness.ReadExactAsciiAsync("206 Compression active\r\n".Length));
    }

    [Fact]
    public void HelpBody_MatchesExistingStringLines_AndNoneNeedDotStuffing()
    {
        Assert.Equal(Help.BodyLines.Count, NntpResponses.HelpBodyLines.Length);
        for (var i = 0; i < Help.BodyLines.Count; i++)
        {
            Assert.False(Help.BodyLines[i].StartsWith('.'));
            var expected = Encoding.ASCII.GetBytes(Help.BodyLines[i] + "\r\n");
            Assert.True(NntpResponses.HelpBodyLines[i].Span.SequenceEqual(expected));
        }
    }

    [Fact]
    public void HelpComplete_IsExactConcatenationOfStatusBodyAndTerminator()
    {
        var expected = ExpectedHelpWire();
        Assert.True(NntpResponses.HelpComplete.Span.SequenceEqual(expected));
        Assert.True(NntpResponses.HelpComplete.Span.EndsWith(".\r\n"u8));
        Assert.Equal(Help.BodyLines.Count + 2, CountCrlf(NntpResponses.HelpComplete.Span));
        Assert.False(NntpResponses.HelpComplete.Span.EndsWith("\r\n.\r\n.\r\n"u8));
    }

    [Fact]
    public async Task HelpComplete_WriteOnce_OneChannelItem_ExactBytes()
    {
        await using var harness = await WriterHarness.CreateAsync();
        await harness.Writer.WriteLineAsync(NntpResponses.HelpComplete);
        var expected = Encoding.ASCII.GetString(ExpectedHelpWire());
        Assert.Equal(expected, await harness.ReadExactAsciiAsync(expected.Length));
        Assert.Equal(1, harness.Writer.ChannelEnqueueCount);
        Assert.Equal(1, harness.Writer.PipeFlushCount);
    }

    [Fact]
    public void StaticResponseTable_Access_DoesNotAllocate()
    {
        _ = NntpResponses.ConnectionClosing;
        _ = NntpResponses.HelpBodyLines;
        var sink = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++)
        {
            sink += NntpResponses.ConnectionClosing.Length;
            sink += NntpResponses.GreetingPostingProhibited.Length;
            sink += NntpResponses.HelpTextFollows.Length;
            sink += NntpResponses.HelpBodyLines.Length;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(sink > 0);
    }

    [Fact]
    public void ResponseEncoding_StaticTableIsZeroAlloc_StringPathAllocates()
    {
        const int iterations = 256;
        var sink = 0;

        for (var i = 0; i < 8; i++)
        {
            sink += Encoding.ASCII.GetBytes($"{205} Connection closing\r\n").Length;
            sink += NntpResponses.ConnectionClosing.Length;
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            sink += Encoding.ASCII.GetBytes($"{205} Connection closing\r\n").Length;
        }

        var stringEncodeAlloc = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            sink += NntpResponses.ConnectionClosing.Length;
        }

        var staticEncodeAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, staticEncodeAlloc);
        Assert.True(
            stringEncodeAlloc >= iterations * NntpResponses.ConnectionClosing.Length,
            $"string encode allocated {stringEncodeAlloc} B over {iterations} lines.");
        Assert.NotEqual(int.MinValue, sink);
    }

    public static TheoryData<string, ReadOnlyMemory<byte>, string> SingleLineResponses() =>
        new()
        {
            { "greeting-200", NntpResponses.GreetingPostingPermitted, "200 VectorNNTPD.ReaderService ready, posting permitted\r\n" },
            { "greeting-201", NntpResponses.GreetingPostingProhibited, "201 VectorNNTPD.ReaderService ready, posting prohibited\r\n" },
            { "quit", NntpResponses.ConnectionClosing, "205 Connection closing\r\n" },
            { "compress-206", NntpResponses.CompressionActive, "206 Compression active\r\n" },
            { "auth-281", NntpResponses.AuthenticationAccepted, "281 Authentication accepted\r\n" },
            { "auth-381", NntpResponses.PasswordRequired, "381 Password required\r\n" },
            { "starttls-382", NntpResponses.ContinueWithTls, "382 Continue with TLS negotiation\r\n" },
            { "240", NntpResponses.ArticleReceivedOk, "240 Article received OK\r\n" },
            { "340", NntpResponses.PostSendArticle, "340 Input article; end with <CR-LF>.<CR-LF>\r\n" },
            { "400", NntpResponses.ServiceTemporarilyUnavailable, "400 Service temporarily unavailable\r\n" },
            { "440", NntpResponses.PostingProhibited, "440 Posting not permitted\r\n" },
            { "441", NntpResponses.PostingFailed, "441 Posting failed\r\n" },
            { "480", NntpResponses.AuthenticationRequired, "480 Authentication required\r\n" },
            { "500-unknown", NntpResponses.UnknownCommand, "500 Unknown command\r\n" },
            { "500-stub", NntpResponses.CommandNotImplemented, "500 Command not implemented\r\n" },
            { "501", NntpResponses.SyntaxError, "501 Syntax error\r\n" },
            { "503-list-data", NntpResponses.ListDataItemNotStored, "503 Data item not stored\r\n" },
            { "215-overview-fmt", NntpResponses.ListOverviewFmtFollows, "215 Order of fields in overview database.\r\n" },
            { "215-headers", NntpResponses.ListHeadersFollows, "215 headers and metadata items supported:\r\n" },
            { "412", NntpResponses.NoNewsgroupSelected, "412 No newsgroup selected\r\n" },
            { "420", NntpResponses.CurrentArticleNumberInvalid, "420 Current article number is invalid\r\n" },
            { "423", NntpResponses.NoArticleWithNumber, "423 No article with that number\r\n" },
            { "430", NntpResponses.NoArticleWithMessageId, "430 No article with that message-id\r\n" },
            { "help-100", NntpResponses.HelpTextFollows, "100 Help text follows\r\n" },
            { "caps-101", NntpResponses.CapabilityListFollows, "101 Capability list:\r\n" },
            { "multiline-end", NntpResponses.MultilineTerminator, ".\r\n" },
        };

    private static byte[] ExpectedHelpWire()
    {
        var builder = new StringBuilder();
        builder.Append("100 Help text follows\r\n");
        foreach (var line in Help.BodyLines)
        {
            builder.Append(line).Append("\r\n");
        }

        builder.Append(".\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static int CountCrlf(ReadOnlySpan<byte> bytes)
    {
        var count = 0;
        for (var i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == (byte)'\r' && bytes[i + 1] == (byte)'\n')
            {
                count++;
            }
        }

        return count;
    }

    private sealed class WriterHarness : IAsyncDisposable
    {
        private readonly Pipe _pipe;
        private int _disposed;

        private WriterHarness(Pipe pipe, NntpResponseWriter writer)
        {
            _pipe = pipe;
            Writer = writer;
        }

        public NntpResponseWriter Writer { get; }

        public static Task<WriterHarness> CreateAsync()
        {
            var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
            return Task.FromResult(new WriterHarness(pipe, new NntpResponseWriter(pipe.Writer)));
        }

        public async Task<string> ReadExactAsciiAsync(int byteCount)
        {
            var buffer = new byte[byteCount];
            var copied = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (copied < byteCount)
            {
                var result = await _pipe.Reader.ReadAsync(timeout.Token);
                var unread = result.Buffer;
                var take = (int)Math.Min(unread.Length, byteCount - copied);
                unread.Slice(0, take).CopyTo(buffer.AsSpan(copied, take));
                copied += take;
                _pipe.Reader.AdvanceTo(unread.GetPosition(take));
                if (result.IsCompleted && copied < byteCount)
                {
                    throw new InvalidOperationException($"Pipe completed after {copied} of {byteCount} bytes.");
                }
            }

            return Encoding.ASCII.GetString(buffer);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await Writer.DisposeAsync();
            await _pipe.Writer.CompleteAsync();
            await _pipe.Reader.CompleteAsync();
        }
    }
}
