using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Diagnostics;

namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// Dynamically sized pool of ingestion workers that drain
    /// <see cref="IArticleIngestionQueue"/> concurrently.
    /// </summary>
    /// <remarks>
    /// Each worker processes one article at a time. OverviewDB RabbitMQ publication
    /// is performed by a separate publisher pool; article workers only enqueue onto
    /// the in-process OverviewDB work queue. Scaling uses sustained queue pressure
    /// with hysteresis. A single worker fault does not stop the pool.
    /// </remarks>
    internal sealed class IngestionWorkerPool
    {
        private readonly IArticleIngestionQueue _queue;
        private readonly Func<InboundArticle, long, CancellationToken, Task> _processArticleAsync;
        private readonly Func<IngestionPressureSnapshot> _samplePressure;
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

        /// <summary>Initializes a new ingestion worker pool.</summary>
        public IngestionWorkerPool(
            IArticleIngestionQueue queue,
            Func<InboundArticle, long, CancellationToken, Task> processArticleAsync,
            ArticleIngestionOptions options,
            ILogger logger,
            IngestionPipelineMetrics? pipelineMetrics = null,
            Func<IngestionPressureSnapshot>? samplePressure = null,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(processArticleAsync);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            _queue = queue;
            _processArticleAsync = processArticleAsync;
            _options = options;
            _logger = logger;
            _pipeline = pipelineMetrics;
            _samplePressure = samplePressure ?? (() => IngestionPressureSnapshot.FromQueue(queue));
            _time = timeProvider ?? TimeProvider.System;
        }

        /// <summary>Gets the current worker count.</summary>
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

        /// <summary>Gets how many scale-up decisions have been applied (tests).</summary>
        public int ScaleUpCount => Volatile.Read(ref _scaleUps);

        /// <summary>Gets how many scale-down decisions have been applied (tests).</summary>
        public int ScaleDownCount => Volatile.Read(ref _scaleDowns);

        /// <summary>Runs workers and the scaler until <paramref name="cancellationToken"/> is cancelled.</summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            IngestionWorkerPoolLogMessages.PoolStarting(
                _logger,
                _options.MinWorkers,
                _options.MaxWorkers,
                _options.MaxPublishConcurrency);

            for (var i = 0; i < _options.MinWorkers; i++)
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

                foreach (var worker in snapshot)
                {
                    try
                    {
                        await worker.Execution.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex)
                    {
                        _ = ex; // observed; intentionally ignored at this boundary
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ex)
                    {
                        IngestionWorkerPoolLogMessages.WorkerFaulted(_logger, ex);
                    }
                    finally
                    {
                        worker.Cts.Dispose();
                    }
                }

                PublishPoolObservation();
                IngestionWorkerPoolLogMessages.PoolStopped(_logger, snapshot.Count);
            }
        }

        /// <summary>Applies one scaling decision from <paramref name="snapshot"/> (tests).</summary>
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
                    var snapshot = _samplePressure();
                    ApplyScaleDecision(snapshot, cancellationToken);
                    PublishPoolObservation();
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
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
                        _pipeline?.RecordScaleUp();
                        IngestionWorkerPoolLogMessages.ScaledUp(_logger, WorkerCount, snapshot.Pressure);
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
                        _pipeline?.RecordScaleDown();
                        IngestionWorkerPoolLogMessages.ScaledDown(_logger, WorkerCount, snapshot.Pressure);
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
                if (_workers.Count >= _options.MaxWorkers)
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
            var worker = new Worker(cts)
            {
                Execution = Task.CompletedTask,
            };
            worker.Execution = Task.Run(() => WorkerLoopAsync(worker, cts.Token), CancellationToken.None);
            _workers.Add(worker);
        }

        private bool TryRemoveWorker()
        {
            Worker? victim;
            lock (_gate)
            {
                if (_workers.Count <= _options.MinWorkers)
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
                IngestionWorkerPoolLogMessages.WorkerFaulted(_logger, ex);
            }
            finally
            {
                worker.Cts.Dispose();
                PublishPoolObservation();
            }
        }

        /// <summary>
        /// Processes queued articles until this worker is cancelled.
        /// </summary>
        /// <remarks>
        /// Graceful shutdown completes the queue before cancelling workers. Cancellation
        /// observed while blocked in <see cref="IArticleIngestionQueue.DequeueAsync"/>, or
        /// between articles, drains leftovers without the worker token.
        /// <see cref="IArticleIngestionQueue.Count"/> is approximate and is not proof the
        /// channel is empty. Scale-down cancels one worker while the queue is still
        /// accepting; that worker exits without draining.
        /// </remarks>
        private async Task WorkerLoopAsync(Worker worker, CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    InboundArticle? article;
                    var idleStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        article = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        _pipeline?.AddIdleTicks(System.Diagnostics.Stopwatch.GetTimestamp() - idleStart);
                        _pipeline?.RecordDequeueWait(idleStart);
                        await DrainIfShuttingDownAsync().ConfigureAwait(false);
                        break;
                    }

                    _pipeline?.AddIdleTicks(System.Diagnostics.Stopwatch.GetTimestamp() - idleStart);
                    _pipeline?.RecordDequeueWait(idleStart);

                    if (article is null)
                    {
                        break;
                    }

                    await ProcessOneAsync(article).ConfigureAwait(false);

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }

                    await DrainIfShuttingDownAsync().ConfigureAwait(false);
                    break;
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                IngestionWorkerPoolLogMessages.WorkerFaulted(_logger, ex);
            }
        }

        /// <summary>
        /// Processes articles still queued after graceful shutdown has stopped admission.
        /// </summary>
        /// <remarks>
        /// No-ops while the queue is accepting, so scale-down cancellation does not steal
        /// work from the remaining workers. Concurrent callers are safe: each article is
        /// dequeued by one caller, and a completed empty queue returns null.
        /// </remarks>
        private async Task DrainIfShuttingDownAsync()
        {
            if (!_queue.IsAccepting)
            {
                await DrainRemainingAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Dequeues and processes until the queue is completed and empty.
        /// </summary>
        /// <remarks>
        /// Uses <see cref="CancellationToken.None"/> so worker cancellation cannot abandon
        /// an article that was already accepted. Safe to run from more than one worker.
        /// </remarks>
        private async Task DrainRemainingAsync()
        {
            while (true)
            {
                InboundArticle? article;
                try
                {
                    article = await _queue.DequeueAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (article is null)
                {
                    break;
                }

                await ProcessOneAsync(article).ConfigureAwait(false);
            }
        }

        private async Task ProcessOneAsync(InboundArticle article)
        {
            var itemStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _pipeline?.ObserveQueueDepth(_queue.Count + 1);
            try
            {
                await _processArticleAsync(article, itemStart, CancellationToken.None).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                IngestionWorkerPoolLogMessages.WorkerFaulted(_logger, ex);
            }
            finally
            {
                _pipeline?.AddBusyTicks(System.Diagnostics.Stopwatch.GetTimestamp() - itemStart);
                _pipeline?.RecordWorkerItem(itemStart);
            }
        }

        private void PublishPoolObservation()
        {
            _pipeline?.ObserveWorkerPool(WorkerCount, _options.MinWorkers, _options.MaxWorkers);
        }

        private sealed class Worker(CancellationTokenSource cts)
        {
            public CancellationTokenSource Cts { get; } = cts;

            public required Task Execution { get; set; }
        }
    }
}
