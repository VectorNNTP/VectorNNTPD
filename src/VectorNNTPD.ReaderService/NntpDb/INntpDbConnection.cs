using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.ReaderService.NntpDb
{
    /// <summary>
    /// NNTPD query surface over one logical MySQL connection.
    /// Dispose returns the physical connection to MySqlConnector's pool.
    /// </summary>
    public interface INntpDbConnection : INntpDbSession
    {
        /// <summary>
        /// Executes <see cref="NntpGroupQueries.SelectNewsgroups"/> and returns every row.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel the query.</param>
        /// <returns>The complete result set. The caller must not retain this connection.</returns>
        ValueTask<IReadOnlyList<NntpGroupRow>> QueryNewsgroupsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Looks up one <c>nntpusers</c> row for reader authentication.
        /// </summary>
        /// <param name="accountName">Plaintext wire username. Bound as <c>@account_name</c>.</param>
        /// <param name="cancellationToken">Token used to cancel the query.</param>
        /// <returns>The mapped account, or <see langword="null"/> when no row exists.</returns>
        ValueTask<Authentication.NntpUserRecord?> QueryUserAccountAsync(
            string accountName,
            CancellationToken cancellationToken);

        /// <summary>
        /// Executes <see cref="NntpModeratorQueries.SelectEnabledModerators"/> and returns every row.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel the query.</param>
        /// <returns>Enabled moderator rows in <c>moderator_id</c> order.</returns>
        ValueTask<IReadOnlyList<Moderation.NntpModeratorRow>> QueryEnabledModeratorsAsync(
            CancellationToken cancellationToken);

        /// <summary>
        /// Loads one published PostFilter revision and the collection rows that
        /// belong to that revision.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel the queries.</param>
        /// <returns>
        /// The mapped policy, or <see langword="null"/> when
        /// <c>nntppostfiltercurrent.policy_id = 1</c> is missing.
        /// </returns>
        ValueTask<PostFilter.PostFilterPolicyRecord?> QueryPostFilterPolicyAsync(
            CancellationToken cancellationToken);

        /// <summary>
        /// Loads the singleton published Transit catalogue.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel the queries.</param>
        /// <returns>
        /// The mapped catalogue, or <see langword="null"/> when
        /// <c>nntptransitcurrent.policy_id = 1</c> is missing.
        /// </returns>
        ValueTask<Transit.TransitCatalogueRecord?> QueryTransitCatalogueAsync(
            CancellationToken cancellationToken);

        /// <summary>
        /// Inserts one PostFilter rejection evidence row. Not used on the accept path.
        /// </summary>
        ValueTask InsertPostFilterRejectionAsync(
            PostFilter.PostFilterRejectionEvidence evidence,
            CancellationToken cancellationToken);

        /// <summary>
        /// Atomically subtracts <paramref name="bytes"/> from
        /// <c>account_byte_limit</c>, clamping at zero. Remaining is never negative.
        /// </summary>
        /// <param name="accountName">Plaintext wire username. Bound as <c>@account_name</c>.</param>
        /// <param name="bytes">Non-negative byte count to subtract.</param>
        /// <param name="cancellationToken">Token used to cancel the transaction.</param>
        /// <returns>
        /// Found status, bytes actually subtracted, and remaining after the update.
        /// </returns>
        ValueTask<AccountByteConsumeResult> ConsumeAccountBytesAsync(
            string accountName,
            long bytes,
            CancellationToken cancellationToken);

        /// <summary>
        /// Reads remaining <c>account_byte_limit</c> without mutating. Does not use the
        /// AUTHINFO user-record cache.
        /// </summary>
        /// <param name="accountName">Plaintext wire username. Bound as <c>@account_name</c>.</param>
        /// <param name="cancellationToken">Token used to cancel the query.</param>
        ValueTask<AccountByteConsumeResult> QueryAccountByteRemainingAsync(
            string accountName,
            CancellationToken cancellationToken);
    }
}
