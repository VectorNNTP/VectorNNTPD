using VectorNNTPD.Common.Core;

namespace VectorNNTPD.TransitService.Moderation
{
    /// <summary>
    /// Loads the moderator catalogue from NntpDB, publishes an immutable snapshot,
    /// and refreshes it on the same interval as the newsgroup catalogue.
    /// </summary>
    /// <remarks>
    /// Initial population runs during <see cref="StartAsync"/> and fails startup when
    /// the query cannot be completed. Subsequent refresh failures keep the last
    /// known-good snapshot. POST captures <see cref="Current"/> once and never queries MySQL.
    /// </remarks>
    public sealed class ModeratorCatalogueService : IModeratorCatalogue, IModeratorAuthorization, IApplicationService, IAsyncDisposable
    {
        /// <summary>Catalogue refresh interval after a successful initial load.</summary>
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

        private readonly INntpModeratorRepository _repository;
        private readonly ILogger<ModeratorCatalogueService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _runCts = new();
        private ModeratorSnapshot? _current;
        private Task? _execution;
        private int _started;
        private int _refreshing;
        private int _disposed;

        /// <summary>Initializes a new instance of the <see cref="ModeratorCatalogueService"/> class.</summary>
        public ModeratorCatalogueService(
            INntpModeratorRepository repository,
            ILogger<ModeratorCatalogueService> logger)
            : this(repository, logger, TimeProvider.System, RefreshInterval)
        {
        }

        /// <summary>Initializes a new instance with an explicit clock and interval (tests).</summary>
        internal ModeratorCatalogueService(
            INntpModeratorRepository repository,
            ILogger<ModeratorCatalogueService> logger,
            TimeProvider timeProvider,
            TimeSpan interval)
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            _repository = repository;
            _logger = logger;
            _timeProvider = timeProvider;
            _interval = interval;
        }

        /// <inheritdoc />
        public string Name => "ModeratorCatalogue";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <inheritdoc />
        public ModeratorSnapshot Current
        {
            get
            {
                var snapshot = Volatile.Read(ref _current);
                if (snapshot is null)
                {
                    throw new InvalidOperationException("Moderator catalogue has not been published.");
                }

                return snapshot;
            }
        }

        /// <summary>Gets whether a snapshot has been published (tests).</summary>
        internal bool HasSnapshot => Volatile.Read(ref _current) is not null;

        /// <summary>Gets whether a refresh is currently running (tests).</summary>
        internal bool IsRefreshing => Volatile.Read(ref _refreshing) == 1;

        /// <inheritdoc />
        public bool IsAuthenticatedModerator(string? authenticatedUsername) =>
            Current.IsAuthenticatedModerator(authenticatedUsername);

        /// <inheritdoc />
        public bool TryResolve(ReadOnlySpan<byte> newsgroup, out ModeratorIdentity identity) =>
            Current.TryResolve(newsgroup, out identity);

        /// <inheritdoc />
        public bool CanApprove(
            string? authenticatedUsername,
            ReadOnlySpan<byte> approvedIdentity,
            ReadOnlySpan<byte> newsgroup) =>
            Current.CanApprove(authenticatedUsername, approvedIdentity, newsgroup);

        /// <inheritdoc />
        public bool TryAuthorizeApproval(
            string? authenticatedUsername,
            ReadOnlySpan<string> approvedIdentities,
            ReadOnlySpan<string> moderatedGroups,
            out string? failureDetail) =>
            Current.TryAuthorizeApproval(authenticatedUsername, approvedIdentities, moderatedGroups, out failureDetail);

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            ModeratorCatalogueLogMessages.InitialLoadStarted(_logger);
            try
            {
                var snapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
                Publish(snapshot);
                ModeratorCatalogueLogMessages.RefreshLoopStarted(_logger, _interval);
                _execution = RunAsync(_runCts.Token);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    ModeratorCatalogueLogMessages.InitialLoadFailed(_logger, ex);
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
        internal void PublishForTests(ModeratorSnapshot snapshot)
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

            ModeratorCatalogueLogMessages.RefreshLoopStopped(_logger);
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
                ModeratorCatalogueLogMessages.RefreshFailed(_logger, ex);
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        }

        private async Task<ModeratorSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
        {
            var rows = await _repository.GetEnabledAsync(cancellationToken).ConfigureAwait(false);
            return ModeratorSnapshot.Create(rows);
        }

        private void Publish(ModeratorSnapshot snapshot)
        {
            Interlocked.Exchange(ref _current, snapshot);
            ModeratorCatalogueLogMessages.SnapshotPublished(_logger, snapshot.Count);
        }
    }
}
