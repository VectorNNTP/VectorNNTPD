using MySqlConnector;
using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.StorageService.NntpDb
{

    /// <summary>
    /// Reads the <c>nntpsharedconfig</c> columns StorageServer applies.
    /// </summary>
    /// <remarks>
    /// The query does not select <c>maxartsize</c>, <c>sitename</c>, or <c>prometheusurl</c>.
    /// NNTPD and BackFiller keep the shared Common reader, which still selects those columns.
    /// This type does not create schema or run migrations.
    /// </remarks>
    internal static class StorageServerSharedConfigurationReader
    {
        /// <summary>
        /// Selects at most two rows so a second row is visible. A singleton table is still invalid when empty or duplicated.
        /// </summary>
        internal const string SelectSql =
            "SELECT acmedirectoryurl, acmerenewalthresholddays, cloudflarezoneid, dnssuffix, acmeaccount, acmecertpass, cloudflareapikey, rabbitmqpassword, rabbitmqusername FROM nntpsharedconfig LIMIT 2";

        /// <summary>Reads one StorageServer <c>nntpsharedconfig</c> row and validates the columns this host applies.</summary>
        /// <param name="session">Open logical session.</param>
        /// <param name="cancellationToken">Token used to cancel the query.</param>
        /// <returns>The validated snapshot. <c>maxartsize</c> and <c>sitename</c> are not taken from the database.</returns>
        /// <exception cref="InvalidOperationException">The row is missing, duplicated, or a required column is invalid.</exception>
        internal static async ValueTask<NntpSharedConfiguration> ReadAsync(
            INntpDbSession session,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(session);
            if (session is INntpSharedConfigurationRowSource source)
            {
                return Accept(await source.ReadCandidatesAsync(cancellationToken).ConfigureAwait(false));
            }

            await using var command = session.CreateCommand();
            command.CommandText = SelectSql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<NntpSharedConfigurationCandidate>(2);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadRow(reader));
                if (rows.Count > 1)
                {
                    break;
                }
            }

            return Accept(rows);
        }

        /// <summary>Validates one candidate after discarding <c>maxartsize</c> and <c>sitename</c>.</summary>
        /// <param name="rows">Candidates. Exactly one is required.</param>
        /// <returns>The validated snapshot.</returns>
        /// <exception cref="InvalidOperationException">The candidates are not one valid row.</exception>
        internal static NntpSharedConfiguration Accept(IReadOnlyList<NntpSharedConfigurationCandidate> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            if (rows.Count != 1)
            {
                return NntpSharedConfigurationReader.Validate(rows);
            }

            return NntpSharedConfigurationReader.Validate([DiscardUnusedColumns(rows[0])]);
        }

        /// <summary>Maps one result row. Unused article columns are not read.</summary>
        /// <param name="reader">Reader positioned on a <c>nntpsharedconfig</c> row.</param>
        /// <returns>A candidate whose article-size and site-name fields are not from the database.</returns>
        private static NntpSharedConfigurationCandidate ReadRow(MySqlDataReader reader) =>
            new(
                MaxArticleBytes: 1,
                SiteName: "unused",
                PrometheusUrl: null,
                AcmeDirectoryUrl: reader.IsDBNull(0) ? null : reader.GetString(0),
                AcmeRenewalThresholdDays: reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1)),
                CloudFlareZoneId: reader.IsDBNull(2) ? null : reader.GetString(2),
                DnsSuffix: reader.IsDBNull(3) ? null : reader.GetString(3),
                AcmeAccount: reader.IsDBNull(4) ? null : reader.GetString(4),
                AcmeCertificatePassword: reader.IsDBNull(5) ? null : reader.GetString(5),
                CloudFlareApiKey: reader.IsDBNull(6) ? null : reader.GetString(6),
                RabbitMqPassword: reader.IsDBNull(7) ? null : reader.GetString(7),
                RabbitMqUsername: reader.IsDBNull(8) ? null : reader.GetString(8));

        /// <summary>Replaces <c>maxartsize</c>, <c>sitename</c>, and <c>prometheusurl</c> so validation cannot observe database values.</summary>
        /// <param name="row">Candidate from a row source. Those three fields are ignored.</param>
        /// <returns>A candidate the shared validator can accept without those columns.</returns>
        private static NntpSharedConfigurationCandidate DiscardUnusedColumns(NntpSharedConfigurationCandidate row) =>
            row with
            {
                MaxArticleBytes = 1,
                SiteName = "unused",
                PrometheusUrl = null,
            };
    }
}
