using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.NntpDb;

namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// Loads the published Transit catalogue from NntpDB and replaces the process snapshot.
    /// </summary>
    /// <remarks>
    /// Initial load failure prevents <c>RUNNING</c>. A failed refresh keeps the last snapshot.
    /// The same <c>publication_id</c> does not replace the snapshot. Send configuration is stored
    /// for a later feeder and does not open outbound connections. Passwords are not logged.
    /// </remarks>
    internal sealed class TransitCatalogueService : IApplicationService, IAsyncDisposable
    {
        /// <summary>Refresh interval after a successful initial load.</summary>
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

        private readonly NntpDbService _nntpDb;
        private readonly TransitConfigurationStore _store;
        private readonly IOptionsMonitor<NntpdOptions> _nntpdOptions;
        private readonly ILogger<TransitCatalogueService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _runCts = new();
        private IDisposable? _optionsSubscription;
        private Task? _execution;
        private int _started;
        private int _refreshing;
        private int _disposed;

        /// <summary>Initializes a production catalogue service.</summary>
        public TransitCatalogueService(
            NntpDbService nntpDb,
            TransitConfigurationStore store,
            IOptionsMonitor<NntpdOptions> nntpdOptions,
            ILogger<TransitCatalogueService> logger)
            : this(nntpDb, store, nntpdOptions, logger, TimeProvider.System, RefreshInterval)
        {
        }

        /// <summary>Initializes a service with an explicit clock and interval (tests).</summary>
        internal TransitCatalogueService(
            NntpDbService nntpDb,
            TransitConfigurationStore store,
            IOptionsMonitor<NntpdOptions> nntpdOptions,
            ILogger<TransitCatalogueService> logger,
            TimeProvider timeProvider,
            TimeSpan interval)
        {
            ArgumentNullException.ThrowIfNull(nntpDb);
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(nntpdOptions);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            _nntpDb = nntpDb;
            _store = store;
            _nntpdOptions = nntpdOptions;
            _logger = logger;
            _timeProvider = timeProvider;
            _interval = interval;
        }

        /// <inheritdoc />
        public string Name => "TransitCatalogue";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            try
            {
                var snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
                Publish(snapshot);
                _optionsSubscription = _nntpdOptions.OnChange(OnOptionsChanged);
                _execution = RunAsync(_runCts.Token);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    TransitLogMessages.CatalogueInitialLoadFailed(_logger, ex);
                }

                Interlocked.Exchange(ref _started, 0);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
            if (_execution is not null)
            {
                try
                {
                    await _execution.ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _optionsSubscription?.Dispose();
            await _runCts.CancelAsync().ConfigureAwait(false);
            _runCts.Dispose();
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
            }
        }

        private async Task TryRefreshAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
                Publish(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                TransitLogMessages.CatalogueRefreshFailed(_logger, ex);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        }

        private async ValueTask<TransitConfigurationSnapshot> LoadAsync(CancellationToken cancellationToken)
        {
            await using var connection = await NntpDbConnections.OpenAsync(_nntpDb, cancellationToken).ConfigureAwait(false);
            var record = await connection.QueryTransitCatalogueAsync(cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                throw new InvalidOperationException(
                    "nntptransitcurrent is missing policy_id = 1. NNTPD will not invent a local Transit catalogue.");
            }

            return TransitCatalogueCompiler.Compile(record);
        }

        private void Publish(TransitConfigurationSnapshot snapshot)
        {
            ApplyGlobalFlags(_nntpdOptions.CurrentValue, snapshot);
            if (_store.Current.PublicationId == snapshot.PublicationId && _store.Current.PublicationId != 0)
            {
                return;
            }

            _store.Replace(snapshot);
            TransitLogMessages.CataloguePublished(_logger, snapshot.PublicationId, snapshot.Peers.Count);
        }

        private void OnOptionsChanged(NntpdOptions options, string? name) =>
            ApplyGlobalFlags(options, _store.Current);

        private static void ApplyGlobalFlags(NntpdOptions options, TransitConfigurationSnapshot snapshot)
        {
            if (snapshot.PublicationId == 0)
            {
                return;
            }

            options.Transit.WantTrash = snapshot.WantTrash;
            options.Transit.LogTrash = snapshot.LogTrash;
        }
    }
}
