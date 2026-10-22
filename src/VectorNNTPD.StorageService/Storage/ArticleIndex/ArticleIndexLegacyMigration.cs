using Microsoft.Data.Sqlite;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>
    /// Copies a development-era single <c>article-index.sqlite</c> into per-spool databases.
    /// </summary>
    /// <remarks>
    /// The old file is renamed to <c>article-index.sqlite.migrated</c> only after every row is present
    /// in the spool database named by its <c>SpoolDirectory</c>. Receipt and access times are kept.
    /// A conflict does not overwrite a timestamp already stored in the target. The old file is not deleted.
    /// An unknown schema is left unchanged.
    /// </remarks>
    internal static class ArticleIndexLegacyMigration
    {
        /// <summary>Migrates the legacy database when it is present. Returns the number of rows inserted.</summary>
        /// <param name="databaseDirectory">Directory that contains the legacy file and the new databases.</param>
        /// <param name="bindings">Registered spools.</param>
        /// <param name="databases">Open target databases keyed by database path.</param>
        /// <param name="cancellationToken">Checked between rows. The legacy file is not renamed when this cancels.</param>
        /// <returns>Rows inserted into per-spool databases. Zero when the legacy file is absent.</returns>
        internal static int MigrateIfPresent(
            string databaseDirectory,
            IReadOnlyList<ArticleSpoolBinding> bindings,
            IReadOnlyDictionary<string, ArticleIndexDatabase> databases,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseDirectory);
            ArgumentNullException.ThrowIfNull(bindings);
            ArgumentNullException.ThrowIfNull(databases);
            // DatabaseFileName is a fixed leaf ("article-index.sqlite"); CombineUnder rejects a rooted override.
            var legacyPath = StorageRelativePath.CombineUnder(databaseDirectory, ArticleIndexSettings.DatabaseFileName);
            if (!File.Exists(legacyPath))
            {
                return 0;
            }

            if (bindings.Count == 0)
            {
                throw new InvalidOperationException(
                    $"'{legacyPath}' exists but no spools are registered. The file was not deleted or modified.");
            }

            var bySpool = new Dictionary<string, ArticleSpoolBinding>(PathComparer);
            foreach (var binding in bindings)
            {
                bySpool[binding.SpoolDirectory] = binding;
            }

            int imported;
            using (var connection = Open(legacyPath))
            {
                EnsureLegacySchema(connection, legacyPath);
                var rows = ReadRows(connection, legacyPath, bySpool, cancellationToken);
                imported = 0;
                foreach (var binding in bindings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mine = rows.Where(row => PathsEqual(row.SpoolDirectory, binding.SpoolDirectory));
                    imported += databases[binding.DatabasePath].ImportRows(mine);
                }

                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var binding = bySpool[Canonical(row.SpoolDirectory)];
                    if (!databases[binding.DatabasePath].TryRead(row.Id, out var stored, counters: null)
                        || stored.ReceivedAtUnixMilliseconds != row.ReceivedAtUnixMilliseconds)
                    {
                        throw new InvalidOperationException(
                            $"Legacy article index '{legacyPath}' could not be verified in '{binding.DatabasePath}'. The legacy file was not renamed.");
                    }
                }
            }

            SqliteConnection.ClearAllPools();
            Move(legacyPath, legacyPath + ".migrated");
            MoveIfExists(legacyPath + "-wal", legacyPath + ".migrated-wal");
            MoveIfExists(legacyPath + "-shm", legacyPath + ".migrated-shm");
            return imported;
        }

        /// <summary>Opens the legacy file read-only.</summary>
        /// <param name="path">Legacy database path.</param>
        /// <returns>An open connection. The caller disposes it.</returns>
        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString);
            connection.Open();
            return connection;
        }

        /// <summary>Refuses any file that is not the version-1 single-database shape.</summary>
        /// <param name="connection">Open legacy connection.</param>
        /// <param name="path">Path named in the failure.</param>
        private static void EnsureLegacySchema(SqliteConnection connection, string path)
        {
            using var version = connection.CreateCommand();
            version.CommandText = "SELECT SchemaVersion FROM ArticleIndexMeta WHERE Id = 1;";
            var value = version.ExecuteScalar();
            if (value is null or DBNull || Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException(
                    $"'{path}' is not schema version 1. It was not modified. Per-spool databases use version {ArticleIndexSettings.SchemaVersion}.");
            }

            using var column = connection.CreateCommand();
            column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ArticleIndex') WHERE name = 'SpoolDirectory';";
            if (Convert.ToInt32(column.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException($"'{path}' has no SpoolDirectory column. It was not modified.");
            }
        }

        /// <summary>Reads every legacy row and rejects a spool that is not registered.</summary>
        /// <param name="connection">Open legacy connection.</param>
        /// <param name="path">Path named in the failure.</param>
        /// <param name="bySpool">Registered spools keyed by canonical path.</param>
        /// <param name="cancellationToken">Checked between rows.</param>
        /// <returns>The rows. This buffers the legacy development database, not a second copy of a per-spool database.</returns>
        private static List<ArticleIndexLegacyRow> ReadRows(
            SqliteConnection connection,
            string path,
            Dictionary<string, ArticleSpoolBinding> bySpool,
            CancellationToken cancellationToken)
        {
            var rows = new List<ArticleIndexLegacyRow>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MessageIdHash, ReceivedAt, LastAccessedAt, SpoolDirectory FROM ArticleIndex;";
            using var reader = command.ExecuteReader();
            var hash = new byte[ArticleIndexSettings.HashLength];
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = reader.GetBytes(0, 0, hash, 0, hash.Length);
                if (length != hash.Length)
                {
                    throw new InvalidOperationException($"'{path}' contains a hash that is not 32 bytes. It was not modified.");
                }

                var spool = Canonical(reader.GetString(3));
                if (!bySpool.ContainsKey(spool))
                {
                    throw new InvalidOperationException(
                        $"'{path}' contains spool '{reader.GetString(3)}', which is not registered. The file was not modified.");
                }

                rows.Add(new ArticleIndexLegacyRow(
                    ArticleId.FromSpan(hash),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    spool));
            }

            return rows;
        }

        /// <summary>Moves <paramref name="source"/> to <paramref name="destination"/> when the destination is absent.</summary>
        /// <param name="source">File to rename.</param>
        /// <param name="destination">New name. An existing destination fails the migration.</param>
        private static void Move(string source, string destination)
        {
            if (File.Exists(destination))
            {
                throw new InvalidOperationException($"'{destination}' already exists. '{source}' was not replaced.");
            }

            File.Move(source, destination);
        }

        /// <summary>Renames a sidecar when it exists.</summary>
        /// <param name="source">Optional WAL or shared-memory file.</param>
        /// <param name="destination">New sidecar name.</param>
        private static void MoveIfExists(string source, string destination)
        {
            if (File.Exists(source))
            {
                Move(source, destination);
            }
        }

        /// <summary>Returns a full path without a trailing separator.</summary>
        /// <param name="path">Spool path from the legacy row or the registry.</param>
        /// <returns>The canonical path.</returns>
        private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        /// <summary>Compares canonical spool paths.</summary>
        /// <param name="left">First path.</param>
        /// <param name="right">Second path.</param>
        /// <returns><see langword="true"/> when they name the same directory.</returns>
        private static bool PathsEqual(string left, string right) => string.Equals(left, right, PathComparison);

        /// <summary>Path comparison used to match a legacy spool directory to a binding.</summary>
        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>Path comparer used for the spool lookup.</summary>
        private static StringComparer PathComparer =>
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }
}
