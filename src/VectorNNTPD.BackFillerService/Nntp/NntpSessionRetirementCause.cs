namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// Structured causes for <see cref="NntpLogMessages.SessionRetired"/>. Distinct from the human-readable reason text.
    /// </summary>
    internal static class NntpSessionRetirementCause
    {
        /// <summary>Lease dispose requested retirement (<see cref="NntpSessionLease.Retire"/> or non-reusable session).</summary>
        internal const string LeaseRequested = "lease_requested";

        /// <summary>Pool dispose or shutdown cancelled outstanding idle/leased sessions.</summary>
        internal const string PoolShuttingDown = "pool_shutting_down";

        /// <summary>Idle return found live count above the bound max after a MaxSessions shrink.</summary>
        internal const string MaxSessionsShrink = "max_sessions_shrink";

        /// <summary>Idle admission rejected the session as not reusable (unhealthy, wrong state, or missing stream).</summary>
        internal const string SessionNotReusable = "session_not_reusable";

        /// <summary>DATE keepalive observed a non-reusable or failed session while idle.</summary>
        internal const string DateKeepaliveFailed = "date_keepalive_failed";

        /// <summary>Connect/publication path retired a session that never became Ready for issue.</summary>
        internal const string EstablishmentFailed = "establishment_failed";
    }
}
