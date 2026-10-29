namespace VectorNNTPD.ReaderService.SessionState
{
    /// <summary>Outcome of authenticated-session admission.</summary>
    public enum SessionAdmissionResult
    {
        /// <summary>The session was admitted, or admission was skipped because both limits are 0.</summary>
        Success,

        /// <summary><c>account_session_limit</c> would be exceeded. Wire: <c>481 Too many sessions</c>.</summary>
        SessionLimitExceeded,

        /// <summary>
        /// A new distinct source address would exceed <c>account_srcip_limit</c>.
        /// Wire: <c>481 Too many source addresses</c>.
        /// </summary>
        SourceAddressLimitExceeded,

        /// <summary>
        /// Cluster membership could not be established. New admission that requires Redis
        /// fails closed. Wire: <c>503 Temporary authentication failure</c>.
        /// </summary>
        Unavailable,
    }
}
