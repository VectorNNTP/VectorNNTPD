using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;
using Xunit.Abstractions;

namespace VectorNNTPD.StorageService.Tests.Ingress
{
    /// <summary>
    /// Observational memory / accounting envelope under healthy and capacity-blocked persistence.
    /// Runs only when <c>VECTORNNTPD_STORAGE_MEMORY_BENCH=1</c>.
    /// </summary>
    public sealed class StorageIngressMemoryEnvelopeBench
    {
        private readonly ITestOutputHelper _output;

        public StorageIngressMemoryEnvelopeBench(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Optional_memory_envelope_healthy_and_capacity_blocked()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("VECTORNNTPD_STORAGE_MEMORY_BENCH"), "1", StringComparison.Ordinal))
            {
                _output.WriteLine("Set VECTORNNTPD_STORAGE_MEMORY_BENCH=1 to measure ingress memory envelopes.");
                return;
            }

            const int articleBytes = 750 * 1024;
            const int maxQueuedBytes = 8 * articleBytes;
            var duration = TimeSpan.FromSeconds(3);

            await ReportHealthyAsync(maxQueuedBytes, articleBytes, duration);
            await ReportCapacityBlockedAsync(maxQueuedBytes, articleBytes, duration);
            await ReportCapacityBlockedStabilizeAndRecoverAsync(maxQueuedBytes, articleBytes, TimeSpan.FromSeconds(15));
        }

        private async Task ReportCapacityBlockedStabilizeAndRecoverAsync(int maxQueuedBytes, int articleBytes, TimeSpan blockedDuration)
        {
            var time = TimeProvider.System;
            var queue = new AcceptedArticleQueue(maxQueuedBytes, time);
            var signal = CreateSignal();
            var processor = new BlockingCapacityProcessor();
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                new SpoolPersistenceOptions
                {
                    MinWorkers = 2,
                    MaxWorkers = 2,
                    IneligibleRetryMilliseconds = 60_000,
                    ControlIntervalMilliseconds = 60_000,
                },
                new SpoolPressure(signal),
                new SpoolPersistenceCounters(),
                time,
                signal);
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var wsBefore = process.WorkingSet64;
            var admitted = 0;
            var rejected = 0;
            var peakAccounted = 0L;
            var peakWs = wsBefore;
            var clock = Stopwatch.StartNew();
            var lastWsSample = 0L;
            try
            {
                while (clock.Elapsed < blockedDuration)
                {
                    // Skip allocating a fresh 750 KiB payload when the budget is already exhausted.
                    // Rejected floods in production never materialize a new AcceptedArticle either.
                    if (queue.AccountedBytes + articleBytes > maxQueuedBytes)
                    {
                        rejected++;
                    }
                    else if (queue.TryEnqueue(BuildArticle(admitted + rejected + 1, articleBytes)))
                    {
                        admitted++;
                    }
                    else
                    {
                        rejected++;
                    }

                    peakAccounted = Math.Max(peakAccounted, queue.AccountedBytes);
                    if (clock.Elapsed.TotalSeconds - lastWsSample >= 1.0)
                    {
                        process.Refresh();
                        peakWs = Math.Max(peakWs, process.WorkingSet64);
                        lastWsSample = (long)clock.Elapsed.TotalSeconds;
                        _output.WriteLine(
                            $"memory-blocked-stabilize t={clock.Elapsed.TotalSeconds:0.0}s admitted={admitted} rejected={rejected} "
                            + $"accounted={queue.AccountedBytes} ws={process.WorkingSet64}");
                    }

                    await Task.Yield();
                }

                process.Refresh();
                var wsBlockedEnd = process.WorkingSet64;
                peakWs = Math.Max(peakWs, wsBlockedEnd);

                processor.AllowComplete = true;
                signal.Notify();
                var recoverDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while ((queue.QueuedCount > 0 || queue.InFlightBytes > 0 || queue.AccountedBytes > 0)
                       && DateTime.UtcNow < recoverDeadline)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }

                process.Refresh();
                var wsAfterRecover = process.WorkingSet64;
                // Snapshot working set without forcing GC. Forced collections are not a contract of
                // the capacity envelope and trip cs/call-to-gc in CodeQL quality suites.
                var wsAfterSettle = wsAfterRecover;
                var line =
                    $"memory-blocked-stabilize-recover blockedMs={blockedDuration.TotalMilliseconds:0} articleBytes={articleBytes} "
                    + $"maxQueuedBytes={maxQueuedBytes} admitted={admitted} enqueueRejected={rejected} completed={processor.Completed} "
                    + $"peakAccounted={peakAccounted} finalAccounted={queue.AccountedBytes} finalInFlight={queue.InFlightBytes} "
                    + $"wsBefore={wsBefore} wsBlockedEnd={wsBlockedEnd} wsPeak={peakWs} wsAfterRecover={wsAfterRecover} "
                    + $"wsAfterSettle={wsAfterSettle} privateAfter={process.PrivateMemorySize64} "
                    + $"managed={GC.GetTotalMemory(forceFullCollection: false)} "
                    + "note=working_set_is_not_MaxQueuedBytes; includes runtime_pools_and_OS_retention;no-forced-gc";
                Console.WriteLine(line);
                _output.WriteLine(line);
                Assert.True(admitted > 0);
                Assert.True(rejected > 0);
                Assert.True(peakAccounted <= maxQueuedBytes);
                Assert.Equal(admitted, processor.Completed);
                Assert.Equal(0, queue.AccountedBytes);
            }
            finally
            {
                processor.AllowComplete = true;
                signal.Notify();
                await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task ReportHealthyAsync(int maxQueuedBytes, int articleBytes, TimeSpan duration)
        {
            var time = TimeProvider.System;
            var queue = new AcceptedArticleQueue(maxQueuedBytes, time);
            var processor = new CompletingProcessor();
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                new SpoolPersistenceOptions { MinWorkers = 2, MaxWorkers = 4, ControlIntervalMilliseconds = 50 },
                counters: new SpoolPersistenceCounters(),
                time: time);
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            var process = Process.GetCurrentProcess();
            var wsBefore = process.WorkingSet64;
            var admitted = 0;
            var completed = 0;
            var clock = Stopwatch.StartNew();
            var peakAccounted = 0L;
            try
            {
                while (clock.Elapsed < duration)
                {
                    if (queue.TryEnqueue(BuildArticle(admitted, articleBytes)))
                    {
                        admitted++;
                    }

                    peakAccounted = Math.Max(peakAccounted, queue.AccountedBytes);
                    if (processor.Completed > completed)
                    {
                        completed = processor.Completed;
                    }

                    await Task.Yield();
                }

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while ((queue.QueuedCount > 0 || queue.InFlightBytes > 0 || queue.AccountedBytes > 0)
                    && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }

                process.Refresh();
                var line =
                    $"memory-healthy durationMs={duration.TotalMilliseconds:0} articleBytes={articleBytes} maxQueuedBytes={maxQueuedBytes} "
                    + $"admitted={admitted} completed={processor.Completed} peakAccounted={peakAccounted} "
                    + $"finalAccounted={queue.AccountedBytes} finalInFlight={queue.InFlightBytes} "
                    + $"wsBefore={wsBefore} wsAfter={process.WorkingSet64} privateAfter={process.PrivateMemorySize64} "
                    + $"managed={GC.GetTotalMemory(forceFullCollection: false)}";
                Console.WriteLine(line);
                _output.WriteLine(line);
                Assert.True(admitted > 0);
                Assert.True(processor.Completed > 0);
                Assert.Equal(0, queue.AccountedBytes);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task ReportCapacityBlockedAsync(int maxQueuedBytes, int articleBytes, TimeSpan duration)
        {
            var time = TimeProvider.System;
            var queue = new AcceptedArticleQueue(maxQueuedBytes, time);
            var signal = CreateSignal();
            var processor = new BlockingCapacityProcessor();
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                new SpoolPersistenceOptions
                {
                    MinWorkers = 2,
                    MaxWorkers = 2,
                    IneligibleRetryMilliseconds = 60_000,
                    ControlIntervalMilliseconds = 60_000,
                },
                new SpoolPressure(signal),
                new SpoolPersistenceCounters(),
                time,
                signal);
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            var process = Process.GetCurrentProcess();
            var wsBefore = process.WorkingSet64;
            var admitted = 0;
            var rejected = 0;
            var peakAccounted = 0L;
            var clock = Stopwatch.StartNew();
            try
            {
                while (clock.Elapsed < duration)
                {
                    if (queue.TryEnqueue(BuildArticle(admitted + rejected, articleBytes)))
                    {
                        admitted++;
                    }
                    else
                    {
                        rejected++;
                    }

                    peakAccounted = Math.Max(peakAccounted, queue.AccountedBytes);
                    await Task.Yield();
                }

                process.Refresh();
                var line =
                    $"memory-blocked durationMs={duration.TotalMilliseconds:0} articleBytes={articleBytes} maxQueuedBytes={maxQueuedBytes} "
                    + $"admitted={admitted} enqueueRejected={rejected} peakAccounted={peakAccounted} "
                    + $"finalAccounted={queue.AccountedBytes} finalQueued={queue.QueuedCount} finalInFlight={queue.InFlightBytes} "
                    + $"workers={worker.ActiveWorkers} processorCalls={processor.Calls} completed={processor.Completed} "
                    + $"wsBefore={wsBefore} wsAfter={process.WorkingSet64} privateAfter={process.PrivateMemorySize64} "
                    + $"managed={GC.GetTotalMemory(forceFullCollection: false)}";
                Console.WriteLine(line);
                _output.WriteLine(line);
                Assert.True(admitted > 0);
                Assert.True(rejected > 0);
                Assert.True(peakAccounted <= maxQueuedBytes);
                Assert.Equal(0, processor.Completed);
                Assert.True(queue.AccountedBytes <= maxQueuedBytes);
            }
            finally
            {
                processor.AllowComplete = true;
                signal.Notify();
                await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static SpoolCapacityWaitSignal CreateSignal()
        {
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:mem", [Path.GetTempPath()])]);
            var monitor = new BlockDeviceMonitorService(
                catalog,
                new PlatformBlockDeviceCounterSource(),
                NullLogger<BlockDeviceMonitorService>.Instance,
                TimeSpan.FromHours(1),
                TimeProvider.System,
                freeSpace: null);
            return new SpoolCapacityWaitSignal(monitor);
        }

        private static AcceptedArticle BuildArticle(int index, int bodyBytes)
        {
            var parser = new NntpArticleParser("cache01.usenet.ninja");
            var header =
                "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: <mem-"
                + index
                + "@seg.test>\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: mem\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            var target = Math.Max(headerBytes.Length + 80, bodyBytes);
            var body = new StringBuilder(target - headerBytes.Length);
            const int lineContent = 64;
            while (headerBytes.Length + body.Length + 2 < target)
            {
                var remaining = target - headerBytes.Length - body.Length - 2;
                body.Append('x', Math.Min(lineContent, remaining));
                body.Append("\r\n");
            }

            var created = ArticleRecordFactory.TryCreate(
                parser,
                Encoding.ASCII.GetBytes(header + body),
                ArticlePathMode.Normalize);
            Assert.True(created.IsAccepted);
            return new AcceptedArticle(
                created.Record.ArtId,
                created.Record.ArtHash,
                created.Record.ArtSize,
                created.Record.ArtData);
        }

        private sealed class CompletingProcessor : IAcceptedArticleProcessor
        {
            private int _completed;

            public int Completed => Volatile.Read(ref _completed);

            public void EnsureReady()
            {
            }

            public ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _completed);
                return ValueTask.FromResult(AcceptedArticleProcessResult.Completed);
            }
        }

        private sealed class BlockingCapacityProcessor : IAcceptedArticleProcessor
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
    }
}
