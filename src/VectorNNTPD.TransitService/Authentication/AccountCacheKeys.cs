using System.Security.Cryptography;
using System.Text;

namespace VectorNNTPD.TransitService.Authentication
{
    /// <summary>
    /// Redis key material for the short-lived AUTHINFO account-record cache.
    /// </summary>
    /// <remarks>
    /// Key: <c>nntpd:account:{sha256hex(accountName)}</c>. The plaintext AUTHINFO
    /// username is hashed with the same UTF-8 SHA-256 convention as other NNTPD
    /// Redis account keys so listings do not expose account names. The value is a
    /// complete MySQL <c>nntpusers</c> snapshot with a Redis-native TTL. This is
    /// not a session, lease, or byte-ledger key.
    /// </remarks>
    internal static class AccountCacheKeys
    {
        /// <summary>ASCII namespace for account-record cache keys.</summary>
        public static ReadOnlySpan<byte> NamespacePrefix => "nntpd:account:"u8;

        /// <summary>Builds the account-record cache key for <paramref name="accountName"/>.</summary>
        public static byte[] Create(string accountName)
        {
            var hex = HashHex(accountName);
            var prefix = NamespacePrefix;
            var key = new byte[prefix.Length + hex.Length];
            prefix.CopyTo(key);
            Encoding.ASCII.GetBytes(hex, key.AsSpan(prefix.Length));
            return key;
        }

        /// <summary>
        /// Returns lowercase hex SHA-256 of the UTF-8 wire account name.
        /// The caller supplies the same string used for the MySQL lookup.
        /// </summary>
        public static string HashHex(string accountName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            Span<byte> hash = stackalloc byte[32];
            var byteCount = Encoding.UTF8.GetByteCount(accountName);
            if (byteCount <= 256)
            {
                Span<byte> utf8 = stackalloc byte[byteCount];
                Encoding.UTF8.GetBytes(accountName, utf8);
                SHA256.HashData(utf8, hash);
            }
            else
            {
                SHA256.HashData(Encoding.UTF8.GetBytes(accountName), hash);
            }

            return Convert.ToHexStringLower(hash);
        }
    }
}
