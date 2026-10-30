using System.Collections.Concurrent;
using System.Security.Authentication;
using VectorNNTPD.BackFillerService.Logging;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Acme;
using System.Net;
using System.Net.Sockets;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.Common.Networking.Listeners;
using VectorNNTPD.BackFillerService.Networking;
using VectorNNTPD.Common.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;

namespace VectorNNTPD.BackFillerService.Listener
{
    /// <summary>
    /// Process-wide TLS listener: binds configured endpoints, authenticates TLS, and serves retained
    /// CanonicalV1 articles with a partial RFC 3977 session. Does not own retention, RabbitMQ, or Article Work settlement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Start requires <see cref="IAcmeCertificateReadiness.IsReady"/> and
    /// <see cref="ITlsCertificateContextProvider.IsAvailable"/>. Bind plans come from
    /// <see cref="ListenEndpointPlanner.Plan"/>. Each admitted socket is started through
    /// <see cref="NntpConnection.StartTlsAsync"/> (shared Common pipeline transport) and one
    /// <see cref="NntpCacheListenerSession"/>. The certificate lease is acquired by the shared
    /// connection and disposed with that connection. This service does not reload retired certificate generations.
    /// The session does not own the socket or the certificate.
    /// </para>
    /// <para>
    /// Admission is a compare-and-swap on <see cref="ActiveConnections"/> against
    /// <see cref="BackFillerListenerRuntimeOptions.MaxActiveConnections"/>. Each admitted socket has Nagle
    /// disabled and receives the configured send and receive buffer limits before TLS. Those options are
    /// not taken from the listening socket. The handshake timeout and
    /// no-progress deadline for each read or non-empty write comes from <see cref="BackFillerRuntimeOptions.Listener"/>.
    /// <see cref="StopAsync"/> does not observe its cancellation token.
    /// <see cref="StartAsync"/> forwards its token only to listener-bound DNS publication.
    /// </para>
    /// </remarks>
    internal sealed class CacheListenerService : IHostedService, IApplicationService, IAsyncDisposable
    {
        /// <summary>Validated runtime snapshot. Bind addresses, TLS port, certificate directory, and listener bounds are read from it.</summary>
        private readonly BackFillerRuntimeOptions _runtime;

        /// <summary>Supplies the server certificate context for each handshake. Leases are taken per connection.</summary>
        private readonly ITlsCertificateContextProvider _certificates;

        /// <summary>Retention authority passed to each <see cref="NntpCacheListenerSession"/>. This service does not retain articles.</summary>
        private readonly IArticleRetentionAuthority _retention;

        /// <summary>Clock passed to each session for <c>DATE</c>. The same instance retention uses for admission.</summary>
        private readonly TimeProvider _time;

        /// <summary>ACME readiness gate. Start throws when <see cref="IAcmeCertificateReadiness.IsReady"/> is false.</summary>
        private readonly IAcmeCertificateReadiness _readiness;

        /// <summary>Startup journal. <see cref="BackFillerStartupStages.ListenerStarted"/> is recorded only after the running transition.</summary>
        private readonly IBackFillerStartupJournal _journal;

        /// <summary>Logger for <see cref="CacheListenerLogMessages"/>.</summary>
        private readonly ILogger<CacheListenerService> _logger;

        /// <summary>
        /// Publishes Cloudflare A/AAAA after the listener binds. Null in tests that do not reconcile DNS.
        /// </summary>
        private readonly IListenerDnsPublication? _publication;

        /// <summary>
        /// Non-recursive lock for <see cref="_state"/> and <see cref="_listeners"/>.
        /// No caller enters it again on the same call stack.
        /// </summary>
        private readonly Lock _gate = new();

        /// <summary>Bound accept listeners. Mutated only while <see cref="_gate"/> is held.</summary>
        private readonly List<SocketAcceptListener> _listeners = [];

        /// <summary>
        /// In-flight connection tasks. The value is unused. Entries are removed when the task completes.
        /// </summary>
        private readonly ConcurrentDictionary<Task, byte> _connections = new();

        /// <summary>Cancels accept loops and admitted connection work. Disposed by <see cref="DisposeAsync"/>.</summary>
        private readonly CancellationTokenSource _runCts = new();

        /// <summary>Accept loops for every bound socket. Null until start has bound sockets and published the running state.</summary>
        private Task? _acceptTask;

        /// <summary>Lifecycle published under <see cref="_gate"/>. Starts at <see cref="CacheListenerState.Created"/>.</summary>
        private CacheListenerState _state = CacheListenerState.Created;

        /// <summary>One after a start attempt begins. Cleared when that attempt fails. Not cleared by disposal.</summary>
        private int _started;

        /// <summary>One after the first <see cref="DisposeAsync"/>. Later dispose and stop calls return immediately.</summary>
        private int _disposed;

        /// <summary>Connections that passed <see cref="TryAdmit"/> and have not yet reached <see cref="ReleaseAdmit"/>.</summary>
        private int _activeConnections;

        /// <summary>
        /// Delay before retrying <see cref="Socket.AcceptAsync(CancellationToken)"/> after a transient
        /// <see cref="SocketException"/>. Cancellation-aware via <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>.
        /// </summary>
        internal static readonly TimeSpan AcceptFailureRetryDelay = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Consecutive accept <see cref="SocketException"/> failures on one listen socket before the accept loop faults.
        /// A faulted <see cref="Execution"/> is observed by
        /// <see cref="VectorNNTPD.BackFillerService.Core.ApplicationServiceManager"/> as unexpected termination
        /// (host unhealthy / stop).
        /// </summary>
        internal const int MaxConsecutiveAcceptFailures = 5;

        /// <summary>
        /// Minimum interval between Warning-level accept-failure logs for one accept loop.
        /// The first failure, the threshold-crossing failure, and failures after this interval are logged.
        /// </summary>
        internal static readonly TimeSpan AcceptFailureLogInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Invoked after admission and before <see cref="TcpSocketOptions.Apply"/> on an accepted socket.
        /// Null in production. A <see cref="SocketException"/> or <see cref="ObjectDisposedException"/> from this
        /// delegate is treated as a socket-option failure: the socket is disposed and the admission slot is released.
        /// </summary>
        internal Action<Socket>? BeforeAcceptedSocketOptions { get; set; }

        /// <summary>
        /// Invoked after socket options are applied and before TLS. Null in production.
        /// Exceptions from this delegate are not caught by the accept loop.
        /// </summary>
        internal Action<Socket>? AfterAcceptedSocketConfigured { get; set; }

        /// <summary>
        /// Replaces <see cref="Socket.AcceptAsync(CancellationToken)"/> when non-null. Null in production.
        /// Tests use this to inject deterministic accept failures without closing the listen socket.
        /// </summary>
        internal Func<Socket, CancellationToken, ValueTask<Socket>>? AcceptAsyncOverride { get; set; }

        /// <summary>
        /// Replaces the post-failure accept backoff when non-null. Null in production uses
        /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> on <see cref="AcceptFailureRetryDelay"/>.
        /// </summary>
        internal Func<TimeSpan, CancellationToken, Task>? AcceptFailureDelayAsync { get; set; }

        /// <summary>Initializes the Listener service for isolated tests with a pre-ready certificate gate.</summary>
        /// <param name="runtime">Runtime snapshot. Must not be null.</param>
        /// <param name="certificates">Certificate provider. Must not be null.</param>
        /// <param name="retention">Retention authority handed to NNTP sessions. Must not be null.</param>
        /// <param name="logger">Listener logger. Must not be null.</param>
        /// <remarks>
        /// Marks a new <see cref="AcmeCertificateReadiness"/> ready and uses a new
        /// <see cref="BackFillerStartupJournal"/>. Null arguments throw from the chained constructor.
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        internal CacheListenerService(
            BackFillerRuntimeOptions runtime,
            ITlsCertificateContextProvider certificates,
            IArticleRetentionAuthority retention,
            ILogger<CacheListenerService> logger)
            : this(runtime, certificates, retention, CreateReadyGate(), new BackFillerStartupJournal(), logger)
        {
        }

        /// <summary>
        /// Creates the production listener with injectable ACME readiness and startup journal dependencies.
        /// </summary>
        /// <param name="runtime">Runtime snapshot. Must not be null.</param>
        /// <param name="certificates">Certificate provider. Must not be null.</param>
        /// <param name="retention">Retention authority handed to NNTP sessions. Must not be null.</param>
        /// <param name="readiness">Certificate readiness gate consulted by <see cref="StartAsync"/>.</param>
        /// <param name="journal">Startup journal that records listener start.</param>
        /// <param name="logger">Listener logger. Must not be null.</param>
        /// <param name="time">
        /// Clock for listener <c>DATE</c>. Null uses <see cref="TimeProvider.System"/>.
        /// Production passes the retention <see cref="TimeProvider"/>.
        /// </param>
        /// <param name="publication">
        /// DNS publication invoked after a successful bind. Null skips publication.
        /// Production passes <see cref="IListenerDnsPublication"/> when the listener-bound lease is registered.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when any required argument is null.</exception>
        internal CacheListenerService(
            BackFillerRuntimeOptions runtime,
            ITlsCertificateContextProvider certificates,
            IArticleRetentionAuthority retention,
            IAcmeCertificateReadiness readiness,
            IBackFillerStartupJournal journal,
            ILogger<CacheListenerService> logger,
            TimeProvider? time = null,
            IListenerDnsPublication? publication = null)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(certificates);
            ArgumentNullException.ThrowIfNull(retention);
            ArgumentNullException.ThrowIfNull(readiness);
            ArgumentNullException.ThrowIfNull(journal);
            ArgumentNullException.ThrowIfNull(logger);
            _runtime = runtime;
            _certificates = certificates;
            _retention = retention;
            _readiness = readiness;
            _journal = journal;
            _logger = logger;
            _time = time ?? TimeProvider.System;
            _publication = publication;
        }

        /// <summary>Builds a readiness gate that is already marked ready for the test constructor.</summary>
        /// <returns>A new <see cref="AcmeCertificateReadiness"/> after <c>MarkReady</c>.</returns>
        private static AcmeCertificateReadiness CreateReadyGate()
        {
            var readiness = new AcmeCertificateReadiness();
            readiness.MarkReady();
            return readiness;
        }

        /// <summary>Gets the supervised-service name <c>CacheListener</c>.</summary>
        public string Name => "CacheListener";

        /// <summary>
        /// Gets the accept-loop task while the listener is running, or <see langword="null"/> before start and after the task is cleared.
        /// </summary>
        /// <remarks>
        /// <see cref="VectorNNTPD.BackFillerService.Core.ApplicationServiceManager"/> treats a faulted or unexpectedly completed task as termination.
        /// The task is <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{System.Threading.Tasks.Task})"/> over each
        /// <see cref="SocketAcceptListener.AcceptLoopTask"/>.
        /// </remarks>
        public Task? Execution => _acceptTask;

        /// <summary>Gets the local lifecycle state.</summary>
        /// <remarks>The read takes <see cref="_gate"/>. Concurrent start or dispose can change the value immediately afterward.</remarks>
        internal CacheListenerState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        /// <summary>Gets currently admitted connections.</summary>
        /// <remarks>
        /// Incremented when <see cref="TryAdmit"/> wins the slot and decremented in the connection <c>finally</c>.
        /// A rejected connection does not change the count.
        /// </remarks>
        internal int ActiveConnections => Volatile.Read(ref _activeConnections);

        /// <summary>Gets bound listen endpoints (tests).</summary>
        /// <remarks>
        /// Copies <see cref="SocketAcceptListener.LocalEndPoint"/> for each listener currently in
        /// <see cref="_listeners"/> while <see cref="_gate"/> is held.
        /// </remarks>
        internal IReadOnlyList<EndPoint?> LocalEndPoints
        {
            get
            {
                lock (_gate)
                {
                    return [.. _listeners.Select(static listener => (EndPoint?)listener.LocalEndPoint)];
                }
            }
        }

        /// <summary>
        /// Binds listen sockets and starts accept loops when the certificate gate and provider are ready.
        /// </summary>
        /// <param name="cancellationToken">Not observed.</param>
        /// <returns>A task that completes when the accept loops have been started, or when start has failed.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the ACME certificate is not ready, no TLS certificate is available, the bind plan is empty,
        /// or every bind attempt was skipped.
        /// </exception>
        /// <remarks>
        /// A second call returns immediately while <see cref="_started"/> is set. A failed attempt clears that
        /// flag, logs <see cref="CacheListenerLogMessages.StartFailed"/>, releases sockets, sets
        /// <see cref="CacheListenerState.Stopped"/>, and rethrows. This method does not consult
        /// <see cref="_disposed"/>.
        /// </remarks>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            lock (_gate)
            {
                _state = CacheListenerState.Starting;
            }

            try
            {
                if (!_readiness.IsReady)
                {
                    throw new InvalidOperationException(
                        "Cache Listener cannot start because the ACME certificate is not ready. BackFiller is TLS-only and the listener must wait for a usable certificate.");
                }

                if (!_certificates.IsAvailable)
                {
                    throw new InvalidOperationException(
                        $"Cache Listener cannot start because no TLS certificate is available from ACME state '{_runtime.CertificateDirectory}'.");
                }

                var bindings = ListenEndpointPlanner.Plan(_runtime.BindAddressTokens, _runtime.BindPortTls);
                if (bindings.Count == 0)
                {
                    throw new InvalidOperationException("Cache Listener has no bind endpoints.");
                }

                CacheListenerLogMessages.Starting(_logger, _runtime.BindPortTls, bindings.Count);
                BindEndpoints(bindings);
                if (_listeners.Count == 0)
                {
                    throw new InvalidOperationException("Cache Listener failed to bind any endpoint.");
                }

                if (_publication is not null)
                {
                    await _publication.PublishAfterListenerBoundAsync(cancellationToken).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    _state = CacheListenerState.Running;
                }

                CacheListenerLogMessages.Running(
                    _logger,
                    _listeners.Count,
                    _runtime.BindPortTls,
                    _runtime.Listener.SendBufferBytes,
                    _runtime.Listener.ReceiveBufferBytes);
                _journal.Record(BackFillerStartupStages.ListenerStarted);
                _acceptTask = Task.WhenAll(
                    _listeners.Select(static listener => listener.AcceptLoopTask ?? Task.CompletedTask));
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                CacheListenerLogMessages.StartFailed(_logger, ex.Message);
                await DisposeBoundResourcesAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    _state = CacheListenerState.Stopped;
                }

                Interlocked.Exchange(ref _started, 0);
                throw;
            }
        }

        /// <summary>Stops the listener by disposing it.</summary>
        /// <param name="cancellationToken">Not observed. Drain is not bounded by this token.</param>
        /// <returns>A task that completes when <see cref="DisposeAsync"/> completes.</returns>
        /// <remarks>A second stop is a no-op because disposal is idempotent.</remarks>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Cancels accept and connection work, closes listen sockets, and waits for admitted connection tasks.
        /// </summary>
        /// <returns>A task that completes when drain and socket release finish. A second call completes immediately.</returns>
        /// <remarks>
        /// State moves to <see cref="CacheListenerState.Retiring"/> before cancel, then to
        /// <see cref="CacheListenerState.Stopped"/> after drain. <see cref="OperationCanceledException"/> from the
        /// accept task is ignored. Expected accept-task faults
        /// (<see cref="InvalidOperationException"/> after consecutive accept failures,
        /// <see cref="ObjectDisposedException"/>, <see cref="SocketException"/>, and
        /// <see cref="AggregateException"/> containing only those) are logged and ignored here so drain and the
        /// stopped state still run; <see cref="Execution"/> already exposed those faults to
        /// <see cref="VectorNNTPD.BackFillerService.Core.ApplicationServiceManager"/>.
        /// Unexpected accept-task <see cref="AggregateException"/> faults are retained, cleanup still reaches
        /// <see cref="CacheListenerState.Stopped"/>, then the aggregate is rethrown so programming errors stay visible.
        /// Connection-task exceptions are logged and ignored by <see cref="DrainConnectionsAsync"/>.
        /// The run token source is disposed before the stopped state.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            lock (_gate)
            {
                _state = CacheListenerState.Retiring;
            }

            CacheListenerLogMessages.Retiring(_logger);
            await _runCts.CancelAsync().ConfigureAwait(false);
            await CloseListenSocketsAsync().ConfigureAwait(false);

            Exception? unexpectedAcceptFault = null;
            var accept = _acceptTask;
            if (accept is not null)
            {
                unexpectedAcceptFault = await ObserveAcceptTaskDuringDisposeAsync(accept).ConfigureAwait(false);
            }

            await DrainConnectionsAsync().ConfigureAwait(false);
            await DisposeBoundResourcesAsync().ConfigureAwait(false);
            _runCts.Dispose();
            lock (_gate)
            {
                _state = CacheListenerState.Stopped;
            }

            CacheListenerLogMessages.Stopped(_logger);

            if (unexpectedAcceptFault is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unexpectedAcceptFault).Throw();
            }
        }

        /// <summary>
        /// Observes <paramref name="accept"/> during dispose without a catch-all. Expected faults are logged;
        /// unexpected aggregates are returned for rethrow after cleanup reaches <see cref="CacheListenerState.Stopped"/>.
        /// </summary>
        /// <param name="accept">The accept-loop task published as <see cref="Execution"/>.</param>
        /// <returns>An unexpected fault to rethrow after cleanup, or <see langword="null"/>.</returns>
        private async Task<Exception?> ObserveAcceptTaskDuringDisposeAsync(Task accept)
        {
            if (accept.IsCompleted)
            {
                return ClassifyCompletedAcceptTask(accept);
            }

            try
            {
                await accept.ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException)
            {
                ShutdownObservations.Canceled(_logger, "cache listener accept loop");
                return null;
            }
            catch (ObjectDisposedException ex)
            {
                ObserveExpectedAcceptLoopFaultDuringDispose(ex);
                return null;
            }
            catch (SocketException ex)
            {
                ObserveExpectedAcceptLoopFaultDuringDispose(ex);
                return null;
            }
            catch (InvalidOperationException ex)
            {
                ObserveExpectedAcceptLoopFaultDuringDispose(ex);
                return null;
            }
            catch (AggregateException ex)
            {
                if (IsExpectedAcceptLoopAggregate(ex))
                {
                    ObserveExpectedAcceptLoopFaultDuringDispose(ex);
                    return null;
                }

                return ex;
            }
        }

        /// <summary>
        /// Classifies an already-completed accept task using <see cref="Task.Exception"/> (always an aggregate when faulted).
        /// </summary>
        /// <param name="accept">Completed accept-loop task.</param>
        /// <returns>Unexpected aggregate to rethrow after cleanup, or <see langword="null"/>.</returns>
        private Exception? ClassifyCompletedAcceptTask(Task accept)
        {
            if (accept.IsCanceled)
            {
                ShutdownObservations.Canceled(_logger, "cache listener accept loop");
                return null;
            }

            if (!accept.IsFaulted)
            {
                return null;
            }

            var fault = accept.Exception!;
            if (IsExpectedAcceptLoopAggregate(fault))
            {
                ObserveExpectedAcceptLoopFaultDuringDispose(fault);
                return null;
            }

            return fault;
        }

        /// <summary>
        /// Logs an expected accept-task fault observed while disposing. Terminal accept failures were already
        /// logged by the accept loop; this records that dispose continued cleanup.
        /// </summary>
        /// <param name="exception">Expected fault from <see cref="_acceptTask"/>.</param>
        private void ObserveExpectedAcceptLoopFaultDuringDispose(Exception exception) =>
            CacheListenerLogMessages.AcceptLoopFaultDuringDispose(
                _logger,
                exception.GetBaseException().GetType().Name);

        /// <summary>
        /// Reports whether every flattened inner exception is an expected accept-loop fault type.
        /// </summary>
        /// <param name="exception">Fault from <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/> over accept loops.</param>
        /// <returns>
        /// <see langword="true"/> when every inner is <see cref="InvalidOperationException"/>,
        /// <see cref="ObjectDisposedException"/>, <see cref="SocketException"/>, or
        /// <see cref="OperationCanceledException"/>.
        /// </returns>
        private static bool IsExpectedAcceptLoopAggregate(AggregateException exception)
        {
            var inners = exception.Flatten().InnerExceptions;
            if (inners.Count == 0)
            {
                return false;
            }

            foreach (var inner in inners)
            {
                if (inner is not (
                    InvalidOperationException
                    or ObjectDisposedException
                    or SocketException
                    or OperationCanceledException))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Binds each planned endpoint, skipping unsupported implicit-wildcard families.</summary>
        /// <param name="bindings">Plans from <see cref="ListenEndpointPlanner.Plan"/>.</param>
        /// <remarks>
        /// <see cref="SocketException"/> is swallowed only when <see cref="IsImplicitWildcard"/> is true and the
        /// error is <see cref="SocketError.AddressFamilyNotSupported"/>, <see cref="SocketError.ProtocolNotSupported"/>,
        /// or <see cref="SocketError.AddressNotAvailable"/>. Every other bind exception propagates to
        /// <see cref="StartAsync"/>.
        /// </remarks>
        private void BindEndpoints(IReadOnlyList<ListenBinding> bindings)
        {
            foreach (var binding in bindings)
            {
                SocketAcceptListener? listener = null;
                try
                {
                    listener = new SocketAcceptListener(
                        binding,
                        OnAcceptedAsync,
                        _logger,
                        maxConsecutiveAcceptFailures: MaxConsecutiveAcceptFailures,
                        acceptFailureRetryDelay: AcceptFailureRetryDelay,
                        acceptFailureDelayAsync: AcceptFailureDelayAsync,
                        acceptFailureObserver: new CacheListenerAcceptFailureObserver(_logger),
                        acceptFailureLogInterval: AcceptFailureLogInterval,
                        reuseAddress: false)
                    {
                        AcceptAsyncOverride = AcceptAsyncOverride,
                    };
                    listener.Start();
                    lock (_gate)
                    {
                        _listeners.Add(listener);
                    }

                    listener = null;
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        var endpoint = binding.EndPoint.ToString();
                        var addressFamily = binding.Address.AddressFamily.ToString();
                        CacheListenerLogMessages.EndpointBound(_logger, endpoint, addressFamily);
                    }
                }
                catch (SocketException ex) when (
                    IsImplicitWildcard(binding.EndPoint)
                    && ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported or SocketError.AddressNotAvailable)
                {
                    CacheListenerLogMessages.WildcardFamilySkipped(_logger, binding.Address.AddressFamily.ToString(), ex.SocketErrorCode.ToString());
                    if (listener is not null)
                    {
                        _ = listener.DisposeAsync().AsTask();
                    }
                }
                catch
                {
                    if (listener is not null)
                    {
                        _ = listener.DisposeAsync().AsTask();
                    }

                    throw;
                }
            }
        }

        /// <summary>Reports whether a failed wildcard bind may be skipped.</summary>
        /// <param name="endpoint">Endpoint that failed to bind.</param>
        /// <returns>
        /// <see langword="true"/> when no bind-address tokens were configured, or when any configured token is a
        /// wildcard and <paramref name="endpoint"/> is IPv4 any or IPv6 any.
        /// </returns>
        private bool IsImplicitWildcard(IPEndPoint endpoint)
        {
            if (_runtime.BindAddressTokens.Count == 0)
            {
                return true;
            }

            return _runtime.BindAddressTokens.Any(BackFillerOptions.IsBindAddressWildcard)
                   && (endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any));
        }

        /// <summary>
        /// Admits one accepted socket, applies TCP options, and starts <see cref="ProcessAsync"/>.
        /// </summary>
        private ValueTask OnAcceptedAsync(Socket accepted, CancellationToken cancellationToken)
        {
            var remote = accepted.RemoteEndPoint?.ToString() ?? "<unknown>";
            if (!TryAdmit())
            {
                CacheListenerLogMessages.CapacityRejected(_logger, remote, _runtime.Listener.MaxActiveConnections);
                accepted.Dispose();
                return ValueTask.CompletedTask;
            }

            if (!TryConfigureAcceptedSocket(accepted, remote))
            {
                ReleaseAdmit();
                return ValueTask.CompletedTask;
            }

            CacheListenerLogMessages.Accepted(_logger, remote, ActiveConnections);
            var connection = ProcessAsync(accepted, remote, cancellationToken);
            _connections[connection] = 0;
            _ = connection.ContinueWith(
                static (task, state) => ((CacheListenerService)state!)._connections.TryRemove(task, out _),
                this,
                TaskScheduler.Default);
            return ValueTask.CompletedTask;
        }

        /// <summary>Handshakes one accepted socket and runs a single NNTP session.</summary>
        /// <param name="accepted">
        /// Accepted socket. Ownership transfers to <see cref="NntpConnection"/> on a successful TLS start;
        /// otherwise this method disposes it.
        /// </param>
        /// <param name="remote">Remote endpoint text captured before processing, used only in logs.</param>
        /// <param name="cancellationToken">Service run token passed to the handshake link and the NNTP session.</param>
        /// <returns>A task that completes when the connection has been released from the admission count.</returns>
        /// <remarks>
        /// The handshake uses a linked token cancelled after <see cref="BackFillerListenerRuntimeOptions.TlsHandshakeTimeout"/>.
        /// That timeout becomes <see cref="TimeoutException"/> with message <c>tls-handshake</c>. After the handshake,
        /// <see cref="StreamCacheListenerTransport"/> applies <see cref="BackFillerListenerRuntimeOptions.IoProgressTimeout"/>
        /// as one no-progress deadline per read or non-empty write over the shared <see cref="NntpConnection"/> pipeline.
        /// <see cref="OperationCanceledException"/> from the service token is ignored.
        /// <see cref="AuthenticationException"/> and <see cref="TimeoutException"/> are logged.
        /// <see cref="IOException"/> is logged as a closed connection. Other exceptions are logged as
        /// <see cref="CacheListenerLogMessages.ConnectionFailed"/>.
        /// <see cref="ReleaseAdmit"/> always runs.
        /// </remarks>
        private async Task ProcessAsync(Socket accepted, string remote, CancellationToken cancellationToken)
        {
            var handshakeCompleted = false;
            var socketOwnedByConnection = false;
            try
            {
                if (accepted.RemoteEndPoint is not IPEndPoint tcpPeer)
                {
                    return;
                }

                var identity = ConnectionClientIdentity.Direct(tcpPeer);
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeCts.CancelAfter(_runtime.Listener.TlsHandshakeTimeout);
                NntpConnection connection;
                try
                {
                    connection = await NntpConnection.StartTlsAsync(
                            accepted,
                            _certificates,
                            identity,
                            _logger,
                            handshakeCts.Token)
                        .ConfigureAwait(false);
                    socketOwnedByConnection = true;
                }
                catch (OperationCanceledException) when (handshakeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("tls-handshake");
                }

                handshakeCompleted = true;
                // The NNTP session starts only after TLS succeeds. Shared pipeline transport owns the socket and certificate lease.
                await using (connection)
                {
                    await using var stream = new NntpConnectionStream(connection, ownsConnection: false);
                    await using var transport = new StreamCacheListenerTransport(
                        stream,
                        _runtime.Listener.IoProgressTimeout,
                        leaveInnerStreamOpen: true);
                    var session = new NntpCacheListenerSession(transport, _retention, _time);
                    await session.RunAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Service shutdown canceled this connection path.
            }
            catch (AuthenticationException)
            {
                CacheListenerLogMessages.HandshakeFailed(_logger, remote);
            }
            catch (TimeoutException ex)
            {
                CacheListenerLogMessages.ConnectionTimedOut(
                    _logger,
                    handshakeCompleted ? "io-progress" : ex.Message,
                    remote);
            }
            catch (IOException ex)
            {
                CacheListenerLogMessages.ConnectionIoClosed(_logger, remote, ex.GetType().Name);
            }
            // codeql[cs/catch-of-all-exceptions] One connection failure must be logged and must not leave the accept loop with an unobserved task fault.
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                CacheListenerLogMessages.ConnectionFailed(_logger, remote, ex.GetType().Name);
            }
            finally
            {
                if (!socketOwnedByConnection)
                {
                    try
                    {
                        accepted.Dispose();
                    }
                    catch (ObjectDisposedException ex)
                    {
                        // Accept path may race with a test hook or failed option setup that already disposed.
                        _ = ex; // observed; intentionally ignored at this boundary
                    }
                }

                ReleaseAdmit();
            }
        }

        /// <summary>
        /// Disables Nagle and sets the configured buffer limits on <paramref name="accepted"/> before any TLS or NNTP byte.
        /// </summary>
        /// <param name="accepted">Socket returned by <see cref="Socket.AcceptAsync(CancellationToken)"/>.</param>
        /// <param name="remote">Remote endpoint text for the failure log.</param>
        /// <returns><see langword="false"/> when the socket was disposed because option setup failed.</returns>
        /// <remarks>
        /// <see cref="BeforeAcceptedSocketOptions"/> runs first when a test supplied it.
        /// <see cref="SocketException"/>, <see cref="ObjectDisposedException"/>, and
        /// <see cref="ArgumentOutOfRangeException"/> dispose the socket and return <see langword="false"/>.
        /// The caller releases the admission slot. Other exceptions propagate.
        /// </remarks>
        private bool TryConfigureAcceptedSocket(Socket accepted, string remote)
        {
            try
            {
                BeforeAcceptedSocketOptions?.Invoke(accepted);
                TcpSocketOptions.Apply(
                    accepted,
                    _runtime.Listener.SendBufferBytes,
                    _runtime.Listener.ReceiveBufferBytes,
                    _runtime.TcpKeepAlive);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or ArgumentOutOfRangeException)
            {
                var error = ex is SocketException socket ? socket.SocketErrorCode.ToString() : ex.GetType().Name;
                CacheListenerLogMessages.AcceptedSocketOptionsFailed(_logger, remote, error);
                try
                {
                    accepted.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // The test hook or the failed option call already disposed it.
                }

                return false;
            }

            AfterAcceptedSocketConfigured?.Invoke(accepted);
            return true;
        }

        /// <summary>Reserves one admission slot without exceeding the configured maximum.</summary>
        /// <returns><see langword="true"/> when this call incremented <see cref="_activeConnections"/>.</returns>
        private bool TryAdmit()
        {
            while (true)
            {
                var current = Volatile.Read(ref _activeConnections);
                if (current >= _runtime.Listener.MaxActiveConnections)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _activeConnections, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// Releases one admission slot. Paired with a successful <see cref="TryAdmit"/>,
        /// either from <see cref="ProcessAsync"/> or from accepted-socket option failure.
        /// </summary>
        private void ReleaseAdmit() => Interlocked.Decrement(ref _activeConnections);

        /// <summary>Disposes every accept listener and clears the list. Dispose exceptions are ignored.</summary>
        private async Task CloseListenSocketsAsync()
        {
            SocketAcceptListener[] listeners;
            lock (_gate)
            {
                listeners = [.. _listeners];
                _listeners.Clear();
            }

            foreach (var listener in listeners)
            {
                try
                {
                    await listener.DisposeAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException ex)
                {
                    // Concurrent stop/dispose can already have released the listen socket.
                    _ = ex; // observed; intentionally ignored at this boundary
                }
                catch (SocketException ex)
                {
                    CacheListenerLogMessages.ListenSocketDisposeFailed(_logger, ex.SocketErrorCode.ToString());
                }
            }
        }

        /// <summary>Waits for connection tasks currently in <see cref="_connections"/>. Every fault from those tasks is logged and ignored.</summary>
        /// <returns>A task that completes when the sampled tasks finish, or immediately when none are tracked.</returns>
        private async Task DrainConnectionsAsync()
        {
            var tasks = _connections.Keys.ToArray();
            if (tasks.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] A connection-task fault during drain must not skip the remaining listener cleanup.
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                try
                {
                    CacheListenerLogMessages.ConnectionDrainFailed(_logger, ex.GetBaseException().GetType().Name);
                }
                // codeql[cs/catch-of-all-exceptions] A connection-task fault during drain must not skip the remaining listener cleanup.
                catch (Exception logFailure) when (!ExceptionHelpers.IsFatal(logFailure))
                {
                    try
                    {
                        Serilog.Debugging.SelfLog.WriteLine(
                            "Cache listener connection drain failure log failed: {0}",
                            logFailure);
                    }
                    // codeql[cs/catch-of-all-exceptions] A connection-task fault during drain must not skip the remaining listener cleanup.
                    catch (Exception selfLogFailure) when (!ExceptionHelpers.IsFatal(selfLogFailure))
                    {
                        // Drain logging is best-effort. Remaining dispose work still runs.
                    }
                }
            }
        }

        /// <summary>Closes accept listeners. Certificate leases stay with the connections that acquired them.</summary>
        /// <returns>A task that completes after listeners are disposed.</returns>
        /// <remarks>
        /// Certificate publication is owned by the Common TLS provider; outstanding leases
        /// keep retired generations alive until each connection completes.
        /// </remarks>
        private Task DisposeBoundResourcesAsync() => CloseListenSocketsAsync();
    }
}
