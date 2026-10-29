using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Session.Framing;
using Xunit;

namespace VectorNNTPD.TransitService.Tests.Session.Commands;

/// <summary>
/// ARTICLE/BODY wire shapes on the shared TX path from externally supplied destuffed bytes.
/// Command handlers return RFC lookup-failure codes; storage/catalog is still out of scope.
/// </summary>
public sealed class NntpArticleBodyTxIntegrationTests
{
    private static readonly string IncomingRoot = @"C:\Temp\Incoming";

    [Fact]
    public async Task Article_Success_FramingAndRestuff()
    {
        await using var harness = await TxHarness.CreateAsync();
        var stored = Encoding.ASCII.GetBytes("Subject: t\r\n\r\nhello\r\n");
        await harness.Writer.WriteCustomerArticleAsync(stored, "<a@b>");
        var wire = await harness.DrainAllAsync();
        Assert.Equal(BuildArticleOracle(stored, "<a@b>", 0), wire);
        Assert.StartsWith("220 0 <a@b>\r\n", Encoding.ASCII.GetString(wire), StringComparison.Ordinal);
        Assert.EndsWith(".\r\n", Encoding.ASCII.GetString(wire), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Article_WithArticleNumber_InStatusLine()
    {
        await using var harness = await TxHarness.CreateAsync();
        var stored = "Subject: x\r\n\r\nbody\r\n"u8.ToArray();
        await harness.Writer.WriteCustomerArticleAsync(stored, "<n@id>", articleNumber: 3000234);
        var wire = Encoding.ASCII.GetString(await harness.DrainAllAsync());
        Assert.StartsWith("220 3000234 <n@id>\r\n", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Head_Success_HeadersOnly()
    {
        await using var harness = await TxHarness.CreateAsync();
        var stored = Encoding.ASCII.GetBytes("Subject: t\r\nFrom: a@b\r\n\r\nbody-line\r\n");
        Assert.True(ArticleWireReconstructor.TrySplitHeadersAndBody(stored, out var headers, out _));
        await harness.Writer.WriteCustomerHeadAsync(headers.ToArray(), "<head@id>");
        var text = Encoding.ASCII.GetString(await harness.DrainAllAsync());
        Assert.StartsWith("221 0 <head@id>\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Subject: t\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("body-line", text, StringComparison.Ordinal);
        Assert.EndsWith(".\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Body_Success_BodyOnly_NoHeaders()
    {
        await using var harness = await TxHarness.CreateAsync();
        var stored = Encoding.ASCII.GetBytes("Subject: t\r\nFrom: a@b\r\n\r\nbody-line\r\n");
        await harness.Writer.WriteCustomerBodyAsync(stored, "<body@id>");
        var wire = await harness.DrainAllAsync();
        var expected = BuildBodyOracle("body-line\r\n"u8, "<body@id>", 0);
        Assert.Equal(expected, wire);
        var text = Encoding.ASCII.GetString(wire);
        Assert.StartsWith("222 0 <body@id>\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Subject:", text, StringComparison.Ordinal);
        Assert.EndsWith(".\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Body_EmptyBody_WhenNoBlankLine()
    {
        await using var harness = await TxHarness.CreateAsync();
        var stored = "Subject: only-headers\r\n"u8.ToArray();
        await harness.Writer.WriteCustomerBodyAsync(stored, "<emptybody@id>");
        Assert.Equal(BuildBodyOracle(ReadOnlySpan<byte>.Empty, "<emptybody@id>", 0), await harness.DrainAllAsync());
    }

    [Fact]
    public async Task Body_EmptyBody_AfterBlankLine()
    {
        await using var harness = await TxHarness.CreateAsync();
        var stored = "Subject: t\r\n\r\n"u8.ToArray();
        await harness.Writer.WriteCustomerBodyAsync(stored, "<blank@id>");
        Assert.Equal(BuildBodyOracle(ReadOnlySpan<byte>.Empty, "<blank@id>", 0), await harness.DrainAllAsync());
    }

    [Fact]
    public async Task Body_DotLeadingLines_Restuffed()
    {
        await using var harness = await TxHarness.CreateAsync();
        var stored = Encoding.ASCII.GetBytes("Subject: t\r\n\r\n.example\r\n..two\r\n");
        await harness.Writer.WriteCustomerBodyAsync(stored, "<dot@id>");
        Assert.Equal(BuildBodyOracle(".example\r\n..two\r\n"u8, "<dot@id>", 0), await harness.DrainAllAsync());
    }

    [Fact]
    public async Task Body_FromSeparatedBody_MatchesSplitPath()
    {
        await using var a = await TxHarness.CreateAsync();
        await using var b = await TxHarness.CreateAsync();
        var stored = Encoding.ASCII.GetBytes("H: 1\r\n\r\n.body\r\n");
        await a.Writer.WriteCustomerBodyAsync(stored, "<x@y>", 42);
        Assert.True(ArticleWireReconstructor.TrySplitHeadersAndBody(stored, out _, out var body));
        await b.Writer.WriteCustomerBodyFromBodyAsync(body.ToArray(), "<x@y>", 42);
        Assert.Equal(await a.DrainAllAsync(), await b.DrainAllAsync());
    }

    [Fact]
    public async Task ArticleAndBody_PreserveMessageId()
    {
        await using var harness = await TxHarness.CreateAsync();
        const string mid = "<unique-mid@example.com>";
        var stored = "Subject: t\r\n\r\nx\r\n"u8.ToArray();
        await harness.Writer.WriteCustomerArticleAsync(stored, mid);
        await harness.Writer.WriteCustomerBodyAsync(stored, mid, 7);
        var wire = Encoding.ASCII.GetString(await harness.DrainAllAsync());
        Assert.Contains("220 0 " + mid, wire, StringComparison.Ordinal);
        Assert.Contains("222 7 " + mid, wire, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NntpArticleTxChunkBudget.DefaultBytes)]
    [InlineData(128 * 1024)]
    [InlineData(NntpArticleTxChunkBudget.MaxBytes)]
    public async Task Article_Large_ChunkBudgets_ByteIdentical(int chunkBytes)
    {
        await using var harness = await TxHarness.CreateAsync();
        var stored = BuildStoredArticle(740 * 1024, leadingDotEvery: 40);
        var framing = NntpArticleTxFraming.CustomerArticle("<large@a>", 0);
        await harness.Writer.WriteArticleAsync(stored, framing, chunkBytes);
        var actual = await harness.DrainAllAsync();
        Assert.Equal(BuildArticleOracle(stored, "<large@a>", 0), actual);
        Assert.True(harness.Writer.ChannelEnqueueCount < 100);
        Assert.True(harness.Writer.ChannelEnqueueCount >= 2);
    }

    [Fact]
    public async Task Body_Large_UsesChunkedPath_NotPerLine()
    {
        await using var harness = await TxHarness.CreateAsync();
        var body = BuildStoredArticle(750 * 1024, leadingDotEvery: 50);
        var stored = Encoding.ASCII.GetBytes("Subject: t\r\n\r\n").Concat(body).ToArray();
        await harness.Writer.WriteCustomerBodyAsync(stored, "<largebody@id>");
        _ = await harness.DrainAllAsync();
        // ~750 KiB body → O(10) chunks, not tens of thousands of lines
        Assert.InRange(harness.Writer.ChannelEnqueueCount, 2, 64);
        Assert.InRange(harness.Writer.PipeFlushCount, 2, 128);
    }

    [Fact]
    public async Task Article_TwoMiB_IfSynthetic()
    {
        await using var harness = await TxHarness.CreateAsync();
        var stored = BuildStoredArticle(2 * 1024 * 1024, leadingDotEvery: 80);
        await harness.Writer.WriteCustomerArticleAsync(stored, "<2m@id>");
        Assert.Equal(BuildArticleOracle(stored, "<2m@id>", 0), await harness.DrainAllAsync());
    }

    [Fact]
    public async Task ConsecutiveArticles_RemainOrdered()
    {
        await using var harness = await TxHarness.CreateAsync();
        var a = "Subject: a\r\n\r\nA\r\n"u8.ToArray();
        var b = "Subject: b\r\n\r\n.B\r\n"u8.ToArray();
        await harness.Writer.WriteCustomerArticleAsync(a, "<a@1>", 1);
        await harness.Writer.WriteCustomerArticleAsync(b, "<b@2>", 2);
        var expected = Concat(
            BuildArticleOracle(a, "<a@1>", 1),
            BuildArticleOracle(b, "<b@2>", 2));
        Assert.Equal(expected, await harness.DrainAllAsync());
    }

    [Fact]
    public async Task ConsecutiveBodies_RemainOrdered()
    {
        await using var harness = await TxHarness.CreateAsync();
        var a = "H: a\r\n\r\na\r\n"u8.ToArray();
        var b = "H: b\r\n\r\n.b\r\n"u8.ToArray();
        await harness.Writer.WriteCustomerBodyAsync(a, "<a@1>");
        await harness.Writer.WriteCustomerBodyAsync(b, "<b@2>", 9);
        var expected = Concat(
            BuildBodyOracle("a\r\n"u8, "<a@1>", 0),
            BuildBodyOracle(".b\r\n"u8, "<b@2>", 9));
        Assert.Equal(expected, await harness.DrainAllAsync());
    }

    [Fact]
    public async Task Cancellation_DuringLargeArticle()
    {
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 4 * 1024,
            resumeWriterThreshold: 2 * 1024,
            useSynchronizationContext: false));
        await using var writer = new NntpResponseWriter(pipe.Writer, channelCapacity: 1);
        var stored = BuildStoredArticle(300 * 1024, 0);
        using var cts = new CancellationTokenSource();
        var task = writer.WriteCustomerArticleAsync(stored, "<c@x>", cancellationToken: cts.Token);
        await Task.Delay(20);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        pipe.Reader.CancelPendingRead();
        await pipe.Reader.CompleteAsync();
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task Corpus_ArticleAndBody_IfPresent()
    {
        if (!Directory.Exists(IncomingRoot))
        {
            return;
        }

        var file = Directory.EnumerateFiles(IncomingRoot, "*", SearchOption.AllDirectories)
            .Select(static f => new FileInfo(f))
            .Where(static f => f.Exists && f.Length is > 1024 and < 2_000_000)
            .OrderBy(static f => Math.Abs(f.Length - 750 * 1024))
            .Select(static f => f.FullName)
            .FirstOrDefault();
        if (file is null)
        {
            return;
        }

        var stored = await File.ReadAllBytesAsync(file);
        await using var articleHarness = await TxHarness.CreateAsync();
        await articleHarness.Writer.WriteCustomerArticleAsync(stored, "<corpus@a>");
        Assert.Equal(BuildArticleOracle(stored, "<corpus@a>", 0), await articleHarness.DrainAllAsync());

        await using var bodyHarness = await TxHarness.CreateAsync();
        await bodyHarness.Writer.WriteCustomerBodyAsync(stored, "<corpus@b>");
        ArticleWireReconstructor.TrySplitHeadersAndBody(stored, out _, out var body);
        Assert.Equal(BuildBodyOracle(body, "<corpus@b>", 0), await bodyHarness.DrainAllAsync());
    }

    [Fact]
    public void ArticleHandlers_WireMessageIdThroughWriterHelpers()
    {
        var src = File.ReadAllText(
            Path.Join(
                FindRepoRoot(),
                "src",
                "VectorNNTPD.TransitService",
                "Session",
                "Commands",
                "Article.cs"));
        Assert.DoesNotContain("NntpCommandNotImplemented", src, StringComparison.Ordinal);
        Assert.Contains("NoArticleWithMessageId", src, StringComparison.Ordinal);
        Assert.Contains("NoArticleWithNumber", src, StringComparison.Ordinal);
        Assert.Contains("NoNewsgroupSelected", src, StringComparison.Ordinal);
        Assert.Contains("CurrentArticleNumberInvalid", src, StringComparison.Ordinal);
        Assert.Contains("WriteCustomerArticleAsync", src, StringComparison.Ordinal);
        Assert.Contains("WriteCustomerHeadAsync", src, StringComparison.Ordinal);
        Assert.Contains("WriteCustomerBodyFromBodyAsync", src, StringComparison.Ordinal);
        Assert.Contains("WriteCustomerStatAsync", src, StringComparison.Ordinal);
        Assert.Contains("IVatpArticleClient", src, StringComparison.Ordinal);
        Assert.Contains("InboundArticleProducer.BackFiller", src, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitHeadersAndBody_FindsBlankLine()
    {
        var stored = "A: 1\r\nB: 2\r\n\r\n.body\r\n"u8;
        Assert.True(ArticleWireReconstructor.TrySplitHeadersAndBody(stored, out var headers, out var body));
        Assert.Equal("A: 1\r\nB: 2\r\n\r\n"u8.ToArray(), headers.ToArray());
        Assert.Equal(".body\r\n"u8.ToArray(), body.ToArray());
    }

    private static byte[] BuildArticleOracle(ReadOnlySpan<byte> stored, string mid, long number)
    {
        var framing = NntpArticleTxFraming.CustomerArticle(mid, number);
        var header = framing.BuildHeaderBytes();
        var body = ArticleWireReconstructor.RestuffArticle(stored);
        return Concat(header, body);
    }

    private static byte[] BuildBodyOracle(ReadOnlySpan<byte> storedBody, string mid, long number)
    {
        var framing = NntpArticleTxFraming.CustomerBody(mid, number);
        var header = framing.BuildHeaderBytes();
        var body = ArticleWireReconstructor.RestuffArticle(storedBody);
        return Concat(header, body);
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

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result.AsSpan());
        b.CopyTo(result.AsSpan(a.Length));
        return result;
    }

    private static byte[] BuildStoredArticle(int sizeHint, int leadingDotEvery)
    {
        using var ms = new MemoryStream(sizeHint + 256);
        ms.Write("Subject: synthetic\r\n\r\n"u8);
        var n = 0;
        while (ms.Length < sizeHint)
        {
            if (leadingDotEvery > 0 && n % leadingDotEvery == 0)
            {
                ms.Write("."u8);
            }

            ms.Write(Encoding.ASCII.GetBytes($"line-{n}-xxxxxxxx\r\n"));
            n++;
        }

        return ms.ToArray();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Join(dir.FullName, "VectorNNTPD.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class TxHarness : IAsyncDisposable
    {
        private readonly Pipe _pipe;
        private readonly MemoryStream _capture = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _drainTask;
        private int _disposed;

        private TxHarness(Pipe pipe, NntpResponseWriter writer)
        {
            _pipe = pipe;
            Writer = writer;
            _drainTask = DrainLoopAsync(_cts.Token);
        }

        public NntpResponseWriter Writer { get; }

        public static Task<TxHarness> CreateAsync()
        {
            var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
            return Task.FromResult(new TxHarness(pipe, new NntpResponseWriter(pipe.Writer)));
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
            // codeql[cs/catch-of-all-exceptions] Best-effort test cleanup must tolerate any filesystem or disposal failure.
            catch (Exception ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
                // ignore
            }

            try
            {
                await _drainTask.ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Best-effort test cleanup must tolerate any filesystem or disposal failure.
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
