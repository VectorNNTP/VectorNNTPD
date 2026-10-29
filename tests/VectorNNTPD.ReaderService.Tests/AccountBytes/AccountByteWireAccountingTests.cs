using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Tests.AccountBytes;

public sealed class AccountByteWireAccountingTests
{
    [Fact]
    public async Task StatusLine_IncludesCrLf_Once()
    {
        await using var harness = await WireHarness.CreateAsync();
        await harness.Writer.WriteLineAsync(200, "Hello");
        Assert.Equal("200 Hello\r\n".Length, harness.Sink.Copied);
        Assert.Equal("200 Hello\r\n", await harness.ReadExactAsciiAsync("200 Hello\r\n".Length));
    }

    [Fact]
    public async Task Multiline_IncludesFramingAndTerminator()
    {
        await using var harness = await WireHarness.CreateAsync();
        await harness.Writer.WriteMultilineStartAsync(100, "Help text follows");
        await harness.Writer.WriteMultilineDataAsync("  one");
        await harness.Writer.WriteMultilineEndAsync();
        var expected = "100 Help text follows\r\n  one\r\n.\r\n";
        Assert.Equal(expected.Length, harness.Sink.Copied);
        Assert.Equal(expected, await harness.ReadExactAsciiAsync(expected.Length));
    }

    [Fact]
    public async Task DotStuffing_IsCountedOnTheWire()
    {
        await using var harness = await WireHarness.CreateAsync();
        await harness.Writer.WriteMultilineDataAsync(".secret");
        await harness.Writer.WriteMultilineEndAsync();
        var expected = "..secret\r\n.\r\n";
        Assert.Equal(expected.Length, harness.Sink.Copied);
        Assert.Equal(expected, await harness.ReadExactAsciiAsync(expected.Length));
    }

    [Fact]
    public async Task ArticlePayload_IsCountedOnce_WithTerminator()
    {
        await using var harness = await WireHarness.CreateAsync();
        var destuffed = "From: a@b\r\n\r\nbody\r\n"u8.ToArray();
        await harness.Writer.WriteArticleAsync(destuffed, NntpArticleTxFraming.PeerTakeThis("<a@b>"));
        Assert.True(harness.Sink.Copied > destuffed.Length);
        var wire = await harness.ReadExactAsciiAsync(harness.Sink.Copied);
        Assert.EndsWith(".\r\n", wire, StringComparison.Ordinal);
        Assert.Equal(wire.Length, harness.Sink.Copied);
    }

    [Fact]
    public async Task PipeWriterCapacity_IsNotCounted()
    {
        var output = new CapacitySpyWriter();
        await using var writer = new NntpResponseWriter(output);
        var sink = new RecordingSink();
        writer.SetByteSink(sink);
        await writer.WriteLineAsync(200, "Hi");
        Assert.True(output.LastGetMemory > output.LastAdvance);
        Assert.Equal(output.LastAdvance, sink.Copied);
        Assert.Equal("200 Hi\r\n".Length, sink.Copied);
    }

    [Fact]
    public async Task DirectSpeedTestWrite_IsCountedAfterAdvance()
    {
        await using var harness = await WireHarness.CreateAsync();
        await using (var lease = await harness.Writer.AcquireDirectTxPipeAsync())
        {
            await lease.WriteAndFlushAsync("SPEED"u8.ToArray());
        }

        Assert.Equal(5, harness.Sink.Copied);
    }

    [Fact]
    public void ObserveCopied_IgnoresNonPositive()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        var tracker = new AccountByteTracker(durable, cluster, Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountByteTracker>.Instance);
        durable.SeedByteAccount("alice", 100);
        var sink = tracker.CreateSink("alice");
        sink.ObserveCopied(0);
        sink.ObserveCopied(-3);
        Assert.Equal(0, tracker.PendingBytes("alice"));
    }

    private sealed class RecordingSink : IAccountByteSink
    {
        public int Copied { get; private set; }

        public void ObserveCopied(int bytes)
        {
            if (bytes > 0)
            {
                Copied += bytes;
            }
        }
    }

    private sealed class WireHarness : IAsyncDisposable
    {
        private WireHarness(Pipe pipe, NntpResponseWriter writer, RecordingSink sink)
        {
            Pipe = pipe;
            Writer = writer;
            Sink = sink;
        }

        public Pipe Pipe { get; }

        public NntpResponseWriter Writer { get; }

        public RecordingSink Sink { get; }

        public static async Task<WireHarness> CreateAsync()
        {
            var pipe = new Pipe();
            var writer = new NntpResponseWriter(pipe.Writer);
            var sink = new RecordingSink();
            writer.SetByteSink(sink);
            await Task.Yield();
            return new WireHarness(pipe, writer, sink);
        }

        public async Task<string> ReadExactAsciiAsync(int count)
        {
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var result = await Pipe.Reader.ReadAsync();
                var copied = result.Buffer.Slice(0, Math.Min(result.Buffer.Length, count - read));
                copied.CopyTo(buffer.AsSpan(read, (int)copied.Length));
                read += (int)copied.Length;
                Pipe.Reader.AdvanceTo(copied.End);
            }

            return Encoding.ASCII.GetString(buffer);
        }

        public async ValueTask DisposeAsync()
        {
            await Writer.DisposeAsync();
            await Pipe.Writer.CompleteAsync();
            await Pipe.Reader.CompleteAsync();
        }
    }

    private sealed class CapacitySpyWriter : PipeWriter
    {
        private readonly Pipe _pipe = new();

        public int LastGetMemory { get; private set; }

        public int LastAdvance { get; private set; }

        public override bool CanGetUnflushedBytes => true;

        public override long UnflushedBytes => _pipe.Writer.UnflushedBytes;

        public override void Advance(int bytes)
        {
            LastAdvance = bytes;
            _pipe.Writer.Advance(bytes);
        }

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            var memory = _pipe.Writer.GetMemory(Math.Max(sizeHint, 64 * 1024));
            LastGetMemory = memory.Length;
            return memory;
        }

        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public override void CancelPendingFlush() => _pipe.Writer.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => _pipe.Writer.Complete(exception);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) =>
            _pipe.Writer.FlushAsync(cancellationToken);
    }
}
