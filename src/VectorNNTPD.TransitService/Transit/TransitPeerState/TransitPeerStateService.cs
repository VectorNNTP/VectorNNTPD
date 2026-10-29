using VectorNNTPD.Common.Core;

namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>
    /// Periodic distributed Transit inbound-ownership maintenance.
    /// </summary>
    /// <remarks>
    /// This service is a scheduler. It does not accept connections or keep its own
    /// connection registry. Every renewal interval it asks
    /// <see cref="ITransitPeerStateLeaseManager"/> to renew currently active local
    /// ownership. Renewal is process/node liveness, not connection activity: idle
    /// Transit connections continue to consume cluster capacity while this process
    /// can renew. <see cref="StopAsync"/> stops the loop and then releases leftover
    /// incarnation ownership. Listeners are registered after this service so they
    /// stop first and admitted connections can finalize before renewal ends.
    /// Process crash is detected by lease expiry after renewals stop.
    /// </remarks>
    public sealed class TransitPeerStateService : IApplicationService
    {
        private readonly ITransitPeerStateLeaseManager _leases;
        private readonly ILogger<TransitPeerStateService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _runCts = new();
        private Task? _execution;
        private int _started;

        /// <summary>Initializes a new instance of the <see cref="TransitPeerStateService"/> class.</summary>
        public TransitPeerStateService(
            ITransitPeerStateLeaseManager leases,
            ILogger<TransitPeerStateService> logger)
            : this(leases, logger, TimeProvider.System, TransitPeerStateDefaults.RenewalPeriod)
        {
        }

        /// <summary>Initializes a new instance with an explicit interval (tests).</summary>
        internal TransitPeerStateService(
            ITransitPeerStateLeaseManager leases,
            ILogger<TransitPeerStateService> logger,
            TimeProvider timeProvider,
            TimeSpan interval)
        {
            ArgumentNullException.ThrowIfNull(leases);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            _leases = leases;
            _logger = logger;
            _timeProvider = timeProvider;
            _interval = interval;
        }

        /// <inheritdoc />
        public string Name => "TransitPeerState";

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
            if (execution is not null)
            {
                try
                {
                    await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
            }

            try
            {
                await _leases.ReleaseAllOwnershipAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            TransitPeerStateLogMessages.TransitPeerRenewalStarted(_logger, _interval);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_interval, _timeProvider, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await _leases.RenewLeasesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ex)
                    {
                        TransitPeerStateLogMessages.TransitPeerRenewalPassFailed(_logger, ex);
                    }
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }

            TransitPeerStateLogMessages.TransitPeerRenewalStopped(_logger);
        }
    }
}
