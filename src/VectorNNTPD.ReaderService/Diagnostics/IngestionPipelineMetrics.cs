using System.Diagnostics;

namespace VectorNNTPD.ReaderService.Diagnostics
{
    /// <summary>
    /// Always-on ingestion-pipeline histograms. Samples are lock-free counters;
    /// ApplicationTelemetry emits one Information snapshot per minute (no per-article logs).
    /// </summary>
    /// <remarks>
    /// OverviewDB publishes use an asynchronous outstanding-confirmation window.
    /// <see cref="RecordPublishAndConfirm"/> records the publish <b>write</b> duration
    /// (not fused with broker ack). <see cref="ObserveInFlightPublishes"/> tracks the
    /// outstanding confirmation count. Confirms remain enabled; crash loss of
    /// outstanding OverviewDB work is acceptable.
    /// </remarks>
    public sealed class IngestionPipelineMetrics
    {
        /// <summary>Process-wide instance used by TAKETHIS and the ingestion queue.</summary>
        public static IngestionPipelineMetrics Shared { get; } = new();

        private readonly DurationHistogram _dequeueWait = new();
        private readonly DurationHistogram _toPublishStart = new();
        private readonly DurationHistogram _encode = new();
        private readonly DurationHistogram _gateWait = new();
        private readonly DurationHistogram _publish = new();
        private readonly DurationHistogram _confirm = new();
        private readonly DurationHistogram _handoff = new();
        private readonly DurationHistogram _news = new();
        private readonly DurationHistogram _workerItem = new();
        private readonly DurationHistogram _emitGateWait = new();
        private readonly DurationHistogram _takeThisEnqueue = new();
        private readonly DurationHistogram _enqueueWait = new();
        private readonly DurationHistogram _budgetWait = new();
        private readonly DurationHistogram _occupiedFullWait = new();

        private long _workerItems;
        private long _confirmFailures;
        private long _confirmTimeouts;
        private long _busyTicks;
        private long _idleTicks;
        private long _occupiedFullWaits;
        private long _budgetWaits;
        private int _maxQueueDepth;
        private int _maxTakeThisOccupied;
        private int _maxTakeThisProcessing;
        private int _currentWorkers;
        private int _minWorkers;
        private int _maxWorkers;
        private int _inFlightPublishes;
        private int _maxInFlightPublishes;
        private long _scaleUps;
        private long _scaleDowns;
        private int _overviewPublisherWorkers;
        private int _overviewPublisherMinWorkers;
        private int _overviewPublisherMaxWorkers;
        private int _overviewWorkCount;
        private long _overviewWorkBytes;
        private int _overviewWorkWaiting;
        private long _overviewPublisherScaleUps;
        private long _overviewPublisherScaleDowns;

        /// <summary>Records time spent blocked in <c>DequeueAsync</c>.</summary>
        public void RecordDequeueWait(long startTimestamp) => _dequeueWait.Record(startTimestamp);

        /// <summary>Records dequeue completion until OverviewDB work-queue enqueue begins.</summary>
        public void RecordToPublishStart(long startTimestamp) => _toPublishStart.Record(startTimestamp);

        /// <summary>Records OverviewArticleV1 encode duration.</summary>
        public void RecordEncode(long startTimestamp) => _encode.Record(startTimestamp);

        /// <summary>Records OverviewDB publisher <c>SemaphoreSlim</c> wait.</summary>
        public void RecordGateWait(long startTimestamp) => _gateWait.Record(startTimestamp);

        /// <summary>
        /// Records OverviewDB publish write duration (async confirms; not fused with broker ack).
        /// </summary>
        public void RecordPublishAndConfirm(long startTimestamp)
        {
            _publish.Record(startTimestamp);
            _confirm.Record(startTimestamp);
        }

        /// <summary>Records the full OverviewDB publisher call including the publish gate.</summary>
        public void RecordHandoff(long startTimestamp) => _handoff.Record(startTimestamp);

        /// <summary>Records news-log <c>Write</c> duration.</summary>
        public void RecordNews(long startTimestamp) => _news.Record(startTimestamp);

        /// <summary>Records an exact news-log duration. Tests use this so the sample is not the wall clock.</summary>
        internal void RecordNews(TimeSpan elapsed) => _news.RecordExact(elapsed);

        /// <summary>Records one worker item from dequeue return through finish or requeue.</summary>
        public void RecordWorkerItem(long startTimestamp)
        {
            _workerItem.Record(startTimestamp);
            Interlocked.Increment(ref _workerItems);
        }

        /// <summary>Adds Stopwatch ticks spent processing a worker item.</summary>
        public void AddBusyTicks(long stopwatchTicks)
        {
            if (stopwatchTicks > 0)
            {
                Interlocked.Add(ref _busyTicks, stopwatchTicks);
            }
        }

        /// <summary>Adds Stopwatch ticks spent waiting for the next dequeue.</summary>
        public void AddIdleTicks(long stopwatchTicks)
        {
            if (stopwatchTicks > 0)
            {
                Interlocked.Add(ref _idleTicks, stopwatchTicks);
            }
        }

        /// <summary>Records a broker nack, return, or other publish failure (not a timeout).</summary>
        public void RecordConfirmFailure() => Interlocked.Increment(ref _confirmFailures);

        /// <summary>Records a publisher-confirm timeout.</summary>
        public void RecordConfirmTimeout() => Interlocked.Increment(ref _confirmTimeouts);

        /// <summary>Records TAKETHIS <c>_emitGate.WaitAsync</c> duration.</summary>
        public void RecordEmitGateWait(long startTimestamp) => _emitGateWait.Record(startTimestamp);

        /// <summary>Records TAKETHIS time spent inside <c>EnqueueAsync</c> while holding <c>_emitGate</c>.</summary>
        public void RecordTakeThisEnqueue(long startTimestamp) => _takeThisEnqueue.Record(startTimestamp);

        /// <summary>Records every ingestion-queue <c>EnqueueAsync</c> wait, including immediate admits.</summary>
        public void RecordEnqueueWaitTicks(long stopwatchTicks) => _enqueueWait.RecordStopwatchTicks(stopwatchTicks);

        /// <summary>Records a producer wait caused by the global byte budget.</summary>
        public void RecordBudgetWaitTicks(long stopwatchTicks)
        {
            Interlocked.Increment(ref _budgetWaits);
            _budgetWait.RecordStopwatchTicks(stopwatchTicks);
        }

        /// <summary>Records time spent in <c>WaitForCapacityAsync</c> because Occupied is at Depth.</summary>
        public void RecordOccupiedFullWait(long startTimestamp)
        {
            Interlocked.Increment(ref _occupiedFullWaits);
            _occupiedFullWait.Record(startTimestamp);
        }

        /// <summary>Tracks the high-water ingestion-queue depth observed by the worker.</summary>
        public void ObserveQueueDepth(int depth)
        {
            if (depth <= 0)
            {
                return;
            }

            var current = Volatile.Read(ref _maxQueueDepth);
            while (depth > current)
            {
                var previous = Interlocked.CompareExchange(ref _maxQueueDepth, depth, current);
                if (previous == current)
                {
                    break;
                }

                current = previous;
            }
        }

        /// <summary>Tracks peak TAKETHIS Occupied and concurrent ProcessArticleAsync counts.</summary>
        public void ObserveTakeThis(int occupied, int processing)
        {
            UpdateMax(ref _maxTakeThisOccupied, occupied);
            UpdateMax(ref _maxTakeThisProcessing, processing);
        }

        /// <summary>Records the configured worker-pool bounds and current worker count.</summary>
        public void ObserveWorkerPool(int currentWorkers, int minWorkers, int maxWorkers)
        {
            Volatile.Write(ref _currentWorkers, Math.Max(0, currentWorkers));
            Volatile.Write(ref _minWorkers, Math.Max(0, minWorkers));
            Volatile.Write(ref _maxWorkers, Math.Max(0, maxWorkers));
        }

        /// <summary>Records the current number of leased OverviewDB publish slots.</summary>
        public void ObserveInFlightPublishes(int inFlight)
        {
            Volatile.Write(ref _inFlightPublishes, Math.Max(0, inFlight));
            UpdateMax(ref _maxInFlightPublishes, inFlight);
        }

        /// <summary>Records one worker-pool scale-up decision.</summary>
        public void RecordScaleUp() => Interlocked.Increment(ref _scaleUps);

        /// <summary>Records one worker-pool scale-down decision.</summary>
        public void RecordScaleDown() => Interlocked.Increment(ref _scaleDowns);

        /// <summary>Records OverviewDB publisher worker-pool bounds and current count.</summary>
        public void ObserveOverviewDbPublisherPool(int currentWorkers, int minWorkers, int maxWorkers)
        {
            Volatile.Write(ref _overviewPublisherWorkers, Math.Max(0, currentWorkers));
            Volatile.Write(ref _overviewPublisherMinWorkers, Math.Max(0, minWorkers));
            Volatile.Write(ref _overviewPublisherMaxWorkers, Math.Max(0, maxWorkers));
        }

        /// <summary>Records OverviewDB in-process work-queue depth gauges.</summary>
        public void ObserveOverviewDbWorkQueue(int count, long queuedBytes, int waitingProducers)
        {
            Volatile.Write(ref _overviewWorkCount, Math.Max(0, count));
            Volatile.Write(ref _overviewWorkBytes, Math.Max(0, queuedBytes));
            Volatile.Write(ref _overviewWorkWaiting, Math.Max(0, waitingProducers));
        }

        /// <summary>Records one OverviewDB publisher scale-up.</summary>
        public void RecordOverviewDbPublisherScaleUp() =>
            Interlocked.Increment(ref _overviewPublisherScaleUps);

        /// <summary>Records one OverviewDB publisher scale-down.</summary>
        public void RecordOverviewDbPublisherScaleDown() =>
            Interlocked.Increment(ref _overviewPublisherScaleDowns);

        /// <summary>
        /// Captures OverviewDB work-queue / publisher-pool gauges and interval scale counters.
        /// </summary>
        public OverviewDbStageSnapshot CaptureOverviewDbStage()
        {
            return new OverviewDbStageSnapshot(
                Volatile.Read(ref _overviewPublisherWorkers),
                Volatile.Read(ref _overviewPublisherMinWorkers),
                Volatile.Read(ref _overviewPublisherMaxWorkers),
                Volatile.Read(ref _overviewWorkCount),
                Volatile.Read(ref _overviewWorkBytes),
                Volatile.Read(ref _overviewWorkWaiting),
                Interlocked.Exchange(ref _overviewPublisherScaleUps, 0),
                Interlocked.Exchange(ref _overviewPublisherScaleDowns, 0));
        }

        /// <summary>Captures interval totals and resets histograms and counters.</summary>
        public IngestionPipelineSnapshot CaptureInterval()
        {
            return new IngestionPipelineSnapshot(
                Interlocked.Exchange(ref _workerItems, 0),
                Interlocked.Exchange(ref _confirmFailures, 0),
                Interlocked.Exchange(ref _confirmTimeouts, 0),
                Interlocked.Exchange(ref _busyTicks, 0),
                Interlocked.Exchange(ref _idleTicks, 0),
                Interlocked.Exchange(ref _occupiedFullWaits, 0),
                Interlocked.Exchange(ref _budgetWaits, 0),
                Interlocked.Exchange(ref _maxQueueDepth, 0),
                Interlocked.Exchange(ref _maxTakeThisOccupied, 0),
                Interlocked.Exchange(ref _maxTakeThisProcessing, 0),
                Volatile.Read(ref _currentWorkers),
                Volatile.Read(ref _minWorkers),
                Volatile.Read(ref _maxWorkers),
                Volatile.Read(ref _inFlightPublishes),
                Interlocked.Exchange(ref _maxInFlightPublishes, 0),
                Interlocked.Exchange(ref _scaleUps, 0),
                Interlocked.Exchange(ref _scaleDowns, 0),
                _dequeueWait.CaptureAndReset(),
                _toPublishStart.CaptureAndReset(),
                _encode.CaptureAndReset(),
                _gateWait.CaptureAndReset(),
                _publish.CaptureAndReset(),
                _confirm.CaptureAndReset(),
                _handoff.CaptureAndReset(),
                _news.CaptureAndReset(),
                _workerItem.CaptureAndReset(),
                _emitGateWait.CaptureAndReset(),
                _takeThisEnqueue.CaptureAndReset(),
                _enqueueWait.CaptureAndReset(),
                _budgetWait.CaptureAndReset(),
                _occupiedFullWait.CaptureAndReset());
        }

        private static void UpdateMax(ref int location, int candidate)
        {
            if (candidate <= 0)
            {
                return;
            }

            var current = Volatile.Read(ref location);
            while (candidate > current)
            {
                var previous = Interlocked.CompareExchange(ref location, candidate, current);
                if (previous == current)
                {
                    return;
                }

                current = previous;
            }
        }
    }

    /// <summary>One telemetry interval of ingestion-pipeline histograms.</summary>
    public readonly struct IngestionPipelineSnapshot
    {
        /// <summary>Initializes a captured interval snapshot.</summary>
        public IngestionPipelineSnapshot(
            long workerItems,
            long confirmFailures,
            long confirmTimeouts,
            long busyTicks,
            long idleTicks,
            long occupiedFullWaits,
            long budgetWaits,
            int maxQueueDepth,
            int maxTakeThisOccupied,
            int maxTakeThisProcessing,
            int currentWorkers,
            int minWorkers,
            int maxWorkers,
            int inFlightPublishes,
            int maxInFlightPublishes,
            long scaleUps,
            long scaleDowns,
            DurationSnapshot dequeueWait,
            DurationSnapshot toPublishStart,
            DurationSnapshot encode,
            DurationSnapshot gateWait,
            DurationSnapshot publish,
            DurationSnapshot confirm,
            DurationSnapshot handoff,
            DurationSnapshot news,
            DurationSnapshot workerItem,
            DurationSnapshot emitGateWait,
            DurationSnapshot takeThisEnqueue,
            DurationSnapshot enqueueWait,
            DurationSnapshot budgetWait,
            DurationSnapshot occupiedFullWait)
        {
            WorkerItems = workerItems;
            ConfirmFailures = confirmFailures;
            ConfirmTimeouts = confirmTimeouts;
            BusyTicks = busyTicks;
            IdleTicks = idleTicks;
            OccupiedFullWaits = occupiedFullWaits;
            BudgetWaits = budgetWaits;
            MaxQueueDepth = maxQueueDepth;
            MaxTakeThisOccupied = maxTakeThisOccupied;
            MaxTakeThisProcessing = maxTakeThisProcessing;
            CurrentWorkers = currentWorkers;
            MinWorkers = minWorkers;
            MaxWorkers = maxWorkers;
            InFlightPublishes = inFlightPublishes;
            MaxInFlightPublishes = maxInFlightPublishes;
            ScaleUps = scaleUps;
            ScaleDowns = scaleDowns;
            DequeueWait = dequeueWait;
            ToPublishStart = toPublishStart;
            Encode = encode;
            GateWait = gateWait;
            Publish = publish;
            Confirm = confirm;
            Handoff = handoff;
            News = news;
            WorkerItem = workerItem;
            EmitGateWait = emitGateWait;
            TakeThisEnqueue = takeThisEnqueue;
            EnqueueWait = enqueueWait;
            BudgetWait = budgetWait;
            OccupiedFullWait = occupiedFullWait;
        }

        /// <summary>Gets worker items completed in the interval.</summary>
        public long WorkerItems { get; }

        /// <summary>Gets OverviewDB publish/confirm failures in the interval.</summary>
        public long ConfirmFailures { get; }

        /// <summary>Gets OverviewDB confirm timeouts in the interval.</summary>
        public long ConfirmTimeouts { get; }

        /// <summary>Gets Stopwatch ticks the worker spent processing items.</summary>
        public long BusyTicks { get; }

        /// <summary>Gets Stopwatch ticks the worker spent waiting to dequeue.</summary>
        public long IdleTicks { get; }

        /// <summary>Gets TAKETHIS <c>WaitForCapacityAsync</c> waits while Occupied was at Depth.</summary>
        public long OccupiedFullWaits { get; }

        /// <summary>Gets producers that waited on the global byte budget.</summary>
        public long BudgetWaits { get; }

        /// <summary>Gets the interval high-water ingestion-queue depth observed by the worker.</summary>
        public int MaxQueueDepth { get; }

        /// <summary>Gets the interval high-water TAKETHIS Occupied count.</summary>
        public int MaxTakeThisOccupied { get; }

        /// <summary>Gets the interval high-water concurrent ProcessArticleAsync count.</summary>
        public int MaxTakeThisProcessing { get; }

        /// <summary>Gets the worker count observed at capture.</summary>
        public int CurrentWorkers { get; }

        /// <summary>Gets the configured minimum worker count.</summary>
        public int MinWorkers { get; }

        /// <summary>Gets the configured maximum worker count.</summary>
        public int MaxWorkers { get; }

        /// <summary>Gets in-flight OverviewDB publishes at capture.</summary>
        public int InFlightPublishes { get; }

        /// <summary>Gets the interval high-water in-flight OverviewDB publishes.</summary>
        public int MaxInFlightPublishes { get; }

        /// <summary>Gets scale-up events in the interval.</summary>
        public long ScaleUps { get; }

        /// <summary>Gets scale-down events in the interval.</summary>
        public long ScaleDowns { get; }

        /// <summary>Gets dequeue-wait samples.</summary>
        public DurationSnapshot DequeueWait { get; }

        /// <summary>Gets dequeue-to-OverviewDB-publish-start samples.</summary>
        public DurationSnapshot ToPublishStart { get; }

        /// <summary>Gets protobuf encode samples.</summary>
        public DurationSnapshot Encode { get; }

        /// <summary>Gets OverviewDB publish-gate wait samples.</summary>
        public DurationSnapshot GateWait { get; }

        /// <summary>Gets fused publish-call samples (same as <see cref="Confirm"/> with tracking).</summary>
        public DurationSnapshot Publish { get; }

        /// <summary>Gets fused confirm-wait samples (same as <see cref="Publish"/> with tracking).</summary>
        public DurationSnapshot Confirm { get; }

        /// <summary>Gets total OverviewDB handoff samples including the gate.</summary>
        public DurationSnapshot Handoff { get; }

        /// <summary>Gets news-log write samples.</summary>
        public DurationSnapshot News { get; }

        /// <summary>Gets total worker-item samples.</summary>
        public DurationSnapshot WorkerItem { get; }

        /// <summary>Gets TAKETHIS emit-gate wait samples.</summary>
        public DurationSnapshot EmitGateWait { get; }

        /// <summary>Gets TAKETHIS <c>EnqueueAsync</c> samples taken while holding the emit gate.</summary>
        public DurationSnapshot TakeThisEnqueue { get; }

        /// <summary>Gets all ingestion-queue <c>EnqueueAsync</c> wait samples.</summary>
        public DurationSnapshot EnqueueWait { get; }

        /// <summary>Gets byte-budget wait samples.</summary>
        public DurationSnapshot BudgetWait { get; }

        /// <summary>Gets Occupied==Depth wait samples.</summary>
        public DurationSnapshot OccupiedFullWait { get; }

        /// <summary>Busy time as a percentage of busy+idle, or 0 when the worker was unused.</summary>
        public double BusyPercent => Percent(BusyTicks, BusyTicks + IdleTicks);

        /// <summary>Confirm-wait time as a percentage of worker busy time.</summary>
        public double ConfirmBusyPercent => Percent(Confirm.SumMs, TicksToMilliseconds(BusyTicks));

        /// <summary>Converts Stopwatch ticks to whole milliseconds.</summary>
        public static long TicksToMilliseconds(long stopwatchTicks)
        {
            if (stopwatchTicks <= 0)
            {
                return 0;
            }

            return (long)Stopwatch.GetElapsedTime(0, stopwatchTicks).TotalMilliseconds;
        }

        /// <summary>Articles per second over <paramref name="period"/>.</summary>
        public double ArticlesPerSecond(TimeSpan period)
        {
            var seconds = period.TotalSeconds;
            if (seconds <= 0 || WorkerItems <= 0)
            {
                return 0;
            }

            return WorkerItems / seconds;
        }

        private static double Percent(long numerator, long denominator)
        {
            if (numerator <= 0 || denominator <= 0)
            {
                return 0;
            }

            return 100d * numerator / denominator;
        }
    }

    /// <summary>Gauge/counter snapshot for the OverviewDB work-queue publisher stage.</summary>
    public readonly struct OverviewDbStageSnapshot
    {
        /// <summary>Initializes a stage snapshot.</summary>
        public OverviewDbStageSnapshot(
            int publisherWorkers,
            int minPublisherWorkers,
            int maxPublisherWorkers,
            int workQueueCount,
            long workQueueBytes,
            int workQueueWaiting,
            long publisherScaleUps,
            long publisherScaleDowns)
        {
            PublisherWorkers = publisherWorkers;
            MinPublisherWorkers = minPublisherWorkers;
            MaxPublisherWorkers = maxPublisherWorkers;
            WorkQueueCount = workQueueCount;
            WorkQueueBytes = workQueueBytes;
            WorkQueueWaiting = workQueueWaiting;
            PublisherScaleUps = publisherScaleUps;
            PublisherScaleDowns = publisherScaleDowns;
        }

        /// <summary>Gets current OverviewDB publisher workers.</summary>
        public int PublisherWorkers { get; }

        /// <summary>Gets configured minimum publisher workers.</summary>
        public int MinPublisherWorkers { get; }

        /// <summary>Gets configured maximum publisher workers.</summary>
        public int MaxPublisherWorkers { get; }

        /// <summary>Gets OverviewDB work-queue item count.</summary>
        public int WorkQueueCount { get; }

        /// <summary>Gets OverviewDB work-queue reserved bytes.</summary>
        public long WorkQueueBytes { get; }

        /// <summary>Gets producers waiting on the OverviewDB work queue.</summary>
        public int WorkQueueWaiting { get; }

        /// <summary>Gets publisher scale-ups in the interval.</summary>
        public long PublisherScaleUps { get; }

        /// <summary>Gets publisher scale-downs in the interval.</summary>
        public long PublisherScaleDowns { get; }
    }

    /// <summary>Bucketed duration summary for one telemetry interval.</summary>
    public readonly struct DurationSnapshot
    {
        /// <summary>Initializes a duration snapshot.</summary>
        public DurationSnapshot(long count, double avgMs, long p50Ms, long p95Ms, long maxMs, long sumMs)
        {
            Count = count;
            AvgMs = avgMs;
            P50Ms = p50Ms;
            P95Ms = p95Ms;
            MaxMs = maxMs;
            SumMs = sumMs;
        }

        /// <summary>Gets the number of samples.</summary>
        public long Count { get; }

        /// <summary>Gets the arithmetic mean in milliseconds.</summary>
        public double AvgMs { get; }

        /// <summary>Gets an estimated 50th percentile in milliseconds.</summary>
        public long P50Ms { get; }

        /// <summary>Gets an estimated 95th percentile in milliseconds.</summary>
        public long P95Ms { get; }

        /// <summary>Gets the maximum sample in milliseconds.</summary>
        public long MaxMs { get; }

        /// <summary>Gets the summed sample duration in milliseconds.</summary>
        public long SumMs { get; }
    }

    /// <summary>Lock-free millisecond histogram used by <see cref="IngestionPipelineMetrics"/>.</summary>
    internal sealed class DurationHistogram
    {
        private static readonly double[] UpperMs =
        [
            1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000,
        ];

        private readonly long[] _buckets = new long[UpperMs.Length + 1];
        private long _count;
        private long _sumTicks;
        private long _maxTicks;

        public void Record(long startTimestamp) =>
            RecordElapsed(Stopwatch.GetElapsedTime(startTimestamp));

        /// <summary>Records <paramref name="elapsed"/> without reading the clock again.</summary>
        internal void RecordExact(TimeSpan elapsed) => RecordElapsed(elapsed);

        public void RecordStopwatchTicks(long stopwatchTicks)
        {
            if (stopwatchTicks <= 0)
            {
                RecordElapsed(TimeSpan.Zero);
                return;
            }

            RecordElapsed(Stopwatch.GetElapsedTime(0, stopwatchTicks));
        }

        public DurationSnapshot CaptureAndReset()
        {
            var count = Interlocked.Exchange(ref _count, 0);
            var sumTicks = Interlocked.Exchange(ref _sumTicks, 0);
            var maxTicks = Interlocked.Exchange(ref _maxTicks, 0);
            var buckets = new long[_buckets.Length];
            for (var i = 0; i < _buckets.Length; i++)
            {
                buckets[i] = Interlocked.Exchange(ref _buckets[i], 0);
            }

            if (count <= 0)
            {
                return default;
            }

            var sumMs = (long)Stopwatch.GetElapsedTime(0, sumTicks).TotalMilliseconds;
            var maxMs = (long)Math.Ceiling(Stopwatch.GetElapsedTime(0, maxTicks).TotalMilliseconds);
            var avgMs = sumMs / (double)count;
            return new DurationSnapshot(
                count,
                avgMs,
                PercentileMs(buckets, count, 0.50),
                PercentileMs(buckets, count, 0.95),
                maxMs,
                sumMs);
        }

        private void RecordElapsed(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            var stopwatchTicks = (long)(elapsed.TotalSeconds * Stopwatch.Frequency);
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _sumTicks, stopwatchTicks);
            UpdateMax(stopwatchTicks);
            var ms = elapsed.TotalMilliseconds;
            var index = UpperMs.Length;
            for (var i = 0; i < UpperMs.Length; i++)
            {
                if (ms <= UpperMs[i])
                {
                    index = i;
                    break;
                }
            }

            Interlocked.Increment(ref _buckets[index]);
        }

        private void UpdateMax(long stopwatchTicks)
        {
            var current = Volatile.Read(ref _maxTicks);
            while (stopwatchTicks > current)
            {
                var previous = Interlocked.CompareExchange(ref _maxTicks, stopwatchTicks, current);
                if (previous == current)
                {
                    return;
                }

                current = previous;
            }
        }

        private static long PercentileMs(long[] buckets, long count, double percentile)
        {
            var target = Math.Max(1, (long)Math.Ceiling(count * percentile));
            long cumulative = 0;
            for (var i = 0; i < buckets.Length; i++)
            {
                cumulative += buckets[i];
                if (cumulative >= target)
                {
                    return i < UpperMs.Length ? (long)UpperMs[i] : (long)UpperMs[^1];
                }
            }

            return (long)UpperMs[^1];
        }
    }
}
