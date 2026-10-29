using System.Security.Cryptography;
using System.Text;

namespace VectorNNTPD.ReaderService.PostFilter
{
    /// <summary>
    /// PostFilter account-list identity: lowercase hexadecimal MD5 of the AUTH
    /// username using the same trim-on-policy / raw-on-request contract as the
    /// evaluator. Does not replace <c>ArticleId</c> or <c>ArtHash</c>.
    /// </summary>
    public static class PostFilterAccountIdentity
    {
        /// <summary>Stored identifier length (<c>CHAR(32)</c> lowercase hex).</summary>
        public const int HexLength = 32;

        /// <summary>
        /// MD5 of <paramref name="username"/> as UTF-8 bytes, lowercase hex.
        /// Does not trim; request matching uses the session username as stored.
        /// </summary>
        public static string FromUsername(string username)
        {
            ArgumentNullException.ThrowIfNull(username);
            var digest = MD5.HashData(Encoding.UTF8.GetBytes(username));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }

        /// <summary>
        /// Policy-list normalization: trim then hash, unless the trimmed value is
        /// already a stored 32-character lowercase hex identifier.
        /// </summary>
        public static string FromPolicyEntry(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            var trimmed = value.Trim();
            return IsStoredIdentifier(trimmed) ? trimmed : FromUsername(trimmed);
        }

        /// <summary>Returns whether <paramref name="value"/> is 32 lowercase hex characters.</summary>
        public static bool IsStoredIdentifier(ReadOnlySpan<char> value)
        {
            if (value.Length != HexLength)
            {
                return false;
            }

            foreach (var ch in value)
            {
                if (ch is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Hashes a request username when it is non-empty; otherwise returns
        /// <see langword="null"/>. Does not trim.
        /// </summary>
        public static string? TryFromRequest(string? accountName) =>
            accountName is { Length: > 0 } ? FromUsername(accountName) : null;
    }
}
