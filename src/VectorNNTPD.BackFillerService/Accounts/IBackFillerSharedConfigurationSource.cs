using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.BackFillerService.Accounts
{
    /// <summary>Reads one <c>nntpsharedconfig</c> snapshot. Does not schedule refresh.</summary>
    internal interface IBackFillerSharedConfigurationSource
    {
        /// <summary>Reads and validates the current row.</summary>
        /// <param name="cancellationToken">Token used to cancel the read.</param>
        /// <returns>The validated snapshot.</returns>
        ValueTask<NntpSharedConfiguration> ReadAsync(CancellationToken cancellationToken);
    }

    /// <summary>Opens one NntpDB session and uses <see cref="NntpSharedConfigurationReader"/>.</summary>
    internal sealed class NntpDbSharedConfigurationSource : IBackFillerSharedConfigurationSource
    {
        /// <summary>
        /// Started NntpDB service used to open one session per read. This source does not start or dispose it.
        /// </summary>
        private readonly NntpDbService _nntpDb;

        /// <summary>Retains the database service.</summary>
        /// <param name="nntpDb">Started NntpDB service.</param>
        public NntpDbSharedConfigurationSource(NntpDbService nntpDb)
        {
            ArgumentNullException.ThrowIfNull(nntpDb);
            _nntpDb = nntpDb;
        }

        /// <inheritdoc />
        public async ValueTask<NntpSharedConfiguration> ReadAsync(CancellationToken cancellationToken)
        {
            await using var session = await _nntpDb.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await NntpSharedConfigurationReader.ReadAsync(session, cancellationToken).ConfigureAwait(false);
        }
    }
}
