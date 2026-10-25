using System.Collections.Concurrent;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Aggregate latency statistics for one observable harness stage.
    /// </summary>
    /// <remarks>
    /// Mean is the arithmetic mean of recorded samples. Percentiles are order statistics.
    /// </remarks>
    public sealed record StageLatencyStats(
        string StageName,
        string Description,
        long Count,
        double MeanMs,
        double P50Ms,
        double P90Ms,
        double P95Ms,
        double P99Ms,
        double MaxMs)
    {
        /// <summary>Builds stage statistics from unsorted millisecond samples; empty bag yields zeros.</summary>
        /// <param name="name">Short stage identifier used in reports.</param>
        /// <param name="description">Human-readable scope of what the samples measure.</param>
        /// <param name="bag">Concurrent sample bag populated during the run.</param>
        public static StageLatencyStats From(string name, string description, ConcurrentBag<long> bag)
        {
            if (bag.IsEmpty)
            {
                return new StageLatencyStats(name, description, 0, 0, 0, 0, 0, 0, 0);
            }

            var values = bag.ToArray();
            Array.Sort(values);
            double sum = 0;
            for (var i = 0; i < values.Length; i++)
            {
                sum += values[i];
            }

            return new StageLatencyStats(
                name,
                description,
                values.Length,
                sum / values.Length,
                At(values, 0.50),
                At(values, 0.90),
                At(values, 0.95),
                At(values, 0.99),
                values[^1]);
        }

        /// <summary>Order-statistic at <paramref name="quantile"/> on a sorted ascending array.</summary>
        private static double At(long[] sorted, double quantile)
        {
            var index = (int)Math.Clamp(Math.Ceiling(quantile * sorted.Length) - 1, 0, sorted.Length - 1);
            return sorted[index];
        }
    }
}
