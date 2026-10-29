namespace VectorNNTPD.TransitService.SessionState.BytesAccounting
{
    /// <summary>Operational timings for account byte-quota reconciliation.</summary>
    /// <remarks>
    /// There is no AccountBytes timer. <see cref="SessionStateService"/> owns the
    /// single ~10-second cycle. Batching is not byte-exact. Redis is never used on
    /// the NNTP write path.
    /// </remarks>
    internal static class AccountByteDefaults
    {
        /// <summary>Documented batch cadence; the live scheduler is <see cref="SessionStateDefaults.RenewalPeriod"/>.</summary>
        public static readonly TimeSpan ReconciliationPeriod = SessionStateDefaults.RenewalPeriod;
    }
}
