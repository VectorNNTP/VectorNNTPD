using System.Data.Common;
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
        /// The server threw <see cref="MySqlException"/> (it is the inner exception).
        /// </exception>
        /// <remarks>
        /// The connection, command, and reader are disposed of before this method returns.
        /// The instance keeps no per-query state, so overlapping calls each open a connection.
        /// A SQL <c>NULL</c> or unreadable cell becomes a null field on <see cref="ProviderAccountRow"/> so
        /// <see cref="ProviderAccountMapper"/> can reject that row without aborting the snapshot.
        /// Reader failures other than <see cref="MySqlException"/> and per-cell conversion propagate unwrapped
        /// when they are not cell-level conversion failures handled by <see cref="MaterializeRow"/>.
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
        /// <param name="cancellationToken">Observed between rows by <see cref="DbDataReader.ReadAsync(CancellationToken)"/>.</param>
        /// <returns>The rows read. Empty when the reader has no rows. Malformed cells become null fields for the mapper.</returns>
        internal static async Task<IReadOnlyList<ProviderAccountRow>> ReadRowsAsync(
            DbDataReader reader,
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
                rows.Add(MaterializeRow(
                    reader.GetValue(backboneOrdinal),
                    reader.GetValue(hostnameOrdinal),
                    reader.GetValue(keepAliveOrdinal),
                    reader.GetValue(maxConnectionsOrdinal),
                    reader.GetValue(usernameOrdinal),
                    reader.GetValue(passwordOrdinal),
                    reader.GetValue(portOrdinal),
                    reader.GetValue(useSslOrdinal)));
            }

            return rows;
        }

        /// <summary>
        /// Converts raw ADO.NET cell values into a <see cref="ProviderAccountRow"/> without throwing on
        /// <see cref="DBNull"/> or unexpected types.
        /// </summary>
        /// <param name="backbone">Raw <c>backbone</c> cell.</param>
        /// <param name="hostname">Raw <c>hostname</c> cell.</param>
        /// <param name="keepAlive">Raw <c>keepalive</c> cell.</param>
        /// <param name="maxConnections">Raw <c>maxconnections</c> cell.</param>
        /// <param name="username">Raw <c>username</c> cell. <see cref="DBNull"/> becomes <see langword="null"/> (authentication disabled).</param>
        /// <param name="password">Raw <c>password</c> cell. <see cref="DBNull"/> becomes <see langword="null"/> (authentication disabled).</param>
        /// <param name="port">Raw <c>port</c> cell.</param>
        /// <param name="useSsl">Raw <c>usessl</c> cell.</param>
        /// <returns>
        /// A row whose null fields represent SQL NULL or unreadable values for <see cref="ProviderAccountMapper"/> to reject
        /// or, for credentials, to treat as authentication disabled.
        /// </returns>
        internal static ProviderAccountRow MaterializeRow(
            object backbone,
            object hostname,
            object keepAlive,
            object maxConnections,
            object username,
            object password,
            object port,
            object useSsl) =>
            new(
                ReadStringCell(backbone),
                ReadStringCell(hostname),
                TryParseKeepAlive(keepAlive),
                ReadInt32Cell(maxConnections),
                ReadStringCell(username),
                ReadStringCell(password),
                ReadInt32Cell(port),
                ReadStringCell(useSsl));

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

        /// <summary>
        /// Attempts to parse <c>keepalive</c> without aborting the snapshot.
        /// </summary>
        /// <param name="raw">Value returned by the reader for <c>keepalive</c>.</param>
        /// <returns>The byte value, or <see langword="null"/> when the cell is SQL NULL or not an accepted integer.</returns>
        internal static byte? TryParseKeepAlive(object raw)
        {
            if (raw is null || raw is DBNull)
            {
                return null;
            }

            try
            {
                return ParseKeepAlive(raw);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OverflowException or InvalidCastException)
            {
                return null;
            }
        }

        /// <summary>Reads a string cell. SQL NULL and unexpected types become <see langword="null"/>.</summary>
        /// <param name="raw">Raw cell value from <see cref="DbDataReader.GetValue(int)"/>.</param>
        /// <returns>The string, or <see langword="null"/> when absent or not a string.</returns>
        private static string? ReadStringCell(object raw) =>
            raw switch
            {
                null => null,
                DBNull => null,
                string value => value,
                _ => null,
            };

        /// <summary>Reads an integer cell. SQL NULL, overflow, and unexpected types become <see langword="null"/>.</summary>
        /// <param name="raw">Raw cell value from <see cref="DbDataReader.GetValue(int)"/>.</param>
        /// <returns>The integer, or <see langword="null"/> when absent or not convertible.</returns>
        private static int? ReadInt32Cell(object raw)
        {
            try
            {
                return raw switch
                {
                    null => null,
                    DBNull => null,
                    byte value => value,
                    sbyte value => value,
                    short value => value,
                    ushort value => value,
                    int value => value,
                    uint value => checked((int)value),
                    long value => checked((int)value),
                    ulong value => checked((int)value),
                    _ => null,
                };
            }
            catch (OverflowException)
            {
                return null;
            }
        }
    }
}
