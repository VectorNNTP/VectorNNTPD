using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolShards;

namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>
    /// Starts one SQLite article index per registered spool, loads one membership set, and runs one writer per spool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Startup creates <c>DbDir</c> when it is missing and fails if that directory cannot be used.
    /// It does not substitute a spool directory. Membership is loaded from the open databases,
    /// and only then does <see cref="StartAsync"/> return. Later application services, including the
    /// accepted-article worker and the VATP listener, start after that.
    /// Startup does not walk article files or compare them with index rows.
    /// </para>
    /// <para>
    /// VATP RESULT is still sent when an article is queued, before the spool write and before this index.
    /// A committed index row is durable across process and power loss because each writer uses WAL and
    /// <c>synchronous=FULL</c>. An access update that is only queued is not durable until its batch commits.
    /// Eviction queries skip a row whose queued access time is newer than the committed time.
    /// </para>
    /// </remarks>
    internal sealed class ArticleIndexService : IArticleIndex, IApplicationService
    {
        private readonly StorageServerOptions _options;
        private readonly SegmentPoolCatalog _catalog;
        private readonly ILogger<ArticleIndexService> _logger;
        private readonly TimeProvider _time;
        private readonly ArticleIndexSettings _settings;
        private readonly ArticleIndexCounters _counters = new();
        private readonly ArticlePresenceIndex _presence = new();
        private readonly ArticleIngestionReservations _reservations = new();
        private readonly List<SpoolSlot> _spools = new();
        private readonly Dictionary<string, SpoolSlot> _bySpool = new(PathComparer);
        private readonly SemaphoreSlim _checkpointGate = new(1, 1);
        private CancellationTokenSource _runCts = new();
        private Task? _execution;
        private Task<ArticleIndexReconciliationReport>? _reconciliation;
        private int _started;
        private int _ready;
        private int _gateDisposed;

        /// <summary>Initializes the service from bound options.</summary>
        /// <param name="options">StorageServer options. <c>DbDir</c> is required after validation.</param>
        /// <param name="catalog">Validated spool roots. One database is opened for each root.</param>
        /// <param name="logger">Startup and writer logger.</param>
        /// <param name="time">Clock for reconciliation elapsed time. Each writer uses the same clock.</param>
        public ArticleIndexService(
            IOptions<StorageServerOptions> options,
            SegmentPoolCatalog catalog,
            ILogger<ArticleIndexService> logger,
            TimeProvider time)
            : this(options.Value, catalog, logger, time, settings: null)
        {
            ArgumentNullException.ThrowIfNull(options);
        }

        /// <summary>Initializes the service with explicit options and test settings.</summary>
        /// <param name="options">StorageServer options.</param>
        /// <param name="catalog">Validated spool roots.</param>
        /// <param name="logger">Startup and writer logger.</param>
        /// <param name="time">Clock.</param>
        /// <param name="settings">Null uses <see cref="ArticleIndexSettings"/> defaults.</param>
        internal ArticleIndexService(
            StorageServerOptions options,
            SegmentPoolCatalog catalog,
            ILogger<ArticleIndexService> logger,
            TimeProvider time,
            ArticleIndexSettings? settings)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(time);
            _options = options;
            _catalog = catalog;
            _logger = logger;
            _time = time;
            _settings = settings ?? new ArticleIndexSettings();
            DatabaseDirectory = string.Empty;
            DatabasePath = string.Empty;
        }

        /// <inheritdoc />
        public string Name => "ArticleIndex";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <inheritdoc />
        public bool IsReady => Volatile.Read(ref _ready) == 1;

        /// <inheritdoc />
        public string DatabaseDirectory { get; private set; }

        /// <inheritdoc />
        public string DatabasePath { get; private set; }

        /// <inheritdoc />
        public long PresenceCount => _presence.Count;

        /// <inheritdoc />
        public long PresenceEstimatedBytes => _presence.EstimatedBytes;

        /// <summary>
        /// Gets process counters. With one open spool this is also that spool's writer counters.
        /// With several spools, writer counters are available from <see cref="CountersFor"/>.
        /// </summary>
        public ArticleIndexCounters Counters => _spools.Count == 1 ? _spools[0].Counters : _counters;

        /// <inheritdoc />
        public Task<ArticleIndexReconciliationReport> Reconciliation =>
            _reconciliation ?? Task.FromResult(new ArticleIndexReconciliationReport(0, 0, 0, 0, 0, 0, 0));

        /// <summary>Gets the membership index. Tests use the growth counters.</summary>
        internal ArticlePresenceIndex Presence => _presence;

        /// <summary>Gets the schema version after a successful start, or zero before a database is open.</summary>
        internal int SchemaVersion => _spools.Count == 0 ? 0 : _spools[0].Database.SchemaVersion;

        /// <summary>Gets the SQLite page size after a successful start, or zero before a database is open.</summary>
        internal int PageSize => _spools.Count == 0 ? 0 : _spools[0].Database.PageSize;

        /// <summary>Gets writer counters for one registered spool.</summary>
        /// <param name="spoolDirectory">Registered spool root.</param>
        /// <returns>That spool's counters.</returns>
        internal ArticleIndexCounters CountersFor(string spoolDirectory) => Require(spoolDirectory).Counters;

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                OpenDirectory();
                var bindings = ArticleSpoolRegistry.Resolve(DatabaseDirectory, Roots());
                var budget = ArticleIndexCacheBudget.ForSpoolCount(bindings.Count);
                long expected = 0;
                foreach (var binding in bindings)
                {
                    var database = new ArticleIndexDatabase(binding.DatabasePath, binding.SpoolDirectory, _settings, budget);
                    database.Initialize(cancellationToken);
                    var counters = bindings.Count == 1 ? _counters : new ArticleIndexCounters();
                    _spools.Add(new SpoolSlot(binding, database, counters, new ArticleAccessCoalescer()));
                    _bySpool[binding.SpoolDirectory] = _spools[^1];
                    expected += database.ArticleCount;
                }

                var databases = new Dictionary<string, ArticleIndexDatabase>(_spools.Count, StringComparer.Ordinal);
                foreach (var spool in _spools)
                {
                    databases.Add(spool.Binding.DatabasePath, spool.Database);
                }

                var migrated = ArticleIndexLegacyMigration.MigrateIfPresent(DatabaseDirectory, bindings, databases, cancellationToken);
                if (migrated > 0)
                {
                    ArticleIndexLogMessages.LegacyMigrated(_logger, migrated, bindings.Count, DatabaseDirectory);
                }

                var loadStarted = _time.GetTimestamp();
                _presence.Reserve(expected);
                foreach (var spool in _spools)
                {
                    ArticlePayloadProbe.RequireReadable(spool.Binding.SpoolDirectory);
                }

                long loaded = 0;
                long duplicates = 0;
                var sample = new List<string>(8);
                foreach (var spool in _spools)
                {
                    loaded += spool.Database.LoadPresence(
                        _presence,
                        articleId =>
                        {
                            duplicates++;
                            if (sample.Count < 8)
                            {
                                sample.Add(spool.Binding.Id + ":" + articleId.ToLowerHexString());
                            }
                        },
                        cancellationToken);
                }

                _counters.NoteStartup(loaded, _time.GetElapsedTime(loadStarted));
                if (duplicates > 0)
                {
                    ArticleIndexLogMessages.CrossSpoolDuplicates(_logger, duplicates, string.Join(",", sample));
                }

                var tasks = new Task[_spools.Count];
                for (var i = 0; i < _spools.Count; i++)
                {
                    var spool = _spools[i];
                    var writer = new ArticleIndexWriter(
                        spool.Database,
                        _presence,
                        _settings,
                        spool.Counters,
                        spool.Coalescer,
                        _logger,
                        _time,
                        spool.Binding.SpoolDirectory,
                        _checkpointGate);
                    spool.Writer = writer;
                    tasks[i] = writer.RunAsync(_runCts.Token);
                }

                _execution = tasks.Length == 0 ? ObserveIdleAsync(_runCts.Token) : Task.WhenAll(tasks);
                _ = _execution.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                if (_spools.Count == 1)
                {
                    DatabasePath = _spools[0].Binding.DatabasePath;
                }

                Volatile.Write(ref _ready, 1);
                _reconciliation = Task.FromResult(new ArticleIndexReconciliationReport(0, 0, 0, 0, 0, 0, 0));
                ArticleIndexLogMessages.Ready(
                    _logger,
                    DatabaseDirectory,
                    SchemaVersion,
                    PageSize,
                    _presence.Count,
                    _counters.StartupMilliseconds,
                    _presence.EstimatedBytes);
                foreach (var spool in _spools)
                {
                    ArticleIndexLogMessages.SpoolReady(
                        _logger,
                        spool.Binding.Id,
                        spool.Binding.DatabasePath,
                        spool.Database.SchemaVersion,
                        spool.Database.PageSize,
                        spool.Database.ArticleCount);
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _ready, 0);
                ArticleIndexLogMessages.DirectoryFailed(_logger, ex, string.IsNullOrEmpty(DatabaseDirectory) ? _options.DbDir : DatabaseDirectory);
                await StopWritersAsync().ConfigureAwait(false);
                DisposeDatabases();
                _spools.Clear();
                _bySpool.Clear();
                _execution = null;
                var retired = _runCts;
                _runCts = new CancellationTokenSource();
                retired.Dispose();
                Interlocked.Exchange(ref _started, 0);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_execution is null && _spools.Count == 0)
            {
                return;
            }

            await StopWritersAsync().ConfigureAwait(false);
            foreach (var spool in _spools)
            {
                if (spool.Writer is null)
                {
                    continue;
                }

                try
                {
                    spool.Writer.Checkpoint(truncate: true);
                }
                catch (Exception ex)
                {
                    ArticleIndexLogMessages.BatchFailed(_logger, ex, 0, 0);
                }
            }

            DisposeDatabases();
            if (Interlocked.Exchange(ref _gateDisposed, 1) == 0)
            {
                _checkpointGate.Dispose();
            }
        }

        /// <inheritdoc />
        public bool Contains(ArticleId articleId)
        {
            ThrowIfNotReady();
            var found = _presence.Contains(articleId);
            if (found)
            {
                _counters.NoteHit();
            }
            else
            {
                _counters.NoteMiss();
            }

            return found;
        }

        /// <inheritdoc />
        public ValueTask<ArticleIngestionHold> ReserveIngestionAsync(ArticleId articleId, CancellationToken cancellationToken)
        {
            ThrowIfNotReady();
            return _reservations.ReserveAsync(articleId, cancellationToken);
        }

        /// <inheritdoc />
        public void ReleaseIngestion(ArticleId articleId)
        {
            ThrowIfNotReady();
            _reservations.Release(articleId);
        }

        /// <inheritdoc />
        public ValueTask<ArticleIndexInsertResult> InsertStoredAsync(
            ArticleId articleId,
            string spoolDirectory,
            long receivedAtUnixMilliseconds,
            CancellationToken cancellationToken)
        {
            ThrowIfNotReady();
            return Require(spoolDirectory).WriterOrThrow().InsertAsync(articleId, spoolDirectory, receivedAtUnixMilliseconds, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<ArticleIndexTouchResult> TouchAsync(
            ArticleId articleId,
            string spoolDirectory,
            long accessedAtUnixMilliseconds,
            CancellationToken cancellationToken)
        {
            ThrowIfNotReady();
            return Require(spoolDirectory).WriterOrThrow().TouchAsync(articleId, accessedAtUnixMilliseconds, cancellationToken);
        }

        /// <inheritdoc />
        public bool TryGetIndexedRecord(ArticleId articleId, out ArticleIndexRecord record)
        {
            ThrowIfNotReady();
            foreach (var spool in _spools)
            {
                if (!FileExists(spool.Binding.SpoolDirectory, articleId))
                {
                    continue;
                }

                if (spool.Database.TryRead(articleId, out record, spool.Counters))
                {
                    return true;
                }
            }

            foreach (var spool in _spools)
            {
                if (spool.Database.TryRead(articleId, out record, spool.Counters))
                {
                    return true;
                }
            }

            record = default;
            return false;
        }

        /// <inheritdoc />
        public bool TryGetRetrievableArticle(ArticleId articleId, out string articlePath)
        {
            articlePath = string.Empty;
            if (!TryGetIndexedRecord(articleId, out var record))
            {
                return false;
            }

            try
            {
                var path = SpoolArticlePath.CombineArticle(record.SpoolDirectory, articleId);
                if (!File.Exists(path))
                {
                    return false;
                }

                articlePath = path;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }

        /// <inheritdoc />
        public int CopyOldest(string spoolDirectory, Span<ArticleIndexRecord> destination)
        {
            ThrowIfNotReady();
            var spool = Require(spoolDirectory);
            return spool.Database.CopyOldest(null, destination, spool.Counters, spool.Include);
        }

        /// <inheritdoc />
        public int CopyAccessedAtOrBefore(string spoolDirectory, long accessedAtOrBeforeUnixMilliseconds, Span<ArticleIndexRecord> destination)
        {
            ThrowIfNotReady();
            if (accessedAtOrBeforeUnixMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(accessedAtOrBeforeUnixMilliseconds));
            }

            var spool = Require(spoolDirectory);
            return spool.Database.CopyOldest(accessedAtOrBeforeUnixMilliseconds, destination, spool.Counters, spool.Include);
        }

        /// <inheritdoc />
        public string DatabasePathFor(string spoolDirectory) => Require(spoolDirectory).Binding.DatabasePath;

        /// <summary>Stops every writer and observes the execution task.</summary>
        private async Task StopWritersAsync()
        {
            foreach (var spool in _spools)
            {
                spool.Writer?.Complete();
            }

            if (_execution is null)
            {
                return;
            }

            try
            {
                await _runCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                await _execution.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ArticleIndexLogMessages.BatchFailed(_logger, ex, 0, 0);
            }
        }

        /// <summary>Gets channel reads owned by running writers and not yet consumed.</summary>
        internal int PendingReads
        {
            get
            {
                var count = 0;
                foreach (var spool in _spools)
                {
                    count += spool.Writer?.PendingReads ?? 0;
                }

                return count;
            }
        }

        /// <summary>Gets flush timers armed by running writers and not yet disposed.</summary>
        internal int ActiveIntervalWaits
        {
            get
            {
                var count = 0;
                foreach (var spool in _spools)
                {
                    count += spool.Writer?.ActiveIntervalWaits ?? 0;
                }

                return count;
            }
        }

        /// <summary>Faults every running writer channel. Test seam.</summary>
        /// <param name="exception">Exception the writer observes.</param>
        internal void FaultWritersForTests(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            foreach (var spool in _spools)
            {
                spool.Writer?.FaultForTests(exception);
            }
        }

        /// <summary>Gets or sets a test signal invoked when a reservation caller is about to wait.</summary>
        internal Action? OnReservationWaiting
        {
            get => _reservations.OnWaiting;
            set => _reservations.OnWaiting = value;
        }

        /// <summary>Finds a spool opened at startup.</summary>
        /// <param name="spoolDirectory">Caller-supplied spool root.</param>
        /// <returns>The open slot.</returns>
        private SpoolSlot Require(string spoolDirectory)
        {
            var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spoolDirectory));
            if (_bySpool.TryGetValue(key, out var spool) && spool.Writer is not null)
            {
                return spool;
            }

            throw new InvalidOperationException($"Spool '{spoolDirectory}' does not have an open article-index database.");
        }

        /// <summary>Rejects calls that would treat an unloaded cache as a miss.</summary>
        private void ThrowIfNotReady()
        {
            if (!IsReady)
            {
                throw new InvalidOperationException("Article index membership is not ready.");
            }
        }

        /// <summary>Resolves and creates <c>DbDir</c>. Failure names the resolved path and does not switch directories.</summary>
        private void OpenDirectory()
        {
            if (string.IsNullOrWhiteSpace(_options.DbDir))
            {
                throw new InvalidOperationException("StorageServer:DbDir is required. The article index does not fall back to a spool directory.");
            }

            string directory;
            try
            {
                directory = ApplicationLocalPath.ResolveApplicationLocalPath(_options.DbDir, AppContext.BaseDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidOperationException(
                    "StorageServer:DbDir is not a usable path. The article index does not fall back to a spool directory.",
                    ex);
            }

            DatabaseDirectory = directory;
            if (File.Exists(directory))
            {
                throw new InvalidOperationException(
                    $"StorageServer:DbDir '{directory}' is a file. Create a directory on NVMe. The article index does not fall back to a spool directory.");
            }

            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, ".write-probe");
                using (var stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                    stream.WriteByte(0);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"StorageServer:DbDir '{directory}' could not be created or written. Place it on a writable NVMe volume. The article index does not fall back to a spool directory.",
                    ex);
            }
        }

        /// <summary>Distinct canonical spool roots in catalog order.</summary>
        /// <returns>The roots to open. Empty when validation has not published a spool.</returns>
        private List<string> Roots()
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(PathComparer);
            foreach (var device in _catalog.Devices)
            {
                foreach (var path in device.PoolPaths)
                {
                    var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                    if (seen.Add(canonical))
                    {
                        roots.Add(canonical);
                    }
                }
            }

            return roots;
        }

        /// <summary>Disposes every open database.</summary>
        private void DisposeDatabases()
        {
            foreach (var spool in _spools)
            {
                spool.Database.Dispose();
            }
        }

        /// <summary>Returns whether the final article file is in <paramref name="spoolDirectory"/>.</summary>
        /// <param name="spoolDirectory">Registered spool root.</param>
        /// <param name="articleId">Canonical article identity.</param>
        /// <returns><see langword="false"/> when the path cannot be formed or the file is absent.</returns>
        private static bool FileExists(string spoolDirectory, ArticleId articleId)
        {
            try
            {
                return File.Exists(SpoolArticlePath.CombineArticle(spoolDirectory, articleId));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>Keeps the execution task incomplete until shutdown when no spool writer exists.</summary>
        /// <param name="cancellationToken">Completes the task when startup is cancelled or the service stops.</param>
        /// <returns>A task that finishes successfully after cancellation.</returns>
        private static async Task ObserveIdleAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>Path comparison for spool roots.</summary>
        private static StringComparer PathComparer =>
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>One open spool database and the writer that owns its mutations.</summary>
        private sealed class SpoolSlot
        {
            /// <summary>Initializes a slot before its writer starts.</summary>
            /// <param name="binding">Identity and paths.</param>
            /// <param name="database">Open database.</param>
            /// <param name="counters">Writer counters. The single-spool service shares the process counters.</param>
            /// <param name="coalescer">Pending access updates for this spool.</param>
            internal SpoolSlot(ArticleSpoolBinding binding, ArticleIndexDatabase database, ArticleIndexCounters counters, ArticleAccessCoalescer coalescer)
            {
                Binding = binding;
                Database = database;
                Counters = counters;
                Coalescer = coalescer;
            }

            /// <summary>Gets the spool identity and paths.</summary>
            internal ArticleSpoolBinding Binding { get; }

            /// <summary>Gets the open database.</summary>
            internal ArticleIndexDatabase Database { get; }

            /// <summary>Gets the writer counters.</summary>
            internal ArticleIndexCounters Counters { get; }

            /// <summary>Gets the access-update coalescer.</summary>
            internal ArticleAccessCoalescer Coalescer { get; }

            /// <summary>Gets or sets the writer. Null until it has been constructed.</summary>
            internal ArticleIndexWriter? Writer { get; set; }

            /// <summary>Returns the writer after startup.</summary>
            /// <returns>The spool writer.</returns>
            internal ArticleIndexWriter WriterOrThrow() =>
                Writer ?? throw new InvalidOperationException("Article index writer is not running.");

            /// <summary>Rejects a row whose queued access is newer than the committed timestamp.</summary>
            /// <param name="articleId">Candidate article.</param>
            /// <param name="accessedAt">Committed <c>LastAccessedAt</c>.</param>
            /// <returns><see langword="false"/> when a pending touch is newer.</returns>
            internal bool Include(ArticleId articleId, long accessedAt) =>
                !Coalescer.TryPeek(articleId, out var pending) || pending <= accessedAt;
        }
    }
}
