using VectorNNTPD.ReaderService.NntpDb;

namespace VectorNNTPD.ReaderService.SessionState.BytesAccounting
{
    /// <summary>Durable remaining-quota store (MySQL <c>nntpusers.account_byte_limit</c>).</summary>
    internal interface IAccountByteDurableStore
    {
        /// <summary>
        /// Atomically subtracts <paramref name="bytes"/> from remaining quota, clamping at zero.
        /// </summary>
        ValueTask<AccountByteConsumeResult> ConsumeAsync(
            string accountName,
            long bytes,
            CancellationToken cancellationToken = default);

        /// <summary>Reads remaining without mutating. Does not use the AUTHINFO user-record cache.</summary>
        ValueTask<AccountByteConsumeResult> QueryRemainingAsync(
            string accountName,
            CancellationToken cancellationToken = default);
    }
}
