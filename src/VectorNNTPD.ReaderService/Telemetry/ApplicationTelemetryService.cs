using System.Diagnostics;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.Common.Core;
using VectorNNTPD.ReaderService.Diagnostics;
using VectorNNTPD.ReaderService.History;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.Telemetry
{
    /// <summary>
    /// Always-on one-minute application telemetry. Independent of FeedDiagnostics.
    /// </summary>
    public sealed class ApplicationTelemetryService : IApplicationService, IAsyncDisposable
    {
        /// <summary>Production emit period.</summary>
        public static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

        private readonly IArticleIngestionQueue _queue;
        private readonly INntpSessionCensus _census;
        private readonly IHistoryLookupMetrics? _history;
        private readonly TransitConfigurationStore _transit;
        private readonly ITransitPeerMetrics? _peerMetrics;
        private readonly IngestionPipelineMetrics? _pipeline;
        private readonly ILogger<ApplicationTelemetryService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly IAsyncInterval? _injectedInterval;
        private readonly TimeSpan _period;
        private readonly CancellationTokenSource _runCts = new();
        private readonly object _emitGate = new();
        private Task? _execution;
        private int _disposed;
        private int _emitBusy;
        private long _priorLookups;
        private long _priorHits;
        private long _priorMisses;
        private long _priorErrors;
        private long _priorWaitTicks;
        private long _priorAdmissionFailures;
        private long _priorAdmissionWaitTicks;
        private readonly Dictionary<string, PeerPrior> _peerPriors = new(StringComparer.Ordinal);

        /// <summary>Initializes a new instance of the <see cref="ApplicationTelemetryService"/> class.</summary>
        public ApplicationTelemetryService(
            IArticleIngestionQueue queue,
            INntpSessionCensus census,
            ILogger<ApplicationTelemetryService> logger,
            IHistoryLookupMetrics? history = null,
            TimeProvider? timeProvider = null,
            TransitConfigurationStore? transit = null,
            ITransitPeerMetrics? peerMetrics = null,
            IngestionPipelineMetrics? pipelineMetrics = null)
            : this(queue, census, logger, history, timeProvider, interval: null, period: null, transit, peerMetrics, pipelineMetrics)
        {
        }

        /// <summary>Initializes a new instance with an injectable interval (tests).</summary>
        internal ApplicationTelemetryService(
            IArticleIngestionQueue queue,
            INntpSessionCensus census,
            ILogger<ApplicationTelemetryService> logger,
            IHistoryLookupMetrics? history,
            TimeProvider? timeProvider,
            IAsyncInterval? interval,
            TimeSpan? period,
            TransitConfigurationStore? transit = null,
            ITransitPeerMetrics? peerMetrics = null,
            IngestionPipelineMetrics? pipelineMetrics = null)
        {
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(census);
            ArgumentNullException.ThrowIfNull(logger);
            _queue = queue;
            _census = census;
            _history = history;
            _transit = transit ?? new TransitConfigurationStore();
            _peerMetrics = peerMetrics;
            _pipeline = pipelineMetrics;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _injectedInterval = interval;
            _period = period ?? Period;
        }

        /// <inheritdoc />
        public string Name => "ApplicationTelemetry";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <summary>Gets how many snapshots have been emitted (tests).</summary>
        internal int EmitCount { get; private set; }

        /// <summary>Gets whether an emit is currently running (tests).</summary>
        internal bool IsEmitBusy => Volatile.Read(ref _emitBusy) != 0;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _execution = RunAsync(_runCts.Token);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
            if (_execution is null)
            {
                return;
            }

            try
            {
                await _execution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
            {
                _ = ignored; // observed; intentionally ignored at this best-effort boundary
            }

            _runCts.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            var owned = _injectedInterval is null;
            var interval = _injectedInterval ?? new PeriodicTimerInterval(_period, _timeProvider);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var more = await interval.WaitNextAsync(cancellationToken).ConfigureAwait(false);
                    if (!more)
                    {
                        break;
                    }

                    Emit();
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
            finally
            {
                if (owned)
                {
                    await interval.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>Emits one snapshot (tests). Production uses the interval loop only.</summary>
        internal void Emit()
        {
            lock (_emitGate)
            {
                Interlocked.Exchange(ref _emitBusy, 1);
                try
                {
                    EmitCore();
                    EmitCount++;
                }
                finally
                {
                    Interlocked.Exchange(ref _emitBusy, 0);
                }
            }
        }

        private void EmitCore()
        {
            var history = _history?.Capture() ?? default;
            var lookups = NonNegativeDelta(history.Lookups, ref _priorLookups);
            var hits = NonNegativeDelta(history.Hits, ref _priorHits);
            var misses = NonNegativeDelta(history.Misses, ref _priorMisses);
            var errors = NonNegativeDelta(history.Errors, ref _priorErrors);
            var waitMs = TicksToMilliseconds(NonNegativeDelta(history.WaitTicks, ref _priorWaitTicks));
            ApplicationTelemetryLogMessages.HistoryDb(_logger, lookups, hits, misses, errors, waitMs);

            var admitFail = NonNegativeDelta(_queue.AdmissionFailureCount, ref _priorAdmissionFailures);
            var admitWaitMs = TicksToMilliseconds(
                NonNegativeDelta(_queue.AdmissionWaitTicks, ref _priorAdmissionWaitTicks));
            ApplicationTelemetryLogMessages.TransitIngressQueue(
                _logger,
                _queue.Count,
                _queue.QueuedBytes,
                _queue.PeakCount,
                _queue.PeakQueuedBytes,
                _queue.WaitingProducerCount,
                _queue.MemoryLimitBytes,
                admitFail,
                admitWaitMs);

            var sessions = _census.Capture();
            ApplicationTelemetryLogMessages.ActiveSessions(
                _logger,
                sessions.Active,
                sessions.Established,
                sessions.Idle,
                sessions.Receiving,
                sessions.WaitingHistory,
                sessions.WaitingQueue,
                sessions.WaitingWindow,
                sessions.Completing);

            EmitPipeline(sessions);
            EmitConfiguredPeers();
        }

        private void EmitPipeline(NntpSessionCensusSnapshot sessions)
        {
            if (_pipeline is null)
            {
                return;
            }

            var snapshot = _pipeline.CaptureInterval();
            var busyMs = IngestionPipelineSnapshot.TicksToMilliseconds(snapshot.BusyTicks);
            var idleMs = IngestionPipelineSnapshot.TicksToMilliseconds(snapshot.IdleTicks);
            ApplicationTelemetryLogMessages.IngestionWorker(
                _logger,
                snapshot.WorkerItems,
                snapshot.ArticlesPerSecond(_period),
                snapshot.BusyPercent,
                idleMs,
                busyMs,
                snapshot.DequeueWait.P95Ms,
                snapshot.ToPublishStart.P95Ms,
                snapshot.Encode.P95Ms,
                snapshot.News.P95Ms,
                snapshot.WorkerItem.AvgMs,
                snapshot.WorkerItem.P95Ms);
            ApplicationTelemetryLogMessages.OverviewDbHandoff(
                _logger,
                snapshot.GateWait.P95Ms,
                snapshot.Publish.P95Ms,
                snapshot.Confirm.AvgMs,
                snapshot.Confirm.P95Ms,
                snapshot.Handoff.AvgMs,
                snapshot.Handoff.P95Ms,
                snapshot.ConfirmBusyPercent,
                snapshot.ConfirmFailures,
                snapshot.ConfirmTimeouts,
                snapshot.InFlightPublishes,
                snapshot.MaxInFlightPublishes);

            var utilisation = _queue.MemoryLimitBytes <= 0
                ? 0d
                : Math.Clamp(_queue.QueuedBytes / (double)_queue.MemoryLimitBytes, 0d, 1d);
            ApplicationTelemetryLogMessages.IngestionWorkerPool(
                _logger,
                snapshot.CurrentWorkers,
                snapshot.MinWorkers,
                snapshot.MaxWorkers,
                snapshot.ScaleUps,
                snapshot.ScaleDowns,
                utilisation,
                _queue.WaitingProducerCount);

            var overviewStage = _pipeline.CaptureOverviewDbStage();
            ApplicationTelemetryLogMessages.OverviewDbWorkQueue(
                _logger,
                overviewStage.WorkQueueCount,
                overviewStage.WorkQueueBytes,
                overviewStage.WorkQueueWaiting,
                overviewStage.PublisherWorkers,
                overviewStage.MinPublisherWorkers,
                overviewStage.MaxPublisherWorkers,
                overviewStage.PublisherScaleUps,
                overviewStage.PublisherScaleDowns);

            var budgetExhausted = _queue.WaitingProducerCount > 0
                || _queue.QueuedBytes >= _queue.MemoryLimitBytes
                ? 1
                : 0;
            var occupiedMax = Math.Max(sessions.TakeThisOccupiedMax, snapshot.MaxTakeThisOccupied);
            var processingMax = Math.Max(sessions.TakeThisProcessingMax, snapshot.MaxTakeThisProcessing);
            ApplicationTelemetryLogMessages.TakeThisPipeline(
                _logger,
                sessions.TakeThisOccupied,
                occupiedMax,
                sessions.TakeThisFullSessions,
                snapshot.OccupiedFullWaits,
                sessions.TakeThisProcessing,
                processingMax,
                snapshot.EmitGateWait.P95Ms,
                snapshot.TakeThisEnqueue.AvgMs,
                snapshot.TakeThisEnqueue.P95Ms,
                snapshot.BudgetWait.P95Ms,
                snapshot.BudgetWaits,
                budgetExhausted);
        }

        private void EmitConfiguredPeers()
        {
            if (_peerMetrics is null)
            {
                return;
            }

            var peers = _transit.Current.Peers;
            if (peers.Count == 0)
            {
                return;
            }

            foreach (var identifier in peers.Keys.OrderBy(static id => id, StringComparer.Ordinal))
            {
                var policy = peers[identifier];
                var snapshot = _peerMetrics.Capture(identifier);
                if (!_peerPriors.TryGetValue(identifier, out var prior))
                {
                    prior = new PeerPrior();
                    _peerPriors[identifier] = prior;
                }

                var accepted = NonNegativeDelta(snapshot.Accepted, ref prior.Accepted);
                var rejected = NonNegativeDelta(snapshot.Rejected, ref prior.Rejected);
                var articles = NonNegativeDelta(snapshot.ArticlesReceived, ref prior.Articles);
                var bytes = NonNegativeDelta(snapshot.ArticleBytes, ref prior.Bytes);
                var checks = NonNegativeDelta(snapshot.Checks, ref prior.Checks);
                var transmitted = NonNegativeDelta(snapshot.Transmitted, ref prior.Transmitted);
                var average = articles > 0 ? bytes / articles : 0;
                ApplicationTelemetryLogMessages.TransitPeer(
                    _logger,
                    identifier,
                    snapshot.Active,
                    policy.MaxIncomingConnections,
                    snapshot.Peak,
                    accepted,
                    transmitted,
                    rejected,
                    articles,
                    bytes,
                    average,
                    AverageMbps(bytes, _period),
                    checks);
            }
        }

        /// <summary>
        /// Decimal megabits/sec from interval article bytes over the telemetry period.
        /// </summary>
        internal static double AverageMbps(long bytes, TimeSpan interval)
        {
            if (bytes <= 0)
            {
                return 0;
            }

            var seconds = interval.TotalSeconds;
            if (seconds <= 0)
            {
                return 0;
            }

            return bytes * 8d / seconds / 1_000_000d;
        }

        private static long NonNegativeDelta(long current, ref long prior)
        {
            var previous = prior;
            prior = current;
            return current < previous ? 0 : current - previous;
        }

        private static long TicksToMilliseconds(long ticks)
        {
            if (ticks <= 0)
            {
                return 0;
            }

            return (long)Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds;
        }

        private sealed class PeerPrior
        {
            public long Accepted;
            public long Rejected;
            public long Articles;
            public long Bytes;
            public long Checks;
            public long Transmitted;
        }
    }
}
