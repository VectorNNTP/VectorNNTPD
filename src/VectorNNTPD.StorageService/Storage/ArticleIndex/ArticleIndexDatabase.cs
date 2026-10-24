using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.Sqlite;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>
    /// Owns the SQLite file, the single writer connection, and a small read-only pool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mutations run on the writer connection. Callers of <see cref="Commit"/> must be the writer thread.
    /// Lookups use a different connection so a reader does not wait for the writer lock except for SQLite's
    /// own WAL snapshot. Microsoft.Data.Sqlite's asynchronous command APIs are not used: they call the
    /// synchronous SQLite library and do not perform asynchronous disk I/O.
    /// </para>
    /// <para>
    /// Timestamps are UTC Unix epoch milliseconds. The page size is set before the first schema write.
    /// An existing database keeps the page size it was created with.
    /// </para>
    /// </remarks>
    internal sealed class ArticleIndexDatabase : IDisposable
    {
        private readonly ArticleIndexSettings _settings;
        private readonly ArticleIndexCacheBudget _budget;
        private readonly string _spoolDirectory;
        private readonly byte[] _writerHash = new byte[ArticleIndexSettings.HashLength];
        private SqliteConnection? _writer;
        private SqliteCommand? _insert;
        private SqliteCommand? _import;
        private SqliteCommand? _touch;
        private SqliteCommand? _exists;
        private SqliteCommand? _countUpdate;
        private SqliteCommand? _storedBytesUpdate;
        private SqliteCommand? _evictDelete;
        private SqliteCommand? _load;
        private ReadSlot[]? _readers;
        private SemaphoreSlim? _readerGate;
        private ConcurrentStack<int>? _freeReaders;
        private int _disposed;

        /// <summary>Initializes a database that opens <paramref name="databasePath"/> on <see cref="Initialize"/>.</summary>
        /// <param name="databasePath">Full path of the SQLite file inside the configured database directory.</param>
        /// <param name="spoolDirectory">Spool root whose articles this file indexes.</param>
        /// <param name="settings">Pragmas, timeouts, and the test commit hook.</param>
        /// <param name="budget">Cache sizes for this database. Callers split one aggregate budget across spools.</param>
        internal ArticleIndexDatabase(
            string databasePath,
            string spoolDirectory,
            ArticleIndexSettings settings,
            ArticleIndexCacheBudget budget)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);
            ArgumentNullException.ThrowIfNull(settings);
            DatabasePath = databasePath;
            _spoolDirectory = spoolDirectory;
            _settings = settings;
            _budget = budget;
        }

        /// <summary>Gets the SQLite file path.</summary>
        internal string DatabasePath { get; }

        /// <summary>Gets the schema version read after initialization.</summary>
        internal int SchemaVersion { get; private set; }

        /// <summary>Gets the page size reported by SQLite after initialization.</summary>
        internal int PageSize { get; private set; }

        /// <summary>Gets the article count stored in <c>ArticleIndexMeta</c> at initialization.</summary>
        internal long ArticleCount { get; private set; }

        /// <summary>Gets the stored-byte total from <c>ArticleIndexMeta</c> at initialization and after commits on this connection.</summary>
        internal long StoredBytes { get; private set; }

        /// <summary>Gets the WAL file length, or zero when the file is absent.</summary>
        internal long WalBytes
        {
            get
            {
                var path = DatabasePath + "-wal";
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
        }

        /// <summary>Opens the writer, creates or migrates schema version 3, and opens readers.</summary>
        /// <param name="cancellationToken">Observed before the file is opened. The SQLite calls themselves are synchronous.</param>
        internal void Initialize(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            };
            using var ownership = new DisposableOwnership<SqliteConnection>(
                new SqliteConnection(builder.ConnectionString));
            var connection = ownership.Resource;
            connection.Open();
            ApplyWriterPragmas(connection, pageSizeFirst: true);
            Migrate(connection);
            ApplyWriterPragmas(connection, pageSizeFirst: false);
            PrepareWriter(connection);
            // Readers open before Transfer so a reader-pool failure still disposes the writer.
            OpenReaders();
            _writer = ownership.Transfer();
        }

        /// <summary>
        /// Copies every stored hash into <paramref name="presence"/>.
        /// The scan selects only <c>MessageIdHash</c> and does not stat article files.
        /// A missing payload can therefore remain in the set until a later eviction removes the row and the file together.
        /// </summary>
        /// <param name="presence">Membership index. The caller reserves capacity before this call.</param>
        /// <param name="onDuplicate">Called when the hash was already published by another spool. It must not re-enter this database.</param>
        /// <param name="cancellationToken">Checked between rows. A throw leaves <paramref name="presence"/> unpublished by the service.</param>
        /// <returns>The number of rows scanned.</returns>
        internal long LoadPresence(ArticlePresenceIndex presence, Action<ArticleId>? onDuplicate, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(presence);
            var command = _load ?? throw new InvalidOperationException("Article index is not open.");
            long scanned = 0;
            using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = reader.GetBytes(0, 0, _writerHash, 0, _writerHash.Length);
                if (length != ArticleIndexSettings.HashLength)
                {
                    throw new InvalidOperationException(
                        $"Article index row {scanned} has a {length}-byte hash. Expected {ArticleIndexSettings.HashLength} bytes.");
                }

                scanned++;
                var articleId = ArticleId.FromSpan(_writerHash);
                if (!presence.Add(articleId))
                {
                    onDuplicate?.Invoke(articleId);
                }
            }

            return scanned;
        }

        /// <summary>
        /// Inserts rows that already have distinct receipt and access times.
        /// Call this on the writer connection before the writer task starts.
        /// A conflict keeps the row already stored in this database.
        /// </summary>
        /// <param name="rows">Rows for this spool. The sequence is not retained.</param>
        /// <returns>The number of rows this call inserted.</returns>
        internal int ImportRows(IEnumerable<ArticleIndexLegacyRow> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            var connection = _writer ?? throw new InvalidOperationException("Article index is not open.");
            var import = _import ?? throw new InvalidOperationException("Article index is not open.");
            var countUpdate = _countUpdate ?? throw new InvalidOperationException("Article index is not open.");
            using var transaction = connection.BeginTransaction();
            import.Transaction = transaction;
            countUpdate.Transaction = transaction;
            try
            {
                var inserted = 0;
                foreach (var row in rows)
                {
                    row.Id.CopyTo(_writerHash);
                    import.Parameters[0].Value = _writerHash;
                    import.Parameters[1].Value = row.ReceivedAtUnixMilliseconds;
                    import.Parameters[2].Value = row.LastAccessedAtUnixMilliseconds;
                    if (import.ExecuteNonQuery() == 1)
                    {
                        inserted++;
                    }
                }

                if (inserted > 0)
                {
                    countUpdate.Parameters[0].Value = inserted;
                    if (countUpdate.ExecuteNonQuery() != 1)
                    {
                        throw new InvalidOperationException("Article index metadata row is missing.");
                    }
                }

                transaction.Commit();
                ArticleCount += inserted;
                return inserted;
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch (SqliteException ex)
                {
                    // Prefer the original commit failure over a secondary rollback fault.
                    ExpectedException.Ignore(ex);
                }

                throw;
            }
            finally
            {
                import.Transaction = null;
                countUpdate.Transaction = null;
            }
        }

        /// <summary>
        /// Commits <paramref name="mutations"/> in one transaction.
        /// Dispositions are written on the mutation objects. Waiters are not completed here.
        /// </summary>
        /// <param name="mutations">Distinct identities for this batch.</param>
        internal void Commit(IReadOnlyList<ArticleIndexMutation> mutations)
        {
            ArgumentNullException.ThrowIfNull(mutations);
            var connection = _writer ?? throw new InvalidOperationException("Article index is not open.");
            var insert = _insert!;
            var touch = _touch!;
            var exists = _exists!;
            var countUpdate = _countUpdate!;
            var storedBytesUpdate = _storedBytesUpdate!;
            var evictDelete = _evictDelete!;
            using var transaction = connection.BeginTransaction();
            insert.Transaction = transaction;
            touch.Transaction = transaction;
            exists.Transaction = transaction;
            countUpdate.Transaction = transaction;
            storedBytesUpdate.Transaction = transaction;
            evictDelete.Transaction = transaction;
            try
            {
                var inserted = 0;
                long insertedBytes = 0;
                var evicted = 0;
                long evictedBytes = 0;
                foreach (var mutation in mutations)
                {
                    if (mutation.HasInsert)
                    {
                        mutation.Id.CopyTo(_writerHash);
                        exists.Parameters[0].Value = _writerHash;
                        var existed = exists.ExecuteScalar() is not null and not DBNull;
                        insert.Parameters[0].Value = _writerHash;
                        insert.Parameters[1].Value = mutation.ReceivedAt;
                        insert.Parameters[2].Value = mutation.ArtSizeBytes;
                        var changed = insert.ExecuteNonQuery();
                        if (existed)
                        {
                            mutation.InsertDisposition = ArticleIndexWriteDisposition.AlreadyPresent;
                        }
                        else if (changed == 1)
                        {
                            inserted++;
                            insertedBytes += mutation.ArtSizeBytes;
                            mutation.InsertDisposition = ArticleIndexWriteDisposition.Inserted;
                        }
                        else
                        {
                            throw new InvalidOperationException("Article index insert did not write its row.");
                        }
                    }

                    if (mutation.HasTouch)
                    {
                        mutation.Id.CopyTo(_writerHash);
                        var delta = mutation.AccessIncrement <= 0 ? 1 : mutation.AccessIncrement;
                        touch.Parameters[0].Value = delta;
                        touch.Parameters[1].Value = mutation.LastAccessedAt;
                        touch.Parameters[2].Value = _writerHash;
                        var updated = touch.ExecuteNonQuery();
                        if (updated == 1)
                        {
                            mutation.TouchDisposition = ArticleIndexWriteDisposition.Updated;
                        }
                        else if (mutation.HasInsert)
                        {
                            mutation.TouchDisposition = ArticleIndexWriteDisposition.Unchanged;
                        }
                        else
                        {
                            exists.Parameters[0].Value = _writerHash;
                            var found = exists.ExecuteScalar();
                            mutation.TouchDisposition = found is null or DBNull
                                ? ArticleIndexWriteDisposition.Missing
                                : ArticleIndexWriteDisposition.Unchanged;
                        }
                    }

                    if (!mutation.HasEvictDelete)
                    {
                        continue;
                    }

                    mutation.Id.CopyTo(_writerHash);
                    evictDelete.Parameters[0].Value = _writerHash;
                    object? deletedSize;
                    using (var reader = evictDelete.ExecuteReader())
                    {
                        deletedSize = reader.Read() ? reader.GetInt64(0) : null;
                    }

                    if (deletedSize is null)
                    {
                        mutation.EvictDeleteDisposition = ArticleIndexWriteDisposition.Missing;
                        continue;
                    }

                    evicted++;
                    evictedBytes += (long)deletedSize;
                    mutation.EvictDeleteDisposition = ArticleIndexWriteDisposition.Updated;
                }

                if (inserted > 0)
                {
                    countUpdate.Parameters[0].Value = inserted;
                    if (countUpdate.ExecuteNonQuery() != 1)
                    {
                        throw new InvalidOperationException("Article index metadata row is missing.");
                    }
                }

                if (insertedBytes > 0)
                {
                    storedBytesUpdate.Parameters[0].Value = insertedBytes;
                    if (storedBytesUpdate.ExecuteNonQuery() != 1)
                    {
                        throw new InvalidOperationException("Article index metadata row is missing.");
                    }
                }

                if (evicted > 0)
                {
                    countUpdate.Parameters[0].Value = -evicted;
                    if (countUpdate.ExecuteNonQuery() != 1)
                    {
                        throw new InvalidOperationException("Article index metadata row is missing.");
                    }
                }

                if (evictedBytes > 0)
                {
                    storedBytesUpdate.Parameters[0].Value = -evictedBytes;
                    if (storedBytesUpdate.ExecuteNonQuery() != 1)
                    {
                        throw new InvalidOperationException("Article index metadata row is missing.");
                    }
                }

                if (CommitHookApplies())
                {
                    _settings.BeforeCommit!.Invoke();
                }
                transaction.Commit();
                ArticleCount += inserted - evicted;
                StoredBytes += insertedBytes - evictedBytes;
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch (SqliteException ex)
                {
                    // Prefer the original commit failure over a secondary rollback fault.
                    ExpectedException.Ignore(ex);
                }

                throw;
            }
            finally
            {
                insert.Transaction = null;
                touch.Transaction = null;
                exists.Transaction = null;
                countUpdate.Transaction = null;
                storedBytesUpdate.Transaction = null;
                evictDelete.Transaction = null;
            }
        }

        /// <summary>Reads one committed row by primary key.</summary>
        /// <param name="articleId">Canonical article identity.</param>
        /// <param name="record">The row when the method returns <see langword="true"/>.</param>
        /// <param name="counters">Optional read timer.</param>
        /// <returns><see langword="false"/> when the hash is not committed in this spool database.</returns>
        internal bool TryRead(ArticleId articleId, out ArticleIndexRecord record, ArticleIndexCounters? counters)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var slot = Rent();
            try
            {
                articleId.CopyTo(slot.Hash);
                slot.Lookup.Parameters[0].Value = slot.Hash;
                using var reader = slot.Lookup.ExecuteReader(CommandBehavior.SingleRow);
                if (!reader.Read())
                {
                    record = default;
                    return false;
                }

                record = new ArticleIndexRecord(
                    articleId,
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    _spoolDirectory,
                    reader.GetInt64(2),
                    reader.GetInt64(3));
                return true;
            }
            finally
            {
                counters?.NoteRead(System.Diagnostics.Stopwatch.GetElapsedTime(started));
                Return(slot.Index);
            }
        }

        /// <summary>Copies the oldest committed rows, optionally bounded by an access time.</summary>
        /// <param name="accessedAtOrBeforeUnixMilliseconds">Inclusive upper bound, or null for the oldest rows overall.</param>
        /// <param name="destination">Buffer filled in ascending <c>LastAccessedAt</c> order.</param>
        /// <param name="counters">Optional read timer.</param>
        /// <param name="include">
        /// Returns false to skip a row and keep scanning. Null accepts every row.
        /// Pending access updates use this so a recently touched article is not returned as stale.
        /// </param>
        /// <returns>The number of rows written to <paramref name="destination"/>.</returns>
        internal int CopyOldest(
            long? accessedAtOrBeforeUnixMilliseconds,
            Span<ArticleIndexRecord> destination,
            ArticleIndexCounters? counters,
            Func<ArticleId, long, bool>? include)
        {
            if (destination.Length == 0)
            {
                return 0;
            }

            if (destination.Length > 10_000)
            {
                throw new ArgumentOutOfRangeException(nameof(destination), "Article index eviction reads are limited to 10000 rows.");
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var slot = Rent();
            try
            {
                SqliteCommand command;
                var limit = include is null ? destination.Length : 10_000;
                if (accessedAtOrBeforeUnixMilliseconds is { } threshold)
                {
                    command = slot.Before;
                    command.Parameters[0].Value = threshold;
                    command.Parameters[1].Value = limit;
                }
                else
                {
                    command = slot.Oldest;
                    command.Parameters[0].Value = limit;
                }

                using var reader = command.ExecuteReader();
                var count = 0;
                while (reader.Read() && count < destination.Length)
                {
                    var length = reader.GetBytes(0, 0, slot.Hash, 0, slot.Hash.Length);
                    if (length != ArticleIndexSettings.HashLength)
                    {
                        throw new InvalidOperationException("Article index returned a hash that is not 32 bytes.");
                    }

                    var articleId = ArticleId.FromSpan(slot.Hash);
                    var accessed = reader.GetInt64(2);
                    if (include is not null && !include(articleId, accessed))
                    {
                        continue;
                    }

                    destination[count++] = new ArticleIndexRecord(
                        articleId,
                        reader.GetInt64(1),
                        accessed,
                        _spoolDirectory,
                        reader.GetInt64(3),
                        reader.GetInt64(4));
                }

                return count;
            }
            finally
            {
                counters?.NoteRead(System.Diagnostics.Stopwatch.GetElapsedTime(started));
                Return(slot.Index);
            }
        }

        /// <summary>Copies rows in LFU order with optional inclusion filter.</summary>
        /// <param name="destination">Buffer filled in ascending LFU order.</param>
        /// <param name="counters">Optional read timer.</param>
        /// <param name="include">Returns false to skip a row and keep scanning.</param>
        /// <returns>The number of rows written to <paramref name="destination"/>.</returns>
        internal int CopyLeastFrequentlyUsed(
            Span<ArticleIndexRecord> destination,
            ArticleIndexCounters? counters,
            Func<ArticleId, long, bool>? include)
        {
            if (destination.Length == 0)
            {
                return 0;
            }

            if (destination.Length > 10_000)
            {
                throw new ArgumentOutOfRangeException(nameof(destination), "Article index eviction reads are limited to 10000 rows.");
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var slot = Rent();
            try
            {
                var command = slot.Lfu;
                var limit = include is null ? destination.Length : 10_000;
                command.Parameters[0].Value = limit;
                using var reader = command.ExecuteReader();
                var count = 0;
                while (reader.Read() && count < destination.Length)
                {
                    var length = reader.GetBytes(0, 0, slot.Hash, 0, slot.Hash.Length);
                    if (length != ArticleIndexSettings.HashLength)
                    {
                        throw new InvalidOperationException("Article index returned a hash that is not 32 bytes.");
                    }

                    var articleId = ArticleId.FromSpan(slot.Hash);
                    var accessed = reader.GetInt64(2);
                    if (include is not null && !include(articleId, accessed))
                    {
                        continue;
                    }

                    destination[count++] = new ArticleIndexRecord(
                        articleId,
                        reader.GetInt64(1),
                        accessed,
                        _spoolDirectory,
                        reader.GetInt64(3),
                        reader.GetInt64(4));
                }

                return count;
            }
            finally
            {
                counters?.NoteRead(System.Diagnostics.Stopwatch.GetElapsedTime(started));
                Return(slot.Index);
            }
        }

        /// <summary>
        /// Visits committed hash and spool-directory pairs. The callback must not re-enter this database.
        /// Payloads are not read. This is the stale-row probe, not the startup membership load.
        /// </summary>
        /// <param name="visit">Called once per committed row.</param>
        /// <param name="cancellationToken">Checked between rows.</param>
        internal void ForEachHash(Action<ArticleId> visit, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(visit);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var slot = Rent();
            try
            {
                using var reader = slot.Scan.ExecuteReader(CommandBehavior.SequentialAccess);
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = reader.GetBytes(0, 0, slot.Hash, 0, slot.Hash.Length);
                    if (length != ArticleIndexSettings.HashLength)
                    {
                        throw new InvalidOperationException("Article index returned a hash that is not 32 bytes.");
                    }

                    visit(ArticleId.FromSpan(slot.Hash));
                }
            }
            finally
            {
                Return(slot.Index);
                _ = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            }
        }

        /// <summary>Runs a WAL checkpoint on the writer connection. The caller must own that connection.</summary>
        /// <param name="truncate">True uses <c>TRUNCATE</c>. False uses <c>PASSIVE</c>.</param>
        /// <returns>The checkpoint row returned by SQLite.</returns>
        internal ArticleIndexCheckpoint Checkpoint(bool truncate)
        {
            var connection = _writer ?? throw new InvalidOperationException("Article index is not open.");
            using var command = connection.CreateCommand();
            command.CommandText = truncate ? "PRAGMA wal_checkpoint(TRUNCATE);" : "PRAGMA wal_checkpoint(PASSIVE);";
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("SQLite did not return a checkpoint result.");
            }

            return new ArticleIndexCheckpoint(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        }

        /// <summary>Closes reader and writer connections.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            DisposeReaders();
            DisposeWriterCommands();
            _writer?.Dispose();
            _writer = null;
            _readerGate?.Dispose();
        }

        /// <summary>Returns whether the test commit hook should run for this spool database.</summary>
        /// <returns><see langword="false"/> when no hook is configured or it names another spool.</returns>
        private bool CommitHookApplies()
        {
            if (_settings.BeforeCommit is null)
            {
                return false;
            }

            if (_settings.BeforeCommitSpool is null)
            {
                return true;
            }

            var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_settings.BeforeCommitSpool));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(expected, _spoolDirectory, comparison);
        }

        /// <summary>Rejects a hash that is not 32 bytes before it can reach SQL.</summary>
        /// <param name="hash">Candidate primary key.</param>
        internal static void EnsureHashLength(ReadOnlySpan<byte> hash)
        {
            if (hash.Length != ArticleIndexSettings.HashLength)
            {
                throw new ArgumentException(
                    $"Article index hash must be exactly {ArticleIndexSettings.HashLength} bytes.",
                    nameof(hash));
            }
        }

        /// <summary>Sets the page size first, then the durable writer pragmas after the schema exists.</summary>
        /// <param name="connection">Open writer connection.</param>
        /// <param name="pageSizeFirst">True for the pre-schema page-size statement only.</param>
        private void ApplyWriterPragmas(SqliteConnection connection, bool pageSizeFirst)
        {
            if (pageSizeFirst)
            {
                Execute(connection, $"PRAGMA page_size={ArticleIndexSettings.PageSizeBytes};");
                return;
            }

            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "PRAGMA synchronous=FULL;");
            Execute(connection, $"PRAGMA busy_timeout={_settings.BusyTimeoutMilliseconds};");
            Execute(connection, $"PRAGMA cache_size=-{_budget.WriterCacheKibibytes};");
            Execute(connection, $"PRAGMA wal_autocheckpoint={ArticleIndexSettings.WalAutoCheckpointPages};");
            Execute(connection, $"PRAGMA journal_size_limit={ArticleIndexSettings.JournalSizeLimitBytes};");
            Execute(connection, $"PRAGMA mmap_size={ArticleIndexSettings.MmapSizeBytes};");
            Execute(connection, "PRAGMA temp_store=MEMORY;");
            PageSize = Convert.ToInt32(ExecuteScalar(connection, "PRAGMA page_size;"), System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Creates schema version 3, migrates version 2, or refuses an unknown version.</summary>
        /// <param name="connection">Open writer connection.</param>
        private void Migrate(SqliteConnection connection)
        {
            // Schema DDL stays inside one writer transaction so no reader remains open across
            // CREATE/ALTER/INDEX (avoids SQLite Error 6: database table is locked).
            using (var transaction = connection.BeginTransaction())
            {
                Execute(connection, ArticleIndexSql.CreateMeta, transaction);
                Execute(connection, ArticleIndexSql.CreateArticles, transaction);
                EnsureV3Columns(connection, transaction);
                Execute(connection, ArticleIndexSql.InsertMeta, transaction);

                var version = ReadMetaVersion(connection, transaction);
                if (version == 2)
                {
                    Execute(connection, ArticleIndexSql.SetSchemaVersion3, transaction);
                }
                else if (version != ArticleIndexSettings.SchemaVersion)
                {
                    throw new InvalidOperationException(
                        $"Article index '{DatabasePath}' has schema version {version}. This build supports version {ArticleIndexSettings.SchemaVersion} only and does not modify a newer or older database.");
                }

                Execute(connection, ArticleIndexSql.CreateLfuIndex, transaction);
                Execute(connection, ArticleIndexSql.DropLastAccessedIndex, transaction);
                transaction.Commit();
            }

            ReadMeta(connection, out var schemaVersion, out var articleCount, out var storedBytes);
            SchemaVersion = schemaVersion;
            ArticleCount = articleCount;
            StoredBytes = storedBytes;
        }

        /// <summary>Reads only the schema version while an optional writer transaction is open.</summary>
        private static int ReadMetaVersion(SqliteConnection connection, SqliteTransaction? transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = ArticleIndexSql.ReadMeta;
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("Article index metadata row is missing.");
            }

            return reader.GetInt32(0);
        }

        /// <summary>Reads metadata and closes the reader before further writer statements run.</summary>
        private static void ReadMeta(
            SqliteConnection connection,
            out int version,
            out long articleCount,
            out long storedBytes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ArticleIndexSql.ReadMeta;
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("Article index metadata row is missing.");
            }

            version = reader.GetInt32(0);
            articleCount = reader.GetInt64(1);
            storedBytes = reader.FieldCount > 2 && !reader.IsDBNull(2) ? reader.GetInt64(2) : 0;
        }

        /// <summary>Adds version-3 columns when opening an older on-disk layout.</summary>
        /// <param name="connection">Open writer connection.</param>
        /// <param name="transaction">Optional transaction.</param>
        private static void EnsureV3Columns(SqliteConnection connection, SqliteTransaction? transaction = null)
        {
            if (!ColumnExists(connection, "ArticleIndexMeta", "StoredBytes", transaction))
            {
                Execute(connection, ArticleIndexSql.AddMetaStoredBytes, transaction);
            }

            if (!ColumnExists(connection, "ArticleIndex", "AccessCount", transaction))
            {
                Execute(connection, ArticleIndexSql.AddAccessCount, transaction);
            }

            if (!ColumnExists(connection, "ArticleIndex", "ArtSizeBytes", transaction))
            {
                Execute(connection, ArticleIndexSql.AddArtSizeBytes, transaction);
            }
        }

        /// <summary>Returns whether a table already has a column.</summary>
        private static bool ColumnExists(
            SqliteConnection connection,
            string table,
            string column,
            SqliteTransaction? transaction = null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({table});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Prepares the writer statements used for every batch and for the startup scan.</summary>
        /// <param name="connection">Open writer connection.</param>
        private void PrepareWriter(SqliteConnection connection)
        {
            _insert = Prepare(connection, ArticleIndexSql.Insert);
            AddBlob(_insert, "$hash");
            _insert.Parameters.Add("$received", SqliteType.Integer);
            _insert.Parameters.Add("$artSize", SqliteType.Integer);

            _import = Prepare(connection, ArticleIndexSql.Import);
            AddBlob(_import, "$hash");
            _import.Parameters.Add("$received", SqliteType.Integer);
            _import.Parameters.Add("$accessed", SqliteType.Integer);

            _touch = Prepare(connection, ArticleIndexSql.Touch);
            _touch.Parameters.Add("$delta", SqliteType.Integer);
            _touch.Parameters.Add("$accessed", SqliteType.Integer);
            AddBlob(_touch, "$hash");

            _exists = Prepare(connection, ArticleIndexSql.Exists);
            AddBlob(_exists, "$hash");

            _countUpdate = Prepare(connection, ArticleIndexSql.AddCount);
            _countUpdate.Parameters.Add("$delta", SqliteType.Integer);

            _storedBytesUpdate = Prepare(connection, ArticleIndexSql.AddStoredBytes);
            _storedBytesUpdate.Parameters.Add("$delta", SqliteType.Integer);

            _evictDelete = Prepare(connection, ArticleIndexSql.EvictDelete);
            AddBlob(_evictDelete, "$hash");

            _load = Prepare(connection, ArticleIndexSql.LoadHashes);
        }

        /// <summary>Opens the read-only pool after the writer has created the file.</summary>
        private void OpenReaders()
        {
            var count = _budget.ReaderCount;
            _readers = new ReadSlot[count];
            _readerGate = new SemaphoreSlim(count, count);
            _freeReaders = new ConcurrentStack<int>();
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            };
            for (var i = 0; i < count; i++)
            {
                try
                {
                    OpenAndPublishReader(i, builder);
                }
                catch
                {
                    try
                    {
                        for (var prior = 0; prior < i; prior++)
                        {
                            var slot = _readers[prior];
                            slot.Lookup.Dispose();
                            slot.Oldest.Dispose();
                            slot.Before.Dispose();
                            slot.Lfu.Dispose();
                            slot.Scan.Dispose();
                            slot.Connection.Dispose();
                        }

                        _readers = null;
                        _freeReaders = null;
                        _readerGate?.Dispose();
                        _readerGate = null;
                    }
                    catch (Exception cleanupEx) when (!ExceptionHelpers.IsFatal(cleanupEx))
                    {
                        // Prefer the original initialization failure over cleanup faults.
                        ExpectedException.Ignore(cleanupEx);
                    }

                    throw;
                }
            }
        }

        /// <summary>
        /// Opens one read-only connection and prepared commands, then publishes them into the pool.
        /// On failure, disposes any locals that were not yet published.
        /// </summary>
        private void OpenAndPublishReader(int index, SqliteConnectionStringBuilder builder)
        {
            SqliteConnection? connection = null;
            SqliteCommand? lookup = null;
            SqliteCommand? oldest = null;
            SqliteCommand? before = null;
            SqliteCommand? lfu = null;
            SqliteCommand? scan = null;
            try
            {
                connection = new SqliteConnection(builder.ConnectionString);
                connection.Open();
                Execute(connection, $"PRAGMA busy_timeout={_settings.BusyTimeoutMilliseconds};");
                Execute(connection, $"PRAGMA cache_size=-{_budget.ReaderCacheKibibytes};");
                Execute(connection, "PRAGMA query_only=ON;");
                lookup = Prepare(connection, ArticleIndexSql.Lookup);
                AddBlob(lookup, "$hash");
                oldest = Prepare(connection, ArticleIndexSql.Oldest);
                oldest.Parameters.Add("$limit", SqliteType.Integer);
                before = Prepare(connection, ArticleIndexSql.AccessedBefore);
                before.Parameters.Add("$threshold", SqliteType.Integer);
                before.Parameters.Add("$limit", SqliteType.Integer);
                lfu = Prepare(connection, ArticleIndexSql.LeastFrequentlyUsed);
                lfu.Parameters.Add("$limit", SqliteType.Integer);
                scan = Prepare(connection, ArticleIndexSql.ScanLocations);
                _readers![index] = new ReadSlot(
                    index,
                    connection,
                    lookup,
                    oldest,
                    before,
                    lfu,
                    scan,
                    new byte[ArticleIndexSettings.HashLength]);
                _freeReaders!.Push(index);
                // Successful return leaves ownership with _readers[index]; catch does not run.
            }
            catch
            {
                scan?.Dispose();
                lfu?.Dispose();
                before?.Dispose();
                oldest?.Dispose();
                lookup?.Dispose();
                connection?.Dispose();
                throw;
            }
        }

        /// <summary>Takes one read connection. The caller returns it with <see cref="Return"/>.</summary>
        /// <returns>The exclusive slot.</returns>
        private ReadSlot Rent()
        {
            var gate = _readerGate ?? throw new InvalidOperationException("Article index readers are not open.");
            gate.Wait();
            if (_freeReaders is null || !_freeReaders.TryPop(out var index) || _readers is null)
            {
                gate.Release();
                throw new InvalidOperationException("Article index read pool was empty after a successful wait.");
            }

            return _readers[index];
        }

        /// <summary>Returns a slot taken by <see cref="Rent"/>.</summary>
        /// <param name="index">Slot index.</param>
        private void Return(int index)
        {
            _freeReaders?.Push(index);
            _readerGate?.Release();
        }

        /// <summary>Disposes reader commands and connections.</summary>
        private void DisposeReaders()
        {
            if (_readers is null)
            {
                return;
            }

            foreach (var slot in _readers)
            {
                slot.Lookup.Dispose();
                slot.Oldest.Dispose();
                slot.Before.Dispose();
                slot.Lfu.Dispose();
                slot.Scan.Dispose();
                slot.Connection.Dispose();
            }

            _readers = null;
        }

        /// <summary>Disposes writer commands.</summary>
        private void DisposeWriterCommands()
        {
            _insert?.Dispose();
            _import?.Dispose();
            _touch?.Dispose();
            _exists?.Dispose();
            _countUpdate?.Dispose();
            _storedBytesUpdate?.Dispose();
            _evictDelete?.Dispose();
            _load?.Dispose();
            _insert = null;
            _import = null;
            _touch = null;
            _exists = null;
            _countUpdate = null;
            _storedBytesUpdate = null;
            _evictDelete = null;
            _load = null;
        }

        /// <summary>
        /// Executes SQL that may return a pragma row. The row is drained and discarded.
        /// </summary>
        /// <param name="connection">Open connection.</param>
        /// <param name="sql">Literal SQL. Callers do not interpolate values.</param>
        /// <param name="transaction">Optional transaction.</param>
        private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // Drain and discard any pragma / statement rows so the connection is idle.
            }
        }

        /// <summary>Executes a scalar pragma.</summary>
        /// <param name="connection">Open connection.</param>
        /// <param name="sql">Literal SQL.</param>
        /// <returns>The scalar value.</returns>
        private static object ExecuteScalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar() ?? throw new InvalidOperationException("SQLite returned no pragma value.");
        }

        /// <summary>Creates a prepared command.</summary>
        /// <param name="connection">Open connection.</param>
        /// <param name="sql">Literal SQL.</param>
        /// <returns>The prepared command. The caller owns it.</returns>
        private static SqliteCommand Prepare(SqliteConnection connection, string sql)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Prepare();
            return command;
        }

        /// <summary>Adds a 32-byte blob parameter.</summary>
        /// <param name="command">Prepared command.</param>
        /// <param name="name">Parameter name, including the <c>$</c> prefix.</param>
        private static void AddBlob(SqliteCommand command, string name)
        {
            var parameter = command.Parameters.Add(name, SqliteType.Blob);
            parameter.Size = ArticleIndexSettings.HashLength;
        }

        /// <summary>One read-only connection and its prepared statements.</summary>
        /// <param name="Index">Pool index.</param>
        /// <param name="Connection">Read-only connection.</param>
        /// <param name="Lookup">Primary-key lookup.</param>
        /// <param name="Oldest">Oldest rows.</param>
        /// <param name="Before">Rows at or before an access time.</param>
        /// <param name="Lfu">Least-frequently-used rows.</param>
        /// <param name="Scan">Hash and spool scan.</param>
        /// <param name="Hash">Reusable 32-byte buffer. It is not shared with another slot.</param>
        private readonly record struct ReadSlot(
            int Index,
            SqliteConnection Connection,
            SqliteCommand Lookup,
            SqliteCommand Oldest,
            SqliteCommand Before,
            SqliteCommand Lfu,
            SqliteCommand Scan,
            byte[] Hash);
    }

    /// <summary>Literal SQL for the article index. Values are bound, not interpolated.</summary>
    internal static class ArticleIndexSql
    {
        /// <summary>Version, article-count, and stored-byte row.</summary>
        internal const string CreateMeta =
            """
            CREATE TABLE IF NOT EXISTS ArticleIndexMeta (
                Id INTEGER NOT NULL CONSTRAINT PK_ArticleIndexMeta PRIMARY KEY CHECK (Id = 1),
                SchemaVersion INTEGER NOT NULL,
                ArticleCount INTEGER NOT NULL CHECK (ArticleCount >= 0),
                StoredBytes INTEGER NOT NULL DEFAULT 0 CHECK (StoredBytes >= 0)
            ) WITHOUT ROWID;
            """;

        /// <summary>Article rows keyed by the 32-byte BLAKE3 digest (schema version 3 shape).</summary>
        /// <remarks>
        /// <c>CREATE TABLE IF NOT EXISTS</c> leaves a pre-existing version-2 table unchanged;
        /// <see cref="ArticleIndexDatabase"/> then adds missing columns via <c>ALTER TABLE</c>.
        /// </remarks>
        internal const string CreateArticles =
            """
            CREATE TABLE IF NOT EXISTS ArticleIndex (
                MessageIdHash BLOB NOT NULL CONSTRAINT PK_ArticleIndex PRIMARY KEY,
                ReceivedAt INTEGER NOT NULL,
                LastAccessedAt INTEGER NOT NULL,
                AccessCount INTEGER NOT NULL DEFAULT 1 CHECK (AccessCount >= 1),
                ArtSizeBytes INTEGER NOT NULL DEFAULT 0 CHECK (ArtSizeBytes >= 0),
                CONSTRAINT CK_ArticleIndex_HashLength CHECK (length(MessageIdHash) = 32),
                CONSTRAINT CK_ArticleIndex_Received CHECK (ReceivedAt >= 0),
                CONSTRAINT CK_ArticleIndex_Accessed CHECK (LastAccessedAt >= 0)
            ) WITHOUT ROWID;
            """;

        /// <summary>LFU eviction ordering index.</summary>
        internal const string CreateLfuIndex =
            """
            CREATE INDEX IF NOT EXISTS IX_ArticleIndex_Lfu
            ON ArticleIndex (AccessCount, LastAccessedAt, MessageIdHash);
            """;

        internal const string DropLastAccessedIndex =
            """
            DROP INDEX IF EXISTS IX_ArticleIndex_LastAccessedAt;
            """;

        internal const string AddMetaStoredBytes =
            """
            ALTER TABLE ArticleIndexMeta ADD COLUMN StoredBytes INTEGER NOT NULL DEFAULT 0;
            """;

        internal const string AddAccessCount =
            """
            ALTER TABLE ArticleIndex ADD COLUMN AccessCount INTEGER NOT NULL DEFAULT 1;
            """;

        internal const string AddArtSizeBytes =
            """
            ALTER TABLE ArticleIndex ADD COLUMN ArtSizeBytes INTEGER NOT NULL DEFAULT 0;
            """;

        internal const string SetSchemaVersion3 =
            """
            UPDATE ArticleIndexMeta SET SchemaVersion = 3 WHERE Id = 1;
            """;

        /// <summary>Inserts schema version 3 when the metadata row is absent.</summary>
        internal const string InsertMeta =
            """
            INSERT INTO ArticleIndexMeta (Id, SchemaVersion, ArticleCount, StoredBytes)
            VALUES (1, 3, 0, 0)
            ON CONFLICT(Id) DO NOTHING;
            """;

        /// <summary>Reads the schema version, article count, and stored-byte total.</summary>
        internal const string ReadMeta =
            """
            SELECT SchemaVersion, ArticleCount, StoredBytes FROM ArticleIndexMeta WHERE Id = 1;
            """;

        /// <summary>Inserts a new article. A conflict keeps the original row.</summary>
        internal const string Insert =
            """
            INSERT INTO ArticleIndex (MessageIdHash, ReceivedAt, LastAccessedAt, AccessCount, ArtSizeBytes)
            VALUES ($hash, $received, $received, 1, $artSize)
            ON CONFLICT(MessageIdHash) DO UPDATE SET
                ReceivedAt = ArticleIndex.ReceivedAt,
                LastAccessedAt = ArticleIndex.LastAccessedAt,
                AccessCount = ArticleIndex.AccessCount,
                ArtSizeBytes = ArticleIndex.ArtSizeBytes;
            """;

        /// <summary>Inserts a migrated row with its original receipt and access times.</summary>
        internal const string Import =
            """
            INSERT INTO ArticleIndex (MessageIdHash, ReceivedAt, LastAccessedAt, AccessCount, ArtSizeBytes)
            VALUES ($hash, $received, $accessed, 1, 0)
            ON CONFLICT(MessageIdHash) DO NOTHING;
            """;

        /// <summary>Increments access count and moves the access time forward when newer.</summary>
        internal const string Touch =
            """
            UPDATE ArticleIndex
            SET AccessCount = AccessCount + $delta,
                LastAccessedAt = CASE WHEN LastAccessedAt < $accessed THEN $accessed ELSE LastAccessedAt END
            WHERE MessageIdHash = $hash;
            """;

        /// <summary>Distinguishes a missing touch from an unchanged row.</summary>
        internal const string Exists =
            """
            SELECT 1 FROM ArticleIndex WHERE MessageIdHash = $hash;
            """;

        /// <summary>Keeps the metadata count in the same transaction as the inserts it describes.</summary>
        internal const string AddCount =
            """
            UPDATE ArticleIndexMeta SET ArticleCount = ArticleCount + $delta WHERE Id = 1;
            """;

        /// <summary>Adjusts the metadata stored-byte total in the same transaction as row changes.</summary>
        internal const string AddStoredBytes =
            """
            UPDATE ArticleIndexMeta SET StoredBytes = StoredBytes + $delta WHERE Id = 1;
            """;

        /// <summary>Deletes one evicted row and returns its indexed size.</summary>
        internal const string EvictDelete =
            """
            DELETE FROM ArticleIndex
            WHERE MessageIdHash = $hash
            RETURNING ArtSizeBytes;
            """;

        /// <summary>Startup membership scan. No other columns are selected.</summary>
        internal const string LoadHashes =
            """
            SELECT MessageIdHash FROM ArticleIndex;
            """;

        /// <summary>Primary-key location lookup.</summary>
        internal const string Lookup =
            """
            SELECT ReceivedAt, LastAccessedAt, AccessCount, ArtSizeBytes
            FROM ArticleIndex
            WHERE MessageIdHash = $hash;
            """;

        /// <summary>Oldest committed rows.</summary>
        internal const string Oldest =
            """
            SELECT MessageIdHash, ReceivedAt, LastAccessedAt, AccessCount, ArtSizeBytes
            FROM ArticleIndex
            ORDER BY LastAccessedAt, MessageIdHash
            LIMIT $limit;
            """;

        /// <summary>Rows whose access time is at or before a threshold, oldest first.</summary>
        internal const string AccessedBefore =
            """
            SELECT MessageIdHash, ReceivedAt, LastAccessedAt, AccessCount, ArtSizeBytes
            FROM ArticleIndex
            WHERE LastAccessedAt <= $threshold
            ORDER BY LastAccessedAt, MessageIdHash
            LIMIT $limit;
            """;

        /// <summary>LFU rows with recency and hash tie-breakers.</summary>
        internal const string LeastFrequentlyUsed =
            """
            SELECT MessageIdHash, ReceivedAt, LastAccessedAt, AccessCount, ArtSizeBytes
            FROM ArticleIndex
            ORDER BY AccessCount, LastAccessedAt, MessageIdHash
            LIMIT $limit;
            """;

        /// <summary>Hash and spool directory for the stale-file probe.</summary>
        internal const string ScanLocations =
            """
            SELECT MessageIdHash FROM ArticleIndex;
            """;
    }
}
