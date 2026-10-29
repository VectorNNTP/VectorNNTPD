using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Session.Framing;

namespace VectorNNTPD.ReaderService.Tests.Session.Commands;

/// <summary>
/// Phase 3: bounded STREAM outstanding-depth scheduler (no storage / no catalog).
/// </summary>
public sealed class NntpStreamArticleTxSchedulerTests
{
    [Fact]
    public void DefaultDepth_IsEight()
    {
        using var scheduler = new SyncDispose(new NntpStreamArticleTxScheduler());
        Assert.Equal(8, scheduler.Inner.Depth);
        Assert.Equal(8, NntpStreamArticleTxScheduler.DefaultDepth);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void ValidDepths_Accepted(int depth)
    {
        using var scheduler = new SyncDispose(new NntpStreamArticleTxScheduler(depth));
        Assert.Equal(depth, scheduler.Inner.Depth);
        Assert.True(NntpStreamArticleTxScheduler.IsValidDepth(depth));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(17)]
    [InlineData(64)]
    public void InvalidDepths_Rejected(int depth)
    {
        Assert.False(NntpStreamArticleTxScheduler.IsValidDepth(depth));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NntpStreamArticleTxScheduler(depth));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NntpStreamArticleTxScheduler.ThrowIfNotValidDepth(depth));
    }

    [Fact]
    public async Task AtMostN_ConcurrentlyAdmitted()
    {
        const int depth = 4;
        await using var scheduler = new NntpStreamArticleTxScheduler(depth);
        var started = new TaskCompletionSource[depth + 1];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var i = 0; i < started.Length; i++)
        {
            started[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var tasks = new Task[depth + 1];
        for (var i = 0; i < tasks.Length; i++)
        {
            var index = i;
            tasks[i] = scheduler.ExecuteAsync(
                    async ct =>
                    {
                        started[index].TrySetResult();
                        await release.Task.WaitAsync(ct).ConfigureAwait(false);
                    },
                    CancellationToken.None)
                .AsTask();
        }

        var firstWave = started.Take(depth).Select(s => s.Task);
        await Task.WhenAll(firstWave).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(depth, scheduler.Outstanding);
        Assert.False(started[depth].Task.IsCompleted);

        release.TrySetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        await started[depth].Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, scheduler.Outstanding);
    }

    [Fact]
    public async Task CompletedOperation_ReleasesSlot_ForWaiter()
    {
        const int depth = 4;
        await using var scheduler = new NntpStreamArticleTxScheduler(depth);
        var blockers = new TaskCompletionSource[depth];
        for (var i = 0; i < depth; i++)
        {
            blockers[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var holders = new Task[depth];
        for (var i = 0; i < depth; i++)
        {
            var index = i;
            holders[i] = scheduler.ExecuteAsync(
                    async ct => await blockers[index].Task.WaitAsync(ct).ConfigureAwait(false),
                    CancellationToken.None)
                .AsTask();
        }

        await WaitUntilAsync(() => scheduler.Outstanding == depth, TimeSpan.FromSeconds(5));

        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = scheduler.ExecuteAsync(
                _ =>
                {
                    admitted.TrySetResult();
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None)
            .AsTask();

        Assert.False(admitted.Task.IsCompleted);
        blockers[0].TrySetResult();
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 1; i < depth; i++)
        {
            blockers[i].TrySetResult();
        }

        await Task.WhenAll(holders.Append(waiter)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, scheduler.Outstanding);
    }

    [Fact]
    public async Task CancelWhileWaiting_DoesNotLeakSlot()
    {
        const int depth = 4;
        await using var scheduler = new NntpStreamArticleTxScheduler(depth);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holders = Enumerable.Range(0, depth)
            .Select(_ => scheduler.ExecuteAsync(
                    async ct => await hold.Task.WaitAsync(ct).ConfigureAwait(false),
                    CancellationToken.None)
                .AsTask())
            .ToArray();

        await WaitUntilAsync(() => scheduler.Outstanding == depth, TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        var waiting = scheduler.ExecuteAsync(_ => ValueTask.CompletedTask, cts.Token).AsTask();
        await WaitUntilAsync(() => scheduler.Outstanding == depth && !waiting.IsCompleted, TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(depth, scheduler.Outstanding);

        hold.TrySetResult();
        await Task.WhenAll(holders).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, scheduler.Outstanding);

        await scheduler.ExecuteAsync(_ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(0, scheduler.Outstanding);
    }

    [Fact]
    public async Task CancelAfterAcquisition_ReleasesSlot()
    {
        await using var scheduler = new NntpStreamArticleTxScheduler(4);
        using var cts = new CancellationTokenSource();
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = scheduler.ExecuteAsync(
                async ct =>
                {
                    acquired.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                },
                cts.Token)
            .AsTask();

        await acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, scheduler.Outstanding);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, scheduler.Outstanding);
    }

    [Fact]
    public async Task ExceptionDuringOperation_ReleasesSlot()
    {
        await using var scheduler = new NntpStreamArticleTxScheduler(4);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scheduler.ExecuteAsync(
                    _ => throw new InvalidOperationException("boom"),
                    CancellationToken.None)
                .AsTask());
        Assert.Equal(0, scheduler.Outstanding);

        await scheduler.ExecuteAsync(_ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(0, scheduler.Outstanding);
    }

    [Fact]
    public async Task RepeatedAcquireRelease_NeverExceedsBound()
    {
        const int depth = 4;
        await using var scheduler = new NntpStreamArticleTxScheduler(depth);
        var peak = 0;
        var tasks = Enumerable.Range(0, 40)
            .Select(_ => scheduler.ExecuteAsync(
                    _ =>
                    {
                        var o = scheduler.Outstanding;
                        while (true)
                        {
                            var observed = Volatile.Read(ref peak);
                            if (o <= observed || Interlocked.CompareExchange(ref peak, o, observed) == observed)
                            {
                                break;
                            }
                        }

                        Assert.True(o <= depth);
                        return ValueTask.CompletedTask;
                    },
                    CancellationToken.None)
                .AsTask())
            .ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(peak <= depth);
        Assert.Equal(0, scheduler.Outstanding);
    }

    [Fact]
    public async Task CancelWaiting_UnblocksWithoutAcquire()
    {
        const int depth = 4;
        await using var scheduler = new NntpStreamArticleTxScheduler(depth);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holders = Enumerable.Range(0, depth)
            .Select(_ => scheduler.ExecuteAsync(
                    async ct => await hold.Task.WaitAsync(ct).ConfigureAwait(false),
                    CancellationToken.None)
                .AsTask())
            .ToArray();

        await WaitUntilAsync(() => scheduler.Outstanding == depth, TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        var waiting = scheduler.ExecuteAsync(_ => ValueTask.CompletedTask, cts.Token).AsTask();
        await WaitUntilAsync(() => !waiting.IsCompleted, TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        hold.TrySetResult();
        await Task.WhenAll(holders).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, scheduler.Outstanding);
    }

    [Fact]
    public async Task OverlappingWriteArticle_PreservesFifoWireOrder()
    {
        var pipe = new Pipe();
        await using var writer = new NntpResponseWriter(pipe.Writer);
        await using var scheduler = new NntpStreamArticleTxScheduler(8);

        var bodies = new[]
        {
            "AAA\r\n"u8.ToArray(),
            "BBB\r\n"u8.ToArray(),
            "CCC\r\n"u8.ToArray(),
            "DDD\r\n"u8.ToArray(),
        };

        var tasks = new Task[bodies.Length];
        for (var i = 0; i < bodies.Length; i++)
        {
            var index = i;
            var framing = NntpArticleTxFraming.PeerTakeThis($"<{index}@id>");
            tasks[index] = scheduler
                .WriteArticleAsync(writer, bodies[index], framing, CancellationToken.None)
                .AsTask();
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        await writer.DisposeAsync();
        pipe.Writer.Complete();

        var buffer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var read = await pipe.Reader.ReadAsync();
            foreach (var segment in read.Buffer)
            {
                buffer.Write(segment.Span);
            }

            pipe.Reader.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
            {
                break;
            }
        }

        var wire = Encoding.ASCII.GetString(buffer.WrittenSpan);
        var expected = new StringBuilder();
        for (var i = 0; i < bodies.Length; i++)
        {
            var framing = NntpArticleTxFraming.PeerTakeThis($"<{i}@id>");
            expected.Append(Encoding.ASCII.GetString(framing.BuildHeaderBytes()));
            expected.Append(Encoding.ASCII.GetString(ArticleWireReconstructor.RestuffArticle(bodies[i])));
        }

        Assert.Equal(expected.ToString(), wire);
    }

    [Fact]
    public async Task WriteArticle_FramingRemainsByteCorrect()
    {
        var pipe = new Pipe();
        await using var writer = new NntpResponseWriter(pipe.Writer);
        await using var scheduler = new NntpStreamArticleTxScheduler(4);

        var stored = ".dot\r\nbody\r\n"u8.ToArray();
        var framing = NntpArticleTxFraming.CustomerArticle("<x@y>", 42);
        await scheduler.WriteArticleAsync(writer, stored, framing);

        await writer.DisposeAsync();
        pipe.Writer.Complete();

        var read = await pipe.Reader.ReadAsync();
        var wire = read.Buffer.ToArray();
        pipe.Reader.AdvanceTo(read.Buffer.End);

        var expected = framing.BuildHeaderBytes()
            .Concat(ArticleWireReconstructor.RestuffArticle(stored))
            .ToArray();
        Assert.Equal(expected, wire);
    }

    [Fact]
    public async Task CancelDuringBackpressuredWrite_ReleasesSlot()
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        await using var writer = new NntpResponseWriter(pipe.Writer, channelCapacity: 1);
        await using var scheduler = new NntpStreamArticleTxScheduler(4);

        using var cts = new CancellationTokenSource();
        var large = Encoding.ASCII.GetBytes(new string('x', 256 * 1024) + "\r\n");

        var write = scheduler
            .WriteArticleAsync(
                writer,
                large,
                NntpArticleTxFraming.PeerTakeThis("<c@x>"),
                cts.Token)
            .AsTask();

        await WaitUntilAsync(
            () => scheduler.Outstanding > 0 || write.IsCompleted,
            TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(0, scheduler.Outstanding);
    }

    [Fact]
    public async Task ThreeConcurrentArticles_NeverInterleaveChunks()
    {
        var pipe = new Pipe();
        await using var writer = new NntpResponseWriter(pipe.Writer);
        await using var scheduler = new NntpStreamArticleTxScheduler(8);

        var bodies = new[]
        {
            Encoding.ASCII.GetBytes("ONE\r\n" + new string('1', 3000) + "\r\n"),
            Encoding.ASCII.GetBytes("TWO\r\n" + new string('2', 3000) + "\r\n"),
            Encoding.ASCII.GetBytes("THREE\r\n" + new string('3', 3000) + "\r\n"),
        };

        var tasks = new Task[bodies.Length];
        for (var i = 0; i < bodies.Length; i++)
        {
            var index = i;
            tasks[index] = scheduler
                .WriteArticleForTestsAsync(
                    writer,
                    bodies[index],
                    NntpArticleTxFraming.PeerTakeThis($"<{index}@id>"),
                    chunkBytes: 1024,
                    CancellationToken.None)
                .AsTask();
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        var wire = await DrainPipeAsync(pipe, writer);
        Assert.Equal(BuildOracle(bodies), wire);
    }

    [Fact]
    public async Task Preparation_CanOverlap_UnderDepth()
    {
        var pipe = new Pipe();
        await using var writer = new NntpResponseWriter(pipe.Writer);
        await using var scheduler = new NntpStreamArticleTxScheduler(4);

        var afterPrepare = new TaskCompletionSource[4];
        var releasePrepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var i = 0; i < 4; i++)
        {
            afterPrepare[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var seen = 0;
        scheduler.TestAfterPrepare = async ct =>
        {
            var n = Interlocked.Increment(ref seen);
            afterPrepare[n - 1].TrySetResult();
            await releasePrepare.Task.WaitAsync(ct).ConfigureAwait(false);
        };

        var bodies = Enumerable.Range(0, 4)
            .Select(i => Encoding.ASCII.GetBytes($"P{i}\r\n"))
            .ToArray();
        var tasks = new Task[4];
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            tasks[index] = scheduler
                .WriteArticleAsync(writer, bodies[index], NntpArticleTxFraming.PeerTakeThis($"<p{index}@id>"))
                .AsTask();
        }

        await Task.WhenAll(afterPrepare.Select(t => t.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(scheduler.PeakPreparing >= 2);
        Assert.Equal(4, scheduler.Outstanding);

        releasePrepare.TrySetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, scheduler.Outstanding);
    }

    [Fact]
    public async Task Enqueue_RemainsArticleAtomic_UnderSlowConsumer()
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        await using var writer = new NntpResponseWriter(pipe.Writer, channelCapacity: 2);
        await using var scheduler = new NntpStreamArticleTxScheduler(8);

        var capture = new ArrayBufferWriter<byte>();
        using var drainCts = new CancellationTokenSource();
        var drain = DrainSlowCapturingAsync(pipe, capture, drainCts.Token);

        var bodies = new[]
        {
            Encoding.ASCII.GetBytes("AAAA\r\n" + new string('a', 20_000) + "\r\n"),
            Encoding.ASCII.GetBytes("BBBB\r\n" + new string('b', 20_000) + "\r\n"),
            Encoding.ASCII.GetBytes("CCCC\r\n" + new string('c', 20_000) + "\r\n"),
        };

        var tasks = new Task[bodies.Length];
        for (var i = 0; i < bodies.Length; i++)
        {
            var index = i;
            tasks[index] = scheduler
                .WriteArticleForTestsAsync(
                    writer,
                    bodies[index],
                    NntpArticleTxFraming.PeerTakeThis($"<{index}@id>"),
                    chunkBytes: 4096,
                    CancellationToken.None)
                .AsTask();
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        await writer.DisposeAsync();
        pipe.Writer.Complete();
        await drainCts.CancelAsync();
        try
        {
            await drain.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }

        // Finish draining any remaining bytes after cancel.
        while (true)
        {
            var read = await pipe.Reader.ReadAsync();
            foreach (var segment in read.Buffer)
            {
                capture.Write(segment.Span);
            }

            pipe.Reader.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
            {
                break;
            }
        }

        Assert.Equal(0, scheduler.Enqueuing);
        Assert.Equal(0, scheduler.Outstanding);
        Assert.Equal(BuildOracle(bodies), Encoding.ASCII.GetString(capture.WrittenSpan));
    }

    [Fact]
    public async Task SlowConsumer_DoesNotLeakDepthPermits()
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        await using var writer = new NntpResponseWriter(pipe.Writer, channelCapacity: 1);
        await using var scheduler = new NntpStreamArticleTxScheduler(4);

        var drain = DrainInBackgroundAsync(pipe);

        var body = Encoding.ASCII.GetBytes(new string('z', 80_000) + "\r\n");
        var tasks = Enumerable.Range(0, 8)
            .Select(i => scheduler
                .WriteArticleForTestsAsync(
                    writer,
                    body,
                    NntpArticleTxFraming.PeerTakeThis($"<z{i}@id>"),
                    chunkBytes: 8192)
                .AsTask())
            .ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, scheduler.Outstanding);

        await writer.DisposeAsync();
        pipe.Writer.Complete();
        await drain;
    }

    [Fact]
    public async Task CancelDuringPreparation_ReleasesDepthPermit()
    {
        var pipe = new Pipe();
        await using var writer = new NntpResponseWriter(pipe.Writer);
        await using var scheduler = new NntpStreamArticleTxScheduler(4);

        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.TestAfterPrepare = async ct =>
        {
            prepared.TrySetResult();
            await hold.Task.WaitAsync(ct).ConfigureAwait(false);
        };

        using var cts = new CancellationTokenSource();
        var write = scheduler
            .WriteArticleAsync(
                writer,
                "body\r\n"u8.ToArray(),
                NntpArticleTxFraming.PeerTakeThis("<prep@id>"),
                cts.Token)
            .AsTask();

        await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, scheduler.Outstanding);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(0, scheduler.Outstanding);
        hold.TrySetCanceled();
    }

    [Fact]
    public async Task CancelWhileWaitingForEnqueueGate_ReleasesDepthPermit()
    {
        var pipe = new Pipe();
        await using var writer = new NntpResponseWriter(pipe.Writer);
        await using var scheduler = new NntpStreamArticleTxScheduler(4);

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.TestWhileHoldingEnqueueGate = async ct =>
        {
            holding.TrySetResult();
            await releaseGate.Task.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        };

        var holder = scheduler
            .WriteArticleAsync(
                writer,
                "hold\r\n"u8.ToArray(),
                NntpArticleTxFraming.PeerTakeThis("<hold@id>"))
            .AsTask();

        await holding.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, scheduler.Enqueuing);

        using var cts = new CancellationTokenSource();
        var waiter = scheduler
            .WriteArticleAsync(
                writer,
                "wait\r\n"u8.ToArray(),
                NntpArticleTxFraming.PeerTakeThis("<wait@id>"),
                cts.Token)
            .AsTask();

        await WaitUntilAsync(() => scheduler.Outstanding == 2, TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Equal(1, scheduler.Outstanding);

        releaseGate.TrySetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, scheduler.Outstanding);
    }

    [Fact]
    public async Task CancelWhileAwaitingTxFlush_ReleasesDepthPermit()
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        await using var writer = new NntpResponseWriter(pipe.Writer, channelCapacity: 1);
        await using var scheduler = new NntpStreamArticleTxScheduler(4);

        var enteredFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.TestBeforeAwaitFlush = async ct =>
        {
            enteredFlush.TrySetResult();
            await holdFlush.Task.WaitAsync(ct).ConfigureAwait(false);
        };

        using var cts = new CancellationTokenSource();
        var large = Encoding.ASCII.GetBytes(new string('q', 128 * 1024) + "\r\n");
        var write = scheduler
            .WriteArticleForTestsAsync(
                writer,
                large,
                NntpArticleTxFraming.PeerTakeThis("<flush@id>"),
                chunkBytes: 4096,
                cts.Token)
            .AsTask();

        // Drain so enqueue can complete and reach the flush-await hook.
        var drain = DrainInBackgroundAsync(pipe);
        await enteredFlush.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, scheduler.Outstanding);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(0, scheduler.Outstanding);
        holdFlush.TrySetCanceled();
        await writer.DisposeAsync();
        pipe.Writer.Complete();
        try
        {
            await drain;
        }
        // codeql[cs/catch-of-all-exceptions] Best-effort test cleanup must tolerate any filesystem or disposal failure.
        catch (Exception ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
    }

    [Fact]
    public async Task ExceptionDuringEnqueue_ReleasesDepthAndGate()
    {
        var pipe = new Pipe();
        var writer = new NntpResponseWriter(pipe.Writer);
        await using var scheduler = new NntpStreamArticleTxScheduler(4);

        await writer.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            scheduler
                .WriteArticleAsync(
                    writer,
                    "x\r\n"u8.ToArray(),
                    NntpArticleTxFraming.PeerTakeThis("<fail@id>"))
                .AsTask());

        Assert.Equal(0, scheduler.Outstanding);
        Assert.Equal(0, scheduler.Enqueuing);
        pipe.Writer.Complete();
    }

    [Fact]
    public async Task EnqueueGate_NeverAllowsConcurrentEnqueuers()
    {
        var pipe = new Pipe();
        await using var writer = new NntpResponseWriter(pipe.Writer);
        await using var scheduler = new NntpStreamArticleTxScheduler(8);

        var peakEnq = 0;
        scheduler.TestWhileHoldingEnqueueGate = _ =>
        {
            var e = scheduler.Enqueuing;
            while (true)
            {
                var observed = Volatile.Read(ref peakEnq);
                if (e <= observed || Interlocked.CompareExchange(ref peakEnq, e, observed) == observed)
                {
                    break;
                }
            }

            Assert.Equal(1, e);
            return ValueTask.CompletedTask;
        };

        var tasks = Enumerable.Range(0, 8)
            .Select(i => scheduler
                .WriteArticleAsync(
                    writer,
                    Encoding.ASCII.GetBytes($"E{i}\r\n"),
                    NntpArticleTxFraming.PeerTakeThis($"<e{i}@id>"))
                .AsTask())
            .ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, peakEnq);
    }

    private static string BuildOracle(IReadOnlyList<byte[]> bodies)
    {
        var expected = new StringBuilder();
        for (var i = 0; i < bodies.Count; i++)
        {
            var framing = NntpArticleTxFraming.PeerTakeThis($"<{i}@id>");
            expected.Append(Encoding.ASCII.GetString(framing.BuildHeaderBytes()));
            expected.Append(Encoding.ASCII.GetString(ArticleWireReconstructor.RestuffArticle(bodies[i])));
        }

        return expected.ToString();
    }

    private static async Task<string> DrainPipeAsync(Pipe pipe, NntpResponseWriter writer)
    {
        await writer.DisposeAsync();
        pipe.Writer.Complete();
        var buffer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var read = await pipe.Reader.ReadAsync();
            foreach (var segment in read.Buffer)
            {
                buffer.Write(segment.Span);
            }

            pipe.Reader.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer.WrittenSpan);
    }

    private static async Task DrainSlowCapturingAsync(
        Pipe pipe,
        ArrayBufferWriter<byte> capture,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ReadResult read;
            try
            {
                read = await pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!read.Buffer.IsEmpty)
            {
                var take = Math.Min(4096, (int)read.Buffer.Length);
                var examined = read.Buffer.Slice(0, take);
                foreach (var segment in examined)
                {
                    capture.Write(segment.Span);
                }

                pipe.Reader.AdvanceTo(examined.End, read.Buffer.End);
            }
            else
            {
                pipe.Reader.AdvanceTo(read.Buffer.End);
            }

            if (read.IsCompleted)
            {
                break;
            }

            await Task.Yield();
        }
    }

    private static async Task DrainInBackgroundAsync(Pipe pipe)
    {
        try
        {
            while (true)
            {
                var read = await pipe.Reader.ReadAsync().ConfigureAwait(false);
                pipe.Reader.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
            // Reader completed.
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeout.TotalMilliseconds)
            {
                throw new TimeoutException("Condition not met within timeout.");
            }

            await Task.Yield();
        }
    }

    /// <summary>Sync dispose wrapper for constructor-only tests.</summary>
    private sealed class SyncDispose : IDisposable
    {
        public SyncDispose(NntpStreamArticleTxScheduler inner) => Inner = inner;

        public NntpStreamArticleTxScheduler Inner { get; }

        public void Dispose() => Inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
