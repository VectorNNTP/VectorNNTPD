using System.Data;
using System.Globalization;
using MySqlConnector;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.TransitService.Authentication;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Moderation;
using VectorNNTPD.TransitService.PostFilter;
using VectorNNTPD.TransitService.SessionState;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.NntpDb
{
    /// <summary>NNTPD queries over a shared logical session. Dispose returns it to the provider pool.</summary>
    internal sealed class MySqlNntpDbConnection : INntpDbConnection
    {
        private readonly INntpDbSession _session;

        /// <summary>Initializes a new instance wrapping an already-open provider connection.</summary>
        /// <param name="connection">Open connection. This instance owns its disposal.</param>
        public MySqlNntpDbConnection(MySqlConnection connection)
            : this(new MySqlNntpDbSession(connection))
        {
        }

        /// <summary>Initializes a new instance wrapping a shared session.</summary>
        /// <param name="session">Open session. This instance owns its disposal.</param>
        public MySqlNntpDbConnection(INntpDbSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            _session = session;
        }

        /// <inheritdoc />
        public ValueTask<int> SelectOneAsync(CancellationToken cancellationToken) =>
            _session.SelectOneAsync(cancellationToken);

        /// <inheritdoc />
        public MySqlCommand CreateCommand() => _session.CreateCommand();

        /// <inheritdoc />
        public ValueTask<MySqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            _session.BeginTransactionAsync(cancellationToken);

        /// <inheritdoc />
        public ValueTask<MySqlTransaction> BeginTransactionAsync(
            IsolationLevel isolationLevel,
            CancellationToken cancellationToken) =>
            _session.BeginTransactionAsync(isolationLevel, cancellationToken);

        /// <inheritdoc />
        public async ValueTask<IReadOnlyList<NntpGroupRow>> QueryNewsgroupsAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var command = _session.CreateCommand();
                command.CommandText = NntpGroupQueries.SelectNewsgroups;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var rows = new List<NntpGroupRow>();
                var index = 0;
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(ReadGroupRow(reader, index));
                    index++;
                }

                return rows;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException
                and not NntpDbUnavailableException
                and not Newsgroups.NewsgroupCatalogueException)
            {
                throw new NntpDbUnavailableException("MySQL newsgroup catalogue query failed.", ex);
            }
        }

        /// <inheritdoc />
        public async ValueTask<NntpUserRecord?> QueryUserAccountAsync(
            string accountName,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            try
            {
                await using var command = _session.CreateCommand();
                command.CommandText = NntpUserQueries.SelectUserByName;
                command.Parameters.AddWithValue("@account_name", accountName);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return MapUserRecord(reader, accountName);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException and not NntpDbUnavailableException)
            {
                throw new NntpDbUnavailableException("MySQL nntpusers lookup failed.", ex);
            }
        }

        /// <inheritdoc />
        public async ValueTask<IReadOnlyList<NntpModeratorRow>> QueryEnabledModeratorsAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                await using var command = _session.CreateCommand();
                command.CommandText = NntpModeratorQueries.SelectEnabledModerators;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var rows = new List<NntpModeratorRow>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(MapModeratorRow(reader));
                }

                return rows;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException and not NntpDbUnavailableException)
            {
                throw new NntpDbUnavailableException("MySQL nntpmoderators catalogue query failed.", ex);
            }
        }

        /// <inheritdoc />
        public async ValueTask<PostFilterPolicyRecord?> QueryPostFilterPolicyAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                await using var transaction = await _session
                    .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    var revision = await ReadCurrentRevisionAsync(transaction, cancellationToken)
                        .ConfigureAwait(false);
                    if (revision is null)
                    {
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return null;
                    }

                    PostFilterPolicyRecord record;
                    await using (var policyCommand = CreateRevisionCommand(
                        NntpPostFilterQueries.SelectPolicy,
                        revision.Value,
                        transaction))
                    {
                        await using var policyReader = await policyCommand
                            .ExecuteReaderAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (!await policyReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException(
                                "nntppostfiltercurrent.revision "
                                + revision.Value.ToString(CultureInfo.InvariantCulture)
                                + " has no nntppostfilterpolicy row.");
                        }

                        record = MapPolicyScalars(policyReader);
                    }

                    var deniedAccounts = new List<string>();
                    var allowlistedAccounts = new List<string>();
                    await ReadKeyedStringsAsync(
                            NntpPostFilterQueries.SelectAccounts,
                            revision.Value,
                            transaction,
                            NntpPostFilterQueries.ListKindDeny,
                            NntpPostFilterQueries.ListKindAllow,
                            deniedAccounts,
                            allowlistedAccounts,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var deniedCidrs = new List<string>();
                    var allowlistedCidrs = new List<string>();
                    await ReadKeyedStringsAsync(
                            NntpPostFilterQueries.SelectCidrs,
                            revision.Value,
                            transaction,
                            NntpPostFilterQueries.ListKindDeny,
                            NntpPostFilterQueries.ListKindAllow,
                            deniedCidrs,
                            allowlistedCidrs,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var rejectArtTypes = new List<string>();
                    var excludeArtTypes = new List<string>();
                    await ReadKeyedStringsAsync(
                            NntpPostFilterQueries.SelectArtTypes,
                            revision.Value,
                            transaction,
                            NntpPostFilterQueries.ListKindReject,
                            NntpPostFilterQueries.ListKindSaExclude,
                            rejectArtTypes,
                            excludeArtTypes,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var hosts = await ReadHostsAsync(revision.Value, transaction, cancellationToken)
                        .ConfigureAwait(false);
                    record.Options.DeniedAccounts = [.. deniedAccounts];
                    record.Options.AllowlistedAccounts = [.. allowlistedAccounts];
                    record.Options.DeniedCidrs = [.. deniedCidrs];
                    record.Options.AllowlistedCidrs = [.. allowlistedCidrs];
                    record.Options.RejectArtTypes = [.. rejectArtTypes];
                    record.Options.SpamAssassin.ExcludeArtTypes = [.. excludeArtTypes];
                    record.Options.SpamAssassin.Hosts = [.. hosts];
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return record;
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException
                and not NntpDbUnavailableException
                and not InvalidOperationException)
            {
                throw new NntpDbUnavailableException("MySQL nntppostfilterpolicy query failed.", ex);
            }
        }

        /// <inheritdoc />
        public async ValueTask InsertPostFilterRejectionAsync(
            PostFilterRejectionEvidence evidence,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            try
            {
                await using var command = _session.CreateCommand();
                command.CommandText = NntpPostFilterQueries.InsertRejection;
                command.Parameters.AddWithValue("@rejected_utc", evidence.RejectedUtc.UtcDateTime);
                command.Parameters.AddWithValue("@revision", evidence.PolicyRevision);
                command.Parameters.AddWithValue(
                    "@account_name",
                    evidence.AccountName is null ? DBNull.Value : evidence.AccountName);
                var sourceIp = command.Parameters.Add("@source_ip", MySqlDbType.VarBinary, 16);
                sourceIp.Value = SourceAddressIdentity.ToNetworkBytes(evidence.SourceAddress);
                command.Parameters.AddWithValue("@art_type", (uint)evidence.ArtType);
                command.Parameters.AddWithValue(
                    "@message_id",
                    evidence.MessageId is null ? DBNull.Value : evidence.MessageId);
                command.Parameters.AddWithValue("@article_size", evidence.ArticleSize);
                command.Parameters.AddWithValue("@stage", evidence.Stage.ToString());
                command.Parameters.AddWithValue("@reason", evidence.Reason);
                command.Parameters.AddWithValue(
                    "@sa_status",
                    evidence.SpamAssassinStatus is { } saStatus ? saStatus.ToString() : DBNull.Value);
                command.Parameters.AddWithValue(
                    "@sa_score",
                    evidence.SpamAssassinScore is { } score ? score : DBNull.Value);
                command.Parameters.AddWithValue(
                    "@sa_threshold",
                    evidence.SpamAssassinThreshold is { } threshold ? threshold : DBNull.Value);
                var payload = evidence.ArticlePayload;
                command.Parameters.AddWithValue(
                    "@article_payload",
                    payload is { Length: > 0 } ? payload.Value.ToArray() : DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException and not NntpDbUnavailableException)
            {
                throw new NntpDbUnavailableException("MySQL nntppostfilterrejections insert failed.", ex);
            }
        }

        /// <inheritdoc />
        public async ValueTask<AccountByteConsumeResult> ConsumeAccountBytesAsync(
            string accountName,
            long bytes,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            try
            {
                await using var transaction = await _session
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    var locked = await ReadByteQuotaAsync(
                            NntpUserQueries.SelectByteQuotaForUpdate,
                            accountName,
                            transaction,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (locked is null)
                    {
                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return new AccountByteConsumeResult(AccountByteConsumeStatus.AccountNotFound, 0, 0);
                    }

                    var current = ClampNonNegative(locked.Value.Remaining);
                    var consumed = bytes > current ? current : bytes;
                    await using (var update = _session.CreateCommand())
                    {
                        update.Transaction = transaction;
                        update.CommandText = NntpUserQueries.ConsumeAccountBytes;
                        update.Parameters.AddWithValue("@account_name", accountName);
                        update.Parameters.AddWithValue("@bytes", bytes);
                        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var after = await ReadByteQuotaAsync(
                            NntpUserQueries.SelectAccountByteRemaining,
                            accountName,
                            transaction,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    var remaining = after is { } row
                        ? ClampNonNegative(row.Remaining)
                        : 0L;
                    return new AccountByteConsumeResult(AccountByteConsumeStatus.Consumed, remaining, consumed);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException and not NntpDbUnavailableException)
            {
                throw new NntpDbUnavailableException("MySQL account byte-quota consume failed.", ex);
            }
        }

        /// <inheritdoc />
        public async ValueTask<AccountByteConsumeResult> QueryAccountByteRemainingAsync(
            string accountName,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            try
            {
                var row = await ReadByteQuotaAsync(
                        NntpUserQueries.SelectAccountByteRemaining,
                        accountName,
                        transaction: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (row is null)
                {
                    return new AccountByteConsumeResult(AccountByteConsumeStatus.AccountNotFound, 0, 0);
                }

                return new AccountByteConsumeResult(
                    AccountByteConsumeStatus.Consumed,
                    ClampNonNegative(row.Value.Remaining),
                    0);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException and not NntpDbUnavailableException)
            {
                throw new NntpDbUnavailableException("MySQL account byte-quota query failed.", ex);
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync() => _session.DisposeAsync();

        /// <summary>Maps one enabled <c>nntpmoderators</c> row. CHAR columns are trimmed.</summary>
        internal static NntpModeratorRow MapModeratorRow(MySqlDataReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);
            var id = Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
            var pattern = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
            var address = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
            var account = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
            return new NntpModeratorRow(id, pattern, address, account);
        }

        /// <summary>
        /// Maps one <c>nntpusers</c> row from <see cref="NntpUserQueries.SelectUserByName"/>.
        /// Flag columns are true only for <c>Y</c>. NULL rate/byte limits map to <c>0</c>.
        /// Ordinals: 0 pass, 1 salt, 2 iterations, 3 stored key, 4 server key, 5 plain,
        /// 6 scram256, 7 rate bps, 8 byte remaining, 9 session limit, 10 srcip limit,
        /// 11 enabled, 12 customer, 13 account_art_type.
        /// </summary>
        internal static NntpUserRecord MapUserRecord(MySqlDataReader reader, string accountName)
        {
            ArgumentNullException.ThrowIfNull(reader);
            var password = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var scramSalt = ReadBinaryColumn(reader, 1);
            var scramIterations = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
            var scramStoredKey = ReadBinaryColumn(reader, 3);
            var scramServerKey = ReadBinaryColumn(reader, 4);
            var allowAuthPlain = IsYesFlag(reader, 5);
            var allowAuthScram256 = IsYesFlag(reader, 6);
            var rateLimitBps = reader.IsDBNull(7) ? 0 : Convert.ToInt32(reader.GetValue(7));
            var byteLimit = reader.IsDBNull(8) ? 0L : ConvertByteLimit(reader.GetValue(8));
            var sessionLimit = reader.IsDBNull(9) ? 0 : Convert.ToInt32(reader.GetValue(9));
            var srcIpLimit = reader.IsDBNull(10) ? 0 : Convert.ToInt32(reader.GetValue(10));
            var isEnabled = IsYesFlag(reader, 11);
            var customerId = ReadCustomerId(reader, 12);
            var allowedArtTypes = ReadAccountArtType(reader, 13);
            return new NntpUserRecord(
                accountName,
                password,
                allowAuthPlain,
                allowAuthScram256,
                scramSalt,
                scramIterations,
                scramStoredKey,
                scramServerKey,
                rateLimitBps,
                byteLimit,
                sessionLimit,
                srcIpLimit,
                isEnabled,
                customerId,
                allowedArtTypes);
        }

        private async ValueTask<ByteQuotaRow?> ReadByteQuotaAsync(
            string commandText,
            string accountName,
            MySqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            await using var command = _session.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = commandText;
            command.Parameters.AddWithValue("@account_name", accountName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var remaining = reader.IsDBNull(0) ? 0L : ConvertByteLimit(reader.GetValue(0));
            return new ByteQuotaRow(remaining);
        }

        /// <summary>
        /// Converts a MySQL integer <c>account_byte_limit</c> to a non-negative <see cref="long"/>.
        /// Unsigned values above <see cref="long.MaxValue"/> saturate rather than wrap.
        /// Negative signed values become <c>0</c> (exhausted / never allowed).
        /// </summary>
        internal static long ConvertByteLimit(object? value) =>
            value switch
            {
                null or DBNull => 0,
                long signed64 => ClampNonNegative(signed64),
                ulong unsigned64 => unsigned64 > long.MaxValue ? long.MaxValue : (long)unsigned64,
                int signed32 => ClampNonNegative(signed32),
                uint unsigned32 => unsigned32,
                short signed16 => ClampNonNegative(signed16),
                ushort unsigned16 => unsigned16,
                sbyte signed8 => ClampNonNegative(signed8),
                byte unsigned8 => unsigned8,
                decimal dec when dec < 0 => 0,
                decimal dec when dec > long.MaxValue => long.MaxValue,
                decimal dec => (long)dec,
                _ => ClampNonNegative(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
            };

        private static long ClampNonNegative(long value) => value < 0 ? 0 : value;

        private readonly record struct ByteQuotaRow(long Remaining);

        private static bool IsYesFlag(MySqlDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return false;
            }

            return string.Equals(Convert.ToString(reader.GetValue(ordinal)), "Y", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadCustomerId(MySqlDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return string.Empty;
            }

            var value = reader.GetValue(ordinal);
            return value switch
            {
                Guid guid => guid.ToString("D"),
                string text => text,
                _ => Convert.ToString(value) ?? string.Empty,
            };
        }

        private static ArticleType ReadAccountArtType(MySqlDataReader reader, int ordinal)
        {
            if (ordinal >= reader.FieldCount)
            {
                throw new InvalidOperationException(
                    "nntpusers.account_art_type was not selected. Apply docs/schema/nntpusers-account-art-type.sql.");
            }

            if (reader.IsDBNull(ordinal))
            {
                throw new InvalidOperationException("nntpusers.account_art_type is NULL.");
            }

            return (ArticleType)Convert.ToUInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static ReadOnlyMemory<byte> ReadBinaryColumn(MySqlDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            var value = reader.GetValue(ordinal);
            return value switch
            {
                byte[] bytes when bytes.Length == 0 => ReadOnlyMemory<byte>.Empty,
                byte[] bytes => bytes,
                ReadOnlyMemory<byte> memory => memory,
                _ => ReadOnlyMemory<byte>.Empty,
            };
        }

        /// <summary>Reads one <c>nntpgroups</c> row, preserving unsigned integer width.</summary>
        internal static NntpGroupRow ReadGroupRow(MySqlDataReader reader, int index)
        {
            ArgumentNullException.ThrowIfNull(reader);
            if (reader.IsDBNull(0))
            {
                throw new Newsgroups.NewsgroupCatalogueException($"nntpgroups row {index} has a NULL group_name.");
            }

            var name = reader.GetString(0);
            var description = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var high = ConvertUnsignedCount(reader.GetValue(2), "count_high", name);
            var low = ConvertUnsignedCount(reader.GetValue(3), "count_low", name);
            var status = ConvertPostingStatus(reader.GetValue(4), name);
            return new NntpGroupRow(name, description, high, low, status);
        }

        /// <summary>
        /// Converts a MySQL unsigned/signed integer column to <see cref="ulong"/> without
        /// narrowing or floating-point conversion.
        /// </summary>
        internal static ulong ConvertUnsignedCount(object? value, string column, string groupName) =>
            value switch
            {
                ulong unsigned64 => unsigned64,
                uint unsigned32 => unsigned32,
                ushort unsigned16 => unsigned16,
                byte unsigned8 => unsigned8,
                long signed64 when signed64 >= 0 => (ulong)signed64,
                int signed32 when signed32 >= 0 => (ulong)signed32,
                short signed16 when signed16 >= 0 => (ulong)signed16,
                sbyte signed8 when signed8 >= 0 => (ulong)signed8,
                _ => throw new Newsgroups.NewsgroupCatalogueException(
                    $"nntpgroups row '{groupName}' column {column} is not a non-negative integer."),
            };

        /// <summary>
        /// Reads posting_status as a single supported octet.
        /// MySQL ENUM/CHAR typically arrives as a one-character string from MySqlConnector;
        /// byte/sbyte/char are also accepted. Values other than <c>y</c>/<c>n</c>/<c>m</c>/<c>x</c>/<c>j</c>
        /// are rejected, including RFC 6048 <c>=&lt;newsgroup&gt;</c>.
        /// </summary>
        internal static byte ConvertPostingStatus(object? value, string groupName)
        {
            var status = value switch
            {
                byte b => b,
                sbyte sb => (byte)sb,
                char ch => (byte)ch,
                string text when text.Length == 1 => (byte)text[0],
                string text when text.Length > 0 && text[0] == '=' => throw new Newsgroups.NewsgroupCatalogueException(
                    $"nntpgroups row '{groupName}' uses unsupported RFC 6048 =<newsgroup> posting_status."),
                _ => throw new Newsgroups.NewsgroupCatalogueException(
                    $"nntpgroups row '{groupName}' has an unreadable posting_status."),
            };

            if (!Newsgroups.NewsgroupPostingStatusOctets.IsSupported(status))
            {
                var reason = status == (byte)'='
                    ? $"nntpgroups row '{groupName}' uses unsupported RFC 6048 =<newsgroup> posting_status."
                    : $"nntpgroups row '{groupName}' has invalid posting_status 0x{status:X2}.";
                throw new Newsgroups.NewsgroupCatalogueException(reason);
            }

            return status;
        }

        /// <summary>
        /// Converts a MySQL <c>SELECT 1</c> scalar, including the connector's <see cref="long"/> result type.
        /// </summary>
        internal static int ConvertSelectOneScalar(object? result) =>
            MySqlNntpDbSession.ConvertSelectOneScalar(result);

        private static PostFilterPolicyRecord MapPolicyScalars(MySqlDataReader reader)
        {
            var revision = Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
            var updated = reader.GetDateTime(1);
            var updatedUtc = updated.Kind == DateTimeKind.Unspecified
                ? new DateTimeOffset(DateTime.SpecifyKind(updated, DateTimeKind.Utc))
                : new DateTimeOffset(updated.ToUniversalTime());
            var gateText = reader.GetString(2).Trim();
            if (!Enum.TryParse<PostFilterGateState>(gateText, ignoreCase: true, out var gate)
                || !Enum.IsDefined(gate))
            {
                throw new InvalidOperationException($"PostFilter gate is not a defined value: '{gateText}'.");
            }

            var options = new PostFilterOptions
            {
                Gate = gate,
                Quota = new PostFilterQuotaOptions
                {
                    LongWindow = TimeSpan.FromMilliseconds(ReadInt64(reader, 3)),
                    ShortWindow = TimeSpan.FromMilliseconds(ReadInt64(reader, 4)),
                    MaxMessagesLong = ReadInt64(reader, 5),
                    MaxBytesLong = ReadInt64(reader, 6),
                    MaxIdenticalLong = ReadInt64(reader, 7),
                    MaxMessagesShort = ReadInt64(reader, 8),
                    MaxBytesShort = ReadInt64(reader, 9),
                    MaxIdenticalShort = ReadInt64(reader, 10),
                },
                SpamAssassin = new PostFilterSpamAssassinOptions
                {
                    Enabled = IsYes(reader, 11),
                    OnFailure = ReadOnFailure(reader, 12),
                    MaxArticleSize = checked((int)ReadInt64(reader, 13)),
                    Port = checked((int)ReadInt64(reader, 14)),
                    ProtocolVersion = reader.IsDBNull(15) ? string.Empty : reader.GetString(15).Trim(),
                    MaxConnections = checked((int)ReadInt64(reader, 16)),
                    HostSelection = ReadHostSelection(reader, 17),
                    ConnectTimeout = TimeSpan.FromMilliseconds(ReadInt64(reader, 18)),
                    OperationTimeout = TimeSpan.FromMilliseconds(ReadInt64(reader, 19)),
                },
            };

            return new PostFilterPolicyRecord(revision, updatedUtc, options);
        }

        private async Task<long?> ReadCurrentRevisionAsync(
            MySqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            await using var command = _session.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = NntpPostFilterQueries.SelectCurrentRevision;
            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return scalar is null or DBNull
                ? null
                : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }

        private MySqlCommand CreateRevisionCommand(
            string commandText,
            long revision,
            MySqlTransaction transaction)
        {
            var command = _session.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = commandText;
            command.Parameters.AddWithValue("@revision", revision);
            return command;
        }

        private async Task ReadKeyedStringsAsync(
            string commandText,
            long revision,
            MySqlTransaction transaction,
            string firstKind,
            string secondKind,
            List<string> first,
            List<string> second,
            CancellationToken cancellationToken)
        {
            await using var command = CreateRevisionCommand(commandText, revision, transaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var kind = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                var value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (string.Equals(kind, firstKind, StringComparison.OrdinalIgnoreCase))
                {
                    first.Add(value);
                }
                else if (string.Equals(kind, secondKind, StringComparison.OrdinalIgnoreCase))
                {
                    second.Add(value);
                }
                else
                {
                    throw new InvalidOperationException($"PostFilter collection list_kind is unknown: '{kind}'.");
                }
            }
        }

        private async Task<List<string>> ReadHostsAsync(
            long revision,
            MySqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            await using var command = CreateRevisionCommand(
                NntpPostFilterQueries.SelectHosts,
                revision,
                transaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var hosts = new List<string>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hosts.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
            }

            return hosts;
        }

        private static long ReadInt64(MySqlDataReader reader, int ordinal) =>
            Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

        private static bool IsYes(MySqlDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return false;
            }

            var value = reader.GetValue(ordinal);
            return value switch
            {
                string text => text.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase),
                char ch => ch is 'Y' or 'y',
                byte b => b is (byte)'Y' or (byte)'y',
                _ => false,
            };
        }

        private static PostFilterSpamOnFailure? ReadOnFailure(MySqlDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return null;
            }

            var text = reader.GetString(ordinal).Trim();
            if (text.Length == 0)
            {
                return null;
            }

            if (!Enum.TryParse<PostFilterSpamOnFailure>(text, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                throw new InvalidOperationException($"PostFilter SpamAssassin OnFailure is not a defined value: '{text}'.");
            }

            return parsed;
        }

        private static PostFilterSpamAssassinHostSelection ReadHostSelection(MySqlDataReader reader, int ordinal)
        {
            var text = reader.IsDBNull(ordinal)
                ? PostFilterSpamAssassinHostSelection.RoundRobin.ToString()
                : reader.GetString(ordinal).Trim();
            if (!Enum.TryParse<PostFilterSpamAssassinHostSelection>(text, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                throw new InvalidOperationException($"PostFilter SpamAssassin HostSelection is not a defined value: '{text}'.");
            }

            return parsed;
        }

        /// <inheritdoc />
        public async ValueTask<TransitCatalogueRecord?> QueryTransitCatalogueAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await NntpTransitCatalogueLoader.LoadAsync(_session, cancellationToken).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException
                                       and not NntpDbUnavailableException
                                       and not InvalidOperationException)
            {
                throw new NntpDbUnavailableException("MySQL Transit catalogue query failed.", ex);
            }
        }
    }
}
