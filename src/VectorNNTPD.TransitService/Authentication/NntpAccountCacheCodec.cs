using System.Buffers.Binary;
using System.Text;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.TransitService.Authentication
{
    /// <summary>
    /// Explicit binary snapshot of one MySQL <c>nntpusers</c> row for Redis.
    /// </summary>
    /// <remarks>
    /// Versioned little-endian layout. Preserves nullability as empty strings or
    /// empty byte spans, booleans as 0/1, and integer widths without truncation.
    /// Does not include <c>account_type</c>. The payload contains credential
    /// material and must not be logged.
    /// </remarks>
    internal static class NntpAccountCacheCodec
    {
        internal const byte Version = 2;

        internal const byte Version1 = 1;

        private const int MaxAccountNameBytes = 512;
        private const int MaxPasswordBytes = 4096;
        private const int MaxScramBytes = 1024;
        private const int MaxCustomerIdBytes = 128;

        /// <summary>Serializes <paramref name="record"/> to the version-1 cache payload.</summary>
        public static byte[] Encode(NntpUserRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            var accountName = Encoding.UTF8.GetBytes(record.AccountName);
            var password = Encoding.UTF8.GetBytes(record.AccountPassword);
            var customerId = Encoding.UTF8.GetBytes(record.CustomerId);
            var salt = record.ScramSalt;
            var storedKey = record.ScramStoredKey;
            var serverKey = record.ScramServerKey;
            if (accountName.Length > MaxAccountNameBytes
                || password.Length > MaxPasswordBytes
                || salt.Length > MaxScramBytes
                || storedKey.Length > MaxScramBytes
                || serverKey.Length > MaxScramBytes
                || customerId.Length > MaxCustomerIdBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(record), "Account cache field exceeds encoding limits.");
            }

            var length = 1
                + 4 + accountName.Length
                + 4 + password.Length
                + 1
                + 1
                + 4 + salt.Length
                + 4
                + 4 + storedKey.Length
                + 4 + serverKey.Length
                + 4
                + 8
                + 4
                + 4
                + 1
                + 4 + customerId.Length
                + 4;
            var buffer = new byte[length];
            var span = buffer.AsSpan();
            span[0] = Version;
            var offset = 1;
            WriteBytes(span, ref offset, accountName);
            WriteBytes(span, ref offset, password);
            span[offset++] = record.AllowAuthPlain ? (byte)1 : (byte)0;
            span[offset++] = record.AllowAuthScram256 ? (byte)1 : (byte)0;
            WriteBytes(span, ref offset, salt.Span);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset), record.ScramIterations);
            offset += 4;
            WriteBytes(span, ref offset, storedKey.Span);
            WriteBytes(span, ref offset, serverKey.Span);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset), record.RateLimitBps);
            offset += 4;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset), record.ByteLimit);
            offset += 8;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset), record.SessionLimit);
            offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset), record.SrcIpLimit);
            offset += 4;
            span[offset++] = record.IsEnabled ? (byte)1 : (byte)0;
            WriteBytes(span, ref offset, customerId);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset), (int)record.AllowedArtTypes);
            return buffer;
        }

        /// <summary>
        /// Decodes <paramref name="payload"/> when it is a well-formed snapshot for
        /// <paramref name="accountName"/>. Returns <see langword="false"/> for any
        /// corrupt, truncated, or account-mismatched payload.
        /// </summary>
        public static bool TryDecode(ReadOnlySpan<byte> payload, string accountName, out NntpUserRecord? record)
        {
            record = null;
            if (payload.Length < 2 || (payload[0] != Version && payload[0] != Version1))
            {
                return false;
            }

            var version = payload[0];
            var offset = 1;
            if (!TryReadBytes(payload, ref offset, MaxAccountNameBytes, out var cachedName)
                || !TryReadBytes(payload, ref offset, MaxPasswordBytes, out var password)
                || !TryReadByte(payload, ref offset, out var allowPlain)
                || allowPlain > 1
                || !TryReadByte(payload, ref offset, out var allowScram)
                || allowScram > 1
                || !TryReadBytes(payload, ref offset, MaxScramBytes, out var salt)
                || !TryReadInt32(payload, ref offset, out var iterations)
                || iterations < 0
                || !TryReadBytes(payload, ref offset, MaxScramBytes, out var storedKey)
                || !TryReadBytes(payload, ref offset, MaxScramBytes, out var serverKey)
                || !TryReadInt32(payload, ref offset, out var rateLimitBps)
                || !TryReadInt64(payload, ref offset, out var byteLimit)
                || !TryReadInt32(payload, ref offset, out var sessionLimit)
                || !TryReadInt32(payload, ref offset, out var srcIpLimit)
                || !TryReadByte(payload, ref offset, out var enabled)
                || enabled > 1
                || !TryReadBytes(payload, ref offset, MaxCustomerIdBytes, out var customerId))
            {
                return false;
            }

            var allowedArtTypes = ArticleTypeCapabilities.All;
            if (version == Version)
            {
                if (!TryReadInt32(payload, ref offset, out var artTypeBits) || offset != payload.Length)
                {
                    return false;
                }

                allowedArtTypes = (ArticleType)artTypeBits;
            }
            else if (offset != payload.Length)
            {
                return false;
            }

            var decodedName = Encoding.UTF8.GetString(cachedName);
            if (!string.Equals(decodedName, accountName, StringComparison.Ordinal))
            {
                return false;
            }

            record = new NntpUserRecord(
                decodedName,
                Encoding.UTF8.GetString(password),
                allowPlain == 1,
                allowScram == 1,
                salt.ToArray(),
                iterations,
                storedKey.ToArray(),
                serverKey.ToArray(),
                rateLimitBps,
                byteLimit,
                sessionLimit,
                srcIpLimit,
                enabled == 1,
                Encoding.UTF8.GetString(customerId),
                allowedArtTypes);
            return true;
        }

        private static void WriteBytes(Span<byte> destination, ref int offset, ReadOnlySpan<byte> value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset), value.Length);
            offset += 4;
            value.CopyTo(destination.Slice(offset));
            offset += value.Length;
        }

        private static bool TryReadBytes(
            ReadOnlySpan<byte> source,
            ref int offset,
            int maxLength,
            out ReadOnlySpan<byte> value)
        {
            value = default;
            if (offset > source.Length - 4)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset));
            offset += 4;
            if (length < 0 || length > maxLength || offset > source.Length - length)
            {
                return false;
            }

            value = source.Slice(offset, length);
            offset += length;
            return true;
        }

        private static bool TryReadByte(ReadOnlySpan<byte> source, ref int offset, out byte value)
        {
            if (offset >= source.Length)
            {
                value = 0;
                return false;
            }

            value = source[offset++];
            return true;
        }

        private static bool TryReadInt32(ReadOnlySpan<byte> source, ref int offset, out int value)
        {
            if (offset > source.Length - 4)
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset));
            offset += 4;
            return true;
        }

        private static bool TryReadInt64(ReadOnlySpan<byte> source, ref int offset, out long value)
        {
            if (offset > source.Length - 8)
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(offset));
            offset += 8;
            return true;
        }
    }
}
