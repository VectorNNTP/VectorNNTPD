using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.SessionState.RateLimiting;
namespace VectorNNTPD.TransitService.Authentication
{
    /// <summary>
    /// Authenticated account policy copied from <c>nntpusers</c> after a successful credential check.
    /// </summary>
    /// <remarks>
    /// Every authenticated account has both remaining-byte and rate policies.
    /// Session and source-IP limits are enforced by
    /// <see cref="VectorNNTPD.TransitService.SessionState.ISessionStateTracker"/>.
    /// <see cref="RateLimitBps"/> is enforced by
    /// <see cref="VectorNNTPD.TransitService.SessionState.RateLimiting.IAccountRateAllocator"/>
    /// after SessionState admission. <see cref="ByteLimit"/> is the AUTHINFO-time
    /// remaining-byte snapshot and is not enforced here; live remaining is observed by
    /// <see cref="VectorNNTPD.TransitService.SessionState.BytesAccounting.IAccountByteAccountant"/>.
    /// </remarks>
    public sealed class NntpAccountPolicy
    {
        /// <summary>Initializes a new account policy snapshot.</summary>
        public NntpAccountPolicy(
            string username,
            int rateLimitBps,
            long byteLimit,
            int sessionLimit,
            int srcIpLimit,
            string customerId,
            ArticleType allowedArtTypes = ArticleTypeCapabilities.All)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(username);
            Username = username;
            RateLimitBps = rateLimitBps;
            ByteLimit = byteLimit;
            SessionLimit = sessionLimit;
            SrcIpLimit = srcIpLimit;
            CustomerId = customerId ?? string.Empty;
            AllowedArtTypes = allowedArtTypes;
        }

        /// <summary>Gets the plaintext wire username.</summary>
        public string Username { get; }

        /// <summary>
        /// Gets <c>account_rate_limit</c> in bits per second.
        /// Examples: <c>240</c> = 240 bps, <c>2_400</c> = 2,400 bps = 300 B/s,
        /// <c>1_000_000</c> = 1 Mbps, <c>10_000_000</c> = 10 Mbps.
        /// <c>0</c> is unlimited. Negative values are treated as unlimited.
        /// </summary>
        public int RateLimitBps { get; }

        /// <summary>
        /// Gets the AUTHINFO-time snapshot of <c>account_byte_limit</c>.
        /// This is remaining bytes at last user-record load: <c>0</c> is exhausted,
        /// not unlimited. Live remaining is observed by
        /// <see cref="VectorNNTPD.TransitService.SessionState.BytesAccounting.IAccountByteAccountant"/> and must not
        /// be taken from this cached snapshot.
        /// </summary>
        public long ByteLimit { get; }

        /// <summary>Gets the cluster-wide authenticated session cap. <c>0</c> is unlimited.</summary>
        public int SessionLimit { get; }

        /// <summary>Gets the cluster-wide distinct source-IP cap. <c>0</c> is unlimited.</summary>
        public int SrcIpLimit { get; }

        /// <summary>Gets the customer/tenant identifier.</summary>
        public string CustomerId { get; }

        /// <summary>
        /// Gets the account ArtType capability flags loaded at AUTHINFO.
        /// <see cref="ArticleTypeCapabilities.All"/> is unrestricted and is the
        /// default for existing users.
        /// </summary>
        public ArticleType AllowedArtTypes { get; }

        /// <summary>
        /// Gets whether cluster admission must run: session limit, source-IP limit,
        /// or a positive rate that needs a cluster session count.
        /// </summary>
        public bool RequiresAdmission => SessionLimit > 0 || SrcIpLimit > 0 || RequiresRateTracking;

        /// <summary>
        /// Gets whether this account has a positive <c>account_rate_limit</c>.
        /// <c>0</c> remains unlimited and does not participate in rate allocation.
        /// </summary>
        public bool RequiresRateTracking => RateLimitBps > 0;

        /// <summary>Builds policy from a validated user record.</summary>
        public static NntpAccountPolicy FromRecord(NntpUserRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            return new(
                record.AccountName,
                record.RateLimitBps,
                record.ByteLimit,
                record.SessionLimit,
                record.SrcIpLimit,
                record.CustomerId,
                record.AllowedArtTypes);
        }
    }
}
