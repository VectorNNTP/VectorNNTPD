using Microsoft.Extensions.Options;
using Serilog.Debugging;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Process-wide RabbitMQ connection owner: startup connect, generation-tagged replacement, and shutdown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RabbitMQ is a runtime invariant for hosts that register this service.
    /// <see cref="StartAsync"/> fails if an initial usable connection cannot be established, and it
    /// disposes any partial connect before returning. After start, a lost connection is not terminal:
    /// one watch task reconnects with backoff until a new generation is installed or shutdown cancels
    /// the loop. A connection that remains broker-blocked longer than
    /// <see cref="RabbitMqRuntimeOptions.ConnectionBlockedTimeoutSeconds"/> requests the same recovery
    /// path while the AMQP session may still be open. RabbitMQ.Client automatic recovery is disabled;
    /// this service is the only lifecycle owner.
    /// </para>
    /// <para>
    /// Callers obtain a generation snapshot with <see cref="TryGetCurrent"/>. A handle does not
    /// own, pin, or dispose the connection. Only the current generation may publish readiness,
    /// request recovery, or be retired as current.
    /// </para>
    /// <para>
    /// This service does not declare topology, create channels, publish, consume, or process messages.
    /// </para>
    /// </remarks>
    internal sealed class RabbitMqService : IRabbitMqService, IApplicationService, IAsyncDisposable
    {
        /// <summary>Opens broker connections. Does not keep them after <see cref="ConnectAndInstallAsync"/> publishes one.</summary>
        private readonly IRabbitMqConnectionFactory _connectionFactory;

        /// <summary>Bound options used for the initial connect and every reconnect.</summary>
        private readonly IOptions<RabbitMqOptions> _options;

        /// <summary>Read once in <see cref="StartAsync"/> to set <see cref="_connectionName"/>.</summary>
        private readonly IRabbitMqConnectionNameProvider _connectionNameProvider;

        /// <summary>Lifecycle logger. Credential values are not written here.</summary>
        private readonly ILogger<RabbitMqService> _logger;

        /// <summary>Clock for reconnect backoff and connect elapsed time.</summary>
        private readonly TimeProvider _timeProvider;

        /// <summary>Cancelled by <see cref="DisposeAsync"/> to stop <see cref="WatchConnectionAsync"/>. Disposed after that task ends.</summary>
        private readonly CancellationTokenSource _runCts = new();

        /// <summary>Serializes publication, retirement, and handle checks. Not held across broker I/O.</summary>
        private readonly Lock _gate = new();

        /// <summary>
        /// One-shot signal consumed by the watch loop. Replaced after each wait so a later loss is not dropped.
        /// </summary>
        private TaskCompletionSource _recoveryRequested = NewRecoverySource();

        /// <summary>Options projected at the start of <see cref="StartAsync"/>. <see langword="null"/> until then.</summary>
        private RabbitMqRuntimeOptions? _runtime;

        /// <summary>Client-provided connection name captured at start. Empty until then.</summary>
        private string _connectionName = string.Empty;

        /// <summary>Published generation, or <see langword="null"/> when none is current.</summary>
        private LiveConnection? _current;

        /// <summary>Watch loop started after the first successful connect. <see langword="null"/> before that and when start fails.</summary>
        private Task? _execution;

        /// <summary>Monotonic generation counter. Zero before the first successful <see cref="Publish"/>.</summary>
        private long _generation;

        /// <summary>
        /// <c>0</c> until <see cref="StartAsync"/> begins, then <c>1</c>. Reset to <c>0</c> when start fails so a later start can retry.
        /// </summary>
        private int _started;

        /// <summary><c>1</c> after the first <see cref="DisposeAsync"/>. Later dispose calls return immediately.</summary>
        private int _disposed;

        /// <summary>
        /// Set under <see cref="_gate"/> when shutdown starts. Blocks publication and ignores recovery requests.
        /// </summary>
        private bool _stopping;

        /// <summary>
        /// Cancels the pending blocked-duration timeout for the current generation.
        /// Owned under <see cref="_gate"/>; replaced only when a new blocked episode starts.
        /// </summary>
        private CancellationTokenSource? _blockedTimeoutCts;

        /// <summary>
        /// Creates the process connection owner, using <see cref="TimeProvider.System"/> for reconnect delays.
        /// </summary>
        /// <param name="connectionFactory">Opens broker connections. This service disposes each connection it publishes.</param>
        /// <param name="options">RabbitMQ options read during <see cref="StartAsync"/> and each reconnect.</param>
        /// <param name="connectionNameProvider">Supplies the client-provided connection name once at start.</param>
        /// <param name="logger">Lifecycle logger. This service does not log credentials.</param>
        public RabbitMqService(
            IRabbitMqConnectionFactory connectionFactory,
            IOptions<RabbitMqOptions> options,
            IRabbitMqConnectionNameProvider connectionNameProvider,
            ILogger<RabbitMqService> logger)
            : this(connectionFactory, options, connectionNameProvider, logger, TimeProvider.System)
        {
        }

        /// <summary>
        /// Creates the process connection owner with an explicit clock for reconnect delays and elapsed-time logs.
        /// </summary>
        /// <param name="connectionFactory">Opens broker connections. This service disposes each connection it publishes.</param>
        /// <param name="options">RabbitMQ options read during <see cref="StartAsync"/> and each reconnect.</param>
        /// <param name="connectionNameProvider">Supplies the client-provided connection name once at start.</param>
        /// <param name="logger">Lifecycle logger. This service does not log credentials.</param>
        /// <param name="timeProvider">Clock used for reconnect <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> and connect duration.</param>
        internal RabbitMqService(
            IRabbitMqConnectionFactory connectionFactory,
            IOptions<RabbitMqOptions> options,
            IRabbitMqConnectionNameProvider connectionNameProvider,
            ILogger<RabbitMqService> logger,
            TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(connectionFactory);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(connectionNameProvider);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(timeProvider);

            _connectionFactory = connectionFactory;
            _options = options;
            _connectionNameProvider = connectionNameProvider;
            _logger = logger;
            _timeProvider = timeProvider;
        }

        /// <inheritdoc />
        public string Name => "RabbitMQ";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <inheritdoc />
        public bool IsReady
        {
            get
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    return false;
                }

                var current = Volatile.Read(ref _current);
                return current is { Connection.IsOpen: true };
            }
        }

        /// <inheritdoc />
        public long ConnectionGeneration => Volatile.Read(ref _generation);

        /// <inheritdoc />
        public event EventHandler<RabbitMqConnectionReplacedEventArgs>? ConnectionReplaced;

        /// <summary>Gets the number of times a usable connection was installed (tests).</summary>
        internal int ConnectionCount { get; private set; }

        /// <summary>
        /// Gets whether <see cref="DisposeAsync"/> has set the shutdown flag.
        /// Recovery tests wait for this before releasing a blocked connection dispose.
        /// </summary>
        internal bool IsStopping
        {
            get
            {
                lock (_gate)
                {
                    return _stopping;
                }
            }
        }

        /// <inheritdoc />
        public bool TryGetCurrent(out RabbitMqConnectionHandle handle)
        {
            lock (_gate)
            {
                if (_stopping
                    || Volatile.Read(ref _disposed) == 1
                    || _current is not { } current
                    || !current.Connection.IsOpen)
                {
                    handle = default;
                    return false;
                }

                handle = new RabbitMqConnectionHandle(this, current.Connection, current.Generation);
                return true;
            }
        }

        /// <summary>
        /// Point-in-time check: <paramref name="handle"/> still names the published open generation.
        /// Not a pin; retirement and dispose can begin as soon as this method returns.
        /// </summary>
        internal bool IsHandleCurrent(in RabbitMqConnectionHandle handle)
        {
            lock (_gate)
            {
                return !_stopping
                    && Volatile.Read(ref _disposed) == 0
                    && _current is { } current
                    && current.Generation == handle.Generation
                    && handle.RefersTo(current.Connection)
                    && current.Connection.IsOpen;
            }
        }

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            try
            {
                _runtime = _options.Value.ToRuntimeOptions();
                _connectionName = _connectionNameProvider.GetConnectionName();
                await ConnectAndInstallAsync(startup: true, logConnect: true, cancellationToken).ConfigureAwait(false);
                _execution = WatchConnectionAsync(_runCts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RabbitMqLogMessages.StartupFailed(_logger, ex);
                await RetireCurrentAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref _started, 0);
                throw;
            }
            catch (OperationCanceledException)
            {
                await RetireCurrentAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref _started, 0);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await DisposeAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            lock (_gate)
            {
                _stopping = true;
                CancelBlockedTimeoutLocked();
            }

            await _runCts.CancelAsync().ConfigureAwait(false);

            var execution = _execution;
            if (execution is not null)
            {
                try
                {
                    await execution.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await RetireCurrentAsync().ConfigureAwait(false);
            RabbitMqLogMessages.Stopped(_logger);
            _runCts.Dispose();
        }

        /// <summary>
        /// Waits for each recovery signal and runs <see cref="RecoverAsync"/> until <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="cancellationToken">Shutdown token from <see cref="_runCts"/>. Cancellation ends the loop without a failure.</param>
        private async Task WatchConnectionAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await WaitForRecoveryRequestAsync(cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await RecoverAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        /// <summary>
        /// Retries connect with exponential backoff until a generation is installed or <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="cancellationToken">Shutdown token. Cancellation throws <see cref="OperationCanceledException"/>.</param>
        /// <remarks>
        /// There is no attempt limit. The first failure is logged as a reconnect failure; later failures are logged
        /// only when <see cref="ShouldAnnounceReconnect"/> is true. A failed attempt does not leave a partial connection published.
        /// </remarks>
        private async Task RecoverAsync(CancellationToken cancellationToken)
        {
            var runtime = _runtime
                ?? throw new InvalidOperationException("RabbitMQ runtime options have not been projected.");

            var attempt = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                attempt++;
                var delay = ComputeReconnectBackoff(runtime, attempt);
                var announce = ShouldAnnounceReconnect(runtime, attempt, delay);
                if (announce)
                {
                    RabbitMqLogMessages.ReconnectStarting(
                        _logger,
                        attempt,
                        delay.TotalMilliseconds,
                        ConnectionGeneration);
                }

                try
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                    await RetireCurrentAsync().ConfigureAwait(false);
                    var generation = await ConnectAndInstallAsync(startup: false, logConnect: announce, cancellationToken)
                        .ConfigureAwait(false);
                    RabbitMqLogMessages.ReconnectSucceeded(_logger, attempt, generation);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    if (attempt == 1)
                    {
                        RabbitMqLogMessages.ReconnectFailed(_logger, attempt, ex.Message);
                    }
                    else if (announce)
                    {
                        RabbitMqLogMessages.ReconnectStillFailing(_logger, attempt, ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// Opens one connection, publishes it as the next generation, and disposes the generation it replaced.
        /// </summary>
        /// <param name="startup">
        /// When <see langword="true"/>, a failure other than cancellation is logged as a connection failure.
        /// Reconnect failures are logged by <see cref="RecoverAsync"/> instead.
        /// </param>
        /// <param name="logConnect">When <see langword="true"/>, logs the connect attempt and the successful connect.</param>
        /// <param name="cancellationToken">Cancels the broker connect. A cancelled connect disposes any connection not yet published.</param>
        /// <returns>The generation installed by <see cref="Publish"/>.</returns>
        /// <exception cref="InvalidOperationException">
        /// The service is stopping, runtime options were not projected, or the opened connection is not usable.
        /// The unusable connection is disposed before the exception is thrown.
        /// </exception>
        /// <remarks>
        /// After <see cref="Publish"/> succeeds, <see cref="RaiseConnectionReplaced"/> isolates subscriber exceptions
        /// so they cannot fail this method or cause <see cref="RecoverAsync"/> to treat the install as failed.
        /// Failures before publication still dispose the unpublished connection and rethrow.
        /// </remarks>
        private async Task<long> ConnectAndInstallAsync(
            bool startup,
            bool logConnect,
            CancellationToken cancellationToken)
        {
            ThrowIfStopping();

            var runtime = _runtime
                ?? throw new InvalidOperationException("RabbitMQ runtime options have not been projected.");
            var hosts = string.Join(',', runtime.Hosts);
            var started = _timeProvider.GetTimestamp();

            if (logConnect)
            {
                RabbitMqLogMessages.Connecting(
                    _logger,
                    hosts,
                    runtime.Port,
                    runtime.VirtualHost,
                    _connectionName,
                    runtime.EnableSsl);
            }

            IRabbitMqConnection? connection = null;
            var installed = false;
            try
            {
                connection = await _connectionFactory
                    .ConnectAsync(_options.Value, _connectionName, cancellationToken)
                    .ConfigureAwait(false);

                if (!connection.IsOpen)
                {
                    RabbitMqLogMessages.ConnectionNotUsable(_logger);
                    await DisposeConnectionQuietlyAsync(connection).ConfigureAwait(false);
                    connection = null;
                    throw new InvalidOperationException("RabbitMQ connection opened but is not usable.");
                }

                var live = Publish(connection);
                installed = true;
                connection = null;

                var previous = live.Replaced;
                if (previous is not null)
                {
                    DetachConnectionHandlers(previous.Connection);
                    await DisposeConnectionQuietlyAsync(previous.Connection).ConfigureAwait(false);
                }

                var elapsedMs = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
                RabbitMqLogMessages.Connected(
                    _logger,
                    live.Current.Connection.Host,
                    live.Current.Connection.Port,
                    live.Current.Connection.VirtualHost,
                    live.Current.Connection.ClientProvidedName,
                    live.Current.Generation,
                    elapsedMs);

                // After Publish, the generation is authoritative. Subscriber failures must not fail connect
                // or cause RecoverAsync to retire this working connection.
                RaiseConnectionReplaced(live.Current.Generation, live.Current.Generation > 1);

                return live.Current.Generation;
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                if (!installed && connection is not null)
                {
                    await DisposeConnectionQuietlyAsync(connection).ConfigureAwait(false);
                }

                if (startup && ex is not OperationCanceledException)
                {
                    RabbitMqLogMessages.ConnectionFailed(
                        _logger,
                        ex,
                        hosts,
                        runtime.Port,
                        runtime.VirtualHost,
                        _connectionName,
                        _timeProvider.GetElapsedTime(started).TotalMilliseconds);
                }

                throw;
            }
        }

        /// <summary>
        /// Invokes each <see cref="ConnectionReplaced"/> subscriber after a generation is published.
        /// </summary>
        /// <param name="generation">Installed connection generation.</param>
        /// <param name="isReplacement"><see langword="true"/> when a prior generation existed.</param>
        /// <remarks>
        /// Handlers run one at a time outside <see cref="_gate"/>. A non-fatal exception from one subscriber is logged and
        /// does not prevent later subscribers from running, and does not invalidate the published generation.
        /// Logging of a subscriber failure is also contained so a non-fatal diagnostic failure cannot fail connect or recover.
        /// <see cref="ExceptionHelpers.IsFatal"/> failures propagate.
        /// </remarks>
        private void RaiseConnectionReplaced(long generation, bool isReplacement)
        {
            var handlers = ConnectionReplaced;
            if (handlers is null)
            {
                return;
            }

            var args = new RabbitMqConnectionReplacedEventArgs(generation, isReplacement);
            foreach (var handler in handlers.GetInvocationList()
                         .Cast<EventHandler<RabbitMqConnectionReplacedEventArgs>>())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    try
                    {
                        RabbitMqLogMessages.ConnectionReplacedHandlerFailed(
                            _logger,
                            ex,
                            generation,
                            DescribeConnectionReplacedSubscriber(handler));
                    }
                    catch (Exception logFailure) when (!ExceptionHelpers.IsFatal(logFailure))
                    {
                        try
                        {
                            SelfLog.WriteLine(
                                "ConnectionReplaced handler failure log failed after generation {0}: {1}",
                                generation,
                                logFailure);
                        }
                        catch (Exception selfLogFailure) when (!ExceptionHelpers.IsFatal(selfLogFailure))
                        {
                            // Handler and its diagnostic log are both best-effort after Publish.
                        }
                    }
                }
            }
        }

        /// <summary>Formats a multicast handler identity for structured logging.</summary>
        /// <param name="handler">Subscriber from <see cref="Delegate.GetInvocationList"/>.</param>
        /// <returns>Declaring type and method name, or a placeholder when metadata is unavailable.</returns>
        private static string DescribeConnectionReplacedSubscriber(
            EventHandler<RabbitMqConnectionReplacedEventArgs> handler)
        {
            var declaringType = handler.Method.DeclaringType?.FullName;
            if (string.IsNullOrEmpty(declaringType))
            {
                return handler.Method.Name;
            }

            return declaringType + "." + handler.Method.Name;
        }

        /// <summary>
        /// Subscribes to connection lifecycle events, then publishes <paramref name="connection"/> as the next generation.
        /// </summary>
        /// <param name="connection">Open connection to make current. The caller must not dispose it after a successful return.</param>
        /// <returns>The installed generation and the previous generation, if one was still published.</returns>
        /// <exception cref="InvalidOperationException">The service is stopping. Handlers are removed and the connection is not published.</exception>
        /// <remarks>
        /// Does not dispose <paramref name="connection"/> or the previous generation. The caller disposes the previous one.
        /// A close that races this method is still observed because the handlers are attached before the publish.
        /// Any pending blocked-duration timeout from a prior generation is cancelled when a new generation is published.
        /// </remarks>
        private PublishedConnection Publish(IRabbitMqConnection connection)
        {
            // Subscribe before making the instance current so a close or block that races the
            // publish is observed. Handlers still ignore a sender that is not current.
            AttachConnectionHandlers(connection);

            lock (_gate)
            {
                if (_stopping)
                {
                    DetachConnectionHandlers(connection);
                    throw new InvalidOperationException("RabbitMQ service is stopping.");
                }

                CancelBlockedTimeoutLocked();
                var previous = _current;
                _generation++;
                var current = new LiveConnection(connection, _generation);
                _current = current;
                ConnectionCount++;
                return new PublishedConnection(current, previous);
            }
        }

        /// <summary>Attaches loss, blocked, and unblocked handlers for a connection about to be published.</summary>
        /// <param name="connection">Connection whose events this service will observe.</param>
        private void AttachConnectionHandlers(IRabbitMqConnection connection)
        {
            connection.ConnectionLost += OnConnectionLost;
            connection.ConnectionBlocked += OnConnectionBlocked;
            connection.ConnectionUnblocked += OnConnectionUnblocked;
        }

        /// <summary>Detaches loss, blocked, and unblocked handlers from a retired or unpublished connection.</summary>
        /// <param name="connection">Connection that must no longer raise recovery work into this service.</param>
        private void DetachConnectionHandlers(IRabbitMqConnection connection)
        {
            connection.ConnectionLost -= OnConnectionLost;
            connection.ConnectionBlocked -= OnConnectionBlocked;
            connection.ConnectionUnblocked -= OnConnectionUnblocked;
        }

        /// <summary>
        /// Logs loss of the current connection and requests recovery. A retired sender is ignored.
        /// </summary>
        /// <param name="sender">Connection that raised the loss. Ignored unless it is still <see cref="_current"/>.</param>
        /// <param name="eventArgs">Broker reply code, text, and initiator. Logged and not otherwise interpreted.</param>
        /// <remarks>Does not dispose the connection and does not start reconnect inline. Shutdown drops the event.</remarks>
        private void OnConnectionLost(object? sender, RabbitMqConnectionLostEventArgs eventArgs)
        {
            long generation;
            lock (_gate)
            {
                // A retired connection must not request recovery or overwrite a newer generation.
                if (_stopping
                    || _current is not { } current
                    || !ReferenceEquals(current.Connection, sender))
                {
                    return;
                }

                CancelBlockedTimeoutLocked();
                generation = current.Generation;
            }

            RabbitMqLogMessages.ConnectionLost(
                _logger,
                generation,
                eventArgs.ReplyCode,
                eventArgs.ReplyText,
                eventArgs.Initiator);
            RequestRecovery();
        }

        /// <summary>
        /// Starts a single blocked-duration timeout for the current connection generation.
        /// Duplicate blocked notifications for the same episode are ignored.
        /// </summary>
        /// <param name="sender">Connection that raised the block. Ignored unless it is still <see cref="_current"/>.</param>
        /// <param name="eventArgs">Broker block reason preserved for timeout logging.</param>
        /// <remarks>
        /// Does not block the AMQP callback thread on I/O. The timeout uses <see cref="_timeProvider"/>
        /// elapsed-duration delay and requests recovery through <see cref="RequestRecovery"/> only if
        /// that same generation is still current and still blocked when the delay completes.
        /// </remarks>
        private void OnConnectionBlocked(object? sender, RabbitMqConnectionBlockedEventArgs eventArgs)
        {
            CancellationTokenSource cts;
            long generation;
            IRabbitMqConnection connection;
            int timeoutSeconds;
            lock (_gate)
            {
                if (_stopping
                    || _runtime is null
                    || _current is not { } current
                    || !ReferenceEquals(current.Connection, sender))
                {
                    return;
                }

                // One timer per continuous blocked episode; duplicates must not stack.
                if (_blockedTimeoutCts is not null)
                {
                    return;
                }

                timeoutSeconds = _runtime.ConnectionBlockedTimeoutSeconds;
                generation = current.Generation;
                connection = current.Connection;
                cts = CancellationTokenSource.CreateLinkedTokenSource(_runCts.Token);
                _blockedTimeoutCts = cts;
            }

            _ = ObserveBlockedTimeoutAsync(generation, connection, timeoutSeconds, eventArgs.Reason, cts);
        }

        /// <summary>
        /// Cancels a pending blocked-duration timeout when the current generation unblocks.
        /// </summary>
        /// <param name="sender">Connection that raised the unblock. Ignored unless it is still <see cref="_current"/>.</param>
        /// <param name="eventArgs">Unused.</param>
        private void OnConnectionUnblocked(object? sender, EventArgs eventArgs)
        {
            lock (_gate)
            {
                if (_stopping
                    || _current is not { } current
                    || !ReferenceEquals(current.Connection, sender))
                {
                    return;
                }

                CancelBlockedTimeoutLocked();
            }
        }

        /// <summary>
        /// Waits for the configured blocked duration, then requests recovery when that generation is still current.
        /// </summary>
        /// <param name="generation">Generation that started the blocked episode.</param>
        /// <param name="connection">Connection instance that must still be current at expiry.</param>
        /// <param name="timeoutSeconds">Configured continuous blocked-duration limit.</param>
        /// <param name="reason">Block reason captured at episode start.</param>
        /// <param name="cts">Episode cancellation source; must match <see cref="_blockedTimeoutCts"/> at expiry.</param>
        /// <remarks>
        /// <para>
        /// Linearization versus unblock/lost/shutdown: after the delay completes, recovery is claimed only by
        /// clearing <see cref="_blockedTimeoutCts"/> under <see cref="_gate"/> while it still references
        /// <paramref name="cts"/>. <see cref="CancelBlockedTimeoutLocked"/> (unblock, lost, retire, publish,
        /// dispose) nulls that field first, so a timeout that loses the race returns without
        /// <see cref="RequestRecovery"/>. A timeout that wins cannot be undone by a later unblock: the CTS
        /// slot is empty, and <see cref="RequestRecovery"/> is gated again on <see cref="_stopping"/>.
        /// </para>
        /// <para>
        /// Delay completion alone is not the claim; an unblock that cancels between completion and this lock
        /// still wins. Stale senders never reach <see cref="CancelBlockedTimeoutLocked"/> for another
        /// generation because blocked/unblocked handlers require <c>ReferenceEquals</c> with <see cref="_current"/>.
        /// </para>
        /// </remarks>
        private async Task ObserveBlockedTimeoutAsync(
            long generation,
            IRabbitMqConnection connection,
            int timeoutSeconds,
            string reason,
            CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), _timeProvider, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                // Includes ObjectDisposedException if the CTS was disposed between schedule and Token use.
                RabbitMqLogMessages.CallbackException(_logger, ex.Message);
                return;
            }

            var shouldRecover = false;
            lock (_gate)
            {
                // Claim point: only the CTS still installed for this episode may clear state or recover.
                if (!ReferenceEquals(_blockedTimeoutCts, cts))
                {
                    return;
                }

                _blockedTimeoutCts = null;
                shouldRecover = !_stopping
                    && _current is { } current
                    && current.Generation == generation
                    && ReferenceEquals(current.Connection, connection);
            }

            try
            {
                cts.Dispose();
            }
            catch (ObjectDisposedException ex)
            {
                RabbitMqLogMessages.BlockedTimeoutCtsAlreadyDisposed(_logger, ex, "Dispose");
            }

            if (!shouldRecover)
            {
                return;
            }

            RabbitMqLogMessages.ConnectionBlockedTimeout(_logger, generation, timeoutSeconds, reason);
            RequestRecovery();
        }

        /// <summary>
        /// Cancels and clears <see cref="_blockedTimeoutCts"/> under <see cref="_gate"/>.
        /// </summary>
        private void CancelBlockedTimeoutLocked()
        {
            var cts = _blockedTimeoutCts;
            _blockedTimeoutCts = null;
            if (cts is null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException ex)
            {
                RabbitMqLogMessages.BlockedTimeoutCtsAlreadyDisposed(_logger, ex, "Cancel");
            }

            try
            {
                cts.Dispose();
            }
            catch (ObjectDisposedException ex)
            {
                RabbitMqLogMessages.BlockedTimeoutCtsAlreadyDisposed(_logger, ex, "Dispose");
            }
        }

        /// <summary>
        /// Completes the current recovery signal. Concurrent calls collapse onto the same signal. No-op while stopping.
        /// </summary>
        private void RequestRecovery()
        {
            TaskCompletionSource source;
            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }

                source = _recoveryRequested;
            }

            source.TrySetResult();
        }

        /// <summary>
        /// Waits for <see cref="RequestRecovery"/>, then replaces a completed signal before recovery runs.
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait. The signal is left in place when the wait is cancelled.</param>
        /// <remarks>
        /// The replacement happens only after the observed signal has completed, so a loss of the generation
        /// installed by the following <see cref="RecoverAsync"/> is not dropped as an already-seen event.
        /// </remarks>
        private async Task WaitForRecoveryRequestAsync(CancellationToken cancellationToken)
        {
            Task wait;
            lock (_gate)
            {
                wait = _recoveryRequested.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Replace the consumed signal before RecoverAsync runs so a loss of the
            // newly installed generation is not dropped as an already-observed event.
            lock (_gate)
            {
                if (_recoveryRequested.Task.IsCompleted)
                {
                    _recoveryRequested = NewRecoverySource();
                }
            }
        }

        /// <summary>
        /// Unpublishes the current connection, cancels any blocked-duration timeout, detaches handlers, and disposes it.
        /// No-op when none is current.
        /// </summary>
        private async Task RetireCurrentAsync()
        {
            LiveConnection? current;
            lock (_gate)
            {
                current = _current;
                _current = null;
                CancelBlockedTimeoutLocked();
            }

            if (current is null)
            {
                return;
            }

            DetachConnectionHandlers(current.Connection);
            await DisposeConnectionQuietlyAsync(current.Connection).ConfigureAwait(false);
        }

        /// <summary>
        /// Disposes <paramref name="connection"/>. A dispose failure is logged and not rethrown.
        /// </summary>
        /// <param name="connection">Connection to dispose. May already be closed.</param>
        private async Task DisposeConnectionQuietlyAsync(IRabbitMqConnection connection)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                RabbitMqLogMessages.ConnectionDisposeFailed(_logger, ex);
            }
        }

        /// <summary>Throws when shutdown has started.</summary>
        /// <exception cref="InvalidOperationException">The service is stopping.</exception>
        private void ThrowIfStopping()
        {
            lock (_gate)
            {
                if (_stopping)
                {
                    throw new InvalidOperationException("RabbitMQ service is stopping.");
                }
            }
        }

        /// <summary>
        /// Returns whether this reconnect attempt should be logged. True for attempt 1 or when
        /// <paramref name="delay"/> has reached <see cref="RabbitMqRuntimeOptions.PoolReconnectMaxDelayMs"/>.
        /// </summary>
        /// <param name="options">Reconnect delay settings.</param>
        /// <param name="attempt">One-based attempt number for the current recovery.</param>
        /// <param name="delay">Delay computed for <paramref name="attempt"/>.</param>
        /// <returns><see langword="true"/> when the attempt is announced.</returns>
        private static bool ShouldAnnounceReconnect(RabbitMqRuntimeOptions options, int attempt, TimeSpan delay) =>
            attempt == 1 || delay.TotalMilliseconds >= options.PoolReconnectMaxDelayMs;

        /// <summary>
        /// Computes reconnect delay as <see cref="RabbitMqRuntimeOptions.PoolReconnectBaseDelayMs"/> times
        /// 2^(attempt-1), capped at <see cref="RabbitMqRuntimeOptions.PoolReconnectMaxDelayMs"/>.
        /// </summary>
        /// <param name="options">Base and maximum delay in milliseconds.</param>
        /// <param name="attempt">One-based attempt. Clamped to 1..30 before the power is applied.</param>
        /// <returns>The delay to wait before the next connect. No jitter is added.</returns>
        private static TimeSpan ComputeReconnectBackoff(RabbitMqRuntimeOptions options, int attempt)
        {
            var boundedAttempt = Math.Clamp(attempt, 1, 30);
            var exponential = Math.Pow(2, boundedAttempt - 1);
            var delayMs = options.PoolReconnectBaseDelayMs * exponential;
            delayMs = Math.Min(delayMs, options.PoolReconnectMaxDelayMs);
            return TimeSpan.FromMilliseconds(delayMs);
        }

        /// <summary>
        /// Creates a recovery signal whose continuations run asynchronously, so they do not resume under <see cref="_gate"/>.
        /// </summary>
        /// <returns>An incomplete <see cref="TaskCompletionSource"/>.</returns>
        private static TaskCompletionSource NewRecoverySource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>One published connection and the generation assigned to it. This type does not dispose the connection.</summary>
        /// <param name="connection">Open connection that is current for <paramref name="generation"/>.</param>
        /// <param name="generation">Generation assigned by <see cref="Publish"/>.</param>
        private sealed class LiveConnection(IRabbitMqConnection connection, long generation)
        {
            /// <summary>The published connection instance.</summary>
            internal IRabbitMqConnection Connection { get; } = connection;

            /// <summary>Generation assigned when this instance was published.</summary>
            internal long Generation { get; } = generation;
        }

        /// <summary>The generation just published and the generation it replaced, if one was still current.</summary>
        /// <param name="Current">Generation installed by <see cref="Publish"/>.</param>
        /// <param name="Replaced">Previous generation, or <see langword="null"/> when none was published.</param>
        private readonly record struct PublishedConnection(LiveConnection Current, LiveConnection? Replaced);
    }
}
