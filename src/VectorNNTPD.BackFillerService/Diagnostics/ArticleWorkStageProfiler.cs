using System.Diagnostics;
using System.Text.Json;

namespace VectorNNTPD.BackFillerService.Diagnostics
{
    /// <summary>
    /// Opt-in, bounded Article Work stage latency profiler for lab diagnosis.
    /// When not installed or disabled, <see cref="IsEnabled"/> is false and hooks are no-ops.
    /// </summary>
    /// <remarks>
    /// Installed by <see cref="ArticleWorkStageProfilingHostedService"/> only when lab options enable it.
    /// Production hosts leave this uninstalled. Nested stages overlap; do not sum exclusive wall time.
    /// </remarks>
    internal sealed class ArticleWorkStageProfiler
    {
        private static ArticleWorkStageProfiler? _installed;

        private readonly StageLatencyHistogram[] _stages;
        private readonly bool _enabled;
        private int _active;
        private int _peakActive;
        private long _completed;
        private long _failed;

        /// <summary>Creates a profiler. <paramref name="enabled"/> gates recording.</summary>
        /// <param name="enabled">When false, <see cref="IsEnabled"/> is false and record methods return immediately.</param>
        internal ArticleWorkStageProfiler(bool enabled)
        {
            _enabled = enabled;
            var count = Enum.GetValues<ArticleWorkStageId>().Length;
            _stages = new StageLatencyHistogram[count];
            for (var i = 0; i < count; i++)
            {
                _stages[i] = new StageLatencyHistogram();
            }
        }

        /// <summary>Currently installed profiler for ambient hot-path hooks; null when disabled.</summary>
        internal static ArticleWorkStageProfiler? Installed => Volatile.Read(ref _installed);

        /// <summary>True when an enabled profiler is installed.</summary>
        internal static bool IsEnabled
        {
            get
            {
                var current = Volatile.Read(ref _installed);
                return current is { _enabled: true };
            }
        }

        /// <summary>Publishes <paramref name="profiler"/> for ambient hooks (or clears when null).</summary>
        /// <param name="profiler">Profiler to install, or null to uninstall.</param>
        internal static void Install(ArticleWorkStageProfiler? profiler) =>
            Volatile.Write(ref _installed, profiler);

        /// <summary>Instance enabled flag (independent of installation).</summary>
        internal bool Enabled => _enabled;

        /// <summary>In-flight handler/pipeline operations currently between begin and end.</summary>
        internal int Active => Volatile.Read(ref _active);

        /// <summary>Peak concurrent active operations since install/reset.</summary>
        internal int PeakActive => Volatile.Read(ref _peakActive);

        /// <summary>Completed pipeline operations (success or failure) that called <see cref="EndActive"/>.</summary>
        internal long Completed => Volatile.Read(ref _completed);

        /// <summary>Pipeline operations marked failed via <see cref="EndActive"/>.</summary>
        internal long Failed => Volatile.Read(ref _failed);

        /// <summary>Histogram for <paramref name="stage"/>.</summary>
        internal StageLatencyHistogram this[ArticleWorkStageId stage] => _stages[(int)stage];

        /// <summary>Marks the start of one concurrent active unit of work.</summary>
        internal void BeginActive()
        {
            if (!_enabled)
            {
                return;
            }

            var n = Interlocked.Increment(ref _active);
            UpdatePeak(ref _peakActive, n);
        }

        /// <summary>Marks the end of one concurrent active unit of work.</summary>
        /// <param name="failed">When true, increments <see cref="Failed"/>.</param>
        internal void EndActive(bool failed = false)
        {
            if (!_enabled)
            {
                return;
            }

            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _completed);
            if (failed)
            {
                Interlocked.Increment(ref _failed);
            }
        }

        /// <summary>Records elapsed time for <paramref name="stage"/> when enabled.</summary>
        /// <param name="stage">Stage identity.</param>
        /// <param name="startedTimestamp"><see cref="Stopwatch.GetTimestamp"/> at stage start.</param>
        internal void Record(ArticleWorkStageId stage, long startedTimestamp)
        {
            if (!_enabled || startedTimestamp == 0)
            {
                return;
            }

            _stages[(int)stage].Record(startedTimestamp);
        }

        /// <summary>Records a failure on <paramref name="stage"/> without a duration.</summary>
        internal void RecordFailure(ArticleWorkStageId stage)
        {
            if (!_enabled)
            {
                return;
            }

            _stages[(int)stage].RecordFailure();
        }

        /// <summary>Records a timeout on <paramref name="stage"/> without a duration.</summary>
        internal void RecordTimeout(ArticleWorkStageId stage)
        {
            if (!_enabled)
            {
                return;
            }

            _stages[(int)stage].RecordTimeout();
        }

        /// <summary>Builds a JSON-serializable snapshot for lab evidence export.</summary>
        internal ArticleWorkStageProfilerSnapshot CaptureSnapshot()
        {
            var stages = new List<ArticleWorkStageSnapshot>(_stages.Length);
            foreach (ArticleWorkStageId id in Enum.GetValues<ArticleWorkStageId>())
            {
                var h = _stages[(int)id];
                stages.Add(
                    new ArticleWorkStageSnapshot(
                        id.ToString(),
                        NestedNote(id),
                        h.Count,
                        h.MeanMilliseconds,
                        h.ApproximatePercentileMilliseconds(0.50),
                        h.ApproximatePercentileMilliseconds(0.95),
                        h.ApproximatePercentileMilliseconds(0.99),
                        h.MaxMicroseconds / 1000.0,
                        h.SumMicroseconds / 1000.0,
                        h.Failures,
                        h.Timeouts));
            }

            return new ArticleWorkStageProfilerSnapshot(
                DateTimeOffset.UtcNow,
                _enabled,
                Active,
                PeakActive,
                Completed,
                Failed,
                stages);
        }

        /// <summary>Serializes <see cref="CaptureSnapshot"/> as indented JSON (AOT-safe source generation).</summary>
        internal string ToJson() =>
            JsonSerializer.Serialize(
                CaptureSnapshot(),
                ArticleWorkStageProfilerJsonContext.Default.ArticleWorkStageProfilerSnapshot);

        private static string NestedNote(ArticleWorkStageId id) =>
            id switch
            {
                ArticleWorkStageId.PipelineTotal => "Inclusive of HandlerTotal and ReplyPublish; settlement excluded.",
                ArticleWorkStageId.HandlerTotal => "Nested in PipelineTotal. Inclusive of NntpDownload, ParseCanonical (via payload), Retain.",
                ArticleWorkStageId.SessionAcquire => "Nested in NntpDownload/HandlerTotal. Exclusive of ARTICLE bytes.",
                ArticleWorkStageId.SessionBusyWait => "Nested in NntpDownload.",
                ArticleWorkStageId.ArticleCommandToStatus => "Nested in NntpDownload. Exclusive of payload.",
                ArticleWorkStageId.ArticlePayloadReceive => "Nested in NntpDownload. Inclusive of ParseCanonical.",
                ArticleWorkStageId.ParseCanonical => "Nested in ArticlePayloadReceive — do not add to parent.",
                ArticleWorkStageId.NntpDownload => "Nested in HandlerTotal. Inclusive of busy wait, command, payload, parse. Exclusive of SessionAcquire.",
                ArticleWorkStageId.Retain => "Nested in HandlerTotal. Exclusive of NNTP.",
                ArticleWorkStageId.ReplyPublish => "Nested in PipelineTotal. Inclusive of ReplySerialize, ReplyPublishGateWait, ReplyPublishConfirm.",
                ArticleWorkStageId.ReplySerialize => "Nested in ReplyPublish. Before publish gate. Exclusive of gate wait/confirm.",
                ArticleWorkStageId.ReplyPublishGateWait => "Nested in ReplyPublish. Wait for _publishGate permit (capacity MaxConcurrentConfirmedPublications). Exclusive of serialize/confirm.",
                ArticleWorkStageId.ReplyPublishConfirm => "Nested in ReplyPublish. Gate-held: channel ensure + BasicPublishAsync with confirm tracking (opaque confirm). Exclusive of gate wait. Concurrent up to gate capacity.",
                _ => string.Empty,
            };

        private static void UpdatePeak(ref int location, int candidate)
        {
            var observed = Volatile.Read(ref location);
            while (candidate > observed)
            {
                var prev = Interlocked.CompareExchange(ref location, candidate, observed);
                if (prev == observed)
                {
                    return;
                }

                observed = prev;
            }
        }
    }

    /// <summary>Point-in-time profiler export for lab evidence.</summary>
    /// <param name="CapturedUtc">Snapshot time.</param>
    /// <param name="Enabled">Whether recording was enabled.</param>
    /// <param name="Active">Current in-flight active count.</param>
    /// <param name="PeakActive">Peak in-flight active count.</param>
    /// <param name="Completed">Completed active units.</param>
    /// <param name="Failed">Failed active units.</param>
    /// <param name="Stages">Per-stage histograms.</param>
    internal sealed record ArticleWorkStageProfilerSnapshot(
        DateTimeOffset CapturedUtc,
        bool Enabled,
        int Active,
        int PeakActive,
        long Completed,
        long Failed,
        IReadOnlyList<ArticleWorkStageSnapshot> Stages);

    /// <summary>One stage histogram summary.</summary>
    /// <param name="Stage">Stage name.</param>
    /// <param name="Nesting">Inclusive/exclusive nesting note.</param>
    /// <param name="Count">Observation count.</param>
    /// <param name="MeanMs">Arithmetic mean milliseconds.</param>
    /// <param name="P50Ms">Approximate p50 milliseconds.</param>
    /// <param name="P95Ms">Approximate p95 milliseconds.</param>
    /// <param name="P99Ms">Approximate p99 milliseconds.</param>
    /// <param name="MaxMs">Maximum milliseconds.</param>
    /// <param name="TotalMs">Sum of durations in milliseconds (parallelism may exceed wall clock).</param>
    /// <param name="Failures">Attributed failures.</param>
    /// <param name="Timeouts">Attributed timeouts.</param>
    internal sealed record ArticleWorkStageSnapshot(
        string Stage,
        string Nesting,
        long Count,
        double MeanMs,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double MaxMs,
        double TotalMs,
        long Failures,
        long Timeouts);
}
