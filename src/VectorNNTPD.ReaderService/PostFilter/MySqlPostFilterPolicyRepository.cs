using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.ReaderService.NntpDb;

namespace VectorNNTPD.ReaderService.PostFilter
{
    /// <summary>Loads the published PostFilter revision through the existing NntpDB connection pool.</summary>
    public sealed class MySqlPostFilterPolicyRepository : IPostFilterPolicyRepository
    {
        private readonly NntpDbService _nntpDb;
        private readonly ILogger<MySqlPostFilterPolicyRepository> _logger;

        /// <summary>Initializes a new instance of the <see cref="MySqlPostFilterPolicyRepository"/> class.</summary>
        public MySqlPostFilterPolicyRepository(
            NntpDbService nntpDb,
            ILogger<MySqlPostFilterPolicyRepository> logger)
        {
            ArgumentNullException.ThrowIfNull(nntpDb);
            ArgumentNullException.ThrowIfNull(logger);
            _nntpDb = nntpDb;
            _logger = logger;
        }

        /// <inheritdoc />
        public async ValueTask<PostFilterPolicyRecord> LoadAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await using var connection = await NntpDbConnections.OpenAsync(_nntpDb, cancellationToken).ConfigureAwait(false);
                var record = await connection.QueryPostFilterPolicyAsync(cancellationToken).ConfigureAwait(false);
                return record ?? throw new InvalidOperationException(
                    "nntppostfiltercurrent is missing policy_id = 1. NNTPD will not invent a local PostFilter policy.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException)
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
                PostFilterLogMessages.PolicyRepositoryFailed(_logger, ex);
                throw new NntpDbUnavailableException("MySQL nntppostfilterpolicy lookup failed.", ex);
            }
        }
    }
}
