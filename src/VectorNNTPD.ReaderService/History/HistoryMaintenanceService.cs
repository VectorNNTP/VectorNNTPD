using VectorNNTPD.Common.Core;

namespace VectorNNTPD.ReaderService.History
{
    /// <summary>
    /// Background worker that removes expired local HistoryDB entries off the CHECK path.
    /// </summary>
    public sealed class HistoryMaintenanceService : IApplicationService
    {
        /// <summary>Default interval between dictionary maintenance passes.</summary>
        internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

        private readonly HistoryDb _history;
        private readonly ILogger<HistoryMaintenanceService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _runCts = new();
        private Task? _execution;
        private int _started;

        /// <summary>Initializes a new instance of the <see cref="HistoryMaintenanceService"/> class.</summary>
        public HistoryMaintenanceService(
            HistoryDb history,
            ILogger<HistoryMaintenanceService> logger)
            : this(history, logger, TimeProvider.System, DefaultInterval)
        {
        }

        /// <summary>Initializes a new instance with an explicit interval (tests).</summary>
        internal HistoryMaintenanceService(
            HistoryDb history,
            ILogger<HistoryMaintenanceService> logger,
            TimeProvider timeProvider,
            TimeSpan interval)
        {
            ArgumentNullException.ThrowIfNull(history);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            _history = history;
            _logger = logger;
            _timeProvider = timeProvider;
            _interval = interval;
        }

        /// <inheritdoc />
        public string Name => "HistoryMaintenance";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return Task.CompletedTask;
            }

            _execution = Task.Run(() => RunAsync(_runCts.Token), CancellationToken.None);
            return Task.CompletedTask;
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                HistoryLogMessages.MaintenanceStopped(_logger);
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            HistoryLogMessages.MaintenanceStarted(_logger, _interval);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_interval, _timeProvider, cancellationToken).ConfigureAwait(false);
                    _ = _history.Maintain();
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }

            HistoryLogMessages.MaintenanceStopped(_logger);
        }
    }
}
