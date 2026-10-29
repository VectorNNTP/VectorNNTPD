using System.Data;
using System.Globalization;
using MySqlConnector;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.NntpDb
{
    /// <summary>Reads one coherent Transit publication inside a repeatable-read transaction.</summary>
    internal static class NntpTransitCatalogueLoader
    {
        /// <summary>
        /// Loads <c>nntptransitcurrent</c>. Returns <see langword="null"/> when the singleton row is missing.
        /// </summary>
        public static async ValueTask<TransitCatalogueRecord?> LoadAsync(
            INntpDbSession session,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(session);
            await using var transaction = await session
                .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var header = await ReadHeaderAsync(session, transaction, cancellationToken).ConfigureAwait(false);
                if (header is null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }

                var receiveCount = await ReadCountAsync(
                        session,
                        transaction,
                        NntpTransitQueries.CountReceive,
                        "@receive_revision",
                        header.ReceiveRevision,
                        cancellationToken)
                    .ConfigureAwait(false);
                var sendCount = await ReadCountAsync(
                        session,
                        transaction,
                        NntpTransitQueries.CountSend,
                        "@send_revision",
                        header.SendRevision,
                        cancellationToken)
                    .ConfigureAwait(false);
                var peers = await ReadPeersAsync(session, transaction, header, cancellationToken).ConfigureAwait(false);
                if (peers.Count != receiveCount || peers.Count != sendCount)
                {
                    throw new InvalidOperationException(
                        "Transit receive revision "
                        + header.ReceiveRevision.ToString(CultureInfo.InvariantCulture)
                        + " and send revision "
                        + header.SendRevision.ToString(CultureInfo.InvariantCulture)
                        + " do not contain the same peer identifiers.");
                }

                var allowFrom = await ReadStringsAsync(
                        session,
                        transaction,
                        NntpTransitQueries.SelectAllowFrom,
                        "@receive_revision",
                        header.ReceiveRevision,
                        "entry",
                        cancellationToken)
                    .ConfigureAwait(false);
                var endpoints = await ReadEndpointsAsync(session, transaction, header.SendRevision, cancellationToken)
                    .ConfigureAwait(false);
                var exclusions = await ReadStringsAsync(
                        session,
                        transaction,
                        NntpTransitQueries.SelectPathExclusions,
                        "@send_revision",
                        header.SendRevision,
                        "path_token",
                        cancellationToken)
                    .ConfigureAwait(false);
                var records = new List<TransitCataloguePeerRecord>(peers.Count);
                foreach (var peer in peers)
                {
                    if (!allowFrom.TryGetValue(peer.Identifier, out var allow))
                    {
                        allow = [];
                    }

                    if (!endpoints.TryGetValue(peer.Identifier, out var ends))
                    {
                        ends = [];
                    }

                    if (!exclusions.TryGetValue(peer.Identifier, out var excluded))
                    {
                        excluded = [];
                    }

                    records.Add(peer.WithChildren(allow, ends, excluded));
                }

                RejectUnknown(allowFrom, peers, "AllowFrom");
                RejectUnknown(endpoints, peers, "Send endpoint");
                RejectUnknown(exclusions, peers, "Path exclusion");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new TransitCatalogueRecord(
                    header.PublicationId,
                    header.ReceiveRevision,
                    header.SendRevision,
                    header.GlobalRevision,
                    header.WantTrash,
                    header.LogTrash,
                    records);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        private static async ValueTask<Header?> ReadHeaderAsync(
            INntpDbSession session,
            MySqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            await using var command = session.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = NntpTransitQueries.SelectCurrent;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var header = new Header(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                ReadYn(reader.GetString(4)),
                ReadYn(reader.GetString(5)));
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("nntptransitcurrent returned more than one publication.");
            }

            return header;
        }

        private static async ValueTask<int> ReadCountAsync(
            INntpDbSession session,
            MySqlTransaction transaction,
            string sql,
            string parameterName,
            long revision,
            CancellationToken cancellationToken)
        {
            await using var command = session.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue(parameterName, revision);
            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
        }

        private static async ValueTask<List<PeerBuilder>> ReadPeersAsync(
            INntpDbSession session,
            MySqlTransaction transaction,
            Header header,
            CancellationToken cancellationToken)
        {
            await using var command = session.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = NntpTransitQueries.SelectPeers;
            command.Parameters.AddWithValue("@receive_revision", header.ReceiveRevision);
            command.Parameters.AddWithValue("@send_revision", header.SendRevision);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var peers = new List<PeerBuilder>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                peers.Add(new PeerBuilder(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    ReadYn(reader.GetString(5)),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetString(8),
                    reader.GetInt32(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    ReadSendMaxArticleBytes(reader, 13),
                    reader.GetInt32(14),
                    reader.GetString(15),
                    reader.GetString(16)));
            }

            return peers;
        }

        private static async ValueTask<Dictionary<string, List<string>>> ReadStringsAsync(
            INntpDbSession session,
            MySqlTransaction transaction,
            string sql,
            string parameterName,
            long revision,
            string valueColumn,
            CancellationToken cancellationToken)
        {
            await using var command = session.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue(parameterName, revision);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var valueOrdinal = reader.GetOrdinal(valueColumn);
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identifier = reader.GetString(0);
                if (!map.TryGetValue(identifier, out var list))
                {
                    list = [];
                    map.Add(identifier, list);
                }

                list.Add(reader.GetString(valueOrdinal));
            }

            return map;
        }

        private static async ValueTask<Dictionary<string, List<TransitCatalogueEndpointRecord>>> ReadEndpointsAsync(
            INntpDbSession session,
            MySqlTransaction transaction,
            long sendRevision,
            CancellationToken cancellationToken)
        {
            await using var command = session.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = NntpTransitQueries.SelectEndpoints;
            command.Parameters.AddWithValue("@send_revision", sendRevision);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var map = new Dictionary<string, List<TransitCatalogueEndpointRecord>>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identifier = reader.GetString(0);
                if (!map.TryGetValue(identifier, out var list))
                {
                    list = [];
                    map.Add(identifier, list);
                }

                list.Add(new TransitCatalogueEndpointRecord(reader.GetString(1), reader.GetInt32(2)));
            }

            return map;
        }

        private static void RejectUnknown<T>(
            Dictionary<string, T> children,
            List<PeerBuilder> peers,
            string kind)
        {
            foreach (var identifier in children.Keys)
            {
                var found = false;
                foreach (var peer in peers)
                {
                    if (string.Equals(peer.Identifier, identifier, StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    throw new InvalidOperationException(
                        "Transit " + kind + " row '" + identifier + "' is not in the published peer set.");
                }
            }
        }

        /// <summary>
        /// Reads <c>nntptransitsend.max_article_bytes</c>.
        /// SQL <c>NULL</c> is unlimited and stays <see langword="null"/>. A stored integer stays that ceiling.
        /// </summary>
        /// <param name="record">The peer row.</param>
        /// <param name="ordinal">The send <c>max_article_bytes</c> column.</param>
        /// <returns><see langword="null"/> when the column is SQL <c>NULL</c>; otherwise the stored byte ceiling.</returns>
        internal static int? ReadSendMaxArticleBytes(IDataRecord record, int ordinal)
        {
            ArgumentNullException.ThrowIfNull(record);
            return record.IsDBNull(ordinal) ? null : record.GetInt32(ordinal);
        }

        private static bool ReadYn(string value) =>
            value switch
            {
                "Y" => true,
                "N" => false,
                _ => throw new InvalidOperationException("Transit catalogue flag is not Y or N."),
            };

        private sealed class Header(
            long publicationId,
            long receiveRevision,
            long sendRevision,
            long globalRevision,
            bool wantTrash,
            bool logTrash)
        {
            public long PublicationId { get; } = publicationId;

            public long ReceiveRevision { get; } = receiveRevision;

            public long SendRevision { get; } = sendRevision;

            public long GlobalRevision { get; } = globalRevision;

            public bool WantTrash { get; } = wantTrash;

            public bool LogTrash { get; } = logTrash;
        }

        private sealed class PeerBuilder
        {
            private readonly string _identifier;
            private readonly string _peerName;
            private readonly int _maxInbound;
            private readonly string _receiveUsername;
            private readonly string _receivePassword;
            private readonly bool _deferOnDuplicate;
            private readonly int _receiveMaxArticleBytes;
            private readonly int _receiveArticleTypes;
            private readonly string _receivePatterns;
            private readonly int _maxOutbound;
            private readonly string _sslMode;
            private readonly string _sendUsername;
            private readonly string _sendPassword;
            private readonly int? _sendMaxArticleBytes;
            private readonly int _sendArticleTypes;
            private readonly string _sendPatterns;
            private readonly string _pathToken;

            public PeerBuilder(
                string identifier,
                string peerName,
                int maxInbound,
                string receiveUsername,
                string receivePassword,
                bool deferOnDuplicate,
                int receiveMaxArticleBytes,
                int receiveArticleTypes,
                string receivePatterns,
                int maxOutbound,
                string sslMode,
                string sendUsername,
                string sendPassword,
                int? sendMaxArticleBytes,
                int sendArticleTypes,
                string sendPatterns,
                string pathToken)
            {
                _identifier = identifier;
                _peerName = peerName;
                _maxInbound = maxInbound;
                _receiveUsername = receiveUsername;
                _receivePassword = receivePassword;
                _deferOnDuplicate = deferOnDuplicate;
                _receiveMaxArticleBytes = receiveMaxArticleBytes;
                _receiveArticleTypes = receiveArticleTypes;
                _receivePatterns = receivePatterns;
                _maxOutbound = maxOutbound;
                _sslMode = sslMode;
                _sendUsername = sendUsername;
                _sendPassword = sendPassword;
                _sendMaxArticleBytes = sendMaxArticleBytes;
                _sendArticleTypes = sendArticleTypes;
                _sendPatterns = sendPatterns;
                _pathToken = pathToken;
            }

            public string Identifier => _identifier;

            public TransitCataloguePeerRecord WithChildren(
                IReadOnlyList<string> allowFrom,
                IReadOnlyList<TransitCatalogueEndpointRecord> endpoints,
                IReadOnlyList<string> pathExclusions) =>
                new(
                    _identifier,
                    _peerName,
                    _maxInbound,
                    _receiveUsername,
                    _receivePassword,
                    _deferOnDuplicate,
                    _receiveMaxArticleBytes,
                    _receiveArticleTypes,
                    _receivePatterns,
                    allowFrom,
                    _maxOutbound,
                    _sslMode,
                    _sendUsername,
                    _sendPassword,
                    _sendMaxArticleBytes,
                    _sendArticleTypes,
                    _sendPatterns,
                    _pathToken,
                    endpoints,
                    pathExclusions);
        }
    }
}
