using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Runtime.ExceptionServices;
using System.Net.Security;
using VectorNNTPD.Common.Articles;
using System.Buffers;
using System.Text;

namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// One upstream NNTP session. ARTICLE and DATE share one exclusive busy lock.
    /// </summary>
    /// <remarks>
    /// Connect accepts a 200 or 201 greeting, always sends <c>CAPABILITIES</c>, sends <c>STARTTLS</c> only when that label is advertised and the provider is not already on implicit TLS, then sends <c>AUTHINFO</c> when credentials are required.
    /// The session owns the transport after connect, including when connect returns a failure. The caller disposes it.
    /// </remarks>
    internal sealed class NntpProviderSession : IAsyncDisposable
    {
        /// <summary>Pre-encoded <c>QUIT</c> command written by <see cref="DisposeAsync"/>.</summary>
        private static readonly byte[] QuitCommand = "QUIT\r\n"u8.ToArray();

        /// <summary>
        /// Upper bound for one graceful <c>QUIT</c> when disposal is not sharing a shorter <see cref="NntpShutdownDeadline"/>.
        /// A shared deadline can only shorten this allowance.
        /// </summary>
        private static readonly TimeSpan QuitAllowance = TimeSpan.FromSeconds(2);

        /// <summary>Provider identity, TLS mode, credentials, and keepalive interval captured at construction.</summary>
        private readonly BackFillerProviderDefinition _provider;

        /// <summary>Timeouts, buffer size, and article ceiling used for this connection.</summary>
        private readonly NntpSessionOptions _options;

        /// <summary>Session logger. Command and response traces are Debug.</summary>
        private readonly ILogger _logger;

        /// <summary>Stable Debug prefix built by <see cref="FormatWireIdentity"/>.</summary>
        private readonly string _wireIdentity;

        /// <summary>
        /// Serializes <see cref="DownloadArticleAsync"/> and <see cref="SendDateKeepAliveAsync"/>.
        /// Held from the start of the command until its response has been consumed or the attempt has failed.
        /// Not held during connect. <see cref="WaitForCurrentCommandAsync"/> waits for the current holder and does not keep the gate.
        /// </summary>
        /// <remarks>
        /// <see cref="DisposeAsync"/> does not dispose this semaphore. A command may still call <see cref="ReleaseCommandGate"/> after disposal has returned, and disposing the gate first throws from that release.
        /// The async wait path does not allocate the semaphore's kernel handle. The gate is abandoned with the session once every holder has released it.
        /// </remarks>
        private readonly SemaphoreSlim _busy = new(1, 1);

        /// <summary>Owned transport. Null until connect assigns it, and null again if a TLS upgrade fails after closing it.</summary>
        private Stream? _stream;

        /// <summary>Reader over <see cref="_stream"/>. Replaced after a STARTTLS upgrade so leftover cleartext is not parsed as TLS.</summary>
        private NntpStreamReader? _reader;

        /// <summary>Zero until <see cref="DisposeAsync"/> runs. A second dispose returns immediately.</summary>
        private int _disposed;

        /// <summary>
        /// Serializes publication of the connected transport and of a STARTTLS wrapper with <see cref="DisposeAsync"/>'s capture of <see cref="_stream"/>.
        /// Not held across connect, the handshake, test gates, or any other await.
        /// </summary>
        private readonly Lock _transportGate = new();

        /// <summary>
        /// One while <see cref="UpgradeExistingTransportToTlsAsync"/> owns the plaintext transport.
        /// <see cref="DisposeAsync"/> then leaves disposal of that transport to the upgrade.
        /// Guarded by <see cref="_transportGate"/>.
        /// </summary>
        private int _upgradeOwnsTransport;

        /// <summary>
        /// Cancelled by the first <see cref="DisposeAsync"/> so an in-progress STARTTLS handshake ends
        /// without the caller's token. A callback exception is logged and does not skip the rest of disposal.
        /// Not disposed here: the handshake may still be linking this source.
        /// </summary>
        private readonly CancellationTokenSource _handshakeCancellation = new();

        /// <summary>
        /// Test gate invoked after STARTTLS has captured its transport and before the handshake reads that transport.
        /// Null in production. Disposal during the gate must not make the handshake observe a null field.
        /// </summary>
        private Func<Task>? _beforeStartTlsHandshake;

        /// <summary>
        /// Test gate invoked after the TLS wrapper exists and before it is stored in <see cref="_stream"/>.
        /// Null in production. The argument is that wrapper. The delegate runs outside <see cref="_transportGate"/>.
        /// </summary>
        private Func<Stream, Task>? _beforePublishUpgradedTransport;

        /// <summary>
        /// Test factory for the STARTTLS <see cref="SslStream"/>. Null in production, which uses <see cref="SslStream"/> directly.
        /// The factory must leave the inner stream owned by the wrapper.
        /// </summary>
        private Func<Stream, SslStream>? _tlsStreamFactory;

        /// <summary>
        /// Test gate after the pre-lock ARTICLE readiness check and before <see cref="_busy"/> is acquired.
        /// Null in production.
        /// </summary>
        private Func<Task>? _beforeArticleBusyWait;

        /// <summary>Set when a command failure must keep the session out of the idle pool. Sticky for the session lifetime.</summary>
        private bool _unhealthy;

        /// <summary>
        /// <see cref="Stopwatch.GetTimestamp"/> taken immediately before ARTICLE bytes are written.
        /// Zero until that write is attempted, and reset to zero at the start of each download.
        /// </summary>
        internal long ArticleCommandStartedTimestamp { get; private set; }

        /// <summary>Initializes a session that is not yet connected.</summary>
        /// <param name="provider">Upstream provider identity and capacity.</param>
        /// <param name="options">Session I/O timeouts and article limits.</param>
        /// <param name="logger">Session logger. Wire traces are Debug only.</param>
        /// <param name="connectionNumber">
        /// One-based slot in <see cref="BackFillerProviderDefinition.MaxSessions"/>.
        /// Stable for this session's lifetime.
        /// </param>
        internal NntpProviderSession(
            BackFillerProviderDefinition provider,
            NntpSessionOptions options,
            ILogger logger,
            int connectionNumber = 1)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentOutOfRangeException.ThrowIfLessThan(connectionNumber, 1);
            _provider = provider;
            _options = options;
            _logger = logger;
            ConnectionNumber = connectionNumber;
            _wireIdentity = FormatWireIdentity(provider, connectionNumber);
            State = NntpSessionState.Created;
        }

        /// <summary>Gets the one-based pool slot captured at construction.</summary>
        internal int ConnectionNumber { get; }

        /// <summary>
        /// Gets the stable Debug wire-log prefix
        /// <c>{Backbone}/{Account}[{ConnectionNumber:000}/{MaxSessions}]</c>.
        /// </summary>
        internal string WireLogIdentity => _wireIdentity;

        /// <summary>
        /// Gets the session lifecycle updated by connect, <c>ARTICLE</c>, <c>DATE</c>, and <see cref="DisposeAsync"/>.
        /// </summary>
        /// <remarks>
        /// After <see cref="DisposeAsync"/> sets <see cref="_disposed"/>, later command paths do not assign this property.
        /// <see cref="DisposeAsync"/> is what leaves it <see cref="NntpSessionState.Closed"/>.
        /// </remarks>
        internal NntpSessionState State { get; private set; }

        /// <summary>Gets a value indicating whether the session may return to the idle pool.</summary>
        /// <remarks>
        /// True only when the session has not been marked unhealthy, <see cref="State"/> is <see cref="NntpSessionState.Ready"/>, and <see cref="_stream"/> is assigned.
        /// The read does not take <see cref="_busy"/>.
        /// </remarks>
        internal bool IsReusable => !_unhealthy && State == NntpSessionState.Ready && _stream is not null;

        /// <summary>Gets the MySQL <c>keepalive</c> interval this session was constructed with.</summary>
        internal byte KeepAliveSeconds => _provider.KeepAliveSeconds;

        /// <summary>
        /// Pauses ARTICLE after the pre-lock readiness check and before <see cref="DownloadArticleAsync"/> waits for <see cref="_busy"/>.
        /// Null in production.
        /// </summary>
        /// <remarks>
        /// A test uses this to prove the check ran while DATE still holds <see cref="_busy"/>, then lets DATE fail before ARTICLE waits.
        /// The gate does not run again after the lock is acquired.
        /// </remarks>
        internal Func<Task>? BeforeArticleBusyWait
        {
            get => _beforeArticleBusyWait;
            set => _beforeArticleBusyWait = value;
        }

        /// <summary>
        /// Pauses STARTTLS after the transport is captured and before <see cref="NntpTlsClient.AuthenticateAsClientAsync"/> uses it.
        /// Null unless a test is holding that window.
        /// </summary>
        internal Func<Task>? BeforeStartTlsHandshake
        {
            get => _beforeStartTlsHandshake;
            set => _beforeStartTlsHandshake = value;
        }

        /// <summary>
        /// Pauses STARTTLS after the handshake returns a wrapper and before that wrapper is published.
        /// Null unless a test is holding that window.
        /// </summary>
        internal Func<Stream, Task>? BeforePublishUpgradedTransport
        {
            get => _beforePublishUpgradedTransport;
            set => _beforePublishUpgradedTransport = value;
        }

        /// <summary>
        /// Replaces <see cref="SslStream"/> construction for STARTTLS. Null in production.
        /// </summary>
        /// <remarks>The returned stream must own its inner stream, matching <see cref="NntpTlsClient.AuthenticateAsClientAsync"/>.</remarks>
        internal Func<Stream, SslStream>? TlsStreamFactory
        {
            get => _tlsStreamFactory;
            set => _tlsStreamFactory = value;
        }

        /// <summary>
        /// Connects, validates the greeting, issues CAPABILITIES, upgrades via STARTTLS
        /// when that capability is advertised and the provider is not already using implicit TLS, and authenticates when configured.
        /// </summary>
        /// <param name="transport">Opens the socket. Implicit TLS is applied by the transport when <see cref="BackFillerProviderDefinition.UseTls"/> is set.</param>
        /// <param name="cancellationToken">
        /// Cancels connect and later command I/O. Caller cancellation returns <see cref="ArticleRetrievalKind.Cancelled"/> and does not throw.
        /// </param>
        /// <returns><see langword="null"/> when the session is <see cref="NntpSessionState.Ready"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="transport"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The session was already disposed.</exception>
        /// <remarks>
        /// A 200 or 201 greeting is required. <c>CAPABILITIES</c> is always sent. <c>STARTTLS</c> is sent only when advertised and <see cref="BackFillerProviderDefinition.UseTls"/> is false.
        /// Protocol, timeout, socket, and I/O failures return a non-reusable <see cref="ArticleRetrievalResult"/> with <see cref="State"/> <see cref="NntpSessionState.Retiring"/>.
        /// <see cref="SocketException"/> is classified here. The reason stored on that result is the exception type name, not the socket message.
        /// The transport connect budget is <see cref="NntpSessionOptions.ConnectTimeout"/>. The greeting and later status lines use <see cref="NntpSessionOptions.CommandTimeout"/>.
        /// The connected stream is published under <see cref="_transportGate"/> after <see cref="INntpTransportFactory.ConnectAsync"/> returns.
        /// The gate is not held across that connect. Disposal that wins first leaves the fields clear, and this method disposes the unpublished stream once.
        /// The caller still owns disposal of this session when a failure result is returned.
        /// </remarks>
        internal async Task<ArticleRetrievalResult?> ConnectAsync(
            INntpTransportFactory transport,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transport);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            AssignState(NntpSessionState.Connecting);
            NntpLogMessages.WireConnecting(
                _logger,
                _wireIdentity,
                _provider.Host,
                _provider.Port,
                _provider.UseTls ? "true" : "false");
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(_options.ConnectTimeout);
                var connected = await transport.ConnectAsync(_provider, _options, connectCts.Token).ConfigureAwait(false);
                if (!TryPublishConnectedTransport(connected))
                {
                    await connected.DisposeAsync().ConfigureAwait(false);
                    return ArticleRetrievalResult.Failed(
                        ArticleRetrievalKind.ProviderFailure,
                        null,
                        "NNTP session was disposed during connect.",
                        sessionReusable: false);
                }

                var greeting = await ReadStatusLineAsync(cancellationToken).ConfigureAwait(false);
                if (greeting is null)
                {
                    return FailClosed(ArticleRetrievalKind.ProviderFailure, null, "NNTP greeting was empty.", reusable: false);
                }

                var parsedGreeting = ConsumedStatus.Parse(greeting.ConsumedLine.Span, StatusTextNeed.UnlessGreeting);
                if (parsedGreeting.IsMalformed)
                {
                    return FailClosed(ArticleRetrievalKind.ProviderFailure, null, "NNTP greeting was malformed.", reusable: false);
                }

                if (!NntpStatusCode.IsServiceReadyGreeting(parsedGreeting.Code))
                {
                    return FailClosed(ArticleRetrievalKind.ProviderFailure, parsedGreeting.Code, parsedGreeting.Text, reusable: false);
                }

                var startTls = await NegotiateCapabilitiesAndStartTlsAsync(cancellationToken).ConfigureAwait(false);
                if (startTls is not null)
                {
                    return startTls;
                }

                var auth = await AuthenticateIfConfiguredAsync(cancellationToken).ConfigureAwait(false);
                if (auth is not null)
                {
                    return auth;
                }

                AssignState(NntpSessionState.Ready);
                NntpLogMessages.SessionReady(_logger, _provider.Backbone, _provider.Host, _provider.Port, _provider.UseTls);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return FailClosed(ArticleRetrievalKind.Cancelled, null, "NNTP connect was cancelled.", reusable: false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                return FailClosed(ArticleRetrievalKind.ProviderFailure, null, "NNTP connect timed out.", reusable: false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or InvalidOperationException or AuthenticationException or FormatException or InvalidDataException or TimeoutException)
            {
                return FailClosed(ArticleRetrievalKind.ProviderFailure, null, ex.GetType().Name, reusable: false);
            }
        }

        /// <summary>Issues ARTICLE with the exact Message-ID bytes.</summary>
        /// <param name="messageId">Argument appended to <c>ARTICLE </c>. Sent as ASCII with no angle-bracket check. CR and LF are not rejected.</param>
        /// <param name="cancellationToken">
        /// Cancellation while waiting for <see cref="_busy"/> throws <see cref="OperationCanceledException"/> and leaves the session unchanged.
        /// After the lock is held, cancellation returns <see cref="ArticleRetrievalKind.Cancelled"/> and marks the session unhealthy.
        /// </param>
        /// <param name="consumePayload">
        /// When null, a successful article is copied into an owned <see cref="RetrievedArticle"/>.
        /// When not null, it is invoked synchronously while this session still holds <see cref="_busy"/> and before
        /// <see cref="State"/> returns to <see cref="NntpSessionState.Ready"/>, with the reader scratch prefix.
        /// That memory is valid only until the delegate returns. The delegate must not store it and must not await.
        /// It is called only after the terminator is recognized and the payload has a header/body separator.
        /// An exception from the delegate is rethrown after <see cref="_busy"/> is released and does not mark the session unhealthy.
        /// </param>
        /// <param name="maxArticleBytes">
        /// Article-size boundary for this download. Zero uses <see cref="NntpSessionOptions.MaxArticleBytes"/>.
        /// </param>
        /// <returns>
        /// Status <see cref="NntpStatusCode.ArticleFollows"/> returns the destuffed payload when <see cref="NntpProtocolIo.HasHeaderBodySeparator"/> is true.
        /// With <paramref name="consumePayload"/> null, that payload is an owned <see cref="RetrievedArticle"/>.
        /// With <paramref name="consumePayload"/> not null, the result's article is null because the delegate already consumed the scratch.
        /// Status <see cref="NntpStatusCode.NoArticleWithMessageId"/> is <see cref="ArticleRetrievalKind.ArticleNotFound"/> and the session stays reusable.
        /// A 220 payload with no header/body separator is <see cref="ArticleRetrievalKind.InvalidArticle"/> and stays reusable.
        /// A payload that reaches <see cref="NntpSessionOptions.MaxArticleBytes"/> is <see cref="ArticleRetrievalKind.InvalidArticle"/> and the session is marked unhealthy because the multiline response is no longer synchronized.
        /// <see cref="NntpStatusCode.IsAuthenticationFailure"/>, <see cref="NntpStatusCode.IsCommandRejected"/>, statuses 412, 420, and 423, and every other status mark the session unhealthy.
        /// </returns>
        /// <exception cref="ArgumentException"><paramref name="messageId"/> is null or whitespace.</exception>
        /// <remarks>
        /// A non-ASCII message-id fails before the lock is taken and leaves the session reusable.
        /// A closed or retiring session, or one whose reader or stream is missing, returns a non-reusable provider failure without taking the lock and without setting <see cref="_unhealthy"/>.
        /// After <see cref="_busy"/> is acquired, <see cref="IsReusable"/> is checked again before <see cref="State"/> becomes <see cref="NntpSessionState.Busy"/> and before ARTICLE is written.
        /// A DATE failure that marked the session unhealthy while this call waited returns that same non-reusable provider failure and does not write ARTICLE.
        /// A status-line timeout becomes <see cref="TimeoutException"/> inside <see cref="ReadStatusLineAsync"/> and is then classified as provider failure whose reason is that exception's type name.
        /// A payload timeout is classified as provider failure with the reason <c>ARTICLE timed out.</c>
        /// Disposal during the command clears the session fields and aborts the transport. It does not dispose <see cref="_busy"/>. The in-flight call keeps the reader it already captured and releases the gate after the attempt ends.
        /// </remarks>
        internal async Task<ArticleRetrievalResult> DownloadArticleAsync(
            string messageId,
            CancellationToken cancellationToken,
            Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult>? consumePayload = null,
            int maxArticleBytes = 0)
        {
            ArticleCommandStartedTimestamp = 0;
            if (maxArticleBytes <= 0)
            {
                maxArticleBytes = _options.MaxArticleBytes;
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            var reader = _reader;
            if (reader is null || _stream is null || State is NntpSessionState.Closed or NntpSessionState.Retiring)
            {
                return WithCommandTimestamp(ArticleRetrievalResult.Failed(
                    ArticleRetrievalKind.ProviderFailure,
                    null,
                    "NNTP session is not ready.",
                    sessionReusable: false));
            }

            if (!NntpProtocolIo.TryEncodeAscii(messageId, out var messageIdBytes))
            {
                return WithCommandTimestamp(ArticleRetrievalResult.Failed(
                    ArticleRetrievalKind.ProviderFailure,
                    null,
                    "Message-ID is not ASCII and cannot be sent on the NNTP wire.",
                    sessionReusable: true));
            }

            if (_beforeArticleBusyWait is { } beforeBusy)
            {
                await beforeBusy().ConfigureAwait(false);
            }

            await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
            var previous = State;
            ExceptionDispatchInfo? consumerError = null;
            try
            {
                if (!IsReusable)
                {
                    return WithCommandTimestamp(ArticleRetrievalResult.Failed(
                        ArticleRetrievalKind.ProviderFailure,
                        null,
                        "NNTP session is not ready.",
                        sessionReusable: false));
                }

                AssignState(NntpSessionState.Busy);
                var commandLength = NntpProtocolIo.ArticlePrefix.Length + messageIdBytes.Length + NntpProtocolIo.Crlf.Length;
                var command = ArrayPool<byte>.Shared.Rent(commandLength);
                try
                {
                    NntpProtocolIo.ArticlePrefix.CopyTo(command.AsSpan());
                    messageIdBytes.CopyTo(command.AsSpan(NntpProtocolIo.ArticlePrefix.Length));
                    NntpProtocolIo.Crlf.CopyTo(command.AsSpan(NntpProtocolIo.ArticlePrefix.Length + messageIdBytes.Length));
                    ArticleCommandStartedTimestamp = Stopwatch.GetTimestamp();
                    var wireCommand = FormatArticleDebugCommand(_logger, messageId);
                    await WriteCommandAsync(
                            wireCommand,
                            command.AsMemory(0, commandLength),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(command);
                }

                var statusLine = await ReadStatusLineAsync(cancellationToken).ConfigureAwait(false);
                if (statusLine is null)
                {
                    return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, "NNTP ARTICLE status was empty.");
                }

                var status = ConsumedStatus.Parse(statusLine.ConsumedLine.Span, StatusTextNeed.UnlessArticleFollows);
                if (status.IsMalformed)
                {
                    return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, "NNTP ARTICLE status was malformed.");
                }

                var code = status.Code;
                var text = status.Text;

                if (code == NntpStatusCode.ArticleFollows)
                {
                    if (consumePayload is null)
                    {
                        byte[] payload;
                        try
                        {
                            payload = await reader
                                .ReadArticlePayloadAsync(maxArticleBytes, _options.ReceiveTimeout, cancellationToken)
                                .ConfigureAwait(false);
                            NntpLogMessages.WireArticlePayloadComplete(_logger, _wireIdentity, payload.Length);
                        }
                        catch (EndOfStreamException)
                        {
                            return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, code, "NNTP article ended before terminator.");
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return MarkUnhealthy(ArticleRetrievalKind.Cancelled, code, "ARTICLE receive was cancelled.");
                        }
                        catch (OperationCanceledException)
                        {
                            return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, code, "ARTICLE receive timed out.");
                        }
                        catch (InvalidOperationException ex) when (ex.Message.Contains("MaxArticleBytes", StringComparison.Ordinal))
                        {
                            // Destuffed size exceeded the hard ceiling: permanently unusable for Article Work.
                            // Retire the session because the multiline response is no longer synchronized.
                            return MarkUnhealthy(ArticleRetrievalKind.InvalidArticle, code, "NNTP article exceeded MaxArticleBytes.");
                        }

                        if (payload.Length == 0 || !NntpProtocolIo.HasHeaderBodySeparator(payload))
                        {
                            AssignState(NntpSessionState.Ready);
                            return WithCommandTimestamp(ArticleRetrievalResult.Failed(
                                ArticleRetrievalKind.InvalidArticle,
                                code,
                                "ARTICLE payload is missing a header/body separator.",
                                sessionReusable: true));
                        }

                        AssignState(NntpSessionState.Ready);
                        return WithCommandTimestamp(ArticleRetrievalResult.Retrieved(code, text, new RetrievedArticle(payload)));
                    }

                    var accepted = false;
                    try
                    {
                        _ = await reader.ReadArticlePayloadAsync(
                                maxArticleBytes,
                                _options.ReceiveTimeout,
                                memory =>
                                {
                                    NntpLogMessages.WireArticlePayloadComplete(_logger, _wireIdentity, memory.Length);
                                    if (memory.Length == 0 || !NntpProtocolIo.HasHeaderBodySeparator(memory.Span))
                                    {
                                        return default;
                                    }

                                    accepted = true;
                                    try
                                    {
                                        return consumePayload(memory);
                                    }
                                    catch (Exception ex) when (ex is not PayloadConsumerException)
                                    {
                                        consumerError = ExceptionDispatchInfo.Capture(ex);
                                        throw new PayloadConsumerException();
                                    }
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (EndOfStreamException)
                    {
                        return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, code, "NNTP article ended before terminator.");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return MarkUnhealthy(ArticleRetrievalKind.Cancelled, code, "ARTICLE receive was cancelled.");
                    }
                    catch (OperationCanceledException)
                    {
                        return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, code, "ARTICLE receive timed out.");
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("MaxArticleBytes", StringComparison.Ordinal))
                    {
                        return MarkUnhealthy(ArticleRetrievalKind.InvalidArticle, code, "NNTP article exceeded MaxArticleBytes.");
                    }

                    if (!accepted)
                    {
                        AssignState(NntpSessionState.Ready);
                        return WithCommandTimestamp(ArticleRetrievalResult.Failed(
                            ArticleRetrievalKind.InvalidArticle,
                            code,
                            "ARTICLE payload is missing a header/body separator.",
                            sessionReusable: true));
                    }

                    AssignState(NntpSessionState.Ready);
                    return WithCommandTimestamp(ArticleRetrievalResult.Retrieved(code, text));
                }

                if (code == NntpStatusCode.NoArticleWithMessageId)
                {
                    AssignState(NntpSessionState.Ready);
                    return WithCommandTimestamp(ArticleRetrievalResult.Failed(
                        ArticleRetrievalKind.ArticleNotFound,
                        code,
                        text,
                        sessionReusable: true));
                }

                if (NntpStatusCode.IsAuthenticationFailure(code))
                {
                    return MarkUnhealthy(ArticleRetrievalKind.AuthenticationFailure, code, text);
                }

                if (NntpStatusCode.IsCommandRejected(code)
                    || code is NntpStatusCode.NoNewsgroupSelected
                        or NntpStatusCode.CurrentArticleInvalid
                        or NntpStatusCode.NoArticleWithNumber)
                {
                    return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, code, text);
                }

                return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, code, text);
            }
            catch (PayloadConsumerException) when (consumerError is not null)
            {
                _ = consumerError.SourceException;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return MarkUnhealthy(ArticleRetrievalKind.Cancelled, null, "ARTICLE was cancelled.");
            }
            catch (OperationCanceledException)
            {
                return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, "ARTICLE timed out.");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or AuthenticationException or FormatException or InvalidDataException or TimeoutException)
            {
                return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, ex.GetType().Name);
            }
            finally
            {
                if (State == NntpSessionState.Busy)
                {
                    AssignState(previous);
                }

                ReleaseCommandGate();
            }

            if (consumerError is not null)
            {
                RememberCommandTimestamp(consumerError.SourceException);
                consumerError.Throw();
            }

            throw new InvalidOperationException("ARTICLE download ended without a result.");
        }

        /// <summary>
        /// Waits until the command that currently holds <see cref="_busy"/> releases it, then releases the gate again.
        /// </summary>
        /// <param name="cancellationToken">Cancels this wait. It does not cancel DATE or ARTICLE.</param>
        /// <returns>A task that completes when the gate was observed free and released.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled before the gate is acquired.</exception>
        /// <remarks>
        /// <see cref="SendDateKeepAliveAsync"/> and <see cref="DownloadArticleAsync"/> hold <see cref="_busy"/> until the response is consumed or the attempt fails, and they update <see cref="State"/> before releasing it.
        /// A caller that only observed <see cref="NntpSessionState.Busy"/> uses this wait instead of polling that state.
        /// The wait takes the gate and drops it immediately, so the caller does not own the command and a later ARTICLE can acquire the gate.
        /// Completing some other keepalive task does not release this gate. After the wait, the caller must read <see cref="IsReusable"/> again.
        /// <see cref="DisposeAsync"/> does not dispose the gate, so this release still belongs to the waiter that observed the current command finish.
        /// </remarks>
        internal async Task WaitForCurrentCommandAsync(CancellationToken cancellationToken)
        {
            await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
            ReleaseCommandGate();
        }

        /// <summary>
        /// Issues RFC 3977 DATE as an idle-session keepalive. Serialized with ARTICLE
        /// through <see cref="_busy"/>. When <paramref name="waitForIdle"/> is
        /// <see langword="false"/> and the session is already busy, DATE is skipped
        /// so article acquisition is not blocked.
        /// </summary>
        /// <param name="cancellationToken">
        /// When <paramref name="waitForIdle"/> is true, cancellation while waiting for <see cref="_busy"/> throws <see cref="OperationCanceledException"/>.
        /// Cancellation after <c>DATE</c> is written marks the session unhealthy and returns <see langword="false"/>.
        /// Cancellation before that write restores the previous <see cref="State"/> and returns <see langword="true"/>.
        /// </param>
        /// <param name="waitForIdle">
        /// <see langword="true"/> waits for <see cref="_busy"/>.
        /// <see langword="false"/> returns <see langword="true"/> immediately when the lock is already held, without writing <c>DATE</c>.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the server returns <see cref="NntpStatusCode.DateFollows"/>, when DATE is skipped because the session is busy, or when cancellation happens before DATE is written.
        /// <see langword="false"/> when the session is already unusable or this call marks it unhealthy.
        /// </returns>
        /// <remarks>
        /// Returns <see langword="false"/> immediately when the reader or stream is missing, the session is unhealthy, closed, retiring, or disposed.
        /// Only <see cref="NntpStatusCode.DateFollows"/> is success. Any other parsed status marks the session unhealthy.
        /// A command timeout is provider failure and marks the session unhealthy.
        /// </remarks>
        internal async Task<bool> SendDateKeepAliveAsync(
            CancellationToken cancellationToken,
            bool waitForIdle = false)
        {
            if (_reader is null || _stream is null || _unhealthy
                || State is NntpSessionState.Closed or NntpSessionState.Retiring
                || Volatile.Read(ref _disposed) == 1)
            {
                return false;
            }

            if (waitForIdle)
            {
                await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!await _busy.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            var previous = State;
            AssignState(NntpSessionState.Busy);
            var wrote = false;
            try
            {
                await WriteCommandAsync("DATE", NntpProtocolIo.DateCommand, cancellationToken)
                    .ConfigureAwait(false);
                wrote = true;
                var statusLine = await ReadStatusLineAsync(cancellationToken).ConfigureAwait(false);
                if (statusLine is null)
                {
                    _ = MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, "NNTP DATE status was empty.");
                    return false;
                }

                var status = ConsumedStatus.Parse(statusLine.ConsumedLine.Span, StatusTextNeed.UnlessDateFollows);
                if (status.IsMalformed)
                {
                    _ = MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, "NNTP DATE status was malformed.");
                    return false;
                }

                var code = status.Code;
                var text = status.Text;

                if (code != NntpStatusCode.DateFollows)
                {
                    _ = MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, code, text);
                    return false;
                }

                AssignState(NntpSessionState.Ready);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (wrote)
                {
                    _ = MarkUnhealthy(ArticleRetrievalKind.Cancelled, null, "DATE keepalive was cancelled.");
                    return false;
                }

                AssignState(previous);
                return true;
            }
            catch (OperationCanceledException)
            {
                _ = MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, "DATE keepalive timed out.");
                return false;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or AuthenticationException or FormatException or InvalidDataException or TimeoutException)
            {
                _ = MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, ex.GetType().Name);
                return false;
            }
            finally
            {
                if (State == NntpSessionState.Busy)
                {
                    AssignState(previous);
                }

                ReleaseCommandGate();
            }
        }

        /// <summary>
        /// Retires the session with the standalone <see cref="QuitAllowance"/>.
        /// This call does not share a pool or registry shutdown deadline.
        /// </summary>
        /// <returns>A task that completes after the transport close has been attempted.</returns>
        /// <remarks>
        /// Pool and registry shutdown call <see cref="DisposeForShutdownAsync"/> so every session in that operation sees the same deadline.
        /// The close protocol is that overload. This entry only supplies <see cref="NntpShutdownDeadline.None"/> and <see cref="CancellationToken.None"/>.
        /// </remarks>
        public ValueTask DisposeAsync() => DisposeForShutdownAsync(NntpShutdownDeadline.None, CancellationToken.None);

        /// <summary>
        /// Retires the session. An idle ready session sends <c>QUIT</c> before the transport is closed. An active command is aborted instead.
        /// A second call returns immediately.
        /// </summary>
        /// <param name="deadline">
        /// Shared shutdown instant. <see cref="NntpShutdownDeadline.None"/> keeps <see cref="QuitAllowance"/>.
        /// An expired deadline, or a deadline whose shared token is already cancelled, skips QUIT.
        /// A shorter remainder replaces the two-second allowance for this write only.
        /// When that remainder is the shared deadline, the write waits on the deadline token rather than a new timer.
        /// </param>
        /// <param name="cancellationToken">
        /// Caller cancellation for the QUIT write. It is not applied to an in-flight ARTICLE or DATE command.
        /// Those commands unwind because the transport is closed, and they release <see cref="_busy"/> themselves.
        /// </param>
        /// <remarks>
        /// <c>QUIT</c> is written only when <see cref="State"/> is <see cref="NntpSessionState.Ready"/>, the session is healthy, <see cref="_busy"/> is free,
        /// <paramref name="deadline"/> has not expired, its shared token is not cancelled, and <paramref name="cancellationToken"/> is not cancelled.
        /// That is the idle case: no ARTICLE or DATE call holds the command gate, and connect is not using the stream.
        /// The write budget is the lesser of <see cref="QuitAllowance"/> and the time left on <paramref name="deadline"/>. It is not followed by a response read.
        /// The gate stays held until that write finishes, so QUIT cannot overlap another command.
        /// Write and flush failures, including <see cref="SocketException"/> and <see cref="NotSupportedException"/>, are logged and do not escape.
        /// When the gate is already held, or the deadline or caller token has already expired, QUIT is skipped.
        /// Closing the captured transport unblocks that command's pending read or write. This method does not wait for the command and does not take the gate.
        /// The transport is still disposed once. A failure from that dispose is logged and does not skip <see cref="NntpSessionState.Closed"/>.
        /// <see cref="_busy"/> is not disposed. The command that holds it releases the gate after it unwinds, including when this method has already returned.
        /// A second call returns before touching the transport, so pool rollback can dispose a session that shutdown already disposed.
        /// The transport reference is captured under <see cref="_transportGate"/> before any await, and the fields are cleared there.
        /// A STARTTLS upgrade that already owns the plaintext transport disposes the TLS wrapper itself. This method does not dispose that transport again.
        /// The first call cancels the session handshake token, so a handshake still inside <see cref="NntpTlsClient.AuthenticateAsClientAsync"/> ends even when the caller's token is not cancelled.
        /// <see cref="AggregateException"/> or <see cref="ObjectDisposedException"/> from that cancellation is logged and does not skip transport disposal or the transition to <see cref="NntpSessionState.Closed"/>.
        /// An upgrade that publishes before this capture is the stream that is closed here, so the wrapper is not left on a closed session.
        /// After the transport dispose is attempted, <see cref="_stream"/> and <see cref="_reader"/> stay clear.
        /// <see cref="DetachReader"/> asks the reader to return its pooled payload scratch. An in-flight payload read
        /// still owns that buffer until the read returns; the scratch is not returned while the read is using it.
        /// Bytes already copied into a <see cref="RetrievedArticle"/> stay with that article.
        /// An in-flight read holds the reader on its own stack until it returns; clearing the fields does not replace that reader's buffer.
        /// <see cref="State"/> ends as <see cref="NntpSessionState.Closed"/>.
        /// Command paths assign <see cref="State"/> through <see cref="AssignState"/>, which returns once <see cref="_disposed"/> is set, so they do not replace that terminal value.
        /// This method does not hold <see cref="_transportGate"/> across an await.
        /// </remarks>
        internal async ValueTask DisposeForShutdownAsync(NntpShutdownDeadline deadline, CancellationToken cancellationToken)
        {
            Stream? stream;
            var idleQuit = false;
            lock (_transportGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                {
                    return;
                }

                idleQuit = State == NntpSessionState.Ready && !_unhealthy && _stream is not null;
                stream = _upgradeOwnsTransport == 1 ? null : _stream;
                _stream = null;
                DetachReader();
            }

            var holdsGate = false;
            try
            {
                try
                {
                    await _handshakeCancellation.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
                {
                    NntpLogMessages.WireHandshakeCancellationFailed(_logger, _wireIdentity, ex.GetType().Name);
                }

                State = NntpSessionState.Retiring;
                NntpLogMessages.WireRetiring(_logger, _wireIdentity);
                if (stream is null)
                {
                    return;
                }

                if (idleQuit && !deadline.IsExpired && !deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        holdsGate = await _busy.WaitAsync(TimeSpan.Zero).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        holdsGate = false;
                    }
                }

                if (holdsGate && !deadline.IsExpired && !deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    await WriteQuitAsync(stream, deadline, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException or NotSupportedException)
                {
                    NntpLogMessages.WireTransportDisposeFailed(_logger, _wireIdentity, ex.GetType().Name);
                }
            }
            finally
            {
                if (holdsGate)
                {
                    ReleaseCommandGate();
                }

                State = NntpSessionState.Closed;
            }
        }

        /// <summary>Writes one best-effort <c>QUIT</c> on <paramref name="stream"/> while the caller holds <see cref="_busy"/>.</summary>
        /// <param name="stream">Transport captured before the session fields were cleared. Not the field, which is already null.</param>
        /// <param name="deadline">Shared shutdown instant. <see cref="NntpShutdownDeadline.Cap"/> limits the write to the time still left.</param>
        /// <param name="cancellationToken">Caller cancellation linked into the write. It does not cancel an ARTICLE or DATE token.</param>
        /// <remarks>
        /// The budget is the lesser of <see cref="QuitAllowance"/> and the time left on <paramref name="deadline"/>.
        /// Zero remaining, a cancelled deadline token, or an already cancelled <paramref name="cancellationToken"/> returns without writing.
        /// When the shared deadline is shorter than <see cref="QuitAllowance"/>, the write waits on that deadline's token.
        /// A new timer is used only for the two-second allowance, and only when the deadline still has more time than that.
        /// A failure is logged and does not escape, so the caller still closes <paramref name="stream"/>.
        /// </remarks>
        private async Task WriteQuitAsync(Stream stream, NntpShutdownDeadline deadline, CancellationToken cancellationToken)
        {
            var budget = deadline.Cap(QuitAllowance);
            if (budget <= TimeSpan.Zero || deadline.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                NntpLogMessages.WireTx(_logger, _wireIdentity, "QUIT");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Cancellation);
                if (!deadline.IsBounded || deadline.Remaining > QuitAllowance)
                {
                    cts.CancelAfter(QuitAllowance);
                }

                await stream.WriteAsync(QuitCommand, cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException or NotSupportedException)
            {
                NntpLogMessages.WireQuitFailed(_logger, _wireIdentity, ex.GetType().Name);
            }
        }

        /// <summary>Releases <see cref="_busy"/> after the command that acquired it has finished with the transport.</summary>
        /// <remarks>
        /// Called from the command <c>finally</c> and from a graceful dispose that acquired the gate to send <c>QUIT</c>.
        /// <see cref="ObjectDisposedException"/> is ignored so a classified command result is not replaced when the gate is already gone.
        /// This method does not dispose the gate. A later release from a command that was already inside it must still succeed.
        /// </remarks>
        private void ReleaseCommandGate()
        {
            try
            {
                _busy.Release();
            }
            catch (ObjectDisposedException)
            {
                // A disposed gate must not replace the command result already classified in the try block.
            }
        }

        /// <summary>
        /// Sends <c>CAPABILITIES</c> and, when <c>STARTTLS</c> is advertised and implicit TLS is off, upgrades the existing transport.
        /// </summary>
        /// <param name="cancellationToken">Cancels the exchange. <see cref="ConnectAsync"/> classifies a propagated cancellation.</param>
        /// <returns>
        /// <see langword="null"/> when the list completed and no upgrade was required, or when the upgrade succeeded.
        /// Otherwise a non-reusable failure.
        /// </returns>
        /// <remarks>
        /// Requires status <see cref="NntpStatusCode.CapabilityListFollows"/> and a dot-terminated body of at most <see cref="NntpProtocolIo.MaxCapabilityLines"/> lines.
        /// Only the <c>STARTTLS</c> label is recorded. An empty, malformed, non-101, unterminated, or over-long list fails closed.
        /// </remarks>
        private async Task<ArticleRetrievalResult?> NegotiateCapabilitiesAndStartTlsAsync(
            CancellationToken cancellationToken)
        {
            await WriteCommandAsync("CAPABILITIES", NntpProtocolIo.CapabilitiesCommand, cancellationToken)
                .ConfigureAwait(false);
            var statusLine = await ReadStatusLineAsync(cancellationToken).ConfigureAwait(false);
            if (statusLine is null)
            {
                return FailClosed(
                    ArticleRetrievalKind.ProviderFailure,
                    null,
                    "NNTP CAPABILITIES status was empty or malformed.",
                    reusable: false);
            }

            var status = ConsumedStatus.Parse(statusLine.ConsumedLine.Span, StatusTextNeed.UnlessCapabilities);
            if (status.IsMalformed)
            {
                return FailClosed(
                    ArticleRetrievalKind.ProviderFailure,
                    null,
                    "NNTP CAPABILITIES status was empty or malformed.",
                    reusable: false);
            }

            var code = status.Code;
            if (code != NntpStatusCode.CapabilityListFollows)
            {
                return FailClosed(ArticleRetrievalKind.ProviderFailure, code, status.Text, reusable: false);
            }

            var startTlsAdvertised = false;
            for (var i = 0; i < NntpProtocolIo.MaxCapabilityLines; i++)
            {
                var lineReader = await ReadStatusLineAsync(cancellationToken).ConfigureAwait(false);
                if (lineReader is null)
                {
                    return FailClosed(
                        ArticleRetrievalKind.ProviderFailure,
                        code,
                        "NNTP CAPABILITIES list ended before the terminator.",
                        reusable: false);
                }

                var terminator = NntpProtocolIo.IsMultilineTerminator(lineReader.ConsumedLine.Span);
                var advertised = !terminator
                    && NntpProtocolIo.CapabilityLabelEquals(
                        NntpProtocolIo.DestuffDotLine(lineReader.ConsumedLine.Span),
                        NntpProtocolIo.StartTlsCapability);
                if (terminator)
                {
                    return startTlsAdvertised && !_provider.UseTls
                        ? await IssueStartTlsAndUpgradeAsync(cancellationToken).ConfigureAwait(false)
                        : null;
                }

                if (advertised)
                {
                    startTlsAdvertised = true;
                }
            }

            return FailClosed(
                ArticleRetrievalKind.ProviderFailure,
                code,
                "NNTP CAPABILITIES list exceeded the maximum number of lines.",
                reusable: false);
        }

        /// <summary>Sends <c>STARTTLS</c> and, on status 382 with no unread bytes, replaces the transport with TLS.</summary>
        /// <param name="cancellationToken">Cancels the command and the handshake.</param>
        /// <returns>
        /// <see langword="null"/> when the handshake completed. Otherwise a non-reusable failure.
        /// This method does not read a greeting after the handshake.
        /// </returns>
        /// <remarks>
        /// <see cref="NntpStreamReader.BufferedByteCount"/> other than zero fails the session so negotiation does not start on a desynchronized stream.
        /// </remarks>
        private async Task<ArticleRetrievalResult?> IssueStartTlsAndUpgradeAsync(CancellationToken cancellationToken)
        {
            await WriteCommandAsync("STARTTLS", NntpProtocolIo.StartTlsCommand, cancellationToken)
                .ConfigureAwait(false);
            var statusLine = await ReadStatusLineAsync(cancellationToken).ConfigureAwait(false);
            if (statusLine is null)
            {
                return FailClosed(
                    ArticleRetrievalKind.ProviderFailure,
                    null,
                    "NNTP STARTTLS status was empty or malformed.",
                    reusable: false);
            }

            var status = ConsumedStatus.Parse(statusLine.ConsumedLine.Span, StatusTextNeed.UnlessStartTls);
            if (status.IsMalformed)
            {
                return FailClosed(
                    ArticleRetrievalKind.ProviderFailure,
                    null,
                    "NNTP STARTTLS status was empty or malformed.",
                    reusable: false);
            }

            if (status.Code != NntpStatusCode.ContinueWithTlsNegotiation)
            {
                return FailClosed(ArticleRetrievalKind.ProviderFailure, status.Code, status.Text, reusable: false);
            }

            if (_reader is { } buffered && buffered.BufferedByteCount != 0)
            {
                return FailClosed(
                    ArticleRetrievalKind.ProviderFailure,
                    status.Code,
                    "NNTP STARTTLS left unread bytes before TLS negotiation.",
                    reusable: false);
            }

            return await UpgradeExistingTransportToTlsAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Wraps the captured transport with TLS and installs a new reader over the encrypted stream.</summary>
        /// <param name="cancellationToken">
        /// Cancels the handshake together with <see cref="NntpSessionOptions.ConnectTimeout"/> and the token cancelled by <see cref="DisposeAsync"/>.
        /// </param>
        /// <returns><see langword="null"/> when the handshake completed. A handshake exception other than cancellation returns a non-reusable provider failure.</returns>
        /// <remarks>
        /// The plaintext transport is captured under <see cref="_transportGate"/> and then owned by this method until publication or failure.
        /// The handshake uses that local, not a later read of <see cref="_stream"/>. <see cref="_transportGate"/> is not held across the handshake or either test gate.
        /// <see cref="BeforeStartTlsHandshake"/> runs after that capture. <see cref="BeforePublishUpgradedTransport"/> runs after the wrapper exists and before publication.
        /// Both gates are null in production.
        /// Publication stores the wrapper only when <see cref="DisposeAsync"/> has not won. A lost publication disposes the wrapper, which closes the plaintext transport, and does not store it.
        /// There is no post-handshake greeting read. On handshake failure the fields are cleared unless disposal already cleared them.
        /// <see cref="NntpTlsClient.AuthenticateAsClientAsync"/> closes the captured transport when the handshake throws.
        /// An exception from the pre-handshake gate or from <see cref="NntpLogMessages.WireTlsHandshakeStarting"/> clears upgrade ownership and disposes the plaintext transport once, then propagates.
        /// A transport exception from that dispose is logged and does not replace the initiating exception.
        /// Cancellation is rethrown after the fields are cleared so <see cref="ConnectAsync"/> can classify it.
        /// </remarks>
        private async Task<ArticleRetrievalResult?> UpgradeExistingTransportToTlsAsync(
            CancellationToken cancellationToken)
        {
            Stream? stream = null;
            lock (_transportGate)
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    return ArticleRetrievalResult.Failed(
                        ArticleRetrievalKind.ProviderFailure,
                        null,
                        "NNTP stream is not open for STARTTLS.",
                        sessionReusable: false);
                }

                if (_stream is not null)
                {
                    stream = _stream;
                    _upgradeOwnsTransport = 1;
                }
            }

            if (stream is null)
            {
                return FailClosed(
                    ArticleRetrievalKind.ProviderFailure,
                    null,
                    "NNTP stream is not open for STARTTLS.",
                    reusable: false);
            }

            try
            {
                if (_beforeStartTlsHandshake is { } beforeHandshake)
                {
                    await beforeHandshake().ConfigureAwait(false);
                }

                NntpLogMessages.WireTlsHandshakeStarting(_logger, _wireIdentity);
            }
            catch (Exception)
            {
                try
                {
                    await AbandonUpgradeTransportAsync(stream).ConfigureAwait(false);
                }
                catch (Exception cleanup) when (cleanup is IOException or ObjectDisposedException or InvalidOperationException or SocketException)
                {
                    NntpLogMessages.WireTlsCleanupFailed(_logger, _wireIdentity, cleanup.GetType().Name);
                }

                throw;
            }

            SslStream ssl;
            try
            {
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _handshakeCancellation.Token);
                ssl = await NntpTlsClient.AuthenticateAsClientAsync(
                        stream,
                        _provider.Host,
                        _options.ConnectTimeout,
                        _options.ServerCertificateValidationCallback,
                        handshakeCts.Token,
                        _tlsStreamFactory)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0 && !cancellationToken.IsCancellationRequested)
            {
                ReleaseUpgradeOwnership();
                _unhealthy = true;
                return ArticleRetrievalResult.Failed(
                    ArticleRetrievalKind.ProviderFailure,
                    null,
                    "NNTP session was disposed during STARTTLS.",
                    sessionReusable: false);
            }
            catch (OperationCanceledException)
            {
                ReleaseUpgradeOwnership();
                throw;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or AuthenticationException or TimeoutException)
            {
                ReleaseUpgradeOwnership();
                if (Volatile.Read(ref _disposed) == 1)
                {
                    _unhealthy = true;
                    return ArticleRetrievalResult.Failed(
                        ArticleRetrievalKind.ProviderFailure,
                        null,
                        $"TLS handshake failed ({ex.GetType().Name}).",
                        sessionReusable: false);
                }

                return FailClosed(
                    ArticleRetrievalKind.ProviderFailure,
                    null,
                    $"TLS handshake failed ({ex.GetType().Name}).",
                    reusable: false);
            }

            var published = false;
            try
            {
                if (_beforePublishUpgradedTransport is { } beforePublish)
                {
                    await beforePublish(ssl).ConfigureAwait(false);
                }

                published = TryPublishUpgradedTransport(ssl);
                if (!published)
                {
                    _unhealthy = true;
                    return ArticleRetrievalResult.Failed(
                        ArticleRetrievalKind.ProviderFailure,
                        null,
                        "NNTP session was disposed during STARTTLS.",
                        sessionReusable: false);
                }

                NntpLogMessages.WireTlsHandshakeCompleted(_logger, _wireIdentity);
                return null;
            }
            finally
            {
                if (!published)
                {
                    ReleaseUpgradeOwnership();
                    await ssl.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>Stores <paramref name="reader"/> and returns the previous reader's payload scratch.</summary>
        /// <param name="reader">Reader bound to the transport just published.</param>
        /// <remarks>
        /// Called under <see cref="_transportGate"/>. <see cref="NntpStreamReader.Dispose"/> does not wait.
        /// A payload read still using the previous reader returns that scratch when the read finishes.
        /// </remarks>
        private void PublishReader(NntpStreamReader reader)
        {
            var previous = _reader;
            _reader = reader;
            previous?.Dispose();
        }

        /// <summary>Clears <see cref="_reader"/> and returns its payload scratch when no payload read is using it.</summary>
        /// <remarks>Called under <see cref="_transportGate"/>. Does not dispose the transport.</remarks>
        private void DetachReader()
        {
            var previous = _reader;
            _reader = null;
            previous?.Dispose();
        }

        /// <summary>Stores <paramref name="connected"/> as the session transport when disposal has not won.</summary>
        /// <param name="connected">Stream returned by the transport factory. The caller disposes it when this returns <see langword="false"/>.</param>
        /// <returns><see langword="true"/> when <paramref name="connected"/> is now <see cref="_stream"/> and <see cref="State"/> is <see cref="NntpSessionState.Connected"/>.</returns>
        /// <remarks>
        /// Does not await. A disposal that already set <see cref="_disposed"/> wins and this method does not publish.
        /// <see cref="State"/> is set in the same lock so a later dispose cannot observe a published transport whose state was still <see cref="NntpSessionState.Connecting"/>, and this method cannot overwrite <see cref="NntpSessionState.Closed"/>.
        /// </remarks>
        private bool TryPublishConnectedTransport(Stream connected)
        {
            lock (_transportGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return false;
                }

                _stream = connected;
                PublishReader(new NntpStreamReader(connected, _options.ReceiveBufferBytes));
                State = NntpSessionState.Connected;
                return true;
            }
        }

        /// <summary>
        /// Clears STARTTLS ownership of <paramref name="stream"/> and closes that plaintext transport once.
        /// Called only when the handshake has not created a wrapper.
        /// </summary>
        /// <param name="stream">Plaintext transport captured before the handshake.</param>
        /// <remarks>
        /// <see cref="_upgradeOwnsTransport"/> and the matching session fields are updated under <see cref="_transportGate"/> before the transport is closed.
        /// <see cref="DisposeAsync"/> skips the transport while the upgrade flag is set, so this method is the one that closes <paramref name="stream"/>.
        /// The field is cleared in the same lock that clears the flag. A concurrent dispose therefore observes either the flag or a null field and does not close the stream again.
        /// A disposal exception propagates after those fields are clear. The caller keeps the initiating pre-handshake exception when the disposal failure is a transport exception.
        /// </remarks>
        private async ValueTask AbandonUpgradeTransportAsync(Stream stream)
        {
            lock (_transportGate)
            {
                _upgradeOwnsTransport = 0;
                if (ReferenceEquals(_stream, stream))
                {
                    _stream = null;
                    DetachReader();
                }
            }

            await stream.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>Stores <paramref name="ssl"/> as the session transport when disposal has not won.</summary>
        /// <param name="ssl">Authenticated wrapper. The caller disposes it when this returns <see langword="false"/>.</param>
        /// <returns><see langword="true"/> when <paramref name="ssl"/> is now <see cref="_stream"/>.</returns>
        /// <remarks>Clears <see cref="_upgradeOwnsTransport"/>. Does not await. Does not dispose <paramref name="ssl"/>.</remarks>
        private bool TryPublishUpgradedTransport(SslStream ssl)
        {
            lock (_transportGate)
            {
                _upgradeOwnsTransport = 0;
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return false;
                }

                _stream = ssl;
                PublishReader(new NntpStreamReader(ssl, _options.ReceiveBufferBytes));
                return true;
            }
        }

        /// <summary>
        /// Drops upgrade ownership. When the session is still live, also drops the plaintext fields the failed handshake already closed.
        /// </summary>
        /// <remarks>Does not dispose the transport. The handshake failure path has already closed it, and a lost publication disposes the wrapper separately.</remarks>
        private void ReleaseUpgradeOwnership()
        {
            lock (_transportGate)
            {
                _upgradeOwnsTransport = 0;
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _stream = null;
                    DetachReader();
                }
            }
        }

        /// <summary>
        /// Sends <c>AUTHINFO USER</c> and, when the server replies 381, <c>AUTHINFO PASS</c>.
        /// </summary>
        /// <param name="cancellationToken">Cancels the exchange. <see cref="ConnectAsync"/> classifies a propagated cancellation.</param>
        /// <returns>
        /// <see langword="null"/> when authentication is not required or the server accepts it with <see cref="NntpStatusCode.AuthenticationAccepted"/>.
        /// Otherwise a non-reusable failure.
        /// </returns>
        /// <remarks>
        /// <see cref="BackFillerProviderDefinition.RequiresAuthentication"/> is true when either credential is non-whitespace, but both must be present and ASCII or this returns <see cref="ArticleRetrievalKind.AuthenticationFailure"/> without writing a command.
        /// Wire logs use <c>AUTHINFO USER ***</c> and <c>AUTHINFO PASS ***</c>. A 281 reply to USER does not send PASS.
        /// </remarks>
        private async Task<ArticleRetrievalResult?> AuthenticateIfConfiguredAsync(CancellationToken cancellationToken)
        {
            if (!_provider.RequiresAuthentication)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(_provider.Username) || string.IsNullOrWhiteSpace(_provider.Password))
            {
                return FailClosed(
                    ArticleRetrievalKind.AuthenticationFailure,
                    null,
                    "Both Username and Password must be configured together.",
                    reusable: false);
            }

            if (!NntpProtocolIo.TryEncodeAscii(_provider.Username, out var userBytes)
                || !NntpProtocolIo.TryEncodeAscii(_provider.Password, out var passBytes))
            {
                return FailClosed(
                    ArticleRetrievalKind.AuthenticationFailure,
                    null,
                    "Provider credentials contain non-ASCII bytes.",
                    reusable: false);
            }

            AssignState(NntpSessionState.Authenticating);
            await WritePrefixedAsync(
                    NntpProtocolIo.AuthInfoUserPrefix,
                    userBytes,
                    "AUTHINFO USER ***",
                    cancellationToken)
                .ConfigureAwait(false);
            var userLine = await ReadStatusLineAsync(cancellationToken).ConfigureAwait(false);
            if (userLine is null)
            {
                return FailClosed(ArticleRetrievalKind.ProviderFailure, null, "Malformed AUTHINFO USER status line.", reusable: false);
            }

            var userStatus = ConsumedStatus.Parse(userLine.ConsumedLine.Span, StatusTextNeed.UnlessAuthAcceptedOrPassword);
            if (userStatus.IsMalformed)
            {
                return FailClosed(ArticleRetrievalKind.ProviderFailure, null, "Malformed AUTHINFO USER status line.", reusable: false);
            }

            var userCode = userStatus.Code;
            var userText = userStatus.Text;

            if (userCode == NntpStatusCode.AuthenticationAccepted)
            {
                return null;
            }

            if (userCode != NntpStatusCode.PasswordRequired)
            {
                return ClassifyAuthFailure(userCode, userText);
            }

            await WritePrefixedAsync(
                    NntpProtocolIo.AuthInfoPassPrefix,
                    passBytes,
                    "AUTHINFO PASS ***",
                    cancellationToken)
                .ConfigureAwait(false);
            var passLine = await ReadStatusLineAsync(cancellationToken).ConfigureAwait(false);
            if (passLine is null)
            {
                return FailClosed(ArticleRetrievalKind.ProviderFailure, null, "Malformed AUTHINFO PASS status line.", reusable: false);
            }

            var passStatus = ConsumedStatus.Parse(passLine.ConsumedLine.Span, StatusTextNeed.UnlessAuthAccepted);
            if (passStatus.IsMalformed)
            {
                return FailClosed(ArticleRetrievalKind.ProviderFailure, null, "Malformed AUTHINFO PASS status line.", reusable: false);
            }

            var passCode = passStatus.Code;
            var passText = passStatus.Text;

            return passCode == NntpStatusCode.AuthenticationAccepted ? null : ClassifyAuthFailure(passCode, passText);
        }

        /// <summary>Maps an AUTHINFO rejection to authentication failure or a generic provider failure.</summary>
        /// <param name="code">Status code from USER or PASS.</param>
        /// <param name="text">Status text, stored on the result as the reason.</param>
        /// <returns>
        /// <see cref="ArticleRetrievalKind.AuthenticationFailure"/> for <see cref="NntpStatusCode.IsAuthenticationFailure"/> and <see cref="NntpStatusCode.IsCommandRejected"/>.
        /// Every other code is <see cref="ArticleRetrievalKind.ProviderFailure"/>. The session is not reusable.
        /// </returns>
        private ArticleRetrievalResult ClassifyAuthFailure(int code, string text)
        {
            if (NntpStatusCode.IsAuthenticationFailure(code) || NntpStatusCode.IsCommandRejected(code))
            {
                return FailClosed(ArticleRetrievalKind.AuthenticationFailure, code, text, reusable: false);
            }

            return FailClosed(ArticleRetrievalKind.ProviderFailure, code, text, reusable: false);
        }

        /// <summary>
        /// Reads one status line with <see cref="NntpSessionOptions.MaxStatusLineBytes"/> and <see cref="NntpSessionOptions.CommandTimeout"/>, then logs the ASCII text at Debug.
        /// </summary>
        /// <param name="cancellationToken">Caller cancellation. Distinguished from the command timeout.</param>
        /// <returns>
        /// The reader whose <see cref="NntpStreamReader.ConsumedLine"/> holds the line, or <see langword="null"/> when
        /// <see cref="_reader"/> is missing or the peer closes before any byte.
        /// </returns>
        /// <exception cref="TimeoutException">The command timeout elapses while <paramref name="cancellationToken"/> is not cancelled.</exception>
        /// <remarks>
        /// <see cref="NntpStreamReader.ConsumedLine"/> is valid only until the next line read on the returned reader.
        /// Callers parse or classify it before awaiting another read. Debug logging copies the line before this method returns.
        /// That copy is skipped when Debug is disabled. Caller cancellation propagates as <see cref="OperationCanceledException"/>.
        /// <see cref="EndOfStreamException"/> and an over-long line propagate to the caller.
        /// The line read has its own <see cref="NntpSessionOptions.CommandTimeout"/>, starting when the reader arms it.
        /// It does not continue the write timeout from <see cref="WriteAsync"/>.
        /// </remarks>
        private async Task<NntpStreamReader?> ReadStatusLineAsync(CancellationToken cancellationToken)
        {
            var reader = _reader;
            if (reader is null)
            {
                return null;
            }

            try
            {
                var length = await reader
                    .ReadLineAsync(_options.MaxStatusLineBytes, _options.CommandTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (length < 0)
                {
                    return null;
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    NntpLogMessages.WireRx(_logger, _wireIdentity, Encoding.ASCII.GetString(reader.ConsumedLine.Span));
                }

                return reader;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("NNTP status read timed out.");
            }
        }

        /// <summary>Writes <paramref name="prefix"/>, <paramref name="argument"/>, and CRLF, logging <paramref name="wireCommand"/> instead of the argument.</summary>
        /// <param name="prefix">Command prefix, such as <see cref="NntpProtocolIo.AuthInfoUserPrefix"/>.</param>
        /// <param name="argument">ASCII field. Not written to the log.</param>
        /// <param name="wireCommand">Debug TX text. Callers pass a redacted command for credentials.</param>
        /// <param name="cancellationToken">Forwarded to <see cref="WriteCommandAsync"/>.</param>
        private async Task WritePrefixedAsync(
            byte[] prefix,
            byte[] argument,
            string wireCommand,
            CancellationToken cancellationToken)
        {
            var length = prefix.Length + argument.Length + NntpProtocolIo.Crlf.Length;
            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                prefix.CopyTo(rented.AsSpan());
                argument.CopyTo(rented.AsSpan(prefix.Length));
                NntpProtocolIo.Crlf.CopyTo(rented.AsSpan(prefix.Length + argument.Length));
                await WriteCommandAsync(wireCommand, rented.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// Debug text for one ARTICLE command. The message-id concatenation runs only when Debug is enabled.
        /// </summary>
        /// <param name="logger">Session logger. <see cref="ILogger.IsEnabled"/> for <see cref="LogLevel.Debug"/> selects the text.</param>
        /// <param name="messageId">ARTICLE argument already accepted for the wire. Not revalidated.</param>
        /// <returns>
        /// <c>ARTICLE</c> when Debug is disabled. Otherwise <c>ARTICLE </c> followed by <paramref name="messageId"/>.
        /// The returned text is not written to the socket.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
        internal static string FormatArticleDebugCommand(ILogger logger, string messageId)
        {
            ArgumentNullException.ThrowIfNull(logger);
            return logger.IsEnabled(LogLevel.Debug)
                ? "ARTICLE " + messageId
                : "ARTICLE";
        }

        /// <summary>Logs <paramref name="wireCommand"/> at Debug, then writes <paramref name="bytes"/> under <see cref="NntpSessionOptions.CommandTimeout"/>.</summary>
        /// <param name="wireCommand">Text passed to <see cref="NntpLogMessages.WireTx"/>.</param>
        /// <param name="bytes">Exact command bytes, including CRLF.</param>
        /// <param name="cancellationToken">Linked with the command timeout.</param>
        /// <returns>The write started by <see cref="WriteAsync"/>.</returns>
        private Task WriteCommandAsync(
            string wireCommand,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            NntpLogMessages.WireTx(_logger, _wireIdentity, wireCommand);
            return WriteAsync(bytes, _options.CommandTimeout, cancellationToken);
        }

        /// <summary>Writes and flushes <paramref name="bytes"/>, cancelling when <paramref name="timeout"/> elapses or <paramref name="cancellationToken"/> is cancelled.</summary>
        /// <param name="bytes">Bytes to write.</param>
        /// <param name="timeout">Linked budget. Not converted into <see cref="TimeoutException"/> here.</param>
        /// <param name="cancellationToken">Caller cancellation, linked with <paramref name="timeout"/>.</param>
        /// <exception cref="InvalidOperationException"><see cref="_stream"/> is null.</exception>
        /// <remarks>
        /// Both the timeout and caller cancellation surface as <see cref="OperationCanceledException"/>.
        /// The linked source covers the write and the flush together and is disposed after both complete.
        /// A following status read creates its own source and its own <see cref="NntpSessionOptions.CommandTimeout"/>.
        /// Reusing this source for that read would let a late timer from the write cancel the read, or would turn two budgets into one.
        /// </remarks>
        private async Task WriteAsync(ReadOnlyMemory<byte> bytes, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var stream = _stream;
            if (stream is null)
            {
                throw new InvalidOperationException("NNTP stream is not open.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await stream.WriteAsync(bytes, timeoutCts.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
        }

        /// <summary>Marks the session unhealthy and retiring, and returns a non-reusable failure.</summary>
        /// <param name="kind">Classification stored on the result.</param>
        /// <param name="code">Status code when one was parsed; otherwise null.</param>
        /// <param name="reason">Diagnostic text stored on the result.</param>
        /// <returns>A failed result with <see cref="ArticleRetrievalResult.SessionReusable"/> false.</returns>
        /// <remarks>
        /// Does not assign <see cref="State"/> after <see cref="DisposeAsync"/> has set <see cref="_disposed"/>.
        /// </remarks>
        private ArticleRetrievalResult MarkUnhealthy(ArticleRetrievalKind kind, int? code, string reason)
        {
            _unhealthy = true;
            AssignState(NntpSessionState.Retiring);
            return WithCommandTimestamp(ArticleRetrievalResult.Failed(kind, code, reason, sessionReusable: false));
        }

        /// <summary>
        /// Copies <see cref="ArticleCommandStartedTimestamp"/> onto <paramref name="result"/>.
        /// Zero means ARTICLE was not sent.
        /// </summary>
        /// <param name="result">Retrieval result about to be returned.</param>
        /// <returns>The same result, with the command timestamp stored.</returns>
        private ArticleRetrievalResult WithCommandTimestamp(ArticleRetrievalResult result)
        {
            result.CommandStartedTimestamp = ArticleCommandStartedTimestamp;
            return result;
        }

        /// <summary>
        /// Stores <see cref="ArticleCommandStartedTimestamp"/> on <paramref name="exception"/> when ARTICLE was sent.
        /// </summary>
        /// <param name="exception">Exception that leaves <see cref="DownloadArticleAsync"/> after the command timestamp is known.</param>
        private void RememberCommandTimestamp(Exception exception)
        {
            if (ArticleCommandStartedTimestamp != 0)
            {
                exception.Data[ArticleRetrievalResult.CommandStartedTimestampKey] = ArticleCommandStartedTimestamp;
            }
        }

        /// <summary>
        /// Assigns <paramref name="next"/> when <see cref="DisposeAsync"/> has not won.
        /// </summary>
        /// <param name="next">Lifecycle state for a session that is still open.</param>
        /// <remarks>
        /// Uses <see cref="_transportGate"/>, the same lock that sets <see cref="_disposed"/>.
        /// A caller that acquires the lock after disposal returns without changing <see cref="State"/>.
        /// A caller that acquires it first finishes this assignment before <see cref="DisposeAsync"/> reaches <see cref="NntpSessionState.Closed"/>.
        /// Does not await. <see cref="DisposeAsync"/>'s own retiring and closed assignments do not call this method.
        /// Callers must not already hold <see cref="_transportGate"/>; <see cref="System.Threading.Lock"/> is not recursive.
        /// <see cref="FailClosed"/> calls this method, so it also must not run under that lock.
        /// </remarks>
        private void AssignState(NntpSessionState next)
        {
            lock (_transportGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                State = next;
            }
        }

        /// <summary>Records whether <paramref name="reusable"/> may return to the pool and returns the matching failure.</summary>
        /// <param name="kind">Classification stored on the result.</param>
        /// <param name="code">Status code when one was parsed; otherwise null.</param>
        /// <param name="reason">Diagnostic text stored on the result.</param>
        /// <param name="reusable">
        /// <see langword="false"/> sets <see cref="_unhealthy"/> and <see cref="NntpSessionState.Retiring"/>.
        /// <see langword="true"/> leaves the session <see cref="NntpSessionState.Ready"/>.
        /// </param>
        /// <returns>A failed result whose reusable flag is <paramref name="reusable"/>.</returns>
        /// <remarks>
        /// When <see cref="DisposeAsync"/> has already set <see cref="_disposed"/>, this method does not assign <see cref="State"/>.
        /// The result is still the caller's failure, and the session stays <see cref="NntpSessionState.Closed"/>.
        /// </remarks>
        private ArticleRetrievalResult FailClosed(ArticleRetrievalKind kind, int? code, string reason, bool reusable)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _unhealthy = true;
                return ArticleRetrievalResult.Failed(kind, code, reason, sessionReusable: false);
            }

            _unhealthy = !reusable;
            AssignState(reusable ? NntpSessionState.Ready : NntpSessionState.Retiring);
            return ArticleRetrievalResult.Failed(kind, code, reason, reusable);
        }

        /// <summary>
        /// Builds <c>{Backbone}/{account}[{connectionNumber:000}/{MaxSessions}]</c>.
        /// The account is the trimmed username, or <c>-</c> when the username is missing.
        /// </summary>
        /// <param name="provider">Provider whose backbone, username, and max sessions are formatted.</param>
        /// <param name="connectionNumber">One-based slot written as three digits.</param>
        /// <returns>The wire-log prefix stored for the session lifetime.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
        private static string FormatWireIdentity(BackFillerProviderDefinition provider, int connectionNumber)
        {
            ArgumentNullException.ThrowIfNull(provider);
            var account = string.IsNullOrWhiteSpace(provider.Username) ? "-" : provider.Username.Trim();
            return $"{provider.Backbone}/{account}[{connectionNumber:000}/{provider.MaxSessions}]";
        }

        /// <summary>
        /// Signals that the article callback failed after the payload was complete.
        /// Caught so the session is not marked unhealthy. The original exception is rethrown after <c>_busy</c> is released.
        /// </summary>
        private sealed class PayloadConsumerException : Exception
        {
        }

        /// <summary>Chooses which well-formed status codes need <see cref="NntpProtocolIo.DecodeStatusText"/>.</summary>
        /// <remarks>Success codes used only for control flow stay undecoded. Unexpected codes are decoded for the result reason.</remarks>
        private enum StatusTextNeed
        {
            /// <summary>Decode text unless the code is <see cref="NntpStatusCode.ArticleFollows"/>.</summary>
            UnlessArticleFollows,

            /// <summary>Decode text unless the code is <see cref="NntpStatusCode.DateFollows"/>.</summary>
            UnlessDateFollows,

            /// <summary>Decode text unless the code is a service-ready greeting.</summary>
            UnlessGreeting,

            /// <summary>Decode text unless the code is <see cref="NntpStatusCode.CapabilityListFollows"/>.</summary>
            UnlessCapabilities,

            /// <summary>Decode text unless the code is <see cref="NntpStatusCode.ContinueWithTlsNegotiation"/>.</summary>
            UnlessStartTls,

            /// <summary>Decode text unless the code is 281 or 381.</summary>
            UnlessAuthAcceptedOrPassword,

            /// <summary>Decode text unless the code is <see cref="NntpStatusCode.AuthenticationAccepted"/>.</summary>
            UnlessAuthAccepted,
        }

        /// <summary>Status code and, when needed, the decoded text from one consumed line.</summary>
        /// <remarks>Text is <see cref="string.Empty"/> when the line is malformed or the code does not need a diagnostic string.</remarks>
        private readonly struct ConsumedStatus
        {
            /// <summary>Gets whether the line did not begin with three ASCII digits.</summary>
            public bool IsMalformed { get; private init; }

            /// <summary>Gets the parsed code. Zero when <see cref="IsMalformed"/> is true.</summary>
            public int Code { get; private init; }

            /// <summary>Gets the diagnostic text. Empty when the caller did not need it.</summary>
            public string Text { get; private init; }

            /// <summary>Parses <paramref name="line"/> and decodes text only when <paramref name="need"/> says this code needs it.</summary>
            /// <param name="line">Line bytes that remain valid for this call.</param>
            /// <param name="need">Which codes store diagnostic text.</param>
            /// <returns>A malformed result, or a code with text allocated only when required.</returns>
            public static ConsumedStatus Parse(ReadOnlySpan<byte> line, StatusTextNeed need)
            {
                if (!NntpProtocolIo.TryParseStatus(line, out var code, out var text))
                {
                    return new ConsumedStatus { IsMalformed = true, Text = string.Empty };
                }

                var decoded = NeedsText(need, code) ? NntpProtocolIo.DecodeStatusText(text) : string.Empty;
                return new ConsumedStatus { Code = code, Text = decoded };
            }

            /// <summary>Returns whether <paramref name="code"/> should be turned into a string for <paramref name="need"/>.</summary>
            /// <param name="need">Caller policy.</param>
            /// <param name="code">Parsed status code.</param>
            /// <returns><see langword="true"/> when the result reason must carry the status text.</returns>
            private static bool NeedsText(StatusTextNeed need, int code) => need switch
            {
                StatusTextNeed.UnlessArticleFollows => code != NntpStatusCode.ArticleFollows,
                StatusTextNeed.UnlessDateFollows => code != NntpStatusCode.DateFollows,
                StatusTextNeed.UnlessGreeting => !NntpStatusCode.IsServiceReadyGreeting(code),
                StatusTextNeed.UnlessCapabilities => code != NntpStatusCode.CapabilityListFollows,
                StatusTextNeed.UnlessStartTls => code != NntpStatusCode.ContinueWithTlsNegotiation,
                StatusTextNeed.UnlessAuthAcceptedOrPassword =>
                    code is not (NntpStatusCode.AuthenticationAccepted or NntpStatusCode.PasswordRequired),
                StatusTextNeed.UnlessAuthAccepted => code != NntpStatusCode.AuthenticationAccepted,
                _ => true,
            };
        }
    }
}
