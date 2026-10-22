using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;

namespace VectorNNTPD.StorageService.Tests.Ingress
{

    public sealed class AdaptiveArticleWorkerTests
    {
        [Fact]
        public async Task Workers_scale_up_after_dwell_when_the_queue_stays_backlogged()
        {
            var time = new FakeTimeProvider();
            var queue = new AcceptedArticleQueue(1 << 20, time);
            var processor = new HoldProcessor();
            var options = Options(scaleUpDepth: 1);
            var worker = Worker(queue, processor, options, time, new SpoolPressure());
            await worker.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal(1, worker.ActiveWorkers);
                Assert.True(queue.TryEnqueue(Article(1)));
                Assert.True(queue.TryEnqueue(Article(2)));
                Assert.True(queue.TryEnqueue(Article(3)));
                await WaitAsync(() => processor.Entered >= 1);
                worker.EvaluateScaling();
                time.Advance(TimeSpan.FromMilliseconds(400));
                worker.EvaluateScaling();
                Assert.Equal(1, worker.DesiredWorkers);

                time.Advance(TimeSpan.FromMilliseconds(600));
                worker.EvaluateScaling();
                await WaitAsync(() => processor.Entered >= 2);
                Assert.Equal(2, worker.DesiredWorkers);
                Assert.True(worker.ActiveWorkers >= 2);
            }
            finally
            {
                processor.Release.TrySetResult();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Workers_do_not_scale_up_while_storage_is_saturated()
        {
            var time = new FakeTimeProvider();
            var queue = new AcceptedArticleQueue(1 << 20, time);
            var processor = new HoldProcessor();
            var pressure = new SpoolPressure();
            pressure.SetSaturated(true);
            var worker = Worker(queue, processor, Options(scaleUpDepth: 1), time, pressure);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(queue.TryEnqueue(Article(1)));
                Assert.True(queue.TryEnqueue(Article(2)));
                await WaitAsync(() => processor.Entered >= 1);
                worker.EvaluateScaling();
                time.Advance(TimeSpan.FromSeconds(2));
                worker.EvaluateScaling();
                Assert.Equal(1, worker.DesiredWorkers);
                Assert.Equal(1, processor.Entered);
            }
            finally
            {
                processor.Release.TrySetResult();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Workers_scale_down_to_the_minimum_after_the_queue_drains()
        {
            var time = new FakeTimeProvider();
            var queue = new AcceptedArticleQueue(1 << 20, time);
            var processor = new HoldProcessor();
            var worker = Worker(queue, processor, Options(scaleUpDepth: 1), time, new SpoolPressure());
            await worker.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(queue.TryEnqueue(Article(1)));
                Assert.True(queue.TryEnqueue(Article(2)));
                await WaitAsync(() => processor.Entered >= 1);
                worker.EvaluateScaling();
                time.Advance(TimeSpan.FromSeconds(1));
                worker.EvaluateScaling();
                await WaitAsync(() => worker.ActiveWorkers >= 2);
                processor.Release.TrySetResult();
                await WaitAsync(() => queue.QueuedCount == 0);
                worker.EvaluateScaling();
                time.Advance(TimeSpan.FromSeconds(1));
                worker.EvaluateScaling();
                await WaitAsync(() => worker.ActiveWorkers == 1);
                Assert.Equal(1, worker.DesiredWorkers);
            }
            finally
            {
                processor.Release.TrySetResult();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task A_deep_queue_does_not_scale_down_when_no_spool_can_accept()
        {
            var time = new FakeTimeProvider();
            var queue = new AcceptedArticleQueue(1 << 20, time);
            var processor = new HoldProcessor();
            var pressure = new SpoolPressure();
            var worker = Worker(queue, processor, Options(scaleUpDepth: 1), time, pressure);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(queue.TryEnqueue(Article(1)));
                Assert.True(queue.TryEnqueue(Article(2)));
                Assert.True(queue.TryEnqueue(Article(3)));
                Assert.True(queue.TryEnqueue(Article(4)));
                await WaitAsync(() => processor.Entered >= 1);
                worker.EvaluateScaling();
                time.Advance(TimeSpan.FromSeconds(1));
                worker.EvaluateScaling();
                await WaitAsync(() => worker.ActiveWorkers >= 2);
                pressure.SetSaturated(true);
                worker.EvaluateScaling();
                time.Advance(TimeSpan.FromSeconds(2));
                worker.EvaluateScaling();
                Assert.Equal(2, worker.DesiredWorkers);
            }
            finally
            {
                processor.Release.TrySetResult();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Global_worker_limit_is_enforced()
        {
            var time = new FakeTimeProvider();
            var queue = new AcceptedArticleQueue(1 << 20, time);
            var processor = new HoldProcessor();
            var options = Options(scaleUpDepth: 1);
            options.MaxWorkers = 2;
            var worker = Worker(queue, processor, options, time, new SpoolPressure());
            await worker.StartAsync(CancellationToken.None);
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    Assert.True(queue.TryEnqueue(Article(i + 1)));
                }

                await WaitAsync(() => processor.Entered >= 1);
                for (var i = 0; i < 4; i++)
                {
                    worker.EvaluateScaling();
                    time.Advance(TimeSpan.FromSeconds(1));
                }

                await WaitAsync(() => worker.ActiveWorkers == 2);
                Assert.Equal(2, worker.DesiredWorkers);
                Assert.True(processor.Entered <= 2);
            }
            finally
            {
                processor.Release.TrySetResult();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task An_article_stays_held_while_no_spool_is_eligible()
        {
            var time = new FakeTimeProvider();
            var queue = new AcceptedArticleQueue(1 << 20, time);
            var processor = new WaitingThenCompleteProcessor();
            var options = Options(scaleUpDepth: 100);
            options.IneligibleRetryMilliseconds = 0;
            var worker = Worker(queue, processor, options, time, new SpoolPressure());
            await worker.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(queue.TryEnqueue(Article(7)));
                await WaitAsync(() => processor.Calls >= 1);
                Assert.Equal(0, queue.QueuedCount);
                time.Advance(TimeSpan.FromMilliseconds(20));
                await WaitAsync(() => processor.Calls >= 2);
                processor.Allow.TrySetResult();
                await WaitAsync(() => processor.Completed == 1);
                Assert.Equal(0, queue.QueuedCount);
            }
            finally
            {
                processor.Allow.TrySetResult();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public void Per_spool_gate_rejects_a_second_reservation_at_the_limit()
        {
            var gate = new SpoolWriteGate();
            Assert.True(gate.TryEnter("spool", 1));
            Assert.False(gate.TryEnter("spool", 1));
            Assert.Equal(1, gate.InFlight("spool"));
            gate.Exit("spool");
            Assert.True(gate.TryEnter("spool", 1));
        }

        private static AcceptedArticleWorker Worker(
            AcceptedArticleQueue queue,
            IAcceptedArticleProcessor processor,
            SpoolPersistenceOptions options,
            FakeTimeProvider time,
            SpoolPressure pressure) =>
            new(queue, processor, NullLogger<AcceptedArticleWorker>.Instance, options, pressure, new SpoolPersistenceCounters(), time);

        private static SpoolPersistenceOptions Options(int scaleUpDepth) => new()
        {
            MinWorkers = 1,
            MaxWorkers = 4,
            ScaleUpQueueDepth = scaleUpDepth,
            ScaleDownQueueDepth = 0,
            ScaleDwellMilliseconds = 1000,
            ControlIntervalMilliseconds = 60_000,
            ScaleUpAgeMilliseconds = 60_000,
            IneligibleRetryMilliseconds = 50,
        };

        private static AcceptedArticle Article(int marker) =>
            new(default, (ulong)marker, 1, new byte[] { (byte)marker });

        private static async Task WaitAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("condition was not met");
                }

                await Task.Delay(10);
            }
        }

        private sealed class WaitingThenCompleteProcessor : IAcceptedArticleProcessor
        {
            private int _calls;
            private int _completed;

            public int Calls => Volatile.Read(ref _calls);

            public int Completed => Volatile.Read(ref _completed);

            public TaskCompletionSource Allow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void EnsureReady()
            {
            }

            public ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref _calls);
                if (call == 1 || !Allow.Task.IsCompleted)
                {
                    return ValueTask.FromResult(AcceptedArticleProcessResult.WaitingForCapacity);
                }

                Interlocked.Increment(ref _completed);
                return ValueTask.FromResult(AcceptedArticleProcessResult.Completed);
            }
        }

        private sealed class HoldProcessor : IAcceptedArticleProcessor
        {
            private int _entered;

            public int Entered => Volatile.Read(ref _entered);

            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void EnsureReady()
            {
            }

            public async ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _entered);
                await Release.Task.WaitAsync(cancellationToken);
                return AcceptedArticleProcessResult.Completed;
            }
        }
    }
}
