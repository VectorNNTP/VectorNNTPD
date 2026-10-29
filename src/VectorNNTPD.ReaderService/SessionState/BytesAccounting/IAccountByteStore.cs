namespace VectorNNTPD.ReaderService.SessionState.BytesAccounting
{
    /// <summary>Cluster remaining-quota store. Not used on the NNTP write path.</summary>
    internal interface IAccountByteStore
    {
        /// <summary>
        /// Atomically applies one MySQL-committed batch. The same
        /// <paramref name="batchId"/> is a no-op. Remaining is floored to
        /// <paramref name="mysqlRemainingAfter"/> and never increased.
        /// </summary>
        /// <returns>Remaining after the apply, or <see langword="null"/> when the store is unavailable.</returns>
        ValueTask<long?> ApplyAsync(
            string accountName,
            string batchId,
            long consumed,
            long mysqlRemainingAfter,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads remaining. <see langword="null"/> means unavailable;
        /// <see cref="AccountByteKeys.Missing"/> means the key is absent.
        /// </summary>
        ValueTask<long?> ObserveAsync(string accountName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes cluster remaining-quota state for an explicit MySQL top-up.
        /// The next observe/apply treats the key as missing and may initialize from
        /// current durable remaining. Does not raise remaining.
        /// </summary>
        /// <returns>
        /// <c>1</c> if a key was removed, <c>0</c> if it was already absent,
        /// or <see langword="null"/> when the store is unavailable.
        /// </returns>
        ValueTask<long?> DeleteAsync(string accountName, CancellationToken cancellationToken = default);
    }
}
