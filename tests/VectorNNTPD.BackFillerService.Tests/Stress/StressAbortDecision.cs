namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Pure abort policy for the stress harness. Separated for regression tests.
    /// </summary>
    /// <remarks>
    /// Rates use <b>completed</b> RPC outcomes (work replies + caller timeouts), never
    /// <c>RequestsStarted</c>. Flood mode intentionally keeps many requests outstanding;
    /// comparing delivered articles to started requests falsely aborts during ramp-up.
    /// </remarks>
    public static class StressAbortDecision
    {
        /// <summary>
        /// Returns whether the run should cancel based on completed-work rates.
        /// </summary>
        /// <param name="requestsStarted">Publish attempts started (diagnostic only; not the rate denominator).</param>
        /// <param name="workSuccess">Article Work Success replies received.</param>
        /// <param name="workNotFound">Article Work NotFound replies received.</param>
        /// <param name="workOtherFailure">Other non-success work replies received.</param>
        /// <param name="timeouts">Caller-side reply wait timeouts.</param>
        /// <param name="articlesDelivered">Listener ARTICLE successes.</param>
        /// <param name="abortAfterCompleted">Minimum completed RPCs before rates are evaluated.</param>
        /// <param name="abortOnTimeoutRate">Timeouts / completed threshold in [0, 1].</param>
        /// <param name="abortOnFailureRate">
        /// Failure threshold using <c>1 - articlesDelivered/completed</c> (includes not-found and work failures).
        /// </param>
        public static bool ShouldAbort(
            long requestsStarted,
            long workSuccess,
            long workNotFound,
            long workOtherFailure,
            long timeouts,
            long articlesDelivered,
            int abortAfterCompleted,
            double abortOnTimeoutRate,
            double abortOnFailureRate)
        {
            _ = requestsStarted;
            var completed = workSuccess + workNotFound + workOtherFailure + timeouts;
            if (completed < abortAfterCompleted || completed <= 0)
            {
                return false;
            }

            var timeoutRate = (double)timeouts / completed;
            var failureRate = 1.0 - ((double)articlesDelivered / completed);
            return timeoutRate >= abortOnTimeoutRate || failureRate >= abortOnFailureRate;
        }
    }
}
