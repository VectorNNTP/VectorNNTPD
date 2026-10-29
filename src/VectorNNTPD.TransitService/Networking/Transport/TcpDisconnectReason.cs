namespace VectorNNTPD.TransitService.Networking.Transport
{
    /// <summary>
    /// Why an established TCP connection completed. Values are only those the existing
    /// teardown paths can set from known cause, not inferred from timing.
    /// </summary>
    internal enum TcpDisconnectReason
    {
        /// <summary>No path has recorded a cause yet.</summary>
        Unspecified = 0,

        /// <summary>Receive pump observed a 0-byte read (peer FIN) before local cancellation.</summary>
        RemoteClosed = 1,

        /// <summary><see cref="INntpConnection.CompleteAsync"/> was invoked without an error or prior cause.</summary>
        LocalClose = 2,

        /// <summary>The receive pump faulted.</summary>
        ReceiveError = 3,

        /// <summary>The send pump faulted.</summary>
        SendError = 4,

        /// <summary><see cref="INntpConnection.CompleteAsync"/> was invoked with <see cref="OperationCanceledException"/>.</summary>
        Cancellation = 5,

        /// <summary>Listener stop completed remaining connections.</summary>
        Shutdown = 6,

        /// <summary>Session <c>RequestClose</c> (QUIT and other protocol-initiated close).</summary>
        ProtocolClose = 7,

        /// <summary>Completed with an exception that is not a receive/send pump fault.</summary>
        ConnectionClosed = 8,

        /// <summary>No NNTP command was executed within <c>Nntpd:IdleTime</c>.</summary>
        IdleTimeout = 9,
    }
}
