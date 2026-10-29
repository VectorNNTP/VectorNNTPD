using System.Security.Cryptography;
using System.Text;

namespace VectorNNTPD.TransitService.SessionState.BytesAccounting
{
    /// <summary>
    /// Redis key material for cluster-wide remaining byte quota.
    /// </summary>
    /// <remarks>
    /// Key: <c>nntpd:bytes:{sha256hex(accountName)}</c> (HASH). The plaintext AUTHINFO
    /// username is hashed so Redis listings do not expose account names. Field
    /// <c>remaining</c> is the remaining byte count. Field <c>b:{batchId}</c> marks an
    /// applied consume batch for idempotent APPLY. This is not a lease and has no key TTL.
    /// </remarks>
    internal static class AccountByteKeys
    {
        /// <summary>ASCII namespace for remaining-quota keys.</summary>
        public static ReadOnlySpan<byte> NamespacePrefix => "nntpd:bytes:"u8;

        /// <summary>Sentinel returned by OBSERVE when the key is missing.</summary>
        public const long Missing = -1;

        /// <summary>Builds the remaining-quota Redis key for <paramref name="accountName"/>.</summary>
        public static byte[] Create(string accountName)
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

            var hex = Convert.ToHexStringLower(hash);
            var prefix = NamespacePrefix;
            var key = new byte[prefix.Length + hex.Length];
            prefix.CopyTo(key);
            Encoding.ASCII.GetBytes(hex, key.AsSpan(prefix.Length));
            return key;
        }
    }
}
