namespace VectorNNTPD.ReaderService.PostFilter
{
    /// <summary>
    /// Loads the cluster PostFilter policy from NntpDB. Must not be called from the POST hot path.
    /// </summary>
    public interface IPostFilterPolicyRepository
    {
        /// <summary>
        /// Reads the singleton policy and its collection tables.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel the load.</param>
        /// <returns>The complete policy document.</returns>
        /// <exception cref="VectorNNTPD.Common.NntpDb.NntpDbUnavailableException">
        /// MySQL is unavailable or the query failed.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The singleton row is missing or the row set cannot be mapped.
        /// </exception>
        ValueTask<PostFilterPolicyRecord> LoadAsync(CancellationToken cancellationToken = default);
    }
}
