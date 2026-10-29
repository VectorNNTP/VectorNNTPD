using VectorNNTPD.Common.Core;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;

namespace VectorNNTPD.ReaderService.SessionState
{
    /// <summary>
    /// Sole periodic scheduler for SessionState: lease renewal, source-address
    /// ownership, byte-quota reconciliation, and rate observation.
    /// </summary>
    /// <remarks>
    /// This service is a scheduler. It does not accept connections, authenticate clients,
    /// or keep its own session registry. Every renewal interval it commits account-wide
    /// MySQL remaining-quota consumes, then asks <see cref="ISessionStateLeaseManager"/>
    /// to renew currently active local ownership. When an account has both distributed
    /// ownership and a committed byte batch, renewal and Redis APPLY share one EVAL.
    /// Accounts with pending bytes but no SessionState ownership receive APPLY-only.
    /// Each account with distributed ownership is one Redis operation covering session
    /// count, every locally owned source IP, and byte state when present. Renewal is
    /// node liveness, not client activity: idle authenticated sessions keep consuming
    /// cluster capacity while this process can renew. <see cref="StopAsync"/> stops the
    /// loop, performs a bounded two-pass final byte reconcile, then releases leftover incarnation
    /// ownership. Listeners are registered after this service so they stop first and
    /// sessions can finalize before renewal ends. Process crash is detected by lease
    /// expiry after renewals stop. Unreconciled process-memory pending bytes are lost.
    /// </remarks>
    public sealed class SessionStateService : IApplicationService
    {
        private readonly ISessionStateLeaseManager _leases;
        private readonly IAccountByteAccountant _bytes;
        private readonly ILogger<SessionStateService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _runCts = new();
        private Task? _execution;
        private int _started;

        /// <summary>Initializes a new instance of the <see cref="SessionStateService"/> class.</summary>
        public SessionStateService(
            ISessionStateLeaseManager leases,
            IAccountByteAccountant bytes,
            ILogger<SessionStateService> logger)
            : this(leases, bytes, logger, TimeProvider.System, SessionStateDefaults.RenewalPeriod)
        {
        }

        /// <summary>Initializes a new instance with an explicit interval (tests).</summary>
        internal SessionStateService(
            ISessionStateLeaseManager leases,
            ILogger<SessionStateService> logger,
            TimeProvider timeProvider,
            TimeSpan interval)
            : this(leases, NullAccountByteAccountant.Instance, logger, timeProvider, interval)
        {
        }

        /// <summary>Initializes a new instance with byte accounting and an explicit interval (tests).</summary>
        internal SessionStateService(
            ISessionStateLeaseManager leases,
            IAccountByteAccountant bytes,
            ILogger<SessionStateService> logger,
            TimeProvider timeProvider,
            TimeSpan interval)
        {
            ArgumentNullException.ThrowIfNull(leases);
            ArgumentNullException.ThrowIfNull(bytes);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            _leases = leases;
            _bytes = bytes;
            _logger = logger;
            _timeProvider = timeProvider;
            _interval = interval;
        }

        /// <inheritdoc />
        public string Name => "SessionState";

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

            AccountByteLogMessages.FinalReconciliationStarted(_logger);
            try
            {
                await _bytes.ReconcileAsync(cancellationToken).ConfigureAwait(false);
                // Pending observed while a MySQL-committed batch was still awaiting APPLY
                // stays in Pending. A second pass consumes it after the first APPLY completes.
                await _bytes.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                AccountByteLogMessages.FinalReconciliationFailed(_logger, ex);
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
            SessionStateLogMessages.SessionStateRenewalStarted(_logger, _interval);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_interval, _timeProvider, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await ReconcileOnceAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ex)
                    {
                        SessionStateLogMessages.SessionStateRenewalPassFailed(_logger, ex);
                    }
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }

            SessionStateLogMessages.SessionStateRenewalStopped(_logger);
        }

        private async Task ReconcileOnceAsync(CancellationToken cancellationToken)
        {
            await _bytes.CommitDurableAsync(cancellationToken).ConfigureAwait(false);
            await _leases.RenewLeasesAsync(cancellationToken).ConfigureAwait(false);
            await _bytes.ApplyCommittedAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
