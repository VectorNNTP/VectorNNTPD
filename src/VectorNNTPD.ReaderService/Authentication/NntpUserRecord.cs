using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.ReaderService.Authentication
{
    /// <summary>
    /// In-process snapshot of one <c>nntpusers</c> row after SQL decrypt.
    /// </summary>
    /// <remarks>
    /// <see cref="AccountPassword"/>, <see cref="ScramStoredKey"/>, <see cref="ScramServerKey"/>,
    /// and <see cref="ScramSalt"/> are sensitive. They must not be logged or copied onto the
    /// session identity snapshot. <c>account_type</c> is not selected for NNTP policy.
    /// Every account has both remaining-byte and rate fields.
    /// </remarks>
    public sealed class NntpUserRecord
    {
        /// <summary>Creates a validated user-record snapshot.</summary>
        public NntpUserRecord(
            string accountName,
            string accountPassword,
            bool allowAuthPlain,
            bool allowAuthScram256,
            ReadOnlyMemory<byte> scramSalt,
            int scramIterations,
            ReadOnlyMemory<byte> scramStoredKey,
            ReadOnlyMemory<byte> scramServerKey,
            int rateLimitBps,
            long byteLimit,
            int sessionLimit,
            int srcIpLimit,
            bool isEnabled,
            string customerId,
            ArticleType allowedArtTypes = ArticleTypeCapabilities.All)
        {
            ArgumentException.ThrowIfNullOrEmpty(accountName);
            ArgumentOutOfRangeException.ThrowIfNegative(scramIterations);
            AccountName = accountName;
            AccountPassword = accountPassword ?? string.Empty;
            AllowAuthPlain = allowAuthPlain;
            AllowAuthScram256 = allowAuthScram256;
            ScramSalt = scramSalt;
            ScramIterations = scramIterations;
            ScramStoredKey = scramStoredKey;
            ScramServerKey = scramServerKey;
            RateLimitBps = rateLimitBps;
            ByteLimit = byteLimit;
            SessionLimit = sessionLimit;
            SrcIpLimit = srcIpLimit;
            IsEnabled = isEnabled;
            CustomerId = customerId ?? string.Empty;
            AllowedArtTypes = allowedArtTypes;
        }

        /// <summary>Gets the plaintext wire username (not the MD5 column value).</summary>
        public string AccountName { get; }

        /// <summary>Gets the decrypted password. Sensitive. Empty when SQL decrypt is NULL.</summary>
        public string AccountPassword { get; }

        /// <summary>Gets whether AUTHINFO PASS, PLAIN, LOGIN, and CRAM-MD5 are permitted.</summary>
        public bool AllowAuthPlain { get; }

        /// <summary>Gets whether SCRAM-SHA-256 is permitted.</summary>
        public bool AllowAuthScram256 { get; }

        /// <summary>Gets SCRAM salt bytes, or empty when not provisioned.</summary>
        public ReadOnlyMemory<byte> ScramSalt { get; }

        /// <summary>Gets SCRAM iteration count. <c>0</c> means SCRAM is not provisioned.</summary>
        public int ScramIterations { get; }

        /// <summary>Gets SCRAM StoredKey. Sensitive.</summary>
        public ReadOnlyMemory<byte> ScramStoredKey { get; }

        /// <summary>Gets SCRAM ServerKey. Sensitive.</summary>
        public ReadOnlyMemory<byte> ScramServerKey { get; }

        /// <summary>
        /// Gets <c>account_rate_limit</c> in bits per second.
        /// Examples: <c>240</c> = 240 bps, <c>1_000_000</c> = 1 Mbps, <c>10_000_000</c> = 10 Mbps.
        /// <c>0</c> is unlimited. NULL at map time becomes <c>0</c>. This is not megabits.
        /// </summary>
        public int RateLimitBps { get; }

        /// <summary>
        /// Gets <c>account_byte_limit</c> remaining bytes.
        /// <c>0</c> is exhausted. NULL at map time becomes <c>0</c>. This snapshot is not
        /// live cluster remaining.
        /// </summary>
        public long ByteLimit { get; }

        /// <summary>Gets <c>account_session_limit</c>. <c>0</c> is unlimited.</summary>
        public int SessionLimit { get; }

        /// <summary>Gets <c>account_srcip_limit</c>. <c>0</c> is unlimited.</summary>
        public int SrcIpLimit { get; }

        /// <summary>Gets whether <c>is_enabled</c> is <c>Y</c>.</summary>
        public bool IsEnabled { get; }

        /// <summary>Gets <c>customer_id</c>.</summary>
        public string CustomerId { get; }

        /// <summary>
        /// Gets <c>account_art_type</c> flags loaded at AUTHINFO.
        /// <see cref="ArticleTypeCapabilities.All"/> is unrestricted.
        /// </summary>
        public ArticleType AllowedArtTypes { get; }

        /// <summary>Gets whether stored SCRAM material is complete enough to begin an exchange.</summary>
        public bool HasScramMaterial =>
            AllowAuthScram256
            && !ScramSalt.IsEmpty
            && ScramIterations > 0
            && !ScramStoredKey.IsEmpty
            && !ScramServerKey.IsEmpty;
    }
}
