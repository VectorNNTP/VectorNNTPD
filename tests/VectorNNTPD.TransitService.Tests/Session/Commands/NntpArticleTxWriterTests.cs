using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Session.Framing;
using Xunit;
using Xunit.Abstractions;

namespace VectorNNTPD.TransitService.Tests.Session.Commands;

/// <summary>Phase 1 shared article TX primitive — correctness, ownership, cancellation.</summary>
public sealed class NntpArticleTxWriterTests
{
    private readonly ITestOutputHelper _output;

    public NntpArticleTxWriterTests(ITestOutputHelper output) => _output = output;

    private static readonly string IncomingRoot = @"C:\Temp\Incoming";

    [Fact]
    public async Task WriteArticle_Empty_EmitsTerminatorOnlyAfterHeader()
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        var framing = NntpArticleTxFraming.CustomerArticle("<empty@test>");
        var oracle = BuildOracle(framing, ReadOnlySpan<byte>.Empty);

        await harness.Writer.WriteArticleAsync(ReadOnlyMemory<byte>.Empty, framing);

        Assert.Equal(oracle, await harness.DrainAllAsync());
        Assert.Equal(1, harness.Writer.ChannelEnqueueCount); // header+terminator fit one 64KiB chunk
    }

    [Fact]
    public async Task WriteArticle_Small_CustomerFraming()
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        var stored = "hello\r\n"u8.ToArray();
        var framing = NntpArticleTxFraming.CustomerArticle("<a@b>");
        var oracle = BuildOracle(framing, stored);

        await harness.Writer.WriteArticleAsync(stored, framing);

        Assert.Equal(oracle, await harness.DrainAllAsync());
    }

    [Fact]
    public async Task WriteArticle_PeerTakeThisFraming()
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        var stored = "body\r\n"u8.ToArray();
        var framing = NntpArticleTxFraming.PeerTakeThis("<peer@id>");
        var oracle = BuildOracle(framing, stored);

        await harness.Writer.WriteArticleAsync(stored, framing);

        var wire = await harness.DrainAllAsync();
        Assert.Equal(oracle, wire);
        Assert.StartsWith("TAKETHIS <peer@id>\r\n", Encoding.ASCII.GetString(wire));
        Assert.EndsWith(".\r\n", Encoding.ASCII.GetString(wire));
    }

    [Fact]
    public async Task WriteArticle_LeadingDot_Restuffed()
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        var stored = ".example\r\n"u8.ToArray();
        var framing = NntpArticleTxFraming.CustomerArticle("<dot@test>");
        await harness.Writer.WriteArticleAsync(stored, framing);
        Assert.Equal(BuildOracle(framing, stored), await harness.DrainAllAsync());
    }

    [Fact]
    public async Task WriteArticle_MultipleLeadingDots()
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        var stored = "..example\r\n.\r\n"u8.ToArray();
        var framing = NntpArticleTxFraming.CustomerArticle("<dots@test>");
        await harness.Writer.WriteArticleAsync(stored, framing);
        Assert.Equal(BuildOracle(framing, stored), await harness.DrainAllAsync());
    }

    [Fact]
    public async Task WriteArticle_TerminatorPresentAndLoneDotContentDoesNotEndEarly()
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        var stored = ".\r\n"u8.ToArray();
        var framing = NntpArticleTxFraming.CustomerArticle("<lone@test>");
        var wire = BuildOracle(framing, stored);
        await harness.Writer.WriteArticleAsync(stored, framing);
        var actual = await harness.DrainAllAsync();
        Assert.Equal(wire, actual);

        var header = framing.BuildHeaderBytes();
        var bodyWire = actual.AsMemory(header.Length);
        var result = await NntpMultilineDataReader.ReadArticleAsync(
            PipeReader.Create(new ReadOnlySequence<byte>(bodyWire)),
            1024,
            CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal(stored, result.Payload.ToArray());
    }

    [Fact]
    public async Task WriteArticle_PreservesCrlf_AndBareLfInsideLine()
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        // Bare LF is not a line separator for restuff (CRLF-based); preserved as content.
        var stored = "Path: host\nnot-for-mail\r\nSubject: x\r\n\r\nbody\r\n"u8.ToArray();
        var framing = NntpArticleTxFraming.CustomerArticle("<barelf@test>");
        await harness.Writer.WriteArticleAsync(stored, framing);
        Assert.Equal(BuildOracle(framing, stored), await harness.DrainAllAsync());
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(100)]
    public async Task WriteArticle_ChunkBoundary_InsideBody(int chunkBytes)
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        var stored = Encoding.ASCII.GetBytes(new string('A', 250) + "\r\n" + new string('B', 250) + "\r\n");
        var framing = NntpArticleTxFraming.CustomerArticle("<chunk@test>");
        await harness.Writer.WriteArticleForTestsAsync(stored, framing, chunkBytes);
        Assert.Equal(BuildOracle(framing, stored), await harness.DrainAllAsync());
        Assert.True(harness.Writer.ChannelEnqueueCount >= 2);
    }

    [Fact]
    public async Task WriteArticle_ChunkBoundary_OnCrlfAndDotLine()
    {
        var stored = Encoding.ASCII.GetBytes(
            new string('x', 20) + "\r\n" +
            ".dotted\r\n" +
            new string('y', 20) + "\r\n");
        var framing = NntpArticleTxFraming.CustomerArticle("<edge@test>");
        var oracle = BuildOracle(framing, stored);
        foreach (var chunk in new[] { 7, 11, 17, 23, 32 })
        {
            await using var harness = await ArticleTxHarness.CreateAsync();
            await harness.Writer.WriteArticleForTestsAsync(stored, framing, chunk);
            Assert.Equal(oracle, await harness.DrainAllAsync());
        }
    }

    [Theory]
    [InlineData(NntpArticleTxChunkBudget.DefaultBytes)]
    [InlineData(128 * 1024)]
    [InlineData(NntpArticleTxChunkBudget.MaxBytes)]
    public async Task WriteArticle_ProductionChunkSizes_TypicalSizedArticle(int chunkBytes)
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        var stored = BuildStoredArticleBytes(sizeHint: 740 * 1024, leadingDotEvery: 50);
        var framing = NntpArticleTxFraming.CustomerArticle("<740k@test>");
        await harness.Writer.WriteArticleAsync(stored, framing, chunkBytes);
        var actual = await harness.DrainAllAsync();
        Assert.Equal(BuildOracle(framing, stored), actual);
        var expectedChunks = (int)Math.Ceiling(actual.Length / (double)chunkBytes);
        Assert.Equal(expectedChunks, harness.Writer.ChannelEnqueueCount);
    }

    [Fact]
    public async Task WriteArticle_TwoMiB_Article()
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        var stored = BuildStoredArticleBytes(sizeHint: 2 * 1024 * 1024, leadingDotEvery: 100);
        var framing = NntpArticleTxFraming.CustomerArticle("<2m@test>");
        await harness.Writer.WriteArticleAsync(stored, framing);
        Assert.Equal(BuildOracle(framing, stored), await harness.DrainAllAsync());
    }

    [Fact]
    public async Task WriteArticle_MultipleConsecutive_PreserveOrder()
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        var a = "aaa\r\n"u8.ToArray();
        var b = ".bbb\r\n"u8.ToArray();
        var c = "ccc\r\n"u8.ToArray();
        var fa = NntpArticleTxFraming.CustomerArticle("<a@1>");
        var fb = NntpArticleTxFraming.PeerTakeThis("<b@2>");
        var fc = NntpArticleTxFraming.CustomerArticle("<c@3>");

        await harness.Writer.WriteArticleAsync(a, fa);
        await harness.Writer.WriteArticleAsync(b, fb);
        await harness.Writer.WriteArticleAsync(c, fc);

        var expected = Concat(BuildOracle(fa, a), BuildOracle(fb, b), BuildOracle(fc, c));
        Assert.Equal(expected, await harness.DrainAllAsync());
    }

    [Fact]
    public async Task WriteArticle_RejectsChunkOutsideProductionRange()
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        var framing = NntpArticleTxFraming.CustomerArticle("<x@y>");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await harness.Writer.WriteArticleAsync("a\r\n"u8.ToArray(), framing, 1024));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await harness.Writer.WriteArticleAsync("a\r\n"u8.ToArray(), framing, 512 * 1024));
    }

    [Fact]
    public async Task WriteArticle_Cancellation_BeforeEnqueue_Throws()
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var framing = NntpArticleTxFraming.CustomerArticle("<cancel@test>");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await harness.Writer.WriteArticleAsync("body\r\n"u8.ToArray(), framing, cts.Token));
    }

    [Fact]
    public async Task WriteArticle_Cancellation_WhileQueued_ReleasesWaiters()
    {
        // Capacity 1 + unread pipe → first chunk flush blocks pump; second enqueue waits; cancel producer.
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 4 * 1024,
            resumeWriterThreshold: 2 * 1024,
            minimumSegmentSize: 1024,
            useSynchronizationContext: false));
        await using var writer = new NntpResponseWriter(pipe.Writer, channelCapacity: 1);
        var framing = NntpArticleTxFraming.CustomerArticle("<bp@test>");
        var stored = BuildStoredArticleBytes(sizeHint: 200 * 1024, leadingDotEvery: 0);
        using var cts = new CancellationTokenSource();

        var writeTask = writer.WriteArticleAsync(stored, framing, cts.Token);
        // Allow first chunk to enter channel / pipe.
        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writeTask);
        pipe.Reader.CancelPendingRead();
        await pipe.Reader.CompleteAsync();
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task WriteArticle_DisposeWriter_CancelsInFlight()
    {
        var pipe = new Pipe();
        var writer = new NntpResponseWriter(pipe.Writer, channelCapacity: 1);
        var framing = NntpArticleTxFraming.CustomerArticle("<disp@test>");
        var stored = BuildStoredArticleBytes(180 * 1024, 0);
        var writeTask = writer.WriteArticleAsync(stored, framing);
        await Task.Delay(30);
        await writer.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(async () => await writeTask);
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task WriteArticle_CompletedPipe_SurfacesFailure()
    {
        var pipe = new Pipe();
        await using var writer = new NntpResponseWriter(pipe.Writer);
        await pipe.Reader.CompleteAsync(); // send path sees completed reader → FlushAsync IsCompleted
        await pipe.Writer.CompleteAsync();
        var framing = NntpArticleTxFraming.CustomerArticle("<fail@test>");
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await writer.WriteArticleAsync("x\r\n"u8.ToArray(), framing));
    }

    [Fact]
    public async Task WriteArticle_DoesNotUseSessionMode()
    {
        // Primitive has no mode parameter; both framings work on the same writer instance.
        await using var harness = await ArticleTxHarness.CreateAsync();
        await harness.Writer.WriteArticleAsync("r\r\n"u8.ToArray(), NntpArticleTxFraming.CustomerArticle("<r@1>"));
        await harness.Writer.WriteArticleAsync("s\r\n"u8.ToArray(), NntpArticleTxFraming.PeerTakeThis("<s@1>"));
        var wire = Encoding.ASCII.GetString(await harness.DrainAllAsync());
        Assert.Contains("220 0 <r@1>", wire, StringComparison.Ordinal);
        Assert.Contains("TAKETHIS <s@1>", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteArticle_ChannelItemsLessThanPerLineModel()
    {
        await using var harness = await ArticleTxHarness.CreateAsync();
        var lineCount = 2000;
        var sb = new StringBuilder();
        for (var i = 0; i < lineCount; i++)
        {
            sb.Append("line-").Append(i).Append("\r\n");
        }

        var stored = Encoding.ASCII.GetBytes(sb.ToString());
        var framing = NntpArticleTxFraming.CustomerArticle("<lines@test>");
        await harness.Writer.WriteArticleAsync(stored, framing);
        _ = await harness.DrainAllAsync();
        // Per-line model would be ~2002 Channel items; chunked must be far fewer.
        Assert.True(harness.Writer.ChannelEnqueueCount < lineCount / 10);
    }

    [Fact]
    public async Task ProductionRestuff_MatchesRoundTripWithMultilineReader()
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
    public async Task Corpus_Oracle_IfPresent()
    {
        if (!Directory.Exists(IncomingRoot))
        {
            return; // environment without corpus — synthetic tests still cover Phase 1
        }

        var samples = PickCorpusSamples(IncomingRoot, maxFiles: 8);
        if (samples.Count == 0)
        {
            return;
        }

        foreach (var path in samples)
        {
            await using var harness = await ArticleTxHarness.CreateAsync();
            var stored = await File.ReadAllBytesAsync(path);
            var framing = NntpArticleTxFraming.CustomerArticle("<corpus@local>");
            await harness.Writer.WriteArticleAsync(stored, framing);
            Assert.Equal(BuildOracle(framing, stored), await harness.DrainAllAsync());
        }
    }

    [Fact]
    public async Task FocusedPerf_ChunkedVsPerLine_TypicalArticle()
    {
        var stored = BuildStoredArticleBytes(750 * 1024, leadingDotEvery: 40);
        var framing = NntpArticleTxFraming.CustomerArticle("<perf@test>");

        // Chunked path
        await using var chunked = await ArticleTxHarness.CreateAsync();
        PrepareForAllocationSample();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await chunked.Writer.WriteArticleAsync(stored, framing);
        sw.Stop();
        var chunkedAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
        var chunkedMs = sw.Elapsed.TotalMilliseconds;
        var chunkedEnq = chunked.Writer.ChannelEnqueueCount;
        var chunkedFlush = chunked.Writer.PipeFlushCount;
        _ = await chunked.DrainAllAsync();

        // Legacy per-line path
        await using var lines = await ArticleTxHarness.CreateAsync();
        PrepareForAllocationSample();
        before = GC.GetTotalAllocatedBytes(precise: true);
        sw.Restart();
        await lines.Writer.WriteMultilineStartAsync(220, "0 <perf@test>");

        // Deterministic line emission matching restuff semantics via oracle body without header:
        var body = ArticleWireReconstructor.RestuffArticle(stored);
        // Strip terminator for per-line write then end:
        var bodyWithoutTerm = body.AsSpan(0, body.Length - 3); // remove .\r\n
        var text = Encoding.ASCII.GetString(bodyWithoutTerm);
        var parts = text.Split("\r\n");
        // last split empty if body ended with CRLF before terminator
        for (var i = 0; i < parts.Length; i++)
        {
            if (i == parts.Length - 1 && parts[i].Length == 0)
            {
                break;
            }

            await lines.Writer.WriteMultilineDataAsync(parts[i]);
        }

        await lines.Writer.WriteMultilineEndAsync();
        sw.Stop();
        var lineAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
        var lineMs = sw.Elapsed.TotalMilliseconds;
        var lineEnq = lines.Writer.ChannelEnqueueCount;
        var lineFlush = lines.Writer.PipeFlushCount;

        Assert.True(chunkedEnq < lineEnq, $"chunked enq {chunkedEnq} should be < line enq {lineEnq}");
        Assert.True(chunkedFlush < lineFlush, $"chunked flush {chunkedFlush} should be < line flush {lineFlush}");
        // Soft throughput check: chunked should not be dramatically slower on in-memory pipe.
        Assert.True(chunkedMs < lineMs * 5 + 50, $"chunked {chunkedMs:F1}ms vs lines {lineMs:F1}ms");

        _output.WriteLine(
            $"PERF chunked: {chunkedMs:F2}ms alloc={chunkedAlloc} enq={chunkedEnq} flush={chunkedFlush}; " +
            $"per-line: {lineMs:F2}ms alloc={lineAlloc} enq={lineEnq} flush={lineFlush}");
        Assert.True(chunkedAlloc > 0 && lineAlloc > 0);
    }

    private static byte[] BuildOracle(NntpArticleTxFraming framing, ReadOnlySpan<byte> stored)
    {
        var header = framing.BuildHeaderBytes();
        var body = ArticleWireReconstructor.RestuffArticle(stored);
        var result = new byte[header.Length + body.Length];
        header.CopyTo(result.AsSpan());
        body.CopyTo(result.AsSpan(header.Length));
        return result;
    }

    /// <summary>Forces a full GC cycle before allocation sampling in the focused perf probe.</summary>
    private static void PrepareForAllocationSample()
    {
        // codeql[cs/call-to-gc] Perf probe resets GC before allocation sampling.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        // codeql[cs/call-to-gc] Second collect after finalizers for stable allocation baseline.
        GC.Collect();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var len = parts.Sum(static p => p.Length);
        var result = new byte[len];
        var o = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result.AsSpan(o));
            o += p.Length;
        }

        return result;
    }

    private static byte[] BuildStoredArticleBytes(int sizeHint, int leadingDotEvery)
    {
        using var ms = new MemoryStream(sizeHint + 1024);
        var n = 0;
        while (ms.Length < sizeHint)
        {
            if (leadingDotEvery > 0 && n % leadingDotEvery == 0)
            {
                ms.Write("."u8);
            }

            var line = Encoding.ASCII.GetBytes($"line-{n}-payload-xxxxxxxx\r\n");
            ms.Write(line);
            n++;
        }

        return ms.ToArray();
    }

    private static List<string> PickCorpusSamples(string root, int maxFiles)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Take(2000)
            .Select(static f => new FileInfo(f))
            .Where(static f => f.Exists && f.Length > 0)
            .OrderBy(static f => f.Length)
            .ToList();
        if (files.Count == 0)
        {
            return [];
        }

        var picks = new List<string>();
        void Add(FileInfo? f)
        {
            if (f is not null && picks.Count < maxFiles && !picks.Contains(f.FullName))
            {
                picks.Add(f.FullName);
            }
        }

        Add(files.First());
        Add(files[files.Count / 2]);
        Add(files.FirstOrDefault(static f => Math.Abs(f.Length - 750 * 1024) < 50 * 1024));
        Add(files[(int)(files.Count * 0.95)]);
        Add(files[(int)(files.Count * 0.99)]);
        Add(files.Last());
        foreach (var f in files.Where(static x =>
                     {
                         try
                         {
                             var head = File.ReadAllBytes(x.FullName).AsSpan(0, Math.Min(4096, (int)x.Length));
                             return head.IndexOf("\r\n."u8) >= 0;
                         }
                         // codeql[cs/catch-of-all-exceptions] Test process-boundary helper must tolerate any failure while releasing resources.
                         catch (Exception ex)
                         {
                             _ = ex.GetType(); // observed: unreadable sample skipped
                             return false;
                         }
                     }).Take(2))
        {
            Add(f);
        }

        return picks;
    }

    private sealed class ArticleTxHarness : IAsyncDisposable
    {
        private readonly Pipe _pipe;
        private readonly MemoryStream _capture = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _drainTask;
        private int _disposed;

        private ArticleTxHarness(Pipe pipe, NntpResponseWriter writer)
        {
            _pipe = pipe;
            Writer = writer;
            _drainTask = DrainLoopAsync(_cts.Token);
        }

        public NntpResponseWriter Writer { get; }

        public static Task<ArticleTxHarness> CreateAsync()
        {
            var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
            var writer = new NntpResponseWriter(pipe.Writer);
            return Task.FromResult(new ArticleTxHarness(pipe, writer));
        }

        public async Task<byte[]> DrainAllAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            await Writer.DisposeAsync().ConfigureAwait(false);
            await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
            await _drainTask.ConfigureAwait(false);

            lock (_capture)
            {
                return _capture.ToArray();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    await Writer.DisposeAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException ex)
                {
                    _ = ex.GetType(); // observed: expected during test teardown/cleanup
                }
            }

            await _cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Test duplex teardown must complete pipes after any protocol or cancel fault.
            catch (Exception ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
                // ignore
            }

            try
            {
                await _drainTask.ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Test duplex teardown must complete pipes after any protocol or cancel fault.
            catch (Exception ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
                // ignore
            }

            _cts.Dispose();
        }

        private async Task DrainLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var result = await _pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    var buffer = result.Buffer;
                    foreach (var segment in buffer)
                    {
                        lock (_capture)
                        {
                            _capture.Write(segment.Span);
                        }
                    }

                    _pipe.Reader.AdvanceTo(buffer.End);
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }
            finally
            {
                await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
            }
        }
    }
}
