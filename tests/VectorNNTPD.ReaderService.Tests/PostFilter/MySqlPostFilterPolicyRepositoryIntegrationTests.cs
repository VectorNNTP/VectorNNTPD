using System.Globalization;
using MySqlConnector;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

[Collection("NntpDbPostFilter")]
public sealed class MySqlPostFilterPolicyRepositoryIntegrationTests
{
    private readonly MySqlPostFilterPolicyIntegrationFixture _fixture;

    public MySqlPostFilterPolicyRepositoryIntegrationTests(MySqlPostFilterPolicyIntegrationFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    [Fact]
    public void OfficialSchemaScript_ContainsRevisionKeyedTables()
    {
        var path = PostFilterSchemaScript.FindPath();
        Assert.EndsWith(Path.Join("docs", "schema", "postfilter.sql"), path, StringComparison.OrdinalIgnoreCase);
        var sql = File.ReadAllText(path);
        Assert.Contains("CREATE TABLE nntppostfilterpolicy", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE nntppostfiltercurrent", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE nntppostfilteraccounts", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE nntppostfiltercidrs", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE nntppostfilterarttypes", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE nntppostfiltersahosts", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE nntppostfilterrejections", sql, StringComparison.Ordinal);
        Assert.Contains("source_ip VARBINARY(16) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("account_name CHAR(32) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("art_type ENUM(", sql, StringComparison.Ordinal);
        Assert.Contains("'YEncoded'", sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (revision, list_kind, account_name)", sql, StringComparison.Ordinal);
        Assert.Contains("trg_nntppostfiltercurrent_revision_forward", sql, StringComparison.Ordinal);
        Assert.Contains("'Disabled'", sql, StringComparison.Ordinal);
        Assert.Contains("86400000, 600000", sql, StringComparison.Ordinal);
        Assert.Contains("'N', NULL, 131072, 783", sql, StringComparison.Ordinal);
        Assert.Contains("(1, 'sa_exclude', 'YEncoded')", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO nntppostfiltercurrent (policy_id, revision) VALUES (1, 1)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO nntppostfiltersahosts", sql, StringComparison.Ordinal);
        Assert.Contains("DELIMITER $$", sql, StringComparison.Ordinal);
        Assert.Contains("END$$", sql, StringComparison.Ordinal);
        Assert.Contains("DELIMITER ;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("VECTORNNTP_STMT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(";;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE nntpusers", sql, StringComparison.Ordinal);
        Assert.Contains("nntpusers-account-art-type.sql", sql, StringComparison.Ordinal);
        var statements = PostFilterSchemaScript.ReadStatements();
        Assert.Equal(11, statements.Count);
        var trigger = Assert.Single(
            statements,
            statement => statement.StartsWith("CREATE TRIGGER trg_nntppostfiltercurrent_revision_forward", StringComparison.Ordinal));
        Assert.DoesNotContain("DELIMITER", trigger, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$$", trigger, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE ON nntppostfiltercurrent", trigger, StringComparison.Ordinal);
        Assert.Contains("NEW.revision <= OLD.revision", trigger, StringComparison.Ordinal);
        Assert.Contains("SIGNAL SQLSTATE '45000'", trigger, StringComparison.Ordinal);
    }

    [NntpDbIntegrationFact]
    public async Task OfficialSeedRevision1_LoadsExpectedScalars()
    {
        RequireReady();
        await _fixture.ExecuteRawAsync("DELETE FROM nntppostfiltercurrent WHERE policy_id = 1");
        await _fixture.ExecuteRawAsync("INSERT INTO nntppostfiltercurrent (policy_id, revision) VALUES (1, 1)");
        var record = await _fixture.LoadAsync();
        Assert.Equal(1, record.Revision);
        Assert.Equal(PostFilterGateState.Disabled, record.Options.Gate);
        Assert.Equal(TimeSpan.FromDays(1), record.Options.Quota.LongWindow);
        Assert.Equal(TimeSpan.FromMinutes(10), record.Options.Quota.ShortWindow);
        Assert.Equal(0, record.Options.Quota.MaxMessagesLong);
        Assert.Equal(0, record.Options.Quota.MaxBytesLong);
        Assert.Equal(0, record.Options.Quota.MaxIdenticalLong);
        Assert.Equal(0, record.Options.Quota.MaxMessagesShort);
        Assert.Equal(0, record.Options.Quota.MaxBytesShort);
        Assert.Equal(0, record.Options.Quota.MaxIdenticalShort);
        Assert.False(record.Options.SpamAssassin.Enabled);
        Assert.Null(record.Options.SpamAssassin.OnFailure);
        Assert.Equal(131072, record.Options.SpamAssassin.MaxArticleSize);
        Assert.Equal(783, record.Options.SpamAssassin.Port);
        Assert.Equal("1.5", record.Options.SpamAssassin.ProtocolVersion);
        Assert.Equal(4, record.Options.SpamAssassin.MaxConnections);
        Assert.Equal(PostFilterSpamAssassinHostSelection.RoundRobin, record.Options.SpamAssassin.HostSelection);
        Assert.Equal(TimeSpan.FromMilliseconds(5000), record.Options.SpamAssassin.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(30000), record.Options.SpamAssassin.OperationTimeout);
        Assert.Equal(["YEncoded"], record.Options.SpamAssassin.ExcludeArtTypes);
        Assert.Empty(record.Options.SpamAssassin.Hosts);
        Assert.Empty(record.Options.DeniedAccounts);
        Assert.Empty(record.Options.AllowlistedAccounts);
        Assert.Empty(record.Options.DeniedCidrs);
        Assert.Empty(record.Options.AllowlistedCidrs);
        Assert.Empty(record.Options.RejectArtTypes);
        var snapshot = PostFilterPolicyCompiler.Compile(record.Options, record.Revision);
        Assert.Equal(1, snapshot.Revision);
        Assert.Equal(PostFilterGateState.Disabled, snapshot.Gate);
    }

    [NntpDbIntegrationFact]
    public async Task UnpublishedCommittedRevision_IsNeverSelected()
    {
        RequireReady();
        var published = _fixture.NextRevision();
        var draft = _fixture.NextRevision();
        await _fixture.InsertDisabledRevisionAsync(published);
        await _fixture.InsertArtTypeAsync(published, NntpPostFilterQueries.ListKindSaExclude, "YEncoded");
        await _fixture.PublishAsync(published);
        await _fixture.InsertDisabledRevisionAsync(draft);
        await _fixture.InsertAccountAsync(draft, NntpPostFilterQueries.ListKindDeny, "unpublished-poster");
        await _fixture.InsertArtTypeAsync(draft, NntpPostFilterQueries.ListKindSaExclude, "YEncoded");

        var record = await _fixture.LoadAsync();
        Assert.Equal(published, record.Revision);
        Assert.DoesNotContain("unpublished-poster", record.Options.DeniedAccounts);
    }

    [NntpDbIntegrationFact]
    public async Task FreshDisabledSeed_LoadsCompletePolicy()
    {
        RequireReady();
        var revision = _fixture.NextRevision();
        await _fixture.InsertDisabledRevisionAsync(revision);
        await _fixture.InsertArtTypeAsync(revision, NntpPostFilterQueries.ListKindSaExclude, "YEncoded");
        await _fixture.PublishAsync(revision);

        var record = await _fixture.LoadAsync();
        Assert.Equal(revision, record.Revision);
        Assert.Equal(PostFilterGateState.Disabled, record.Options.Gate);
        Assert.Equal(TimeSpan.FromDays(1), record.Options.Quota.LongWindow);
        Assert.Equal(TimeSpan.FromMinutes(10), record.Options.Quota.ShortWindow);
        Assert.Equal(0, record.Options.Quota.MaxMessagesLong);
        Assert.Equal(0, record.Options.Quota.MaxMessagesShort);
        Assert.False(record.Options.SpamAssassin.Enabled);
        Assert.Null(record.Options.SpamAssassin.OnFailure);
        Assert.Equal(131072, record.Options.SpamAssassin.MaxArticleSize);
        Assert.Equal(783, record.Options.SpamAssassin.Port);
        Assert.Equal("1.5", record.Options.SpamAssassin.ProtocolVersion);
        Assert.Equal(4, record.Options.SpamAssassin.MaxConnections);
        Assert.Equal(PostFilterSpamAssassinHostSelection.RoundRobin, record.Options.SpamAssassin.HostSelection);
        Assert.Equal(["YEncoded"], record.Options.SpamAssassin.ExcludeArtTypes);
        Assert.Empty(record.Options.SpamAssassin.Hosts);
        var snapshot = PostFilterPolicyCompiler.Compile(record.Options, record.Revision);
        Assert.Equal(PostFilterGateState.Disabled, snapshot.Gate);
        Assert.Equal(revision, snapshot.Revision);
    }

    [NntpDbIntegrationFact]
    public async Task MissingCurrent_FailsWithoutInventingPolicy()
    {
        RequireReady();
        var revision = _fixture.NextRevision();
        await _fixture.InsertDisabledRevisionAsync(revision);
        await _fixture.PublishAsync(revision);
        await _fixture.ExecuteRawAsync("DELETE FROM nntppostfiltercurrent WHERE policy_id = 1");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(_fixture.LoadAsync);
            Assert.Contains("nntppostfiltercurrent", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await _fixture.ExecuteRawAsync(
                "INSERT INTO nntppostfiltercurrent (policy_id, revision) VALUES (1, @revision)",
                command => command.Parameters.AddWithValue("@revision", revision));
        }
    }

    [NntpDbIntegrationFact]
    public async Task InvalidRevision_IsRejectedByDatabase()
    {
        RequireReady();
        var ex = await Assert.ThrowsAsync<MySqlException>(() =>
            _fixture.ExecuteRawAsync(
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
                  0, UTC_TIMESTAMP(3), 'Disabled',
                  86400000, 600000,
                  0, 0, 0, 0, 0, 0,
                  'N', NULL, 131072, 783,
                  '1.5', 4, 'RoundRobin',
                  5000, 30000
                )
                """));
        Assert.Contains("chk_nntppostfilterpolicy_revision", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [NntpDbIntegrationFact]
    public async Task DuplicateCollectionRows_AreRejectedByDatabase()
    {
        RequireReady();
        var revision = _fixture.NextRevision();
        await _fixture.InsertDisabledRevisionAsync(revision);
        await _fixture.InsertAccountAsync(revision, NntpPostFilterQueries.ListKindDeny, "poster");
        await Assert.ThrowsAsync<MySqlException>(() =>
            _fixture.InsertAccountAsync(revision, NntpPostFilterQueries.ListKindDeny, "poster"));
        await _fixture.InsertCidrAsync(revision, NntpPostFilterQueries.ListKindAllow, "10.0.0.0/8");
        await Assert.ThrowsAsync<MySqlException>(() =>
            _fixture.InsertCidrAsync(revision, NntpPostFilterQueries.ListKindAllow, "10.0.0.0/8"));
        await _fixture.InsertArtTypeAsync(revision, NntpPostFilterQueries.ListKindReject, "Binary");
        await Assert.ThrowsAsync<MySqlException>(() =>
            _fixture.InsertArtTypeAsync(revision, NntpPostFilterQueries.ListKindReject, "Binary"));
        await _fixture.InsertHostAsync(revision, 1, "127.0.0.1");
        await Assert.ThrowsAsync<MySqlException>(() =>
            _fixture.InsertHostAsync(revision, 1, "10.0.0.1"));
        await Assert.ThrowsAsync<MySqlException>(() =>
            _fixture.InsertHostAsync(revision, 2, "127.0.0.1"));
    }

    [NntpDbIntegrationFact]
    public async Task CompleteRevision_LoadsOnlyThatRevisionCollections()
    {
        RequireReady();
        var first = _fixture.NextRevision();
        var second = _fixture.NextRevision();
        await _fixture.InsertDisabledRevisionAsync(first);
        await _fixture.InsertAccountAsync(first, NntpPostFilterQueries.ListKindDeny, "old-poster");
        await _fixture.InsertCidrAsync(first, NntpPostFilterQueries.ListKindDeny, "192.0.2.0/24");
        await _fixture.InsertArtTypeAsync(first, NntpPostFilterQueries.ListKindReject, "Html");
        await _fixture.InsertHostAsync(first, 1, "192.0.2.10");
        await _fixture.PublishAsync(first);

        await _fixture.InsertDisabledRevisionAsync(second);
        await _fixture.InsertAccountAsync(second, NntpPostFilterQueries.ListKindDeny, "new-poster");
        await _fixture.InsertCidrAsync(second, NntpPostFilterQueries.ListKindAllow, "198.51.100.0/24");
        await _fixture.InsertArtTypeAsync(second, NntpPostFilterQueries.ListKindSaExclude, "YEncoded");
        await _fixture.InsertHostAsync(second, 1, "198.51.100.10");
        await _fixture.PublishAsync(second);

        var record = await _fixture.LoadAsync();
        Assert.Equal(second, record.Revision);
        Assert.Equal([PostFilterAccountIdentity.FromUsername("new-poster")], record.Options.DeniedAccounts);
        Assert.DoesNotContain("new-poster", record.Options.DeniedAccounts);
        Assert.DoesNotContain("old-poster", record.Options.DeniedAccounts);
        Assert.DoesNotContain(PostFilterAccountIdentity.FromUsername("old-poster"), record.Options.DeniedAccounts);
        Assert.Equal(["198.51.100.0/24"], record.Options.AllowlistedCidrs);
        Assert.DoesNotContain("192.0.2.0/24", record.Options.DeniedCidrs);
        Assert.Equal(["YEncoded"], record.Options.SpamAssassin.ExcludeArtTypes);
        Assert.DoesNotContain("Html", record.Options.RejectArtTypes);
        Assert.Equal(["198.51.100.10"], record.Options.SpamAssassin.Hosts);
    }

    [NntpDbIntegrationFact]
    public async Task UncommittedUpdate_IsInvisible_AndRollbackLeavesPrevious()
    {
        RequireReady();
        var published = _fixture.NextRevision();
        var draft = _fixture.NextRevision();
        await _fixture.InsertDisabledRevisionAsync(published);
        await _fixture.PublishAsync(published);
        var before = await _fixture.LoadAsync();
        Assert.Equal(published, before.Revision);

        await using var held = await _fixture.BeginHeldWriteAsync();
        await held.ExecuteAsync(
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
              @revision, UTC_TIMESTAMP(3), 'Closed',
              86400000, 600000,
              0, 0, 0, 0, 0, 0,
              'N', NULL, 131072, 783,
              '1.5', 4, 'RoundRobin',
              5000, 30000
            )
            """,
            command => command.Parameters.AddWithValue("@revision", draft));
        await held.ExecuteAsync(
            "UPDATE nntppostfiltercurrent SET revision = @revision WHERE policy_id = 1",
            command => command.Parameters.AddWithValue("@revision", draft));

        var unseen = await _fixture.LoadAsync();
        Assert.Equal(published, unseen.Revision);
        Assert.Equal(PostFilterGateState.Disabled, unseen.Options.Gate);

        await held.RollbackAsync();
        var afterRollback = await _fixture.LoadAsync();
        Assert.Equal(published, afterRollback.Revision);
        Assert.Equal(PostFilterGateState.Disabled, afterRollback.Options.Gate);
    }

    [NntpDbIntegrationFact]
    public async Task CommittedUpdate_IsVisibleToTwoConsumers()
    {
        RequireReady();
        var revision = _fixture.NextRevision();
        await _fixture.InsertDisabledRevisionAsync(revision);
        await _fixture.PublishAsync(revision);
        var first = await _fixture.LoadAsync();
        var second = await _fixture.LoadAsync();
        Assert.Equal(revision, first.Revision);
        Assert.Equal(revision, second.Revision);
        Assert.Equal(first.Options.Gate, second.Options.Gate);
    }

    [NntpDbIntegrationFact]
    public async Task RevisionRegression_IsRejectedByTrigger()
    {
        RequireReady();
        var first = _fixture.NextRevision();
        var second = _fixture.NextRevision();
        await _fixture.InsertDisabledRevisionAsync(first);
        await _fixture.PublishAsync(first);
        var same = await Assert.ThrowsAsync<MySqlException>(() => _fixture.PublishAsync(first));
        Assert.Contains("must increase", same.Message, StringComparison.OrdinalIgnoreCase);
        var seedPointer = await Assert.ThrowsAsync<MySqlException>(() =>
            _fixture.ExecuteRawAsync(
                "UPDATE nntppostfiltercurrent SET revision = 1 WHERE policy_id = 1"));
        Assert.Contains("must increase", seedPointer.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first, (await _fixture.LoadAsync()).Revision);

        await _fixture.InsertDisabledRevisionAsync(second);
        await _fixture.PublishAsync(second);
        Assert.Equal(second, (await _fixture.LoadAsync()).Revision);
        var lower = await Assert.ThrowsAsync<MySqlException>(() => _fixture.PublishAsync(first));
        Assert.Contains("must increase", lower.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(second, (await _fixture.LoadAsync()).Revision);
    }

    [NntpDbIntegrationFact]
    public async Task InvalidCidrAndArtType_FailCompilation()
    {
        RequireReady();
        var revision = _fixture.NextRevision();
        await _fixture.InsertDisabledRevisionAsync(revision);
        await _fixture.InsertCidrAsync(revision, NntpPostFilterQueries.ListKindDeny, "not-a-cidr");
        await _fixture.PublishAsync(revision);
        var record = await _fixture.LoadAsync();
        var cidr = Assert.Throws<InvalidOperationException>(() =>
            PostFilterPolicyCompiler.Compile(record.Options, record.Revision));
        Assert.Contains("CIDR", cidr.Message, StringComparison.Ordinal);

        var next = _fixture.NextRevision();
        await _fixture.InsertDisabledRevisionAsync(next);
        var invalidArtType = await Assert.ThrowsAsync<MySqlException>(() =>
            _fixture.InsertArtTypeAsync(next, NntpPostFilterQueries.ListKindReject, "NotAnArtType"));
        Assert.Contains("art_type", invalidArtType.Message, StringComparison.OrdinalIgnoreCase);
    }

    [NntpDbIntegrationFact]
    public async Task ShowCreate_UsesMd5AccountAndArtTypeEnumAndEvidenceTable()
    {
        RequireReady();
        var accounts = await _fixture.ShowCreateTableAsync("nntppostfilteraccounts");
        Assert.Contains("char(32)", accounts, StringComparison.OrdinalIgnoreCase);
        var artTypes = await _fixture.ShowCreateTableAsync("nntppostfilterarttypes");
        Assert.Contains("enum(", artTypes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("YEncoded", artTypes, StringComparison.Ordinal);
        var evidence = await _fixture.ShowCreateTableAsync("nntppostfilterrejections");
        Assert.Contains("longblob", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("article_payload", evidence, StringComparison.Ordinal);
        Assert.Contains("varbinary(16)", evidence, StringComparison.OrdinalIgnoreCase);
    }

    [NntpDbIntegrationFact]
    public async Task AccountInsert_RejectsPlaintextUsername()
    {
        RequireReady();
        var revision = _fixture.NextRevision();
        await _fixture.InsertDisabledRevisionAsync(revision);
        var error = await Assert.ThrowsAsync<MySqlException>(() =>
            _fixture.ExecuteRawAsync(
                "INSERT INTO nntppostfilteraccounts (revision, list_kind, account_name) "
                + "VALUES (" + revision.ToString(CultureInfo.InvariantCulture)
                + ", 'deny', 'plaintext-user')"));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    private void RequireReady()
    {
        if (_fixture.IsConfigured)
        {
            return;
        }

        if (NntpDbIntegration.TryGetConnectionString() is null)
        {
            return;
        }

        Assert.Fail(_fixture.SkipReason ?? "PostFilter MySQL fixture is not configured.");
    }
}
