using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>Thread-safe counters and latency samples for one stress run.</summary>
    public sealed class StressMetrics
    {
        private readonly ConcurrentBag<long> _endToEndSuccessLatenciesMs = [];
        private readonly ConcurrentBag<long> _endToEndFailureLatenciesMs = [];
        private readonly ConcurrentBag<long> _scheduleToPublishMs = [];
        private readonly ConcurrentBag<long> _publishCallMs = [];
        private readonly ConcurrentBag<long> _publishToReplyMs = [];
        private readonly ConcurrentBag<long> _replyToNntpStartMs = [];
        private readonly ConcurrentBag<long> _nntpDeliveryLatenciesMs = [];
        private readonly ConcurrentBag<long> _articleSizeBytes = [];
        private readonly ConcurrentDictionary<string, long> _failureBuckets = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, BackboneCounters> _byBackbone = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<ThroughputSample> _throughputSamples = new();
        private readonly ConcurrentQueue<QueueDepthSample> _queueDepthSamples = new();
        private long _requestsStarted;
        private long _workSuccess;
        private long _workNotFound;
        private long _workOtherFailure;
        private long _articlesDelivered;
        private long _logicalPayloadBytes;
        private long _wirePayloadBytes;
        private long _nntpNotFound;
        private long _nntpErrors;
        private long _timeouts;
        private long _disconnects;
        private long _outstanding;
        private long _peakOutstanding;
        private long _deliveryOutstanding;
        private long _peakDeliveryOutstanding;
        private long _outstandingSum;
        private long _outstandingSamples;
        private long _activeConnections;
        private long _peakConnections;
        private long _incompleteResponses;
        private long _drainMilliseconds;
        private long _lateRepliesAfterTimeout;
        private long _unresolvedAtDispose;
        private long _brokerOutstandingAtDrainEnd;

        public required string RunName { get; init; }
        public string? Backbone { get; init; }
        public int ConfiguredConcurrency { get; init; }
        public int ConfiguredMaxInFlight { get; init; }
        public long SteadyStateStartTimestamp { get; set; }
        public long SteadyStateEndTimestamp { get; set; }

        public void RequestStarted()
        {
            Interlocked.Increment(ref _requestsStarted);
            var outstanding = Interlocked.Increment(ref _outstanding);
            UpdatePeak(ref _peakOutstanding, outstanding);
        }

        public void RequestFinished() => Interlocked.Decrement(ref _outstanding);

        public long Outstanding => Interlocked.Read(ref _outstanding);

        public void DeliveryStarted()
        {
            var value = Interlocked.Increment(ref _deliveryOutstanding);
            UpdatePeak(ref _peakDeliveryOutstanding, value);
        }

        public void DeliveryFinished() => Interlocked.Decrement(ref _deliveryOutstanding);

        public long DeliveryOutstanding => Interlocked.Read(ref _deliveryOutstanding);

        public void ConnectionOpened()
        {
            var active = Interlocked.Increment(ref _activeConnections);
            UpdatePeak(ref _peakConnections, active);
        }

        public void ConnectionClosed() => Interlocked.Decrement(ref _activeConnections);

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

        public void SampleQueueDepth(long timestamp, string queueName, long messages, long ready, long unacked, long consumers)
        {
            _queueDepthSamples.Enqueue(new QueueDepthSample(timestamp, queueName, messages, ready, unacked, consumers));
        }

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

        public void WriteJson(string path, Process? process = null)
        {
            var summary = BuildSummary(process);
            var json = JsonSerializer.Serialize(summary, StressJsonContext.Default.StressRunSummary);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

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

        private sealed class BackboneCounters
        {
            public long WorkSuccess;
            public long WorkNotFound;
            public long WorkOtherFailure;
            public long ArticlesDelivered;
            public long LogicalPayloadBytes;
            public long WirePayloadBytes;
            public long Failures;

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

    public readonly record struct ThroughputSample(
        long Timestamp,
        long ArticlesDelivered,
        long LogicalPayloadBytes,
        long Outstanding,
        long DeliveryOutstanding);

    public readonly record struct QueueDepthSample(
        long Timestamp,
        string QueueName,
        long Messages,
        long Ready,
        long Unacked,
        long Consumers);

    public sealed record Percentiles(long Count, double Mean, double P50, double P90, double P95, double P99, double Max)
    {
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

        private static double At(long[] sorted, double quantile)
        {
            var index = (int)Math.Clamp(Math.Ceiling(quantile * sorted.Length) - 1, 0, sorted.Length - 1);
            return sorted[index];
        }
    }

    public sealed record BackboneSummary(
        long WorkSuccess,
        long WorkNotFound,
        long WorkOtherFailure,
        long ArticlesDelivered,
        long LogicalPayloadBytes,
        long WirePayloadBytes,
        long Failures);

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
        public string FormatTableRow()
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{RunName,-40} Lcfg={ConfiguredConcurrency,3} Lpeak={PeakOutstanding,3} Lmean={MeanOutstanding,5:F1} art/s={ArticlesPerSecond,7:F1} Gbps={PayloadGbps,7:F3} replyMean={WorkReplyLatencyMs.Mean,7:F0} e2eMean={EndToEndSuccessLatencyMs.Mean,7:F0} ok={ArticlesDelivered} to={Timeouts} late={LateRepliesAfterTimeout} unresolved={UnresolvedAtDispose} drainMs={DrainMilliseconds}");
        }
    }

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
