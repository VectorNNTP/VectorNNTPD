namespace VectorNNTPD.StorageService.Listener
{
    /// <summary>
    /// How a Storage NNTP feed session finished after
    /// <see cref="StorageNntpFeedSession.RunAsync"/>.
    /// </summary>
    public enum StorageNntpFeedSessionEndReason
    {
        /// <summary>
        /// Normal close: <c>QUIT</c>, intentional session teardown after a command, or
        /// cancellation exited the command loop without a peer EOF.
        /// </summary>
        Closed = 0,

        /// <summary>
        /// Peer closed the connection while the session was reading the next command
        /// (transport read returned EOF).
        /// </summary>
        PeerDisconnected = 1,
    }
}
