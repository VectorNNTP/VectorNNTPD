using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.Common.Networking.Listeners;

namespace VectorNNTPD.StorageService.Listener
{
    /// <summary>
    /// Process-wide NNTP TLS listener: binds configured endpoints, authenticates TLS, and serves
    /// RFC 4644 STREAMING feed sessions (<c>CHECK</c>/<c>TAKETHIS</c>). Does not own article storage.
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
        private readonly Func<ArticleId, bool>? _articlePresent;

        /// <summary>
        /// Publishes DNS after at least one socket is bound. Null skips publication.
        /// A failure disposes the bound sockets and fails startup before the listener accepts connections.
        /// </summary>
        private readonly Func<CancellationToken, Task>? _publishAfterBind;
        private readonly object _gate = new();
        private readonly List<Socket> _listenSockets = [];
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
            Func<ArticleId, bool>? articlePresent = null)
            : this(runtime, certificates, CreateReadyGate(), logger, acceptedArticles, assemblyAdmission, articlePresent)
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
            Func<ArticleId, bool>? articlePresent = null,
            Func<CancellationToken, Task>? publishAfterBind = null)
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
            _articlePresent = articlePresent;
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
            Func<ArticleId, bool>? articlePresent = null,
            Func<CancellationToken, Task>? publishAfterBind = null)
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
            _articlePresent = articlePresent;
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
                    return [.. _listenSockets.Select(static socket => socket.LocalEndPoint)];
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
                if (_listenSockets.Count == 0)
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

                StorageNntpListenerLogMessages.Running(_logger, _listenSockets.Count, Runtime.BindPortTls);
                _acceptTask = AcceptAllAsync(_runCts.Token);
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
            CloseListenSockets();

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
                try
                {
                    var socket = CreateBoundListenSocket(binding);
                    lock (_gate)
                    {
                        _listenSockets.Add(socket);
                    }

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
                }
            }
        }

        private static Socket CreateBoundListenSocket(ListenBinding binding)
        {
            var endpoint = binding.EndPoint;
            using var ownership = new DisposableOwnership<Socket>(
                new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                });
            var socket = ownership.Resource;

            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = binding.DualMode;
            }

            socket.Bind(endpoint);
            socket.Listen(512);
            return ownership.Transfer();
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

        private async Task AcceptAllAsync(CancellationToken cancellationToken)
        {
            Socket[] sockets;
            lock (_gate)
            {
                sockets = [.. _listenSockets];
            }

            await Task.WhenAll(sockets.Select(socket => AcceptLoopAsync(socket, cancellationToken))).ConfigureAwait(false);
        }

        private async Task AcceptLoopAsync(Socket listenSocket, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket accepted;
                try
                {
                    accepted = await listenSocket.AcceptAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                var remote = accepted.RemoteEndPoint?.ToString() ?? "<unknown>";
                if (!TryAdmit())
                {
                    StorageNntpListenerLogMessages.CapacityRejected(_logger, remote, Runtime.Listener.MaxActiveConnections);
                    accepted.Dispose();
                    continue;
                }

                StorageNntpListenerLogMessages.Accepted(_logger, remote, ActiveConnections);
                var connection = ProcessAsync(accepted, remote, cancellationToken);
                _connections[connection] = 0;
                _ = connection.ContinueWith(
                    static (task, state) => ((StorageNntpListenerService)state!)._connections.TryRemove(task, out _),
                    this,
                    TaskScheduler.Default);
            }
        }

        private async Task ProcessAsync(Socket accepted, string remote, CancellationToken cancellationToken)
        {
            var handshakeCompleted = false;
            try
            {
                await using var network = new NetworkStream(accepted, ownsSocket: true);
                LastAcceptedSendBuffer = accepted.SendBufferSize;
                LastAcceptedReceiveBuffer = accepted.ReceiveBufferSize;
                using var lease = _certificates.Acquire();
                await using var ssl = new SslStream(network, leaveInnerStreamOpen: true);
                var options = new SslServerAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ServerCertificateContext = lease.Context,
                };

                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeCts.CancelAfter(Runtime.Listener.TlsHandshakeTimeout);
                try
                {
                    await ssl.AuthenticateAsServerAsync(options, handshakeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (handshakeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("tls-handshake");
                }

                handshakeCompleted = true;
                await using var transport = new StreamStorageListenerTransport(
                    ssl,
                    Runtime.Listener.IoProgressTimeout,
                    leaveInnerStreamOpen: true);
                await using var session = new StorageNntpFeedSession(
                    transport,
                    Runtime.Listener,
                    _logger,
                    Runtime.Fqdn,
                    acceptedArticles: _acceptedArticles,
                    assemblyAdmission: _assemblyAdmission,
                    articlePresent: _articlePresent);
                await session.RunAsync(cancellationToken).ConfigureAwait(false);
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

        private void CloseListenSockets()
        {
            lock (_gate)
            {
                foreach (var socket in _listenSockets)
                {
                    try
                    {
                        socket.Dispose();
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or System.Net.Sockets.SocketException)
                    {
                        // Concurrent retire may dispose the same listen socket twice.
                        ExpectedException.Ignore(ex);
                    }
                }

                _listenSockets.Clear();
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

        private Task DisposeBoundResourcesAsync()
        {
            CloseListenSockets();
            return Task.CompletedTask;
        }
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
