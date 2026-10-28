using System.Diagnostics;

namespace VectorNNTPD.BackFillerService.Diagnostics
{
    /// <summary>
    /// Lock-free fixed-bucket latency histogram in microseconds.
    /// Buckets are powers of two from 1 µs through 2^20 µs (~1.05 s), plus an overflow bucket.
    /// </summary>
    /// <remarks>
    /// Percentiles are approximate (upper edge of the cumulative bucket). Memory is bounded:
    /// <see cref="BucketCount"/> counters plus sum/count/max/failure fields.
    /// </remarks>
    internal sealed class StageLatencyHistogram
    {
        /// <summary>Number of power-of-two buckets including overflow (indices 0..20 + 21).</summary>
        internal const int BucketCount = 22;

        /// <summary>Upper inclusive microsecond edge for bucket <paramref name="index"/> (overflow uses <see cref="long.MaxValue"/>).</summary>
        internal static long BucketUpperUs(int index) =>
            index >= BucketCount - 1 ? long.MaxValue : 1L << index;

        private readonly long[] _buckets = new long[BucketCount];
        private long _count;
        private long _sumUs;
        private long _maxUs;
        private long _failures;
        private long _timeouts;

        /// <summary>Successful and failed observations that recorded a duration.</summary>
        internal long Count => Volatile.Read(ref _count);

        /// <summary>Sum of recorded durations in microseconds.</summary>
        internal long SumMicroseconds => Volatile.Read(ref _sumUs);

        /// <summary>Maximum recorded duration in microseconds.</summary>
        internal long MaxMicroseconds => Volatile.Read(ref _maxUs);

        /// <summary>Failure outcomes attributed to this stage (not every observation).</summary>
        internal long Failures => Volatile.Read(ref _failures);

        /// <summary>Timeout outcomes attributed to this stage.</summary>
        internal long Timeouts => Volatile.Read(ref _timeouts);

        /// <summary>Records one elapsed interval from a prior <see cref="Stopwatch.GetTimestamp"/>.</summary>
        /// <param name="startedTimestamp"><see cref="Stopwatch.GetTimestamp"/> at stage start.</param>
        internal void Record(long startedTimestamp)
        {
            var us = (long)(Stopwatch.GetElapsedTime(startedTimestamp).TotalMicroseconds);
            if (us < 0)
            {
                us = 0;
            }

            RecordMicroseconds(us);
        }

        /// <summary>Records a duration already expressed in microseconds (tests).</summary>
        /// <param name="microseconds">Non-negative elapsed microseconds.</param>
        internal void RecordMicroseconds(long microseconds)
        {
            if (microseconds < 0)
            {
                microseconds = 0;
            }

            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _sumUs, microseconds);
            UpdateMax(ref _maxUs, microseconds);
            Interlocked.Increment(ref _buckets[BucketIndex(microseconds)]);
        }

        /// <summary>Increments the failure counter without recording a duration.</summary>
        internal void RecordFailure() => Interlocked.Increment(ref _failures);

        /// <summary>Increments the timeout counter without recording a duration.</summary>
        internal void RecordTimeout() => Interlocked.Increment(ref _timeouts);

        /// <summary>Copies bucket counts for snapshotting.</summary>
        /// <param name="destination">Must have length ≥ <see cref="BucketCount"/>.</param>
        internal void CopyBuckets(Span<long> destination)
        {
            for (var i = 0; i < BucketCount; i++)
            {
                destination[i] = Volatile.Read(ref _buckets[i]);
            }
        }

        /// <summary>Approximate percentile in milliseconds from the current bucket snapshot.</summary>
        /// <param name="quantile">Inclusive (0, 1].</param>
        /// <returns>Milliseconds at the upper edge of the cumulative bucket; 0 when empty.</returns>
        internal double ApproximatePercentileMilliseconds(double quantile)
        {
            var count = Volatile.Read(ref _count);
            if (count == 0 || quantile <= 0)
            {
                return 0;
            }

            Span<long> buckets = stackalloc long[BucketCount];
            CopyBuckets(buckets);
            var target = (long)Math.Ceiling(Math.Clamp(quantile, 0, 1) * count);
            if (target < 1)
            {
                target = 1;
            }

            long cumulative = 0;
            for (var i = 0; i < BucketCount; i++)
            {
                cumulative += buckets[i];
                if (cumulative >= target)
                {
                    var upperUs = BucketUpperUs(i);
                    if (upperUs == long.MaxValue)
                    {
                        return Volatile.Read(ref _maxUs) / 1000.0;
                    }

                    return upperUs / 1000.0;
                }
            }

            return Volatile.Read(ref _maxUs) / 1000.0;
        }

        /// <summary>Mean duration in milliseconds; 0 when empty.</summary>
        internal double MeanMilliseconds
        {
            get
            {
                var count = Volatile.Read(ref _count);
                return count == 0 ? 0 : (Volatile.Read(ref _sumUs) / (double)count) / 1000.0;
            }
        }

        /// <summary>Maps microseconds to a power-of-two bucket index.</summary>
        internal static int BucketIndex(long microseconds)
        {
            if (microseconds <= 1)
            {
                return 0;
            }

            // ceil(log2(us)): bucket i covers (2^(i-1), 2^i] for i>=1, with i=0 covering [0,1].
            var logCeil = 64 - (int)ulong.LeadingZeroCount((ulong)(microseconds - 1));
            return Math.Min(logCeil, BucketCount - 1);
        }

        private static void UpdateMax(ref long location, long candidate)
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
}
