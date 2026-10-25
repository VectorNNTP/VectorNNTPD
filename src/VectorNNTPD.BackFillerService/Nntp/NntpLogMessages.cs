namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>Source-generated NNTP provider logs. Never includes credentials or article bodies.</summary>
    internal static partial class NntpLogMessages
    {
        /// <summary>
        /// Written after connect, greeting, capability negotiation, optional STARTTLS, and authentication
        /// leave the session in <see cref="NntpSessionState.Ready"/>.
        /// </summary>
        /// <param name="logger">Logger that receives the information event.</param>
        /// <param name="Backbone">Provider backbone label.</param>
        /// <param name="ConnectionId"><see cref="NntpProviderSession.ConnectionId"/> of the ready physical connection.</param>
        /// <param name="Host">Upstream host from the provider definition.</param>
        /// <param name="Port">Upstream port from the provider definition.</param>
        /// <param name="UseTls">
        /// <see cref="BackFillerProviderDefinition.UseTls"/> (implicit TLS from connect).
        /// A session that upgraded with STARTTLS still logs false when implicit TLS is off.
        /// </param>
        [LoggerMessage(
            EventId = 5400,
            Level = LogLevel.Information,
            Message = "NNTP session ready backbone={Backbone} connectionId={ConnectionId} host={Host} port={Port} tls={UseTls}")]
        internal static partial void SessionReady(
            ILogger logger,
            string Backbone,
            Guid ConnectionId,
            string Host,
            int Port,
            bool UseTls);

        /// <summary>
        /// Written when a live pooled session is removed for retirement, before that session is disposed.
        /// Not written when the session was already absent from the pool.
        /// </summary>
        /// <param name="logger">Logger that receives the warning.</param>
        /// <param name="Backbone">Backbone of the pool that owned the session.</param>
        /// <param name="ConnectionId"><see cref="NntpProviderSession.ConnectionId"/> of the retired physical connection.</param>
        /// <param name="Cause">Structured cause from <see cref="NntpSessionRetirementCause"/>.</param>
        /// <param name="Reason">Caller-supplied human-readable retirement text. This method does not throw.</param>
        [LoggerMessage(
            EventId = 5401,
            Level = LogLevel.Warning,
            Message = "NNTP session retired backbone={Backbone} connectionId={ConnectionId} cause={Cause} reason={Reason}")]
        internal static partial void SessionRetired(
            ILogger logger,
            string Backbone,
            Guid ConnectionId,
            string Cause,
            string Reason);

        /// <summary>
        /// Defined for a non-success download. The article retriever does not call it.
        /// Article completion is one Information event from <c>ProviderArticleWorkHandler</c>.
        /// </summary>
        /// <param name="logger">Logger that receives the warning.</param>
        /// <param name="Backbone">Work-item backbone (<see cref="VectorNNTPD.BackFillerService.ArticleWork.ArticleWorkRequest.Backbone"/>).</param>
        /// <param name="Kind">Retrieval classification from the download result.</param>
        /// <param name="StatusCode">NNTP status from the download result, or null when the server produced none.</param>
        /// <param name="Reason">Diagnostic text from the download result. Callers do not pass secrets.</param>
        [LoggerMessage(
            EventId = 5402,
            Level = LogLevel.Information,
            Message = "NNTP provider retrieval failed backbone={Backbone} kind={Kind} status={StatusCode} reason={Reason}")]
        private static partial void RetrievalFailed(ILogger logger, string Backbone, ArticleRetrievalKind Kind, int? StatusCode, string Reason);

        /// <summary>
        /// Written by the provider registry after it publishes pool ACTIVE counts.
        /// Not written when dispose publishes an empty snapshot.
        /// </summary>
        /// <param name="logger">Logger that receives the information event.</param>
        /// <param name="BackboneCount">
        /// Caller-supplied count. The registry passes how many entries in the dictionary just given to
        /// <see cref="BackboneUsableCapacityState.PublishSnapshot"/> have a positive count.
        /// A blank name with a positive count is included here even though publication omits it.
        /// </param>
        [LoggerMessage(
            EventId = 5403,
            Level = LogLevel.Information,
            Message = "NNTP usable capacity published backboneCount={BackboneCount}")]
        internal static partial void UsableCapacityPublished(ILogger logger, int BackboneCount);

        /// <summary>
        /// Written when a pool deficit fill throws an exception other than shutdown cancellation,
        /// and when Article Work reconcile throws any exception.
        /// </summary>
        /// <param name="logger">Logger that receives the warning.</param>
        /// <param name="Backbone">
        /// Caller-supplied scope. Pool fills pass the provider backbone. Consumer reconcile passes the literal <c>consumers</c>.
        /// </param>
        /// <param name="Reason">
        /// <see cref="Exception.Message"/> from the caught exception. The exception is logged through this text; this method does not throw it.
        /// </param>
        [LoggerMessage(
            EventId = 5404,
            Level = LogLevel.Warning,
            Message = "NNTP session replenish failed backbone={Backbone} reason={Reason}")]
        internal static partial void SessionReplenishFailed(ILogger logger, string Backbone, string Reason);

        /// <summary>
        /// Written at Debug before waiting for the application-wide establishment gate.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Backbone">Provider backbone label.</param>
        /// <param name="ConnectionNumber">One-based pool slot for the session being established.</param>
        [LoggerMessage(
            EventId = 5412,
            Level = LogLevel.Debug,
            Message = "NNTP session establishment waiting for capacity backbone={Backbone} connection={ConnectionNumber}")]
        internal static partial void SessionEstablishmentWaiting(ILogger logger, string Backbone, int ConnectionNumber);

        /// <summary>
        /// Written at Debug after the establishment gate is acquired and before <c>ConnectAsync</c>.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Backbone">Provider backbone label.</param>
        /// <param name="ConnectionNumber">One-based pool slot for the session being established.</param>
        [LoggerMessage(
            EventId = 5413,
            Level = LogLevel.Debug,
            Message = "NNTP session establishment started backbone={Backbone} connection={ConnectionNumber}")]
        internal static partial void SessionEstablishmentStarted(ILogger logger, string Backbone, int ConnectionNumber);

        /// <summary>
        /// Written at Debug when connect leaves the session Ready and the establishment gate is about to release.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Backbone">Provider backbone label.</param>
        /// <param name="ConnectionNumber">One-based pool slot for the session that became Ready.</param>
        [LoggerMessage(
            EventId = 5414,
            Level = LogLevel.Debug,
            Message = "NNTP session establishment completed backbone={Backbone} connection={ConnectionNumber}")]
        internal static partial void SessionEstablishmentCompleted(ILogger logger, string Backbone, int ConnectionNumber);

        /// <summary>
        /// Written at Debug when establishment is cancelled while waiting for the gate or during connect.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Backbone">Provider backbone label.</param>
        /// <param name="ConnectionNumber">One-based pool slot for the cancelled establishment.</param>
        [LoggerMessage(
            EventId = 5415,
            Level = LogLevel.Debug,
            Message = "NNTP session establishment cancelled backbone={Backbone} connection={ConnectionNumber}")]
        internal static partial void SessionEstablishmentCancelled(ILogger logger, string Backbone, int ConnectionNumber);

        /// <summary>
        /// Written at Debug when connect fails after the establishment gate was acquired.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Backbone">Provider backbone label.</param>
        /// <param name="ConnectionNumber">One-based pool slot for the failed establishment.</param>
        /// <param name="Reason">Diagnostic text from the connect failure. Callers do not pass secrets.</param>
        [LoggerMessage(
            EventId = 5416,
            Level = LogLevel.Debug,
            Message = "NNTP session establishment failed backbone={Backbone} connection={ConnectionNumber} reason={Reason}")]
        internal static partial void SessionEstablishmentFailed(ILogger logger, string Backbone, int ConnectionNumber, string Reason);

        /// <summary>
        /// Written at Debug when a session starts connect, before the transport is opened.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
        /// <param name="Host">Upstream host from the provider definition.</param>
        /// <param name="Port">Upstream port from the provider definition.</param>
        /// <param name="UseTls">
        /// Caller-supplied text. The session passes <c>true</c> or <c>false</c> for
        /// <see cref="BackFillerProviderDefinition.UseTls"/>, not a Boolean and not STARTTLS state.
        /// </param>
        [LoggerMessage(
            EventId = 5405,
            Level = LogLevel.Debug,
            Message = "{Session}: Connecting article acquisition session to {Host}:{Port} (SSL={UseTls})")]
        internal static partial void WireConnecting(ILogger logger, string Session, string Host, int Port, string UseTls);

        /// <summary>
        /// Written at Debug immediately before command bytes are written, including QUIT during dispose.
        /// The log is emitted even if the write then fails.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
        /// <param name="Command">
        /// Caller-supplied command text. AUTHINFO callers pass redacted <c>AUTHINFO USER ***</c> and
        /// <c>AUTHINFO PASS ***</c>. This method does not redact <paramref name="Command"/>.
        /// </param>
        [LoggerMessage(
            EventId = 5406,
            Level = LogLevel.Debug,
            Message = "{Session}: TX: {Command}")]
        internal static partial void WireTx(ILogger logger, string Session, string Command);

        /// <summary>
        /// Written at Debug when a non-null status line has been read. Article payload bytes are not logged here.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
        /// <param name="Response">ASCII decoding of the status-line bytes supplied by the caller.</param>
        [LoggerMessage(
            EventId = 5407,
            Level = LogLevel.Debug,
            Message = "{Session}: RX: {Response}")]
        internal static partial void WireRx(ILogger logger, string Session, string Response);

        /// <summary>
        /// Written at Debug after a destuffed ARTICLE payload has been read and before the header/body separator check.
        /// Not written when the payload read throws.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
        /// <param name="Bytes">Destuffed payload length. The payload itself is not logged.</param>
        [LoggerMessage(
            EventId = 5408,
            Level = LogLevel.Debug,
            Message = "{Session}: RX: ARTICLE payload complete bytes={Bytes}")]
        internal static partial void WireArticlePayloadComplete(ILogger logger, string Session, int Bytes);

        /// <summary>
        /// Written at Debug on the first dispose, after the session enters <see cref="NntpSessionState.Retiring"/> and before QUIT.
        /// A second dispose does not write it.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
        [LoggerMessage(
            EventId = 5409,
            Level = LogLevel.Debug,
            Message = "{Session}: Session retiring")]
        internal static partial void WireRetiring(ILogger logger, string Session);

        /// <summary>
        /// Written at Debug immediately before a STARTTLS handshake on the existing clear-text stream.
        /// Implicit TLS from connect does not write this event.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
        [LoggerMessage(
            EventId = 5410,
            Level = LogLevel.Debug,
            Message = "{Session}: TLS handshake starting")]
        internal static partial void WireTlsHandshakeStarting(ILogger logger, string Session);

        /// <summary>
        /// Written at Debug after a STARTTLS handshake returns and the session stream has been replaced.
        /// Not written when the handshake throws. Implicit TLS from connect does not write this event.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
        [LoggerMessage(
            EventId = 5411,
            Level = LogLevel.Debug,
            Message = "{Session}: TLS handshake completed")]
        internal static partial void WireTlsHandshakeCompleted(ILogger logger, string Session);

        /// <summary>
        /// Written at Debug when best-effort QUIT write or flush fails during dispose.
        /// The session still disposes the transport and transitions to closed.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
        /// <param name="ErrorType">Runtime exception type from the QUIT write/flush failure.</param>
        [LoggerMessage(
            EventId = 5417,
            Level = LogLevel.Debug,
            Message = "{Session}: QUIT failed during dispose error={ErrorType}")]
        internal static partial void WireQuitFailed(ILogger logger, string Session, string ErrorType);

        /// <summary>
        /// Written at Debug when closing the plaintext transport fails after a pre-handshake STARTTLS error.
        /// The initiating handshake error remains the connect failure. This event records only the cleanup failure.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
        /// <param name="ErrorType">Runtime exception type from the transport dispose.</param>
        [LoggerMessage(
            EventId = 5418,
            Level = LogLevel.Debug,
            Message = "{Session}: STARTTLS transport cleanup failed error={ErrorType}")]
        internal static partial void WireTlsCleanupFailed(ILogger logger, string Session, string ErrorType);

        /// <summary>
        /// Written at Debug when cancelling the session handshake token throws from a registered callback.
        /// Disposal still closes the captured transport and ends <see cref="NntpSessionState.Closed"/>.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
        /// <param name="ErrorType">Runtime exception type from <see cref="CancellationTokenSource.CancelAsync"/>.</param>
        [LoggerMessage(
            EventId = 5419,
            Level = LogLevel.Debug,
            Message = "{Session}: handshake cancellation callback failed error={ErrorType}")]
        internal static partial void WireHandshakeCancellationFailed(ILogger logger, string Session, string ErrorType);

        /// <summary>
        /// Written at Debug when disposing the captured transport throws after QUIT was attempted or skipped.
        /// The session still transitions to <see cref="NntpSessionState.Closed"/>.
        /// </summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
        /// <param name="ErrorType">Runtime exception type from the transport dispose.</param>
        [LoggerMessage(
            EventId = 5420,
            Level = LogLevel.Debug,
            Message = "{Session}: transport dispose failed error={ErrorType}")]
        internal static partial void WireTransportDisposeFailed(ILogger logger, string Session, string ErrorType);

        /// <summary>
        /// Written once when the provider registry starts, before any pool connects.
        /// </summary>
        /// <param name="logger">Logger that receives the information event.</param>
        /// <param name="SendBufferBytes">Configured upstream TCP send-buffer limit. Not the OS-reported value.</param>
        /// <param name="ReceiveBufferBytes">Configured upstream TCP receive-buffer limit. Not the OS-reported value.</param>
        /// <remarks>
        /// Nagle is disabled for every upstream socket. This event does not repeat per session or per write.
        /// </remarks>
        [LoggerMessage(
            EventId = 5421,
            Level = LogLevel.Information,
            Message = "NNTP upstream sockets noDelay=true sendBufferBytes={SendBufferBytes} receiveBufferBytes={ReceiveBufferBytes}")]
        internal static partial void UpstreamSocketOptions(ILogger logger, int SendBufferBytes, int ReceiveBufferBytes);
    }
}
