using MySqlConnector;
using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.BackFillerService.Accounts
{
    /// <summary>
    /// Reads <c>nntpbackfilleraccounts</c> for the configured server id. Opens a connection only while querying.
    /// </summary>
    internal sealed class MySqlProviderAccountSource : IProviderAccountSource
    {
        /// <summary>Authoritative table name from the old BackFiller.</summary>
        internal const string AccountsTableName = "nntpbackfilleraccounts";

        /// <summary>
        /// Parameterized query for the columns <see cref="ProviderAccountMapper"/> reads, filtered by <c>serverid</c>.
        /// </summary>
        /// <remarks><c>entryid</c> and <c>serverid</c> are not selected. The filter still uses <c>@ServerId</c>.</remarks>
        internal const string AccountsQuery =
            "SELECT " +
            "backbone, " +
            "hostname, " +
            "keepalive, " +
            "maxconnections, " +
            "password, " +
            "port, " +
            "username, " +
            "usessl " +
            "FROM nntpbackfilleraccounts " +
            "WHERE serverid = @ServerId;";

        /// <summary>Shared database service. Connections are opened only while querying.</summary>
        private readonly NntpDbService _nntpDb;

        /// <summary>Server id copied at construction and bound to <c>@ServerId</c> as an unsigned byte.</summary>
        private readonly byte _serverId;

        /// <summary>Retains the database service and server id.</summary>
        /// <param name="nntpDb">Started NntpDB service. This source does not start it.</param>
        /// <param name="serverId">BackFiller server id bound to <c>@ServerId</c>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="nntpDb"/> is null.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="serverId"/> is outside 0–255.</exception>
        internal MySqlProviderAccountSource(NntpDbService nntpDb, int serverId)
        {
            ArgumentNullException.ThrowIfNull(nntpDb);
            if (serverId is < byte.MinValue or > byte.MaxValue)
            {
                throw new InvalidOperationException("BackFiller:ServerId must fit in an unsigned byte for nntpbackfilleraccounts.");
            }

            _nntpDb = nntpDb;
            _serverId = (byte)serverId;
        }

        /// <summary>
        /// Opens one connection, runs <see cref="AccountsQuery"/> for <see cref="_serverId"/>, and returns every row.
        /// </summary>
        /// <param name="cancellationToken">Passed to open, execute, and row reads. Cancellation is not wrapped.</param>
        /// <returns>Rows in reader order. Backbone and session rules are not applied here.</returns>
        /// <exception cref="OperationCanceledException">Open, execute, or read threw <see cref="OperationCanceledException"/>. It propagates unwrapped.</exception>
        /// <exception cref="InvalidOperationException">
        /// The server threw <see cref="MySqlException"/> (it is the inner exception), or <c>keepalive</c> parsing failed.
        /// </exception>
        /// <exception cref="OverflowException"><c>keepalive</c> is an integer outside 0–255.</exception>
        /// <remarks>
        /// The connection, command, and reader are disposed of before this method returns.
        /// The instance keeps no per-query state, so overlapping calls each open a connection.
        /// Reader failures other than <see cref="MySqlException"/> propagate unwrapped.
        /// </remarks>
        public async Task<IReadOnlyList<ProviderAccountRow>> QueryAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var session = await _nntpDb.OpenAsync(cancellationToken).ConfigureAwait(false);

                {
                    var command = session.CreateCommand();
                    await using (command.ConfigureAwait(false))
                    {
                        command.CommandText = AccountsQuery;
                        _ = command.Parameters.Add(new MySqlParameter("@ServerId", MySqlDbType.UByte) { Value = _serverId });
                        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        await using (reader.ConfigureAwait(false))
                        {
                            return await ReadRowsAsync(reader, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (MySqlException ex)
            {
                throw new InvalidOperationException("Provider account query failed against NntpDB.", ex);
            }
        }

        /// <summary>Materializes every remaining row. Column ordinals are resolved once before the first read.</summary>
        /// <param name="reader">Open the reader positioned before the first row. Not disposed of here.</param>
        /// <param name="cancellationToken">Observed between rows by <see cref="MySqlDataReader.ReadAsync(CancellationToken)"/>.</param>
        /// <returns>The rows read. Empty when the reader has no rows.</returns>
        private static async Task<IReadOnlyList<ProviderAccountRow>> ReadRowsAsync(
            MySqlDataReader reader,
            CancellationToken cancellationToken)
        {
            var rows = new List<ProviderAccountRow>();
            var backboneOrdinal = reader.GetOrdinal("backbone");
            var hostnameOrdinal = reader.GetOrdinal("hostname");
            var keepAliveOrdinal = reader.GetOrdinal("keepalive");
            var maxConnectionsOrdinal = reader.GetOrdinal("maxconnections");
            var passwordOrdinal = reader.GetOrdinal("password");
            var portOrdinal = reader.GetOrdinal("port");
            var usernameOrdinal = reader.GetOrdinal("username");
            var useSslOrdinal = reader.GetOrdinal("usessl");

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ProviderAccountRow(
                    reader.GetString(backboneOrdinal),
                    reader.GetString(hostnameOrdinal),
                    ParseKeepAlive(reader.GetValue(keepAliveOrdinal)),
                    reader.GetInt32(maxConnectionsOrdinal),
                    reader.GetString(usernameOrdinal),
                    reader.GetString(passwordOrdinal),
                    reader.GetInt32(portOrdinal),
                    reader.GetString(useSslOrdinal)));
            }

            return rows;
        }

        /// <summary>Parses <c>keepalive</c> into the stored tinyint representation.</summary>
        /// <param name="raw">Value returned by the reader for <c>keepalive</c>.</param>
        /// <returns>The integer value as a byte.</returns>
        /// <exception cref="InvalidOperationException"><paramref name="raw"/> is null or not one of the accepted integer types.</exception>
        /// <exception cref="OverflowException">The integer is outside 0–255. The conversion is checked.</exception>
        /// <remarks>Accepted types are <see cref="byte"/>, <see cref="sbyte"/>, <see cref="short"/>, <see cref="ushort"/>, <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>, and <see cref="ulong"/>.</remarks>
        internal static byte ParseKeepAlive(object raw) =>
            raw switch
            {
                byte value => value,
                sbyte value => checked((byte)value),
                short value => checked((byte)value),
                ushort value => checked((byte)value),
                int value => checked((byte)value),
                uint value => checked((byte)value),
                long value => checked((byte)value),
                ulong value => checked((byte)value),
                _ => throw new InvalidOperationException("nntpbackfilleraccounts.keepalive is not a non-null integer."),
            };
    }
}
