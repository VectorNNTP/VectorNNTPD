namespace VectorNNTPD.TransitService.PostFilter.Quota
{
    /// <summary>Fixed-epoch window lengths in milliseconds.</summary>
    /// <param name="LongWindowMs">Sustained (L) window. Must be greater than zero.</param>
    /// <param name="ShortWindowMs">Burst (S) window. Must be greater than zero.</param>
    internal readonly record struct PostFilterQuotaWindows(long LongWindowMs, long ShortWindowMs)
    {
        /// <summary>Returns the current bucket id for <paramref name="window"/> at <paramref name="nowMs"/>.</summary>
        public long BucketId(char window, long nowMs)
        {
            var ms = WindowMs(window);
            return nowMs / ms;
        }

        /// <summary>Returns whether bucket <paramref name="bucketId"/> of <paramref name="window"/> has ended.</summary>
        public bool BucketEnded(char window, long bucketId, long nowMs) =>
            (bucketId + 1) * WindowMs(window) <= nowMs;

        /// <summary>Returns the millisecond length of <paramref name="window"/>.</summary>
        public long WindowMs(char window) => window == PostFilterQuotaKeys.LongWindow
            ? LongWindowMs
            : ShortWindowMs;

        /// <summary>Throws when either window length is not positive.</summary>
        public void Validate()
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LongWindowMs, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ShortWindowMs, 0);
        }
    }
}
