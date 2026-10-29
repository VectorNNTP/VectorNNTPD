using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.ReaderService.NntpDb;

namespace VectorNNTPD.ReaderService.Authentication
{
    /// <summary>
    /// Loads <c>nntpusers</c> through the existing NntpDB connection pool.
    /// </summary>
    public sealed class MySqlUserRecordStore : INntpUserRecordStore
    {
        private readonly NntpDbService _nntpDb;
        private readonly ILogger<MySqlUserRecordStore> _logger;

        /// <summary>Initializes a new instance of the <see cref="MySqlUserRecordStore"/> class.</summary>
        public MySqlUserRecordStore(NntpDbService nntpDb, ILogger<MySqlUserRecordStore> logger)
        {
            ArgumentNullException.ThrowIfNull(nntpDb);
            ArgumentNullException.ThrowIfNull(logger);
            _nntpDb = nntpDb;
            _logger = logger;
        }

        /// <inheritdoc />
        public async ValueTask<NntpUserRecord?> TryGetUserAsync(
            string accountName,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            try
            {
                await using var connection = await NntpDbConnections.OpenAsync(_nntpDb, cancellationToken).ConfigureAwait(false);
                return await connection.QueryUserAccountAsync(accountName, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (NntpDbUnavailableException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                AuthenticationLogMessages.UserLookupFailed(_logger, ex, accountName);
                throw new NntpDbUnavailableException("MySQL nntpusers lookup failed.", ex);
            }
        }
    }
}
