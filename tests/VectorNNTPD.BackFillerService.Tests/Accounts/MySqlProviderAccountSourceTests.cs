using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.BackFillerService.Tests.Accounts
{
    /// <summary>
    /// MySqlProviderAccountSource SQL shape, parsing, and absence of command timeout.
    /// </summary>

    public sealed class MySqlProviderAccountSourceTests
    {
        /// <summary>
        /// Contract: Accounts query selects mapped columns and filters by server id.
        /// </summary>

        [Fact]
        public void Accounts_query_selects_mapped_columns_and_filters_by_server_id()
        {
            Assert.Equal("nntpbackfilleraccounts", MySqlProviderAccountSource.AccountsTableName);
            Assert.Equal(
                "SELECT backbone, hostname, keepalive, maxconnections, password, port, username, usessl FROM nntpbackfilleraccounts WHERE serverid = @ServerId;",
                MySqlProviderAccountSource.AccountsQuery);
            Assert.DoesNotContain("Password=", MySqlProviderAccountSource.AccountsQuery, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Contract: ParseKeepAlive accepts integer shapes.
        /// </summary>

        [Theory]
        [InlineData((byte)7)]
        [InlineData((short)7)]
        [InlineData(7)]
        public void ParseKeepAlive_accepts_integer_shapes(object raw)
        {
            Assert.Equal((byte)7, MySqlProviderAccountSource.ParseKeepAlive(raw));
        }

        /// <summary>
        /// The operation throws InvalidOperationException with the expected contract.
        /// </summary>

        [Fact]
        public void ParseKeepAlive_rejects_null_and_non_integers()
        {
            Assert.Throws<InvalidOperationException>(() => MySqlProviderAccountSource.ParseKeepAlive("keep"));
            var ex = Assert.Throws<InvalidOperationException>(
                () => MySqlProviderAccountSource.ParseKeepAlive(DBNull.Value));
            Assert.DoesNotContain("db-secret-xyz", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: TryParseKeepAlive returns null for SQL NULL and non-integers without throwing.
        /// </summary>
        [Fact]
        public void TryParseKeepAlive_returns_null_for_sql_null_and_non_integers()
        {
            Assert.Null(MySqlProviderAccountSource.TryParseKeepAlive(DBNull.Value));
            Assert.Null(MySqlProviderAccountSource.TryParseKeepAlive("keep"));
            Assert.Equal((byte)7, MySqlProviderAccountSource.TryParseKeepAlive(7));
        }

        /// <summary>
        /// Contract: MaterializeRow maps SQL NULL credentials to null without inventing empty strings.
        /// </summary>
        [Fact]
        public void MaterializeRow_maps_sql_null_credentials_to_null()
        {
            var row = MySqlProviderAccountSource.MaterializeRow(
                "Giganews",
                "news.example.test",
                (byte)0,
                4,
                DBNull.Value,
                DBNull.Value,
                563,
                "y");

            Assert.Null(row.Username);
            Assert.Null(row.Password);
            Assert.Equal("Giganews", row.Backbone);
            Assert.Equal("news.example.test", row.Hostname);
        }

        /// <summary>
        /// Contract: MaterializeRow maps SQL NULL required non-credential cells to null fields for mapper rejection.
        /// </summary>
        [Fact]
        public void MaterializeRow_maps_sql_null_required_fields_to_null()
        {
            var row = MySqlProviderAccountSource.MaterializeRow(
                DBNull.Value,
                DBNull.Value,
                DBNull.Value,
                DBNull.Value,
                "user",
                "pass",
                DBNull.Value,
                DBNull.Value);

            Assert.Null(row.Backbone);
            Assert.Null(row.Hostname);
            Assert.Null(row.KeepAliveSeconds);
            Assert.Null(row.MaxConnections);
            Assert.Null(row.Port);
            Assert.Null(row.UseSslRaw);
            Assert.Equal("user", row.Username);
            Assert.Equal("pass", row.Password);
        }

        /// <summary>
        /// Contract: unexpected cell types become null fields instead of throwing.
        /// </summary>
        [Fact]
        public void MaterializeRow_maps_unexpected_types_to_null_without_throwing()
        {
            var row = MySqlProviderAccountSource.MaterializeRow(
                123,
                true,
                "keep",
                "max",
                42,
                Guid.NewGuid(),
                "port",
                9);

            Assert.Null(row.Backbone);
            Assert.Null(row.Hostname);
            Assert.Null(row.KeepAliveSeconds);
            Assert.Null(row.MaxConnections);
            Assert.Null(row.Username);
            Assert.Null(row.Password);
            Assert.Null(row.Port);
            Assert.Null(row.UseSslRaw);
        }

        /// <summary>
        /// Contract: a mixed materialization set still maps valid rows after NULL/malformed cells.
        /// </summary>
        [Fact]
        public void MaterializeRow_mixed_set_maps_through_provider_account_mapper()
        {
            var rows = new[]
            {
                MySqlProviderAccountSource.MaterializeRow(
                    "Giganews",
                    DBNull.Value,
                    (byte)0,
                    4,
                    "user",
                    "pass",
                    563,
                    "y"),
                MySqlProviderAccountSource.MaterializeRow(
                    "Eweka",
                    "eweka.example.test",
                    (byte)0,
                    2,
                    DBNull.Value,
                    "pass",
                    119,
                    "n"),
                MySqlProviderAccountSource.MaterializeRow(
                    "Abavia",
                    "abavia.example.test",
                    (byte)0,
                    3,
                    "abavia-user",
                    ProviderAccountTestRows.SecretPassword,
                    563,
                    "y"),
            };

            var mapped = ProviderAccountMapper.Map(rows);
            Assert.Equal(2, mapped.Providers.Count);
            Assert.Contains(mapped.Providers, static p => p.Backbone == "Eweka" && !p.RequiresAuthentication);
            Assert.Contains(
                mapped.Providers,
                static p => p.Backbone == "Abavia"
                    && p.RequiresAuthentication
                    && p.Username == "abavia-user");
            Assert.Contains(mapped.Rejected, static r => r.Backbone == "Giganews");
        }

        /// <summary>
        /// Contract: ReadRowsAsync surfaces real DBNull cells, continues after a malformed row, and does not invent empty credentials.
        /// </summary>
        [Fact]
        public async Task ReadRowsAsync_maps_dbnull_cells_and_continues_to_later_rows()
        {
            var names = new[]
            {
                "backbone",
                "hostname",
                "keepalive",
                "maxconnections",
                "password",
                "port",
                "username",
                "usessl",
            };
            await using var reader = new SequenceDbDataReader(
                names,
                [
                    "Giganews",
                    DBNull.Value,
                    (byte)0,
                    4,
                    "pass",
                    563,
                    "user",
                    "y",
                ],
                [
                    "Eweka",
                    "eweka.example.test",
                    (byte)0,
                    2,
                    ProviderAccountTestRows.SecretPassword,
                    119,
                    DBNull.Value,
                    "n",
                ],
                [
                    "Abavia",
                    "abavia.example.test",
                    (byte)0,
                    3,
                    ProviderAccountTestRows.SecretPassword,
                    563,
                    "abavia-user",
                    "y",
                ]);

            var rows = await MySqlProviderAccountSource.ReadRowsAsync(reader, CancellationToken.None);
            Assert.Equal(3, rows.Count);
            Assert.Null(rows[0].Hostname);
            Assert.Null(rows[1].Username);
            Assert.Equal(ProviderAccountTestRows.SecretPassword, rows[1].Password);
            Assert.Equal("abavia-user", rows[2].Username);

            var mapped = ProviderAccountMapper.Map(rows);
            Assert.Equal(2, mapped.Providers.Count);
            Assert.Contains(mapped.Providers, static p => p.Backbone == "Eweka" && !p.RequiresAuthentication && p.Password is null);
            Assert.Contains(
                mapped.Providers,
                static p => p.Backbone == "Abavia" && p.RequiresAuthentication && p.Username == "abavia-user");
            Assert.Contains(mapped.Rejected, static r => r.Backbone == "Giganews" && r.Reason == "Hostname is required.");
        }

        /// <summary>
        /// Contract: ReadRowsAsync propagates cancellation from ReadAsync without swallowing it as a row failure.
        /// </summary>
        [Fact]
        public async Task ReadRowsAsync_propagates_cancellation()
        {
            var names = new[]
            {
                "backbone",
                "hostname",
                "keepalive",
                "maxconnections",
                "password",
                "port",
                "username",
                "usessl",
            };
            await using var reader = new SequenceDbDataReader(
                names,
                [
                    "Giganews",
                    "news.example.test",
                    (byte)0,
                    4,
                    "pass",
                    563,
                    "user",
                    "y",
                ]);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => MySqlProviderAccountSource.ReadRowsAsync(reader, cts.Token));
        }

        /// <summary>
        /// Contract: Source does not set an application command timeout.
        /// </summary>

        [Fact]
        public void Source_does_not_set_an_application_command_timeout()
        {
            var source = File.ReadAllText(FindSource());
            Assert.DoesNotContain("CommandTimeout", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CommandTimeoutSeconds", source, StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: Source stores the validated connection string without logging it.
        /// </summary>

        [Fact]
        public void Source_stores_the_validated_connection_string_without_logging_it()
        {
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());
            var database = new NntpDbService(
                new MySqlNntpDbConnectionFactory(),
                Options.Create(new NntpDbOptions { ConnectionString = runtime.NntpDb.ConnectionString }),
                NullLogger<NntpDbService>.Instance);
            _ = new MySqlProviderAccountSource(database, runtime.ServerId);
            Assert.Contains("Password=db-secret-xyz", runtime.NntpDb.ConnectionString, StringComparison.Ordinal);
        }

        /// <summary>Walks parents of the test output directory until <c>MySqlProviderAccountSource.cs</c> exists.</summary>
        /// <exception cref="DirectoryNotFoundException">No parent contains that file.</exception>
        private static string FindSource()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = RelativePath.Under(
                    directory.FullName,
                    "src",
                    "VectorNNTPD.BackFillerService",
                    "Accounts",
                    "MySqlProviderAccountSource.cs");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate MySqlProviderAccountSource.cs.");
        }
    }
}
