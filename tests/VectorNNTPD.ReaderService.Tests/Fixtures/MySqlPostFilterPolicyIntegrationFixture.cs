using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.PostFilter;

namespace VectorNNTPD.ReaderService.Tests.Fixtures;

/// <summary>
/// Applies the official PostFilter DDL when missing and isolates test revisions
/// on a live MySQL target. Unconfigured runs do not open a connection.
/// </summary>
public sealed class MySqlPostFilterPolicyIntegrationFixture : IAsyncLifetime
{
    private NntpDbService? _nntpDb;
    private long? _savedCurrentRevision;
    private readonly List<long> _revisions = [];

    public bool IsConfigured { get; private set; }

    public string? SkipReason { get; private set; }

    public long RevisionBase { get; private set; } =
        9_000_000_000L + (Environment.TickCount64 % 1_000_000L) * 100L;

    public MySqlPostFilterPolicyRepository? Repository { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = NntpDbIntegration.TryGetConnectionString();
        if (connectionString is null)
        {
            SkipReason = NntpDbIntegration.SkipReason;
            return;
        }

        try
        {
            await using var connection = MySqlNntpDbConnectionFactory.CreateConnection(connectionString);
            await connection.OpenAsync();
            if (await HasColumnAsync(connection, "nntppostfilterpolicy", "policy_id")
                && !await HasTableAsync(connection, "nntppostfiltercurrent"))
            {
                SkipReason =
                    "NntpDB still has the pre-revision PostFilter singleton. Apply docs/schema/postfilter.sql.";
                return;
            }

            if (!await HasTableAsync(connection, "nntppostfiltercurrent"))
            {
                foreach (var statement in PostFilterSchemaScript.ReadStatements())
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync();
                }
            }
            else
            {
                try
                {
                    await EnsureExtendedSchemaAsync(connection);
                }
                catch (MySqlException ex)
                {
                    SkipReason = FormatPrepareFailure(ex);
                    return;
                }
            }

            _savedCurrentRevision = await ReadCurrentRevisionAsync(connection);
            if (_savedCurrentRevision is { } saved && saved >= RevisionBase)
            {
                RevisionBase = saved + 1_000;
            }

            IsConfigured = true;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            SkipReason = FormatPrepareFailure(ex);
            return;
        }

        var options = Options.Create(NntpDbIntegration.CreateOptions(connectionString));
        _nntpDb = new NntpDbService(
            new MySqlNntpDbConnectionFactory(),
            options,
            NullLogger<NntpDbService>.Instance);
        await _nntpDb.StartAsync(CancellationToken.None);
        Repository = new MySqlPostFilterPolicyRepository(
            _nntpDb,
            NullLogger<MySqlPostFilterPolicyRepository>.Instance);
    }

    public async Task DisposeAsync()
    {
        var connectionString = NntpDbIntegration.TryGetConnectionString();
        if (connectionString is not null && _revisions.Count > 0)
        {
            await using var connection = MySqlNntpDbConnectionFactory.CreateConnection(connectionString);
            await connection.OpenAsync();
            if (_savedCurrentRevision is { } saved)
            {
                await using var restore = connection.CreateCommand();
                restore.CommandText =
                    "UPDATE nntppostfiltercurrent SET revision = @revision WHERE policy_id = 1";
                restore.Parameters.AddWithValue("@revision", saved);
                try
                {
                    await restore.ExecuteNonQueryAsync();
                }
                catch (MySqlException)
                {
                    // Restore can fail if the trigger forbids regression; drop current then reinsert.
                    await using var deleteCurrent = connection.CreateCommand();
                    deleteCurrent.CommandText = "DELETE FROM nntppostfiltercurrent WHERE policy_id = 1";
                    await deleteCurrent.ExecuteNonQueryAsync();
                    await using var insertCurrent = connection.CreateCommand();
                    insertCurrent.CommandText =
                        "INSERT INTO nntppostfiltercurrent (policy_id, revision) VALUES (1, @revision)";
                    insertCurrent.Parameters.AddWithValue("@revision", saved);
                    await insertCurrent.ExecuteNonQueryAsync();
                }
            }

            foreach (var revision in _revisions.OrderByDescending(static value => value))
            {
                await DeleteRevisionAsync(connection, revision);
            }
        }

        if (_nntpDb is not null)
        {
            await _nntpDb.DisposeAsync().ConfigureAwait(false);
        }
    }

    public long NextRevision()
    {
        var revision = RevisionBase + _revisions.Count + 1;
        _revisions.Add(revision);
        return revision;
    }

    /// <summary>
    /// Allocates a revision that remains after dispose so a live validation
    /// publish can stay current. Must be higher than <see cref="RevisionBase"/>.
    /// </summary>
    public long NextPersistentRevision() =>
        RevisionBase + 90_000 + (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000);

    /// <summary>
    /// Remembers a published revision so dispose restores the pointer here
    /// instead of the pre-test value.
    /// </summary>
    public void RememberPublishedRevision(long revision) => _savedCurrentRevision = revision;

    public async Task InsertDisabledRevisionAsync(long revision, Action<MySqlCommand>? customize = null)
    {
        var connectionString = RequireConnectionString();
        await using var connection = MySqlNntpDbConnectionFactory.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO nntppostfilterpolicy (
              revision, updated_utc, gate,
              long_window_ms, short_window_ms,
              max_messages_long, max_bytes_long, max_identical_long,
              max_messages_short, max_bytes_short, max_identical_short,
              sa_enabled, sa_on_failure, sa_max_article_size, sa_port,
              sa_protocol_version, sa_max_connections, sa_host_selection,
              sa_connect_timeout_ms, sa_operation_timeout_ms
            ) VALUES (
              @revision, UTC_TIMESTAMP(3), 'Disabled',
              86400000, 600000,
              0, 0, 0, 0, 0, 0,
              'N', NULL, 131072, 783,
              '1.5', 4, 'RoundRobin',
              5000, 30000
            )
            """;
        command.Parameters.AddWithValue("@revision", revision);
        customize?.Invoke(command);
        await command.ExecuteNonQueryAsync();
    }

    public async Task InsertArtTypeAsync(long revision, string listKind, string artType)
    {
        await ExecuteAsync(
            "INSERT INTO nntppostfilterarttypes (revision, list_kind, art_type) VALUES (@revision, @kind, @value)",
            command =>
            {
                command.Parameters.AddWithValue("@revision", revision);
                command.Parameters.AddWithValue("@kind", listKind);
                command.Parameters.AddWithValue("@value", artType);
            });
    }

    public async Task InsertAccountAsync(long revision, string listKind, string account)
    {
        await ExecuteAsync(
            "INSERT INTO nntppostfilteraccounts (revision, list_kind, account_name) VALUES (@revision, @kind, @value)",
            command =>
            {
                command.Parameters.AddWithValue("@revision", revision);
                command.Parameters.AddWithValue("@kind", listKind);
                command.Parameters.AddWithValue(
                    "@value",
                    PostFilterAccountIdentity.FromPolicyEntry(account));
            });
    }

    public async Task InsertCidrAsync(long revision, string listKind, string cidr)
    {
        await ExecuteAsync(
            "INSERT INTO nntppostfiltercidrs (revision, list_kind, cidr) VALUES (@revision, @kind, @value)",
            command =>
            {
                command.Parameters.AddWithValue("@revision", revision);
                command.Parameters.AddWithValue("@kind", listKind);
                command.Parameters.AddWithValue("@value", cidr);
            });
    }

    public async Task InsertHostAsync(long revision, int order, string host)
    {
        await ExecuteAsync(
            "INSERT INTO nntppostfiltersahosts (revision, host_order, host) VALUES (@revision, @order, @host)",
            command =>
            {
                command.Parameters.AddWithValue("@revision", revision);
                command.Parameters.AddWithValue("@order", order);
                command.Parameters.AddWithValue("@host", host);
            });
    }

    public async Task PublishOptionsAsync(long revision, PostFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Quota);
        ArgumentNullException.ThrowIfNull(options.SpamAssassin);
        await InsertPolicyScalarsAsync(revision, options);
        foreach (var account in options.DeniedAccounts)
        {
            await InsertAccountAsync(revision, NntpPostFilterQueries.ListKindDeny, account);
        }

        foreach (var account in options.AllowlistedAccounts)
        {
            await InsertAccountAsync(revision, NntpPostFilterQueries.ListKindAllow, account);
        }

        foreach (var cidr in options.DeniedCidrs)
        {
            await InsertCidrAsync(revision, NntpPostFilterQueries.ListKindDeny, cidr);
        }

        foreach (var cidr in options.AllowlistedCidrs)
        {
            await InsertCidrAsync(revision, NntpPostFilterQueries.ListKindAllow, cidr);
        }

        foreach (var artType in options.RejectArtTypes)
        {
            await InsertArtTypeAsync(revision, NntpPostFilterQueries.ListKindReject, artType);
        }

        foreach (var artType in options.SpamAssassin.ExcludeArtTypes)
        {
            await InsertArtTypeAsync(revision, NntpPostFilterQueries.ListKindSaExclude, artType);
        }

        for (var i = 0; i < options.SpamAssassin.Hosts.Length; i++)
        {
            await InsertHostAsync(revision, i + 1, options.SpamAssassin.Hosts[i]);
        }

        await PublishAsync(revision);
    }

    public async Task InsertPolicyScalarsAsync(long revision, PostFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Quota);
        ArgumentNullException.ThrowIfNull(options.SpamAssassin);
        var connectionString = RequireConnectionString();
        await using var connection = MySqlNntpDbConnectionFactory.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO nntppostfilterpolicy (
              revision, updated_utc, gate,
              long_window_ms, short_window_ms,
              max_messages_long, max_bytes_long, max_identical_long,
              max_messages_short, max_bytes_short, max_identical_short,
              sa_enabled, sa_on_failure, sa_max_article_size, sa_port,
              sa_protocol_version, sa_max_connections, sa_host_selection,
              sa_connect_timeout_ms, sa_operation_timeout_ms
            ) VALUES (
              @revision, UTC_TIMESTAMP(3), @gate,
              @long_window_ms, @short_window_ms,
              @max_messages_long, @max_bytes_long, @max_identical_long,
              @max_messages_short, @max_bytes_short, @max_identical_short,
              @sa_enabled, @sa_on_failure, @sa_max_article_size, @sa_port,
              @sa_protocol_version, @sa_max_connections, @sa_host_selection,
              @sa_connect_timeout_ms, @sa_operation_timeout_ms
            )
            """;
        command.Parameters.AddWithValue("@revision", revision);
        command.Parameters.AddWithValue("@gate", options.Gate.ToString());
        command.Parameters.AddWithValue(
            "@long_window_ms",
            checked((long)options.Quota.LongWindow.TotalMilliseconds));
        command.Parameters.AddWithValue(
            "@short_window_ms",
            checked((long)options.Quota.ShortWindow.TotalMilliseconds));
        command.Parameters.AddWithValue("@max_messages_long", options.Quota.MaxMessagesLong);
        command.Parameters.AddWithValue("@max_bytes_long", options.Quota.MaxBytesLong);
        command.Parameters.AddWithValue("@max_identical_long", options.Quota.MaxIdenticalLong);
        command.Parameters.AddWithValue("@max_messages_short", options.Quota.MaxMessagesShort);
        command.Parameters.AddWithValue("@max_bytes_short", options.Quota.MaxBytesShort);
        command.Parameters.AddWithValue("@max_identical_short", options.Quota.MaxIdenticalShort);
        command.Parameters.AddWithValue("@sa_enabled", options.SpamAssassin.Enabled ? "Y" : "N");
        command.Parameters.AddWithValue(
            "@sa_on_failure",
            options.SpamAssassin.OnFailure is { } onFailure
                ? onFailure.ToString()
                : DBNull.Value);
        command.Parameters.AddWithValue("@sa_max_article_size", options.SpamAssassin.MaxArticleSize);
        command.Parameters.AddWithValue("@sa_port", options.SpamAssassin.Port);
        command.Parameters.AddWithValue("@sa_protocol_version", options.SpamAssassin.ProtocolVersion);
        command.Parameters.AddWithValue("@sa_max_connections", options.SpamAssassin.MaxConnections);
        command.Parameters.AddWithValue("@sa_host_selection", options.SpamAssassin.HostSelection.ToString());
        command.Parameters.AddWithValue(
            "@sa_connect_timeout_ms",
            checked((int)options.SpamAssassin.ConnectTimeout.TotalMilliseconds));
        command.Parameters.AddWithValue(
            "@sa_operation_timeout_ms",
            checked((int)options.SpamAssassin.OperationTimeout.TotalMilliseconds));
        await command.ExecuteNonQueryAsync();
    }

    public async Task PublishAsync(long revision)
    {
        await ExecuteAsync(
            "UPDATE nntppostfiltercurrent SET revision = @revision WHERE policy_id = 1",
            command => command.Parameters.AddWithValue("@revision", revision));
    }

    public async Task<PostFilterPolicyRecord> LoadAsync()
    {
        if (Repository is null)
        {
            throw new InvalidOperationException("PostFilter MySQL fixture is not configured.");
        }

        return await Repository.LoadAsync();
    }

    public async Task ExecuteRawAsync(string sql, Action<MySqlCommand>? bind = null)
    {
        await ExecuteAsync(sql, bind);
    }

    public async Task<object?> ExecuteRawScalarAsync(string sql, Action<MySqlCommand>? bind = null)
    {
        var connectionString = RequireConnectionString();
        await using var connection = MySqlNntpDbConnectionFactory.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);
        return await command.ExecuteScalarAsync();
    }

    public async Task<string> ShowCreateTableAsync(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        var connectionString = RequireConnectionString();
        await using var connection = MySqlNntpDbConnectionFactory.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW CREATE TABLE `" + table.Replace("`", string.Empty, StringComparison.Ordinal) + "`";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("SHOW CREATE TABLE returned no rows.");
        }

        return reader.GetString(1);
    }

    public async Task InsertRejectionAsync(PostFilterRejectionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var connectionString = RequireConnectionString();
        await using var connection = MySqlNntpDbConnectionFactory.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var db = new MySqlNntpDbConnection(connection);
        await db.InsertPostFilterRejectionAsync(evidence, CancellationToken.None);
    }

    public async Task<HeldPostFilterWrite> BeginHeldWriteAsync()
    {
        var connection = MySqlNntpDbConnectionFactory.CreateConnection(RequireConnectionString());
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        return new HeldPostFilterWrite(connection, transaction);
    }

    private async Task ExecuteAsync(string sql, Action<MySqlCommand>? bind)
    {
        var connectionString = RequireConnectionString();
        await using var connection = MySqlNntpDbConnectionFactory.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);
        await command.ExecuteNonQueryAsync();
    }

    private string RequireConnectionString() =>
        NntpDbIntegration.TryGetConnectionString()
        ?? throw new InvalidOperationException(NntpDbIntegration.SkipReason);

    private static string FormatPrepareFailure(Exception ex)
    {
        if (ex is MySqlException mysql)
        {
            return "PostFilter MySQL schema could not be prepared: MySqlException "
                + mysql.Number.ToString(CultureInfo.InvariantCulture)
                + " "
                + (mysql.SqlState ?? "-")
                + " "
                + mysql.Message;
        }

        return "PostFilter MySQL schema could not be prepared: "
            + ex.GetType().Name
            + " "
            + ex.Message;
    }

    private static async Task<bool> HasTableAsync(MySqlConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM information_schema.tables "
            + "WHERE table_schema = DATABASE() AND table_name = @name";
        command.Parameters.AddWithValue("@name", table);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        return count > 0;
    }

    private static async Task EnsureExtendedSchemaAsync(MySqlConnection connection)
    {
        if (!await HasTableAsync(connection, "nntppostfilterrejections"))
        {
            var create = PostFilterSchemaScript.ReadStatements()
                .Single(statement => statement.StartsWith(
                    "CREATE TABLE nntppostfilterrejections",
                    StringComparison.Ordinal));
            await using var command = connection.CreateCommand();
            command.CommandText = create;
            await command.ExecuteNonQueryAsync();
        }

        if (await HasTableAsync(connection, "nntppostfilteraccounts")
            && await ColumnTypeAsync(connection, "nntppostfilteraccounts", "account_name") is { } accountType
            && !accountType.Contains("char(32)", StringComparison.OrdinalIgnoreCase))
        {
            await using var hash = connection.CreateCommand();
            hash.CommandText =
                """
                UPDATE nntppostfilteraccounts
                SET account_name = LOWER(MD5(account_name))
                WHERE CHAR_LENGTH(account_name) <> 32
                """;
            await hash.ExecuteNonQueryAsync();
            await using var alter = connection.CreateCommand();
            alter.CommandText =
                "ALTER TABLE nntppostfilteraccounts MODIFY account_name CHAR(32) NOT NULL";
            await alter.ExecuteNonQueryAsync();
            try
            {
                await using var check = connection.CreateCommand();
                check.CommandText =
                    "ALTER TABLE nntppostfilteraccounts ADD CONSTRAINT chk_nntppostfilteraccounts_name "
                    + "CHECK (account_name REGEXP '^[0-9a-f]{32}$')";
                await check.ExecuteNonQueryAsync();
            }
            catch (MySqlException ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
                // Constraint may already exist on a partially migrated schema.
            }
        }

        if (await HasTableAsync(connection, "nntppostfilterarttypes")
            && await ColumnTypeAsync(connection, "nntppostfilterarttypes", "art_type") is { } artType
            && !artType.Contains("enum", StringComparison.OrdinalIgnoreCase))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText =
                """
                ALTER TABLE nntppostfilterarttypes
                MODIFY art_type ENUM(
                  'Default','Control','Cancel','Mime','Binary','UuEncode','Base64','YEncoded',
                  'BommaNews','UniData','Multipart','Html','PostScript','BinHex','Partial','PgpMessage'
                ) NOT NULL
                """;
            await alter.ExecuteNonQueryAsync();
        }

        if (await HasTableAsync(connection, "nntppostfilterrejections")
            && await ColumnTypeAsync(connection, "nntppostfilterrejections", "source_ip") is { } sourceIpType
            && !sourceIpType.Contains("varbinary", StringComparison.OrdinalIgnoreCase))
        {
            await using var convert = connection.CreateCommand();
            convert.CommandText =
                """
                ALTER TABLE nntppostfilterrejections
                  ADD COLUMN source_ip_bin VARBINARY(16) NULL
                """;
            await convert.ExecuteNonQueryAsync();
            await using var copy = connection.CreateCommand();
            copy.CommandText =
                """
                UPDATE nntppostfilterrejections
                SET source_ip_bin = INET6_ATON(CAST(source_ip AS CHAR))
                """;
            await copy.ExecuteNonQueryAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "ALTER TABLE nntppostfilterrejections DROP COLUMN source_ip";
            await drop.ExecuteNonQueryAsync();
            await using var rename = connection.CreateCommand();
            rename.CommandText =
                """
                ALTER TABLE nntppostfilterrejections
                  CHANGE source_ip_bin source_ip VARBINARY(16) NOT NULL
                """;
            await rename.ExecuteNonQueryAsync();
        }

        if (await HasTableAsync(connection, "nntpusers")
            && !await HasColumnAsync(connection, "nntpusers", "account_art_type"))
        {
            try
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = NntpUserQueries.AddAccountArtTypeColumn;
                await alter.ExecuteNonQueryAsync();
            }
            catch (MySqlException ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
                // Account capability is additive on nntpusers. PostFilter
                // schema tests must still run when this ALTER is denied.
            }
        }
    }

    private static async Task<string?> ColumnTypeAsync(MySqlConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COLUMN_TYPE FROM information_schema.columns "
            + "WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<bool> HasColumnAsync(MySqlConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM information_schema.columns "
            + "WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        return count > 0;
    }

    private static async Task<long?> ReadCurrentRevisionAsync(MySqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision FROM nntppostfiltercurrent WHERE policy_id = 1";
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull
            ? null
            : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task DeleteRevisionAsync(MySqlConnection connection, long revision)
    {
        foreach (var table in new[]
                 {
                     "nntppostfilteraccounts",
                     "nntppostfiltercidrs",
                     "nntppostfilterarttypes",
                     "nntppostfiltersahosts",
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM " + table + " WHERE revision = @revision";
            command.Parameters.AddWithValue("@revision", revision);
            await command.ExecuteNonQueryAsync();
        }

        await using var policy = connection.CreateCommand();
        policy.CommandText = "DELETE FROM nntppostfilterpolicy WHERE revision = @revision";
        policy.Parameters.AddWithValue("@revision", revision);
        await policy.ExecuteNonQueryAsync();
    }
}

/// <summary>Open writer transaction used to prove uncommitted policy is invisible.</summary>
public sealed class HeldPostFilterWrite : IAsyncDisposable
{
    public HeldPostFilterWrite(MySqlConnection connection, MySqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        Connection = connection;
        Transaction = transaction;
    }

    public MySqlConnection Connection { get; }

    public MySqlTransaction Transaction { get; }

    public async Task ExecuteAsync(string sql, Action<MySqlCommand>? bind = null)
    {
        await using var command = Connection.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = sql;
        bind?.Invoke(command);
        await command.ExecuteNonQueryAsync();
    }

    public Task CommitAsync() => Transaction.CommitAsync();

    public Task RollbackAsync() => Transaction.RollbackAsync();

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

/// <summary>Serializes live PostFilter schema tests against one MySQL target.</summary>
[CollectionDefinition("NntpDbPostFilter")]
public sealed class NntpDbPostFilterCollection : ICollectionFixture<MySqlPostFilterPolicyIntegrationFixture>
{
}
