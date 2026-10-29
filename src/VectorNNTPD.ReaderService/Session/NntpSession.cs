using System.IO.Pipelines;
using System.Net;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.History;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.Common.Networking.RateLimiting;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Session.Framing;
using VectorNNTPD.ReaderService.Session.SpeedTest;
using VectorNNTPD.ReaderService.Diagnostics;
using VectorNNTPD.ReaderService.Hosting;
using VectorNNTPD.ReaderService.Moderation;
using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.Transit;
using VectorNNTPD.ReaderService.RabbitMq.ArticleWork;
using VectorNNTPD.ReaderService.Storage;
using VectorNNTPD.ReaderService.Transport.Vatp;
using VectorNNTPD.ReaderService.SessionState.RateLimiting;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Session
{
    using VectorNNTPD.Common.Core;
    /// <summary>
    /// NNTP session boundary: greeting, command loop, mode, authentication, and authorization state over an
    /// <see cref="INntpConnection"/>.
    /// </summary>
    /// <remarks>
    /// Transport owns sockets, TLS, DEFLATE, PROXY identity, and connection lifecycle.
    /// This type owns NNTP protocol sequencing and does not touch sockets or stream wrappers directly.
    /// Authentication and authorization are distinct; AUTHINFO success applies only provider-returned privileges.
    /// Connection-time <see cref="ITransitPeerAuthorization"/> may grant transit/streaming peer privileges
    /// without authentication and retains the named Transit peer policy.
    /// An established session that executes no NNTP command for <c>Nntpd:IdleTime</c> is disconnected
    /// via the existing close path (reason <c>IdleTimeout</c>). In-flight commands including
    /// pipelined CHECK and TAKETHIS keep the session non-idle.
    /// Any command-loop exit (QUIT, EOF, reset, timeout, cancel, exception, shutdown)
    /// takes the same one-shot finalization path and releases distributed admission at most once.
    /// </remarks>
    public sealed class NntpSession
    {
        private readonly ILogger<NntpSession> _logger;
        private readonly NntpCommandDispatcher _dispatcher;
        private readonly NntpAuthorization _connectionAuthorization;
        private NntpResponseWriter? _response;
        private NntpAuthorization _authorization;
        private NntpAuthenticationState _authentication;
        private string? _pendingAuthUsername;
        private NntpAccountPolicy? _accountPolicy;
        private string? _admittedAccountName;
        private TransitInboundConnectionLease? _transitAdmission;
        private NntpSaslExchange? _saslExchange;
        private NntpSessionMode _mode;
        private NntpAuthenticationAuthority _authenticationAuthority;
        private int _closeRequested;
        private int _lifetime;
        private int _activityState;
        private int _commandWork;
        private long _lastCommandTimestamp;
        /// <summary>
        /// Low 63 bits: activity epoch, incremented when <see cref="BeginCommandWork"/> commits.
        /// High bit set: idle waiter has committed <see cref="TcpDisconnectReason.IdleTimeout"/>.
        /// </summary>
        private long _idleState;
        private readonly TimeSpan _commandIdleTimeout;
        private readonly TimeProvider _timeProvider;
        private readonly TaskCompletionSource _idleWatchArmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // codeql[cs/missed-using-statement] Owned by NntpSession until Dispose; a using at this declaration would cancel session lifetime when the constructor returns.
        private readonly CancellationTokenSource _closeCts = new();
        private Task? _idleWatchTask;
        private int _closeReason;
        private ReadOnlyMemory<byte> _selectedGroupName;

        /// <summary>Initializes a new instance of the <see cref="NntpSession"/> class.</summary>
        public NntpSession(
            INntpConnection connection,
            ILogger<NntpSession> logger,
            ITlsCertificateContextProvider? certificateProvider = null,
            INntpAuthenticationProvider? authenticationProvider = null,
            bool allowCleartextAuth = true,
            ILoggerFactory? loggerFactory = null,
            IArticleIngestionQueue? articleIngestion = null,
            ITransitPeerAuthorization? transitPeerAuthorization = null,
            int streamOutstandingArticleDepth = NntpStreamArticleTxScheduler.DefaultDepth,
            IHistoryDb? historyDb = null,
            ISpeedTestCoordinator? speedTest = null,
            INntpSessionCensus? sessionCensus = null,
            ITransitPeerMetrics? peerMetrics = null,
            TimeSpan? commandIdleTimeout = null,
            TimeProvider? timeProvider = null,
            int? maxArticleSize = null,
            string? injectionIdentity = null,
            Commands.Posting.INewsgroupPostingPolicy? newsgroupPostingPolicy = null,
            string? mailComplaintsTo = null,
            Commands.Posting.IPostingTraceProtector? postingTraceProtector = null,
            INewsgroupCatalogue? newsgroupCatalogue = null,
            IModeratorCatalogue? moderatorCatalogue = null,
            IModeratorAuthorization? moderatorAuthorization = null,
            IModerationSubmissionService? moderationSubmission = null,
            ISessionStateTracker? sessionAdmission = null,
            NntpSaslService? saslService = null,
            ITransitPeerAuthenticator? transitAuthenticator = null,
            IAccountByteAccountant? accountBytes = null,
            IAccountRateAllocator? accountRates = null,
            IArticleWorkRpcClient? articleWorkRpc = null,
            IVatpArticleClient? vatpArticleClient = null,
            IStorageArticleLookupClient? storageArticleLookup = null,
            IPostFilter? postFilter = null,
            PostFilterMetrics? postFilterMetrics = null,
            IPostFilterRejectionEvidenceQueue? postFilterEvidence = null,
            INewsLogWriter? newsLog = null,
            TransitOptions? transit = null,
            NntpServerRole serverRole = NntpServerRole.Reader)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(logger);
            ServerRole = serverRole;
            Connection = connection;
            ClientIdentity = connection.ClientIdentity;
            _logger = logger;
            CertificateProvider = certificateProvider;
            _connectionAuthorization = (transitPeerAuthorization ?? TransitPeerAuthorization.Disabled)
                .Resolve(ClientIdentity.ClientAddress);
            _authorization = _connectionAuthorization;
            _authentication = NntpAuthenticationState.Unauthenticated;
            _mode = NntpSessionMode.Unspecified;
            _authenticationAuthority = NntpAuthenticationAuthority.Reader;
            AllowCleartextAuth = allowCleartextAuth;
            AuthenticationProvider = authenticationProvider ?? DenyAllNntpAuthenticationProvider.Instance;
            TransitAuthenticator = transitAuthenticator ?? TransitPeerAuthenticator.Instance;
            ArticleIngestion = articleIngestion ?? DisabledArticleIngestionQueue.Instance;
            HistoryDb = historyDb;
            SpeedTest = speedTest;
            SessionCensus = sessionCensus;
            PeerMetrics = peerMetrics;
            StreamArticleTx = new NntpStreamArticleTxScheduler(streamOutstandingArticleDepth);
            _dispatcher = new NntpCommandDispatcher(loggerFactory);
            _timeProvider = timeProvider ?? TimeProvider.System;
            _commandIdleTimeout = commandIdleTimeout ?? TimeSpan.FromSeconds(NntpdOptions.DefaultIdleTime);
            if (_commandIdleTimeout < TimeSpan.FromSeconds(NntpdOptions.MinIdleTime)
                || _commandIdleTimeout > TimeSpan.FromSeconds(NntpdOptions.MaxIdleTime))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commandIdleTimeout),
                    commandIdleTimeout,
                    $"Idle timeout must be between {NntpdOptions.MinIdleTime} and {NntpdOptions.MaxIdleTime} seconds.");
            }

            MaxArticleSize = maxArticleSize ?? NntpdOptions.DefaultMaxArticleSize;
            if (MaxArticleSize < NntpdOptions.MinMaxArticleSize
                || MaxArticleSize > NntpdOptions.MaxMaxArticleSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxArticleSize),
                    maxArticleSize,
                    $"Max article size must be between {NntpdOptions.MinMaxArticleSize} and {NntpdOptions.MaxMaxArticleSize} bytes.");
            }

            InjectionIdentity = string.IsNullOrWhiteSpace(injectionIdentity)
                ? NntpdOptions.FormatFqdn(1, "usenet.ninja")
                : injectionIdentity.Trim();
            ArticleParser = new NntpArticleParser(InjectionIdentity);
            NewsgroupPostingPolicy = newsgroupPostingPolicy
                ?? (newsgroupCatalogue is not null
                    ? new Commands.Posting.CatalogueNewsgroupPostingPolicy(newsgroupCatalogue)
                    : Commands.Posting.SyntaxOnlyNewsgroupPostingPolicy.Instance);
            MailComplaintsTo = string.IsNullOrWhiteSpace(mailComplaintsTo)
                ? NntpdOptions.DefaultMailComplaintsTo
                : mailComplaintsTo.Trim();
            PostingTraceProtector = postingTraceProtector;
            NewsgroupCatalogue = newsgroupCatalogue;
            ModeratorCatalogue = moderatorCatalogue;
            ModeratorAuthorization = moderatorAuthorization
                ?? (IModeratorAuthorization?)moderatorCatalogue
                ?? EmptyModeratorAuthorization.Instance;
            ModerationSubmission = moderationSubmission ?? UnavailableModerationSubmissionService.Instance;
            SessionAdmission = sessionAdmission;
            SaslService = saslService;
            AccountBytes = accountBytes ?? NullAccountByteAccountant.Instance;
            AccountRates = accountRates ?? NullAccountRateAllocator.Instance;
            ArticleWorkRpc = articleWorkRpc;
            VatpArticleClient = vatpArticleClient;
            StorageArticleLookup = storageArticleLookup;
            PostFilter = postFilter ?? DisabledPostFilter.Instance;
            PostFilterMetrics = postFilterMetrics ?? new PostFilterMetrics();
            PostFilterEvidence = postFilterEvidence ?? DisabledPostFilterRejectionEvidenceQueue.Instance;
            NewsLog = newsLog ?? NullNewsLogWriter.Instance;
            Transit = transit ?? new TransitOptions();
            SessionId = Guid.NewGuid().ToString("N");
        }

        /// <summary>Gets the underlying transport connection.</summary>
        public INntpConnection Connection { get; }

        /// <summary>Gets the immutable client identity established at connection start.</summary>
        public ConnectionClientIdentity ClientIdentity { get; }

        /// <summary>
        /// Gets the article ingestion queue used by transfer commands (<c>TAKETHIS</c>, <c>IHAVE</c>, <c>POST</c>).
        /// </summary>
        public IArticleIngestionQueue ArticleIngestion { get; }

        /// <summary>
        /// Gets the article-work RPC client used by ARTICLE/HEAD/BODY/STAT message-id lookup, or
        /// <see langword="null"/> when tests construct a session without RabbitMQ.
        /// </summary>
        internal IArticleWorkRpcClient? ArticleWorkRpc { get; }

        /// <summary>
        /// Gets the VATP article client used after ArticleWork Success, or
        /// <see langword="null"/> when tests construct a session without VATP.
        /// </summary>
        internal IVatpArticleClient? VatpArticleClient { get; }

        /// <summary>
        /// Gets the StorageServer fleet lookup used after a definitive ArticleWork miss, or
        /// <see langword="null"/> when tests construct a session without fleet lookup.
        /// </summary>
        internal IStorageArticleLookupClient? StorageArticleLookup { get; }

        /// <summary>Gets the POST-only PostFilter. IHAVE/TAKETHIS must not call this.</summary>
        internal IPostFilter PostFilter { get; }

        /// <summary>Gets process counters for PostFilter COMMIT outcomes.</summary>
        internal PostFilterMetrics PostFilterMetrics { get; }

        /// <summary>Gets the bounded PostFilter rejection-evidence queue.</summary>
        internal IPostFilterRejectionEvidenceQueue PostFilterEvidence { get; }

        /// <summary>Gets the dedicated INN <c>news</c> writer.</summary>
        internal INewsLogWriter NewsLog { get; }

        /// <summary>Gets site-wide transit options including WantTrash/LogTrash.</summary>
        internal TransitOptions Transit { get; }

        /// <summary>Gets the destuffed POST article size limit (<c>Nntpd:MaxArticleSize</c>).</summary>
        public int MaxArticleSize { get; }

        /// <summary>
        /// Published <c>nntpsharedconfig</c>. Null when the session was constructed without the catalogue.
        /// Article commands capture one policy for the whole operation.
        /// </summary>
        internal INntpArticlePolicySource? SharedConfiguration { get; set; }

        /// <summary>
        /// Gets the server injection identity used for POST metadata
        /// (<c>nntpd{ServerId:00}.{DnsSuffix}</c>).
        /// </summary>
        public string InjectionIdentity { get; }

        /// <summary>
        /// Gets the session-scoped Common article parser (local identity is
        /// <see cref="InjectionIdentity"/>). Used by TAKETHIS and POST before queue admission.
        /// </summary>
        internal NntpArticleParser ArticleParser { get; }

        /// <summary>
        /// Gets the newsgroup existence/posting-authorization boundary used by POST.
        /// Catalogue-backed when <see cref="NewsgroupCatalogue"/> is set and no policy was injected.
        /// </summary>
        public Commands.Posting.INewsgroupPostingPolicy NewsgroupPostingPolicy { get; }

        /// <summary>Gets the configured POST <c>mail-complaints-to</c> mailbox.</summary>
        public string MailComplaintsTo { get; }

        /// <summary>Gets the POST <c>X-Trace</c> protector, or <see langword="null"/> when unset (tests).</summary>
        public Commands.Posting.IPostingTraceProtector? PostingTraceProtector { get; }

        /// <summary>
        /// Gets the in-memory newsgroup catalogue, or <see langword="null"/> when unset (tests).
        /// </summary>
        public INewsgroupCatalogue? NewsgroupCatalogue { get; }

        /// <summary>
        /// Gets the process-wide moderator catalogue, or <see langword="null"/> when unset (tests).
        /// </summary>
        public IModeratorCatalogue? ModeratorCatalogue { get; }

        /// <summary>
        /// Gets the moderator authorization table used by POST for <c>Approved:</c> decisions.
        /// </summary>
        public IModeratorAuthorization ModeratorAuthorization { get; }

        /// <summary>Captures the current moderator snapshot once for a POST command.</summary>
        public IModeratorAuthorization CaptureModeratorAuthorization() =>
            ModeratorCatalogue?.Current ?? ModeratorAuthorization;

        /// <summary>
        /// Gets the moderation submission boundary used when an unapproved moderated
        /// proto-article must be forwarded.
        /// </summary>
        public IModerationSubmissionService ModerationSubmission { get; }

        /// <summary>Gets whether a newsgroup is currently selected.</summary>
        internal bool HasSelectedGroup => !_selectedGroupName.IsEmpty;

        /// <summary>Gets the original stored name of the currently selected newsgroup.</summary>
        internal ReadOnlySpan<byte> SelectedGroupName => _selectedGroupName.Span;

        /// <summary>Gets the clock used for idle accounting and POST injection timestamps.</summary>
        public TimeProvider Time => _timeProvider;

        /// <summary>Gets the HistoryDB used by CHECK, IHAVE, TAKETHIS, and POST, or <see langword="null"/> when unset (tests).</summary>
        public IHistoryDb? HistoryDb { get; }

        /// <summary>
        /// Gets the SPEEDTEST coordinator, or <see langword="null"/> when the diagnostic is not registered.
        /// </summary>
        public ISpeedTestCoordinator? SpeedTest { get; }

        /// <summary>
        /// Gets the opt-in feed-diagnostics probe for this session, or <see langword="null"/> when disabled.
        /// </summary>
        public FeedSessionProbe? FeedProbe { get; set; }

        /// <summary>Gets the process-wide session census, or <see langword="null"/> when unset (tests).</summary>
        internal INntpSessionCensus? SessionCensus { get; }

        /// <summary>Gets always-on Transit peer counters, or <see langword="null"/> when unset (tests).</summary>
        internal ITransitPeerMetrics? PeerMetrics { get; }

        /// <summary>Gets the existing article-ingest activity state for this session.</summary>
        public FeedSessionState ActivityState => (FeedSessionState)Volatile.Read(ref _activityState);

        /// <summary>Gets the per-session CHECK pipeline once <see cref="RunAsync"/> has started.</summary>
        internal CheckPipeline? Pipeline { get; private set; }

        /// <summary>Gets the per-session TAKETHIS pipeline once <see cref="RunAsync"/> has started.</summary>
        internal TakeThisPipeline? TakeThisWindow { get; private set; }

        /// <summary>
        /// Gets the bounded STREAM article TX scheduler (depth gate above
        /// <see cref="NntpResponseWriter.WriteArticleAsync(System.ReadOnlyMemory{byte}, NntpArticleTxFraming, System.Threading.CancellationToken)"/>).
        /// </summary>
        /// <remarks>
        /// Available for outbound STREAM article producers that already hold destuffed bytes.
        /// No production command currently supplies those bytes (storage/catalogue is out of scope).
        /// </remarks>
        public NntpStreamArticleTxScheduler StreamArticleTx { get; }

        /// <summary>Gets the effective client IP address for this session.</summary>
        public IPAddress ClientAddress => ClientIdentity.ClientAddress;

        /// <summary>Gets the effective client TCP source port for this session.</summary>
        public int ClientPort => ClientIdentity.ClientPort;

        /// <summary>Gets the actual TCP peer endpoint of the accepted socket.</summary>
        public IPEndPoint TcpPeer => ClientIdentity.TcpPeer;

        /// <summary>Gets the reader AUTHINFO provider (newsmaster / MySQL). Never used in Transit authority.</summary>
        public INntpAuthenticationProvider AuthenticationProvider { get; }

        /// <summary>Gets the Transit AUTHINFO authenticator. Never used in Reader authority.</summary>
        public ITransitPeerAuthenticator TransitAuthenticator { get; }

        /// <summary>
        /// Gets the AUTHINFO credential authority selected by MODE, not by source IP.
        /// </summary>
        public NntpAuthenticationAuthority AuthenticationAuthority => _authenticationAuthority;

        /// <summary>Gets the authenticated-session admission tracker, if registered.</summary>
        public ISessionStateTracker? SessionAdmission { get; }

        /// <summary>Gets the AUTHINFO SASL service, if registered.</summary>
        public NntpSaslService? SaslService { get; }

        /// <summary>Gets the account byte-quota accountant. No-op when unauthenticated.</summary>
        public IAccountByteAccountant AccountBytes { get; }

        /// <summary>Gets the account rate allocator. No-op when the rate is unlimited.</summary>
        public IAccountRateAllocator AccountRates { get; }

        /// <summary>Gets the unique id used for admission tracking.</summary>
        public string SessionId { get; }

        /// <summary>Gets the authenticated account policy, or <see langword="null"/> when none applies.</summary>
        public NntpAccountPolicy? AccountPolicy => _accountPolicy;

        /// <summary>Gets whether an AUTHINFO SASL exchange is waiting for a continuation line.</summary>
        internal bool HasSaslExchange => _saslExchange is not null;

        /// <summary>Gets the in-progress SASL exchange, or <see langword="null"/>.</summary>
        internal NntpSaslExchange? CurrentSaslExchange => _saslExchange;

        /// <summary>Gets the TLS certificate provider used by STARTTLS, if configured.</summary>
        public ITlsCertificateContextProvider? CertificateProvider { get; }

        /// <summary>
        /// Gets a value indicating whether AUTHINFO USER/PASS is permitted without TLS
        /// (<c>Nntpd:AllowCleartextAuth</c>). TLS connections always permit AUTHINFO USER/PASS.
        /// </summary>
        public bool AllowCleartextAuth { get; }

        /// <summary>
        /// Gets a value indicating whether AUTHINFO USER/PASS may proceed on this connection
        /// given TLS state and <see cref="AllowCleartextAuth"/>.
        /// </summary>
        public bool IsAuthinfoPassPermitted => Connection.IsTls || AllowCleartextAuth;

        /// <summary>Gets the current authentication identity snapshot.</summary>
        public NntpAuthenticationState Authentication => _authentication;

        /// <summary>Gets the current authorization snapshot.</summary>
        public NntpAuthorization Authorization => _authorization;

        /// <summary>Gets the current NNTP operating mode.</summary>
        public NntpSessionMode Mode => _mode;

        /// <summary>Gets the host process role that created this session.</summary>
        public NntpServerRole ServerRole { get; }

        /// <summary>
        /// Gets the RX strategy implied by <see cref="Mode"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="NntpReceiveStrategy.ReaderCommand"/> only after <c>MODE READER</c>.
        /// <c>MODE STREAM</c> does not change this (RFC 4644 §2.3).
        /// </remarks>
        internal NntpReceiveStrategy ReceiveStrategy =>
            _mode == NntpSessionMode.Reader
                ? NntpReceiveStrategy.ReaderCommand
                : NntpReceiveStrategy.StreamDataPlane;

        /// <summary>
        /// Gets the pending username from <c>AUTHINFO USER</c> awaiting <c>AUTHINFO PASS</c>,
        /// or <see langword="null"/> when no USER is cached.
        /// </summary>
        public string? PendingAuthUsername => _pendingAuthUsername;

        /// <summary>Selects <paramref name="groupName"/> as the current newsgroup (immortal snapshot bytes).</summary>
        internal void SelectGroup(ReadOnlyMemory<byte> groupName) => _selectedGroupName = groupName;

        /// <summary>Replaces the authorization snapshot (tests / advanced handlers).</summary>
        public void SetAuthorization(NntpAuthorization authorization)
        {
            ArgumentNullException.ThrowIfNull(authorization);
            _authorization = authorization;
            if (!authorization.IsAuthenticated)
            {
                _authentication = NntpAuthenticationState.Unauthenticated;
                _accountPolicy = null;
                _saslExchange = null;
            }
        }

        /// <summary>
        /// Sets the session operating mode and the AUTHINFO authority that accompanies it.
        /// <see cref="NntpSessionMode.Stream"/> selects Transit credentials; any other mode
        /// selects reader (newsmaster / MySQL) credentials.
        /// </summary>
        public void SetMode(NntpSessionMode mode)
        {
            _mode = mode;
            _authenticationAuthority = mode == NntpSessionMode.Stream
                ? NntpAuthenticationAuthority.Transit
                : NntpAuthenticationAuthority.Reader;
        }

        /// <summary>
        /// Selects the AUTHINFO authority without changing <see cref="Mode"/>.
        /// Used by <c>MODE STREAM</c>, which must not change RFC 4644 receive state.
        /// </summary>
        public void SetAuthenticationAuthority(NntpAuthenticationAuthority authority) =>
            _authenticationAuthority = authority;

        /// <summary>Caches the username from <c>AUTHINFO USER</c> (does not authenticate).</summary>
        public void SetPendingAuthUsername(string username)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(username);
            _pendingAuthUsername = username;
        }

        /// <summary>Clears any pending <c>AUTHINFO USER</c> username.</summary>
        public void ClearPendingAuthUsername() => _pendingAuthUsername = null;

        /// <summary>
        /// Applies a successful authentication atomically: identity + authorization, pending USER cleared.
        /// Forces <see cref="NntpAuthorization.IsAuthenticated"/> to <see langword="true"/>.
        /// </summary>
        public void ApplySuccessfulAuthentication(
            string username,
            NntpAuthorization authorization,
            NntpAccountPolicy? policy = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(username);
            ArgumentNullException.ThrowIfNull(authorization);
            var previousPeer = _authorization.TransitPeerName;
            _pendingAuthUsername = null;
            _saslExchange = null;
            _authentication = NntpAuthenticationState.ForUser(username);
            _authorization = authorization.With(isAuthenticated: true);
            _accountPolicy = policy;
            if (previousPeer is not null && _authorization.TransitPeerName is null)
            {
                SessionCensus?.ReleasePeerAttribution(this);
                PeerMetrics?.RetractAccepted(previousPeer);
            }
        }

        /// <summary>
        /// Attaches the inbound TransitPeerState lease taken at connection identification.
        /// The listener still disposes the same lease on disconnect; the lease is one-shot.
        /// </summary>
        internal void AttachTransitAdmission(TransitInboundConnectionLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);
            _transitAdmission = lease;
        }

        /// <summary>
        /// Releases the connection-time TransitPeerState slot when AUTHINFO replaced
        /// named-peer identity with reader privileges. No-op when the session is still
        /// a named Transit peer or no slot was taken.
        /// </summary>
        internal ValueTask ReleaseTransitAdmissionWhenNotTransitAsync()
        {
            if (_authorization.TransitPeerName is not null)
            {
                return ValueTask.CompletedTask;
            }

            return _transitAdmission?.DisposeAsync() ?? ValueTask.CompletedTask;
        }

        /// <summary>
        /// Attaches output accounting after AUTHINFO success and before the 281.
        /// Live remaining is observed from Redis/MySQL, not from the cached policy snapshot.
        /// Every authenticated account participates.
        /// </summary>
        internal async ValueTask AttachByteAccountingAsync(
            NntpResponseWriter response,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(response);
            var policy = _accountPolicy;
            if (policy is null)
            {
                return;
            }

            long? remaining;
            try
            {
                remaining = await AccountBytes.ObserveRemainingAsync(policy.Username, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                remaining = null;
            }

            if (remaining is <= 0)
            {
                AccountBytes.MarkExhausted(policy.Username);
            }
            else if (remaining is > 0)
            {
                AccountBytes.ClearExhausted(policy.Username);
            }

            response.SetByteSink(AccountBytes.CreateSink(policy.Username));
        }

        /// <summary>Gets whether this authenticated session must reject the next command.</summary>
        internal bool IsByteQuotaExhausted
        {
            get
            {
                var policy = _accountPolicy;
                return policy is not null && AccountBytes.IsExhausted(policy.Username);
            }
        }

        /// <summary>
        /// Records a failed authentication attempt: session remains unauthenticated.
        /// Connection-time peer privileges (if any) are restored; pending USER is retained so the
        /// client may retry <c>AUTHINFO PASS</c> or issue a new <c>AUTHINFO USER</c>.
        /// </summary>
        public void ApplyFailedAuthentication()
        {
            _authentication = NntpAuthenticationState.Unauthenticated;
            _authorization = _connectionAuthorization;
            _accountPolicy = null;
            _saslExchange = null;
            ReleaseAdmission();
        }

        /// <summary>Abandons an in-progress SASL exchange without changing identity.</summary>
        internal void AbandonSaslExchange() => _saslExchange = null;

        /// <summary>Stores in-progress SASL state for the next continuation line.</summary>
        internal void SetSaslExchange(NntpSaslExchange exchange)
        {
            ArgumentNullException.ThrowIfNull(exchange);
            _saslExchange = exchange;
        }

        /// <summary>Admits this session for <paramref name="policy"/> after credential success.</summary>
        internal async ValueTask<NntpAuthenticationResult> AdmitAuthenticatedSessionAsync(
            NntpAccountPolicy? policy,
            CancellationToken cancellationToken = default)
        {
            if (policy is null
                || !policy.RequiresAdmission
                || SessionAdmission is null
                || !string.Equals(_admittedAccountName, policy.Username, StringComparison.Ordinal))
            {
                await ReleaseAdmissionAsync().ConfigureAwait(false);
            }

            if (policy is null || !policy.RequiresAdmission || SessionAdmission is null)
            {
                return NntpAuthenticationResult.Success(
                    policy?.Username ?? Authentication.Username ?? "unknown",
                    Authorization,
                    policy);
            }

            var outcome = await SessionAdmission.TryAdmitAsync(
                policy.Username,
                SessionId,
                ClientAddress,
                policy.SessionLimit,
                policy.SrcIpLimit,
                policy.RequiresRateTracking ? policy.RateLimitBps : 0,
                cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case SessionAdmissionResult.Success:
                    _admittedAccountName = policy.Username;
                    if (policy.RequiresRateTracking && Connection.OutboundRate is { } cap)
                    {
                        AccountRates.Register(policy.Username, SessionId, cap, policy.RateLimitBps);
                    }

                    return NntpAuthenticationResult.Success(policy.Username, Authorization, policy);
                case SessionAdmissionResult.SessionLimitExceeded:
                    return NntpAuthenticationResult.TooManySessions;
                case SessionAdmissionResult.SourceAddressLimitExceeded:
                    return NntpAuthenticationResult.TooManySourceAddresses;
                case SessionAdmissionResult.Unavailable:
                    return NntpAuthenticationResult.TransientFailure;
                default:
                    return NntpAuthenticationResult.TransientFailure;
            }
        }

        /// <summary>Gets the current connection lifetime (tests and diagnostics).</summary>
        internal NntpSessionLifetime Lifetime => (NntpSessionLifetime)Volatile.Read(ref _lifetime);

        /// <summary>
        /// Claims the one-shot <see cref="NntpSessionLifetime.Running"/> →
        /// <see cref="NntpSessionLifetime.Finalizing"/> transition.
        /// </summary>
        internal bool TryBeginFinalization()
        {
            var current = Volatile.Read(ref _lifetime);
            if (current is (int)NntpSessionLifetime.Finalizing or (int)NntpSessionLifetime.Finalized)
            {
                return false;
            }

            if (current == (int)NntpSessionLifetime.Running
                && Interlocked.CompareExchange(
                    ref _lifetime,
                    (int)NntpSessionLifetime.Finalizing,
                    (int)NntpSessionLifetime.Running) == (int)NntpSessionLifetime.Running)
            {
                return true;
            }

            return Interlocked.CompareExchange(
                    ref _lifetime,
                    (int)NntpSessionLifetime.Finalizing,
                    (int)NntpSessionLifetime.Created) == (int)NntpSessionLifetime.Created;
        }

        /// <summary>
        /// Releases distributed admission at most once and marks the session finalized.
        /// Safe to call from QUIT, cancellation, timeout, and exception paths.
        /// </summary>
        internal async ValueTask FinalizeAdmissionAsync()
        {
            if (!TryBeginFinalization())
            {
                return;
            }

            try
            {
                await ReleaseAdmissionAsync().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _lifetime, (int)NntpSessionLifetime.Finalized);
            }
        }

        /// <summary>Releases any admission slot held by this session without awaiting Redis.</summary>
        internal void ReleaseAdmission()
        {
            var pending = ReleaseAdmissionAsync();
            if (pending.IsCompletedSuccessfully)
            {
                return;
            }

            if (pending.IsCompleted)
            {
                _ = pending.AsTask().Exception;
                return;
            }

            _ = ObserveAdmissionReleaseAsync(pending);
        }

        /// <summary>Releases any admission slot held by this session.</summary>
        internal async ValueTask ReleaseAdmissionAsync()
        {
            var account = Interlocked.Exchange(ref _admittedAccountName, null);
            if (account is not null)
            {
                AccountRates.Unregister(account, SessionId);
                if (SessionAdmission is not null)
                {
                    await SessionAdmission.ReleaseAsync(account, SessionId).ConfigureAwait(false);
                }
            }
        }

        private async Task ObserveAdmissionReleaseAsync(ValueTask pending)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                SessionStateLogMessages.SessionStateReleaseFailed(
                    _logger,
                    ex,
                    "session",
                    ClientAddress.ToString());
            }
        }

        /// <summary>Records a successful inbound admission for this session's Transit peer, if any.</summary>
        public void RecordPeerAccepted()
        {
            if (Authorization.TransitPeerName is { } peerId)
            {
                PeerMetrics?.RecordAccepted(peerId);
            }
        }

        /// <summary>Records an inbound admission rejection for this session's Transit peer, if any.</summary>
        public void RecordPeerRejected()
        {
            if (Authorization.TransitPeerName is { } peerId)
            {
                PeerMetrics?.RecordRejected(peerId);
            }
        }

        /// <summary>Records one CHECK for this session's Transit peer, if any.</summary>
        public void RecordPeerCheck()
        {
            if (Authorization.TransitPeerName is { } peerId)
            {
                PeerMetrics?.RecordCheck(peerId);
            }
        }

        /// <summary>Records one framed article received from this session's Transit peer, if any.</summary>
        public void RecordPeerArticleReceived(int bytes)
        {
            if (Authorization.TransitPeerName is { } peerId)
            {
                PeerMetrics?.RecordArticleReceived(peerId, bytes);
            }
        }

        /// <summary>
        /// Updates <see cref="ActivityState"/> using the existing feed-session state machine
        /// and forwards to <see cref="FeedProbe"/> when attached.
        /// </summary>
        public void SetActivityState(FeedSessionState state)
        {
            Volatile.Write(ref _activityState, (int)state);
            FeedProbe?.SetState(state);
        }

        /// <summary>
        /// Requests the command loop to exit after the current response (e.g. QUIT / TAKETHIS 400).
        /// Cancels a pending RX <c>ReadAsync</c> so a close requested off the RX stack still ends
        /// the session.
        /// </summary>
        public void RequestClose() => RequestCloseWithReason(TcpDisconnectReason.ProtocolClose);

        /// <summary>
        /// Notes <paramref name="reason"/> (first-wins on the transport) and cancels the session loop.
        /// </summary>
        internal void RequestCloseWithReason(TcpDisconnectReason reason)
        {
            if (Connection is NntpConnection nntpConnection)
            {
                nntpConnection.NoteDisconnectReason(reason);
            }

            Interlocked.CompareExchange(
                ref _closeReason,
                (int)reason,
                (int)TcpDisconnectReason.Unspecified);

            Interlocked.Exchange(ref _closeRequested, 1);
            try
            {
                _closeCts.Cancel();
            }
            catch (ObjectDisposedException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
        }

        /// <summary>
        /// Marks that an NNTP command has been accepted for processing.
        /// Timestamp and work count are published before the idle-epoch CAS so a waiter
        /// that has not yet committed close observes activity and cannot close on stale idle.
        /// </summary>
        /// <remarks>
        /// Ownership: if this method increments the epoch while the high bit of
        /// <see cref="_idleState"/> is still clear, idle-timeout close must not win.
        /// If the high bit is already set, this command loses and runs on the closing session.
        /// </remarks>
        internal void BeginCommandWork()
        {
            Volatile.Write(ref _lastCommandTimestamp, _timeProvider.GetTimestamp());
            Interlocked.Increment(ref _commandWork);
            CommitCommandActivityEpoch();
        }

        /// <summary>
        /// Increments the idle activity epoch unless idle-timeout close has already committed.
        /// </summary>
        private void CommitCommandActivityEpoch()
        {
            while (true)
            {
                var current = Interlocked.Read(ref _idleState);
                if ((current & IdleClosedFlag) != 0)
                {
                    return;
                }

                var next = current + 1;
                if (Interlocked.CompareExchange(ref _idleState, next, current) == current)
                {
                    return;
                }
            }
        }

        /// <summary>High bit of <see cref="_idleState"/>: idle waiter committed close.</summary>
        private const long IdleClosedFlag = unchecked((long)0x8000_0000_0000_0000);

        /// <summary>
        /// Marks that one accepted NNTP command (or pipeline slot) has finished.
        /// Timestamp is published before the work count drops to zero so a waiter
        /// cannot treat a just-finished long command as already idle.
        /// </summary>
        internal void EndCommandWork()
        {
            Volatile.Write(ref _lastCommandTimestamp, _timeProvider.GetTimestamp());
            Interlocked.Decrement(ref _commandWork);
        }

        /// <summary>Signaled once the per-session idle waiter is armed after the greeting.</summary>
        internal Task IdleWatchArmed => _idleWatchArmed.Task;

        /// <summary>The idle-watch task, or <see langword="null"/> before <see cref="RunAsync"/> starts it.</summary>
        internal Task? IdleWatchTaskForTests => _idleWatchTask;

        /// <summary>Session-requested close reason (first-wins); tests and idle timeout.</summary>
        internal TcpDisconnectReason CloseReasonForTests =>
            (TcpDisconnectReason)Volatile.Read(ref _closeReason);

        /// <summary>In-flight accepted command / pipeline-slot count (tests).</summary>
        internal int CommandWorkForTests => Volatile.Read(ref _commandWork);

        /// <summary>Gets whether the idle waiter has committed <see cref="TcpDisconnectReason.IdleTimeout"/>.</summary>
        internal bool IdleCloseCommittedForTests =>
            (Interlocked.Read(ref _idleState) & IdleClosedFlag) != 0;

        /// <summary>
        /// Test-only: set when the idle waiter has observed a due deadline and is about to
        /// CAS-commit close. Production never sets this.
        /// </summary>
        internal TaskCompletionSource? IdleAboutToCommit { get; set; }

        /// <summary>
        /// Test-only: when set, the idle waiter awaits this after <see cref="IdleAboutToCommit"/>
        /// and before the close CAS. Production never sets this.
        /// </summary>
        internal Task? IdleCommitHold { get; set; }

        /// <summary>
        /// Sends the initial greeting and runs the command loop until QUIT, EOF, or cancellation.
        /// </summary>
        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            if (Connection is NntpConnection nntpConnection)
            {
                nntpConnection.AttachInputReaderConsumer();
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                Connection.ConnectionClosed,
                _closeCts.Token);
            var token = linked.Token;

            try
            {
                Interlocked.CompareExchange(
                    ref _lifetime,
                    (int)NntpSessionLifetime.Running,
                    (int)NntpSessionLifetime.Created);
                SessionCensus?.Register(this);
                var response = _response ??= new NntpResponseWriter(Connection.Output);
                Pipeline = new CheckPipeline(this, response);
                TakeThisWindow = new TakeThisPipeline(this, response);
                await SendGreetingAsync(response, token).ConfigureAwait(false);
                _idleWatchTask = WatchIdleAsync(token);

                var readerRx = new NntpReaderCommandRx(this, _dispatcher, _logger);
                var streamRx = new NntpStreamDataPlaneRx(this, _dispatcher, _logger);

                while (!token.IsCancellationRequested && Volatile.Read(ref _closeRequested) == 0)
                {
                    var progressed = ReceiveStrategy == NntpReceiveStrategy.ReaderCommand
                        ? await readerRx.ProcessOneAsync(response, token).ConfigureAwait(false)
                        : await streamRx.ProcessOneAsync(response, token).ConfigureAwait(false);
                    if (!progressed)
                    {
                        break;
                    }

                    // Coalesced fire-and-forget lines (CHECK EnqueueLineAsync) flush when the
                    // inbound pipe has nothing ready. TAKETHIS uses EnqueueLineImmediateAsync
                    // and does not wait for this idle flush. STREAM leftover stays unconsumed
                    // so CHECK can still reach its coalesce batch.
                    if (response.HasCoalescedUnflushed && !HasUnconsumedInput(Connection.Input))
                    {
                        await response.FlushCoalescedAsync(token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
                // Expected on shutdown or connection close.
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (NntpPeerDisconnect.IsPeerDisconnect(ex, Connection))
            {
                SessionLogMessages.SessionEndedByPeerDisconnect(_logger, ex, ClientAddress);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                SessionLogMessages.SessionEndedWithError(_logger, ex, ClientAddress);
            }
            finally
            {
                try
                {
                    _closeCts.Cancel();
                }
                catch (ObjectDisposedException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }

                if (_idleWatchTask is not null)
                {
                    try
                    {
                        await _idleWatchTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex)
                    {
                        _ = ex; // observed; intentionally ignored at this boundary
                    }
                }

                SessionCensus?.Unregister(this);
                try
                {
                    await FinalizeAdmissionAsync().ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    SessionStateLogMessages.SessionStateReleaseFailed(
                        _logger,
                        ex,
                        "session",
                        ClientAddress.ToString());
                }
                if (TakeThisWindow is not null)
                {
                    try
                    {
                        await TakeThisWindow.ShutdownAsync().ConfigureAwait(false);
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                    {
                        _ = ignored; // observed; intentionally ignored at this best-effort boundary
                        // Best-effort; peeks use the session token.
                    }
                }

                if (Pipeline is not null)
                {
                    try
                    {
                        await Pipeline.ShutdownAsync().ConfigureAwait(false);
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                    {
                        _ = ignored; // observed; intentionally ignored at this best-effort boundary
                        // Best-effort; lookups use the session token.
                    }
                }

                if (_response is not null)
                {
                    try
                    {
                        await _response.DisposeAsync().ConfigureAwait(false);
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                    {
                        _ = ignored; // observed; intentionally ignored at this best-effort boundary
                        // Best-effort.
                    }
                }

                try
                {
                    await StreamArticleTx.DisposeAsync().ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                {
                    _ = ignored; // observed; intentionally ignored at this best-effort boundary
                    // Best-effort.
                }

                try
                {
                    if (Connection is NntpConnection inputOwner)
                    {
                        await inputOwner.CompleteInputReaderAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        await Connection.Input.CompleteAsync().ConfigureAwait(false);
                    }
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                {
                    _ = ignored; // observed; intentionally ignored at this best-effort boundary
                    // Best-effort; reader may already be completed.
                }

                try
                {
                    await Connection.CompleteAsync().ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                {
                    _ = ignored; // observed; intentionally ignored at this best-effort boundary
                    // Best-effort.
                }

                _closeCts.Dispose();
            }
        }

        private ValueTask SendGreetingAsync(NntpResponseWriter response, CancellationToken cancellationToken)
        {
            if (_authorization.PostingPermitted)
            {
                return response.WriteLineAsync(NntpResponses.GreetingPostingPermitted, cancellationToken);
            }

            return response.WriteLineAsync(NntpResponses.GreetingPostingProhibited, cancellationToken);
        }

        /// <summary>
        /// Logs and dispatches one already-parsed command (shared by both RX strategies).
        /// </summary>
        internal async ValueTask DispatchCommandAsync(
            NntpCommandDispatcher dispatcher,
            NntpResponseWriter response,
            ILogger logger,
            NntpCommand command,
            ReadOnlyMemory<byte> line,
            NntpMultilineReadResult? preReadArticle,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(response);
            ArgumentNullException.ThrowIfNull(logger);

            if (command.Verb != NntpVerb.BenchIt
                && !NntpCommandLogFormat.SuppressHotPathCommand(command.Verb)
                && logger.IsEnabled(LogLevel.Debug))
            {
                var client = NntpCommandLogFormat.Client(this);
                var commandText = NntpCommandLogFormat.RedactRxCommand(command, line.Span);
                CommandLogMessages.CommandRx(logger, client, commandText);
            }

            await dispatcher
                .DispatchAsync(this, command, line, response, cancellationToken, preReadArticle)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Dispatches one parsed unit: authorized CHECK enters the CHECK pipeline; every other
        /// command drains outstanding CHECK and TAKETHIS windows first and then runs serially.
        /// </summary>
        internal async ValueTask ProcessParsedCommandAsync(
            NntpCommandDispatcher dispatcher,
            NntpResponseWriter response,
            ILogger logger,
            NntpCommand command,
            ReadOnlyMemory<byte> line,
            NntpMultilineReadResult? preReadArticle,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(Pipeline);
            ArgumentNullException.ThrowIfNull(TakeThisWindow);

            BeginCommandWork();
            try
            {
                if (IsByteQuotaExhausted)
                {
                    await response
                        .WriteLineAsync(NntpResponses.ServiceTemporarilyUnavailable, cancellationToken)
                        .ConfigureAwait(false);
                    RequestClose();
                    return;
                }

                // Transit CHECK fast-path: only on a Transit host with transit authority.
                // Reader hosts must fall through to the dispatcher role gate (502).
                if (command.IsValid
                    && command.Verb == NntpVerb.Check
                    && ServerRole == NntpServerRole.Transit
                    && Authorization.AuthorizedTransit)
                {
                    await TakeThisWindow.DrainAsync(cancellationToken).ConfigureAwait(false);
                    if (command.Verb != NntpVerb.BenchIt
                        && !NntpCommandLogFormat.SuppressHotPathCommand(command.Verb)
                        && logger.IsEnabled(LogLevel.Debug))
                    {
                        var client = NntpCommandLogFormat.Client(this);
                        var commandText = NntpCommandLogFormat.RedactRxCommand(command, line.Span);
                        CommandLogMessages.CommandRx(logger, client, commandText);
                    }

                    await Pipeline.SubmitAsync(command, line, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await TakeThisWindow.DrainAsync(cancellationToken).ConfigureAwait(false);
                await Pipeline.DrainAsync(cancellationToken).ConfigureAwait(false);
                await DispatchCommandAsync(dispatcher, response, logger, command, line, preReadArticle, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                EndCommandWork();
            }
        }

        /// <summary>
        /// Waits until <c>lastCommandActivity + IdleTime</c> with no accepted command work,
        /// then closes the session. One Delay per wait; no per-command timer or lock.
        /// </summary>
        private async Task WatchIdleAsync(CancellationToken cancellationToken)
        {
            Volatile.Write(ref _lastCommandTimestamp, _timeProvider.GetTimestamp());
            _idleWatchArmed.TrySetResult();
            try
            {
                while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _closeRequested) == 0)
                {
                    if (Volatile.Read(ref _commandWork) > 0)
                    {
                        await Task.Delay(_commandIdleTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var last = Volatile.Read(ref _lastCommandTimestamp);
                    var epochAtIdle = Interlocked.Read(ref _idleState);
                    if ((epochAtIdle & IdleClosedFlag) != 0)
                    {
                        return;
                    }

                    var elapsed = _timeProvider.GetElapsedTime(last, _timeProvider.GetTimestamp());
                    var remaining = _commandIdleTimeout - elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, _timeProvider, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // Ownership snapshot: CAS this epoch later. BeginCommandWork increments
                    // the epoch before publishing work; if that increment lands first,
                    // CompareExchange(epochAtIdle) fails and this stale idle decision cannot close.
                    // Re-reading _idleState after the hold would observe the new epoch and
                    // incorrectly close after activity had already committed.
                    var aboutToCommit = IdleAboutToCommit;
                    aboutToCommit?.TrySetResult();
                    if (IdleCommitHold is { } hold)
                    {
                        await hold.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (Volatile.Read(ref _commandWork) > 0
                        || Volatile.Read(ref _lastCommandTimestamp) != last)
                    {
                        continue;
                    }

                    if (Interlocked.CompareExchange(ref _idleState, epochAtIdle | IdleClosedFlag, epochAtIdle)
                        != epochAtIdle)
                    {
                        continue;
                    }

                    RequestCloseWithReason(TcpDisconnectReason.IdleTimeout);
                    return;
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
        }

        /// <summary>Waits until the CHECK window can accept another command (RX backpressure).</summary>
        internal ValueTask WaitForCheckCapacityAsync(CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(Pipeline);
            return Pipeline.IsFull
                ? Pipeline.WaitForCapacityAsync(cancellationToken)
                : ValueTask.CompletedTask;
        }

        /// <summary>Waits until the TAKETHIS window can accept another command (RX backpressure).</summary>
        internal ValueTask WaitForTakeThisCapacityAsync(CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(TakeThisWindow);
            return TakeThisWindow.IsFull
                ? TakeThisWindow.WaitForCapacityAsync(cancellationToken)
                : ValueTask.CompletedTask;
        }

        /// <summary>
        /// Emits an already-decided news event. Failures are reported through diagnostics
        /// and do not change protocol state.
        /// </summary>
        internal void TryWriteNews(in NewsLogEvent evt)
        {
            try
            {
                NewsLog.Write(in evt);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                var id = evt.MessageId.IsEmpty
                    ? "-"
                    : System.Text.Encoding.ASCII.GetString(evt.MessageId.Span);
                SpoolLogMessages.NewsLogFailed(_logger, ex, id);
            }
        }

        /// <summary>Logs a parser rejection without converting the full command line to a string.</summary>
        internal void LogCommandRejected(NntpCommand command, string detail)
        {
            if (!_logger.IsEnabled(LogLevel.Debug))
            {
                return;
            }

            var client = NntpCommandLogFormat.Client(this);
            CommandLogMessages.CommandRejected(
                _logger,
                client,
                command.Verb,
                command.Qualifier,
                command.Status,
                detail);
        }

        /// <summary>
        /// Peeks the application input pipe without consuming. Used to decide whether a partial
        /// coalesced TX batch must flush before the session waits for more inbound octets.
        /// </summary>
        private static bool HasUnconsumedInput(PipeReader input)
        {
            try
            {
                if (!input.TryRead(out var result))
                {
                    return false;
                }

                var has = !result.Buffer.IsEmpty;
                // Peek only. Examined must stay at Start: AdvanceTo(Start, End) tells the Pipe
                // the reader needs more data, so the next ReadAsync waits even though leftover
                // TAKETHIS bytes are already buffered (same rule as NntpContinuousRxReader
                // after a complete unit, and NntpConnection.EnsureApplicationInputDrainedForUpgrade).
                input.AdvanceTo(result.Buffer.Start, result.Buffer.Start);
                return has;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
