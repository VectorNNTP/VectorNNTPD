using VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// <see cref="QueueSampleHistory"/> retention, success/failure sample semantics, and
    /// <see cref="QueueSampleHistory.TryComputeCounterRate"/> edge cases (no invented zeros on failed samples).
    /// </summary>
    public sealed class QueueSampleHistoryTests
    {
        /// <summary>
        /// Counter rate is the delta of the selected total divided by elapsed seconds between two valid samples.
        /// </summary>
        [Fact]
        public void Counter_rate_increases_normally()
        {
            var older = Valid(0, ackTotal: 10);
            var newer = Valid(10, ackTotal: 20);
            var rate = QueueSampleHistory.TryComputeCounterRate(older, newer, static s => s.AckTotal);
            Assert.Equal(1.0, rate);
        }

        /// <summary>
        /// When the counter does not increase, the computed rate is zero rather than null.
        /// </summary>
        [Fact]
        public void Counter_rate_no_progress_is_zero()
        {
            var older = Valid(0, ackTotal: 10);
            var newer = Valid(10, ackTotal: 10);
            var rate = QueueSampleHistory.TryComputeCounterRate(older, newer, static s => s.AckTotal);
            Assert.Equal(0.0, rate);
        }

        /// <summary>
        /// A decreasing counter total (reset) yields null so callers do not treat the drop as negative progress.
        /// </summary>
        [Fact]
        public void Counter_reset_returns_null()
        {
            var older = Valid(0, ackTotal: 50);
            var newer = Valid(10, ackTotal: 1);
            Assert.Null(QueueSampleHistory.TryComputeCounterRate(older, newer, static s => s.AckTotal));
        }

        /// <summary>
        /// Either sample marked invalid or missing snapshot prevents rate computation.
        /// </summary>
        [Fact]
        public void Missing_or_invalid_samples_return_null()
        {
            var valid = Valid(0, ackTotal: 1);
            var invalid = new QueueSampleEntry(
                DateTimeOffset.UtcNow,
                IsValid: false,
                Snapshot: null,
                ManagementPollKind.TransientFailure,
                "fail");
            Assert.Null(QueueSampleHistory.TryComputeCounterRate(invalid, valid, static s => s.AckTotal));
            Assert.Null(QueueSampleHistory.TryComputeCounterRate(valid, invalid, static s => s.AckTotal));
        }

        /// <summary>
        /// Non-positive elapsed time between sample timestamps returns null.
        /// </summary>
        [Fact]
        public void Non_positive_elapsed_returns_null()
        {
            var a = Valid(5, ackTotal: 1);
            var b = Valid(5, ackTotal: 2);
            Assert.Null(QueueSampleHistory.TryComputeCounterRate(a, b, static s => s.AckTotal));
        }

        /// <summary>
        /// History drops oldest entries when capacity is exceeded and <see cref="QueueSampleHistory.Snapshot"/> reflects the newest samples.
        /// </summary>
        [Fact]
        public void History_is_bounded_and_retains_newest()
        {
            var history = new QueueSampleHistory(3);
            for (var i = 0; i < 5; i++)
            {
                history.AddSuccess(Snap(ready: i, consumers: 1, unacked: 0));
            }

            Assert.Equal(3, history.Count);
            var snap = history.Snapshot();
            Assert.Equal(2, snap[0].Snapshot!.MessagesReady);
            Assert.Equal(4, snap[2].Snapshot!.MessagesReady);
        }

        /// <summary>
        /// Failed polls record invalid entries with null snapshot rather than synthesizing zero gauges.
        /// </summary>
        [Fact]
        public void Failure_does_not_invent_zeros()
        {
            var history = new QueueSampleHistory(4);
            history.AddFailure(
                DateTimeOffset.UtcNow,
                ManagementPollKind.TransientFailure,
                "timeout");
            var entry = Assert.Single(history.Snapshot());
            Assert.False(entry.IsValid);
            Assert.Null(entry.Snapshot);
        }

        /// <summary>Builds a valid history entry with fixed base time plus offset seconds.</summary>
        /// <param name="seconds">Seconds added to the canonical sample timestamp.</param>
        /// <param name="ackTotal">Ack counter total on the embedded snapshot.</param>
        /// <returns>A valid <see cref="QueueSampleEntry"/> for rate tests.</returns>
        private static QueueSampleEntry Valid(int seconds, long ackTotal)
        {
            var at = DateTimeOffset.Parse("2026-10-25T20:00:00Z").AddSeconds(seconds);
            return new QueueSampleEntry(
                at,
                IsValid: true,
                new ManagementQueueSnapshot
                {
                    QueueName = "q",
                    VirtualHost = "/",
                    SampledAtUtc = at,
                    MessagesReady = 0,
                    MessagesUnacknowledged = 1,
                    Consumers = 1,
                    AckTotal = ackTotal,
                },
                null,
                null);
        }

        /// <summary>Minimal successful management snapshot for history capacity tests.</summary>
        /// <param name="ready"><see cref="ManagementQueueSnapshot.MessagesReady"/>.</param>
        /// <param name="consumers"><see cref="ManagementQueueSnapshot.Consumers"/>.</param>
        /// <param name="unacked"><see cref="ManagementQueueSnapshot.MessagesUnacknowledged"/>.</param>
        /// <param name="ackTotal">Optional ack total; omitted when null.</param>
        /// <returns>Snapshot stamped at the canonical test UTC instant.</returns>
        private static ManagementQueueSnapshot Snap(long ready, long consumers, long unacked, long? ackTotal = null) =>
            new()
            {
                QueueName = "q",
                VirtualHost = "/",
                SampledAtUtc = DateTimeOffset.Parse("2026-10-25T20:00:00Z"),
                MessagesReady = ready,
                MessagesUnacknowledged = unacked,
                Consumers = consumers,
                AckTotal = ackTotal,
            };
    }
}
