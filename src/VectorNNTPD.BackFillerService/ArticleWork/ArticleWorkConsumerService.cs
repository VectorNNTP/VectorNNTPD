using RabbitMQ.Client.Exceptions;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Logging;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.RabbitMq;
using VectorNNTPD.Common.Messaging.RabbitMq;


namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Hosts one consume session per desired NNTP slot when that backbone has usable NNTP capacity.
    /// Does not own the RabbitMQ connection. Declares per-backbone ArticleWork topology only for
    /// backbones that currently have usable capacity, immediately before consumers start.
    /// </summary>
    /// <remarks>
    /// Membership is reconciled on start, on a supervised interval, after a usable-capacity snapshot,
    /// and after a RabbitMQ connection replacement. Those paths share <see cref="_replaceGate"/> except
    /// <see cref="RetireCapacityAsync"/>. Tracked sessions are guarded by <see cref="_gate"/>.
    /// An unexpected reconcile-loop fault, or a pass that cannot declare topology for a desired consumer, is logged
    /// and the same loop waits with bounded backoff before trying again. A pass that starts every desired consumer,
    /// including a pass with nothing to start, resets that delay. Shutdown cancellation ends the loop and does not
    /// schedule another one.
    /// This type does not delete topology when capacity later drops to zero.
    /// </remarks>
    internal sealed class ArticleWorkConsumerService : IHostedService, IAsyncDisposable, IArticleWorkConsumerReconciliation
    {
        /// <summary>Delay waited by the background reconcile loop between successful membership passes.</summary>
        private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(15);

        /// <summary>Upper bound for the reconcile-loop backoff after repeated unexpected faults or topology-not-ready passes.</summary>
        private static readonly TimeSpan ReconcileBackoffCap = TimeSpan.FromMinutes(2);

        /// <summary>Process RabbitMQ connection owner. This service opens channels on it and does not open another connection.</summary>
        private readonly IRabbitMqService _connections;

        /// <summary>Validated runtime snapshot. Supplies prefetch, payload limit, and the disposal grace period.</summary>
        private readonly BackFillerRuntimeOptions _runtime;

        /// <summary>Admitted-work handler shared by pipelines created during reconciling.</summary>
        private readonly IArticleWorkHandler _handler;

        /// <summary>Response publisher shared by those pipelines. This service does not publish itself.</summary>
        private readonly IArticleWorkResponsePublisher _publisher;

        /// <summary>Consumer logger. This type does not write payloads or credentials.</summary>
        private readonly ILogger<ArticleWorkConsumerService> _logger;

        /// <summary>Provider catalogue read when building desired consume slots. An omitted catalogue is empty.</summary>
        private readonly IBackFillerProviderCatalog _catalog;

        /// <summary>Usable NNTP capacity gate. An omitted provider starts empty and raises no snapshots.</summary>
        private readonly IBackboneUsableCapacityProvider _capacity;

        /// <summary>Clock for the reconcile delay. Production uses <see cref="TimeProvider.System"/>.</summary>
        private readonly TimeProvider _time;

        /// <summary>
        /// Loop body. Null runs <see cref="ReconcileAsync"/>. Tests supply a faulting body without replacing the loop task.
        /// </summary>
        private readonly Func<CancellationToken, Task>? _reconcileLoopBody;

        /// <summary>Current reconcile delay in ticks. Doubled after an unexpected fault and reset after success.</summary>
        private long _reconcileDelayTicks = ReconcileInterval.Ticks;

        /// <summary>Unexpected reconcile-loop faults observed by this process. Shutdown cancellation is not counted.</summary>
        private int _unexpectedReconcileFaults;

        /// <summary>How many times the loop has armed its delay. Used by tests to observe backoff without a second loop.</summary>
        private int _reconcileWaitsStarted;

        /// <summary>
        /// Guards the session dictionary, <see cref="_stopping"/>, and publication of <see cref="_replaceTask"/>.
        /// Non-recursive. No caller enters it again on the same call stack.
        /// </summary>
        private readonly Lock _gate = new();

        /// <summary>Tracked sessions keyed by <see cref="ArticleWorkConsumerSession.SessionKey"/> using ordinal comparison.</summary>
        private readonly Dictionary<string, ArticleWorkConsumerSession> _sessions = new(StringComparer.Ordinal);

        /// <summary>Serializes start, reconcile, connection replacement, and shutdown session replacement.</summary>
        private readonly SemaphoreSlim _replaceGate = new(1, 1);

        /// <summary>Cancelled to stop the background reconcile loop. Disposed by shutdown.</summary>
        private readonly CancellationTokenSource _reconcileCts = new();

        /// <summary>Latest connection-replacement rebuild. A later replacement can overwrite it before the previous task is observed.</summary>
        private Task _replaceTask = Task.CompletedTask;

        /// <summary>Background reconcile loop. Null until start assigns it. Shutdown awaits it.</summary>
        private Task? _reconcileLoop;

        /// <summary>One after <see cref="StartAsync"/> has entered startup. Cleared when that attempt fails.</summary>
        private int _started;

        /// <summary>One after <see cref="ShutdownAsync"/> has entered shutdown.</summary>
        private int _disposed;

        /// <summary>Set under <see cref="_gate"/> once shutdown has begun. Blocks new replacement work and session publication.</summary>
        private bool _stopping;

        /// <summary>
        /// Cancellation source shared by replacement-triggered retirement calls.
        /// It remains not-cancelled during normal operation and is cancelled when shutdown budget is cancelled.
        /// </summary>
        private readonly CancellationTokenSource _replacementRetirementCts = new();

        /// <summary>
        /// Initializes a new consumer service.
        /// </summary>
        /// <param name="connections">Sole connection owner.</param>
        /// <param name="runtime">Validated runtime snapshot.</param>
        /// <param name="handler">Admitted-work handler.</param>
        /// <param name="publisher">Response-publish seam.</param>
        /// <param name="logger">Consumer logger.</param>
        /// <param name="catalog">Current provider snapshot. Empty when omitted.</param>
        /// <param name="capacity">Published usable NNTP capacity. Empty when omitted.</param>
        /// <param name="time">Clock for the reconcile delay. <see cref="TimeProvider.System"/> when omitted.</param>
        /// <param name="reconcileLoopBody">
        /// Optional loop body. Null reconciles membership. A thrown exception is supervised inside the same loop task.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connections"/>, <paramref name="runtime"/>, <paramref name="handler"/>,
        /// <paramref name="publisher"/>, or <paramref name="logger"/> is null.
        /// </exception>
        /// <remarks>Does not subscribe, declare topology, or start sessions. Omitted collaborators are empty stand-ins, not the host singletons.</remarks>
        internal ArticleWorkConsumerService(
            IRabbitMqService connections,
            BackFillerRuntimeOptions runtime,
            IArticleWorkHandler handler,
            IArticleWorkResponsePublisher publisher,
            ILogger<ArticleWorkConsumerService> logger,
            IBackFillerProviderCatalog? catalog = null,
            IBackboneUsableCapacityProvider? capacity = null,
            TimeProvider? time = null,
            Func<CancellationToken, Task>? reconcileLoopBody = null)
        {
            ArgumentNullException.ThrowIfNull(connections);
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(publisher);
            ArgumentNullException.ThrowIfNull(logger);
            _connections = connections;
            _runtime = runtime;
            _handler = handler;
            _publisher = publisher;
            _logger = logger;
            _catalog = catalog ?? new StaticBackFillerProviderCatalog();
            _capacity = capacity ?? new BackboneUsableCapacityState();
            _time = time ?? TimeProvider.System;
            _reconcileLoopBody = reconcileLoopBody;
        }

        /// <summary>
        /// Gets whether startup has been claimed, shutdown has not begun, and disposal has not been claimed.
        /// </summary>
        /// <remarks>
        /// True while <see cref="StartAsync"/> is in progress, because the start flag is set before sessions exist and is cleared only if that attempt fails.
        /// The read takes <see cref="_gate"/>.
        /// </remarks>
        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    return Volatile.Read(ref _started) == 1 && !_stopping && Volatile.Read(ref _disposed) == 0;
                }
            }
        }

        /// <summary>Background reconcile loop started by <see cref="StartAsync"/>. The same task continues after a fault.</summary>
        internal Task? ReconcileLoop => _reconcileLoop;

        /// <summary>Unexpected faults handled inside <see cref="RunReconcileLoopAsync"/>. Shutdown cancellation is excluded.</summary>
        internal int UnexpectedReconcileFaults => Volatile.Read(ref _unexpectedReconcileFaults);

        /// <summary>How many reconcile delays the loop has armed, including the delay that follows a fault.</summary>
        internal int ReconcileWaitsStarted => Volatile.Read(ref _reconcileWaitsStarted);

        /// <summary>Delay the loop will use for the wait it has armed or will arm next.</summary>
        internal TimeSpan CurrentReconcileDelay => TimeSpan.FromTicks(Volatile.Read(ref _reconcileDelayTicks));

        /// <summary>Gets a copy of the tracked sessions, taken under the session lock.</summary>
        /// <remarks>Intended for tests. The copy does not observe the latest dictionary changes.</remarks>
        internal IReadOnlyList<ArticleWorkConsumerSession> Sessions
        {
            get
            {
                lock (_gate)
                {
                    return [.. _sessions.Values];
                }
            }
        }

        /// <summary>
        /// Subscribes to connection replacement and capacity snapshots, reconciles sessions at once, and starts the reconciling loop.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancels the replacement-gate wait, the first reconciling, and topology declaration during that reconciling.
        /// A second call returns without waiting for an in-progress start.
        /// </param>
        /// <returns>
        /// A task that completes when the first reconciling has finished and the loop task has been stored,
        /// or immediately when start has already been entered.
        /// </returns>
        /// <remarks>
        /// On failure the subscriptions are removed, sessions started during the attempt are stopped, the start flag is cleared, and the exception propagates.
        /// The loop is not started when reconcile throws. A broker that is not ready does not throw, so the loop still starts and applies backoff on its next pass.
        /// A later call can retry.
        /// </remarks>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            await _replaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _connections.ConnectionReplaced += OnConnectionReplaced;
                _capacity.SnapshotPublished += OnCapacitySnapshotPublished;
                await ReconcileSessionsAsync(cancellationToken).ConfigureAwait(false);
                _reconcileLoop = RunReconcileLoopAsync(_reconcileCts.Token);
            }
            catch
            {
                _connections.ConnectionReplaced -= OnConnectionReplaced;
                _capacity.SnapshotPublished -= OnCapacitySnapshotPublished;
                await StopSessionsAsync(CancellationToken.None).ConfigureAwait(false);
                Interlocked.Exchange(ref _started, 0);
                throw;
            }
            finally
            {
                _replaceGate.Release();
            }
        }

        /// <summary>Shuts the consumer host down and retires tracked sessions with <paramref name="cancellationToken"/>.</summary>
        /// <param name="cancellationToken">
        /// Passed to session retirement. This token does not cancel the replacement-gate wait and the reconcile-loop wait.
        /// </param>
        /// <returns>A task that completes when shutdown finishes. A second call returns a completed task.</returns>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Shuts the consumer host down, bounding session retirement by <see cref="BackFillerShutdownRuntimeOptions.GracePeriod"/>.
        /// </summary>
        /// <returns>
        /// A task that completes when this call's shutdown finishes.
        /// When <see cref="ShutdownAsync"/> has already been entered, this call returns a completed task and does not wait for that earlier shutdown.
        /// </returns>
        /// <remarks>
        /// The grace token is created here and passed to <see cref="ShutdownAsync"/>. It is not the host stop token.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            using var grace = new CancellationTokenSource(_runtime.Shutdown.GracePeriod);
            await ShutdownAsync(grace.Token).ConfigureAwait(false);
        }

        /// <summary>Creates, keeps, or retires consume sessions to match desired membership.</summary>
        /// <param name="cancellationToken">Cancels the replacement-gate wait and the membership pass.</param>
        /// <returns>
        /// A task that completes when the pass finishes. Returns a completed task when <see cref="IsRunning"/> is false
        /// before the gate is taken or after it is acquired. Infrastructure failure is not thrown; the periodic loop
        /// reads it from <see cref="ReconcileCoreAsync"/>.
        /// </returns>
        /// <remarks>
        /// Serialized with start, connection replacement, and shutdown through <see cref="_replaceGate"/>.
        /// Does not catch exceptions from the membership pass. Callers other than the periodic loop do not change
        /// its backoff. A failed replacement or capacity pass is retried by that loop.
        /// </remarks>
        public Task ReconcileAsync(CancellationToken cancellationToken) =>
            ReconcileCoreAsync(cancellationToken);

        /// <summary>
        /// Runs one membership pass and reports whether every desired consumer was started.
        /// </summary>
        /// <param name="cancellationToken">Cancels the replacement-gate wait and the membership pass.</param>
        /// <returns>
        /// <see cref="ReconcilePass.Succeeded"/> when there was nothing to start or every desired slot is running.
        /// <see cref="ReconcilePass.InfrastructureUnavailable"/> when a desired backbone could not be declared because
        /// RabbitMQ was not ready or topology declaration failed. That result is not an unexpected exception.
        /// </returns>
        private async Task<ReconcilePass> ReconcileCoreAsync(CancellationToken cancellationToken)
        {
            if (!IsRunning)
            {
                return ReconcilePass.Succeeded;
            }

            await _replaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsRunning)
                {
                    return ReconcilePass.Succeeded;
                }

                return await ReconcileSessionsAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _replaceGate.Release();
            }
        }

        /// <summary>
        /// Retires tracked sessions for <paramref name="backbone"/> whose connection number is greater than <paramref name="retainConnectionCount"/>.
        /// </summary>
        /// <param name="backbone">Provider backbone to match, ordinal ignore-case.</param>
        /// <param name="retainConnectionCount">Highest connection number to keep. Zero retires every positive slot for that backbone.</param>
        /// <param name="cancellationToken">Passed to each selected session's retirement. Disposal of the session then uses no extra budget.</param>
        /// <returns>A task that completes when each selected session has been retired and disposed of.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="backbone"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="retainConnectionCount"/> is negative.</exception>
        /// <remarks>
        /// Sessions at or below the retained number stay tracked. Removal happens under <see cref="_gate"/>; retirement happens outside it.
        /// This method does not take <see cref="_replaceGate"/> and does not require <see cref="IsRunning"/>.
        /// </remarks>
        public async Task RetireCapacityAsync(
            string backbone,
            int retainConnectionCount,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
            ArgumentOutOfRangeException.ThrowIfNegative(retainConnectionCount);

            List<ArticleWorkConsumerSession> retire = [];
            lock (_gate)
            {
                foreach (var pair in _sessions.ToArray())
                {
                    if (!string.Equals(pair.Value.Backbone, backbone, StringComparison.OrdinalIgnoreCase)
                        || pair.Value.ConnectionNumber <= retainConnectionCount)
                    {
                        continue;
                    }

                    _sessions.Remove(pair.Key);
                    retire.Add(pair.Value);
                }
            }

            foreach (var session in retire)
            {
                await session.RetireAsync(cancellationToken).ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Unsubscribes, stops the reconciling loop, waits for the tracked replacement, and retires every tracked session.
        /// </summary>
        /// <param name="cancellationToken">
        /// Passed only to session retirement. The loop wait and the replacement-gate wait do not use this token.
        /// </param>
        /// <returns>A task that completes when sessions are stopped and the gate and reconcile source are disposed of. A second call returns immediately.</returns>
        /// <remarks>
        /// Sets <see cref="_stopping"/> before waiting. <see cref="OperationCanceledException"/> from the reconciling loop is swallowed.
        /// Unexpected loop faults are contained by the loop and do not fault this wait. A faulted replacement task is logged with its message, and shutdown continues.
        /// </remarks>
        private async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            lock (_gate)
            {
                _stopping = true;
            }

            _connections.ConnectionReplaced -= OnConnectionReplaced;
            _capacity.SnapshotPublished -= OnCapacitySnapshotPublished;
            using var shutdownRegistration = cancellationToken.Register(static state =>
            {
                var cts = (CancellationTokenSource)state!;
                cts.Cancel();
            }, _replacementRetirementCts);
            await _reconcileCts.CancelAsync().ConfigureAwait(false);
            var loop = _reconcileLoop;
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ShutdownObservations.Canceled(_logger, "article-work reconcile loop");
                }
            }

            Task replace;
            lock (_gate)
            {
                replace = _replaceTask;
            }

            try
            {
                await replace.ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] A background reconcile or replacement failure must be logged without faulting an unobserved task.
            catch (Exception ex)
            {
                ArticleWorkLogMessages.ConsumerReplaceFailed(_logger, ex.Message);
            }

            await _replaceGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await StopSessionsAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _replaceGate.Release();
                DisposeShutdownHandles();
            }
        }

        /// <summary>Releases the replacement gate and the reconcile cancellation sources after sessions have stopped.</summary>
        private void DisposeShutdownHandles()
        {
            _replaceGate.Dispose();
            _reconcileCts.Dispose();
            _replacementRetirementCts.Dispose();
        }

        /// <summary>
        /// Waits, then reconciles, until <paramref name="cancellationToken"/> is cancelled.
        /// An unexpected exception is logged and the same task waits again with bounded backoff.
        /// </summary>
        /// <param name="cancellationToken">Loop cancellation, owned by <see cref="_reconcileCts"/>.</param>
        /// <returns>A task that completes when shutdown cancels the delay or a reconciling. Other exceptions do not end the loop.</returns>
        private async Task RunReconcileLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _reconcileWaitsStarted);
                    await Task.Delay(CurrentReconcileDelay, _time, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (_reconcileLoopBody is null)
                        {
                            var pass = await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
                            if (pass == ReconcilePass.Succeeded)
                            {
                                Volatile.Write(ref _reconcileDelayTicks, ReconcileInterval.Ticks);
                            }
                            else
                            {
                                ArticleWorkLogMessages.ConsumerReconcileFailed(
                                    _logger,
                                    "provider topology is not ready");
                                IncreaseReconcileDelay();
                            }
                        }
                        else
                        {
                            await _reconcileLoopBody(cancellationToken).ConfigureAwait(false);
                            Volatile.Write(ref _reconcileDelayTicks, ReconcileInterval.Ticks);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    // codeql[cs/catch-of-all-exceptions] A reconcile fault must stay inside this loop so membership recovery continues.
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _unexpectedReconcileFaults);
                        ArticleWorkLogMessages.ConsumerReconcileFailed(_logger, ex.Message);
                        IncreaseReconcileDelay();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ShutdownObservations.Canceled(_logger, "article-work periodic reconcile");
            }
        }

        /// <summary>Doubles the reconcile delay up to <see cref="ReconcileBackoffCap"/>.</summary>
        private void IncreaseReconcileDelay()
        {
            var current = Volatile.Read(ref _reconcileDelayTicks);
            var doubled = current > (ReconcileBackoffCap.Ticks / 2)
                ? ReconcileBackoffCap.Ticks
                : current * 2;
            if (doubled > ReconcileBackoffCap.Ticks)
            {
                doubled = ReconcileBackoffCap.Ticks;
            }

            Volatile.Write(ref _reconcileDelayTicks, doubled);
        }

        /// <summary>Starts an untracked reconciling when the host is running.</summary>
        /// <param name="sender">Event sender. Not used.</param>
        /// <param name="eventArgs">Empty snapshot notice. Not used.</param>
        /// <remarks>
        /// The reconcile uses <see cref="CancellationToken.None"/>. Failures are logged by <see cref="ReconcileObservedAsync"/>
        /// and retried by <see cref="RunReconcileLoopAsync"/>. They do not replace that loop.
        /// </remarks>
        private void OnCapacitySnapshotPublished(object? sender, EventArgs eventArgs)
        {
            if (!IsRunning)
            {
                return;
            }

            _ = ReconcileObservedAsync();
        }

        /// <summary>
        /// Schedules an observed reconcile after a running session reports an unexpected channel fault.
        /// Does not open a new AMQP connection.
        /// </summary>
        private void OnSessionChannelFaulted()
        {
            if (!IsRunning)
            {
                return;
            }

            _ = ReconcileObservedAsync();
        }

        /// <summary>
        /// Returns whether a tracked session still owns an open broker consumer.
        /// <see cref="ArticleWorkConsumerState.Running"/> alone is not sufficient after channel failure.
        /// </summary>
        /// <param name="session">Tracked session candidate.</param>
        /// <returns>
        /// <see langword="true"/> when the session is running and its consume channel reports itself open.
        /// </returns>
        private static bool IsHealthyRunningSession(ArticleWorkConsumerSession session) =>
            session.State == ArticleWorkConsumerState.Running
            && session.Channel is { IsOpen: true };

        /// <summary>Reconciles after a capacity snapshot.</summary>
        /// <returns>
        /// A task that completes when <see cref="ReconcileAsync"/> finishes or its exception has been logged.
        /// The exception is not rethrown. Cancellation is not observed.
        /// </returns>
        private async Task ReconcileObservedAsync()
        {
            try
            {
                await ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] A background reconcile or replacement failure must be logged without faulting an unobserved task.
            catch (Exception ex)
            {
                ArticleWorkLogMessages.ConsumerReplaceFailed(_logger, ex.Message);
            }
        }

        /// <summary>
        /// Retires sessions that are no longer desired, declares topology for backbones that still need consumers, and starts the missing slots.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancels retirement of stale sessions, topology declaration, and each new session start.
        /// </param>
        /// <returns>
        /// <see cref="ReconcilePass.Succeeded"/> when no slot was desired or every desired slot was started.
        /// <see cref="ReconcilePass.InfrastructureUnavailable"/> when at least one desired backbone had no ready topology.
        /// Sessions whose topology did declare are still started.
        /// </returns>
        /// <remarks>
        /// A tracked running session whose key is still desired and whose consume channel is open is kept.
        /// Any other tracked session is removed and retired, including a Running session whose channel is closed.
        /// A desired key with no healthy tracked session is started. Prefetch is the configured positive count, otherwise 1.
        /// Sessions started in one pass share a new pipeline. A start failure disposes sessions started in that pass and propagates; they are not tracked.
        /// If shutdown is set before publication, those new sessions are disposed of instead of tracked.
        /// No desired slots is success. A desired slot whose topology is not ready is not success and does not log completion.
        /// </remarks>
        private async Task<ReconcilePass> ReconcileSessionsAsync(CancellationToken cancellationToken)
        {
            var pipeline = new ArticleWorkDeliveryPipeline(
                _handler,
                _publisher,
                _runtime.RabbitMq.WorkRequestMaxPayloadBytes);
            var prefetch = _runtime.RabbitMq.ConsumerPrefetchCount is { } configured and > 0
                ? configured
                : (ushort)1;

            var desired = BuildDesiredSessions();
            var started = new List<ArticleWorkConsumerSession>();
            List<ArticleWorkConsumerSession> stale = [];
            lock (_gate)
            {
                foreach (var pair in _sessions.ToArray())
                {
                    if (desired.ContainsKey(pair.Key) && IsHealthyRunningSession(pair.Value))
                    {
                        desired.Remove(pair.Key);
                        continue;
                    }

                    _sessions.Remove(pair.Key);
                    stale.Add(pair.Value);
                }
            }

            foreach (var session in stale)
            {
                await session.RetireAsync(cancellationToken).ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }

            var infrastructureUnavailable = false;
            if (desired.Count > 0)
            {
                var requestedBackbones = desired.Values
                    .Select(static item => item.Backbone)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var readyBackbones = await EnsureProviderTopologyAsync(
                        requestedBackbones,
                        cancellationToken)
                    .ConfigureAwait(false);
                infrastructureUnavailable = requestedBackbones.Any(backbone => !readyBackbones.Contains(backbone));
                if (readyBackbones.Count == 0)
                {
                    return ReconcilePass.InfrastructureUnavailable;
                }

                foreach (var pair in desired.ToArray().Where(pair => !readyBackbones.Contains(pair.Value.Backbone)))
                {
                    desired.Remove(pair.Key);
                }
            }

            try
            {
                foreach (var identity in desired.Values.OrderBy(static item => item.Backbone, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(static item => item.ConnectionNumber))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var session = new ArticleWorkConsumerSession(
                        identity.Backbone,
                        prefetch,
                        pipeline,
                        _connections,
                        _logger,
                        _runtime.Shutdown,
                        identity.ConnectionNumber,
                        identity.ConnectionLimit)
                    {
                        UnexpectedChannelFault = OnSessionChannelFaulted,
                    };
                    await session.StartAsync(cancellationToken).ConfigureAwait(false);
                    started.Add(session);
                }
            }
            catch
            {
                foreach (var session in started)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }

            lock (_gate)
            {
                if (_stopping)
                {
                    stale = started;
                }
                else
                {
                    foreach (var session in started)
                    {
                        _sessions[session.SessionKey] = session;
                    }

                    stale = [];
                }
            }

            foreach (var session in stale)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            if (infrastructureUnavailable)
            {
                return ReconcilePass.InfrastructureUnavailable;
            }

            ArticleWorkLogMessages.ConsumerReconcileCompleted(_logger, Sessions.Count);
            return ReconcilePass.Succeeded;
        }

        /// <summary>Result of one membership pass for the periodic reconcile loop.</summary>
        private enum ReconcilePass
        {
            /// <summary>Every desired consumer is running, or no consumer was desired.</summary>
            Succeeded,

            /// <summary>At least one desired backbone could not be declared. The periodic loop keeps its backoff.</summary>
            InfrastructureUnavailable,
        }

        /// <summary>
        /// Declares durable quorum fanout topology for each backbone that is about to receive
        /// consumers. Topology is never deleted when capacity later drops to zero.
        /// </summary>
        /// <param name="backbones">Backbones that still need consumers. Blank names are ignored. Comparison is ordinal ignore-case.</param>
        /// <param name="cancellationToken">Cancels channel creation and each declaration. Cancellation propagates.</param>
        /// <returns>
        /// Backbones whose declaration succeeded. Empty when there is nothing to declare, the connection is not ready, or every declaration failed.
        /// </returns>
        /// <remarks>
        /// Declarations reuse one manual-ack channel while it remains open. A failed declaration disposes that channel
        /// before the next backbone is attempted, because RabbitMQ closes the channel on broker-side declare failures
        /// such as <c>PRECONDITION_FAILED</c>. A later backbone therefore never reuses an unusable channel.
        /// The connection generation is rechecked before each channel open. <see cref="OperationCanceledException"/> is not logged.
        /// </remarks>
        private async Task<HashSet<string>> EnsureProviderTopologyAsync(
            IEnumerable<string> backbones,
            CancellationToken cancellationToken)
        {
            var ready = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var backbone in backbones.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
            {
                unique.Add(backbone);
            }

            if (unique.Count == 0)
            {
                return ready;
            }

            if (!_connections.TryGetCurrent(out _))
            {
                foreach (var backbone in unique)
                {
                    ArticleWorkLogMessages.ProviderTopologyDeclareFailed(
                        _logger,
                        backbone,
                        BackFillerRabbitMqTopology.ComposeProviderEntity(backbone),
                        "RabbitMQ connection is not ready for topology declaration.");
                }

                return ready;
            }

            IRabbitMqManualAckChannel? channel = null;
            try
            {
                foreach (var backbone in unique.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var queue = BackFillerRabbitMqTopology.ComposeProviderEntity(backbone);
                    try
                    {
                        channel = await EnsureOpenTopologyChannelAsync(channel, cancellationToken)
                            .ConfigureAwait(false);
                        if (channel is null)
                        {
                            ArticleWorkLogMessages.ProviderTopologyDeclareFailed(
                                _logger,
                                backbone,
                                queue,
                                "RabbitMQ connection is not ready for topology declaration.");
                            continue;
                        }

                        await BackFillerArticleWorkTopology
                            .DeclareProviderEndpointAsync(channel, backbone, cancellationToken)
                            .ConfigureAwait(false);
                        ready.Add(backbone);
                        ArticleWorkLogMessages.ProviderTopologyDeclared(_logger, backbone, queue);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ArticleWorkLogMessages.ProviderTopologyDeclareFailed(
                            _logger,
                            backbone,
                            queue,
                            FormatTopologyDeclareFailure(ex));
                        channel = await DisposeTopologyChannelAsync(channel).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _ = await DisposeTopologyChannelAsync(channel).ConfigureAwait(false);
            }

            return ready;
        }

        /// <summary>
        /// Returns <paramref name="channel"/> when it is still open; otherwise disposes it and opens a new channel on the current generation.
        /// </summary>
        /// <param name="channel">Previous topology channel, or <see langword="null"/>.</param>
        /// <param name="cancellationToken">Cancels channel creation.</param>
        /// <returns>
        /// An open manual-ack channel, or <see langword="null"/> when no current connection is available.
        /// </returns>
        private async Task<IRabbitMqManualAckChannel?> EnsureOpenTopologyChannelAsync(
            IRabbitMqManualAckChannel? channel,
            CancellationToken cancellationToken)
        {
            if (channel is { IsOpen: true })
            {
                return channel;
            }

            _ = await DisposeTopologyChannelAsync(channel).ConfigureAwait(false);
            if (!_connections.TryGetCurrent(out var handle) || !handle.IsCurrent)
            {
                return null;
            }

            return await handle.Connection
                .CreateManualAckChannelAsync(handle.Generation, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>Disposes <paramref name="channel"/> when non-null and returns <see langword="null"/>.</summary>
        /// <param name="channel">Topology channel to release.</param>
        /// <returns><see langword="null"/> so the caller can clear its local handle.</returns>
        private static async Task<IRabbitMqManualAckChannel?> DisposeTopologyChannelAsync(
            IRabbitMqManualAckChannel? channel)
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }

            return null;
        }

        /// <summary>
        /// Formats a topology declaration failure, preferring structured AMQP shutdown details when present.
        /// </summary>
        /// <param name="exception">Failure from channel creation or declare/bind.</param>
        /// <returns>Stable log text that includes reply code and text for broker channel closes.</returns>
        private static string FormatTopologyDeclareFailure(Exception exception)
        {
            if (exception is OperationInterruptedException interrupted
                && interrupted.ShutdownReason is { } interruptedShutdown)
            {
                return FormatAmqpShutdownFailure(interruptedShutdown, interrupted.Message);
            }

            if (exception is AlreadyClosedException alreadyClosed
                && alreadyClosed.ShutdownReason is { } closedShutdown)
            {
                return FormatAmqpShutdownFailure(closedShutdown, alreadyClosed.Message);
            }

            return exception.Message;
        }

        /// <summary>Formats broker channel-close metadata without credentials or payload bytes.</summary>
        /// <param name="shutdown">Client shutdown details from the failed RPC.</param>
        /// <param name="fallbackMessage">Exception message appended for context.</param>
        /// <returns>Log text including reply code, reply text, and initiator.</returns>
        private static string FormatAmqpShutdownFailure(
            RabbitMQ.Client.Events.ShutdownEventArgs shutdown,
            string fallbackMessage)
        {
            var replyText = string.IsNullOrWhiteSpace(shutdown.ReplyText)
                ? "(none)"
                : shutdown.ReplyText;
            return $"AMQP channel closed replyCode={shutdown.ReplyCode} text={replyText} initiator={shutdown.Initiator}: {fallbackMessage}";
        }

        /// <summary>
        /// Builds one desired slot for each connection number from 1 through <see cref="BackFillerProviderDefinition.MaxSessions"/>
        /// when that provider has a positive limit and usable NNTP capacity.
        /// </summary>
        /// <returns>
        /// Desired slots keyed by <see cref="ArticleWorkConsumerSession.ComposeSessionKey"/>. Providers with no usable capacity are omitted.
        /// </returns>
        private Dictionary<string, DesiredConsumer> BuildDesiredSessions()
        {
            var desired = new Dictionary<string, DesiredConsumer>(StringComparer.Ordinal);
            foreach (var provider in _catalog.Providers)
            {
                if (provider.MaxSessions <= 0)
                {
                    continue;
                }

                if (!_capacity.HasUsableCapacityForBackbone(provider.Backbone))
                {
                    continue;
                }

                for (var connectionNumber = 1; connectionNumber <= provider.MaxSessions; connectionNumber++)
                {
                    var key = ArticleWorkConsumerSession.ComposeSessionKey(provider.Backbone, connectionNumber);
                    desired[key] = new DesiredConsumer(provider.Backbone, connectionNumber, provider.MaxSessions);
                }
            }

            return desired;
        }

        /// <summary>Removes every tracked session and retires then disposes each one.</summary>
        /// <param name="cancellationToken">Passed to retirement. <see cref="CancellationToken.None"/> when retirement must not be cancelled.</param>
        /// <returns>A task that completes when every removed session has been retired and disposed of.</returns>
        private async Task StopSessionsAsync(CancellationToken cancellationToken)
        {
            List<ArticleWorkConsumerSession> sessions;
            lock (_gate)
            {
                sessions = [.. _sessions.Values];
                _sessions.Clear();
            }

            await RetireTrackedSessionsAsync(sessions, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Retires and disposes sessions that have already been removed from <see cref="_sessions"/>.</summary>
        /// <param name="sessions">Sessions to retire. The caller has removed them from the tracked set.</param>
        /// <param name="cancellationToken">Passed to retirement. Disposal uses no extra budget.</param>
        /// <returns>A task that completes when every session has been retired and disposed of.</returns>
        /// <remarks>Does not take <see cref="_gate"/>. A failure from one session prevents later sessions in <paramref name="sessions"/> from being retired.</remarks>
        private static async Task RetireTrackedSessionsAsync(
            List<ArticleWorkConsumerSession> sessions,
            CancellationToken cancellationToken)
        {
            foreach (var session in sessions)
            {
                await session.RetireAsync(cancellationToken).ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Schedules a session rebuild when the event is a connection replacement and shutdown has not started.</summary>
        /// <param name="sender">Event sender. Not used.</param>
        /// <param name="eventArgs">Replacement notice. Non-replacement events are ignored.</param>
        /// <remarks>
        /// Under <see cref="_gate"/>, a non-stopping host publishes <see cref="_replaceTask"/> and joins <see cref="_replaceGate"/>.
        /// When that wait is already satisfied, tracked sessions are removed in the same critical section.
        /// The rebuild body runs only after this method releases <see cref="_gate"/>, so the non-recursive lock is not re-entered.
        /// A later replacement overwrites <see cref="_replaceTask"/>; an earlier rebuild that is still running is no longer tracked.
        /// A completed unsuccessful replacement-gate wait is published as that task and is not observed on this stack.
        /// </remarks>
        private void OnConnectionReplaced(object? sender, RabbitMqConnectionReplacedEventArgs eventArgs)
        {
            if (!eventArgs.IsReplacement)
            {
                return;
            }

            TaskCompletionSource? started = null;
            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }

                var replaceGateWait = _replaceGate.WaitAsync();
                if (replaceGateWait.IsCompletedSuccessfully)
                {
                    if (Volatile.Read(ref _started) == 0)
                    {
                        _replaceGate.Release();
                        _replaceTask = Task.CompletedTask;
                        return;
                    }

                    List<ArticleWorkConsumerSession> removed = [.. _sessions.Values];
                    _sessions.Clear();
                    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _replaceTask = FinishAcquiredReplacementAsync(removed, started.Task);
                }
                else if (!replaceGateWait.IsCompleted)
                {
                    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _replaceTask = FinishQueuedReplacementAsync(replaceGateWait, started.Task);
                }
                else
                {
                    _replaceTask = replaceGateWait;
                }
            }

            started?.TrySetResult();
        }

        /// <summary>
        /// Retires sessions already removed under <see cref="_gate"/> and reconciles unless shutdown has started.
        /// The caller owns <see cref="_replaceGate"/>.
        /// </summary>
        /// <param name="removed">Sessions removed before this task was published.</param>
        /// <param name="started">Completed after <see cref="OnConnectionReplaced"/> releases <see cref="_gate"/>.</param>
        /// <returns>A task that completes when the rebuild attempt finishes. Failures are logged and not rethrown.</returns>
        /// <remarks>
        /// An infrastructure-unavailable membership result is not a thrown failure and does not change the periodic loop's delay.
        /// That loop retries the missing sessions.
        /// </remarks>
        private async Task FinishAcquiredReplacementAsync(
            List<ArticleWorkConsumerSession> removed,
            Task started)
        {
            await started.ConfigureAwait(false);
            try
            {
                await RetireTrackedSessionsAsync(removed, _replacementRetirementCts.Token).ConfigureAwait(false);
                if (ReplacementStopped())
                {
                    return;
                }

                await ReconcileSessionsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] A background reconcile or replacement failure must be logged without faulting an unobserved task.
            catch (Exception ex)
            {
                ArticleWorkLogMessages.ConsumerReplaceFailed(_logger, ex.Message);
            }
            finally
            {
                _replaceGate.Release();
            }
        }

        /// <summary>
        /// Waits for an already queued <see cref="_replaceGate"/> acquisition, then runs a full rebuild.
        /// </summary>
        /// <param name="replaceGateWait">Wait started under <see cref="_gate"/> while the gate was busy.</param>
        /// <param name="started">Completed after <see cref="OnConnectionReplaced"/> releases <see cref="_gate"/>.</param>
        /// <returns>A task that completes when the rebuild attempt finishes. Failures are logged and not rethrown.</returns>
        /// <remarks>
        /// <paramref name="replaceGateWait"/> is awaited before the rebuild body. A fault from that wait is not logged here
        /// and does not release <see cref="_replaceGate"/>, because this method does not own the gate until the wait succeeds.
        /// </remarks>
        private async Task FinishQueuedReplacementAsync(Task replaceGateWait, Task started)
        {
            await started.ConfigureAwait(false);
            await replaceGateWait.ConfigureAwait(false);
            try
            {
                await RunReplacementBodyAsync().ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] A background reconcile or replacement failure must be logged without faulting an unobserved task.
            catch (Exception ex)
            {
                ArticleWorkLogMessages.ConsumerReplaceFailed(_logger, ex.Message);
            }
            finally
            {
                _replaceGate.Release();
            }
        }

        /// <summary>Stops every tracked session and reconciles again unless shutdown has started or start did not succeed.</summary>
        /// <returns>
        /// A task that completes when the rebuild attempt finishes. Failures are logged with the exception message and not rethrown.
        /// The reconcile uses <see cref="CancellationToken.None"/>.
        /// </returns>
        private async Task RunReplacementBodyAsync()
        {
            if (ReplacementStopped() || Volatile.Read(ref _started) == 0)
            {
                return;
            }

            await StopSessionsAsync(_replacementRetirementCts.Token).ConfigureAwait(false);
            if (ReplacementStopped())
            {
                return;
            }

            await ReconcileSessionsAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>Reads <see cref="_stopping"/> under <see cref="_gate"/>.</summary>
        /// <returns><see langword="true"/> when shutdown has already set the flag.</returns>
        private bool ReplacementStopped()
        {
            lock (_gate)
            {
                return _stopping;
            }
        }

        /// <summary>One desired consume slot derived from the provider catalogue and usable capacity.</summary>
        /// <param name="Backbone">Provider backbone label, not case-folded.</param>
        /// <param name="ConnectionNumber">One-based slot in the range 1 through <paramref name="ConnectionLimit"/>.</param>
        /// <param name="ConnectionLimit">Provider <see cref="BackFillerProviderDefinition.MaxSessions"/> for this backbone.</param>
        private readonly record struct DesiredConsumer(string Backbone, int ConnectionNumber, int ConnectionLimit);
    }
}
