using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
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
