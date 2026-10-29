using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.ReaderService.NntpDb;

namespace VectorNNTPD.ReaderService.Newsgroups
{
    /// <summary>
    /// Loads the newsgroup catalogue from NntpDB, publishes an immutable snapshot,
    /// and refreshes it every five minutes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MySqlConnector owns physical connection pooling. Each refresh opens one logical
    /// connection through <see cref="NntpDbService"/>, consumes the result set, and
    /// disposes the connection before the snapshot is published. The snapshot retains
    /// only managed immutable data.
    /// </para>
    /// <para>
    /// Initial population runs during <see cref="StartAsync"/> and fails startup when
    /// the query cannot be completed. Subsequent refresh failures keep the last
    /// known-good snapshot. A refresh does not start while the previous refresh is
    /// still running (single loop).
    /// </para>
    /// </remarks>
    public sealed class NewsgroupCatalogueService : INewsgroupCatalogue, IApplicationService, IAsyncDisposable
    {
        /// <summary>Catalogue refresh interval after a successful initial load.</summary>
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

        private readonly NntpDbService _nntpDb;
        private readonly ILogger<NewsgroupCatalogueService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _runCts = new();
        private NewsgroupSnapshot? _current;
        private Task? _execution;
        private int _started;
        private int _refreshing;
        private int _disposed;

        /// <summary>Initializes a new instance of the <see cref="NewsgroupCatalogueService"/> class.</summary>
        public NewsgroupCatalogueService(
            NntpDbService nntpDb,
            ILogger<NewsgroupCatalogueService> logger)
            : this(nntpDb, logger, TimeProvider.System, RefreshInterval)
        {
        }

        /// <summary>Initializes a new instance with an explicit clock and interval (tests).</summary>
        internal NewsgroupCatalogueService(
            NntpDbService nntpDb,
            ILogger<NewsgroupCatalogueService> logger,
            TimeProvider timeProvider,
            TimeSpan interval)
        {
            ArgumentNullException.ThrowIfNull(nntpDb);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            _nntpDb = nntpDb;
            _logger = logger;
            _timeProvider = timeProvider;
            _interval = interval;
        }

        /// <inheritdoc />
        public string Name => "NewsgroupCatalogue";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <inheritdoc />
        public NewsgroupSnapshot Current
        {
            get
            {
                var snapshot = Volatile.Read(ref _current);
                return snapshot ?? throw new InvalidOperationException("Newsgroup catalogue has not been published.");
            }
        }

        /// <summary>Gets whether a snapshot has been published (tests).</summary>
        internal bool HasSnapshot => Volatile.Read(ref _current) is not null;

        /// <summary>Gets whether a refresh is currently running (tests).</summary>
        internal bool IsRefreshing => Volatile.Read(ref _refreshing) == 1;

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            NewsgroupCatalogueLogMessages.InitialLoadStarted(_logger);
            try
            {
                var snapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
                Publish(snapshot);
                NewsgroupCatalogueLogMessages.RefreshLoopStarted(_logger, _interval);
                _execution = RunAsync(_runCts.Token);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    NewsgroupCatalogueLogMessages.InitialLoadFailed(_logger, ex);
                }

                Interlocked.Exchange(ref _started, 0);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
            var execution = _execution;
            if (execution is null)
            {
                return;
            }

            try
            {
                await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
                // Expected on shutdown.
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _runCts.CancelAsync().ConfigureAwait(false);
            _runCts.Dispose();
        }

        /// <summary>Replaces the published snapshot (tests).</summary>
        internal void PublishForTests(NewsgroupSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            Publish(snapshot);
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_interval, _timeProvider, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await TryRefreshAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
                // Shutdown.
            }

            NewsgroupCatalogueLogMessages.RefreshLoopStopped(_logger);
        }

        private async Task TryRefreshAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var snapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
                Publish(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                NewsgroupCatalogueLogMessages.RefreshFailed(_logger, ex);
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        }

        private async Task<NewsgroupSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
        {
            await using var connection = await NntpDbConnections.OpenAsync(_nntpDb, cancellationToken).ConfigureAwait(false);
            var rows = await connection.QueryNewsgroupsAsync(cancellationToken).ConfigureAwait(false);
            return NewsgroupSnapshot.Create(rows);
        }

        private void Publish(NewsgroupSnapshot snapshot)
        {
            Interlocked.Exchange(ref _current, snapshot);
            NewsgroupCatalogueLogMessages.SnapshotPublished(_logger, snapshot.Groups.Count);
        }
    }
}
