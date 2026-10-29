namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>Cluster membership outcome for one Transit inbound admit attempt.</summary>
    public enum TransitPeerStateAdmitStatus
    {
        /// <summary>Admitted; this owner now holds one additional inbound connection.</summary>
        Accepted,

        /// <summary>
        /// Rejected: <c>MaxIncomingConnections</c> is <c>0</c> (closed) or the cluster
        /// already holds at least that many unexpired inbound connections.
        /// </summary>
        Rejected,

        /// <summary>Redis (or the membership store) was unavailable. Fail closed.</summary>
        Unavailable,
    }

    /// <summary>Result of a cluster Transit inbound admit attempt.</summary>
    public readonly struct TransitPeerStateAdmitResult
    {
        /// <summary>Initializes a Transit membership admit result.</summary>
        public TransitPeerStateAdmitResult(TransitPeerStateAdmitStatus status, long generation = 0)
        {
            Status = status;
            Generation = generation;
        }

        /// <summary>Gets the membership outcome.</summary>
        public TransitPeerStateAdmitStatus Status { get; }

        /// <summary>
        /// Gets the generation that must be supplied to <see cref="ITransitPeerStateTracker.ReleaseAsync"/>.
        /// Zero when admission was not granted.
        /// </summary>
        public long Generation { get; }

        /// <summary>Gets whether membership was granted.</summary>
        public bool Accepted => Status == TransitPeerStateAdmitStatus.Accepted;
    }

    /// <summary>Outcome of a Transit node ownership lease renewal.</summary>
    public enum TransitPeerStateRenewStatus
    {
        /// <summary>This owner's requested lease was refreshed.</summary>
        Renewed,

        /// <summary>The requested ownership field was gone, expired, or generation-mismatched.</summary>
        Lost,

        /// <summary>The membership store could not be reached.</summary>
        Unavailable,
    }
}
