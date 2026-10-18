namespace VectorNNTPD.BackFillerService.Listener
{
    /// <summary>Source-generated cache Listener logs. Never includes payloads, certificates, or secrets.</summary>
    /// <remarks>
    /// Each method writes one structured event and does not throw the logged condition.
    /// Call sites are <see cref="CacheListenerService"/>.
    /// </remarks>
    internal static partial class CacheListenerLogMessages
    {
        /// <summary>
        /// Written from <see cref="CacheListenerService"/> start after a non-empty bind plan exists and before sockets are bound.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="BindPortTls">Configured TLS listen port.</param>
        /// <param name="EndpointCount">Number of planned bind endpoints.</param>
        [LoggerMessage(
            EventId = 5400,
            Level = LogLevel.Information,
            Message = "Cache Listener starting bindPortTls={BindPortTls} endpoints={EndpointCount}")]
        internal static partial void Starting(ILogger logger, int BindPortTls, int EndpointCount);

        /// <summary>Written after one listen socket is bound and added to the service socket list.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Endpoint">Bound endpoint text from <c>IPEndPoint.ToString()</c>.</param>
        /// <param name="AddressFamily">Address family name of the binding address.</param>
        [LoggerMessage(
            EventId = 5401,
            Level = LogLevel.Information,
            Message = "Cache Listener bound endpoint={Endpoint} family={AddressFamily}")]
        internal static partial void EndpointBound(ILogger logger, string Endpoint, string AddressFamily);

        /// <summary>Written after the service state becomes running and before the accept task is assigned.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="SocketCount">Number of sockets successfully bound.</param>
        /// <param name="BindPortTls">Configured TLS listen port.</param>
        /// <param name="SendBufferBytes">TCP send-buffer limit that each accepted socket will request. Not the OS-reported value.</param>
        /// <param name="ReceiveBufferBytes">TCP receive-buffer limit that each accepted socket will request. Not the OS-reported value.</param>
        /// <remarks>Nagle is disabled on each accepted socket. This event is once per successful start, not per connection.</remarks>
        [LoggerMessage(
            EventId = 5402,
            Level = LogLevel.Information,
            Message = "Cache Listener running sockets={SocketCount} bindPortTls={BindPortTls} noDelay=true sendBufferBytes={SendBufferBytes} receiveBufferBytes={ReceiveBufferBytes}")]
        internal static partial void Running(
            ILogger logger,
            int SocketCount,
            int BindPortTls,
            int SendBufferBytes,
            int ReceiveBufferBytes);

        /// <summary>Written at the start of disposal, after the state becomes retiring and before the accept token is cancelled.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 5403,
            Level = LogLevel.Information,
            Message = "Cache Listener retiring")]
        internal static partial void Retiring(ILogger logger);

        /// <summary>Written after admitted connections have been awaited, sockets released, and the state set to stopped.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 5404,
            Level = LogLevel.Information,
            Message = "Cache Listener stopped")]
        internal static partial void Stopped(ILogger logger);

        /// <summary>Written when start throws, before sockets are released and the exception is rethrown.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Reason"><c>Exception.Message</c> from the start failure. The exception object is not attached.</param>
        [LoggerMessage(
            EventId = 5405,
            Level = LogLevel.Error,
            Message = "Cache Listener failed to start: {Reason}")]
        internal static partial void StartFailed(ILogger logger, string Reason);

        /// <summary>Written when the TLS handshake throws <see cref="System.Security.Authentication.AuthenticationException"/>.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Remote">Remote endpoint text, or <c>&lt;unknown&gt;</c> when the socket has no remote endpoint.</param>
        [LoggerMessage(
            EventId = 5406,
            Level = LogLevel.Warning,
            Message = "Cache Listener TLS handshake failed remote={Remote}")]
        internal static partial void HandshakeFailed(ILogger logger, string Remote);

        /// <summary>Written when a connection catches <see cref="TimeoutException"/>.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Stage">
        /// <c>io-progress</c> after the handshake completed; otherwise the exception message
        /// (<c>tls-handshake</c> for the handshake timeout).
        /// </param>
        /// <param name="Remote">Remote endpoint text, or <c>&lt;unknown&gt;</c> when the socket has no remote endpoint.</param>
        [LoggerMessage(
            EventId = 5407,
            Level = LogLevel.Warning,
            Message = "Cache Listener connection timed out stage={Stage} remote={Remote}")]
        internal static partial void ConnectionTimedOut(ILogger logger, string Stage, string Remote);

        /// <summary>Written after a connection is admitted and before its processing task runs.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Remote">Remote endpoint text, or <c>&lt;unknown&gt;</c> when the socket has no remote endpoint.</param>
        /// <param name="Active">Admitted connection count after this connection was reserved.</param>
        [LoggerMessage(
            EventId = 5408,
            Level = LogLevel.Debug,
            Message = "Cache Listener accepted remote={Remote} active={Active}")]
        internal static partial void Accepted(ILogger logger, string Remote, int Active);

        /// <summary>Written when admission fails because the active count is already at the configured maximum. The socket is then disposed.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Remote">Remote endpoint text, or <c>&lt;unknown&gt;</c> when the socket has no remote endpoint.</param>
        /// <param name="Max">Configured maximum active connections.</param>
        [LoggerMessage(
            EventId = 5409,
            Level = LogLevel.Warning,
            Message = "Cache Listener rejected connection at capacity remote={Remote} max={Max}")]
        internal static partial void CapacityRejected(ILogger logger, string Remote, int Max);

        /// <summary>
        /// Written when an implicit wildcard bind throws <see cref="System.Net.Sockets.SocketException"/>
        /// with <c>AddressFamilyNotSupported</c>, <c>ProtocolNotSupported</c>, or <c>AddressNotAvailable</c>.
        /// That family is skipped. Other bind failures are not logged here.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="AddressFamily">Address family name that was skipped.</param>
        /// <param name="Reason"><c>SocketError</c> name from the failed bind.</param>
        [LoggerMessage(
            EventId = 5410,
            Level = LogLevel.Warning,
            Message = "Cache Listener skipped unsupported wildcard family family={AddressFamily}: {Reason}")]
        internal static partial void WildcardFamilySkipped(ILogger logger, string AddressFamily, string Reason);

        /// <summary>
        /// Written for a connection failure that is not handshake authentication, timeout, cancellation, or <see cref="IOException"/>.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Remote">Remote endpoint text, or <c>&lt;unknown&gt;</c> when the socket has no remote endpoint.</param>
        /// <param name="ErrorType">Runtime type name of the exception. The exception object is not attached.</param>
        [LoggerMessage(
            EventId = 5411,
            Level = LogLevel.Warning,
            Message = "Cache Listener connection failed remote={Remote} error={ErrorType}")]
        internal static partial void ConnectionFailed(ILogger logger, string Remote, string ErrorType);

        /// <summary>
        /// Written when a connection ends with an <see cref="IOException"/> that is expected to be recoverable per-connection noise.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Remote">Remote endpoint text, or <c>&lt;unknown&gt;</c> when unavailable.</param>
        /// <param name="ErrorType">Runtime exception type name.</param>
        [LoggerMessage(
            EventId = 5412,
            Level = LogLevel.Debug,
            Message = "Cache Listener connection io closed remote={Remote} error={ErrorType}")]
        internal static partial void ConnectionIoClosed(ILogger logger, string Remote, string ErrorType);

        /// <summary>
        /// Written when best-effort disposal of a listen socket throws a socket-level failure.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Reason">Socket error name from the dispose failure.</param>
        [LoggerMessage(
            EventId = 5413,
            Level = LogLevel.Debug,
            Message = "Cache Listener listen socket dispose failed reason={Reason}")]
        internal static partial void ListenSocketDisposeFailed(ILogger logger, string Reason);

        /// <summary>
        /// Written when draining tracked connection tasks observes a non-cancellation fault.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="ErrorType">Runtime exception type name from the aggregated drain fault.</param>
        [LoggerMessage(
            EventId = 5414,
            Level = LogLevel.Warning,
            Message = "Cache Listener connection drain observed fault error={ErrorType}")]
        internal static partial void ConnectionDrainFailed(ILogger logger, string ErrorType);

        /// <summary>
        /// Written when accepted-socket option setup fails. The socket is disposed and the admission slot is released.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Remote">Remote endpoint text, or <c>&lt;unknown&gt;</c>.</param>
        /// <param name="Error">
        /// <see cref="System.Net.Sockets.SocketError"/> name, or the exception type name when the failure is not a
        /// <see cref="System.Net.Sockets.SocketException"/>.
        /// </param>
        [LoggerMessage(
            EventId = 5415,
            Level = LogLevel.Warning,
            Message = "Cache Listener accepted socket options failed remote={Remote} error={Error}")]
        internal static partial void AcceptedSocketOptionsFailed(ILogger logger, string Remote, string Error);
    }
}
