using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.PostFilter;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.SessionState.BytesAccounting;
using VectorNNTPD.TransitService.SessionState.RateLimiting;
using VectorNNTPD.TransitService.Authentication;
using VectorNNTPD.TransitService.SessionState;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Authentication;
using VectorNNTPD.TransitService.Session.Commands.Posting;
using VectorNNTPD.TransitService.Session.SpeedTest;
using VectorNNTPD.TransitService.Transit;
using VectorNNTPD.TransitService.Diagnostics;
using VectorNNTPD.TransitService.Hosting;
using VectorNNTPD.TransitService.Moderation;
using VectorNNTPD.TransitService.Newsgroups;
using VectorNNTPD.TransitService.RabbitMq.ArticleWork;
using VectorNNTPD.TransitService.Storage;
using VectorNNTPD.TransitService.Transport.Vatp;
using VectorNNTPD.Common.Networking.Listeners;
using VectorNNTPD.Common.Cloudflare;

namespace VectorNNTPD.TransitService.Networking.Listeners
{
    /// <summary>
    /// Application service that binds cleartext NNTP TCP listeners and owns accepted plain connections.
    /// </summary>
    /// <remarks>
    /// Starts after Cloudflare DNS reconciliation and before ACME. Does not wait for certificates.
    /// <see cref="StartAsync"/> succeeds only after every planned endpoint has bound; a bind
    /// failure disposes already-bound endpoints and fails startup. Accept-loop failures after a
    /// successful bind do not fail startup.
    /// </remarks>
    public sealed class NntpPlainListenerService : IApplicationService, IAsyncDisposable
    {
        private readonly IOptions<NntpdOptions> _options;
        private readonly ITrustedProxyHosts _trustedProxyHosts;
        private readonly ITlsCertificateContextProvider _certificateProvider;
        private readonly INntpAuthenticationProvider _authenticationProvider;
        private readonly IArticleIngestionQueue _articleIngestion;
        private readonly IHistoryDb? _historyDb;
        private readonly ISpeedTestCoordinator? _speedTest;
        private readonly ITransitPeerAuthorization _transitPeerAuthorization;
        private readonly ITransitInboundConnectionLimiter _inboundConnectionLimiter;
        private readonly IFeedDiagnostics _feedDiagnostics;
        private readonly INntpSessionCensus? _sessionCensus;
        private readonly ITransitPeerMetrics? _peerMetrics;
        private readonly IPostingTraceProtector? _postingTraceProtector;
        private readonly INewsgroupCatalogue? _newsgroupCatalogue;
        private readonly INntpArticlePolicySource? _sharedConfiguration;
        private readonly IModeratorCatalogue? _moderatorCatalogue;
        private readonly IModeratorAuthorization _moderatorAuthorization;
        private readonly IModerationSubmissionService _moderationSubmission;
        private readonly ISessionStateTracker? _sessionAdmission;
        private readonly NntpSaslService? _saslService;
        private readonly IAccountByteAccountant _accountBytes;
        private readonly IAccountRateAllocator _accountRates;
        private readonly IArticleWorkRpcClient? _articleWorkRpc;
        private readonly IVatpArticleClient? _vatpArticleClient;
        private readonly IStorageArticleLookupClient? _storageArticleLookup;
        private readonly IPostFilter _postFilter;
        private readonly PostFilterMetrics _postFilterMetrics;
        private readonly IPostFilterRejectionEvidenceQueue _postFilterEvidence;
        private readonly INewsLogWriter _newsLog;
        private readonly IListenSocketBinder _listenBinder;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<NntpPlainListenerService> _logger;
        private readonly NntpServerRole _serverRole;

        /// <summary>
        /// Publishes DNS after every plain socket is bound, and only when the TLS listener is disabled.
        /// When TLS is enabled, <see cref="NntpTlsListenerService"/> publishes after its own bind.
        /// Null skips publication. A failure rolls back the sockets bound by this start.
        /// </summary>
        private readonly IListenerDnsPublication? _publication;
        private readonly ConcurrentDictionary<NntpConnection, byte> _connections = new();
        private readonly List<SocketAcceptListener> _listeners = [];
        private readonly CancellationTokenSource _runCts = new();
        private Task? _execution;
        private int _started;
        private int _disposed;

        /// <summary>Initializes a new instance of the <see cref="NntpPlainListenerService"/> class.</summary>
        public NntpPlainListenerService(
            IOptions<NntpdOptions> options,
            ITrustedProxyHosts trustedProxyHosts,
            ITlsCertificateContextProvider certificateProvider,
            INntpAuthenticationProvider authenticationProvider,
            IArticleIngestionQueue articleIngestion,
            ITransitPeerAuthorization transitPeerAuthorization,
            ILoggerFactory loggerFactory,
            ILogger<NntpPlainListenerService> logger,
            ITransitInboundConnectionLimiter? inboundConnectionLimiter = null,
            IHistoryDb? historyDb = null,
            ISpeedTestCoordinator? speedTest = null,
            IFeedDiagnostics? feedDiagnostics = null,
            IListenSocketBinder? listenBinder = null,
            INntpSessionCensus? sessionCensus = null,
            ITransitPeerMetrics? peerMetrics = null,
            IPostingTraceProtector? postingTraceProtector = null,
            INewsgroupCatalogue? newsgroupCatalogue = null,
            IModeratorCatalogue? moderatorCatalogue = null,
            IModeratorAuthorization? moderatorAuthorization = null,
            IModerationSubmissionService? moderationSubmission = null,
            ISessionStateTracker? sessionAdmission = null,
            NntpSaslService? saslService = null,
            IAccountByteAccountant? accountBytes = null,
            IAccountRateAllocator? accountRates = null,
            IArticleWorkRpcClient? articleWorkRpc = null,
            IVatpArticleClient? vatpArticleClient = null,
            IStorageArticleLookupClient? storageArticleLookup = null,
            IPostFilter? postFilter = null,
            PostFilterMetrics? postFilterMetrics = null,
            IPostFilterRejectionEvidenceQueue? postFilterEvidence = null,
            INewsLogWriter? newsLog = null,
            INntpArticlePolicySource? sharedConfiguration = null,
            IListenerDnsPublication? publication = null,
            NntpServerRole serverRole = NntpServerRole.Transit)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(trustedProxyHosts);
            ArgumentNullException.ThrowIfNull(certificateProvider);
            ArgumentNullException.ThrowIfNull(authenticationProvider);
            ArgumentNullException.ThrowIfNull(articleIngestion);
            ArgumentNullException.ThrowIfNull(transitPeerAuthorization);
            ArgumentNullException.ThrowIfNull(loggerFactory);
            ArgumentNullException.ThrowIfNull(logger);
            _options = options;
            _trustedProxyHosts = trustedProxyHosts;
            _certificateProvider = certificateProvider;
            _authenticationProvider = authenticationProvider;
            _articleIngestion = articleIngestion;
            _transitPeerAuthorization = transitPeerAuthorization;
            _inboundConnectionLimiter = inboundConnectionLimiter ?? TransitInboundConnectionLimiter.Disabled;
            _historyDb = historyDb;
            _speedTest = speedTest;
            _feedDiagnostics = feedDiagnostics ?? NullFeedDiagnostics.Instance;
            _sessionCensus = sessionCensus;
            _peerMetrics = peerMetrics;
            _postingTraceProtector = postingTraceProtector;
            _newsgroupCatalogue = newsgroupCatalogue;
            _sharedConfiguration = sharedConfiguration;
            _moderatorCatalogue = moderatorCatalogue;
            _moderatorAuthorization = moderatorAuthorization ?? EmptyModeratorAuthorization.Instance;
            _moderationSubmission = moderationSubmission ?? UnavailableModerationSubmissionService.Instance;
            _sessionAdmission = sessionAdmission;
            _saslService = saslService;
            _accountBytes = accountBytes ?? NullAccountByteAccountant.Instance;
            _accountRates = accountRates ?? NullAccountRateAllocator.Instance;
            _articleWorkRpc = articleWorkRpc;
            _vatpArticleClient = vatpArticleClient;
            _storageArticleLookup = storageArticleLookup;
            _postFilter = postFilter ?? DisabledPostFilter.Instance;
            _postFilterMetrics = postFilterMetrics ?? new PostFilterMetrics();
            _postFilterEvidence = postFilterEvidence ?? DisabledPostFilterRejectionEvidenceQueue.Instance;
            _newsLog = newsLog ?? NullNewsLogWriter.Instance;
            _listenBinder = listenBinder ?? SocketListenBinder.Instance;
            _loggerFactory = loggerFactory;
            _logger = logger;
            _publication = publication;
            _serverRole = serverRole;
        }

        /// <inheritdoc />
        public string Name => "NntpPlainListener";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <summary>Gets the number of active plain connections (tests).</summary>
        internal int ActiveConnectionCount => _connections.Count;

        /// <summary>Gets bound local endpoints after start (tests).</summary>
        internal IReadOnlyList<IPEndPoint> LocalEndPoints =>
            [.. _listeners.Select(static l => l.LocalEndPoint)];

        /// <summary>Gets whether any accept loop is still running (tests).</summary>
        internal bool HasActiveAcceptLoops => _listeners.Exists(static l => l.AcceptLoopActive);

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var options = _options.Value;
                var bindings = ListenEndpointPlanner.Plan(options.BindAddress, options.BindPort);
                if (bindings.Count == 0)
                {
                    throw new InvalidOperationException("No listen bindings were produced for the plain NNTP port.");
                }

                foreach (var binding in bindings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var listener = new SocketAcceptListener(
                        binding,
                        OnAcceptedAsync,
                        _loggerFactory.CreateLogger($"{nameof(SocketAcceptListener)}.Plain"),
                        _listenBinder);
                    try
                    {
                        listener.Start();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        await listener.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ex)
                    {
                        NetworkingLogMessages.ListenerBindFailed(
                            _logger,
                            ex,
                            "Plain",
                            binding.EndPoint.ToString(),
                            options.BindPort);
                        await listener.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }

                    _listeners.Add(listener);
                }

                if (_publication is not null && !_options.Value.IsTlsListenerEnabled)
                {
                    await _publication.PublishAfterListenerBoundAsync(cancellationToken).ConfigureAwait(false);
                }

                _execution = WaitUntilStoppedAsync(_runCts.Token);
                NetworkingLogMessages.PlainListenersStarted(_logger, _listeners.Count, options.BindPort);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                await RollbackPartialStartupAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);

            foreach (var listener in _listeners)
            {
                await listener.StopAsync().ConfigureAwait(false);
            }

            var connections = _connections.Keys.ToArray();
            foreach (var connection in connections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (connection is NntpConnection nntpConnection)
                    {
                        nntpConnection.NoteDisconnectReason(TcpDisconnectReason.Shutdown);
                    }

                    await connection.CompleteAsync().ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    NetworkingLogMessages.PlainConnectionCompleteError(_logger, ex);
                }
            }

            foreach (var connection in connections)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                {
                    _ = ignored; // observed; intentionally ignored at this best-effort boundary
                    // Best-effort.
                }

                _connections.TryRemove(connection, out _);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
            {
                _ = ignored; // observed; intentionally ignored at this best-effort boundary
                // Best-effort dispose.
            }

            foreach (var listener in _listeners)
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }

            _listeners.Clear();
            _runCts.Dispose();
        }

        private async Task RollbackPartialStartupAsync()
        {
            foreach (var listener in _listeners)
            {
                try
                {
                    await listener.DisposeAsync().ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                {
                    _ = ignored; // observed; intentionally ignored at this best-effort boundary
                    // Best-effort rollback of already-bound endpoints.
                }
            }

            _listeners.Clear();
            Interlocked.Exchange(ref _started, 0);
        }

        // codeql[cs/missed-using-statement] Accepted socket ownership transfers to NntpConnection on success; disposed in the method finally only when no connection was started. A using on the parameter would dispose under the live session.
        private async ValueTask OnAcceptedAsync(Socket socket, CancellationToken cancellationToken)
        {
            NntpConnection? connection = null;
            try
            {
                if (!ProxyPreambleResolver.TryGetTcpPeer(socket, out var tcpPeer))
                {
                    NetworkingLogMessages.PlainAcceptDiscarded(_logger);
                    return;
                }

                ProxyPreambleResolution preamble;
                try
                {
                    preamble = await ProxyPreambleResolver
                        .ResolveAsync(socket, tcpPeer, _trustedProxyHosts, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ProxyProtocolException ex)
                {
                    NetworkingLogMessages.PlainProxyPreambleRejected(_logger, ex, tcpPeer);
                    return;
                }

                connection = NntpConnection.StartPlain(
                    socket,
                    preamble.Identity,
                    _loggerFactory.CreateLogger<NntpConnection>(),
                    preamble.Leftover,
                    _feedDiagnostics);
                _connections[connection] = 0;

                // Session owns NNTP greeting/command loop; transport owns the socket lifecycle.
                var session = new NntpSession(
                    connection,
                    _loggerFactory.CreateLogger<NntpSession>(),
                    certificateProvider: _certificateProvider,
                    authenticationProvider: _authenticationProvider,
                    allowCleartextAuth: _options.Value.AllowCleartextAuth,
                    loggerFactory: _loggerFactory,
                    articleIngestion: _articleIngestion,
                    transitPeerAuthorization: _transitPeerAuthorization,
                    streamOutstandingArticleDepth: _options.Value.Transit.StreamOutstandingArticleDepth,
                    historyDb: _historyDb,
                    speedTest: _speedTest,
                    sessionCensus: _sessionCensus,
                    peerMetrics: _peerMetrics,
                    commandIdleTimeout: TimeSpan.FromSeconds(_options.Value.IdleTime),
                    maxArticleSize: _options.Value.MaxArticleSize,
                    injectionIdentity: _options.Value.Fqdn,
                    mailComplaintsTo: _options.Value.MailComplaintsTo,
                    postingTraceProtector: _postingTraceProtector,
                    newsgroupCatalogue: _newsgroupCatalogue,
                    moderatorCatalogue: _moderatorCatalogue,
                    moderatorAuthorization: _moderatorAuthorization,
                    moderationSubmission: _moderationSubmission,
                    sessionAdmission: _sessionAdmission,
                    saslService: _saslService,
                    accountBytes: _accountBytes,
                    accountRates: _accountRates,
                    articleWorkRpc: _articleWorkRpc,
                    vatpArticleClient: _vatpArticleClient,
                    storageArticleLookup: _storageArticleLookup,
                    postFilter: _postFilter,
                    postFilterMetrics: _postFilterMetrics,
                    postFilterEvidence: _postFilterEvidence,
                    newsLog: _newsLog,
                    transit: _options.Value.Transit,
                    serverRole: _serverRole)
                {
                    SharedConfiguration = _sharedConfiguration,
                };
                ConnectionAcceptanceLogging.LogPlainAccepted(_logger, connection.ClientIdentity);

                TransitInboundAdmitResult admission;
                try
                {
                    admission = await TransitConnectionAdmission
                        .TryAdmitAsync(_inboundConnectionLimiter, session, _runCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_runCts.IsCancellationRequested)
                {
                    return;
                }

                if (!admission.Admitted)
                {
                    session.RecordPeerRejected();
                    _feedDiagnostics.OnRejected(
                        session.Authorization.TransitPeerName ?? "none",
                        ConnectionAcceptanceLogging.FormatEndpoint(session.ClientIdentity.Client));
                    await TransitConnectionAdmission
                        .WriteUnavailableAsync(connection, _runCts.Token)
                        .ConfigureAwait(false);
                    return;
                }

                session.AttachTransitAdmission(admission.Lease);
                session.RecordPeerAccepted();
                session.FeedProbe = _feedDiagnostics.OnAccepted(session);
                try
                {
                    await session.RunAsync(_runCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (_runCts.IsCancellationRequested)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                    // Listener stopping.
                }
                finally
                {
                    _feedDiagnostics.OnReleased(session.FeedProbe);
                    await admission.Lease.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (connection is not null)
                {
                    _connections.TryRemove(connection, out _);
                    try
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                    {
                        _ = ignored; // observed; intentionally ignored at this best-effort boundary
                        // Best-effort.
                    }
                }
                else
                {
                    socket.Dispose();
                }
            }
        }

        private static async Task WaitUntilStoppedAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
                // Expected.
            }
        }
    }
}
