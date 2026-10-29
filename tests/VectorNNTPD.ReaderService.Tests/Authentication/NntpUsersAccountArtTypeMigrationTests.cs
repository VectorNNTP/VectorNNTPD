using System.Globalization;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.Authentication;

[Collection("NntpDbPostFilter")]
public sealed class NntpUsersAccountArtTypeMigrationTests
{
    private readonly MySqlPostFilterPolicyIntegrationFixture _fixture;

    public NntpUsersAccountArtTypeMigrationTests(MySqlPostFilterPolicyIntegrationFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    [Fact]
    public void OfficialMigrationScript_IsAdditiveAndIdempotent()
    {
        var path = NntpUsersArtTypeMigrationScript.FindPath();
        Assert.EndsWith(
            Path.Join("docs", "schema", "nntpusers-account-art-type.sql"),
            path,
            StringComparison.OrdinalIgnoreCase);
        var sql = File.ReadAllText(path);
        Assert.Contains("ALTER TABLE nntpusers", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN account_art_type", sql, StringComparison.Ordinal);
        Assert.Contains("INT UNSIGNED NOT NULL DEFAULT 65535", sql, StringComparison.Ordinal);
        Assert.Contains("information_schema.columns", sql, StringComparison.Ordinal);
        Assert.Contains("DELIMITER $$", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE nntpusers", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DEFAULT 65535", NntpUserQueries.AddAccountArtTypeColumn, StringComparison.Ordinal);
        Assert.DoesNotContain("IF NOT EXISTS", NntpUserQueries.AddAccountArtTypeColumn, StringComparison.Ordinal);
        Assert.Equal(65535u, ArticleTypeCapabilities.AllValue);
        var statements = NntpUsersArtTypeMigrationScript.ReadStatements();
        Assert.Equal(4, statements.Count);
        Assert.Equal("DROP PROCEDURE IF EXISTS nntpusers_add_account_art_type", statements[0]);
        Assert.StartsWith("CREATE PROCEDURE nntpusers_add_account_art_type()", statements[1], StringComparison.Ordinal);
        Assert.Equal("CALL nntpusers_add_account_art_type()", statements[2]);
        Assert.Equal("DROP PROCEDURE nntpusers_add_account_art_type", statements[3]);
    }

    [NntpDbIntegrationFact]
    public async Task OfficialMigration_IsSafeToApplyTwice_AndDefaultsExistingRows()
    {
        RequireReady();
        await _fixture.ExecuteRawAsync(
            """
            CREATE TABLE IF NOT EXISTS nntpusers (
              account_name CHAR(32) NOT NULL PRIMARY KEY
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
            """);
        await _fixture.ExecuteRawAsync(
            "INSERT IGNORE INTO nntpusers (account_name) VALUES ('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')");
        foreach (var statement in NntpUsersArtTypeMigrationScript.ReadStatements())
        {
            await _fixture.ExecuteRawAsync(statement);
        }

        foreach (var statement in NntpUsersArtTypeMigrationScript.ReadStatements())
        {
            await _fixture.ExecuteRawAsync(statement);
        }

        var columnType = await _fixture.ExecuteRawScalarAsync(
            """
            SELECT COLUMN_TYPE FROM information_schema.columns
            WHERE table_schema = DATABASE()
              AND table_name = 'nntpusers'
              AND column_name = 'account_art_type'
            """);
        Assert.Equal("int unsigned", Convert.ToString(columnType, CultureInfo.InvariantCulture));

        var defaultValue = await _fixture.ExecuteRawScalarAsync(
            "SELECT account_art_type FROM nntpusers WHERE account_name = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'");
        Assert.Equal(
            ArticleTypeCapabilities.AllValue,
            Convert.ToUInt32(defaultValue, CultureInfo.InvariantCulture));
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
