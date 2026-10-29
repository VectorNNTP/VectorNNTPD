namespace VectorNNTPD.ReaderService.PostFilter.Quota
{
    /// <summary>
    /// Per-window ceilings. <c>0</c> disables that dimension.
    /// </summary>
    /// <param name="MaxMessagesLong">Message ceiling for L. <c>0</c> is disabled.</param>
    /// <param name="MaxBytesLong">Byte ceiling for L. <c>0</c> is disabled.</param>
    /// <param name="MaxIdenticalLong">Identical-body ceiling for L. <c>0</c> is disabled.</param>
    /// <param name="MaxMessagesShort">Message ceiling for S. <c>0</c> is disabled.</param>
    /// <param name="MaxBytesShort">Byte ceiling for S. <c>0</c> is disabled.</param>
    /// <param name="MaxIdenticalShort">Identical-body ceiling for S. <c>0</c> is disabled.</param>
    internal readonly record struct PostFilterQuotaCeilings(
        long MaxMessagesLong,
        long MaxBytesLong,
        long MaxIdenticalLong,
        long MaxMessagesShort,
        long MaxBytesShort,
        long MaxIdenticalShort)
    {
        /// <summary>Throws when any ceiling is negative.</summary>
        public void Validate()
        {
            ArgumentOutOfRangeException.ThrowIfNegative(MaxMessagesLong);
            ArgumentOutOfRangeException.ThrowIfNegative(MaxBytesLong);
            ArgumentOutOfRangeException.ThrowIfNegative(MaxIdenticalLong);
            ArgumentOutOfRangeException.ThrowIfNegative(MaxMessagesShort);
            ArgumentOutOfRangeException.ThrowIfNegative(MaxBytesShort);
            ArgumentOutOfRangeException.ThrowIfNegative(MaxIdenticalShort);
        }

        /// <summary>Returns the message ceiling for <paramref name="window"/>.</summary>
        public long MaxMessages(char window) =>
            window == PostFilterQuotaKeys.LongWindow ? MaxMessagesLong : MaxMessagesShort;

        /// <summary>Returns the byte ceiling for <paramref name="window"/>.</summary>
        public long MaxBytes(char window) =>
            window == PostFilterQuotaKeys.LongWindow ? MaxBytesLong : MaxBytesShort;

        /// <summary>Returns the identical-body ceiling for <paramref name="window"/>.</summary>
        public long MaxIdentical(char window) =>
            window == PostFilterQuotaKeys.LongWindow ? MaxIdenticalLong : MaxIdenticalShort;
    }
}
