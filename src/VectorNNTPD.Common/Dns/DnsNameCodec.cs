using System.Text;

namespace VectorNNTPD.Common.Dns
{
    /// <summary>
    /// Reads and skips DNS domain names in wire format, including compression pointers (RFC 1035 §4.1.4).
    /// </summary>
    /// <remarks>
    /// Compression traversal is bounded by <see cref="MaxPointerHops"/>. Reserved label types (bits <c>10</c>/<c>01</c>)
    /// are rejected. Skip advances past a pointer without following it (offset arithmetic only).
    /// </remarks>
    internal static class DnsNameCodec
    {
        /// <summary>Maximum compression-pointer hops before treating a name as malformed.</summary>
        public const int MaxPointerHops = 128;

        /// <summary>Maximum expanded wire name length in bytes per RFC 1035.</summary>
        private const int MaxExpandedNameLengthBytes = 255;

        /// <summary>
        /// Reads a domain name starting at <paramref name="offset"/>; advances <paramref name="offset"/> past the
        /// on-wire encoding (after the root label, or past a compression pointer that ends the name).
        /// </summary>
        /// <returns><see langword="true"/> on success; <see langword="false"/> on malformed encoding.</returns>
        internal static bool TryReadDomainName(ReadOnlySpan<byte> packet, ref int offset, out string name)
        {
            name = string.Empty;

            List<string>? labels = null;
            bool jumped = false;
            int jumpBack = 0;
            int pos = offset;
            int expandedBytes = 0;

            for (int hop = 0; hop < MaxPointerHops; hop++)
            {
                if (pos >= packet.Length)
                {
                    return false;
                }

                byte len = packet[pos];
                if ((len & 0xC0) == 0xC0)
                {
                    if (pos + 1 >= packet.Length)
                    {
                        return false;
                    }

                    if (!jumped)
                    {
                        jumped = true;
                        jumpBack = pos + 2;
                    }

                    pos = ((len & 0x3F) << 8) | packet[pos + 1];
                    if (pos >= packet.Length)
                    {
                        return false;
                    }

                    continue;
                }

                if (len == 0)
                {
                    pos++;
                    offset = jumped ? jumpBack : pos;
                    name = labels is null || labels.Count == 0 ? string.Empty : string.Join(".", labels);
                    return true;
                }

                if (len > 63)
                {
                    return false;
                }

                if (pos + 1 + len > packet.Length)
                {
                    return false;
                }

                pos++;
                labels ??= [];

                expandedBytes += (labels.Count == 0 ? 0 : 1) + len;
                if (expandedBytes > MaxExpandedNameLengthBytes)
                {
                    return false;
                }

                labels.Add(Encoding.ASCII.GetString(packet.Slice(pos, len)));
                pos += len;
            }

            return false;
        }

        /// <summary>
        /// Returns whether two presentation-form DNS names are equal under DNS case-insensitive comparison.
        /// </summary>
        /// <remarks>
        /// Trailing root dots are ignored. Comparison is label-boundary preserving (ordinal ignore-case on
        /// the full dotted string); suffix or substring matches are not treated as equal.
        /// </remarks>
        /// <param name="left">First name.</param>
        /// <param name="right">Second name.</param>
        /// <returns><see langword="true"/> when the names denote the same DNS owner.</returns>
        internal static bool NamesEqual(string left, string right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);

            ReadOnlySpan<char> a = left.AsSpan().TrimEnd('.');
            ReadOnlySpan<char> b = right.AsSpan().TrimEnd('.');
            return a.Equals(b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Advances <paramref name="offset"/> past a DNS name encoding (labels or a compression pointer).
        /// </summary>
        /// <remarks>
        /// Compression pointers advance by two bytes without following the target (sufficient when skipping
        /// owner names and questions before parsing typed RDATA).
        /// </remarks>
        internal static bool TrySkipName(ReadOnlySpan<byte> packet, ref int offset)
        {
            int hops = 0;
            while (offset < packet.Length)
            {
                if (++hops > MaxPointerHops)
                {
                    return false;
                }

                byte labelLength = packet[offset];
                if (labelLength == 0)
                {
                    offset++;
                    return true;
                }

                if ((labelLength & 0xC0) == 0xC0)
                {
                    if (offset + 2 > packet.Length)
                    {
                        return false;
                    }

                    offset += 2;
                    return true;
                }

                if ((labelLength & 0xC0) != 0)
                {
                    return false;
                }

                int advance = 1 + labelLength;
                if (offset + advance > packet.Length)
                {
                    return false;
                }

                offset += advance;
            }

            return false;
        }
    }
}
