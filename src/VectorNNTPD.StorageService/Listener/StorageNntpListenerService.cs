using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.Common.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.Common.Networking.Listeners;

namespace VectorNNTPD.StorageService.Listener
{
    /// <summary>
    /// Process-wide NNTP TLS listener: binds configured endpoints, authenticates TLS, and serves
    /// Storage sessions (<c>ARTICLE</c>/<c>TAKETHIS</c>/<c>QUIT</c>). Does not own article storage.
    /// </summary>
    public sealed class StorageNntpListenerService : IApplicationService, IAsyncDisposable
    {
        private readonly StorageServerRuntimeOptionsSource? _runtimeSource;
        private StorageServerRuntimeOptions? _runtime;
        private readonly ITlsCertificateContextProvider _certificates;
        private readonly IAcmeCertificateReadiness _readiness;
        private readonly ILogger<StorageNntpListenerService> _logger;
        private readonly IAcceptedArticleQueue? _acceptedArticles;
        private readonly StoreAssemblyAdmission? _assemblyAdmission;
        private readonly Func<bool>? _allowsArticleAdmission;
        private readonly Func<ArticleId, bool>? _articlePresent;
        private readonly Func<ArticleId, string?>? _resolveRetrievableArticle;
        private readonly Func<ArticleId, CancellationToken, ValueTask>? _afterArticleDelivered;
        private readonly Func<ArticleId, IDisposable>? _acquireArticleReadLease;

        /// <summary>
        /// Publishes DNS after at least one socket is bound. Null skips publication.
        /// A failure disposes the bound sockets and fails startup before the listener accepts connections.
        /// </summary>
        private readonly Func<CancellationToken, Task>? _publishAfterBind;
        private readonly object _gate = new();
        private readonly List<SocketAcceptListener> _listeners = [];
        private readonly ConcurrentDictionary<Task, byte> _connections = new();
        private readonly CancellationTokenSource _runCts = new();

        private Task? _acceptTask;
        private StorageNntpListenerState _state = StorageNntpListenerState.Created;
        private int _started;
        private int _disposed;
        private int _activeConnections;

        /// <summary>Send buffer of the most recently accepted socket, in bytes. Zero before the first accept.</summary>
        internal static int LastAcceptedSendBuffer { get; private set; }

        /// <summary>Receive buffer of the most recently accepted socket, in bytes. Zero before the first accept.</summary>
        internal static int LastAcceptedReceiveBuffer { get; private set; }

        /// <summary>Initializes the Listener service for isolated tests with a pre-ready certificate gate.</summary>
        public StorageNntpListenerService(
            StorageServerRuntimeOptions runtime,
            ITlsCertificateContextProvider certificates,
            ILogger<StorageNntpListenerService> logger,
            IAcceptedArticleQueue? acceptedArticles = null,
            StoreAssemblyAdmission? assemblyAdmission = null,
            Func<bool>? allowsArticleAdmission = null,
            Func<ArticleId, bool>? articlePresent = null,
            Func<ArticleId, string?>? resolveRetrievableArticle = null,
            Func<ArticleId, CancellationToken, ValueTask>? afterArticleDelivered = null,
            Func<ArticleId, IDisposable>? acquireArticleReadLease = null)
            : this(
                runtime,
                certificates,
                CreateReadyGate(),
                logger,
                acceptedArticles,
                assemblyAdmission,
                allowsArticleAdmission,
                articlePresent,
                resolveRetrievableArticle: resolveRetrievableArticle,
                afterArticleDelivered: afterArticleDelivered,
                acquireArticleReadLease: acquireArticleReadLease)
        {
        }

        /// <summary>Initializes the Listener service.</summary>
        public StorageNntpListenerService(
            StorageServerRuntimeOptions runtime,
            ITlsCertificateContextProvider certificates,
            IAcmeCertificateReadiness readiness,
            ILogger<StorageNntpListenerService> logger,
            IAcceptedArticleQueue? acceptedArticles = null,
            StoreAssemblyAdmission? assemblyAdmission = null,
            Func<bool>? allowsArticleAdmission = null,
            Func<ArticleId, bool>? articlePresent = null,
            Func<CancellationToken, Task>? publishAfterBind = null,
            Func<ArticleId, string?>? resolveRetrievableArticle = null,
            Func<ArticleId, CancellationToken, ValueTask>? afterArticleDelivered = null,
            Func<ArticleId, IDisposable>? acquireArticleReadLease = null)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(certificates);
            ArgumentNullException.ThrowIfNull(readiness);
            ArgumentNullException.ThrowIfNull(logger);
            _runtime = runtime;
            _certificates = certificates;
            _readiness = readiness;
            _logger = logger;
            _acceptedArticles = acceptedArticles;
            _assemblyAdmission = assemblyAdmission;
            _allowsArticleAdmission = allowsArticleAdmission;
            _articlePresent = articlePresent;
            _resolveRetrievableArticle = resolveRetrievableArticle;
            _afterArticleDelivered = afterArticleDelivered;
            _acquireArticleReadLease = acquireArticleReadLease;
            _publishAfterBind = publishAfterBind;
        }

        /// <summary>
        /// Initializes the listener for the host. Runtime options are read when <see cref="StartAsync"/> runs,
        /// after <c>nntpsharedconfig</c> has been published.
        /// </summary>
        public StorageNntpListenerService(
            StorageServerRuntimeOptionsSource runtimeSource,
            ITlsCertificateContextProvider certificates,
            IAcmeCertificateReadiness readiness,
            ILogger<StorageNntpListenerService> logger,
            IAcceptedArticleQueue? acceptedArticles,
            StoreAssemblyAdmission? assemblyAdmission,
            Func<bool>? allowsArticleAdmission = null,
            Func<ArticleId, bool>? articlePresent = null,
            Func<CancellationToken, Task>? publishAfterBind = null,
            Func<ArticleId, string?>? resolveRetrievableArticle = null,
            Func<ArticleId, CancellationToken, ValueTask>? afterArticleDelivered = null,
            Func<ArticleId, IDisposable>? acquireArticleReadLease = null)
        {
            ArgumentNullException.ThrowIfNull(runtimeSource);
            ArgumentNullException.ThrowIfNull(certificates);
            ArgumentNullException.ThrowIfNull(readiness);
            ArgumentNullException.ThrowIfNull(logger);
            _runtimeSource = runtimeSource;
            _certificates = certificates;
            _readiness = readiness;
            _logger = logger;
            _acceptedArticles = acceptedArticles;
            _assemblyAdmission = assemblyAdmission;
            _allowsArticleAdmission = allowsArticleAdmission;
            _articlePresent = articlePresent;
            _resolveRetrievableArticle = resolveRetrievableArticle;
            _afterArticleDelivered = afterArticleDelivered;
            _acquireArticleReadLease = acquireArticleReadLease;
            _publishAfterBind = publishAfterBind;
        }

        private StorageServerRuntimeOptions Runtime =>
            _runtime ?? throw new InvalidOperationException("StorageServer runtime options have not been published.");

        private static IAcmeCertificateReadiness CreateReadyGate()
        {
            var readiness = new AcmeCertificateReadiness();
            readiness.MarkReady();
            return readiness;
        }

        /// <inheritdoc />
        public string Name => "StorageNntpListener";

        /// <inheritdoc />
        public Task? Execution => _acceptTask;

        /// <summary>Gets the local lifecycle state.</summary>
        public StorageNntpListenerState State
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
        public int ActiveConnections => Volatile.Read(ref _activeConnections);

        /// <summary>Gets bound listen endpoints (tests).</summary>
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

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            lock (_gate)
            {
                _state = StorageNntpListenerState.Starting;
            }

            try
            {
                _runtime ??= _runtimeSource?.Current
                    ?? throw new InvalidOperationException("StorageServer runtime options have not been published.");

                if (!_readiness.IsReady)
                {
                    throw new InvalidOperationException(
                        "Storage NNTP Listener cannot start because the ACME certificate is not ready. StorageServer is TLS-only and the listener must wait for a usable certificate.");
                }

                if (!_certificates.IsAvailable)
                {
                    throw new InvalidOperationException(
                        $"Storage NNTP Listener cannot start because no TLS certificate is available from ACME state '{Runtime.CertificateDirectory}'.");
                }

                var bindings = ListenEndpointPlanner.Plan(Runtime.BindAddressTokens, Runtime.BindPortTls);
                if (bindings.Count == 0)
                {
                    throw new InvalidOperationException("Storage NNTP Listener has no bind endpoints.");
                }

                StorageNntpListenerLogMessages.Starting(_logger, Runtime.BindPortTls, bindings.Count);
                BindEndpoints(bindings);
                if (_listeners.Count == 0)
                {
                    throw new InvalidOperationException("Storage NNTP Listener failed to bind any endpoint.");
                }

                if (_publishAfterBind is not null)
                {
                    await _publishAfterBind(cancellationToken).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    _state = StorageNntpListenerState.Running;
                }

                StorageNntpListenerLogMessages.Running(_logger, _listeners.Count, Runtime.BindPortTls);
                _acceptTask = Task.WhenAll(
                    _listeners.Select(static listener => listener.AcceptLoopTask ?? Task.CompletedTask));
            }
            catch (Exception ex)
            {
                StorageNntpListenerLogMessages.StartFailed(_logger, ex.Message);
                await DisposeBoundResourcesAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    _state = StorageNntpListenerState.Stopped;
                }

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
                _state = StorageNntpListenerState.Retiring;
            }

            StorageNntpListenerLogMessages.Retiring(_logger);
            await _runCts.CancelAsync().ConfigureAwait(false);
            await CloseListenSocketsAsync().ConfigureAwait(false);

            var accept = _acceptTask;
            if (accept is not null)
            {
                try
                {
                    await accept.ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    ExpectedException.Ignore(ex);
                }
            }

            // Finish in-flight TAKETHIS (including TryCommit) before completing the queue so a
            // reservation cannot race Complete. ApplicationServiceManager then stops the worker,
            // which drains every accepted article before the article index stops.
            await DrainConnectionsAsync().ConfigureAwait(false);
            _acceptedArticles?.Complete();
            await DisposeBoundResourcesAsync().ConfigureAwait(false);
            _runCts.Dispose();
            lock (_gate)
            {
                _state = StorageNntpListenerState.Stopped;
            }

            StorageNntpListenerLogMessages.Stopped(_logger);
        }

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
                        reuseAddress: false);
                    listener.Start();
                    lock (_gate)
                    {
                        _listeners.Add(listener);
                    }

                    listener = null;
                    StorageNntpListenerLogMessages.EndpointBound(
                        _logger,
                        binding.EndPoint.ToString() ?? string.Empty,
                        binding.Address.AddressFamily.ToString());
                }
                catch (SocketException ex) when (
                    IsImplicitWildcard(binding.EndPoint)
                    && ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported or SocketError.AddressNotAvailable)
                {
                    StorageNntpListenerLogMessages.WildcardFamilySkipped(
                        _logger,
                        binding.Address.AddressFamily.ToString(),
                        ex.SocketErrorCode.ToString());
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

        private bool IsImplicitWildcard(IPEndPoint endpoint)
        {
            if (Runtime.BindAddressTokens.Count == 0)
            {
                return true;
            }

            return Runtime.BindAddressTokens.Any(StorageServerOptions.IsBindAddressWildcard)
                   && (endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any));
        }

        private ValueTask OnAcceptedAsync(Socket accepted, CancellationToken cancellationToken)
        {
            var remote = accepted.RemoteEndPoint?.ToString() ?? "<unknown>";
            if (!TryAdmit())
            {
                StorageNntpListenerLogMessages.CapacityRejected(_logger, remote, Runtime.Listener.MaxActiveConnections);
                accepted.Dispose();
                return ValueTask.CompletedTask;
            }

            StorageNntpListenerLogMessages.Accepted(_logger, remote, ActiveConnections);
            var connection = ProcessAsync(accepted, remote, cancellationToken);
            _connections[connection] = 0;
            _ = connection.ContinueWith(
                static (task, state) => ((StorageNntpListenerService)state!)._connections.TryRemove(task, out _),
                this,
                TaskScheduler.Default);
            return ValueTask.CompletedTask;
        }

        private async Task ProcessAsync(Socket accepted, string remote, CancellationToken cancellationToken)
        {
            var handshakeCompleted = false;
            var socketOwnedByConnection = false;
            try
            {
                LastAcceptedSendBuffer = accepted.SendBufferSize;
                LastAcceptedReceiveBuffer = accepted.ReceiveBufferSize;
                if (accepted.RemoteEndPoint is not IPEndPoint tcpPeer)
                {
                    return;
                }

                var identity = ConnectionClientIdentity.Direct(tcpPeer);
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeCts.CancelAfter(Runtime.Listener.TlsHandshakeTimeout);
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
                await using (connection)
                {
                    await using var stream = new NntpConnectionStream(connection, ownsConnection: false);
                    await using var transport = new StreamStorageListenerTransport(
                        stream,
                        Runtime.Listener.IoProgressTimeout,
                        leaveInnerStreamOpen: true);
                    await using var session = new StorageNntpFeedSession(
                        transport,
                        Runtime.Listener,
                        _logger,
                        Runtime.Fqdn,
                        acceptedArticles: _acceptedArticles,
                        assemblyAdmission: _assemblyAdmission,
                        allowsArticleAdmission: _allowsArticleAdmission,
                        articlePresent: _articlePresent,
                        resolveRetrievableArticle: _resolveRetrievableArticle,
                        afterArticleDelivered: _afterArticleDelivered,
                        acquireArticleReadLease: _acquireArticleReadLease);
                    await session.RunAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                ExpectedException.Ignore(ex);
            }
            catch (AuthenticationException)
            {
                StorageNntpListenerLogMessages.HandshakeFailed(_logger, remote);
            }
            catch (TimeoutException ex)
            {
                StorageNntpListenerLogMessages.ConnectionTimedOut(
                    _logger,
                    handshakeCompleted ? "io-progress" : ex.Message,
                    remote);
            }
            catch (IOException)
            {
                // Routine peer reset / transport tear-down after accept; not an operational failure.
                StorageNntpListenerLogMessages.ConnectionPeerDisconnected(_logger, remote);
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                StorageNntpListenerLogMessages.ConnectionFailed(_logger, remote, ex.GetType().Name);
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
                        // Handshake failure or concurrent teardown may already have disposed the socket.
                        ExpectedException.Ignore(ex);
                    }
                }

                ReleaseAdmit();
            }
        }

        private bool TryAdmit()
        {
            while (true)
            {
                var current = Volatile.Read(ref _activeConnections);
                if (current >= Runtime.Listener.MaxActiveConnections)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _activeConnections, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        private void ReleaseAdmit() => Interlocked.Decrement(ref _activeConnections);

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
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
                {
                    ExpectedException.Ignore(ex);
                }
            }
        }

        private async Task DrainConnectionsAsync()
        {
            var tasks = _connections.Keys.ToArray();
            if (tasks.Length == 0)
            {
                return;
            }

            try
            {
                // Wait for every tracked connection so one fault cannot skip cleanup of the others.
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                ExpectedException.Ignore(ex);
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                // ProcessAsync logs expected connection faults and completes successfully; a faulted
                // tracked task here is unexpected and must remain observable without aborting drain.
                LogUnexpectedDrainFailures(tasks, ex);
            }
        }

        private void LogUnexpectedDrainFailures(Task[] tasks, Exception awaitException)
        {
            var logged = false;
            foreach (var task in tasks)
            {
                if (task.Exception is null)
                {
                    continue;
                }

                foreach (var inner in task.Exception.Flatten().InnerExceptions)
                {
                    if (inner is OperationCanceledException oce)
                    {
                        ExpectedException.Ignore(oce);
                        continue;
                    }

                    logged = true;
                    StorageNntpListenerLogMessages.ConnectionDrainFailed(_logger, inner);
                }
            }

            if (!logged)
            {
                StorageNntpListenerLogMessages.ConnectionDrainFailed(_logger, awaitException);
            }
        }

        private Task DisposeBoundResourcesAsync() => CloseListenSocketsAsync();
    }

    /// <summary>Local lifecycle states for the StorageServer NNTP listener.</summary>
    public enum StorageNntpListenerState
    {
        /// <summary>Created but not started.</summary>
        Created = 0,

        /// <summary>Start in progress.</summary>
        Starting = 1,

        /// <summary>Accepting connections.</summary>
        Running = 2,

        /// <summary>Shutdown in progress.</summary>
        Retiring = 3,

        /// <summary>Fully stopped.</summary>
        Stopped = 4,
    }
}
