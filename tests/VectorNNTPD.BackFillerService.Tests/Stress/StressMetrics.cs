using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>Thread-safe counters and latency samples for one stress run.</summary>
    public sealed class StressMetrics
    {
        /// <summary>End-to-end success latency samples (schedule to NNTP complete).</summary>
        private readonly ConcurrentBag<long> _endToEndSuccessLatenciesMs = [];

        /// <summary>End-to-end failure latency samples.</summary>
        private readonly ConcurrentBag<long> _endToEndFailureLatenciesMs = [];

        /// <summary>Harness enqueue-to-publish-complete samples.</summary>
        private readonly ConcurrentBag<long> _scheduleToPublishMs = [];

        /// <summary>BasicPublishAsync duration samples.</summary>
        private readonly ConcurrentBag<long> _publishCallMs = [];

        /// <summary>Publish-complete to reply-received samples.</summary>
        private readonly ConcurrentBag<long> _publishToReplyMs = [];

        /// <summary>Success reply to ARTICLE-start samples.</summary>
        private readonly ConcurrentBag<long> _replyToNntpStartMs = [];

        /// <summary>NNTP ARTICLE multiline consume duration samples.</summary>
        private readonly ConcurrentBag<long> _nntpDeliveryLatenciesMs = [];

        /// <summary>Logical article byte sizes for delivered articles.</summary>
        private readonly ConcurrentBag<long> _articleSizeBytes = [];

        /// <summary>Counts keyed by <see cref="StressFailureClass"/> name.</summary>
        private readonly ConcurrentDictionary<string, long> _failureBuckets = new(StringComparer.Ordinal);

        /// <summary>Per-backbone counter shards.</summary>
        private readonly ConcurrentDictionary<string, BackboneCounters> _byBackbone = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Time series of throughput snapshots.</summary>
        private readonly ConcurrentQueue<ThroughputSample> _throughputSamples = new();

        /// <summary>Time series of queue depth snapshots.</summary>
        private readonly ConcurrentQueue<QueueDepthSample> _queueDepthSamples = new();

        /// <summary>Total work requests started.</summary>
        private long _requestsStarted;

        /// <summary>Article Work replies with success outcome.</summary>
        private long _workSuccess;

        /// <summary>Article Work article-not-found outcomes.</summary>
        private long _workNotFound;

        /// <summary>Other Article Work failure outcomes.</summary>
        private long _workOtherFailure;

        /// <summary>Successful NNTP listener deliveries.</summary>
        private long _articlesDelivered;

        /// <summary>Sum of logical payload bytes delivered.</summary>
        private long _logicalPayloadBytes;

        /// <summary>Sum of on-wire payload bytes delivered.</summary>
        private long _wirePayloadBytes;

        /// <summary>NNTP 430/404 class not-found deliveries.</summary>
        private long _nntpNotFound;

        /// <summary>NNTP errors excluding clean not-found.</summary>
        private long _nntpErrors;

        /// <summary>Work or delivery timeouts recorded.</summary>
        private long _timeouts;

        /// <summary>Disconnect failures during delivery.</summary>
        private long _disconnects;

        /// <summary>Current publisher-side outstanding requests.</summary>
        private long _outstanding;

        /// <summary>Peak publisher-side outstanding.</summary>
        private long _peakOutstanding;

        /// <summary>Current NNTP deliveries in flight.</summary>
        private long _deliveryOutstanding;

        /// <summary>Peak NNTP deliveries in flight.</summary>
        private long _peakDeliveryOutstanding;

        /// <summary>Running sum of outstanding for mean calculation.</summary>
        private long _outstandingSum;

        /// <summary>Number of outstanding samples taken.</summary>
        private long _outstandingSamples;

        /// <summary>Active pooled NNTP connections.</summary>
        private long _activeConnections;

        /// <summary>Peak active pooled connections.</summary>
        private long _peakConnections;

        /// <summary>Incomplete multiline NNTP responses.</summary>
        private long _incompleteResponses;

        /// <summary>ReplyTo dispose drain duration in milliseconds.</summary>
        private long _drainMilliseconds;

        /// <summary>Late replies after caller timeout (from stress client).</summary>
        private long _lateRepliesAfterTimeout;

        /// <summary>Broker outstanding at dispose when drain timed out.</summary>
        private long _unresolvedAtDispose;

        /// <summary>Broker outstanding recorded at end of drain window.</summary>
        private long _brokerOutstandingAtDrainEnd;

        /// <summary>Human-readable run label (phase, backbone, concurrency).</summary>
        public required string RunName { get; init; }

        /// <summary>Provider backbone under test, when single-backbone.</summary>
        public string? Backbone { get; init; }

        /// <summary>Configured publisher worker count for this run.</summary>
        public int ConfiguredConcurrency { get; init; }

        /// <summary>Configured in-flight request cap (semaphore limit).</summary>
        public int ConfiguredMaxInFlight { get; init; }

        /// <summary><see cref="Stopwatch"/> timestamp marking steady-state measurement start.</summary>
        public long SteadyStateStartTimestamp { get; set; }

        /// <summary><see cref="Stopwatch"/> timestamp marking steady-state measurement end.</summary>
        public long SteadyStateEndTimestamp { get; set; }

        /// <summary>Increments started count and current outstanding; updates peak outstanding.</summary>
        public void RequestStarted()
        {
            Interlocked.Increment(ref _requestsStarted);
            var outstanding = Interlocked.Increment(ref _outstanding);
            UpdatePeak(ref _peakOutstanding, outstanding);
        }

        /// <summary>Decrements publisher-side outstanding when a work request completes.</summary>
        public void RequestFinished() => Interlocked.Decrement(ref _outstanding);

        /// <summary>Current in-flight work requests (publisher side).</summary>
        public long Outstanding => Interlocked.Read(ref _outstanding);

        /// <summary>Increments NNTP delivery outstanding and peak.</summary>
        public void DeliveryStarted()
        {
            var value = Interlocked.Increment(ref _deliveryOutstanding);
            UpdatePeak(ref _peakDeliveryOutstanding, value);
        }

        /// <summary>Decrements NNTP delivery outstanding after ARTICLE completes.</summary>
        public void DeliveryFinished() => Interlocked.Decrement(ref _deliveryOutstanding);

        /// <summary>Current in-flight NNTP ARTICLE deliveries.</summary>
        public long DeliveryOutstanding => Interlocked.Read(ref _deliveryOutstanding);

        /// <summary>Increments active pooled NNTP connections and peak.</summary>
        public void ConnectionOpened()
        {
            var active = Interlocked.Increment(ref _activeConnections);
            UpdatePeak(ref _peakConnections, active);
        }

        /// <summary>Decrements active pooled NNTP connections when a session is disposed.</summary>
        public void ConnectionClosed() => Interlocked.Decrement(ref _activeConnections);

        /// <summary>Records ReplyTo dispose drain duration for the run summary.</summary>
        public void SetDrainMilliseconds(long milliseconds) =>
            Interlocked.Exchange(ref _drainMilliseconds, milliseconds);

        /// <summary>
        /// Records ReplyTo lifecycle counters after harness drain / client dispose.
        /// Distinguishes late replies from unresolved broker-outstanding work without changing deadlines.
        /// </summary>
        public void SetReplyLifecycleCounters(
            long lateRepliesAfterTimeout,
            long unresolvedAtDispose,
            long brokerOutstandingAtDrainEnd)
        {
            Interlocked.Exchange(ref _lateRepliesAfterTimeout, lateRepliesAfterTimeout);
            Interlocked.Exchange(ref _unresolvedAtDispose, unresolvedAtDispose);
            Interlocked.Exchange(ref _brokerOutstandingAtDrainEnd, brokerOutstandingAtDrainEnd);
        }

        /// <summary>Increments aggregate and per-backbone Article Work outcome counters.</summary>
        public void RecordWorkReply(string backbone, ArticleWorkStressOutcome outcome)
        {
            var counters = _byBackbone.GetOrAdd(backbone, static _ => new BackboneCounters());
            switch (outcome)
            {
                case ArticleWorkStressOutcome.Success:
                    Interlocked.Increment(ref _workSuccess);
                    Interlocked.Increment(ref counters.WorkSuccess);
                    break;
                case ArticleWorkStressOutcome.ArticleNotFound:
                    Interlocked.Increment(ref _workNotFound);
                    Interlocked.Increment(ref counters.WorkNotFound);
                    break;
                default:
                    Interlocked.Increment(ref _workOtherFailure);
                    Interlocked.Increment(ref counters.WorkOtherFailure);
                    break;
            }
        }

        /// <summary>Appends harness stage latency samples for later percentile aggregation.</summary>
        public void RecordStageTimings(
            long scheduleToPublishMs,
            long publishCallMs,
            long publishToReplyMs,
            long? replyToNntpStartMs,
            long? nntpDeliveryMs,
            long endToEndMs,
            bool success,
            long? logicalBytes)
        {
            _scheduleToPublishMs.Add(scheduleToPublishMs);
            _publishCallMs.Add(publishCallMs);
            _publishToReplyMs.Add(publishToReplyMs);
            if (replyToNntpStartMs is { } r)
            {
                _replyToNntpStartMs.Add(r);
            }

            if (nntpDeliveryMs is { } n)
            {
                _nntpDeliveryLatenciesMs.Add(n);
            }

            if (success)
            {
                _endToEndSuccessLatenciesMs.Add(endToEndMs);
                if (logicalBytes is { } bytes)
                {
                    _articleSizeBytes.Add(bytes);
                }
            }
            else
            {
                _endToEndFailureLatenciesMs.Add(endToEndMs);
            }
        }

        /// <summary>Counts a successful listener delivery and payload byte totals.</summary>
        public void RecordArticleDelivered(
            string backbone,
            long logicalBytes,
            long wireBytes,
            long endToEndMs,
            long nntpDeliveryMs)
        {
            Interlocked.Increment(ref _articlesDelivered);
            Interlocked.Add(ref _logicalPayloadBytes, logicalBytes);
            Interlocked.Add(ref _wirePayloadBytes, wireBytes);
            var counters = _byBackbone.GetOrAdd(backbone, static _ => new BackboneCounters());
            Interlocked.Increment(ref counters.ArticlesDelivered);
            Interlocked.Add(ref counters.LogicalPayloadBytes, logicalBytes);
            Interlocked.Add(ref counters.WirePayloadBytes, wireBytes);
            _ = endToEndMs;
            _ = nntpDeliveryMs;
        }

        /// <summary>Increments failure buckets, timeout/disconnect counters, and per-backbone failures.</summary>
        public void RecordFailure(
            string backbone,
            StressFailureClass failure,
            long? endToEndMs,
            NntpResponseKind? nntpKind = null)
        {
            _failureBuckets.AddOrUpdate(failure.ToString(), 1, static (_, v) => v + 1);
            if (endToEndMs is { } ms)
            {
                _endToEndFailureLatenciesMs.Add(ms);
            }

            switch (failure)
            {
                case StressFailureClass.Timeout:
                    Interlocked.Increment(ref _timeouts);
                    break;
                case StressFailureClass.Disconnect:
                    Interlocked.Increment(ref _disconnects);
                    break;
                case StressFailureClass.IncompleteResponse:
                    Interlocked.Increment(ref _incompleteResponses);
                    break;
                case StressFailureClass.MissingOrExpired when nntpKind == NntpResponseKind.NotFound:
                    Interlocked.Increment(ref _nntpNotFound);
                    break;
                default:
                    if (nntpKind is not null and not NntpResponseKind.ArticleDelivered)
                    {
                        Interlocked.Increment(ref _nntpErrors);
                    }

                    break;
            }

            var counters = _byBackbone.GetOrAdd(backbone, static _ => new BackboneCounters());
            Interlocked.Increment(ref counters.Failures);
        }

        /// <summary>Enqueues a throughput snapshot with current counters and outstanding counts.</summary>
        public void SampleThroughput(long timestamp)
        {
            var outstanding = Interlocked.Read(ref _outstanding);
            Interlocked.Add(ref _outstandingSum, outstanding);
            Interlocked.Increment(ref _outstandingSamples);
            _throughputSamples.Enqueue(new ThroughputSample(
                timestamp,
                Interlocked.Read(ref _articlesDelivered),
                Interlocked.Read(ref _logicalPayloadBytes),
                outstanding,
                Interlocked.Read(ref _deliveryOutstanding)));
        }

        /// <summary>Enqueues one Management API queue depth observation.</summary>
        public void SampleQueueDepth(long timestamp, string queueName, long messages, long ready, long unacked, long consumers)
        {
            _queueDepthSamples.Enqueue(new QueueDepthSample(timestamp, queueName, messages, ready, unacked, consumers));
        }

        /// <summary>Materializes immutable run summary with percentiles, stages, and optional process stats.</summary>
        public StressRunSummary BuildSummary(Process? process = null)
        {
            var start = SteadyStateStartTimestamp;
            var end = SteadyStateEndTimestamp;
            if (end <= start)
            {
                end = Stopwatch.GetTimestamp();
            }

            var elapsedSeconds = Stopwatch.GetElapsedTime(start, end).TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                elapsedSeconds = 1e-9;
            }

            var logicalBytes = Interlocked.Read(ref _logicalPayloadBytes);
            var articles = Interlocked.Read(ref _articlesDelivered);
            var gbps = logicalBytes * 8.0 / elapsedSeconds / 1_000_000_000.0;
            var mibPerSec = logicalBytes / elapsedSeconds / (1024.0 * 1024.0);
            var samples = Interlocked.Read(ref _outstandingSamples);
            var meanOutstanding = samples == 0
                ? 0.0
                : (double)Interlocked.Read(ref _outstandingSum) / samples;
            var meanArticle = articles == 0 ? 0.0 : (double)logicalBytes / articles;

            var byBackbone = _byBackbone.ToDictionary(
                static pair => pair.Key,
                pair => pair.Value.Snapshot(),
                StringComparer.OrdinalIgnoreCase);

            var stages = new[]
            {
                StageLatencyStats.From(
                    "schedule_to_publish",
                    "Harness: work enqueued to BasicPublish completed. Not upstream fetch.",
                    _scheduleToPublishMs),
                StageLatencyStats.From(
                    "publish_call",
                    "Harness: duration of BasicPublishAsync only.",
                    _publishCallMs),
                StageLatencyStats.From(
                    "publish_to_reply",
                    "Aggregate: publish-complete to ReplyTo receipt (BackFiller consume+upstream+retain+reply). Upstream fetch is not separately observable.",
                    _publishToReplyMs),
                StageLatencyStats.From(
                    "reply_to_nntp_start",
                    "Harness: Success reply received to ARTICLE send start (delivery queue + connection rent).",
                    _replyToNntpStartMs),
                StageLatencyStats.From(
                    "nntp_delivery",
                    "Harness: ARTICLE command through complete multiline consume on listener.",
                    _nntpDeliveryLatenciesMs),
                StageLatencyStats.From(
                    "schedule_to_complete_success",
                    "Harness: schedule timestamp to successful ARTICLE complete.",
                    _endToEndSuccessLatenciesMs),
            };

            return new StressRunSummary(
                RunName,
                Backbone,
                ConfiguredConcurrency,
                ConfiguredMaxInFlight,
                elapsedSeconds,
                Interlocked.Read(ref _requestsStarted),
                Interlocked.Read(ref _workSuccess),
                Interlocked.Read(ref _workNotFound),
                Interlocked.Read(ref _workOtherFailure),
                articles,
                logicalBytes,
                Interlocked.Read(ref _wirePayloadBytes),
                meanArticle,
                articles / elapsedSeconds,
                gbps,
                mibPerSec,
                Interlocked.Read(ref _nntpNotFound),
                Interlocked.Read(ref _nntpErrors),
                Interlocked.Read(ref _timeouts),
                Interlocked.Read(ref _disconnects),
                Interlocked.Read(ref _incompleteResponses),
                Interlocked.Read(ref _outstanding),
                Interlocked.Read(ref _peakOutstanding),
                meanOutstanding,
                Interlocked.Read(ref _peakDeliveryOutstanding),
                Interlocked.Read(ref _peakConnections),
                Interlocked.Read(ref _drainMilliseconds),
                Interlocked.Read(ref _lateRepliesAfterTimeout),
                Interlocked.Read(ref _unresolvedAtDispose),
                Interlocked.Read(ref _brokerOutstandingAtDrainEnd),
                Percentiles.From(_endToEndSuccessLatenciesMs),
                Percentiles.From(_endToEndFailureLatenciesMs),
                Percentiles.From(_publishToReplyMs),
                Percentiles.From(_nntpDeliveryLatenciesMs),
                stages,
                StageLatencyStats.From("article_size_bytes", "Logical payload bytes per delivered article.", _articleSizeBytes),
                _failureBuckets.ToDictionary(static p => p.Key, static p => p.Value, StringComparer.Ordinal),
                byBackbone,
                _throughputSamples.ToArray(),
                _queueDepthSamples.ToArray(),
                process?.WorkingSet64,
                process?.TotalProcessorTime.TotalSeconds);
        }

        /// <summary>Writes <see cref="BuildSummary"/> JSON to <paramref name="path"/> using source-generated context.</summary>
        public void WriteJson(string path, Process? process = null)
        {
            var summary = BuildSummary(process);
            var json = JsonSerializer.Serialize(summary, StressJsonContext.Default.StressRunSummary);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

        /// <summary>Lock-free max update for peak counters.</summary>
        private static void UpdatePeak(ref long peak, long candidate)
        {
            while (true)
            {
                var observed = Interlocked.Read(ref peak);
                if (candidate <= observed)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref peak, candidate, observed) == observed)
                {
                    return;
                }
            }
        }

        /// <summary>Per-backbone counter shard updated concurrently during a run.</summary>
        private sealed class BackboneCounters
        {
            /// <summary>Successful work replies for this backbone.</summary>
            public long WorkSuccess;

            /// <summary>Article-not-found work replies.</summary>
            public long WorkNotFound;

            /// <summary>Other work reply failures.</summary>
            public long WorkOtherFailure;

            /// <summary>NNTP deliveries completed.</summary>
            public long ArticlesDelivered;

            /// <summary>Logical bytes delivered on this backbone.</summary>
            public long LogicalPayloadBytes;

            /// <summary>Wire bytes delivered on this backbone.</summary>
            public long WirePayloadBytes;

            /// <summary>Delivery or work failures attributed to this backbone.</summary>
            public long Failures;

            /// <summary>Volatile read snapshot for summary aggregation.</summary>
            public BackboneSummary Snapshot() =>
                new(
                    Interlocked.Read(ref WorkSuccess),
                    Interlocked.Read(ref WorkNotFound),
                    Interlocked.Read(ref WorkOtherFailure),
                    Interlocked.Read(ref ArticlesDelivered),
                    Interlocked.Read(ref LogicalPayloadBytes),
                    Interlocked.Read(ref WirePayloadBytes),
                    Interlocked.Read(ref Failures));
        }
    }

    /// <summary>Point-in-time throughput counters sampled during a run.</summary>
    public readonly record struct ThroughputSample(
        long Timestamp,
        long ArticlesDelivered,
        long LogicalPayloadBytes,
        long Outstanding,
        long DeliveryOutstanding);

    /// <summary>One RabbitMQ queue depth observation from the Management API.</summary>
    public readonly record struct QueueDepthSample(
        long Timestamp,
        string QueueName,
        long Messages,
        long Ready,
        long Unacked,
        long Consumers);

    /// <summary>Order statistics for a latency or size sample bag.</summary>
    public sealed record Percentiles(long Count, double Mean, double P50, double P90, double P95, double P99, double Max)
    {
        /// <summary>Computes percentiles from unsorted samples; empty bag yields zeros.</summary>
        public static Percentiles From(ConcurrentBag<long> bag)
        {
            if (bag.IsEmpty)
            {
                return new Percentiles(0, 0, 0, 0, 0, 0, 0);
            }

            var values = bag.ToArray();
            Array.Sort(values);
            double sum = 0;
            for (var i = 0; i < values.Length; i++)
            {
                sum += values[i];
            }

            return new Percentiles(
                values.Length,
                sum / values.Length,
                At(values, 0.50),
                At(values, 0.90),
                At(values, 0.95),
                At(values, 0.99),
                values[^1]);
        }

        /// <summary>Quantile on sorted ascending values.</summary>
        private static double At(long[] sorted, double quantile)
        {
            var index = (int)Math.Clamp(Math.Ceiling(quantile * sorted.Length) - 1, 0, sorted.Length - 1);
            return sorted[index];
        }
    }

    /// <summary>Aggregate counters for one provider backbone.</summary>
    public sealed record BackboneSummary(
        long WorkSuccess,
        long WorkNotFound,
        long WorkOtherFailure,
        long ArticlesDelivered,
        long LogicalPayloadBytes,
        long WirePayloadBytes,
        long Failures);

    /// <summary>Immutable end-of-run report for one stress execution.</summary>
    public sealed record StressRunSummary(
        string RunName,
        string? Backbone,
        int ConfiguredConcurrency,
        int ConfiguredMaxInFlight,
        double ElapsedSeconds,
        long RequestsStarted,
        long WorkSuccess,
        long WorkNotFound,
        long WorkOtherFailure,
        long ArticlesDelivered,
        long LogicalPayloadBytes,
        long WirePayloadBytes,
        double MeanArticleLogicalBytes,
        double ArticlesPerSecond,
        double PayloadGbps,
        double PayloadMiBPerSecond,
        long NntpNotFound,
        long NntpErrors,
        long Timeouts,
        long Disconnects,
        long IncompleteResponses,
        long OutstandingAtEnd,
        long PeakOutstanding,
        double MeanOutstanding,
        long PeakDeliveryOutstanding,
        long PeakConnections,
        long DrainMilliseconds,
        long LateRepliesAfterTimeout,
        long UnresolvedAtDispose,
        long BrokerOutstandingAtDrainEnd,
        Percentiles EndToEndSuccessLatencyMs,
        Percentiles EndToEndFailureLatencyMs,
        Percentiles WorkReplyLatencyMs,
        Percentiles NntpDeliveryLatencyMs,
        IReadOnlyList<StageLatencyStats> Stages,
        StageLatencyStats ArticleSizeBytes,
        IReadOnlyDictionary<string, long> FailureBuckets,
        IReadOnlyDictionary<string, BackboneSummary> ByBackbone,
        IReadOnlyList<ThroughputSample> ThroughputSamples,
        IReadOnlyList<QueueDepthSample> QueueDepthSamples,
        long? WorkingSetBytes,
        double? ProcessCpuSeconds)
    {
        /// <summary>Single-line console/table row for sweep comparisons.</summary>
        public string FormatTableRow()
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{RunName,-40} Lcfg={ConfiguredConcurrency,3} Lpeak={PeakOutstanding,3} Lmean={MeanOutstanding,5:F1} art/s={ArticlesPerSecond,7:F1} Gbps={PayloadGbps,7:F3} replyMean={WorkReplyLatencyMs.Mean,7:F0} e2eMean={EndToEndSuccessLatencyMs.Mean,7:F0} ok={ArticlesDelivered} to={Timeouts} late={LateRepliesAfterTimeout} unresolved={UnresolvedAtDispose} drainMs={DrainMilliseconds}");
        }
    }

    /// <summary>Source-generated JSON serializer context for stress report types.</summary>
    [System.Text.Json.Serialization.JsonSerializable(typeof(StressRunSummary))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(StageLatencyStats))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(Percentiles))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(BackboneSummary))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(ThroughputSample))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(QueueDepthSample))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(NewsLogDiscoveryStats))]
    internal partial class StressJsonContext : System.Text.Json.Serialization.JsonSerializerContext
    {
    }
}
