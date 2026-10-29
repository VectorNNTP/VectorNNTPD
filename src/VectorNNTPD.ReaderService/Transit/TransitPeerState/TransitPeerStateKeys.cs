using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// Redis key material for cluster-wide Transit inbound connection ownership.
    /// </summary>
    /// <remarks>
    /// Key: <c>nntpd:tconn:{sha256hex(identifier)}</c>.
    /// Field: <c>{ownerId}</c> where <c>ownerId</c> is <c>{nodeId}:{incarnation}</c>.
    /// Value: <c>{expiryUnixMs}|{generation}|{count}</c>.
    /// The Transit dictionary key (<see cref="TransitPeerPolicy.Identifier"/>) is hashed
    /// so Redis key listings do not expose peer names. Source IP is never part of this key.
    /// Ownership uses value expiry, not a Redis key TTL.
    /// </remarks>
    internal static class TransitPeerStateKeys
    {
        /// <summary>ASCII namespace for Transit inbound-connection ownership hashes.</summary>
        public static ReadOnlySpan<byte> NamespacePrefix => "nntpd:tconn:"u8;

        /// <summary>Separates expiry, generation, and count in a HASH value.</summary>
        public const char ValueSeparator = '|';

        /// <summary>Builds the Transit inbound-connection Redis HASH key for <paramref name="identifier"/>.</summary>
        public static byte[] Create(string identifier)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
            Span<byte> hash = stackalloc byte[32];
            var byteCount = Encoding.UTF8.GetByteCount(identifier);
            if (byteCount <= 256)
            {
                Span<byte> utf8 = stackalloc byte[byteCount];
                Encoding.UTF8.GetBytes(identifier, utf8);
                SHA256.HashData(utf8, hash);
            }
            else
            {
                SHA256.HashData(Encoding.UTF8.GetBytes(identifier), hash);
            }

            var hex = Convert.ToHexStringLower(hash);
            var prefix = NamespacePrefix;
            var key = new byte[prefix.Length + hex.Length];
            prefix.CopyTo(key);
            Encoding.ASCII.GetBytes(hex, key.AsSpan(prefix.Length));
            return key;
        }

        /// <summary>Formats a lease value as <c>expiryMs|generation|count</c>.</summary>
        public static string Value(long expiryUnixMs, long generation, int count) =>
            string.Concat(
                expiryUnixMs.ToString(CultureInfo.InvariantCulture),
                ValueSeparator,
                generation.ToString(CultureInfo.InvariantCulture),
                ValueSeparator,
                count.ToString(CultureInfo.InvariantCulture));

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
    }
}
