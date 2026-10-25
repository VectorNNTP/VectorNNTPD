namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Regression: flood outstanding work must not abort using RequestsStarted as the rate denominator.
    /// </summary>
    public sealed class StressAbortDecisionTests
    {
        [Fact]
        public void ShouldAbort_false_during_flood_ramp_when_few_rpcs_completed()
        {
            // Prior DualSoak defect: Giganews cancelled after ~259 starts / 9 delivered
            // because failureRate used RequestsStarted (≈0.97) despite almost no completed replies.
            Assert.False(
                StressAbortDecision.ShouldAbort(
                    requestsStarted: 259,
                    workSuccess: 9,
                    workNotFound: 0,
                    workOtherFailure: 0,
                    timeouts: 0,
                    articlesDelivered: 9,
                    abortAfterCompleted: 200,
                    abortOnTimeoutRate: 0.25,
                    abortOnFailureRate: 0.50));
        }

        [Fact]
        public void ShouldAbort_true_when_completed_failure_rate_exceeds_threshold()
        {
            Assert.True(
                StressAbortDecision.ShouldAbort(
                    requestsStarted: 500,
                    workSuccess: 50,
                    workNotFound: 100,
                    workOtherFailure: 50,
                    timeouts: 0,
                    articlesDelivered: 40,
                    abortAfterCompleted: 200,
                    abortOnTimeoutRate: 0.25,
                    abortOnFailureRate: 0.50));
        }

        [Fact]
        public void ShouldAbort_true_when_completed_timeout_rate_exceeds_threshold()
        {
            Assert.True(
                StressAbortDecision.ShouldAbort(
                    requestsStarted: 300,
                    workSuccess: 150,
                    workNotFound: 0,
                    workOtherFailure: 0,
                    timeouts: 100,
                    articlesDelivered: 150,
                    abortAfterCompleted: 200,
                    abortOnTimeoutRate: 0.25,
                    abortOnFailureRate: 0.50));
        }

        [Fact]
        public void ShouldAbort_false_when_below_completed_threshold()
        {
            Assert.False(
                StressAbortDecision.ShouldAbort(
                    requestsStarted: 1000,
                    workSuccess: 10,
                    workNotFound: 10,
                    workOtherFailure: 10,
                    timeouts: 50,
                    articlesDelivered: 0,
                    abortAfterCompleted: 200,
                    abortOnTimeoutRate: 0.25,
                    abortOnFailureRate: 0.50));
        }
    }
}
