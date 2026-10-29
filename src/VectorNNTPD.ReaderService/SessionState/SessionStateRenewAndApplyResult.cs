namespace VectorNNTPD.ReaderService.SessionState
{
    /// <summary>Outcome of one atomic session-renewal plus byte-quota APPLY.</summary>
    public readonly struct SessionStateRenewAndApplyResult
    {
        /// <summary>Initializes a combined renew/apply result.</summary>
        public SessionStateRenewAndApplyResult(SessionStateRenewStatus renew, long? remaining)
        {
            Renew = renew;
            Remaining = remaining;
        }

        /// <summary>Gets the session/source lease renewal outcome.</summary>
        public SessionStateRenewStatus Renew { get; }

        /// <summary>
        /// Remaining after APPLY, or <see langword="null"/> when this store did not
        /// apply byte state (in-memory renew-only path).
        /// </summary>
        public long? Remaining { get; }
    }
}
