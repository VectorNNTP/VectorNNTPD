using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.ReaderService.NntpDb;

namespace VectorNNTPD.ReaderService.Moderation
{
    /// <summary>Loads <c>nntpmoderators</c> through the existing NntpDB connection pool.</summary>
    public sealed class MySqlNntpModeratorRepository : INntpModeratorRepository
    {
        private readonly NntpDbService _nntpDb;
        private readonly ILogger<MySqlNntpModeratorRepository> _logger;

        /// <summary>Initializes a new instance of the <see cref="MySqlNntpModeratorRepository"/> class.</summary>
        public MySqlNntpModeratorRepository(NntpDbService nntpDb, ILogger<MySqlNntpModeratorRepository> logger)
        {
            ArgumentNullException.ThrowIfNull(nntpDb);
            ArgumentNullException.ThrowIfNull(logger);
            _nntpDb = nntpDb;
            _logger = logger;
        }

        /// <inheritdoc />
        public async ValueTask<IReadOnlyList<NntpModeratorRow>> GetEnabledAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                await using var connection = await NntpDbConnections.OpenAsync(_nntpDb, cancellationToken).ConfigureAwait(false);
                return await connection.QueryEnabledModeratorsAsync(cancellationToken).ConfigureAwait(false);
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
                ModeratorCatalogueLogMessages.RepositoryFailed(_logger, ex);
                throw new NntpDbUnavailableException("MySQL nntpmoderators lookup failed.", ex);
            }
        }
    }
}
