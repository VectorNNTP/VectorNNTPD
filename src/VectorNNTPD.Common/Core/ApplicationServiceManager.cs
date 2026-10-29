using System.Diagnostics;
using VectorNNTPD.Common.Logging;

namespace VectorNNTPD.Common.Core
{
    /// <summary>
    /// Coordinates deterministic start/stop of registered <see cref="IApplicationService"/> instances.
    /// </summary>
    /// <remarks>
    /// Services start in registration order and stop in reverse order. On startup failure, already-started
    /// services are stopped (rollback). Concurrent lifecycle execution is rejected.
    /// </remarks>
    internal sealed class ApplicationServiceManager
    {
        /// <summary>Registered services in startup order. The sequence is fixed for the manager lifetime.</summary>
        private readonly IReadOnlyList<IApplicationService> _services;

        /// <summary>Graceful-shutdown budget applied to stop and startup rollback.</summary>
        private readonly IApplicationLifecycleOptions _options;

        /// <summary>Service-manager diagnostics.</summary>
        private readonly ILogger<ApplicationServiceManager> _logger;

        /// <summary>Serializes the body of <see cref="StartAsync"/> and <see cref="StopAsync"/> after the busy flag is taken.</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>
        /// Services whose <see cref="IApplicationService.StartAsync"/> succeeded and whose stop attempt has not yet finished.
        /// Guarded by <see cref="_startedSync"/>.
        /// </summary>
        private readonly List<IApplicationService> _started = [];

        /// <summary>Guards <see cref="_started"/>.</summary>
        private readonly Lock _startedSync = new();

        /// <summary>
        /// Non-zero while <see cref="StartAsync"/> or <see cref="StopAsync"/> is in progress.
        /// A second call throws <see cref="InvalidOperationException"/> instead of queueing.
        /// </summary>
        private int _lifecycleBusy;

        /// <summary>Cancels execution watches during stop or rollback. <see langword="null"/> when monitoring is not active.</summary>
        private CancellationTokenSource? _executionCts;

        /// <summary>In-flight <see cref="MonitorExecutionsAsync"/> task, or <see langword="null"/> when no service exposes <see cref="IApplicationService.Execution"/>.</summary>
        private Task? _executionMonitor;

        /// <summary>
        /// Occurs when a started service's <see cref="IApplicationService.Execution"/> faulted or completed unexpectedly.
        /// </summary>
        internal event EventHandler<UnexpectedServiceTerminationEventArgs>? UnexpectedServiceTermination;

        /// <summary>
        /// Initializes a new instance of the <see cref="ApplicationServiceManager"/> class.
        /// </summary>
        /// <param name="services">Registered application services in startup order.</param>
        /// <param name="options">Hosting and lifecycle options.</param>
        /// <param name="logger">Logger.</param>
        public ApplicationServiceManager(
            IEnumerable<IApplicationService> services,
            IApplicationLifecycleOptions options,
            ILogger<ApplicationServiceManager> logger)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);

            _services = services as IReadOnlyList<IApplicationService> ?? [.. services];
            _options = options;
            _logger = logger;
        }

        /// <summary>
        /// Gets the services that completed <see cref="StartAsync"/> successfully and have not yet finished a
        /// stop attempt (success, cooperative cancel/timeout, or exception).
        /// </summary>
        /// <remarks>
        /// Removal from this list means the manager has finished awaiting that service's
        /// <see cref="IApplicationService.StopAsync"/> call for the current shutdown. It does not imply
        /// that a non-cooperative service released all resources if the process is later killed by the host.
        /// </remarks>
        internal IReadOnlyList<IApplicationService> StartedServices
        {
            get
            {
                lock (_startedSync)
                {
                    return [.. _started];
                }
            }
        }

        /// <summary>
        /// Starts all registered services in deterministic registration order.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel startup.</param>
        /// <returns>A task that completes when all services have started.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a lifecycle operation is already in progress.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
        internal async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _lifecycleBusy, 1, 0) != 0)
            {
                throw new InvalidOperationException("An application service lifecycle operation is already in progress.");
            }

            var acquired = false;
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;

                lock (_startedSync)
                {
                    if (_started.Count > 0)
                    {
                        throw new InvalidOperationException("Application services have already been started.");
                    }
                }

                ServiceManagerLogMessages.StartingServices(_logger, _services.Count);

                for (var i = 0; i < _services.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var service = _services[i];
                    var sw = Stopwatch.StartNew();

                    ServiceManagerLogMessages.StartingService(_logger, service.Name, i + 1, _services.Count);

                    try
                    {
                        await service.StartAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        ServiceManagerLogMessages.ServiceStartupCanceled(
                            _logger,
                            service.Name,
                            sw.ElapsedMilliseconds,
                            StartedServices.Count);

                        await RollbackStartedAsync(CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                    catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                    {
                        ServiceManagerLogMessages.ServiceStartupFailed(
                            _logger,
                            ex,
                            service.Name,
                            sw.ElapsedMilliseconds,
                            StartedServices.Count);

                        await RollbackStartedAsync(CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }

                    lock (_startedSync)
                    {
                        _started.Add(service);
                    }

                    ServiceManagerLogMessages.ServiceStarted(_logger, service.Name, sw.ElapsedMilliseconds);
                }

                BeginExecutionMonitoring();

                ServiceManagerLogMessages.AllServicesStarted(_logger);
            }
            finally
            {
                if (acquired)
                {
                    _gate.Release();
                }

                Interlocked.Exchange(ref _lifecycleBusy, 0);
            }
        }

        /// <summary>
        /// Stops successfully started services in reverse startup order, applying a bounded shutdown timeout.
        /// </summary>
        /// <param name="cancellationToken">External cancellation that cooperates with the configured shutdown timeout.</param>
        /// <returns>A task that completes when shutdown finishes (or times out).</returns>
        /// <exception cref="InvalidOperationException">Thrown when a lifecycle operation is already in progress.</exception>
        /// <exception cref="TimeoutException">Thrown when shutdown exceeds the configured graceful timeout.</exception>
        /// <exception cref="AggregateException">Thrown when one or more services fail during shutdown.</exception>
        /// <remarks>
        /// <para>
        /// <see cref="IApplicationLifecycleOptions.GracefulShutdownTimeout"/> is a single overall wall-clock budget for the
        /// entire stop sequence (not a fresh full timeout per service). The manager always awaits each
        /// <see cref="IApplicationService.StopAsync"/> — it does not abandon in-flight stops. Cooperative
        /// services observe the linked timeout/cancel token; after the budget is exhausted or the caller
        /// cancels, remaining services are offered an already-cancelled token and are removed from
        /// <see cref="StartedServices"/> when their awaited stop attempt finishes.
        /// </para>
        /// <para>
        /// A service that ignores cancellation keeps the manager awaiting until that call returns. The
        /// Generic Host <c>ShutdownTimeout</c> (aligned with <see cref="IApplicationLifecycleOptions.GracefulShutdownTimeout"/>)
        /// and process supervisors (for example systemd <c>TimeoutStopSec</c>) remain the backstop that can
        /// terminate the process. Concurrent <see cref="StartAsync"/> / <see cref="StopAsync"/> calls on this
        /// manager are rejected; <see cref="ApplicationLifecycle"/> provides single-flight stop for host paths.
        /// </para>
        /// </remarks>
        internal async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _lifecycleBusy, 1, 0) != 0)
            {
                throw new InvalidOperationException("An application service lifecycle operation is already in progress.");
            }

            var acquired = false;
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;

                await StopExecutionMonitoringAsync().ConfigureAwait(false);

                IApplicationService[] toStop;
                lock (_startedSync)
                {
                    toStop = [.. _started];
                }

                if (toStop.Length == 0)
                {
                    ServiceManagerLogMessages.NoServicesToStop(_logger);
                    return;
                }

                var timeout = _options.GracefulShutdownTimeout;
                ServiceManagerLogMessages.StoppingServices(_logger, toStop.Length, timeout);

                var budgetSw = Stopwatch.StartNew();
                var failures = new List<Exception>();
                var budgetExhausted = false;
                var externallyCanceled = false;

                for (var i = toStop.Length - 1; i >= 0; i--)
                {
                    var service = toStop[i];
                    var sw = Stopwatch.StartNew();

                    ServiceManagerLogMessages.StoppingService(_logger, service.Name, i + 1);

                    // codeql[cs/missed-using-statement] Conditionally constructed timeout/linked sources disposed in finally after StopAsync; a using would dispose before the stop await completes.
                    CancellationTokenSource? timeoutCts = null;
                    // codeql[cs/missed-using-statement] Conditionally constructed timeout/linked sources disposed in finally after StopAsync; a using would dispose before the stop await completes.
                    CancellationTokenSource? linkedCts = null;
                    try
                    {
                        CancellationToken stopToken;
                        if (externallyCanceled || budgetExhausted || budgetSw.Elapsed >= timeout)
                        {
                            // Overall budget spent or caller cancelled: cooperative abort only — do not extend the wall clock.
                            if (budgetSw.Elapsed >= timeout)
                            {
                                budgetExhausted = true;
                            }

                            timeoutCts = new CancellationTokenSource();
                            timeoutCts.Cancel();
                            stopToken = timeoutCts.Token;
                        }
                        else
                        {
                            var remaining = timeout - budgetSw.Elapsed;
                            timeoutCts = new CancellationTokenSource(remaining);
                            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                                cancellationToken,
                                timeoutCts.Token);
                            stopToken = linkedCts.Token;
                        }

                        try
                        {
                            await service.StopAsync(stopToken).ConfigureAwait(false);

                            // Tracking drops only after the awaited stop attempt returns — never while still executing.
                            lock (_startedSync)
                            {
                                _started.Remove(service);
                            }

                            // Non-cooperative stops may return after the budget without throwing OCE.
                            if (budgetSw.Elapsed >= timeout)
                            {
                                if (!budgetExhausted)
                                {
                                    budgetExhausted = true;
                                    ServiceManagerLogMessages.GracefulShutdownTimedOutAfterBudget(
                                        _logger,
                                        timeout,
                                        service.Name,
                                        sw.ElapsedMilliseconds);

                                    failures.Add(new TimeoutException(
                                        $"Graceful shutdown timed out after {timeout} while stopping '{service.Name}'."));
                                }
                                else
                                {
                                    ServiceManagerLogMessages.ServiceStopCompletedAfterBudget(
                                        _logger,
                                        service.Name,
                                        sw.ElapsedMilliseconds);
                                }
                            }
                            else
                            {
                                ServiceManagerLogMessages.ServiceStopped(_logger, service.Name, sw.ElapsedMilliseconds);
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            externallyCanceled = true;
                            ServiceManagerLogMessages.ServiceShutdownCanceled(
                                _logger,
                                service.Name,
                                sw.ElapsedMilliseconds);

                            // Drop tracking and continue so ApplicationLifecycle Stopped/DisposeAsync cannot
                            // strand services that were never offered a stop after external cancellation.
                            lock (_startedSync)
                            {
                                _started.Remove(service);
                            }
                        }
                        catch (OperationCanceledException) when (budgetExhausted
                                                                 || timeoutCts.IsCancellationRequested)
                        {
                            if (!budgetExhausted)
                            {
                                budgetExhausted = true;
                                ServiceManagerLogMessages.GracefulShutdownTimedOut(
                                    _logger,
                                    timeout,
                                    service.Name,
                                    sw.ElapsedMilliseconds);

                                failures.Add(new TimeoutException(
                                    $"Graceful shutdown timed out after {timeout} while stopping '{service.Name}'."));
                            }
                            else
                            {
                                ServiceManagerLogMessages.ServiceStopAbortedAfterBudget(_logger, service.Name);
                            }

                            // Drop tracking even when StopAsync did not complete cleanly so lifecycle
                            // Stopped does not strand services in StartedServices.
                            lock (_startedSync)
                            {
                                _started.Remove(service);
                            }
                        }
                        catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                        {
                            ServiceManagerLogMessages.ServiceShutdownFailed(
                                _logger,
                                ex,
                                service.Name,
                                sw.ElapsedMilliseconds);
                            failures.Add(ex);

                            lock (_startedSync)
                            {
                                _started.Remove(service);
                            }
                        }
                    }
                    finally
                    {
                        linkedCts?.Dispose();
                        timeoutCts?.Dispose();
                    }
                }

                if (externallyCanceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (failures is [TimeoutException timeoutEx])
                {
                    throw timeoutEx;
                }

                if (failures.Count > 0)
                {
                    throw new AggregateException("One or more application services failed during shutdown.", failures);
                }

                ServiceManagerLogMessages.AllServicesStopped(_logger);
            }
            finally
            {
                if (acquired)
                {
                    _gate.Release();
                }

                Interlocked.Exchange(ref _lifecycleBusy, 0);
            }
        }

        /// <summary>
        /// Stops services already in <see cref="_started"/>, in reverse order, after a failed or canceled startup.
        /// </summary>
        /// <param name="cancellationToken">Linked with <see cref="IApplicationLifecycleOptions.GracefulShutdownTimeout"/>.</param>
        /// <remarks>
        /// Stop failures are logged and do not fail the rollback. Each service is removed from
        /// <see cref="StartedServices"/> after its stop attempt. Execution monitoring is stopped first.
        /// </remarks>
        private async Task RollbackStartedAsync(CancellationToken cancellationToken)
        {
            await StopExecutionMonitoringAsync().ConfigureAwait(false);

            IApplicationService[] toStop;
            lock (_startedSync)
            {
                toStop = [.. _started];
            }

            if (toStop.Length == 0)
            {
                return;
            }

            ServiceManagerLogMessages.RollingBackServices(_logger, toStop.Length);

            var timeout = _options.GracefulShutdownTimeout;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            for (var i = toStop.Length - 1; i >= 0; i--)
            {
                var service = toStop[i];
                try
                {
                    await service.StopAsync(timeoutCts.Token).ConfigureAwait(false);
                    ServiceManagerLogMessages.ServiceRolledBack(_logger, service.Name);
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    ServiceManagerLogMessages.ServiceRollbackFailed(_logger, ex, service.Name);
                }
                finally
                {
                    lock (_startedSync)
                    {
                        _started.Remove(service);
                    }
                }
            }
        }

        /// <summary>
        /// After every service has started, watches each non-null <see cref="IApplicationService.Execution"/>
        /// until stop or rollback cancels the watch.
        /// </summary>
        /// <remarks>Does nothing when no registered service exposes an execution task.</remarks>
        private void BeginExecutionMonitoring()
        {
            var monitored = _services
                .Where(static s => s.Execution is not null)
                .ToArray();

            if (monitored.Length == 0)
            {
                return;
            }

            _executionCts = new CancellationTokenSource();
            var token = _executionCts.Token;
            _executionMonitor = MonitorExecutionsAsync(monitored, token);
        }

        /// <summary>Runs <see cref="WatchServiceAsync"/> for each monitored service until all complete or <paramref name="cancellationToken"/> is canceled.</summary>
        /// <param name="monitored">Services that had a non-null <see cref="IApplicationService.Execution"/> when monitoring began.</param>
        /// <param name="cancellationToken">Canceled by <see cref="StopExecutionMonitoringAsync"/> during orderly shutdown.</param>
        private async Task MonitorExecutionsAsync(IReadOnlyList<IApplicationService> monitored, CancellationToken cancellationToken)
        {
            var tasks = monitored
                .Select(s => WatchServiceAsync(s, cancellationToken))
                .ToArray();

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected during orderly shutdown.
            }
        }

        /// <summary>
        /// Waits for <paramref name="service"/>'s execution task. A fault, cancellation, or successful completion
        /// while <paramref name="cancellationToken"/> is not canceled raises <see cref="UnexpectedServiceTermination"/>.
        /// </summary>
        /// <param name="service">Started service whose <see cref="IApplicationService.Execution"/> was non-null.</param>
        /// <param name="cancellationToken">Orderly-shutdown signal. When canceled, the watch returns without raising the event.</param>
        private async Task WatchServiceAsync(IApplicationService service, CancellationToken cancellationToken)
        {
            var execution = service.Execution;
            if (execution is null)
            {
                return;
            }

            try
            {
                var completed = await Task.WhenAny(execution, Task.Delay(Timeout.Infinite, cancellationToken))
                    .ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (completed != execution)
                {
                    return;
                }

                // Observe the execution outcome.
                if (execution.IsFaulted)
                {
                    var ex = execution.Exception?.GetBaseException() ?? new InvalidOperationException("Service execution faulted.");
                    ServiceManagerLogMessages.ServiceTerminatedWithFault(_logger, ex, service.Name);

                    UnexpectedServiceTermination?.Invoke(
                        this,
                        new UnexpectedServiceTerminationEventArgs(service.Name, ex, completedNormally: false));
                    return;
                }

                if (execution.IsCanceled)
                {
                    ServiceManagerLogMessages.ServiceExecutionCanceledUnexpectedly(_logger, service.Name);

                    UnexpectedServiceTermination?.Invoke(
                        this,
                        new UnexpectedServiceTerminationEventArgs(
                            service.Name,
                            new OperationCanceledException("Service execution was canceled."),
                            completedNormally: false));
                    return;
                }

                ServiceManagerLogMessages.ServiceExecutionCompletedUnexpectedly(_logger, service.Name);

                UnexpectedServiceTermination?.Invoke(
                    this,
                    new UnexpectedServiceTerminationEventArgs(service.Name, exception: null, completedNormally: true));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Orderly shutdown.
            }
        }

        /// <summary>
        /// Cancels and awaits the execution monitor, then disposes its token source.
        /// </summary>
        /// <remarks>
        /// <see cref="OperationCanceledException"/> from the monitor is expected.
        /// Any other exception is logged and does not fail stop or rollback.
        /// </remarks>
        private async Task StopExecutionMonitoringAsync()
        {
            // codeql[cs/missed-using-statement] CTS ownership is the manager field lifetime; Exchange takes it for cancel/await then finally Dispose below. A using at exchange would dispose before the monitor finishes.
            var cts = Interlocked.Exchange(ref _executionCts, null);
            var monitor = Interlocked.Exchange(ref _executionMonitor, null);

            try
            {
                if (cts is not null)
                {
                    try
                    {
                        await cts.CancelAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Already disposed.
                    }
                }

                if (monitor is not null)
                {
                    try
                    {
                        await monitor.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected.
                    }
                    catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                    {
                        ServiceManagerLogMessages.ExecutionMonitorExceptionDuringShutdown(_logger, ex);
                    }
                }
            }
            finally
            {
                cts?.Dispose();
            }
        }
    }

    /// <summary>
    /// Provides data for the <see cref="ApplicationServiceManager.UnexpectedServiceTermination"/> event.
    /// </summary>
    internal sealed class UnexpectedServiceTerminationEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UnexpectedServiceTerminationEventArgs"/> class.
        /// </summary>
        /// <param name="serviceName">Name of the service that terminated.</param>
        /// <param name="exception">Fault exception, if any.</param>
        /// <param name="completedNormally">Whether the execution task completed without fault or cancellation.</param>
        internal UnexpectedServiceTerminationEventArgs(string serviceName, Exception? exception, bool completedNormally)
        {
            ServiceName = serviceName;
            Exception = exception;
            CompletedNormally = completedNormally;
        }

        /// <summary>Gets the service name.</summary>
        internal string ServiceName { get; }

        /// <summary>Gets the fault exception, if the execution faulted.</summary>
        internal Exception? Exception { get; }

        /// <summary>Gets a value indicating whether execution completed without fault or cancellation.</summary>
        internal bool CompletedNormally { get; }
    }
}
