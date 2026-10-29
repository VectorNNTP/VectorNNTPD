using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VectorNNTPD.Common.Logging;

namespace VectorNNTPD.Common.Core
{
    /// <summary>
    /// Explicit, observable application lifecycle coordinator with validated state transitions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Happy path: <see cref="ApplicationState.Created"/> → <see cref="ApplicationState.Starting"/> →
    /// <see cref="ApplicationState.Running"/> → <see cref="ApplicationState.Stopping"/> →
    /// <see cref="ApplicationState.Stopped"/>.
    /// </para>
    /// <para>
    /// Startup failure or cancellation: <see cref="ApplicationState.Starting"/> →
    /// <see cref="ApplicationState.Stopping"/> → <see cref="ApplicationState.Stopped"/>, then the exception is rethrown.
    /// Partially started services are rolled back by <see cref="ApplicationServiceManager"/>.
    /// </para>
    /// <para>
    /// Repeated <see cref="StopAsync"/> calls are safe: subsequent callers await the in-flight stop.
    /// Concurrent <see cref="StartAsync"/> / <see cref="StopAsync"/> operations are serialized.
    /// </para>
    /// </remarks>
    public sealed class ApplicationLifecycle : IAsyncDisposable
    {
        /// <summary>
        /// Legal state changes. Any other pair causes <see cref="Transition"/> to throw
        /// <see cref="InvalidOperationException"/>. Same-state calls are a no-op and are not listed.
        /// </summary>
        private static readonly HashSet<(ApplicationState From, ApplicationState To)> ValidTransitions =
        [
            (ApplicationState.Created, ApplicationState.Starting),
            (ApplicationState.Created, ApplicationState.Stopped),
            (ApplicationState.Starting, ApplicationState.Running),
            (ApplicationState.Starting, ApplicationState.Stopping),
            (ApplicationState.Running, ApplicationState.Stopping),
            (ApplicationState.Stopping, ApplicationState.Stopped),
        ];

        /// <summary>Ordered start, stop, and rollback of registered application services.</summary>
        private readonly ApplicationServiceManager _serviceManager;

        /// <summary>Display name, optional startup timeout, and graceful-shutdown budget.</summary>
        private readonly IApplicationLifecycleOptions _options;

        /// <summary>Lifecycle diagnostics.</summary>
        private readonly ILogger<ApplicationLifecycle> _logger;

        /// <summary>Serializes <see cref="StartAsync"/> and <see cref="StopCoreAsync"/>.</summary>
        // codeql[cs/missed-using-statement] Owned by ApplicationLifecycle until DisposeAsync; a using at field declaration would dispose when the constructor returns.
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Guards <see cref="_state"/> reads and writes.</summary>
        private readonly Lock _stateSync = new();

        /// <summary>
        /// Completes when the lifecycle reaches <see cref="ApplicationState.Stopped"/>,
        /// including never-started stop and startup-failure cleanup.
        /// </summary>
        private readonly TaskCompletionSource _stoppedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Completes when unexpected service termination is observed while
        /// <see cref="ApplicationState.Running"/>.
        /// </summary>
        private readonly TaskCompletionSource _unexpectedTerminationTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Current state. Mutated only under <see cref="_stateSync"/>. Starts at <see cref="ApplicationState.Created"/>.</summary>
        private ApplicationState _state = ApplicationState.Created;

        /// <summary>
        /// In-flight <see cref="StopCoreAsync"/>, or <see langword="null"/> until stop begins.
        /// Later <see cref="StopAsync"/> callers await this task.
        /// </summary>
        private Task? _stopTask;

        /// <summary>Non-zero after <see cref="DisposeAsync"/> has marked the instance disposed, which happens after stop.</summary>
        private int _disposed;

        /// <summary>Non-zero after the first <see cref="DisposeAsync"/> entry. Later callers await the existing stop.</summary>
        private int _disposeStarted;

        /// <summary>
        /// Non-zero once stop is requested or the state moves to
        /// <see cref="ApplicationState.Stopping"/> or <see cref="ApplicationState.Stopped"/>.
        /// </summary>
        private int _shutdownRequested;

        /// <summary>
        /// Completes when <see cref="DisposeAsync"/> finishes releasing resources.
        /// Faults when shutdown threw, so the first dispose caller can rethrow that failure.
        /// </summary>
        private readonly TaskCompletionSource _disposeCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Initializes a new instance of the <see cref="ApplicationLifecycle"/> class.
        /// </summary>
        /// <param name="serviceManager">Service manager used for ordered start/stop.</param>
        /// <param name="options">Lifecycle options.</param>
        /// <param name="logger">Logger.</param>
        internal ApplicationLifecycle(
            ApplicationServiceManager serviceManager,
            IApplicationLifecycleOptions options,
            ILogger<ApplicationLifecycle> logger)
        {
            ArgumentNullException.ThrowIfNull(serviceManager);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);

            _serviceManager = serviceManager;
            _options = options;
            _logger = logger;

            _serviceManager.UnexpectedServiceTermination += OnUnexpectedServiceTermination;
        }

        /// <summary>
        /// Occurs after a validated lifecycle state transition has been committed.
        /// </summary>
        internal event EventHandler<ApplicationStateChangedEventArgs>? StateChanged;

        /// <summary>Gets the current lifecycle state (thread-safe).</summary>
        internal ApplicationState State
        {
            get
            {
                lock (_stateSync)
                {
                    return _state;
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether shutdown has been requested or the lifecycle has left
        /// <see cref="ApplicationState.Running"/> toward stop.
        /// </summary>
        internal bool ShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

        /// <summary>
        /// Gets a task that completes when an unexpected application-service termination is observed while running.
        /// </summary>
        internal Task UnexpectedTermination => _unexpectedTerminationTcs.Task;

        /// <summary>
        /// Starts the application asynchronously.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel startup.</param>
        /// <returns>A task that completes when the application enters <see cref="ApplicationState.Running"/>.</returns>
        /// <exception cref="InvalidOperationException">Thrown for invalid transitions or concurrent unsafe use.</exception>
        /// <exception cref="OperationCanceledException">Thrown when startup is cancelled.</exception>
        internal async Task StartAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Transition(ApplicationState.Starting);

                LifecycleLogMessages.StartupInitiatedWithState(
                    _logger,
                    _options.ApplicationName,
                    State);

                var sw = Stopwatch.StartNew();

                using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (_options.StartupTimeout is { } startupTimeout)
                {
                    startupCts.CancelAfter(startupTimeout);
                }

                try
                {
                    await _serviceManager.StartAsync(startupCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    LifecycleLogMessages.StartupCanceled(_logger, sw.ElapsedMilliseconds);
                    await FailStartupCleanupAsync().ConfigureAwait(false);
                    throw;
                }
                catch (OperationCanceledException) when (startupCts.IsCancellationRequested)
                {
                    LifecycleLogMessages.StartupTimedOut(
                        _logger,
                        _options.StartupTimeout,
                        sw.ElapsedMilliseconds);
                    await FailStartupCleanupAsync().ConfigureAwait(false);
                    throw new TimeoutException(
                        $"Application startup timed out after {_options.StartupTimeout}.");
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    LifecycleLogMessages.StartupFailed(_logger, ex, sw.ElapsedMilliseconds);
                    await FailStartupCleanupAsync().ConfigureAwait(false);
                    throw;
                }

                Transition(ApplicationState.Running);

                LifecycleLogMessages.InitializationCompleted(
                    _logger,
                    _options.ApplicationName,
                    sw.ElapsedMilliseconds,
                    State);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Stops the application asynchronously. Repeated calls are safe and await the same shutdown.
        /// </summary>
        /// <param name="cancellationToken">Token that cooperates with the configured graceful shutdown timeout.</param>
        /// <returns>A task that completes when the application enters <see cref="ApplicationState.Stopped"/>.</returns>
        internal Task StopAsync(CancellationToken cancellationToken)
        {
            lock (_stateSync)
            {
                // Idempotent when already stopped, including during/after disposal of a completed lifecycle.
                if (_state == ApplicationState.Stopped)
                {
                    return Task.CompletedTask;
                }

                ObjectDisposedException.ThrowIf(_disposed != 0, this);

                if (_state == ApplicationState.Created)
                {
                    // Never started: move directly to Stopped.
                    Interlocked.Exchange(ref _shutdownRequested, 1);
                    var from = _state;
                    _state = ApplicationState.Stopped;
                    LifecycleLogMessages.StopRequestedBeforeStartup(
                        _logger,
                        ApplicationState.Created,
                        ApplicationState.Stopped);
                    _stoppedTcs.TrySetResult();
                    RaiseStateChanged(from, ApplicationState.Stopped);
                    return Task.CompletedTask;
                }

                if (_stopTask is not null)
                {
                    return _stopTask;
                }

                Interlocked.Exchange(ref _shutdownRequested, 1);
                _stopTask = StopCoreAsync(cancellationToken);
                return _stopTask;
            }
        }

        /// <summary>
        /// Waits until shutdown completes or an unexpected termination is observed.
        /// </summary>
        /// <param name="cancellationToken">Token cancelled when the host requests shutdown.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when an application service terminates unexpectedly while <see cref="ApplicationState.Running"/>.
        /// </exception>
        internal async Task WaitAsync(CancellationToken cancellationToken)
        {
            var shutdown = Task.Delay(Timeout.Infinite, cancellationToken);
            var completed = await Task.WhenAny(UnexpectedTermination, _stoppedTcs.Task, shutdown)
                .ConfigureAwait(false);

            if (completed == UnexpectedTermination)
            {
                await UnexpectedTermination.ConfigureAwait(false);
                throw new InvalidOperationException(
                    "An application service terminated unexpectedly while the application was Running.");
            }

            if (completed == _stoppedTcs.Task)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <inheritdoc />
        /// <remarks>
        /// Shutdown is completed before the instance is marked disposed so that a normal host-driven
        /// stop followed by DI disposal does not observe <see cref="ObjectDisposedException"/>.
        /// Repeated disposal is safe and awaits any in-flight stop.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            // Serialize dispose entry: the first caller performs shutdown + resource release.
            if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
            {
                await AwaitExistingStopAsync().ConfigureAwait(false);
                return;
            }

            _serviceManager.UnexpectedServiceTermination -= OnUnexpectedServiceTermination;

            Exception? shutdownFailure = null;
            try
            {
                // Complete graceful stop before marking disposed.
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                shutdownFailure = ex;
                LifecycleLogMessages.DisposalShutdownFailure(_logger, ex);
            }
            finally
            {
                Interlocked.Exchange(ref _disposed, 1);

                try
                {
                    _gate.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Gate may already be disposed by a concurrent path; ignore.
                }

                if (shutdownFailure is null)
                {
                    _disposeCompleted.TrySetResult();
                }
                else
                {
                    _disposeCompleted.TrySetException(shutdownFailure);
                }
            }

            // Surface genuine shutdown failures to DisposeAsync callers / DI host disposal.
            if (shutdownFailure is not null)
            {
                throw shutdownFailure;
            }
        }

        /// <summary>
        /// Waits for an in-flight stop and for disposal completion.
        /// Failures stay with the first <see cref="DisposeAsync"/> caller; this path does not rethrow them.
        /// </summary>
        private async Task AwaitExistingStopAsync()
        {
            Task? stop;
            lock (_stateSync)
            {
                stop = _stopTask;
            }

            if (stop is not null)
            {
                try
                {
                    await stop.ConfigureAwait(false);
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    // The original stop/dispose caller observes the failure; subsequent dispose awaits are idempotent.
                }
            }

            try
            {
                await _disposeCompleted.Task.ConfigureAwait(false);
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                // Idempotent subsequent dispose: do not rethrow the original failure.
            }
        }

        /// <summary>
        /// Holds <see cref="_gate"/>, moves to <see cref="ApplicationState.Stopping"/> when not already there,
        /// stops services, then moves to <see cref="ApplicationState.Stopped"/>.
        /// </summary>
        /// <param name="cancellationToken">
        /// Passed to <see cref="ApplicationServiceManager.StopAsync"/>. The gate wait itself is not canceled.
        /// </param>
        /// <remarks>
        /// <see cref="TimeoutException"/> and other stop failures still force <see cref="ApplicationState.Stopped"/>,
        /// complete <see cref="_stoppedTcs"/>, and are rethrown.
        /// </remarks>
        private async Task StopCoreAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var current = State;
                if (current == ApplicationState.Stopped)
                {
                    return;
                }

                if (current is not ApplicationState.Stopping)
                {
                    Transition(ApplicationState.Stopping);
                }

                LifecycleLogMessages.ShutdownInitiatedWithDetails(
                    _logger,
                    _options.ApplicationName,
                    State,
                    _options.GracefulShutdownTimeout);

                var sw = Stopwatch.StartNew();

                try
                {
                    await _serviceManager.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    LifecycleLogMessages.ShutdownTimedOutElapsed(_logger, ex, sw.ElapsedMilliseconds);
                    Transition(ApplicationState.Stopped);
                    _stoppedTcs.TrySetResult();
                    throw;
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    LifecycleLogMessages.ShutdownFailed(_logger, ex, sw.ElapsedMilliseconds);
                    Transition(ApplicationState.Stopped);
                    _stoppedTcs.TrySetResult();
                    throw;
                }

                Transition(ApplicationState.Stopped);

                LifecycleLogMessages.ShutdownCompletedWithState(
                    _logger,
                    _options.ApplicationName,
                    sw.ElapsedMilliseconds,
                    State);

                _stoppedTcs.TrySetResult();
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// After startup failure or cancellation, moves <see cref="ApplicationState.Starting"/> through
        /// <see cref="ApplicationState.Stopping"/> to <see cref="ApplicationState.Stopped"/>, completes
        /// <see cref="_stoppedTcs"/>, and best-effort stops any services the manager still tracks.
        /// </summary>
        /// <remarks>
        /// The service manager is expected to have rolled back already. Errors from the residual stop are logged
        /// and do not replace the original startup exception.
        /// </remarks>
        private async Task FailStartupCleanupAsync()
        {
            // Services should already be rolled back by the manager; ensure lifecycle ends in Stopped.
            if (State == ApplicationState.Starting)
            {
                Transition(ApplicationState.Stopping);
            }

            if (State == ApplicationState.Stopping)
            {
                Transition(ApplicationState.Stopped);
            }

            _stoppedTcs.TrySetResult();

            // Ensure any residual started services are cleared (best effort; manager already rolled back).
            try
            {
                if (_serviceManager.StartedServices.Count > 0)
                {
                    await _serviceManager.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                LifecycleLogMessages.StartupFailureCleanupError(_logger, ex);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        /// <summary>
        /// Commits a validated state change under <see cref="_stateSync"/>, logs it, then raises
        /// <see cref="StateChanged"/> outside the lock.
        /// </summary>
        /// <param name="to">Destination state.</param>
        /// <exception cref="InvalidOperationException">The pair is not in <see cref="ValidTransitions"/>.</exception>
        /// <remarks>
        /// Same-state calls return without logging. Moving to <see cref="ApplicationState.Stopping"/> or
        /// <see cref="ApplicationState.Stopped"/> sets <see cref="ShutdownRequested"/>.
        /// </remarks>
        private void Transition(ApplicationState to)
        {
            ApplicationState from;
            lock (_stateSync)
            {
                from = _state;
                if (from == to)
                {
                    return;
                }

                if (!ValidTransitions.Contains((from, to)))
                {
                    throw new InvalidOperationException(
                        $"Invalid application lifecycle transition from {from} to {to}.");
                }

                if (to is ApplicationState.Stopping or ApplicationState.Stopped)
                {
                    Interlocked.Exchange(ref _shutdownRequested, 1);
                }

                _state = to;
                LifecycleLogMessages.LifecycleTransition(
                    _logger,
                    (ApplicationStateLog)from,
                    (ApplicationStateLog)to);
            }

            RaiseStateChanged(from, to);
        }

        /// <summary>
        /// Invokes <see cref="StateChanged"/>. A handler exception is logged and does not undo the transition.
        /// </summary>
        /// <param name="from">State before the transition.</param>
        /// <param name="to">State after the transition.</param>
        private void RaiseStateChanged(ApplicationState from, ApplicationState to)
        {
            try
            {
                StateChanged?.Invoke(this, new ApplicationStateChangedEventArgs(from, to));
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                LifecycleLogMessages.StateChangedHandlerFailed(_logger, ex, from, to);
            }
        }

        /// <summary>
        /// While <see cref="ApplicationState.Running"/>, logs the termination and completes
        /// <see cref="UnexpectedTermination"/>. Events received in any other state are ignored.
        /// </summary>
        /// <param name="sender">The <see cref="ApplicationServiceManager"/> that raised the event.</param>
        /// <param name="e">Service name, exception, and whether execution completed without fault or cancellation.</param>
        private void OnUnexpectedServiceTermination(object? sender, UnexpectedServiceTerminationEventArgs e)
        {
            if (State != ApplicationState.Running)
            {
                return;
            }

            LifecycleLogMessages.UnexpectedServiceTerminationWhileRunning(
                _logger,
                e.Exception,
                e.ServiceName,
                e.CompletedNormally);

            _unexpectedTerminationTcs.TrySetResult();
        }
    }

    /// <summary>
    /// Provides data for <see cref="ApplicationLifecycle.StateChanged"/>.
    /// </summary>
    internal sealed class ApplicationStateChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ApplicationStateChangedEventArgs"/> class.
        /// </summary>
        /// <param name="fromState">State before the committed transition.</param>
        /// <param name="toState">State after the committed transition.</param>
        internal ApplicationStateChangedEventArgs(ApplicationState fromState, ApplicationState toState)
        {
            FromState = fromState;
            ToState = toState;
        }

        /// <summary>Gets the previous lifecycle state.</summary>
        private ApplicationState FromState { get; }

        /// <summary>Gets the new lifecycle state.</summary>
        internal ApplicationState ToState { get; }
    }
}
