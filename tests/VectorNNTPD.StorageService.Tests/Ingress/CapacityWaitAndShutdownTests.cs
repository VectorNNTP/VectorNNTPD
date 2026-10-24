using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;

namespace VectorNNTPD.StorageService.Tests.Ingress
{
    /// <summary>
    /// Capacity-blocked persistence: bounded hold, signal wake-up, and shutdown drain semantics.
    /// </summary>
    public sealed class CapacityWaitAndShutdownTests
    {
        private static readonly TimeSpan Safety = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task Capacity_signal_wakes_blocked_persistence_without_spinning()
        {
            var time = new FakeTimeProvider();
            var queue = new AcceptedArticleQueue(1 << 20, time);
            var signal = CreateSignal();
            var processor = new ControllableCapacityProcessor();
            var options = new SpoolPersistenceOptions
            {
                MinWorkers = 1,
                MaxWorkers = 1,
                IneligibleRetryMilliseconds = 60_000,
                ControlIntervalMilliseconds = 60_000,
            };
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                options,
                new SpoolPressure(signal),
                new SpoolPersistenceCounters(),
                time,
                signal);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(queue.TryEnqueue(Article("<wake@seg.test>")));
                await WaitAsync(() => processor.Calls >= 1);
                Assert.Equal(0, queue.QueuedCount);
                Assert.Equal(1, worker.ActiveWorkers);

                var callsBefore = processor.Calls;
                await Task.Delay(40);
                Assert.Equal(callsBefore, processor.Calls);

                processor.AllowComplete = true;
                signal.Notify();
                await WaitAsync(() => processor.Completed == 1);
                Assert.True(processor.Calls >= callsBefore + 1);
            }
            finally
            {
                processor.AllowComplete = true;
                signal.Notify();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Notify_during_process_does_not_lose_wakeup()
        {
            var time = new FakeTimeProvider();
            var queue = new AcceptedArticleQueue(1 << 20, time);
            var signal = CreateSignal();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var processor = new RaceNotifyProcessor(entered, proceed);
            var options = new SpoolPersistenceOptions
            {
                MinWorkers = 1,
                MaxWorkers = 1,
                IneligibleRetryMilliseconds = 60_000,
                ControlIntervalMilliseconds = 60_000,
            };
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                options,
                new SpoolPressure(signal),
                new SpoolPersistenceCounters(),
                time,
                signal);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(queue.TryEnqueue(Article("<race@seg.test>")));
                await entered.Task.WaitAsync(Safety);
                signal.Notify();
                proceed.TrySetResult();
                await WaitAsync(() => processor.Completed == 1);
            }
            finally
            {
                proceed.TrySetResult();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Multiple_blocked_articles_do_not_create_unbounded_retry_tasks()
        {
            var time = new FakeTimeProvider();
            var queue = new AcceptedArticleQueue(1 << 20, time);
            var signal = CreateSignal();
            var processor = new ControllableCapacityProcessor();
            var options = new SpoolPersistenceOptions
            {
                MinWorkers = 2,
                MaxWorkers = 2,
                IneligibleRetryMilliseconds = 60_000,
                ControlIntervalMilliseconds = 60_000,
            };
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                options,
                new SpoolPressure(signal),
                new SpoolPersistenceCounters(),
                time,
                signal);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(queue.TryEnqueue(Article("<a@seg.test>")));
                Assert.True(queue.TryEnqueue(Article("<b@seg.test>")));
                Assert.True(queue.TryEnqueue(Article("<c@seg.test>")));
                await WaitAsync(() => processor.Calls >= 2 && queue.QueuedCount == 1);
                Assert.Equal(2, worker.ActiveWorkers);
                Assert.Equal(1, queue.QueuedCount);

                processor.AllowComplete = true;
                signal.Notify();
                await WaitAsync(() => processor.Completed == 3 && queue.QueuedCount == 0);
            }
            finally
            {
                processor.AllowComplete = true;
                signal.Notify();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Stop_waits_while_capacity_blocked_then_completes_after_recovery()
        {
            var time = new FakeTimeProvider();
            var queue = new AcceptedArticleQueue(1 << 20, time);
            var signal = CreateSignal();
            var processor = new ControllableCapacityProcessor();
            var options = new SpoolPersistenceOptions
            {
                MinWorkers = 1,
                MaxWorkers = 1,
                IneligibleRetryMilliseconds = 60_000,
                ControlIntervalMilliseconds = 60_000,
            };
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                options,
                new SpoolPressure(signal),
                new SpoolPersistenceCounters(),
                time,
                signal);
            await worker.StartAsync(CancellationToken.None);
            Assert.True(queue.TryEnqueue(Article("<drain-cap@seg.test>")));
            await WaitAsync(() => processor.Calls >= 1);

            var stopping = worker.StopAsync(CancellationToken.None);
            Assert.False(stopping.IsCompleted);
            await Task.Delay(30);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, processor.Completed);

            processor.AllowComplete = true;
            signal.Notify();
            await stopping.WaitAsync(Safety);
            Assert.Equal(1, processor.Completed);
            Assert.Equal(0, queue.QueuedCount);
        }

        [Fact]
        public async Task Host_cancellation_during_capacity_wait_fails_drain()
        {
            var time = new FakeTimeProvider();
            var queue = new AcceptedArticleQueue(1 << 20, time);
            var signal = CreateSignal();
            var processor = new ControllableCapacityProcessor();
            var options = new SpoolPersistenceOptions
            {
                MinWorkers = 1,
                MaxWorkers = 1,
                IneligibleRetryMilliseconds = 60_000,
                ControlIntervalMilliseconds = 60_000,
            };
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                options,
                new SpoolPressure(signal),
                new SpoolPersistenceCounters(),
                time,
                signal);
            await worker.StartAsync(CancellationToken.None);
            Assert.True(queue.TryEnqueue(Article("<cancel-cap@seg.test>")));
            await WaitAsync(() => processor.Calls >= 1);

            using var cts = new CancellationTokenSource();
            var stopping = worker.StopAsync(cts.Token);
            await Task.Delay(20);
            Assert.False(stopping.IsCompleted);
            cts.Cancel();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stopping.WaitAsync(Safety));
            Assert.Contains("drain", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Capacity_blocked_hold_keeps_accounted_budget_and_blocks_further_admission()
        {
            var time = new FakeTimeProvider();
            var article = Article("<budget-hold@seg.test>");
            var queue = new AcceptedArticleQueue(article.ArtSize, time);
            var signal = CreateSignal();
            var processor = new ControllableCapacityProcessor();
            var options = new SpoolPersistenceOptions
            {
                MinWorkers = 1,
                MaxWorkers = 1,
                IneligibleRetryMilliseconds = 60_000,
                ControlIntervalMilliseconds = 60_000,
            };
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                options,
                new SpoolPressure(signal),
                new SpoolPersistenceCounters(),
                time,
                signal);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(queue.TryEnqueue(article));
                await WaitAsync(() => processor.Calls >= 1 && queue.QueuedCount == 0);
                Assert.Equal(article.ArtSize, queue.InFlightBytes);
                Assert.Equal(article.ArtSize, queue.AccountedBytes);
                Assert.False(queue.TryEnqueue(Article("<budget-extra@seg.test>")));

                processor.AllowComplete = true;
                signal.Notify();
                await WaitAsync(() => processor.Completed == 1 && queue.AccountedBytes == 0);
            }
            finally
            {
                processor.AllowComplete = true;
                signal.Notify();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task NoteFreedBytes_publishes_capacity_sample_and_wakes_blocked_worker()
        {
            var time = new FakeTimeProvider();
            var queue = new AcceptedArticleQueue(1 << 20, time);
            var spool = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:freed", [spool])]);
            var monitor = new BlockDeviceMonitorService(
                catalog,
                new PlatformBlockDeviceCounterSource(),
                NullLogger<BlockDeviceMonitorService>.Instance,
                TimeSpan.FromHours(1),
                TimeProvider.System,
                freeSpace: null);
            monitor.SetSpaceObservationForTests(spool, totalBytes: 1_000_000, availableBytes: 100_000);
            var signal = new SpoolCapacityWaitSignal(monitor);
            var processor = new ControllableCapacityProcessor();
            var options = new SpoolPersistenceOptions
            {
                MinWorkers = 1,
                MaxWorkers = 1,
                IneligibleRetryMilliseconds = 60_000,
                ControlIntervalMilliseconds = 60_000,
            };
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                options,
                new SpoolPressure(signal),
                new SpoolPersistenceCounters(),
                time,
                signal);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(queue.TryEnqueue(Article("<freed-wake@seg.test>")));
                await WaitAsync(() => processor.Calls >= 1);
                var callsBefore = processor.Calls;
                await Task.Delay(40);
                Assert.Equal(callsBefore, processor.Calls);

                processor.AllowComplete = true;
                monitor.NoteFreedBytes(spool, 50_000);
                await WaitAsync(() => processor.Completed == 1);
            }
            finally
            {
                processor.AllowComplete = true;
                signal.Notify();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        private static SpoolCapacityWaitSignal CreateSignal()
        {
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:test", [Path.GetTempPath()])]);
            var monitor = new BlockDeviceMonitorService(
                catalog,
                new PlatformBlockDeviceCounterSource(),
                NullLogger<BlockDeviceMonitorService>.Instance,
                TimeSpan.FromHours(1),
                TimeProvider.System,
                freeSpace: null);
            return new SpoolCapacityWaitSignal(monitor);
        }

        private static AcceptedArticle Article(string messageId)
        {
            var parser = new NntpArticleParser("cache01.usenet.ninja");
            var text = "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: "
                + messageId
                + "\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: capacity\r\n\r\nbody\r\n";
            var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(text));
            Assert.True(created.IsAccepted);
            return new AcceptedArticle(
                created.Record.ArtId,
                created.Record.ArtHash,
                created.Record.ArtSize,
                created.Record.ArtData);
        }

        private static async Task WaitAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Safety;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("condition was not met");
                }

                await Task.Delay(10);
            }
        }

        private sealed class ControllableCapacityProcessor : IAcceptedArticleProcessor
        {
            private int _calls;
            private int _completed;

            public int Calls => Volatile.Read(ref _calls);

            public int Completed => Volatile.Read(ref _completed);

            public bool AllowComplete { get; set; }

            public void EnsureReady()
            {
            }

            public ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _calls);
                if (!AllowComplete)
                {
                    return ValueTask.FromResult(AcceptedArticleProcessResult.WaitingForCapacity);
                }

                Interlocked.Increment(ref _completed);
                return ValueTask.FromResult(AcceptedArticleProcessResult.Completed);
            }
        }

        private sealed class RaceNotifyProcessor : IAcceptedArticleProcessor
        {
            private readonly TaskCompletionSource _entered;
            private readonly TaskCompletionSource _proceed;
            private int _calls;
            private int _completed;

            public RaceNotifyProcessor(TaskCompletionSource entered, TaskCompletionSource proceed)
            {
                _entered = entered;
                _proceed = proceed;
            }

            public int Completed => Volatile.Read(ref _completed);

            public void EnsureReady()
            {
            }

            public async ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref _calls);
                if (call == 1)
                {
                    _entered.TrySetResult();
                    await _proceed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return AcceptedArticleProcessResult.WaitingForCapacity;
                }

                Interlocked.Increment(ref _completed);
                return AcceptedArticleProcessResult.Completed;
            }
        }
    }
}
