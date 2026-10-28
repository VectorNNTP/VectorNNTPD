using VectorNNTPD.BackFillerService.Diagnostics;

namespace VectorNNTPD.BackFillerService.Tests.Diagnostics
{
    public sealed class StageLatencyHistogramTests
    {
        [Fact]
        public void Empty_histogram_reports_zeros()
        {
            var h = new StageLatencyHistogram();
            Assert.Equal(0, h.Count);
            Assert.Equal(0, h.MeanMilliseconds);
            Assert.Equal(0, h.ApproximatePercentileMilliseconds(0.50));
            Assert.Equal(0, h.ApproximatePercentileMilliseconds(0.99));
            Assert.Equal(0, h.Failures);
            Assert.Equal(0, h.Timeouts);
        }

        [Fact]
        public void Records_count_sum_mean_and_max()
        {
            var h = new StageLatencyHistogram();
            h.RecordMicroseconds(1_000);
            h.RecordMicroseconds(3_000);
            h.RecordMicroseconds(2_000);
            Assert.Equal(3, h.Count);
            Assert.Equal(6_000, h.SumMicroseconds);
            Assert.Equal(3_000, h.MaxMicroseconds);
            Assert.Equal(2.0, h.MeanMilliseconds, precision: 3);
        }

        [Fact]
        public void Percentiles_use_bucket_upper_edges()
        {
            var h = new StageLatencyHistogram();
            for (var i = 0; i < 100; i++)
            {
                h.RecordMicroseconds(1_000); // 1 ms → bucket covering up to 1024 µs
            }

            Assert.True(h.ApproximatePercentileMilliseconds(0.50) >= 1.0);
            Assert.True(h.ApproximatePercentileMilliseconds(0.99) >= 1.0);
            Assert.True(h.ApproximatePercentileMilliseconds(0.99) <= 2.0);
        }

        [Fact]
        public void Failures_and_timeouts_are_independent_of_duration_count()
        {
            var h = new StageLatencyHistogram();
            h.RecordFailure();
            h.RecordTimeout();
            h.RecordTimeout();
            Assert.Equal(0, h.Count);
            Assert.Equal(1, h.Failures);
            Assert.Equal(2, h.Timeouts);
        }

        [Fact]
        public void Concurrent_updates_aggregate()
        {
            var h = new StageLatencyHistogram();
            Parallel.For(0, 1000, _ => h.RecordMicroseconds(500));
            Assert.Equal(1000, h.Count);
            Assert.Equal(500_000, h.SumMicroseconds);
        }

        [Fact]
        public void Bucket_index_is_bounded()
        {
            Assert.Equal(0, StageLatencyHistogram.BucketIndex(0));
            Assert.Equal(0, StageLatencyHistogram.BucketIndex(1));
            Assert.InRange(StageLatencyHistogram.BucketIndex(1L << 40), 0, StageLatencyHistogram.BucketCount - 1);
        }

        [Fact]
        public void Disabled_profiler_records_nothing()
        {
            var profiler = new ArticleWorkStageProfiler(enabled: false);
            ArticleWorkStageProfiler.Install(profiler);
            try
            {
                Assert.False(ArticleWorkStageProfiler.IsEnabled);
                profiler.BeginActive();
                profiler.Record(ArticleWorkStageId.HandlerTotal, 1);
                profiler.EndActive();
                Assert.Equal(0, profiler[ArticleWorkStageId.HandlerTotal].Count);
                Assert.Equal(0, profiler.Completed);
            }
            finally
            {
                ArticleWorkStageProfiler.Install(null);
            }
        }

        [Fact]
        public void Enabled_profiler_tracks_active_and_stages()
        {
            var profiler = new ArticleWorkStageProfiler(enabled: true);
            ArticleWorkStageProfiler.Install(profiler);
            try
            {
                Assert.True(ArticleWorkStageProfiler.IsEnabled);
                profiler.BeginActive();
                Assert.Equal(1, profiler.Active);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                profiler.Record(ArticleWorkStageId.Retain, started);
                profiler.EndActive(failed: true);
                Assert.Equal(0, profiler.Active);
                Assert.Equal(1, profiler.PeakActive);
                Assert.Equal(1, profiler.Completed);
                Assert.Equal(1, profiler.Failed);
                Assert.Equal(1, profiler[ArticleWorkStageId.Retain].Count);
                var snap = profiler.CaptureSnapshot();
                Assert.Contains(snap.Stages, static s => s.Stage == nameof(ArticleWorkStageId.Retain) && s.Count == 1);
            }
            finally
            {
                ArticleWorkStageProfiler.Install(null);
            }
        }

        [Fact]
        public void Publisher_sub_stages_aggregate_and_appear_in_snapshot()
        {
            var profiler = new ArticleWorkStageProfiler(enabled: true);
            ArticleWorkStageProfiler.Install(profiler);
            try
            {
                profiler[ArticleWorkStageId.ReplySerialize].RecordMicroseconds(100);
                profiler[ArticleWorkStageId.ReplyPublishGateWait].RecordMicroseconds(20_000);
                profiler[ArticleWorkStageId.ReplyPublishConfirm].RecordMicroseconds(800);
                profiler[ArticleWorkStageId.ReplyPublish].RecordMicroseconds(21_000);

                Assert.Equal(1, profiler[ArticleWorkStageId.ReplyPublishGateWait].Count);
                Assert.Equal(20.0, profiler[ArticleWorkStageId.ReplyPublishGateWait].MeanMilliseconds, precision: 3);
                Assert.Equal(0.8, profiler[ArticleWorkStageId.ReplyPublishConfirm].MeanMilliseconds, precision: 3);

                var snap = profiler.CaptureSnapshot();
                Assert.Contains(
                    snap.Stages,
                    static s => s.Stage == nameof(ArticleWorkStageId.ReplyPublishGateWait)
                                && s.Count == 1
                                && s.Nesting.Contains("_publishGate", StringComparison.Ordinal)
                                && s.Nesting.Contains("MaxConcurrentConfirmedPublications", StringComparison.Ordinal));
                Assert.Contains(
                    snap.Stages,
                    static s => s.Stage == nameof(ArticleWorkStageId.ReplyPublishConfirm)
                                && s.Nesting.Contains("BasicPublishAsync", StringComparison.Ordinal));
            }
            finally
            {
                ArticleWorkStageProfiler.Install(null);
            }
        }

        [Fact]
        public void Concurrent_publisher_stage_records_do_not_lose_counts()
        {
            var profiler = new ArticleWorkStageProfiler(enabled: true);
            ArticleWorkStageProfiler.Install(profiler);
            try
            {
                Parallel.For(
                    0,
                    500,
                    _ =>
                    {
                        profiler.Record(ArticleWorkStageId.ReplyPublishGateWait, System.Diagnostics.Stopwatch.GetTimestamp());
                        profiler.Record(ArticleWorkStageId.ReplyPublishConfirm, System.Diagnostics.Stopwatch.GetTimestamp());
                    });
                Assert.Equal(500, profiler[ArticleWorkStageId.ReplyPublishGateWait].Count);
                Assert.Equal(500, profiler[ArticleWorkStageId.ReplyPublishConfirm].Count);
            }
            finally
            {
                ArticleWorkStageProfiler.Install(null);
            }
        }
    }
}
