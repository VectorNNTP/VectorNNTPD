using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;

namespace VectorNNTPD.TransitService.Tests.Session.Commands;

/// <summary>
/// Bounded TX coalescing: logical response bytes stay identical; only Pipe flush granularity changes.
/// </summary>
public sealed class NntpResponseWriterCoalescingTests
{
    [Fact]
    public async Task TxChannel_AllowsSynchronousContinuations()
    {
        var pipe = new Pipe();
        await using var writer = new NntpResponseWriter(pipe.Writer);
        Assert.True(writer.ChannelAllowsSynchronousContinuations);
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task SingleWriteLine_DeliversExactBytes()
    {
        await using var harness = await CoalesceHarness.CreateAsync();
        await harness.Writer.WriteLineAsync(200, "Hello");
        Assert.Equal("200 Hello\r\n", await harness.ReadExactAsciiAsync("200 Hello\r\n".Length));
        Assert.Equal(1, harness.Writer.PipeFlushCount);
    }

    [Fact]
    public async Task TwoWriteLines_ExactBytesAndOrder()
    {
        await using var harness = await CoalesceHarness.CreateAsync();
        await harness.Writer.WriteLineAsync(200, "one");
        await harness.Writer.WriteLineAsync(201, "two");
        Assert.Equal("200 one\r\n201 two\r\n", await harness.ReadExactAsciiAsync("200 one\r\n201 two\r\n".Length));
    }

    [Fact]
    public async Task EightEnqueueLines_OneFlush_ExactBytesAndOrder()
    {
        await using var harness = await CoalesceHarness.CreateAsync();
        var expected = new StringBuilder();
        for (var i = 1; i <= NntpResponseWriter.CoalesceResponseBatchSize; i++)
        {
            var text = $"<a{i}@ex.com>";
            expected.Append("239 ").Append(text).Append("\r\n");
            await harness.Writer.EnqueueLineAsync(NntpReplyCodes.ArticleTransferredOk, text);
        }

        var wire = expected.ToString();
        Assert.Equal(wire, await harness.ReadExactAsciiAsync(wire.Length));
        Assert.Equal(1, harness.Writer.PipeFlushCount);
        Assert.False(harness.Writer.HasCoalescedUnflushed);
    }

    [Fact]
    public async Task NineEnqueueLines_FirstEightFlush_NinthOrderedAfterBarrier()
    {
        await using var harness = await CoalesceHarness.CreateAsync();
        var expected = new StringBuilder();
        for (var i = 1; i <= 9; i++)
        {
            var text = $"<b{i}@ex.com>";
            expected.Append("239 ").Append(text).Append("\r\n");
            await harness.Writer.EnqueueLineAsync(NntpReplyCodes.ArticleTransferredOk, text);
        }

        var firstEight = expected.ToString()[..^("239 <b9@ex.com>\r\n".Length)];
        Assert.Equal(firstEight, await harness.ReadExactAsciiAsync(firstEight.Length));
        Assert.Equal(1, harness.Writer.PipeFlushCount);
        Assert.True(harness.Writer.HasCoalescedUnflushed);

        await harness.Writer.FlushCoalescedAsync();
        Assert.Equal("239 <b9@ex.com>\r\n", await harness.ReadExactAsciiAsync("239 <b9@ex.com>\r\n".Length));
        Assert.Equal(2, harness.Writer.PipeFlushCount);
        Assert.Equal(expected.ToString(), firstEight + "239 <b9@ex.com>\r\n");
    }

    [Fact]
    public async Task PartialBatch_FlushCoalesced_DeliversHeldLines()
    {
        await using var harness = await CoalesceHarness.CreateAsync();
        await harness.Writer.EnqueueLineAsync(239, "<p1@ex.com>");
        await harness.Writer.EnqueueLineAsync(239, "<p2@ex.com>");
        await harness.Writer.EnqueueLineAsync(239, "<p3@ex.com>");
        Assert.Equal(0, harness.Writer.PipeFlushCount);

        await harness.Writer.FlushCoalescedAsync();
        Assert.Equal(
            "239 <p1@ex.com>\r\n239 <p2@ex.com>\r\n239 <p3@ex.com>\r\n",
            await harness.ReadExactAsciiAsync("239 <p1@ex.com>\r\n239 <p2@ex.com>\r\n239 <p3@ex.com>\r\n".Length));
        Assert.Equal(1, harness.Writer.PipeFlushCount);
    }

    [Fact]
    public async Task WriteLineBarrier_FlushesQueuedEnqueueLinesFirst()
    {
        await using var harness = await CoalesceHarness.CreateAsync();
        await harness.Writer.EnqueueLineAsync(239, "<pre@ex.com>");
        await harness.Writer.EnqueueLineAsync(239, "<pre2@ex.com>");
        await harness.Writer.WriteLineAsync(382, "Continue with TLS negotiation");

        Assert.Equal(
            "239 <pre@ex.com>\r\n239 <pre2@ex.com>\r\n382 Continue with TLS negotiation\r\n",
            await harness.ReadExactAsciiAsync(
                "239 <pre@ex.com>\r\n239 <pre2@ex.com>\r\n382 Continue with TLS negotiation\r\n".Length));
        Assert.False(harness.Writer.HasCoalescedUnflushed);
    }

    [Fact]
    public async Task Cancellation_PendingPartialBatch_DoesNotHang()
    {
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 1,
            resumeWriterThreshold: 1,
            useSynchronizationContext: false));
        var writer = new NntpResponseWriter(pipe.Writer);
        await writer.EnqueueLineAsync(239, "<c1@ex.com>");
        await writer.EnqueueLineAsync(239, "<c2@ex.com>");

        using var cts = new CancellationTokenSource();
        var flush = writer.FlushCoalescedAsync(cts.Token).AsTask();
        cts.Cancel();
        await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => flush);
        Assert.True(
            ex is OperationCanceledException or ObjectDisposedException,
            $"Unexpected {ex.GetType().FullName}: {ex.Message}");
        pipe.Writer.Complete();
        pipe.Reader.Complete();
    }

    [Fact]
    public async Task Disconnect_DisposeTerminatesPumpWithHeldBatch()
    {
        var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        var writer = new NntpResponseWriter(pipe.Writer);
        await writer.EnqueueLineAsync(239, "<d1@ex.com>");
        await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        pipe.Writer.Complete();
        pipe.Reader.Complete();
    }

    [Fact]
    public async Task Backpressure_EnqueueWaitsWhenChannelAndPipeAreSaturated()
    {
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 1,
            resumeWriterThreshold: 1,
            useSynchronizationContext: false));
        await using var writer = new NntpResponseWriter(pipe.Writer, channelCapacity: 2);

        for (var i = 0; i < NntpResponseWriter.CoalesceResponseBatchSize; i++)
        {
            await writer.EnqueueLineAsync(239, $"<bp{i}@ex.com>");
        }

        // Batch of 8 flushes into a paused pipe. Two more fill the bounded channel; the next waits.
        await writer.EnqueueLineAsync(239, "<q1@ex.com>");
        await writer.EnqueueLineAsync(239, "<q2@ex.com>");
        var blocked = writer.EnqueueLineAsync(239, "<blocked@ex.com>").AsTask();
        await Assert.ThrowsAsync<TimeoutException>(() => blocked.WaitAsync(TimeSpan.FromMilliseconds(200)));

        var read = await pipe.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        pipe.Reader.AdvanceTo(read.Buffer.End);
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        await writer.DisposeAsync();
        pipe.Writer.Complete();
        pipe.Reader.Complete();
    }

    [Fact]
    public async Task NonTakethisWriteLine_IsNotHeldForBatch()
    {
        await using var harness = await CoalesceHarness.CreateAsync();
        var write = harness.Writer.WriteLineAsync(111, "Server date-time").AsTask();
        await write.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("111 Server date-time\r\n", await harness.ReadExactAsciiAsync("111 Server date-time\r\n".Length));
        Assert.Equal(1, harness.Writer.PipeFlushCount);
    }

    [Fact]
    public async Task ExactBytes_CrlfAndFramingPreserved()
    {
        await using var harness = await CoalesceHarness.CreateAsync();
        await harness.Writer.EnqueueLineAsync(239, "<id@ex.com>");
        await harness.Writer.EnqueueLineAsync(439, "<rej@ex.com>");
        await harness.Writer.FlushCoalescedAsync();
        var bytes = Encoding.ASCII.GetBytes(await harness.ReadExactAsciiAsync("239 <id@ex.com>\r\n439 <rej@ex.com>\r\n".Length));
        Assert.Equal("239 <id@ex.com>\r\n439 <rej@ex.com>\r\n"u8.ToArray(), bytes);
    }

    [Fact]
    public async Task FlushCoalesced_WhenEmpty_IsNoOp()
    {
        await using var harness = await CoalesceHarness.CreateAsync();
        await harness.Writer.FlushCoalescedAsync();
        Assert.Equal(0, harness.Writer.PipeFlushCount);
    }

    private sealed class CoalesceHarness : IAsyncDisposable
    {
        private readonly Pipe _pipe;
        private int _disposed;

        private CoalesceHarness(Pipe pipe, NntpResponseWriter writer)
        {
            _pipe = pipe;
            Writer = writer;
        }

        public NntpResponseWriter Writer { get; }

        public static Task<CoalesceHarness> CreateAsync()
        {
            var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
            return Task.FromResult(new CoalesceHarness(pipe, new NntpResponseWriter(pipe.Writer)));
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
