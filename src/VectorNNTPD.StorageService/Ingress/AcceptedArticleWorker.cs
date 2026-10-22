using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;

namespace VectorNNTPD.StorageService.Ingress
{

    /// <summary>
    /// Consumes <see cref="IAcceptedArticleQueue"/> with a bounded, adaptive set of workers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pool starts at <see cref="SpoolPersistenceOptions.MinWorkers"/> and changes by one
    /// only after <see cref="SpoolPersistenceOptions.ScaleDwellMilliseconds"/> of the same condition.
    /// It does not add workers while <see cref="SpoolPressure.IsSaturated"/> is true.
    /// That flag means no spool can accept another publish. Device load does not set it
    /// and does not by itself scale the pool down. A drained queue still scales down.
    /// </para>
    /// <para>
    /// Each worker holds at most one dequeued article. A waiting-for-capacity result keeps that
    /// article off the queue and retries after <see cref="SpoolPersistenceOptions.IneligibleRetryMilliseconds"/>.
    /// Shutdown cancels in-flight publishes. An article already dequeued and not yet published is
    /// logged and dropped. VATP RESULT was sent at enqueue and is not repeated here.
    /// </para>
    /// </remarks>
    internal sealed class AcceptedArticleWorker : IApplicationService
    {
        private readonly IAcceptedArticleQueue _queue;
        private readonly IAcceptedArticleProcessor _processor;
        private readonly ILogger<AcceptedArticleWorker> _logger;
        private readonly SpoolPersistenceOptions _options;
        private readonly SpoolPressure? _pressure;
        private readonly SpoolPersistenceCounters? _counters;
        private readonly TimeProvider _time;
        private readonly CancellationTokenSource _runCts = new();
        private readonly object _taskGate = new();
        private readonly List<Task> _tasks = [];
        private CancellationTokenSource _wake = new();
        private Task? _execution;
        private int _started;
        private int _active;
        private int _desired;
        private long _scaleArmedAt;
        private int _scaleDirection;
        private long _lastSummaryAt;

        /// <summary>Initializes the host worker from validated options.</summary>
        /// <param name="queue">Accepted-article queue.</param>
        /// <param name="processor">Path rewrite and spool publish.</param>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="options">Worker limits and dwell.</param>
        /// <param name="pressure">Saturation signal. A null signal never reports saturation.</param>
        /// <param name="counters">Optional fault counter.</param>
        /// <param name="time">Clock for dwell and retry delays.</param>
        public AcceptedArticleWorker(
            IAcceptedArticleQueue queue,
            IAcceptedArticleProcessor processor,
            ILogger<AcceptedArticleWorker> logger,
            IOptions<StorageServerOptions> options,
            SpoolPressure pressure,
            SpoolPersistenceCounters counters,
            TimeProvider time)
            : this(
                queue,
                processor,
                logger,
                options.Value.SpoolPersistence ?? new SpoolPersistenceOptions(),
                pressure,
                counters,
                time)
        {
            ArgumentNullException.ThrowIfNull(options);
        }

        /// <summary>Initializes a worker with explicit control settings. Tests use this constructor.</summary>
        /// <param name="queue">Accepted-article queue.</param>
        /// <param name="processor">Article processor.</param>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="options">Worker limits. Null uses <see cref="SpoolPersistenceOptions"/> defaults.</param>
        /// <param name="pressure">Saturation signal.</param>
        /// <param name="counters">Fault counter.</param>
        /// <param name="time">Clock. Null uses <see cref="TimeProvider.System"/>.</param>
        internal AcceptedArticleWorker(
            IAcceptedArticleQueue queue,
            IAcceptedArticleProcessor processor,
            ILogger<AcceptedArticleWorker> logger,
            SpoolPersistenceOptions? options = null,
            SpoolPressure? pressure = null,
            SpoolPersistenceCounters? counters = null,
            TimeProvider? time = null)
        {
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(processor);
            ArgumentNullException.ThrowIfNull(logger);
            _queue = queue;
            _processor = processor;
            _logger = logger;
            _options = options ?? new SpoolPersistenceOptions();
            _pressure = pressure;
            _counters = counters;
            _time = time ?? TimeProvider.System;
        }

        /// <inheritdoc />
        public string Name => "AcceptedArticleWorker";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <summary>Workers currently inside <see cref="WorkerLoop"/>.</summary>
        internal int ActiveWorkers => Volatile.Read(ref _active);

        /// <summary>Target worker count. The control loop moves <see cref="ActiveWorkers"/> toward this.</summary>
        internal int DesiredWorkers => Volatile.Read(ref _desired);

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return Task.CompletedTask;
            }

            _processor.EnsureReady();
            _desired = _options.MinWorkers;
            for (var i = 0; i < _options.MinWorkers; i++)
            {
                StartWorker();
            }

            AcceptedArticleWorkerLogMessages.Started(_logger);
            _execution = RunAsync(_runCts.Token);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
            Wake();
            var execution = _execution;
            if (execution is null)
            {
                return;
            }

            try
            {
                await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        /// <summary>
        /// Applies one scale decision. Production calls this from the control loop.
        /// Tests call it directly so dwell does not depend on a real delay.
        /// </summary>
        internal void EvaluateScaling()
        {
            var depth = _queue.QueuedCount;
            var saturated = _pressure?.IsSaturated ?? false;
            var age = _queue.OldestAge(_time);
            var backlog = depth >= _options.ScaleUpQueueDepth
                || (depth > 0 && age >= TimeSpan.FromMilliseconds(_options.ScaleUpAgeMilliseconds));
            var idle = depth <= _options.ScaleDownQueueDepth;
            var active = Volatile.Read(ref _active);
            var desired = Volatile.Read(ref _desired);
            int direction;
            if (!saturated && backlog && desired < _options.MaxWorkers)
            {
                direction = 1;
            }
            else if (idle && desired > _options.MinWorkers)
            {
                direction = -1;
            }
            else
            {
                _scaleDirection = 0;
                _scaleArmedAt = 0;
                return;
            }

            var now = _time.GetTimestamp();
            if (_scaleDirection != direction || _scaleArmedAt == 0)
            {
                _scaleDirection = direction;
                _scaleArmedAt = now;
                return;
            }

            if (_time.GetElapsedTime(_scaleArmedAt, now) < TimeSpan.FromMilliseconds(_options.ScaleDwellMilliseconds))
            {
                return;
            }

            if (direction > 0)
            {
                var updated = desired + 1;
                if (updated <= _options.MaxWorkers
                    && Interlocked.CompareExchange(ref _desired, updated, desired) == desired)
                {
                    StartWorker();
                    SpoolPersistenceLogMessages.WorkersChanged(_logger, Volatile.Read(ref _active), updated, depth);
                }
            }
            else if (Interlocked.CompareExchange(ref _desired, desired - 1, desired) == desired)
            {
                Wake();
                SpoolPersistenceLogMessages.WorkersChanged(_logger, active, desired - 1, depth);
            }

            _scaleArmedAt = now;
        }

        /// <summary>Control loop. Worker tasks are started before this waits.</summary>
        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_queue.IsCompleted && Volatile.Read(ref _active) == 0)
                    {
                        break;
                    }

                    ObserveCompletedWorkers();
                    EnsureDesiredWorkers();
                    EvaluateScaling();
                    NoteSummary();
                    await Task.Delay(TimeSpan.FromMilliseconds(_options.ControlIntervalMilliseconds), _time, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                Wake();
                Task[] pending;
                lock (_taskGate)
                {
                    pending = _tasks.ToArray();
                }

                try
                {
                    await Task.WhenAll(pending).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AcceptedArticleWorkerLogMessages.ProcessingFailed(_logger, ex, "worker-pool");
                }

                AcceptedArticleWorkerLogMessages.Stopped(_logger);
            }
        }

        /// <summary>Starts a worker when a fault left the pool below the target.</summary>
        private void EnsureDesiredWorkers()
        {
            while (Volatile.Read(ref _active) < Volatile.Read(ref _desired)
                && Volatile.Read(ref _active) < _options.MaxWorkers)
            {
                StartWorker();
            }
        }

        /// <summary>Drops finished tasks from the tracked set. Faults are already counted in the worker.</summary>
        private void ObserveCompletedWorkers()
        {
            lock (_taskGate)
            {
                for (var i = _tasks.Count - 1; i >= 0; i--)
                {
                    if (_tasks[i].IsCompleted)
                    {
                        _tasks.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>Starts one worker and counts it as active before the task runs.</summary>
        private void StartWorker()
        {
            Interlocked.Increment(ref _active);
            var task = Task.Run(() => WorkerLoop(_runCts.Token));
            lock (_taskGate)
            {
                _tasks.Add(task);
            }
        }

        /// <summary>Dequeues and processes until retirement, queue completion, or shutdown.</summary>
        private async Task WorkerLoop(CancellationToken cancellationToken)
        {
            AcceptedArticle? held = null;
            var retired = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (held is null && TryClaimRetirement())
                    {
                        retired = true;
                        return;
                    }

                    AcceptedArticle article;
                    if (held is { } pending)
                    {
                        article = pending;
                    }
                    else
                    {
                        AcceptedArticleDelivery delivery;
                        try
                        {
                            delivery = await DequeueAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (!delivery.HasArticle)
                        {
                            if (cancellationToken.IsCancellationRequested || _queue.IsCompleted)
                            {
                                break;
                            }

                            if (TryClaimRetirement())
                            {
                                retired = true;
                                return;
                            }

                            continue;
                        }

                        article = delivery.Article;
                    }

                    try
                    {
                        var status = await _processor.ProcessAsync(article, cancellationToken).ConfigureAwait(false);
                        if (status == AcceptedArticleProcessResult.WaitingForCapacity)
                        {
                            held = article;
                            await Task.Delay(
                                    TimeSpan.FromMilliseconds(_options.IneligibleRetryMilliseconds),
                                    _time,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            continue;
                        }

                        held = null;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        LogDropped(article);
                        held = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        AcceptedArticleWorkerLogMessages.ProcessingFailed(
                            _logger,
                            ex,
                            article.ArtId.ToLowerHexString());
                        held = null;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _counters?.NoteWorkerFault();
                AcceptedArticleWorkerLogMessages.ProcessingFailed(_logger, ex, "worker");
            }
            finally
            {
                if (held is { } leftover && cancellationToken.IsCancellationRequested)
                {
                    LogDropped(leftover);
                }

                if (!retired)
                {
                    ReleaseActiveOnce();
                }
            }
        }

        /// <summary>Waits for an article or for a scale-down wake. A wake does not drop a queued article.</summary>
        private async ValueTask<AcceptedArticleDelivery> DequeueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_queue.TryDequeue(out var ready))
                {
                    return ready;
                }

                // The channel keeps an article that arrives after this miss, so the wait below cannot lose it.

                CancellationToken wake;
                lock (_taskGate)
                {
                    wake = _wake.Token;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wake);
                try
                {
                    return await _queue.DequeueAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return default;
                }
            }

            throw new OperationCanceledException(cancellationToken);
        }

        /// <summary>
        /// Claims one excess slot when more workers are running than desired.
        /// The caller must exit without decrementing again.
        /// </summary>
        /// <returns><see langword="true"/> when this worker owns the retirement and has already decremented the count.</returns>
        private bool TryClaimRetirement()
        {
            while (true)
            {
                var active = Volatile.Read(ref _active);
                var desired = Volatile.Read(ref _desired);
                if (active <= desired || active <= _options.MinWorkers)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _active, active - 1, active) == active)
                {
                    return true;
                }
            }
        }

        /// <summary>Decrements the active count once when the loop exits.</summary>
        private void ReleaseActiveOnce()
        {
            Interlocked.Decrement(ref _active);
        }

        /// <summary>Unblocks workers parked in <see cref="DequeueAsync"/>.</summary>
        private void Wake()
        {
            CancellationTokenSource previous;
            lock (_taskGate)
            {
                previous = _wake;
                _wake = new CancellationTokenSource();
            }

            previous.Cancel();
        }

        /// <summary>Logs a dequeued article that will not be published.</summary>
        /// <param name="article">Article already removed from the queue.</param>
        private void LogDropped(AcceptedArticle article) =>
            SpoolPersistenceLogMessages.DroppedOnShutdown(_logger, article.ArtId.ToLowerHexString());

        /// <summary>Emits a summary about once per five seconds of monotonic time.</summary>
        private void NoteSummary()
        {
            if (_counters is null)
            {
                return;
            }

            var now = _time.GetTimestamp();
            var previous = Volatile.Read(ref _lastSummaryAt);
            if (previous != 0 && _time.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(5))
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastSummaryAt, now, previous) != previous)
            {
                return;
            }

            SpoolPersistenceLogMessages.Summary(
                _logger,
                _counters.Stored,
                _counters.StoredBytes,
                _counters.Deferred,
                Volatile.Read(ref _active),
                _queue.QueuedCount,
                _queue.QueuedBytes,
                _queue.ReservedBytes);
        }
    }
}
