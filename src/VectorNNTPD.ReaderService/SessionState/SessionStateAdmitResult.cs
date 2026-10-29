namespace VectorNNTPD.ReaderService.SessionState
{
    /// <summary>Cluster membership outcome for one combined session/source-IP admit attempt.</summary>
    public enum SessionStateAdmitStatus
    {
        /// <summary>Admitted; the source IP was already active.</summary>
        AcceptedExisting,

        /// <summary>Admitted; a new distinct source IP was established.</summary>
        AcceptedNew,

        /// <summary>Rejected: cluster <c>account_session_limit</c> would be exceeded.</summary>
        RejectedSessionLimit,

        /// <summary>Rejected: cluster <c>account_srcip_limit</c> would be exceeded.</summary>
        RejectedSourceLimit,

        /// <summary>Redis (or the membership store) was unavailable. Fail closed.</summary>
        Unavailable,
    }

    /// <summary>Result of a cluster session/source-IP admit attempt.</summary>
    public readonly struct SessionStateAdmitResult
    {
        /// <summary>Initializes a membership admit result.</summary>
        public SessionStateAdmitResult(SessionStateAdmitStatus status, int sessionTotal = 0)
        {
            Status = status;
            SessionTotal = sessionTotal < 0 ? 0 : sessionTotal;
        }

        /// <summary>Gets the membership outcome.</summary>
        public SessionStateAdmitStatus Status { get; }

        /// <summary>
        /// Cluster-wide unexpired session count after this attempt (0 when rejected
        /// or unavailable).
        /// </summary>
        public int SessionTotal { get; }

        /// <summary>Gets whether membership was granted.</summary>
        public bool Accepted =>
            Status is SessionStateAdmitStatus.AcceptedExisting or SessionStateAdmitStatus.AcceptedNew;
    }

    /// <summary>Outcome of a node ownership lease renewal.</summary>
    public enum SessionStateRenewStatus
    {
        /// <summary>This owner's requested leases were refreshed.</summary>
        Renewed,

        /// <summary>At least one requested ownership field was gone or mismatched.</summary>
        Lost,

        /// <summary>The membership store could not be reached.</summary>
        Unavailable,
    }

    /// <summary>Renewal outcome plus the cluster session total used for rate allocation.</summary>
    public readonly struct SessionStateRenewResult
    {
        /// <summary>Initializes a renewal result.</summary>
        public SessionStateRenewResult(SessionStateRenewStatus status, int sessionTotal = 0)
        {
            Status = status;
            SessionTotal = sessionTotal < 0 ? 0 : sessionTotal;
        }

        /// <summary>Gets the renewal outcome.</summary>
        public SessionStateRenewStatus Status { get; }

        /// <summary>Unexpired cluster session count when <see cref="Status"/> is Renewed.</summary>
        public int SessionTotal { get; }

        /// <summary>Converts a status-only result (session total 0).</summary>
        public static implicit operator SessionStateRenewResult(SessionStateRenewStatus status) =>
            new(status);
    }
}
