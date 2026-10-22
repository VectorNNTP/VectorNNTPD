using MySqlConnector;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.Common.NntpDb
{
    /// <summary>
    /// Opens logical MySQL connections from the configured NntpDB connection string.
    /// </summary>
    /// <remarks>
    /// Provider pooling is controlled only by <c>ConnectionStrings:NntpDB</c>.
    /// This factory does not rewrite that string.
    /// </remarks>
    public sealed class MySqlNntpDbConnectionFactory : INntpDbConnectionFactory
    {
        /// <inheritdoc />
        public async Task<INntpDbSession> OpenAsync(string connectionString, CancellationToken cancellationToken)
        {
            await using var ownership = new AsyncDisposableOwnership<MySqlConnection>(CreateConnection(connectionString));
            try
            {
                await ownership.Resource.OpenAsync(cancellationToken).ConfigureAwait(false);
                return new MySqlNntpDbSession(ownership.Transfer());
            }
            catch (MySqlException ex) when (IsAuthenticationFailure(ex))
            {
                throw new NntpDbAuthenticationException("MySQL authentication failed.", ex);
            }
            catch (ArgumentException ex)
            {
                throw new NntpDbConfigurationException(ex.Message, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                and not NntpDbAuthenticationException
                and not NntpDbConfigurationException)
            {
                throw new NntpDbUnavailableException("MySQL connection failed.", ex);
            }
        }

        /// <summary>
        /// Creates an unopened provider connection from the configured string without rewriting it.
        /// </summary>
        /// <param name="connectionString">Configured connection string.</param>
        /// <returns>An unopened <see cref="MySqlConnection"/>.</returns>
        internal static MySqlConnection CreateConnection(string connectionString)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            return new MySqlConnection(connectionString);
        }

        /// <summary>Reports whether <paramref name="exception"/> is an access-denied or authentication error that must not be retried.</summary>
        /// <param name="exception">Provider exception from open.</param>
        /// <returns><see langword="true"/> for access denied, database-access denied, or the related authentication codes.</returns>
        private static bool IsAuthenticationFailure(MySqlException exception) =>
            exception.ErrorCode is MySqlErrorCode.AccessDenied
                or (MySqlErrorCode)1044
                or (MySqlErrorCode)1045
                or (MySqlErrorCode)1698
                or (MySqlErrorCode)1862;
    }
}
