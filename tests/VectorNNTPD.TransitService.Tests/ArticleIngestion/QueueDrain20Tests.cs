using System.Collections.Concurrent;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Tests.Fixtures;

namespace VectorNNTPD.TransitService.Tests.ArticleIngestion;

/// <summary>
/// True concurrent <see cref="IArticleIngestionQueue.DequeueAsync"/> consumers.
/// Ownership is released inside dequeue; workers must not release again.
/// </summary>
public sealed class QueueDrain20Tests
{
    internal const int WorkerCount = 20;

    [Fact]
    public async Task TwoWorkers_ConsumeEveryArticleExactlyOnce()
    {
        await DrainExactlyOnceAsync(workers: 2, articleCount: 100);
    }

    [Fact]
    public async Task TwentyWorkers_ConsumeEveryArticleExactlyOnce()
    {
        await DrainExactlyOnceAsync(workers: WorkerCount, articleCount: 200);
    }

    [Fact]
    public async Task TwentyWorkers_CallDequeueAsyncConcurrently_AndAllParticipate()
    {
        // Multi-reader Channel does not promise fair item distribution: one consumer may
        // drain every article when WaitToReadAsync completes synchronously while data
        // remains (observed on Linux CI for the Reader twin). Concurrent DequeueAsync
        // entry is the portable contract; fair sharing is not.
        const int articleCount = 400;
        var queue = CreateQueue(articleCount);
        var seen = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var hits = new int[WorkerCount];
        using var enteredDequeue = new CountdownEvent(WorkerCount);
        var concurrentDequeues = 0;
        var maxConcurrentDequeues = 0;

        var workers = new Task[WorkerCount];
        for (var i = 0; i < WorkerCount; i++)
        {
            var workerIndex = i;
            workers[workerIndex] = Task.Run(async () =>
            {
                var signaledEntry = false;
                while (true)
                {
                    InboundArticle? article;
                    try
                    {
                        var inflight = Interlocked.Increment(ref concurrentDequeues);
                        UpdateMax(ref maxConcurrentDequeues, inflight);
                        try
                        {
                            var dequeue = queue.DequeueAsync(CancellationToken.None);
                            if (!signaledEntry)
                            {
                                signaledEntry = true;
                                enteredDequeue.Signal();
                            }

                            article = await dequeue.ConfigureAwait(false);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref concurrentDequeues);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (article is null)
                    {
                        return;
                    }

                    seen.AddOrUpdate(article.MessageId, 1, static (_, count) => count + 1);
                    Interlocked.Increment(ref hits[workerIndex]);
                }
            });
        }

        Assert.True(
            enteredDequeue.Wait(TimeSpan.FromSeconds(5)),
            "all workers must enter DequeueAsync before articles are published");
        Assert.True(
            Volatile.Read(ref maxConcurrentDequeues) >= WorkerCount,
            "DequeueAsync must support WorkerCount concurrent waiters on an empty queue");

        EnqueueUnique(queue, articleCount);
        queue.Complete();
        await WaitWorkersAsync(workers);

        Assert.Equal(articleCount, seen.Count);
        Assert.All(seen.Values, static count => Assert.Equal(1, count));
        Assert.Equal(articleCount, hits.Sum());
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task TwentyWorkers_WaitingThenEnqueue_AllExitAfterComplete()
    {
        const int articleCount = 200;
        var queue = CreateQueue(articleCount);
        var seen = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var hits = new int[WorkerCount];
        var workers = StartWorkers(queue, WorkerCount, seen, hits);

        var ids = EnqueueUnique(queue, articleCount);
        queue.Complete();
        await WaitWorkersAsync(workers);

        Assert.Equal(articleCount, seen.Count);
        Assert.All(seen.Values, static count => Assert.Equal(1, count));
        Assert.True(ids.SetEquals(seen.Keys));
        Assert.Equal(articleCount, hits.Sum());
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task ConcurrentConsumers_EachWorkerReceivesIncreasingFifoSubsequence()
    {
        const int articleCount = 200;
        var queue = CreateQueue(articleCount);
        EnqueueUnique(queue, articleCount, idPrefix: "seq");
        var perWorker = new List<int>[WorkerCount];
        for (var i = 0; i < WorkerCount; i++)
        {
            perWorker[i] = [];
        }

        var workers = new Task[WorkerCount];
        for (var i = 0; i < WorkerCount; i++)
        {
            var workerIndex = i;
            workers[workerIndex] = Task.Run(async () =>
            {
                while (await queue.DequeueAsync(CancellationToken.None) is { } article)
                {
                    var start = article.MessageId.IndexOf('.') + 1;
                    var end = article.MessageId.IndexOf('@');
                    var seq = int.Parse(article.MessageId.AsSpan(start, end - start));
                    perWorker[workerIndex].Add(seq);
                }
            });
        }

        queue.Complete();
        await WaitWorkersAsync(workers);

        var all = new HashSet<int>();
        foreach (var ordered in perWorker.Select(static bag => bag.ToArray()))
        {
            for (var i = 1; i < ordered.Length; i++)
            {
                Assert.True(ordered[i] > ordered[i - 1], "per-worker dequeue subsequence must be increasing FIFO");
            }

            foreach (var seq in ordered)
            {
                Assert.True(all.Add(seq));
            }
        }

        Assert.Equal(articleCount, all.Count);
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task TwentyWorkers_AccountingReturnsToZero_AndPeaksMatchPrefill()
    {
        const int articleCount = 80;
        var queue = CreateQueue(articleCount);
        EnqueueUnique(queue, articleCount);
        var expectedBytes = queue.QueuedBytes;

        Assert.Equal(expectedBytes, queue.QueuedBytes);
        Assert.Equal(articleCount, queue.Count);
        Assert.Equal(expectedBytes, queue.PeakQueuedBytes);
        Assert.Equal(articleCount, queue.PeakCount);

        var workers = StartWorkers(queue, WorkerCount);
        queue.Complete();
        await WaitWorkersAsync(workers);

        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);
        Assert.Equal(expectedBytes, queue.PeakQueuedBytes);
        Assert.Equal(articleCount, queue.PeakCount);
        Assert.False(queue.IsAccepting);
    }

    [Fact]
    public async Task TwentyWorkers_CompleteEmptyQueue_UnblocksEveryWorker()
    {
        var queue = CreateQueue(1);
        var workers = StartWorkers(queue, WorkerCount);
        queue.Complete();
        await WaitWorkersAsync(workers);

        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Null(await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TwentyWorkers_Cancellation_UnblocksWithoutReservationLeak()
    {
        var queue = CreateQueue(1);
        using var cts = new CancellationTokenSource();
        var workers = StartWorkers(queue, WorkerCount, cancellationToken: cts.Token);

        await cts.CancelAsync();
        await WaitWorkersAsync(workers);

        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);
        Assert.True(queue.IsAccepting);
    }

    [Fact]
    public async Task CancelledConsumer_DoesNotCorruptAccounting_RemainingWorkersDrain()
    {
        const int articleCount = 120;
        var queue = CreateQueue(articleCount);
        EnqueueUnique(queue, articleCount);

        var seen = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        using var cancelled = new CancellationTokenSource();
        var tasks = new List<Task>(WorkerCount);
        tasks.AddRange(StartWorkers(queue, 5, seen, cancellationToken: cancelled.Token));
        tasks.AddRange(StartWorkers(queue, 15, seen));

        await cancelled.CancelAsync();
        queue.Complete();
        await WaitWorkersAsync(tasks.ToArray());

        Assert.Equal(articleCount, seen.Count);
        Assert.All(seen.Values, static count => Assert.Equal(1, count));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);
    }

    [Fact]
    public async Task TwentyWorkers_CancelDuringDrain_DoesNotDoubleRelease()
    {
        const int articleCount = 100;
        var queue = CreateQueue(articleCount);
        var sample = Article("<q20.000@ex.com>");
        EnqueueUnique(queue, articleCount);

        using var cts = new CancellationTokenSource();
        var consumed = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var workers = StartWorkers(queue, WorkerCount, consumed, cancellationToken: cts.Token);
        await cts.CancelAsync();
        await WaitWorkersAsync(workers);

        var remainingBytes = queue.QueuedBytes;
        var remainingCount = queue.Count;
        Assert.True(remainingBytes >= 0);
        Assert.True(remainingCount >= 0);
        Assert.Equal(remainingCount * sample.Payload.Length, remainingBytes);
        Assert.Equal(0, queue.DebugWaiterCount);
        Assert.All(consumed.Values, static count => Assert.Equal(1, count));

        queue.Complete();
        var leftover = 0;
        while (await queue.DequeueAsync(CancellationToken.None) is not null)
        {
            leftover++;
        }

        Assert.Equal(remainingCount, leftover);
        Assert.Equal(articleCount, consumed.Count + leftover);
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task TwentyWorkers_CompleteAfterPartialPrefill_DrainsAndRejectsNewAdmits()
    {
        var queue = CreateQueue(5);
        EnqueueUnique(queue, 5, idPrefix: "held");

        var workers = StartWorkers(queue, WorkerCount);
        queue.Complete();
        await WaitWorkersAsync(workers);

        Assert.Equal(ArticleEnqueueResult.Unavailable, queue.TryAdmit(Article("<late@ex.com>")));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.False(queue.IsAccepting);
        Assert.Null(await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentProducersAndConsumers_CannotOversubscribeBudget()
    {
        var sample = Article("<p0.00@c>");
        var size = sample.Payload.Length;
        var limit = size * 4L;
        const int producers = 8;
        const int consumers = 4;
        const int perProducer = 8;
        var queue = CreateWithByteLimit(limit);
        var seen = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var maxObserved = 0L;
        var oversubscribed = 0;
        using var observeCts = new CancellationTokenSource();
        var observer = Task.Run(async () =>
        {
            while (!observeCts.Token.IsCancellationRequested)
            {
                var bytes = queue.QueuedBytes;
                if (bytes > maxObserved)
                {
                    maxObserved = bytes;
                }

                if (bytes > limit)
                {
                    Interlocked.Increment(ref oversubscribed);
                }

                await Task.Yield();
            }
        });

        var consumerTasks = StartWorkers(queue, consumers, seen);
        var producerTasks = new Task[producers];
        for (var p = 0; p < producers; p++)
        {
            var producer = p;
            producerTasks[producer] = Task.Run(async () =>
            {
                for (var i = 0; i < perProducer; i++)
                {
                    var result = await queue.EnqueueAsync(Article($"<p{producer}.{i:D2}@c>"), CancellationToken.None);
                    Assert.Equal(ArticleEnqueueResult.Accepted, result);
                }
            });
        }

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(producerTasks).WaitAsync(safety.Token);
        queue.Complete();
        await WaitWorkersAsync(consumerTasks);
        await observeCts.CancelAsync();
        await observer.WaitAsync(safety.Token);

        Assert.Equal(0, oversubscribed);
        Assert.True(maxObserved <= limit);
        Assert.Equal(producers * perProducer, seen.Count);
        Assert.All(seen.Values, static count => Assert.Equal(1, count));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);
    }

    [Fact]
    public async Task SuccessfulDequeue_ReleasesAccountingBeforeCallerOwnsArticle()
    {
        var article = Article("<own@ex.com>");
        var queue = CreateQueue(1);
        Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(article));
        Assert.Equal(article.Payload.Length, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);

        var dequeued = await queue.DequeueAsync(CancellationToken.None);
        Assert.Same(article, dequeued);
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(article.Payload.Length, dequeued!.Payload.Length);
    }

    private static async Task DrainExactlyOnceAsync(int workers, int articleCount)
    {
        var queue = CreateQueue(articleCount);
        var ids = EnqueueUnique(queue, articleCount);
        var seen = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var hits = new int[workers];

        var tasks = StartWorkers(queue, workers, seen, hits);
        queue.Complete();
        await WaitWorkersAsync(tasks);

        Assert.Equal(articleCount, seen.Count);
        Assert.All(seen.Values, static count => Assert.Equal(1, count));
        Assert.True(ids.SetEquals(seen.Keys));
        Assert.Equal(articleCount, hits.Sum());
        Assert.True(hits.Count(static h => h > 0) >= 1);
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);
    }

    private static ArticleIngestionQueue CreateQueue(int articleSlots)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(articleSlots);
        return new ArticleIngestionQueue(new ArticleIngestionOptions(), 64L * 1024 * 1024);
    }

    private static ArticleIngestionQueue CreateWithByteLimit(long memoryLimit) =>
        new(new ArticleIngestionOptions(), memoryLimit);

    private static HashSet<string> EnqueueUnique(
        ArticleIngestionQueue queue,
        int articleCount,
        string idPrefix = "q20")
    {
        var ids = new HashSet<string>(articleCount, StringComparer.Ordinal);
        for (var i = 0; i < articleCount; i++)
        {
            var id = $"<{idPrefix}.{i:D3}@ex.com>";
            Assert.True(ids.Add(id));
            Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(Article(id)));
        }

        return ids;
    }

    private static Task[] StartWorkers(
        IArticleIngestionQueue queue,
        int workers,
        ConcurrentDictionary<string, int>? seen = null,
        int[]? hits = null,
        CancellationToken cancellationToken = default)
    {
        var tasks = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            var workerIndex = i;
            tasks[workerIndex] = Task.Run(
                () => DrainAsync(queue, seen, hits, workerIndex, cancellationToken),
                CancellationToken.None);
        }

        return tasks;
    }

    private static async Task DrainAsync(
        IArticleIngestionQueue queue,
        ConcurrentDictionary<string, int>? seen,
        int[]? hits,
        int workerIndex,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            InboundArticle? article;
            try
            {
                article = await queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (article is null)
            {
                return;
            }

            if (seen is not null)
            {
                seen.AddOrUpdate(article.MessageId, 1, static (_, count) => count + 1);
            }

            if (hits is not null)
            {
                Interlocked.Increment(ref hits[workerIndex]);
            }
        }
    }

    private static async Task WaitWorkersAsync(Task[] workers)
    {
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(workers).WaitAsync(safety.Token);
    }

    private static void UpdateMax(ref int location, int candidate)
    {
        var current = Volatile.Read(ref location);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref location, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static InboundArticle Article(string messageId) =>
        CanonicalArticleText.CreateQueued(messageId, InboundArticleProducer.IHave);
}
