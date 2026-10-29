using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VectorNNTPD.ReaderService.PostFilter.Quota
{
    /// <summary>
    /// Redis key and field material for PostFilter accept-quota state.
    /// </summary>
    /// <remarks>
    /// Keys: <c>nntpd:pf:q:{sha256hex(account)}</c> and <c>nntpd:pf:m:{sha256hex(account)}</c>.
    /// Account hashing matches <c>nntpd:sess:</c> / <c>nntpd:account:</c>
    /// (UTF-8 SHA-256, lowercase hex). Reservation value:
    /// <c>expiryMs|generation|messages|bytes|mpUnits|bodyHex</c>.
    /// </remarks>
    internal static class PostFilterQuotaKeys
    {
        /// <summary>ASCII namespace for committed + reservation hashes.</summary>
        public static ReadOnlySpan<byte> QuotaNamespacePrefix => "nntpd:pf:q:"u8;

        /// <summary>ASCII namespace for committed identical-body hashes.</summary>
        public static ReadOnlySpan<byte> MultipostNamespacePrefix => "nntpd:pf:m:"u8;

        /// <summary>Prefix of live reservation fields.</summary>
        public const string ReservationFieldPrefix = "r:";

        /// <summary>Separates packed reservation fields.</summary>
        public const char ValueSeparator = '|';

        /// <summary>Long / sustained window identifier.</summary>
        public const char LongWindow = 'L';

        /// <summary>Short / burst window identifier.</summary>
        public const char ShortWindow = 'S';

        /// <summary>Builds the quota HASH key for <paramref name="accountName"/>.</summary>
        public static byte[] CreateQuota(string accountName) => Create(QuotaNamespacePrefix, accountName);

        /// <summary>Builds the multipost HASH key for <paramref name="accountName"/>.</summary>
        public static byte[] CreateMultipost(string accountName) => Create(MultipostNamespacePrefix, accountName);

        /// <summary>Builds <c>c:m:{W}:{bucketId}</c>.</summary>
        public static string CommittedMessagesField(char window, long bucketId) =>
            string.Concat("c:m:", window, ":", bucketId.ToString(CultureInfo.InvariantCulture));

        /// <summary>Builds <c>c:b:{W}:{bucketId}</c>.</summary>
        public static string CommittedBytesField(char window, long bucketId) =>
            string.Concat("c:b:", window, ":", bucketId.ToString(CultureInfo.InvariantCulture));

        /// <summary>Builds <c>{bodyHex}:{W}:{bucketId}</c>.</summary>
        public static string MultipostField(string bodyHex, char window, long bucketId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(bodyHex);
            return string.Concat(bodyHex, ":", window, ":", bucketId.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Packs a reservation value.</summary>
        public static string PackReservation(
            long expiryMs,
            long generation,
            long messages,
            long bytes,
            int mpUnits,
            string bodyHex) =>
            string.Concat(
                expiryMs.ToString(CultureInfo.InvariantCulture),
                ValueSeparator,
                generation.ToString(CultureInfo.InvariantCulture),
                ValueSeparator,
                messages.ToString(CultureInfo.InvariantCulture),
                ValueSeparator,
                bytes.ToString(CultureInfo.InvariantCulture),
                ValueSeparator,
                mpUnits.ToString(CultureInfo.InvariantCulture),
                ValueSeparator,
                bodyHex);

        /// <summary>Parses a reservation value. Returns <see langword="false"/> when malformed.</summary>
        public static bool TryParseReservation(
            string value,
            out long expiryMs,
            out long generation,
            out long messages,
            out long bytes,
            out int mpUnits,
            out string bodyHex)
        {
            expiryMs = 0;
            generation = 0;
            messages = 0;
            bytes = 0;
            mpUnits = 0;
            bodyHex = string.Empty;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            // Lua split_res takes the first five '|' fields; the remainder is bodyHex
            // and may itself contain '|'. Require exactly those five separators.
            var fields = new string[5];
            var start = 0;
            for (var i = 0; i < 5; i++)
            {
                var sep = value.IndexOf(ValueSeparator, start);
                if (sep < 0)
                {
                    return false;
                }

                fields[i] = value[start..sep];
                start = sep + 1;
            }

            if (!long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out expiryMs)
                || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out generation)
                || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out messages)
                || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out bytes)
                || !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out mpUnits))
            {
                return false;
            }

            bodyHex = value[start..];
            return true;
        }

        /// <summary>Parses <c>c:m|b:{W}:{bucket}</c>. Returns <see langword="false"/> when not a committed field.</summary>
        public static bool TryParseCommittedField(string field, out char kind, out char window, out long bucketId)
        {
            kind = default;
            window = default;
            bucketId = 0;
            if (field.Length < 7 || field[0] != 'c' || field[1] != ':')
            {
                return false;
            }

            if (field[2] is not ('m' or 'b') || field[3] != ':')
            {
                return false;
            }

            kind = field[2];
            if (field[4] is not (LongWindow or ShortWindow) || field[5] != ':')
            {
                return false;
            }

            window = field[4];
            return long.TryParse(field.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out bucketId);
        }

        /// <summary>Parses <c>{bodyHex}:{W}:{bucket}</c> from the right.</summary>
        public static bool TryParseMultipostField(string field, out string bodyHex, out char window, out long bucketId)
        {
            bodyHex = string.Empty;
            window = default;
            bucketId = 0;
            var last = field.LastIndexOf(':');
            if (last <= 2 || last >= field.Length - 1)
            {
                return false;
            }

            var mid = field.LastIndexOf(':', last - 1);
            if (mid <= 0 || last != mid + 2)
            {
                return false;
            }

            var w = field[mid + 1];
            if (w is not (LongWindow or ShortWindow))
            {
                return false;
            }

            if (!long.TryParse(field.AsSpan(last + 1), NumberStyles.None, CultureInfo.InvariantCulture, out bucketId))
            {
                return false;
            }

            window = w;
            bodyHex = field[..mid];
            return bodyHex.Length > 0;
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
