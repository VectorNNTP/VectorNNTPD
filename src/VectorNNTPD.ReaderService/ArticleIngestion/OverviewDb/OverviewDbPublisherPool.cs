using VectorNNTPD.Common.Core;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Diagnostics;

namespace VectorNNTPD.ReaderService.ArticleIngestion.OverviewDb
{
    /// <summary>
    /// Dynamically sized pool that drains <see cref="IOverviewDbWorkQueue"/> and
    /// publishes through <see cref="IOverviewDbHandoffPublisher"/> with asynchronous confirms.
    /// </summary>
    /// <remarks>
    /// Workers do not await broker confirmation per message. They wait only for outstanding
    /// confirmation-window capacity and the network write. Nack/return failures are drained
    /// from the publisher and requeued in-process (bounded; abandoned on shutdown).
    /// </remarks>
    internal sealed class OverviewDbPublisherPool
    {
        private readonly IOverviewDbWorkQueue _queue;
        private readonly IOverviewDbHandoffPublisher _publisher;
        private readonly ArticleIngestionOptions _options;
        private readonly ILogger _logger;
        private readonly IngestionPipelineMetrics? _pipeline;
        private readonly TimeProvider _time;
        private readonly object _gate = new();
        private readonly List<Worker> _workers = [];
        private int _highPressureStreak;
        private int _lowPressureStreak;
        private int _scaleUps;
        private int _scaleDowns;

        /// <summary>Initializes a new OverviewDB publisher pool.</summary>
        public OverviewDbPublisherPool(
            IOverviewDbWorkQueue queue,
            IOverviewDbHandoffPublisher publisher,
            ArticleIngestionOptions options,
            ILogger logger,
            IngestionPipelineMetrics? pipelineMetrics = null,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(publisher);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            _queue = queue;
            _publisher = publisher;
            _options = options;
            _logger = logger;
            _pipeline = pipelineMetrics;
            _time = timeProvider ?? TimeProvider.System;
        }

        /// <summary>Gets the current publisher worker count.</summary>
        public int WorkerCount
        {
            get
            {
                lock (_gate)
                {
                    return _workers.Count;
                }
            }
        }

        /// <summary>Gets scale-up count (tests).</summary>
        public int ScaleUpCount => Volatile.Read(ref _scaleUps);

        /// <summary>Gets scale-down count (tests).</summary>
        public int ScaleDownCount => Volatile.Read(ref _scaleDowns);

        /// <summary>Runs publisher workers and the scaler until cancelled.</summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            OverviewDbPublisherPoolLogMessages.PoolStarting(
                _logger,
                _options.OverviewDbMinPublisherWorkers,
                _options.OverviewDbMaxPublisherWorkers,
                _options.OverviewDbPublisherBatchSize);

            for (var i = 0; i < _options.OverviewDbMinPublisherWorkers; i++)
            {
                AddWorker(cancellationToken);
            }

            PublishPoolObservation();

            var scaler = Task.Run(() => ScaleLoopAsync(cancellationToken), CancellationToken.None);
            try
            {
                await scaler.ConfigureAwait(false);
            }
            finally
            {
                List<Worker> snapshot;
                lock (_gate)
                {
                    snapshot = [.. _workers];
                    _workers.Clear();
                }

                foreach (var worker in snapshot)
                {
                    await worker.Cts.CancelAsync().ConfigureAwait(false);
                }

                var drainTimeout = TimeSpan.FromSeconds(
                    Math.Max(1, _options.OverviewDbPublisherShutdownSeconds));
                using var drainCts = new CancellationTokenSource(drainTimeout);
                foreach (var worker in snapshot)
                {
                    try
                    {
                        await worker.Execution.WaitAsync(drainCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex)
                    {
                        _ = ex; // observed; intentionally ignored at this boundary
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ex)
                    {
                        OverviewDbPublisherPoolLogMessages.WorkerFaulted(_logger, ex);
                    }
                    finally
                    {
                        worker.Cts.Dispose();
                    }
                }

                // Bounded shutdown: abandon any remaining outstanding confirms.
                _publisher.AbandonOutstanding();
                PublishPoolObservation();
                OverviewDbPublisherPoolLogMessages.PoolStopped(_logger, snapshot.Count);
            }
        }

        /// <summary>Applies one scaling decision (tests).</summary>
        internal void ApplyScaleDecisionForTests(IngestionPressureSnapshot snapshot, CancellationToken workerToken)
        {
            ApplyScaleDecision(snapshot, workerToken);
            PublishPoolObservation();
        }

        private async Task ScaleLoopAsync(CancellationToken cancellationToken)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, _options.ScaleIntervalSeconds));
            using var timer = new PeriodicTimer(interval, _time);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    ApplyScaleDecision(SamplePressure(), cancellationToken);
                    PublishPoolObservation();
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
        }

        private IngestionPressureSnapshot SamplePressure()
        {
            var limit = _queue.MemoryLimitBytes;
            var bytes = _queue.QueuedBytes;
            var waiting = _queue.WaitingProducerCount;
            var utilisation = limit <= 0 ? 0d : Math.Clamp(bytes / (double)limit, 0d, 1d);
            var waitingSignal = waiting > 0 ? 1d : 0d;
            return new IngestionPressureSnapshot(
                Math.Max(utilisation, waitingSignal),
                bytes,
                limit,
                _queue.Count,
                waiting);
        }

        private void ApplyScaleDecision(IngestionPressureSnapshot snapshot, CancellationToken workerToken)
        {
            if (snapshot.Pressure >= _options.ScaleUpPressureThreshold)
            {
                _highPressureStreak++;
                _lowPressureStreak = 0;
                if (_highPressureStreak >= _options.ScaleUpConsecutiveIntervals)
                {
                    _highPressureStreak = 0;
                    if (TryAddWorker(workerToken))
                    {
                        Interlocked.Increment(ref _scaleUps);
                        _pipeline?.RecordOverviewDbPublisherScaleUp();
                        OverviewDbPublisherPoolLogMessages.ScaledUp(_logger, WorkerCount, snapshot.Pressure);
                    }
                }

                return;
            }

            if (snapshot.Pressure <= _options.ScaleDownPressureThreshold)
            {
                _lowPressureStreak++;
                _highPressureStreak = 0;
                if (_lowPressureStreak >= _options.ScaleDownConsecutiveIntervals)
                {
                    _lowPressureStreak = 0;
                    if (TryRemoveWorker())
                    {
                        Interlocked.Increment(ref _scaleDowns);
                        _pipeline?.RecordOverviewDbPublisherScaleDown();
                        OverviewDbPublisherPoolLogMessages.ScaledDown(_logger, WorkerCount, snapshot.Pressure);
                    }
                }

                return;
            }

            _highPressureStreak = 0;
            _lowPressureStreak = 0;
        }

        private bool TryAddWorker(CancellationToken workerToken)
        {
            lock (_gate)
            {
                if (_workers.Count >= _options.OverviewDbMaxPublisherWorkers)
                {
                    return false;
                }

                AddWorkerAlreadyLocked(workerToken);
                return true;
            }
        }

        private void AddWorker(CancellationToken workerToken)
        {
            lock (_gate)
            {
                AddWorkerAlreadyLocked(workerToken);
            }
        }

        private void AddWorkerAlreadyLocked(CancellationToken workerToken)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(workerToken);
            var worker = new Worker(cts) { Execution = Task.CompletedTask };
            worker.Execution = Task.Run(() => WorkerLoopAsync(worker, cts.Token), CancellationToken.None);
            _workers.Add(worker);
        }

        private bool TryRemoveWorker()
        {
            Worker? victim;
            lock (_gate)
            {
                if (_workers.Count <= _options.OverviewDbMinPublisherWorkers)
                {
                    return false;
                }

                victim = _workers[^1];
                _workers.RemoveAt(_workers.Count - 1);
            }

            _ = RetireWorkerAsync(victim);
            return true;
        }

        private async Task RetireWorkerAsync(Worker worker)
        {
            try
            {
                await worker.Cts.CancelAsync().ConfigureAwait(false);
                await worker.Execution.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                OverviewDbPublisherPoolLogMessages.WorkerFaulted(_logger, ex);
            }
            finally
            {
                worker.Cts.Dispose();
                PublishPoolObservation();
            }
        }

        private async Task WorkerLoopAsync(Worker worker, CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    DrainPublishFailures();

                    OverviewDbWorkItem? item;
                    try
                    {
                        item = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        if (!_queue.IsAccepting)
                        {
                            await DrainRemainingAsync().ConfigureAwait(false);
                        }

                        DrainPublishFailures();
                        break;
                    }

                    if (item is null)
                    {
                        DrainPublishFailures();
                        break;
                    }

                    await PublishOneAsync(item, cancellationToken).ConfigureAwait(false);
                    DrainPublishFailures();

                    if (cancellationToken.IsCancellationRequested && _queue.Count == 0)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                OverviewDbPublisherPoolLogMessages.WorkerFaulted(_logger, ex);
            }
        }

        private async Task DrainRemainingAsync()
        {
            while (true)
            {
                DrainPublishFailures();
                OverviewDbWorkItem? item;
                try
                {
                    // Channel is completed; None only ends the read when the writer is done and empty.
                    item = await _queue.DequeueAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (item is null)
                {
                    break;
                }

                await PublishAcceptedDuringDrainAsync(item).ConfigureAwait(false);
            }

            DrainPublishFailures();
        }

        private async Task PublishOneAsync(OverviewDbWorkItem item, CancellationToken cancellationToken)
        {
            try
            {
                // Does not await broker confirmation — only outstanding-window capacity + write.
                await _publisher.PublishAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && _queue.IsAccepting)
            {
                _ = ex; // observed; intentionally ignored at this boundary
                // Queue still accepting: cooperative cancel abandons this in-flight publish.
                // Do not claim the OverviewDB work queue is unavailable — cancellation is not
                // a queue-admission failure.
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && !_queue.IsAccepting)
            {
                _ = ex; // observed; intentionally ignored at this boundary
                // Queue completed: this item was already dequeued. DrainRemainingAsync only
                // covers items still in the channel; finish this one under the drain budget.
                await PublishAcceptedDuringDrainAsync(item).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                // Local serialization / publish-write failures (e.g. WireFormattingException)
                // never reached the broker; treat as publish failure + in-memory requeue.
                OverviewDbHandoffLogMessages.PublishFailed(_logger, ex, item.MessageId, item.ByteLength);
                await TryRequeueFailureAsync(item).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Publishes one accepted item after <see cref="IOverviewDbWorkQueue.Complete"/> using the
        /// configured OverviewDB publisher shutdown budget instead of an uncancellable token.
        /// </summary>
        private async Task PublishAcceptedDuringDrainAsync(OverviewDbWorkItem item)
        {
            var drainSeconds = Math.Max(1, _options.OverviewDbPublisherShutdownSeconds);
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(drainSeconds));
            try
            {
                await _publisher.PublishAsync(item, drainCts.Token).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                // Timed-out or failed drain publish: requeue only while still accepting; otherwise log.
                OverviewDbHandoffLogMessages.PublishFailed(_logger, ex, item.MessageId, item.ByteLength);
                await TryRequeueFailureAsync(item).ConfigureAwait(false);
            }
        }

        private void DrainPublishFailures()
        {
            while (_publisher.TryDequeuePublishFailure(out var failed))
            {
                OverviewDbHandoffLogMessages.PublishFailed(
                    _logger,
                    new InvalidOperationException("OverviewDB publish was nacked or returned."),
                    failed.MessageId,
                    failed.ByteLength);
                // Fire-and-forget requeue onto the in-process work queue while accepting.
                _ = TryRequeueFailureAsync(failed);
            }
        }

        private async Task TryRequeueFailureAsync(OverviewDbWorkItem item)
        {
            if (!_queue.IsAccepting)
            {
                OverviewDbHandoffLogMessages.RequeueUnavailable(_logger, item.MessageId);
                return;
            }

            var result = await _queue.EnqueueAsync(item, CancellationToken.None).ConfigureAwait(false);
            if (result == ArticleEnqueueResult.Accepted)
            {
                OverviewDbHandoffLogMessages.Requeued(_logger, item.MessageId);
                return;
            }

            if (result == ArticleEnqueueResult.Unavailable)
            {
                OverviewDbHandoffLogMessages.RequeueUnavailable(_logger, item.MessageId);
                return;
            }

            // Rejected (e.g. oversized) — not an OverviewDB work-queue unavailable condition.
            OverviewDbHandoffLogMessages.PublishFailed(
                _logger,
                new InvalidOperationException($"OverviewDB work-queue rejected requeue ({result})."),
                item.MessageId,
                item.ByteLength);
        }

        private void PublishPoolObservation()
        {
            _pipeline?.ObserveOverviewDbPublisherPool(
                WorkerCount,
                _options.OverviewDbMinPublisherWorkers,
                _options.OverviewDbMaxPublisherWorkers);
            _pipeline?.ObserveOverviewDbWorkQueue(_queue.Count, _queue.QueuedBytes, _queue.WaitingProducerCount);
        }

        private sealed class Worker(CancellationTokenSource cts)
        {
            public CancellationTokenSource Cts { get; } = cts;

            public Task Execution { get; set; } = Task.CompletedTask;
        }
    }
}
