using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Tests.Fixtures;

namespace VectorNNTPD.TransitService.Tests.ArticleIngestion;

public sealed class ArticleIngestionQueueTests
{
    [Fact]
    public void DefaultMemoryLimit_IsOneGibibyte()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions());
        Assert.Equal(NntpdOptions.DefaultTransitQueueMemoryLimit, queue.MemoryLimitBytes);
        Assert.Equal(1_073_741_824L, queue.MemoryLimitBytes);
    }

    [Fact]
    public void ConfiguredMemoryLimit_IsHonoured()
    {
        var options = new NntpdOptions
        {
            TransitQueueMemoryLimit = 4096,
            ArticleIngestion = new ArticleIngestionOptions(),
        };
        var queue = new ArticleIngestionQueue(Options.Create(options));
        Assert.Equal(4096, queue.MemoryLimitBytes);
    }

    [Fact]
    public async Task ArticleSmallerThanBudget_IsAdmitted()
    {
        var article = Article("<small@ex.com>", InboundArticleProducer.IHave);
        var queue = CreateQueue(article.Payload.Length + 16);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(article, CancellationToken.None));
        Assert.Equal(article.Payload.Length, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);
        Assert.Equal(ArticleParseStatus.CanonicalV1, article.Record.ParseStatus);
    }

    [Fact]
    public async Task MultipleArticles_ConsumeBudget()
    {
        var a = Article("<a@ex.com>");
        var b = Article("<b@ex.com>");
        var c = Article("<c@ex.com>");
        var total = a.Payload.Length + b.Payload.Length + c.Payload.Length;
        var queue = CreateQueue(total);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(a, CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(b, CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(c, CancellationToken.None));
        Assert.Equal(total, queue.QueuedBytes);
        Assert.Equal(3, queue.Count);
        Assert.Equal(total, queue.PeakQueuedBytes);
        Assert.Equal(3, queue.PeakCount);
    }

    [Fact]
    public async Task Admission_BlocksWhenBudgetExhausted_AndResumesAfterRelease()
    {
        var full = Article("<full@ex.com>");
        var wait = Article("<wait@ex.com>");
        var queue = CreateQueue(full.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(full, CancellationToken.None));

        var blocked = queue.EnqueueAsync(wait, CancellationToken.None);
        await WaitForWaitersAsync(queue, 1);

        Assert.False(blocked.IsCompleted);
        Assert.Equal(full.Payload.Length, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);

        var first = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("<full@ex.com>", first!.MessageId);

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleEnqueueResult.Accepted, await blocked.AsTask().WaitAsync(safety.Token));
        Assert.Equal(wait.Payload.Length, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);
        Assert.Equal("<wait@ex.com>", (await queue.DequeueAsync(CancellationToken.None))!.MessageId);
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task ConcurrentProducers_CannotOversubscribeBudget()
    {
        const int producers = 16;
        const int admitted = 4;
        var sample = Article("<x00@c>");
        var size = sample.Payload.Length;
        var limit = size * admitted;
        var queue = CreateQueue(limit);
        var results = new ArticleEnqueueResult[producers];
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task[producers];
        for (var i = 0; i < producers; i++)
        {
            var index = i;
            tasks[index] = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                results[index] = await queue
                    .EnqueueAsync(Article($"<x{index:D2}@c>"), CancellationToken.None)
                    .ConfigureAwait(false);
            });
        }

        start.SetResult();

        await WaitUntilAsync(
            () => queue.Count == admitted && queue.DebugWaiterCount == producers - admitted,
            TimeSpan.FromSeconds(5));

        Assert.Equal(limit, queue.QueuedBytes);
        Assert.Equal(admitted, queue.Count);
        Assert.True(queue.QueuedBytes <= limit);
        Assert.Equal(admitted, tasks.Count(static t => t.IsCompleted));

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (var i = 0; i < producers; i++)
        {
            Assert.NotNull(await queue.DequeueAsync(safety.Token));
        }

        await Task.WhenAll(tasks).WaitAsync(safety.Token);
        Assert.All(results, static r => Assert.Equal(ArticleEnqueueResult.Accepted, r));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);
    }

    [Fact]
    public async Task ExactBoundaryArticle_Fits()
    {
        var edge = Article("<edge@ex.com>");
        var queue = CreateQueue(edge.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(edge, CancellationToken.None));
        Assert.Equal(edge.Payload.Length, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);
        Assert.False(queue.TryProbeCapacity());
    }

    [Fact]
    public async Task ArticleExceedingBudget_IsRejected()
    {
        var big = Article("<big@ex.com>");
        var queue = CreateQueue(big.Payload.Length - 1);
        Assert.Equal(ArticleEnqueueResult.Rejected, await queue.EnqueueAsync(big, CancellationToken.None));
        Assert.False(queue.TryEnqueue(Article("<big2@ex.com>")));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);
    }

    [Fact]
    public async Task FailedEnqueueAfterReservation_ReleasesBytes()
    {
        var article = Article("<fail@ex.com>");
        var queue = CreateQueue(article.Payload.Length + 16);
        queue.CompleteChannelWithoutMarkingUnavailableForTests();
        Assert.Equal(ArticleEnqueueResult.Unavailable, await queue.EnqueueAsync(article, CancellationToken.None));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Cancellation_ReleasesReservation()
    {
        var held = Article("<held01@ex.com>");
        var cancel = Article("<wait01@ex.com>");
        var queue = CreateQueue(held.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(held, CancellationToken.None));

        using var cts = new CancellationTokenSource();
        var blocked = queue.EnqueueAsync(cancel, cts.Token);
        await WaitForWaitersAsync(queue, 1);
        cts.Cancel();

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleEnqueueResult.Unavailable, await blocked.AsTask().WaitAsync(safety.Token));
        Assert.Equal(held.Payload.Length, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);

        Assert.Equal("<held01@ex.com>", (await queue.DequeueAsync(CancellationToken.None))!.MessageId);
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task ShutdownDrain_DoesNotLeakReservations()
    {
        var a = Article("<a@ex.com>");
        var b = Article("<b@ex.com>");
        var c = Article("<c@ex.com>");
        var queue = CreateQueue(a.Payload.Length + b.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(a, CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(b, CancellationToken.None));

        var blocked = queue.EnqueueAsync(c, CancellationToken.None);
        await WaitForWaitersAsync(queue, 1);

        queue.Complete();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleEnqueueResult.Unavailable, await blocked.AsTask().WaitAsync(safety.Token));
        Assert.Equal(ArticleEnqueueResult.Unavailable, await queue.EnqueueAsync(Article("<d@ex.com>"), CancellationToken.None));

        Assert.Equal("<a@ex.com>", (await queue.DequeueAsync(CancellationToken.None))!.MessageId);
        Assert.Equal("<b@ex.com>", (await queue.DequeueAsync(CancellationToken.None))!.MessageId);
        Assert.Null(await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);
        Assert.False(queue.IsAccepting);
    }

    [Fact]
    public async Task QueuedBytes_ReturnToZeroAfterConsume()
    {
        var a = Article("<a@ex.com>");
        var b = Article("<b@ex.com>");
        var total = a.Payload.Length + b.Payload.Length;
        var queue = CreateQueue(total);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(a, CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(b, CancellationToken.None));
        Assert.Equal(total, queue.QueuedBytes);
        Assert.NotNull(await queue.DequeueAsync(CancellationToken.None));
        Assert.NotNull(await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(total, queue.PeakQueuedBytes);
    }

    [Fact]
    public void TryProbeCapacity_IsFalseWhenBudgetIsExhausted()
    {
        var full = Article("<full@ex.com>");
        var queue = CreateQueue(full.Payload.Length);
        Assert.True(queue.TryProbeCapacity());
        Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(full));
        Assert.False(queue.TryProbeCapacity());
        Assert.Equal(ArticleEnqueueResult.Full, queue.TryAdmit(Article("<next@ex.com>")));
        Assert.Equal(full.Payload.Length, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void TryAdmit_DoesNotWait_WhenBudgetIsExhausted()
    {
        var held = Article("<held01@ex.com>");
        var queue = CreateQueue(held.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(held));
        var started = Environment.TickCount64;
        Assert.Equal(ArticleEnqueueResult.Full, queue.TryAdmit(Article("<wait01@ex.com>")));
        Assert.True(Environment.TickCount64 - started < 250);
        Assert.Equal(0, queue.DebugWaiterCount);
        Assert.Equal(held.Payload.Length, queue.QueuedBytes);
    }

    [Fact]
    public void TryAdmit_RejectedArticle_DoesNotReserve()
    {
        var big = Article("<big@ex.com>");
        var queue = CreateQueue(big.Payload.Length - 1);
        Assert.Equal(ArticleEnqueueResult.Rejected, queue.TryAdmit(big));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.True(queue.TryProbeCapacity());
    }

    [Fact]
    public void TryAdmit_ConcurrentProducers_CannotOversubscribe()
    {
        const int producers = 16;
        const int admitted = 4;
        var sample = Article("<x00@c>");
        var size = sample.Payload.Length;
        var limit = size * admitted;
        var queue = CreateQueue(limit);
        var results = new ArticleEnqueueResult[producers];
        Parallel.For(0, producers, i =>
        {
            results[i] = queue.TryAdmit(Article($"<x{i:D2}@c>"));
        });

        Assert.Equal(admitted, results.Count(static r => r == ArticleEnqueueResult.Accepted));
        Assert.Equal(producers - admitted, results.Count(static r => r == ArticleEnqueueResult.Full));
        Assert.Equal(limit, queue.QueuedBytes);
        Assert.Equal(admitted, queue.Count);
        Assert.True(queue.QueuedBytes <= limit);
    }

    [Fact]
    public void TryAdmit_RejectedAndFull_IncrementAdmissionFailures()
    {
        var fit = Article("<fit01@ex.com>");
        var huge = Article("<huge01@ex.com>", body: "body\r\n" + new string('Z', 80) + "\r\n");
        Assert.True(huge.Payload.Length > fit.Payload.Length);
        var queue = CreateQueue(fit.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Rejected, queue.TryAdmit(huge));
        Assert.Equal(1, queue.AdmissionFailureCount);

        Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(fit));
        Assert.Equal(ArticleEnqueueResult.Full, queue.TryAdmit(Article("<nxt01@ex.com>")));
        Assert.Equal(2, queue.AdmissionFailureCount);
        Assert.Equal(0, queue.AdmissionWaitTicks);
    }

    [Fact]
    public async Task Dequeue_IsFifo()
    {
        var sample = Article("<0@fifo>");
        var queue = CreateQueue(sample.Payload.Length * 20);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(Article($"<{i}@fifo>")));
        }

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal($"<{i}@fifo>", (await queue.DequeueAsync(CancellationToken.None))!.MessageId);
        }

        queue.Complete();
        Assert.Null(await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task ProducerIdentityAndOrder_RemainIntact()
    {
        var first = Article("<ihave@ex.com>", InboundArticleProducer.IHave);
        var second = Article("<takethis@ex.com>");
        var queue = CreateQueue(first.Payload.Length + second.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(first, CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(second, CancellationToken.None));

        var dequeuedFirst = await queue.DequeueAsync(CancellationToken.None);
        var dequeuedSecond = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("<ihave@ex.com>", dequeuedFirst!.MessageId);
        Assert.Equal(InboundArticleProducer.IHave, dequeuedFirst.Producer);
        Assert.Equal(ArticleParseStatus.CanonicalV1, dequeuedFirst.Record.ParseStatus);
        Assert.Equal("<takethis@ex.com>", dequeuedSecond!.MessageId);
        Assert.Equal(InboundArticleProducer.TakeThis, dequeuedSecond.Producer);
        Assert.Equal(ArticleParseStatus.CanonicalV1, dequeuedSecond.Record.ParseStatus);
        Assert.Equal(0, queue.QueuedBytes);
    }

    private static ArticleIngestionQueue CreateQueue(long memoryLimit) =>
        new(new ArticleIngestionOptions(), memoryLimit);

    private static InboundArticle Article(
        string messageId,
        InboundArticleProducer producer = InboundArticleProducer.TakeThis,
        string body = "body\r\n") =>
        CanonicalArticleText.CreateQueued(messageId, producer, body);

    private static Task WaitForWaitersAsync(ArticleIngestionQueue queue, int count) =>
        WaitUntilAsync(() => queue.DebugWaiterCount >= count, TimeSpan.FromSeconds(5));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(1, cts.Token);
        }
    }
}
