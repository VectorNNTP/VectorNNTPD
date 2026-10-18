using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.BackFillerService.Core
{
    /// <summary>
    /// Coordinates deterministic start/stop of registered <see cref="IApplicationService"/> instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Services start in registration order and stop in reverse order. On startup failure,
    /// already-started services are stopped (rollback). Concurrent lifecycle execution is rejected.
    /// </para>
    /// <para>
    /// The stop budget is one <see cref="BackFillerShutdownRuntimeOptions.GracePeriod"/> for the
    /// whole stop or rollback loop, linked with the caller token on stop and with
    /// <see cref="CancellationToken.None"/> on rollback. It is not granted separately to each service.
    /// Overlapping <see cref="StartAsync"/> and <see cref="StopAsync"/> calls throw
    /// <see cref="InvalidOperationException"/> before waiting on <see cref="_gate"/>.
    /// A failed start leaves the started list empty and the manager idle, so start may be attempted again.
    /// </para>
    /// <para>
    /// After every service starts, non-null <see cref="IApplicationService.Execution"/> tasks are watched
    /// until stop or rollback cancels the watch. A fault, a successful completion, or cancellation of that
    /// execution raises <see cref="UnexpectedServiceTermination"/> on the watch task. Exceptions thrown by
    /// handlers are not caught there.
    /// </para>
    /// </remarks>
    internal sealed class ApplicationServiceManager
    {
        /// <summary>
        /// Services in registration order. An argument that is already an
        /// <see cref="IReadOnlyList{T}"/> is stored as that instance; any other sequence is copied.
        /// </summary>
        private readonly IReadOnlyList<IApplicationService> _services;

        /// <summary>
        /// Shared stop and startup-rollback budget from <see cref="BackFillerShutdownRuntimeOptions.GracePeriod"/>.
        /// </summary>
        private readonly TimeSpan _shutdownBudget;

        /// <summary>Logger that receives lifecycle events from <see cref="ApplicationServiceManagerLogMessages"/>.</summary>
        private readonly ILogger<ApplicationServiceManager> _logger;

        /// <summary>
        /// Serializes a start or stop that has already passed the <see cref="_lifecycleBusy"/> check.
        /// </summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>
        /// Services whose <see cref="IApplicationService.StartAsync"/> completed and whose stop has not finished.
        /// Guarded by <see cref="_startedSync"/>.
        /// </summary>
        private readonly List<IApplicationService> _started = [];

        /// <summary>
        /// Non-recursive lock for <see cref="_started"/>. No caller enters it again on the same call stack.
        /// </summary>
        private readonly Lock _startedSync = new();

        /// <summary>
        /// Non-zero while <see cref="StartAsync"/> or <see cref="StopAsync"/> has entered and not yet left.
        /// </summary>
        private int _lifecycleBusy;

        /// <summary>
        /// Cancels execution watches. Exchanged away and disposed by <see cref="StopExecutionMonitoringAsync"/>.
        /// </summary>
        private CancellationTokenSource? _executionCts;

        /// <summary>
        /// In-flight execution watch, or <see langword="null"/> when no service exposed an execution.
        /// </summary>
        private Task? _executionMonitor;

        /// <summary>
        /// Occurs when a started service's <see cref="IApplicationService.Execution"/> faulted or completed unexpectedly.
        /// </summary>
        /// <remarks>
        /// Raised synchronously on the execution-watch task, once per settled execution.
        /// <see cref="UnexpectedServiceTerminationEventArgs.Exception"/> is <see langword="null"/> when the
        /// execution ran to completion or was canceled. Handler exceptions propagate from the watch task
        /// and can surface from <see cref="StopAsync"/> or from startup rollback.
        /// </remarks>
        internal event EventHandler<UnexpectedServiceTerminationEventArgs>? UnexpectedServiceTermination;

        /// <summary>
        /// Captures the service sequence and the shutdown budget from <paramref name="runtime"/>.
        /// </summary>
        /// <param name="services">Registered application services in startup order.</param>
        /// <param name="runtime">Runtime snapshot whose shutdown grace period is the stop and rollback budget.</param>
        /// <param name="logger">Logger that receives lifecycle events.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="services"/>, <paramref name="runtime"/>, or <paramref name="logger"/> is <see langword="null"/>.
        /// </exception>
        /// <remarks>
        /// When <paramref name="services"/> is already an <see cref="IReadOnlyList{T}"/>, that instance is stored
        /// without copying. Otherwise the sequence is copied before start.
        /// </remarks>
        public ApplicationServiceManager(
            IEnumerable<IApplicationService> services,
            BackFillerRuntimeOptions runtime,
            ILogger<ApplicationServiceManager> logger)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(logger);
            _services = services as IReadOnlyList<IApplicationService> ?? [.. services];
            _shutdownBudget = runtime.Shutdown.GracePeriod;
            _logger = logger;
        }

        /// <summary>Gets the services that completed start and have not finished stop.</summary>
        /// <value>
        /// A copy taken under <see cref="_startedSync"/>. Later start, stop, or rollback calls do not mutate the returned list.
        /// </value>
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

        /// <summary>Starts registered services in registration order, then watches their executions.</summary>
        /// <param name="cancellationToken">
        /// Cancels the gate wait and each <see cref="IApplicationService.StartAsync"/>.
        /// Rollback after a failed or canceled start does not observe this token.
        /// </param>
        /// <returns>
        /// A task that completes when every service has started and execution watching has begun,
        /// or when startup has failed after rollback of services that already started.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a lifecycle operation is already in progress, or when <see cref="StartedServices"/> is not empty.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown when <paramref name="cancellationToken"/> is canceled while waiting for <see cref="_gate"/>
        /// or during a service start. Services that already started are stopped before this exception propagates,
        /// unless cancellation happens before the gate is acquired.
        /// </exception>
        /// <remarks>
        /// <para>
        /// Any other exception from <see cref="IApplicationService.StartAsync"/>, including an
        /// <see cref="OperationCanceledException"/> raised while <paramref name="cancellationToken"/> is not
        /// canceled, is logged, followed by rollback, then rethrown.
        /// </para>
        /// <para>
        /// Rollback stops already-started services in reverse order under <see cref="_shutdownBudget"/>
        /// and logs and swallows every exception from those stops. The busy flag is cleared when this
        /// method returns, including after failure.
        /// </para>
        /// </remarks>
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

                for (var i = 0; i < _services.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var service = _services[i];
                    ApplicationServiceManagerLogMessages.StartingService(
                        _logger,
                        service.Name,
                        i + 1,
                        _services.Count);
                    try
                    {
                        await service.StartAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        ApplicationServiceManagerLogMessages.ServiceStartupFailed(_logger, ex, service.Name);
                        await RollbackStartedAsync(CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        ApplicationServiceManagerLogMessages.ServiceStartupCanceled(_logger, service.Name);
                        await RollbackStartedAsync(CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }

                    lock (_startedSync)
                    {
                        _started.Add(service);
                    }

                    ApplicationServiceManagerLogMessages.ServiceStarted(_logger, service.Name);
                }

                BeginExecutionMonitoring();
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

        /// <summary>Stops started services in reverse order under one shared shutdown budget.</summary>
        /// <param name="cancellationToken">
        /// Cancels the gate wait. After the gate is acquired, this token is linked with
        /// <see cref="_shutdownBudget"/> and passed to each <see cref="IApplicationService.StopAsync"/>.
        /// </param>
        /// <returns>
        /// A task that completes when every started service has been asked to stop and removed from
        /// <see cref="StartedServices"/>, or when the gate wait is canceled.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a lifecycle operation is already in progress.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown when <paramref name="cancellationToken"/> is canceled while waiting for <see cref="_gate"/>.
        /// Execution watching is left running in that case.
        /// </exception>
        /// <exception cref="AggregateException">
        /// Thrown when one or more services throw a non-cancellation exception from
        /// <see cref="IApplicationService.StopAsync"/>. Cancellation of an individual stop is logged and
        /// is not an inner exception. The started service is still removed.
        /// </exception>
        /// <remarks>
        /// Execution watching is stopped after the gate is acquired and before services are stopped.
        /// Stopping when nothing has started completes without throwing. A canceled or budget-expired
        /// individual stop does not fail this method by itself; later services are still stopped with the
        /// same linked token.
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

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_shutdownBudget);
                var failures = new List<Exception>();
                for (var i = toStop.Length - 1; i >= 0; i--)
                {
                    var service = toStop[i];
                    ApplicationServiceManagerLogMessages.StoppingService(_logger, service.Name);
                    try
                    {
                        await service.StopAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ApplicationServiceManagerLogMessages.ServiceStopFailed(_logger, ex, service.Name);
                        failures.Add(ex);
                    }
                    catch (OperationCanceledException)
                    {
                        ApplicationServiceManagerLogMessages.ServiceStopCanceled(_logger, service.Name);
                    }
                    finally
                    {
                        lock (_startedSync)
                        {
                            _started.Remove(service);
                        }
                    }
                }

                if (failures.Count > 0)
                {
                    throw new AggregateException("One or more application services failed during shutdown.", failures);
                }
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
        /// Stops services in <see cref="StartedServices"/> in reverse order after a failed or canceled start.
        /// </summary>
        /// <param name="cancellationToken">
        /// Linked with <see cref="_shutdownBudget"/>. <see cref="StartAsync"/> passes <see cref="CancellationToken.None"/>.
        /// </param>
        /// <returns>
        /// A task that completes after every previously started service has been removed from <see cref="StartedServices"/>.
        /// </returns>
        /// <remarks>
        /// Every exception from <see cref="IApplicationService.StopAsync"/>, including
        /// <see cref="OperationCanceledException"/>, is logged and swallowed. Execution watching is canceled first.
        /// </remarks>
        private async Task RollbackStartedAsync(CancellationToken cancellationToken)
        {
            await StopExecutionMonitoringAsync().ConfigureAwait(false);
            IApplicationService[] toStop;
            lock (_startedSync)
            {
                toStop = [.. _started];
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_shutdownBudget);
            for (var i = toStop.Length - 1; i >= 0; i--)
            {
                var service = toStop[i];
                try
                {
                    await service.StopAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] A service start or stop failure must be recorded without abandoning the remaining services.
                catch (Exception ex)
                {
                    ApplicationServiceManagerLogMessages.ServiceRollbackFailed(_logger, ex, service.Name);
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
        /// Starts a background watch of every registered service whose <see cref="IApplicationService.Execution"/> is non-null.
        /// </summary>
        /// <remarks>
        /// The non-null check uses the execution observed when this method runs, after every start has returned.
        /// No watch task is stored when every execution is null. The watch is not observed until
        /// <see cref="StopExecutionMonitoringAsync"/>.
        /// </remarks>
        private void BeginExecutionMonitoring()
        {
            var monitored = _services.Where(static item => item.Execution is not null).ToArray();
            if (monitored.Length == 0)
            {
                return;
            }

            _executionCts = new CancellationTokenSource();
            var token = _executionCts.Token;
            _executionMonitor = MonitorExecutionsAsync(monitored, token);
        }

        /// <summary>
        /// Watches each supplied service until its execution settles or <paramref name="cancellationToken"/> is canceled.
        /// </summary>
        /// <param name="monitored">Services selected because their execution was non-null when watching began.</param>
        /// <param name="cancellationToken">Canceled by <see cref="StopExecutionMonitoringAsync"/> for orderly shutdown.</param>
        /// <returns>
        /// A task that completes when every per-service watch finishes.
        /// <see cref="OperationCanceledException"/> caused by <paramref name="cancellationToken"/> is swallowed.
        /// </returns>
        /// <remarks>
        /// Other exceptions, including exceptions thrown by <see cref="UnexpectedServiceTermination"/> handlers, propagate.
        /// </remarks>
        private async Task MonitorExecutionsAsync(IReadOnlyList<IApplicationService> monitored, CancellationToken cancellationToken)
        {
            try
            {
                await Task.WhenAll(monitored.Select(item => WatchServiceAsync(item, cancellationToken)))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Orderly shutdown.
            }
        }

        /// <summary>
        /// Waits until <paramref name="service"/> execution settles, unless <paramref name="cancellationToken"/> cancels first.
        /// </summary>
        /// <param name="service">Service whose <see cref="IApplicationService.Execution"/> is read again here.</param>
        /// <param name="cancellationToken">
        /// Orderly-shutdown signal. When it is canceled before the execution settles, no event is raised.
        /// </param>
        /// <returns>A task that completes when the execution settles or the watch is canceled.</returns>
        /// <remarks>
        /// <para>
        /// A null execution returns without waiting. A fault raises <see cref="UnexpectedServiceTermination"/>
        /// with <see cref="AggregateException.GetBaseException"/>, or with a new
        /// <see cref="InvalidOperationException"/> when the faulted task exposes no exception.
        /// That exception is logged and passed to handlers; it is not thrown from this method.
        /// </para>
        /// <para>
        /// Ran-to-completion and cancellation of the execution both raise the event with a null exception.
        /// </para>
        /// </remarks>
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
                if (cancellationToken.IsCancellationRequested || completed != execution)
                {
                    return;
                }

                if (execution.IsFaulted)
                {
                    var ex = execution.Exception?.GetBaseException()
                        ?? new InvalidOperationException("Service execution faulted.");
                    ApplicationServiceManagerLogMessages.ServiceExecutionFaulted(_logger, ex, service.Name);
                    UnexpectedServiceTermination?.Invoke(
                        this,
                        new UnexpectedServiceTerminationEventArgs(service.Name, ex));
                    return;
                }

                ApplicationServiceManagerLogMessages.ServiceExecutionEnded(_logger, service.Name);
                UnexpectedServiceTermination?.Invoke(
                    this,
                    new UnexpectedServiceTerminationEventArgs(service.Name, exception: null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Orderly shutdown.
            }
        }

        /// <summary>Cancels and disposes the execution watch, then waits for the watch task.</summary>
        /// <returns>
        /// A task that completes when any in-flight watch has been observed.
        /// <see cref="OperationCanceledException"/> from that watch is swallowed.
        /// </returns>
        /// <remarks>
        /// <see cref="ObjectDisposedException"/> from canceling an already disposed source is swallowed.
        /// Other exceptions from the watch task propagate. The method completes immediately when no watch was started.
        /// </remarks>
        private async Task StopExecutionMonitoringAsync()
        {
            var cts = Interlocked.Exchange(ref _executionCts, null);
            var monitor = Interlocked.Exchange(ref _executionMonitor, null);
            if (cts is not null)
            {
                using (cts)
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
            }
        }
    }

    /// <summary>Provides data for unexpected application-service termination.</summary>
    internal sealed class UnexpectedServiceTerminationEventArgs : EventArgs
    {
        /// <summary>
        /// Captures the service name and optional fault for <see cref="ApplicationServiceManager.UnexpectedServiceTermination"/>.
        /// </summary>
        /// <param name="serviceName">Terminated <see cref="IApplicationService.Name"/>. Must not be null or whitespace.</param>
        /// <param name="exception">
        /// Fault from the execution, or <see langword="null"/> when the execution completed or was canceled without faulting.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="serviceName"/> is null or whitespace.
        /// </exception>
        internal UnexpectedServiceTerminationEventArgs(string serviceName, Exception? exception)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
            ServiceName = serviceName;
            Exception = exception;
        }

        /// <summary>Gets the terminated service name.</summary>
        /// <value>The <see cref="IApplicationService.Name"/> supplied when the event was raised.</value>
        internal string ServiceName { get; }

        /// <summary>Gets the fault, if the execution faulted.</summary>
        /// <value>
        /// <see langword="null"/> when the execution ran to completion or was canceled. Not <see langword="null"/> when it faulted.
        /// </value>
        internal Exception? Exception { get; }
    }
}
