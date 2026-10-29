namespace VectorNNTPD.ReaderService.Diagnostics
{
    /// <summary>
    /// Interval rate helpers for feed-diagnostics snapshots.
    /// </summary>
    /// <remarks>
    /// All methods are allocation-free and safe for zero elapsed time and
    /// non-monotonic counters (reset or observed reordering).
    /// </remarks>
    public static class FeedRateCalculator
    {
        /// <summary>
        /// Returns <c>current - previous</c> when the counter advanced; otherwise <c>0</c>.
        /// </summary>
        public static long NonNegativeDelta(long current, long previous)
        {
            if (current < previous)
            {
                return 0;
            }

            return current - previous;
        }

        /// <summary>
        /// Returns <paramref name="delta"/> per second, or <c>0</c> when elapsed time is not positive.
        /// </summary>
        public static double PerSecond(long delta, TimeSpan elapsed)
        {
            var seconds = elapsed.TotalSeconds;
            if (seconds <= 0 || delta <= 0)
            {
                return 0;
            }

            return delta / seconds;
        }

        /// <summary>
        /// Returns megabits/sec from a byte delta (decimal Mbps: bits / 1e6).
        /// </summary>
        public static double MegabitsPerSecond(long byteDelta, TimeSpan elapsed) =>
            PerSecond(byteDelta, elapsed) * 8d / 1_000_000d;
    }
}
