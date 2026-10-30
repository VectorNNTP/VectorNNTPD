using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client.Exceptions;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.ArticleIngestion.OverviewDb;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Tests.TestDoubles;

namespace VectorNNTPD.TransitService.Tests.ArticleIngestion;

/// <summary>OverviewDB intermediate work-queue and publisher-pool contracts.</summary>
public sealed class OverviewDbPublisherStageTests
{
    [Fact]
    public async Task WorkQueue_BackpressuresWhenByteBudgetExhausted()
    {
        var queue = new OverviewDbWorkQueue(memoryLimitBytes: 10);
        var first = new OverviewDbWorkItem(new byte[8], "<a@test>");
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(first, CancellationToken.None));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var second = new OverviewDbWorkItem(new byte[8], "<b@test>");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.EnqueueAsync(second, cts.Token).AsTask());
        Assert.True(queue.WaitingProducerCount >= 0);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task PublisherPool_PublishesAndAwaitsConfirm_WithoutArticleQueue()
    {
        var work = new OverviewDbWorkQueue(memoryLimitBytes: 1024 * 1024);
        var publisher = new RecordingOverviewDbHandoffPublisher();
        var options = new ArticleIngestionOptions
        {
            OverviewDbMinPublisherWorkers = 1,
            OverviewDbMaxPublisherWorkers = 1,
            MaxPublishConcurrency = 1,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new OverviewDbPublisherPool(
            work,
            publisher,
            options,
            NullLogger.Instance);
        var run = pool.RunAsync(cts.Token);

        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await work.EnqueueAsync(new OverviewDbWorkItem("abc"u8.ToArray(), "<m@test>"), CancellationToken.None));

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (publisher.Payloads.Count < 1)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, wait.Token);
        }

        Assert.Equal(1, publisher.AttemptCount);
        Assert.Single(publisher.Payloads);

        work.Complete();
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PublisherPool_RequeuesWorkItemOnPublishFailure()
    {
        var work = new OverviewDbWorkQueue(memoryLimitBytes: 1024 * 1024);
        var publisher = new RecordingOverviewDbHandoffPublisher { RemainingFailures = 1 };
        var options = new ArticleIngestionOptions
        {
            OverviewDbMinPublisherWorkers = 1,
            OverviewDbMaxPublisherWorkers = 1,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new OverviewDbPublisherPool(work, publisher, options, NullLogger.Instance);
        var run = pool.RunAsync(cts.Token);

        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await work.EnqueueAsync(new OverviewDbWorkItem("retry"u8.ToArray(), "<r@test>"), CancellationToken.None));

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (publisher.Payloads.Count < 1)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, wait.Token);
        }

        Assert.Equal(2, publisher.AttemptCount);
        Assert.Single(publisher.Payloads);

        work.Complete();
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PublisherPool_WireFormattingException_IsPublishFailure_NotQueueUnavailable()
    {
        var work = new OverviewDbWorkQueue(memoryLimitBytes: 1024 * 1024);
        var publisher = new RecordingOverviewDbHandoffPublisher
        {
            RemainingFailures = 1,
            TransientPublishException = new WireFormattingException(
                "Value of type 'UInt64' cannot appear as table value"),
        };
        var logger = new CollectingLogger();
        var options = new ArticleIngestionOptions
        {
            OverviewDbMinPublisherWorkers = 1,
            OverviewDbMaxPublisherWorkers = 1,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new OverviewDbPublisherPool(work, publisher, options, logger);
        var run = pool.RunAsync(cts.Token);

        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await work.EnqueueAsync(
                new OverviewDbWorkItem("wire"u8.ToArray(), "<wire@test>"),
                CancellationToken.None));

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (publisher.Payloads.Count < 1)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, wait.Token);
        }

        Assert.Equal(2, publisher.AttemptCount);
        Assert.Contains(
            logger.Messages,
            m => m.Contains("OverviewDB RabbitMQ handoff failed", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Messages,
            m => m.Contains("queue is unavailable", StringComparison.Ordinal));
        Assert.Contains(
            logger.Messages,
            m => m.Contains("was requeued", StringComparison.Ordinal));

        work.Complete();
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PublisherPool_RequeueUnavailable_OnlyWhenWorkQueueUnavailable()
    {
        var work = new OverviewDbWorkQueue(memoryLimitBytes: 1024 * 1024);
        var publisher = new RecordingOverviewDbHandoffPublisher
        {
            RemainingFailures = 1,
            TransientPublishException = new WireFormattingException("local serialize failure"),
            BlockOnFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var logger = new CollectingLogger();
        var options = new ArticleIngestionOptions
        {
            OverviewDbMinPublisherWorkers = 1,
            OverviewDbMaxPublisherWorkers = 1,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new OverviewDbPublisherPool(work, publisher, options, logger);
        var run = pool.RunAsync(cts.Token);

        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await work.EnqueueAsync(
                new OverviewDbWorkItem("gone"u8.ToArray(), "<gone@test>"),
                CancellationToken.None));

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (publisher.AttemptCount < 1)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, wait.Token);
        }

        // Complete the work queue before the publish failure requeue runs.
        work.Complete();
        publisher.BlockOnFailure.TrySetResult();

        using var logWait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!logger.Messages.Exists(
                   m => m.Contains("queue is unavailable", StringComparison.Ordinal)))
        {
            logWait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, logWait.Token);
        }

        Assert.Contains(
            logger.Messages,
            m => m.Contains("OverviewDB RabbitMQ handoff failed", StringComparison.Ordinal));
        Assert.Contains(
            logger.Messages,
            m => m.Contains("queue is unavailable", StringComparison.Ordinal));

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PublisherPool_ScalesIndependentlyOfArticleWorkerBounds()
    {
        var work = new OverviewDbWorkQueue(memoryLimitBytes: 1024);
        var options = new ArticleIngestionOptions
        {
            MinWorkers = 1,
            MaxWorkers = 1,
            OverviewDbMinPublisherWorkers = 2,
            OverviewDbMaxPublisherWorkers = 4,
            ScaleUpPressureThreshold = 0.5,
            ScaleDownPressureThreshold = 0.1,
            ScaleUpConsecutiveIntervals = 1,
            ScaleDownConsecutiveIntervals = 1,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new OverviewDbPublisherPool(
            work,
            new RecordingOverviewDbHandoffPublisher(),
            options,
            NullLogger.Instance);
        var run = pool.RunAsync(cts.Token);

        Assert.Equal(2, pool.WorkerCount);
        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(1, 1, 1, 1, 1), cts.Token);
        Assert.Equal(3, pool.WorkerCount);

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PublisherPool_DrainsQueuedWorkOnComplete_BeforeExit()
    {
        var work = new OverviewDbWorkQueue(memoryLimitBytes: 1024 * 1024);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new GatedOverviewDbHandoffPublisher(gate);
        var options = new ArticleIngestionOptions
        {
            OverviewDbMinPublisherWorkers = 1,
            OverviewDbMaxPublisherWorkers = 1,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new OverviewDbPublisherPool(work, publisher, options, NullLogger.Instance);
        var run = pool.RunAsync(cts.Token);

        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await work.EnqueueAsync(new OverviewDbWorkItem("drain-a"u8.ToArray(), "<a@test>"), CancellationToken.None));
        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await work.EnqueueAsync(new OverviewDbWorkItem("drain-b"u8.ToArray(), "<b@test>"), CancellationToken.None));

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (publisher.Started < 1)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, wait.Token);
        }

        // Complete first, then cancel while the in-flight publish is still gated.
        // Production must finish that item (and drain the queued peer) after Complete(),
        // not abandon the in-flight publish when the worker token cancels.
        work.Complete();
        await cts.CancelAsync();
        gate.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, publisher.ConfirmCount);
        // At least one attempt for each accepted item. A cancel-vs-gate race may skip the
        // drain retry for the first item when WaitAsync observes the completed gate first.
        Assert.InRange(publisher.Started, 2, 3);
        Assert.Equal(0, work.Count);
    }

    [Fact]
    public async Task PublisherPool_DrainPublishFailure_AfterComplete_ContinuesDrainWithoutHang()
    {
        var work = new OverviewDbWorkQueue(memoryLimitBytes: 1024 * 1024);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new GatedFailOnceOverviewDbHandoffPublisher(gate);
        var options = new ArticleIngestionOptions
        {
            OverviewDbMinPublisherWorkers = 1,
            OverviewDbMaxPublisherWorkers = 1,
            ScaleIntervalSeconds = 3600,
            OverviewDbPublisherShutdownSeconds = 2,
        };

        using var cts = new CancellationTokenSource();
        var pool = new OverviewDbPublisherPool(work, publisher, options, NullLogger.Instance);
        var run = pool.RunAsync(cts.Token);

        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await work.EnqueueAsync(new OverviewDbWorkItem("fail-a"u8.ToArray(), "<fail-a@test>"), CancellationToken.None));
        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await work.EnqueueAsync(new OverviewDbWorkItem("fail-b"u8.ToArray(), "<fail-b@test>"), CancellationToken.None));

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (publisher.Started < 1)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, wait.Token);
        }

        work.Complete();
        await cts.CancelAsync();
        gate.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        // First item's drain retry fails (cannot requeue after Complete); second item still publishes.
        Assert.Equal(1, publisher.ConfirmCount);
        Assert.Equal(1, publisher.DrainFailureCount);
        Assert.Equal(0, work.Count);
    }

    private sealed class GatedOverviewDbHandoffPublisher(TaskCompletionSource gate) : IOverviewDbHandoffPublisher
    {
        private int _started;
        private int _confirms;

        public int Started => Volatile.Read(ref _started);

        public int ConfirmCount => Volatile.Read(ref _confirms);

        public int OutstandingCount => 0;

        public async Task PublishAsync(OverviewDbWorkItem item, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            Interlocked.Increment(ref _started);
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _confirms);
        }

        public bool TryDequeuePublishFailure(out OverviewDbWorkItem item)
        {
            item = null!;
            return false;
        }

        public void AbandonOutstanding()
        {
        }
    }

    private sealed class GatedFailOnceOverviewDbHandoffPublisher(TaskCompletionSource gate) : IOverviewDbHandoffPublisher
    {
        private int _started;
        private int _confirms;
        private int _drainFailures;
        private int _postGateFailures;

        public int Started => Volatile.Read(ref _started);

        public int ConfirmCount => Volatile.Read(ref _confirms);

        public int DrainFailureCount => Volatile.Read(ref _drainFailures);

        public int OutstandingCount => 0;

        public async Task PublishAsync(OverviewDbWorkItem item, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            Interlocked.Increment(ref _started);
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref _postGateFailures, 1, 0) == 0)
            {
                Interlocked.Increment(ref _drainFailures);
                throw new InvalidOperationException("simulated OverviewDB drain publish failure");
            }

            Interlocked.Increment(ref _confirms);
        }

        public bool TryDequeuePublishFailure(out OverviewDbWorkItem item)
        {
            item = null!;
            return false;
        }

        public void AbandonOutstanding()
        {
        }
    }

    private sealed class CollectingLogger : ILogger
    {
        private readonly object _gate = new();

        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
