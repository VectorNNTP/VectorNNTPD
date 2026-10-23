namespace VectorNNTPD.StorageService.Listener
{

    /// <summary>Source-generated StorageServer NNTP listener logs.</summary>
    internal static partial class StorageNntpListenerLogMessages
    {
        [LoggerMessage(
            EventId = 5400,
            Level = LogLevel.Information,
            Message = "Storage NNTP Listener starting bindPortTls={BindPortTls} endpoints={EndpointCount}")]
        public static partial void Starting(ILogger logger, int BindPortTls, int EndpointCount);

        [LoggerMessage(
            EventId = 5401,
            Level = LogLevel.Information,
            Message = "Storage NNTP Listener bound endpoint={Endpoint} family={AddressFamily}")]
        public static partial void EndpointBound(ILogger logger, string Endpoint, string AddressFamily);

        [LoggerMessage(
            EventId = 5402,
            Level = LogLevel.Information,
            Message = "Storage NNTP Listener running sockets={SocketCount} bindPortTls={BindPortTls}")]
        public static partial void Running(ILogger logger, int SocketCount, int BindPortTls);

        [LoggerMessage(
            EventId = 5403,
            Level = LogLevel.Information,
            Message = "Storage NNTP Listener retiring")]
        public static partial void Retiring(ILogger logger);

        [LoggerMessage(
            EventId = 5404,
            Level = LogLevel.Information,
            Message = "Storage NNTP Listener stopped")]
        public static partial void Stopped(ILogger logger);

        [LoggerMessage(
            EventId = 5405,
            Level = LogLevel.Error,
            Message = "Storage NNTP Listener failed to start: {Reason}")]
        public static partial void StartFailed(ILogger logger, string Reason);

        [LoggerMessage(
            EventId = 5406,
            Level = LogLevel.Warning,
            Message = "Storage NNTP Listener TLS handshake failed remote={Remote}")]
        public static partial void HandshakeFailed(ILogger logger, string Remote);

        [LoggerMessage(
            EventId = 5407,
            Level = LogLevel.Warning,
            Message = "Storage NNTP Listener connection timed out stage={Stage} remote={Remote}")]
        public static partial void ConnectionTimedOut(ILogger logger, string Stage, string Remote);

        [LoggerMessage(
            EventId = 5408,
            Level = LogLevel.Debug,
            Message = "Storage NNTP Listener accepted remote={Remote} active={Active}")]
        public static partial void Accepted(ILogger logger, string Remote, int Active);

        [LoggerMessage(
            EventId = 5409,
            Level = LogLevel.Warning,
            Message = "Storage NNTP Listener rejected connection at capacity remote={Remote} max={Max}")]
        public static partial void CapacityRejected(ILogger logger, string Remote, int Max);

        [LoggerMessage(
            EventId = 5410,
            Level = LogLevel.Warning,
            Message = "Storage NNTP Listener skipped unsupported wildcard family family={AddressFamily}: {Reason}")]
        public static partial void WildcardFamilySkipped(ILogger logger, string AddressFamily, string Reason);

        [LoggerMessage(
            EventId = 5411,
            Level = LogLevel.Warning,
            Message = "Storage NNTP Listener connection failed remote={Remote} error={ErrorType}")]
        public static partial void ConnectionFailed(ILogger logger, string Remote, string ErrorType);

        /// <summary>Routine peer reset or transport tear-down after accept; not an operational failure.</summary>
        [LoggerMessage(
            EventId = 5412,
            Level = LogLevel.Debug,
            Message = "Storage NNTP Listener connection peer disconnect remote={Remote}")]
        public static partial void ConnectionPeerDisconnected(ILogger logger, string Remote);

        /// <summary>
        /// A tracked connection task faulted during drain. Per-connection handlers already log expected
        /// handshake/I/O/protocol faults and complete successfully; a fault here is unexpected.
        /// </summary>
        [LoggerMessage(
            EventId = 5413,
            Level = LogLevel.Error,
            Message = "Storage NNTP Listener connection drain observed unexpected task failure")]
        public static partial void ConnectionDrainFailed(ILogger logger, Exception exception);
    }

    /// <summary>Source-generated StorageServer NNTP session logs.</summary>
    internal static partial class StorageNntpFeedSessionLogMessages
    {
        [LoggerMessage(
            EventId = 5500,
            Level = LogLevel.Debug,
            Message = "Storage NNTP client HELLO accepted maxFramePayload={MaxFramePayload}")]
        public static partial void ClientHelloAccepted(ILogger logger, uint MaxFramePayload);

        [LoggerMessage(
            EventId = 5501,
            Level = LogLevel.Debug,
            Message = "Storage NNTP OPEN rejected streamId={StreamId} requestId={RequestId}")]
        public static partial void OpenRejected(ILogger logger, uint StreamId, Guid RequestId);

        [LoggerMessage(
            EventId = 5502,
            Level = LogLevel.Debug,
            Message = "Storage NNTP session closed")]
        public static partial void ConnectionClosed(ILogger logger);

        [LoggerMessage(
            EventId = 5503,
            Level = LogLevel.Debug,
            Message = "Storage NNTP OPEN accepted streamId={StreamId} requestId={RequestId}")]
        public static partial void OpenAccepted(ILogger logger, uint StreamId, Guid RequestId);

        [LoggerMessage(
            EventId = 5504,
            Level = LogLevel.Debug,
            Message = "Storage NNTP transfer completed streamId={StreamId}")]
        public static partial void TransferCompleted(ILogger logger, uint StreamId);

        [LoggerMessage(
            EventId = 5505,
            Level = LogLevel.Debug,
            Message = "Storage NNTP transfer cancelled streamId={StreamId}")]
        public static partial void TransferCancelled(ILogger logger, uint StreamId);

        [LoggerMessage(
            EventId = 5506,
            Level = LogLevel.Trace,
            Message = "Storage NNTP STORE completed artId={ArtId} outcome={Outcome} attempt=1")]
        public static partial void StoreCompleted(ILogger logger, string ArtId, string Outcome);

        [LoggerMessage(
            EventId = 5507,
            Level = LogLevel.Warning,
            Message = "Storage NNTP STORE failed artId={ArtId} failure={Failure} attempt=1")]
        public static partial void StoreFailed(ILogger logger, string ArtId, string Failure);

        /// <summary>Logs that STORE body credit is withheld until queue capacity is reserved.</summary>
        /// <param name="logger">Session logger.</param>
        /// <param name="StreamId">STORE stream.</param>
        /// <param name="Bytes">META ArtSize waiting for a reservation.</param>
        [LoggerMessage(
            EventId = 5508,
            Level = LogLevel.Debug,
            Message = "Storage NNTP STORE waiting for queue capacity streamId={StreamId} bytes={Bytes}")]
        public static partial void StoreAdmissionWaiting(ILogger logger, uint StreamId, int Bytes);
    }
}
