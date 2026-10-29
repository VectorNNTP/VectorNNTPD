namespace VectorNNTPD.TransitService.SessionState.RateLimiting
{
    /// <summary>
    /// Converts <c>account_rate_limit</c> (bits per second) to integer bytes/sec and
    /// divides that aggregate by the cluster-wide active session count.
    /// </summary>
    /// <remarks>
    /// <c>account_rate_limit = 0</c> is unlimited (existing policy). Conversion is
    /// <c>floor(bps / 8)</c>. Division uses integer floor so
    /// <c>sum(per-session caps) &lt;= account bytes/sec</c>.
    /// The denominator is the live active session count, never
    /// <c>account_session_limit</c>. A positive configured rate that floors to
    /// <c>0</c> bytes/sec is <see cref="BlockedBytesPerSecond"/>, not unlimited.
    /// </remarks>
    public static class AccountRateFormula
    {
        /// <summary>Bits in one byte. Database <c>account_rate_limit</c> is bits/sec.</summary>
        public const int BitsPerByte = 8;

        /// <summary>
        /// Account-wide cap in bytes/sec: <c>floor(rateBps / 8)</c>.
        /// <paramref name="rateBps"/> <c>&lt;= 0</c> is unlimited and returns <c>0</c>.
        /// A positive rate below 8 bps returns <c>0</c> here; callers that apply a
        /// limiter cap must use <see cref="EqualShareCap"/> so that is blocked.
        /// </summary>
        public static long AccountBytesPerSecond(int rateBps)
        {
            if (rateBps <= 0)
            {
                return 0;
            }

            // int.MaxValue / 8 fits in int and long; no Mbps multiplier remains.
            return (long)rateBps / BitsPerByte;
        }

        /// <summary>
        /// Per-session cap in bytes/sec: <c>floor(accountBytesPerSec / activeSessions)</c>.
        /// Returns <c>0</c> (unlimited / no allocation) when the account rate is unlimited
        /// or there are no active sessions. A positive rate that floors to <c>0</c> is
        /// not unlimited; use <see cref="EqualShareCap"/> on the write path.
        /// </summary>
        public static long PerSessionBytesPerSecond(int rateBps, int activeSessions)
        {
            if (rateBps <= 0 || activeSessions <= 0)
            {
                return 0;
            }

            return AccountBytesPerSecond(rateBps) / activeSessions;
        }

        /// <summary>
        /// Equal-share cap for a positive rate. A floor of <c>0</c> is
        /// <see cref="BlockedBytesPerSecond"/> so the limiter cannot treat it as unlimited.
        /// </summary>
        public static long EqualShareCap(int rateBps, int activeSessions)
        {
            if (rateBps <= 0)
            {
                return 0;
            }

            var perSession = PerSessionBytesPerSecond(rateBps, activeSessions);
            return perSession > 0 ? perSession : BlockedBytesPerSecond;
        }

        /// <summary>
        /// Returns whether <c>perSession * activeSessions</c> stays at or under the account cap.
        /// </summary>
        public static bool AggregateDoesNotExceed(int rateBps, int activeSessions)
        {
            if (rateBps <= 0 || activeSessions <= 0)
            {
                return true;
            }

            var perSession = PerSessionBytesPerSecond(rateBps, activeSessions);
            return perSession * activeSessions <= AccountBytesPerSecond(rateBps);
        }

        /// <summary>
        /// Bytes/sec a newly admitted session may take while remotes may still hold
        /// <c>floor(account / previousSessions)</c>. This is the unused remainder
        /// after that previous equal split. <c>0</c> means no residual; callers must
        /// not treat it as unlimited.
        /// </summary>
        public static long ConservativeJoinBytesPerSecond(int rateBps, int previousActiveSessions)
        {
            if (rateBps <= 0 || previousActiveSessions <= 0)
            {
                return 0;
            }

            return AccountBytesPerSecond(rateBps) % previousActiveSessions;
        }

        /// <summary>
        /// Negative sentinel: block writes until the cap is replaced. Distinct from
        /// <c>0</c>, which remains unlimited.
        /// </summary>
        public const long BlockedBytesPerSecond = -1;

        /// <summary>Counts a stored cap toward a cluster aggregate. Blocked contributes 0; unlimited is 0.</summary>
        public static long AllocatedBytesPerSecond(long cap) => cap > 0 ? cap : 0;
    }
}
