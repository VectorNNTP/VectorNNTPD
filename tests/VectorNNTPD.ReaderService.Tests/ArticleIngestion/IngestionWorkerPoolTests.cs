using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Diagnostics;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.ArticleIngestion;

/// <summary>Ingestion worker-pool scaling and lifecycle contracts.</summary>
public sealed class IngestionWorkerPoolTests
{
    [Fact]
    public async Task Pool_StartsAtMinimumWorkers_AndNeverExceedsMaximum()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions(), transitQueueMemoryLimit: 64 * 1024);
        var options = new ArticleIngestionOptions
        {
            MinWorkers = 2,
            MaxWorkers = 3,
            ScaleUpPressureThreshold = 0.5,
            ScaleDownPressureThreshold = 0.1,
            ScaleUpConsecutiveIntervals = 1,
            ScaleDownConsecutiveIntervals = 1,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new IngestionWorkerPool(
            queue,
            static (_, _, _) => Task.CompletedTask,
            options,
            NullLogger.Instance,
            samplePressure: static () => new IngestionPressureSnapshot(0, 0, 1, 0, 0));

        var run = pool.RunAsync(cts.Token);
        await WaitUntilAsync(() => pool.WorkerCount == 2, TimeSpan.FromSeconds(2));
        Assert.Equal(2, pool.WorkerCount);

        pool.ApplyScaleDecisionForTests(
            new IngestionPressureSnapshot(1.0, 1, 1, 10, 1),
            cts.Token);
        Assert.Equal(3, pool.WorkerCount);

        pool.ApplyScaleDecisionForTests(
            new IngestionPressureSnapshot(1.0, 1, 1, 10, 1),
            cts.Token);
        Assert.Equal(3, pool.WorkerCount);

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, pool.WorkerCount);
    }

    [Fact]
    public async Task Pool_ScalesDownOnlyAfterSustainedLowPressure_AndNotBelowMinimum()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions(), transitQueueMemoryLimit: 64 * 1024);
        var options = new ArticleIngestionOptions
        {
            MinWorkers = 1,
            MaxWorkers = 3,
            ScaleUpPressureThreshold = 0.5,
            ScaleDownPressureThreshold = 0.2,
            ScaleUpConsecutiveIntervals = 1,
            ScaleDownConsecutiveIntervals = 2,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new IngestionWorkerPool(
            queue,
            static (_, _, _) => Task.CompletedTask,
            options,
            NullLogger.Instance);

        var run = pool.RunAsync(cts.Token);
        await WaitUntilAsync(() => pool.WorkerCount == 1, TimeSpan.FromSeconds(2));

        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(1, 1, 1, 1, 1), cts.Token);
        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(1, 1, 1, 1, 1), cts.Token);
        Assert.Equal(3, pool.WorkerCount);

        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(0, 0, 1, 0, 0), cts.Token);
        Assert.Equal(3, pool.WorkerCount);
        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(0, 0, 1, 0, 0), cts.Token);
        Assert.Equal(2, pool.WorkerCount);
        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(0, 0, 1, 0, 0), cts.Token);
        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(0, 0, 1, 0, 0), cts.Token);
        Assert.Equal(1, pool.WorkerCount);
        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(0, 0, 1, 0, 0), cts.Token);
        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(0, 0, 1, 0, 0), cts.Token);
        Assert.Equal(1, pool.WorkerCount);

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Pool_Hysteresis_ResetsWhenPressureIsInBand()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions(), transitQueueMemoryLimit: 64 * 1024);
        var options = new ArticleIngestionOptions
        {
            MinWorkers = 1,
            MaxWorkers = 4,
            ScaleUpPressureThreshold = 0.6,
            ScaleDownPressureThreshold = 0.2,
            ScaleUpConsecutiveIntervals = 3,
            ScaleDownConsecutiveIntervals = 3,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new IngestionWorkerPool(
            queue,
            static (_, _, _) => Task.CompletedTask,
            options,
            NullLogger.Instance);
        var run = pool.RunAsync(cts.Token);
        await WaitUntilAsync(() => pool.WorkerCount == 1, TimeSpan.FromSeconds(2));

        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(1, 1, 1, 1, 0), cts.Token);
        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(1, 1, 1, 1, 0), cts.Token);
        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(0.4, 0, 1, 0, 0), cts.Token);
        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(1, 1, 1, 1, 0), cts.Token);
        Assert.Equal(1, pool.WorkerCount);
        Assert.Equal(0, pool.ScaleUpCount);

        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(1, 1, 1, 1, 0), cts.Token);
        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(1, 1, 1, 1, 0), cts.Token);
        Assert.Equal(2, pool.WorkerCount);
        Assert.Equal(1, pool.ScaleUpCount);

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MultipleWorkers_DrainQueueWithoutDuplicateOrLoss()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions(), transitQueueMemoryLimit: 1024 * 1024);
        var processed = new ConcurrentBag<string>();
        var options = new ArticleIngestionOptions
        {
            MinWorkers = 4,
            MaxWorkers = 4,
            ScaleIntervalSeconds = 3600,
        };

        for (var i = 0; i < 40; i++)
        {
            var article = CanonicalArticleText.CreateQueued(
                $"<{i}@pool.test>",
                InboundArticleProducer.TakeThis,
                body: $"body-{i}\r\n");
            Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(article, CancellationToken.None));
        }

        using var cts = new CancellationTokenSource();
        var pool = new IngestionWorkerPool(
            queue,
            (article, _, _) =>
            {
                processed.Add(article.MessageId);
                return Task.CompletedTask;
            },
            options,
            NullLogger.Instance);

        var run = pool.RunAsync(cts.Token);
        await WaitUntilAsync(() => processed.Count == 40, TimeSpan.FromSeconds(5));
        Assert.Equal(40, processed.Count);
        Assert.Equal(40, processed.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(0, queue.Count);

        queue.Complete();
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GracefulShutdown_CancellationBetweenItems_PersistsArticlesWhenCountReadsEmpty()
    {
        var queue = new CountReportsEmptyQueue();
        var processed = new ConcurrentBag<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new ArticleIngestionOptions
        {
            MinWorkers = 1,
            MaxWorkers = 1,
            ScaleIntervalSeconds = 3600,
        };

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(
                ArticleEnqueueResult.Accepted,
                await queue.EnqueueAsync(
                    CanonicalArticleText.CreateQueued(
                        $"<{i}@shutdown.test>",
                        InboundArticleProducer.IHave,
                        "body\r\n"),
                    CancellationToken.None));
        }

        var pool = new IngestionWorkerPool(
            queue,
            async (article, _, _) =>
            {
                if (entered.TrySetResult())
                {
                    await release.Task.ConfigureAwait(false);
                }

                processed.Add(article.MessageId);
            },
            options,
            NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var run = pool.RunAsync(cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Complete();
        await cts.CancelAsync();
        release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, processed.Count);
        Assert.Equal(3, processed.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PressureSnapshot_UsesByteUtilisationAndWaitingProducers()
    {
        var fromBytes = new IngestionPressureSnapshot(0.5, 50, 100, 3, 0);
        Assert.Equal(0.5, fromBytes.Pressure);

        var waiting = IngestionPressureSnapshot.FromQueue(
            new ArticleIngestionQueue(new ArticleIngestionOptions(), transitQueueMemoryLimit: 100));
        Assert.Equal(0, waiting.Pressure);
    }

    /// <summary>
    /// Queue whose <see cref="Count"/> stays 0 while accepted articles remain readable.
    /// </summary>
    /// <remarks>
    /// Models the approximate count window: an article can already be in the channel
    /// before <see cref="Count"/> advances. Shutdown must not treat 0 as empty.
    /// </remarks>
    private sealed class CountReportsEmptyQueue : IArticleIngestionQueue
    {
        private readonly ConcurrentQueue<InboundArticle> _items = new();
        private int _accepting = 1;

        public long MemoryLimitBytes => 1024 * 1024;

        public long QueuedBytes => 0;

        public long PeakQueuedBytes => 0;

        public int MaxArticleBytes => 1024 * 1024;

        public int Count => 0;

        public int PeakCount => 0;

        public bool IsAccepting => Volatile.Read(ref _accepting) != 0;

        public ValueTask<ArticleEnqueueResult> EnqueueAsync(
            InboundArticle article,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(article);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAccepting)
            {
                return new ValueTask<ArticleEnqueueResult>(ArticleEnqueueResult.Unavailable);
            }

            _items.Enqueue(article);
            return new ValueTask<ArticleEnqueueResult>(ArticleEnqueueResult.Accepted);
        }

        public bool TryProbeCapacity() => IsAccepting;

        public ArticleEnqueueResult TryAdmit(InboundArticle article)
        {
            ArgumentNullException.ThrowIfNull(article);
            if (!IsAccepting)
            {
                return ArticleEnqueueResult.Unavailable;
            }

            _items.Enqueue(article);
            return ArticleEnqueueResult.Accepted;
        }

        public bool TryEnqueue(InboundArticle article) =>
            TryAdmit(article) == ArticleEnqueueResult.Accepted;

        public void Complete() => Interlocked.Exchange(ref _accepting, 0);

        public ValueTask<InboundArticle?> DequeueAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_items.TryDequeue(out var article))
            {
                return new ValueTask<InboundArticle?>(article);
            }

            return IsAccepting
                ? throw new InvalidOperationException("Test queue was read while empty and still accepting.")
                : new ValueTask<InboundArticle?>((InboundArticle?)null);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }
}
