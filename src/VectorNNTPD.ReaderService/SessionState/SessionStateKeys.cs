using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VectorNNTPD.ReaderService.SessionState
{
    /// <summary>
    /// Redis key material for cluster-wide account session and source-IP membership.
    /// </summary>
    /// <remarks>
    /// Keys: <c>nntpd:srcip:{sha256hex(account)}</c> and <c>nntpd:sess:{sha256hex(account)}</c>.
    /// Account names are hashed so Redis key listings do not expose usernames.
    /// Source field: <c>{normalizedIp}\x1f{ownerId}</c>.
    /// Session field: <c>{ownerId}</c>.
    /// Value: <c>{expiryUnixMs}|{generation}|{count}</c>.
    /// <c>ownerId</c> is <c>{nodeId}:{incarnation}</c> so a process restart cannot
    /// overwrite a previous incarnation's unexpired ownership.
    /// </remarks>
    internal static class SessionStateKeys
    {
        /// <summary>ASCII namespace for source-IP ownership hashes.</summary>
        public static ReadOnlySpan<byte> SourceNamespacePrefix => "nntpd:srcip:"u8;

        /// <summary>ASCII namespace for session ownership hashes.</summary>
        public static ReadOnlySpan<byte> SessionNamespacePrefix => "nntpd:sess:"u8;

        /// <summary>Separates the normalized IP from the owner id in a source HASH field.</summary>
        public const char FieldSeparator = '\u001f';

        /// <summary>Separates expiry, generation, and count in a HASH value.</summary>
        public const char ValueSeparator = '|';

        /// <summary>Builds the source-IP Redis HASH key for <paramref name="accountName"/>.</summary>
        public static byte[] CreateSource(string accountName) => Create(SourceNamespacePrefix, accountName);

        /// <summary>Builds the session Redis HASH key for <paramref name="accountName"/>.</summary>
        public static byte[] CreateSession(string accountName) => Create(SessionNamespacePrefix, accountName);

        /// <summary>Builds the HASH field for a node-owned source IP.</summary>
        public static string SourceField(string normalizedSourceIp, string ownerId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(normalizedSourceIp);
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
            return string.Concat(normalizedSourceIp, FieldSeparator, ownerId);
        }

        /// <summary>Formats a lease value as <c>expiryMs|generation|count</c>.</summary>
        public static string Value(long expiryUnixMs, long generation, int count) =>
            string.Concat(
                expiryUnixMs.ToString(CultureInfo.InvariantCulture),
                ValueSeparator,
                generation.ToString(CultureInfo.InvariantCulture),
                ValueSeparator,
                count.ToString(CultureInfo.InvariantCulture));

        /// <summary>Parses a source HASH field into source IP and owner id.</summary>
        public static bool TrySplitField(string field, out string ip, out string ownerId)
        {
            ArgumentNullException.ThrowIfNull(field);
            var separator = field.IndexOf(FieldSeparator, StringComparison.Ordinal);
            if (separator <= 0 || separator >= field.Length - 1)
            {
                ip = string.Empty;
                ownerId = string.Empty;
                return false;
            }

            ip = field[..separator];
            ownerId = field[(separator + 1)..];
            return true;
        }

        /// <summary>Parses a HASH value into expiry, generation, and count.</summary>
        public static bool TrySplitValue(string value, out long expiryUnixMs, out long generation, out int count)
        {
            ArgumentNullException.ThrowIfNull(value);
            expiryUnixMs = 0;
            generation = 0;
            count = 0;
            var first = value.IndexOf(ValueSeparator, StringComparison.Ordinal);
            if (first <= 0 || first >= value.Length - 1)
            {
                return false;
            }

            var second = value.IndexOf(ValueSeparator, first + 1);
            if (second <= first + 1 || second >= value.Length - 1)
            {
                return false;
            }

            return long.TryParse(value.AsSpan(0, first), out expiryUnixMs)
                && long.TryParse(value.AsSpan(first + 1, second - first - 1), out generation)
                && int.TryParse(value.AsSpan(second + 1), out count);
        }

        private static byte[] Create(ReadOnlySpan<byte> prefix, string accountName)
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
            var key = new byte[prefix.Length + hex.Length];
            prefix.CopyTo(key);
            Encoding.ASCII.GetBytes(hex, key.AsSpan(prefix.Length));
            return key;
        }
    }
}
